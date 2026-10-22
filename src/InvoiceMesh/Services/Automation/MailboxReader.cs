using InvoiceMesh.Configuration;
using InvoiceMesh.Data.Entities;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace InvoiceMesh.Services.Automation;

/// <summary>
/// Reads a mailbox over IMAP: the unread mails of a folder, their attachments taken in as documents. A mail is marked as read afterwards and, when a
/// done folder is set, moved there (made when missing). Pictures inside the text of a mail (a logo in the signature) are not attachments and are left out.
/// </summary>
public sealed class MailboxReader(SecretProtector secrets)
{
    private const int MaxMailsPerRun = 100;

    private static readonly string[] Documents = [".pdf", ".xml", ".zip"];

    private static readonly string[] Pictures = [".png", ".jpg", ".jpeg", ".tif", ".tiff"];

    public async Task<IntakeOutcome> FetchAsync(IntakeSource source, IntakeService intake, CancellationToken ct)
    {
        var outcome = new IntakeOutcome();
        using var client = await ConnectAsync(source, ct);
        var folder = await client.GetFolderAsync(source.MailFolder, ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        var done = string.IsNullOrWhiteSpace(source.DoneFolder) ? null : await GetOrCreateAsync(client, source.DoneFolder.Trim(), ct);

        var unread = await folder.SearchAsync(SearchQuery.NotSeen, ct);
        foreach (var uid in unread.Take(MaxMailsPerRun))
        {
            var message = await folder.GetMessageAsync(uid, ct);
            var from = message.From.Mailboxes.FirstOrDefault()?.Address ?? "unbekannt";
            var sent = message.Date == DateTimeOffset.MinValue ? "" : $", gesendet {Fmt.Moment(message.Date.UtcDateTime)}";
            var detail = $"{source.Name}: von {from}, „{message.Subject}“{sent}";
            var attachments = Attachments(message).ToList();
            if (attachments.Count == 0)
            {
                outcome.Problems.Add($"Die E-Mail „{message.Subject}“ von {from} hat keinen Anhang, der eine Rechnung sein kann.");
            }

            foreach (var part in attachments.Where(a => a.Content is not null))
            {
                using var buffer = new MemoryStream();
                await part.Content!.DecodeToAsync(buffer, ct);
                outcome.Add(await intake.AddAsync(part.FileName ?? "anhang", buffer.ToArray(), InvoiceSource.Mail, detail, ct));
            }

            await folder.AddFlagsAsync(uid, MessageFlags.Seen, true, ct);
            if (done is not null)
            {
                await folder.MoveToAsync(uid, done, ct);
            }
        }

        await client.DisconnectAsync(true, ct);
        return outcome;
    }

    public async Task<(bool Ok, string Message)> CheckAsync(IntakeSource source, CancellationToken ct)
    {
        using var client = await ConnectAsync(source, ct);
        var folder = await client.GetFolderAsync(source.MailFolder, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        var unread = (await folder.SearchAsync(SearchQuery.NotSeen, ct)).Count;
        await client.DisconnectAsync(true, ct);
        return (true, $"Angemeldet bei {source.Host} als {source.UserName}. Im Ordner „{source.MailFolder}“ liegen {Fmt.Integer(folder.Count)} E-Mail(s), {Fmt.Integer(unread)} davon ungelesen.");
    }

    /// <summary>
    /// The parts of a mail that may be invoices: PDF, XML and ZIP files wherever they are (some programs send them "inline"), pictures only when they are
    /// real attachments. A mail attached to the mail is looked into as well.
    /// </summary>
    public static IEnumerable<MimePart> Attachments(MimeMessage message)
    {
        foreach (var entity in message.BodyParts)
        {
            if (entity is MessagePart { Message: { } inner })
            {
                foreach (var nested in Attachments(inner))
                {
                    yield return nested;
                }

                continue;
            }

            if (entity is not MimePart { FileName: { Length: > 0 } name } part)
            {
                continue;
            }

            var extension = Path.GetExtension(name).ToLowerInvariant();
            if (Documents.Contains(extension) || Pictures.Contains(extension) && part.IsAttachment)
            {
                yield return part;
            }
        }
    }

    private async Task<ImapClient> ConnectAsync(IntakeSource source, CancellationToken ct)
    {
        var client = new ImapClient { Timeout = 60_000 };
        try
        {
            var security = source.Security switch
            {
                MailSecurity.Tls => SecureSocketOptions.SslOnConnect,
                MailSecurity.StartTls => SecureSocketOptions.StartTls,
                _ => SecureSocketOptions.None,
            };
            await client.ConnectAsync(source.Host ?? "", source.Port, security, ct);
            await client.AuthenticateAsync(source.UserName ?? "", secrets.Unprotect(source.PasswordProtected), ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<IMailFolder> GetOrCreateAsync(ImapClient client, string name, CancellationToken ct)
    {
        try
        {
            return await client.GetFolderAsync(name, ct);
        }
        catch (FolderNotFoundException)
        {
            var root = client.GetFolder(client.PersonalNamespaces[0]);
            return await root.CreateAsync(name, true, ct) ?? throw new FolderNotFoundException(name);
        }
    }
}
