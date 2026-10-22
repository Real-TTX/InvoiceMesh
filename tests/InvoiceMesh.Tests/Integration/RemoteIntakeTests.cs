using System.Text;
using InvoiceMesh.Configuration;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services.Automation;
using InvoiceMesh.Storage;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace InvoiceMesh.Tests.Integration;

/// <summary>
/// A test that needs a mail server with IMAP and SMTP that takes any sign-in (GreenMail, the CI starts one): <c>INVOICEMESH_TEST_IMAP</c> is
/// <c>host;imap=3143;smtp=3025</c>.
/// </summary>
public sealed class ImapFactAttribute : FactAttribute
{
    public const string Variable = "INVOICEMESH_TEST_IMAP";

    public ImapFactAttribute()
    {
        if (Settings is null || !AppFixture.Available)
        {
            Skip = $"{Variable} or {AppFixture.Variable} is not set: no mail server or no database.";
        }
    }

    public static (string Host, int Imap, int Smtp)? Settings
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable(Variable);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var parts = raw.Split(';');
            int Part(string key) => int.Parse(parts.Skip(1).Select(p => p.Split('=', 2)).First(p => p[0] == key)[1], System.Globalization.CultureInfo.InvariantCulture);
            return (parts[0], Part("imap"), Part("smtp"));
        }
    }
}

/// <summary>Intake from a mailbox and from a share of a NAS, against real servers.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class RemoteIntakeTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public RemoteIntakeTests(AppFixture app) => _app = app;

    private static byte[] Sample(string number)
        => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Core.Samples.Bytes("xrechnung-ubl.xml")).Replace("RE-2026-0815", number, StringComparison.Ordinal));

    private Task<IntakeOutcome> RunAsync(long id) => _app.WithScopeAsync(async sp =>
        await sp.GetRequiredService<IntakeService>().RunAsync(await sp.GetRequiredService<ImDbContext>().IntakeSource.FirstAsync(s => s.Id == id), manual: true));

    private Task<long> AddSourceAsync(IntakeSource source, string password) => _app.WithScopeAsync(async sp =>
    {
        source.PasswordProtected = sp.GetRequiredService<SecretProtector>().Protect(password);
        var db = sp.GetRequiredService<ImDbContext>();
        db.IntakeSource.Add(source);
        await db.SaveChangesAsync();
        return source.Id;
    });

    [ImapFact]
    public async Task Attachments_of_unread_mails_are_taken_and_the_mails_moved_away()
    {
        await _app.EnsureAdminAsync();
        var (host, imapPort, smtpPort) = ImapFactAttribute.Settings!.Value;
        var address = $"rechnungen-{Guid.NewGuid():N}"[..20] + "@example.test";

        // a mail with an XRechnung, a PDF that is no invoice, and a logo inside the text; and a mail without an attachment
        var body = new BodyBuilder { HtmlBody = "<p>Anbei die Rechnung.</p><img src=\"cid:logo\">" };
        body.Attachments.Add("RE-MAIL-1.xml", Sample("RE-MAIL-1"), new ContentType("application", "xml"));
        var logo = body.LinkedResources.Add("logo.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 }, new ContentType("image", "png"));
        logo.ContentId = "logo";
        await SendAsync(host, smtpPort, address, "Rechnung RE-MAIL-1", body.ToMessageBody());
        await SendAsync(host, smtpPort, address, "Nur eine Frage", new TextPart("plain") { Text = "Wann zahlt ihr?" });

        var id = await AddSourceAsync(new IntakeSource
        {
            Name = "Postfach", Kind = IntakeKind.Mailbox, Host = host, Port = imapPort, Security = MailSecurity.None, UserName = address, MailFolder = "INBOX", DoneFolder = "Verarbeitet", IsEnabled = false,
        }, "egal");

        var check = await _app.WithScopeAsync(async sp => await sp.GetRequiredService<IntakeService>().CheckAsync(await sp.GetRequiredService<ImDbContext>().IntakeSource.FirstAsync(s => s.Id == id)));
        Assert.True(check.Ok, check.Message);
        Assert.Contains("2 davon ungelesen", check.Message, StringComparison.Ordinal);

        var outcome = await RunAsync(id);
        var added = Assert.Single(outcome.Added);
        Assert.Equal("RE-MAIL-1.xml", added.Document.FileName);
        Assert.Equal(InvoiceSource.Mail, added.Document.Source);
        Assert.Contains("„Rechnung RE-MAIL-1“", added.Document.SourceDetail, StringComparison.Ordinal);
        Assert.Contains(outcome.Problems, p => p.Contains("Nur eine Frage", StringComparison.Ordinal));

        // both mails were moved; a second run finds nothing
        using (var client = new ImapClient())
        {
            await client.ConnectAsync(host, imapPort, SecureSocketOptions.None);
            await client.AuthenticateAsync(address, "egal");
            await client.Inbox.OpenAsync(MailKit.FolderAccess.ReadOnly);
            Assert.Equal(0, client.Inbox.Count);
            var done = await client.GetFolderAsync("Verarbeitet");
            await done.OpenAsync(MailKit.FolderAccess.ReadOnly);
            Assert.Equal(2, done.Count);
        }

        Assert.Empty((await RunAsync(id)).Added);
    }

    private static async Task SendAsync(string host, int port, string to, string subject, MimeEntity body)
    {
        var message = new MimeMessage { Subject = subject, Body = body };
        message.From.Add(MailboxAddress.Parse("lieferant@example.test"));
        message.To.Add(MailboxAddress.Parse(to));
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(host, port, SecureSocketOptions.None);
        await smtp.SendAsync(message);
        await smtp.DisconnectAsync(true);
    }

    [SmbFact]
    public async Task A_folder_on_a_share_of_a_nas_is_emptied_into_documents()
    {
        if (!AppFixture.Available)
        {
            return;
        }

        await _app.EnsureAdminAsync();
        var s = SmbFactAttribute.Settings!.Value;
        var folder = "im-eingang-" + Guid.NewGuid().ToString("N")[..8];
        var share = new SmbShare(new SmbOptions(s.Host, s.Share, folder, null, s.User, s.Password));
        await share.WriteAsync("scan.xml", new MemoryStream(Sample("RE-NAS-1")));
        await share.WriteAsync("paket.zip", new MemoryStream(Core.ArchiveTests.Zip(("a.xml", Sample("RE-NAS-2")))));

        // the files must have settled before they are taken
        await Task.Delay(IntakeService.Settle + TimeSpan.FromSeconds(1));
        var id = await AddSourceAsync(new IntakeSource { Name = "NAS", Kind = IntakeKind.Smb, Host = s.Host, Share = s.Share, Path = folder, UserName = s.User, DoneFolder = "verarbeitet", IsEnabled = false }, s.Password);

        var outcome = await RunAsync(id);

        Assert.Equal(2, outcome.Added.Count);
        Assert.Empty(outcome.Problems);
        Assert.Empty(await share.ListAsync(string.Empty));
        Assert.Equal(2, (await share.ListAsync("verarbeitet")).Count);
        Assert.All(outcome.Added, a => Assert.Equal(InvoiceSource.Folder, a.Document.Source));
    }
}
