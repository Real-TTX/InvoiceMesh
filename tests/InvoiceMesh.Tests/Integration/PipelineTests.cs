using System.Net;
using System.Text;
using InvoiceMesh.Core.Mapping;
using InvoiceMesh.Core.Schemas;
using InvoiceMesh.Core.Validation;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceMesh.Tests.Integration;

/// <summary>The way of a document through the application: upload, processing, outputs, corrections, schema and mapping changes, exports.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class PipelineTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public PipelineTests(AppFixture app) => _app = app;

    [DatabaseFact]
    public async Task An_upload_through_the_page_is_stored_unchanged_processed_and_gets_its_outputs()
    {
        await _app.EnsureAdminAsync();
        using var client = _app.NewClient();
        await AppFixture.LoginAsync(client, "admin@example.com", "Start12345");

        var bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Core.Samples.Bytes("facturx-en16931.xml")).Replace("FA-2026-117", "FA-UPLOAD-1", StringComparison.Ordinal));
        var token = await AppFixture.TokenAsync(client, "/Invoices/Upload");
        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new ByteArrayContent(bytes), "Files", "rechnung.pdf" },
        };
        var response = await client.PostAsync("/Invoices/Upload", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var id = long.Parse(AppFixture.LocationQuery(response, "id"));

        var (doc, original) = await _app.ProcessedAsync(id);
        Assert.Equal(ProcessingStatus.Processed, doc.ProcessingStatus);
        Assert.Equal("FA-UPLOAD-1", doc.InvoiceNumber);
        Assert.Equal("CII", doc.FormatCode);
        Assert.Equal("rechnung.pdf", doc.FileName);

        // the original is byte for byte what came
        var stored = await _app.WithScopeAsync(sp => sp.GetRequiredService<Storage.FileStoreService>().ReadAllAsync(original));
        Assert.Equal(bytes, stored);

        // the readable PDF (the original is XML); no XRechnung: the Factur-X lacks what XRechnung asks for (the contact of the seller), and nothing is made up
        var files = await _app.WithScopeAsync(sp => sp.GetRequiredService<ImDbContext>().GeneratedFile.AsNoTracking().Where(f => f.InvoiceId == id).ToListAsync());
        Assert.Contains(files, f => f.Kind == GeneratedKind.Pdf);
        Assert.False(doc.XRechnungReady);
        Assert.DoesNotContain(files, f => f.Kind is GeneratedKind.XRechnungCii or GeneratedKind.XRechnungUbl);
    }

    [DatabaseFact]
    public async Task A_correction_is_laid_over_the_extraction_and_can_be_taken_back()
    {
        await _app.EnsureAdminAsync();
        var (doc, _) = await _app.CreateDocumentAsync("Korrektur");
        Assert.True(doc.XRechnungReady);
        var made = await _app.WithScopeAsync(sp => sp.GetRequiredService<ImDbContext>().GeneratedFile.AsNoTracking().Where(f => f.InvoiceId == doc.Id).ToListAsync());
        Assert.Contains(made, f => f.Kind == GeneratedKind.XRechnungUbl && f.Check!.Contains("0 Fehler", StringComparison.Ordinal));

        // the Leitweg-ID is cleared: no XRechnung any more, the extraction keeps its value
        await _app.WithScopeAsync(async sp =>
        {
            var invoices = sp.GetRequiredService<InvoiceService>();
            var ctx = await invoices.LoadAsync(doc.Id);
            await invoices.CorrectAsync(ctx!.Doc, CorrectionScope.Source, new Dictionary<string, string?> { ["BT-10"] = "", ["BT-1"] = "RE-KORRIGIERT" }, "Test", null);
        });

        var ctx = await _app.WithScopeAsync(sp => sp.GetRequiredService<InvoiceService>().LoadAsync(doc.Id));
        Assert.Equal("04011000-12345-67", ctx!.Extracted.Get("BT-10"));
        Assert.Null(ctx.Corrected.Get("BT-10"));
        Assert.Equal("RE-KORRIGIERT", ctx.Doc.InvoiceNumber);
        Assert.Equal("RE-KORRIGIERT", InvoiceService.FieldText(ctx.Data, "invoice.number"));
        Assert.False(ctx.Doc.XRechnungReady);
        Assert.Contains(ctx.XRechnungMissing, f => f.RuleId == "BR-DE-15");
        Assert.Equal(2, ctx.Revision!.Number);

        // taken back: the value of the extraction counts again
        var correction = ctx.Corrections.First(c => c.Key == "BT-10");
        await _app.WithScopeAsync(async sp =>
        {
            var invoices = sp.GetRequiredService<InvoiceService>();
            await invoices.RevertCorrectionAsync((await invoices.LoadAsync(doc.Id))!.Doc, correction.Id);
        });
        ctx = await _app.WithScopeAsync(sp => sp.GetRequiredService<InvoiceService>().LoadAsync(doc.Id));
        Assert.Equal("04011000-12345-67", ctx!.Corrected.Get("BT-10"));
        Assert.True(ctx.Doc.XRechnungReady);

        // a value of the target schema only
        await _app.WithScopeAsync(async sp =>
        {
            var invoices = sp.GetRequiredService<InvoiceService>();
            var c = await invoices.LoadAsync(doc.Id);
            await invoices.CorrectAsync(c!.Doc, CorrectionScope.Target, new Dictionary<string, string?> { ["invoice.note"] = "Kostenstelle 4711" }, null, c.Report!.TargetSchemaId);
        });
        ctx = await _app.WithScopeAsync(sp => sp.GetRequiredService<InvoiceService>().LoadAsync(doc.Id));
        Assert.Equal("Kostenstelle 4711", InvoiceService.FieldText(ctx!.Data, "invoice.note"));
        Assert.Contains(ctx.Report!.Fields, f => f.Target == "invoice.note" && f.Status == FieldStatus.Manual);
    }

    [DatabaseFact]
    public async Task A_schema_change_leaves_the_old_revision_and_a_renormalization_makes_a_new_one()
    {
        await _app.EnsureAdminAsync();
        var (doc, _) = await _app.CreateDocumentAsync("Schema");
        var schemaId = await _app.WithScopeAsync(async sp => (await sp.GetRequiredService<DefinitionService>().DefaultSchemaAsync()).Id);

        // a new field, in a draft first: the documents are not touched until it is active
        await _app.WithScopeAsync(async sp =>
        {
            var defs = sp.GetRequiredService<DefinitionService>();
            var draft = await defs.EnsureSchemaDraftAsync(schemaId);
            var def = DefinitionService.Definition(draft);
            def.Group("invoice")!.Fields.Add(new SchemaField { Key = "costCenter", Label = "Kostenstelle", DefaultValue = "K-100" });
            await defs.SaveSchemaDraftAsync(draft, def);
            await defs.ActivateSchemaAsync(draft.Id, "Kostenstelle");
        });

        var before = await _app.WithScopeAsync(sp => sp.GetRequiredService<InvoiceService>().LoadAsync(doc.Id));
        Assert.Null(InvoiceService.FieldText(before!.Data, "invoice.costCenter"));
        var oldRevision = before.Revision!.Id;

        await _app.WithScopeAsync(async sp =>
        {
            var invoices = sp.GetRequiredService<InvoiceService>();
            await invoices.RenormalizeAsync((await invoices.LoadAsync(doc.Id))!.Doc, null);
        });

        var after = await _app.WithScopeAsync(sp => sp.GetRequiredService<InvoiceService>().LoadAsync(doc.Id));
        Assert.Equal("K-100", InvoiceService.FieldText(after!.Data, "invoice.costCenter"));
        Assert.NotEqual(oldRevision, after.Revision!.Id);
        Assert.True(await _app.WithScopeAsync(sp => sp.GetRequiredService<ImDbContext>().InvoiceRevision.AnyAsync(r => r.Id == oldRevision)));
    }

    [DatabaseFact]
    public async Task A_mapping_draft_is_seen_in_the_preview_but_changes_nothing_until_it_is_active()
    {
        await _app.EnsureAdminAsync();
        var (doc, _) = await _app.CreateDocumentAsync("Mapping");
        var setId = await _app.WithScopeAsync(sp => sp.GetRequiredService<ImDbContext>().MappingSet.OrderBy(m => m.Id).Select(m => m.Id).FirstAsync());

        await _app.WithScopeAsync(async sp =>
        {
            var defs = sp.GetRequiredService<DefinitionService>();
            var draft = await defs.EnsureMappingDraftAsync(setId);
            var def = DefinitionService.Definition(draft);
            def.Rules.First(r => r.Target == "invoice.number").Transforms.Add(new TransformStep { Op = "prefix", Arg = "X-" });
            await defs.SaveMappingDraftAsync(draft, def);
        });

        using var client = _app.NewClient();
        await AppFixture.LoginAsync(client, "admin@example.com", "Start12345");
        var preview = await AppFixture.TextAsync(client, $"/Mappings/Test?id={setId}&sample={doc.Id}");
        Assert.Contains("X-RE-Mapping", preview);

        var stored = await _app.WithScopeAsync(sp => sp.GetRequiredService<InvoiceService>().LoadAsync(doc.Id));
        Assert.Equal("RE-Mapping", InvoiceService.FieldText(stored!.Data, "invoice.number"));

        // put back, so that the other tests see the standard mapping
        await _app.WithScopeAsync(sp => sp.GetRequiredService<DefinitionService>().DiscardMappingDraftAsync(setId));
    }

    [DatabaseFact]
    public async Task An_export_runs_over_the_documents_in_the_chosen_format()
    {
        await _app.EnsureAdminAsync();
        var (doc, _) = await _app.CreateDocumentAsync("Export");
        var result = await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ImDbContext>();
            var profile = await db.ExportProfile.FirstAsync(e => e.Name == "CSV je Position");
            return await sp.GetRequiredService<ExportService>().RunAsync(profile, [doc.Id]);
        });

        var csv = Encoding.UTF8.GetString(result.Bytes);
        Assert.Equal(1, result.Count);
        Assert.Contains("RE-Export", csv);
        Assert.Contains("Fachbuch", csv);
        Assert.EndsWith(".csv", result.FileName);
    }

    [DatabaseFact]
    public async Task A_broken_file_is_kept_and_reported_in_words()
    {
        await _app.EnsureAdminAsync();
        var id = await _app.WithScopeAsync(async sp => (await sp.GetRequiredService<InvoiceService>().AddAsync("<Invoice><cbc:ID>"u8.ToArray(), "kaputt.xml")).Document.Id);
        var (doc, _) = await _app.ProcessedAsync(id);
        Assert.Equal(ProcessingStatus.Failed, doc.ProcessingStatus);
        Assert.Contains("nicht wohlgeformt", doc.ProcessingError);
        Assert.Equal(ValidationStatus.Invalid, doc.ValidationStatus);
    }
}
