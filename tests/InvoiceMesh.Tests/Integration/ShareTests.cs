using System.Net;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceMesh.Tests.Integration;

/// <summary>The role "Anonym": a link shows one document read only, with the files it allows, for a limited time, behind a password when it has one.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class ShareTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public ShareTests(AppFixture app) => _app = app;

    private async Task<(InvoiceDocument Doc, ShareLink Link)> DocumentWithLinkAsync(string name, string? password = null, bool allowOriginal = true)
    {
        await _app.EnsureAdminAsync();
        var (doc, _) = await _app.CreateDocumentAsync(name);
        var link = await _app.WithScopeAsync(async sp =>
        {
            var tracked = await sp.GetRequiredService<ImDbContext>().Invoice.FirstAsync(i => i.Id == doc.Id);
            return await sp.GetRequiredService<ShareService>().CreateAsync(tracked, allowOriginal, true, "Rechnung " + doc.InvoiceNumber, "Hallo " + name, 7, password, null);
        });
        return (doc, link);
    }

    [DatabaseFact]
    public async Task Anybody_with_the_link_sees_the_document_and_gets_its_files()
    {
        var (doc, link) = await DocumentWithLinkAsync("Offen");
        using var visitor = _app.NewClient();

        var response = await visitor.GetAsync($"/s/{link.Token}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await AppFixture.TextAsync(response);
        Assert.Contains(doc.InvoiceNumber!, text);
        Assert.Contains("Hallo Offen", text);
        Assert.Contains("Beratung", text);
        Assert.DoesNotContain("/Invoices/Details", text);

        var original = await visitor.GetAsync($"/s/{link.Token}/file/original");
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        Assert.Contains("RE-Offen", await original.Content.ReadAsStringAsync());

        var report = await visitor.GetAsync($"/s/{link.Token}/file/report");
        Assert.Equal("application/pdf", report.Content.Headers.ContentType?.MediaType);

        // the files of the document are not open to the visitor outside the link
        Assert.Equal(HttpStatusCode.Redirect, (await visitor.GetAsync($"/files/{doc.Id}/original")).StatusCode);

        var counted = await _app.WithScopeAsync(sp => sp.GetRequiredService<ImDbContext>().ShareLink.AsNoTracking().FirstAsync(l => l.Id == link.Id));
        Assert.Equal(1, counted.ViewCount);
    }

    [DatabaseFact]
    public async Task A_link_without_the_original_does_not_give_it()
    {
        var (_, link) = await DocumentWithLinkAsync("OhneOriginal", allowOriginal: false);
        using var visitor = _app.NewClient();
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/s/{link.Token}/file/original")).StatusCode);
        Assert.DoesNotContain("/file/original", await AppFixture.TextAsync(visitor, $"/s/{link.Token}"));
    }

    [DatabaseFact]
    public async Task A_password_is_asked_first_and_wrong_ones_are_counted()
    {
        var (_, link) = await DocumentWithLinkAsync("Geheim", "Sesam-oeffne-dich");
        using var visitor = _app.NewClient();

        var gate = await AppFixture.TextAsync(visitor, $"/s/{link.Token}");
        Assert.Contains("Passwort", gate);
        Assert.DoesNotContain("Hallo Geheim", gate);
        Assert.Equal(HttpStatusCode.Redirect, (await visitor.GetAsync($"/s/{link.Token}/file/original")).StatusCode);

        var wrong = await AppFixture.PostFormAsync(visitor, $"/s/{link.Token}", new Dictionary<string, string> { ["Password"] = "falsch" }, $"/s/{link.Token}?handler=Password");
        Assert.Contains("stimmt nicht", await AppFixture.TextAsync(wrong));

        var right = await AppFixture.PostFormAsync(visitor, $"/s/{link.Token}", new Dictionary<string, string> { ["Password"] = "Sesam-oeffne-dich" }, $"/s/{link.Token}?handler=Password");
        Assert.Equal(HttpStatusCode.Redirect, right.StatusCode);
        Assert.Contains("Hallo Geheim", await AppFixture.TextAsync(visitor, $"/s/{link.Token}"));
    }

    [DatabaseFact]
    public async Task Revoked_and_unknown_links_say_so()
    {
        var (_, link) = await DocumentWithLinkAsync("Widerrufen");
        await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ImDbContext>();
            var l = await db.ShareLink.FirstAsync(x => x.Id == link.Id);
            l.RevokedDate = DateTime.UtcNow;
            await db.SaveChangesAsync();
        });

        using var visitor = _app.NewClient();
        var revoked = await visitor.GetAsync($"/s/{link.Token}");
        Assert.Equal(HttpStatusCode.Gone, revoked.StatusCode);
        Assert.Contains("widerrufen", await AppFixture.TextAsync(revoked));
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/s/{Guid.NewGuid()}")).StatusCode);
    }
}
