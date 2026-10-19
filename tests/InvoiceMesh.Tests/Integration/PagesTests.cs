using System.Net;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceMesh.Tests.Integration;

/// <summary>Every page of the application opens, with the sample data and as the people who may see it, and the roles keep to their side.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class PagesTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public PagesTests(AppFixture app) => _app = app;

    /// <summary>The sample documents, all of them processed.</summary>
    private async Task DemoAsync()
    {
        await _app.EnsureAdminAsync();
        await _app.WithScopeAsync(sp => sp.GetRequiredService<DemoData>().CreateAsync());
        var ids = await _app.WithScopeAsync(sp => sp.GetRequiredService<ImDbContext>().Invoice.Select(i => i.Id).ToListAsync());
        foreach (var id in ids)
        {
            await _app.ProcessedAsync(id);
        }
    }

    [DatabaseFact]
    public async Task The_sample_data_is_written_once_and_processed_like_uploads()
    {
        await DemoAsync();
        Assert.False(await _app.WithScopeAsync(sp => sp.GetRequiredService<DemoData>().CreateAsync()));
        var docs = await _app.WithScopeAsync(sp => sp.GetRequiredService<ImDbContext>().Invoice.AsNoTracking().ToListAsync());
        Assert.True(docs.Count >= 7);

        // structured invoices are read and valid; the hybrid PDF is recognised by its attachment; the wrong sum is found; the zip is no invoice
        Assert.Contains(docs, d => d.FileName == "RE-2026-0815.xml" && d.FormatCode == "UBL" && d.Standard == "XRechnung" && d.ValidationStatus != InvoiceMesh.Core.Validation.ValidationStatus.Invalid && d.XRechnungReady);
        Assert.Contains(docs, d => d.FileName == "facture_FA-2026-117.pdf" && d.FormatCode == "PDF+CII" && d.EmbeddedXmlFileId != null && d.InvoiceNumber == "FA-2026-117");
        Assert.Contains(docs, d => d.FileName == "Rechnung_2026-10477.pdf" && d.Method == "PdfText" && d.InvoiceNumber == "2026-10477" && d.NeedsReview);
        Assert.Contains(docs, d => d.FileName == "RE-2026-0816.xml" && d.ValidationStatus == InvoiceMesh.Core.Validation.ValidationStatus.Invalid);
        Assert.Contains(docs, d => d.FileName == "Notizen.docx" && d.ProcessingStatus == ProcessingStatus.Failed && d.ProcessingError != null);
    }

    [DatabaseFact]
    public async Task Every_page_opens_for_an_administrator()
    {
        await DemoAsync();
        using var client = _app.NewClient();
        await AppFixture.LoginAsync(client, "admin@example.com", "Start12345");

        var (doc, failed, schema, mapping, export, share) = await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ImDbContext>();
            var docId = await db.Invoice.Where(i => i.FileName == "RE-2026-0815.xml").Select(i => i.Id).FirstAsync();
            var tracked = await db.Invoice.FirstAsync(i => i.Id == docId);
            var link = await sp.GetRequiredService<ShareService>().CreateAsync(tracked, true, true, "Probe", null, 7, null, null);
            return (docId, await db.Invoice.Where(i => i.ProcessingStatus == ProcessingStatus.Failed).Select(i => i.Id).FirstAsync(),
                await db.TargetSchema.Select(s => s.Id).FirstAsync(), await db.MappingSet.Select(m => m.Id).FirstAsync(), await db.ExportProfile.Select(e => e.Id).FirstAsync(), link.Id);
        });

        string[] urls =
        [
            "/", "/Invoices", "/Invoices?status=pruefen&sort=betrag&dir=desc", "/Invoices?status=fehler", "/Invoices?format=UBL", "/Invoices?xrechnung=ja", "/Invoices?schema=veraltet", "/Invoices/Upload",
            $"/Invoices/Details?id={doc}", $"/Invoices/Original?id={doc}", $"/Invoices/Data?id={doc}", $"/Invoices/Data?id={doc}&q=Item", $"/Invoices/Validation?id={doc}", $"/Invoices/Validation?id={doc}&stufe=warnungen",
            $"/Invoices/Files?id={doc}", $"/Invoices/History?id={doc}", $"/Invoices/Correct?id={doc}", $"/Invoices/Share?id={doc}",
            $"/Invoices/Details?id={failed}", $"/Invoices/Original?id={failed}", $"/Invoices/Validation?id={failed}", $"/Invoices/Files?id={failed}",
            "/Schemas", "/Schemas/Edit", $"/Schemas/Edit?id={schema}", $"/Schemas/Field?id={schema}&group=invoice&field=number", $"/Schemas/Field?id={schema}&group=invoice",
            $"/Schemas/Group?id={schema}&group=lines", $"/Schemas/Group?id={schema}", $"/Schemas/Versions?id={schema}",
            "/Mappings", "/Mappings/Edit", $"/Mappings/Edit?id={mapping}", $"/Mappings/Rule?id={mapping}&target=invoice.number", $"/Mappings/Rule?id={mapping}&target=lines.name",
            $"/Mappings/Test?id={mapping}", $"/Mappings/Versions?id={mapping}",
            "/Exports", "/Exports/Edit", $"/Exports/Edit?id={export}", $"/Exports/Run?id={export}", "/Formats",
            "/Admin/Users", "/Admin/Users/Edit", "/Admin/Shares", $"/Admin/Shares/Edit?id={share}", "/Admin/Processing", "/Admin/Formats", "/Admin/Formats/Edit",
            "/Admin/Storage", "/Admin/Backup", "/Admin/Backup/Plans", "/Admin/Backup/Plans/Edit", "/Admin/Backup/Targets", "/Admin/Backup/Targets/Edit", "/Admin/Backup/Restore", "/Admin/Security", "/Admin/Activity",
            "/Admin/System", "/Account/Profile", "/Account/Security",
        ];

        var failures = new List<string>();
        foreach (var url in urls)
        {
            var response = await client.GetAsync(url);
            var text = response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadAsStringAsync() : "";
            if (response.StatusCode != HttpStatusCode.OK || text.Contains("Exception", StringComparison.Ordinal) || !text.Contains("<h1", StringComparison.Ordinal))
            {
                failures.Add($"{(int)response.StatusCode} {url}");
            }
        }

        Assert.True(failures.Count == 0, "Pages that failed: " + string.Join(", ", failures));

        // the files of a document
        foreach (var (url, type) in new[]
                 {
                     ($"/files/{doc}/original", "application/xml"), ($"/files/{doc}/normalized.json", "application/json"), ($"/files/{doc}/extracted.json", "application/json"),
                     ($"/files/{doc}/report.json", "application/json"), ($"/files/{doc}/report.pdf", "application/pdf"),
                 })
        {
            var file = await client.GetAsync(url);
            Assert.True(file.StatusCode == HttpStatusCode.OK, $"{url} answered {(int)file.StatusCode}");
            Assert.Equal(type, file.Content.Headers.ContentType?.MediaType);
        }

        // the report as a PDF is shown in the page, and is a file with ?download
        Assert.Null((await client.GetAsync($"/files/{doc}/report.pdf")).Content.Headers.ContentDisposition);
        Assert.Equal("attachment", (await client.GetAsync($"/files/{doc}/report.pdf?download=1")).Content.Headers.ContentDisposition?.DispositionType);

        // the preview of the files: the XRechnung without a choice, the JSON of the normalized data, the report in a frame; an unknown choice shows nothing
        Assert.Contains("CustomizationID", await AppFixture.TextAsync(client, $"/Invoices/Files?id={doc}"));
        var normalized = await AppFixture.TextAsync(client, $"/Invoices/Files?id={doc}&show=normalized");
        Assert.Contains("Vorschau: Normalisierte Daten", normalized);
        Assert.Contains("RE-2026-0815", normalized);
        Assert.Contains($"src=\"/files/{doc}/report.pdf\"", await AppFixture.TextAsync(client, $"/Invoices/Files?id={doc}&show=report"));
        Assert.DoesNotContain("Vorschau:", await AppFixture.TextAsync(client, $"/Invoices/Files?id={doc}&show=g999999"));

        // the structure was checked against the XML schema, and the report says so
        var validation = await AppFixture.TextAsync(client, $"/Invoices/Validation?id={doc}");
        Assert.Contains("Struktur (XSD)", validation);
        Assert.Contains("UBL 2.1", validation);
    }

    [DatabaseFact]
    public async Task A_user_stays_out_of_the_settings_and_the_anonymous_visitor_is_sent_to_the_sign_in()
    {
        await _app.EnsureAdminAsync();
        await _app.CreateUserAsync("normal@example.test", "Normal-Passwort-2026", UserRole.User);

        using var anonymous = _app.NewClient();
        foreach (var url in new[] { "/", "/Invoices", "/Schemas", "/Mappings", "/Exports", "/Admin/Users", "/files/1/original", "/files/1/report.pdf", "/api/invoices/status?ids=1" })
        {
            var status = (await anonymous.GetAsync(url)).StatusCode;
            Assert.True(status is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized, $"{url} answered {(int)status} to somebody who is not signed in");
        }

        using var user = _app.NewClient();
        await AppFixture.LoginAsync(user, "normal@example.test", "Normal-Passwort-2026");
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/Invoices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/Mappings")).StatusCode);
        foreach (var url in new[] { "/Admin/Users", "/Admin/Backup", "/Admin/Shares", "/Admin/System", "/Admin/Processing", "/Admin/Formats", "/Admin/Security" })
        {
            var status = (await user.GetAsync(url)).StatusCode;
            Assert.NotEqual(HttpStatusCode.OK, status);
        }

        // the menu of a user has no settings
        Assert.DoesNotContain("/Admin/Users", await AppFixture.TextAsync(user, "/Invoices"));
    }
}
