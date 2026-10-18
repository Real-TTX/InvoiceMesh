using InvoiceMesh.Core.Formats;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using InvoiceMesh.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Pages.Invoices;

/// <summary>
/// The overview of a document: the main facts, the steps of its processing with their result, the files that belong to it, and what can be done with it.
/// The processing state and the validation state are shown apart.
/// </summary>
public sealed class DetailsModel(InvoiceService invoices, ImDbContext db, ActivityLogger log, NavCounts counts) : InvoicePage(invoices)
{
    public sealed record Step(string Title, string Tone, string Icon, string Result, string? Href = null);

    protected override string TabKey => "overview";

    public List<Step> Steps { get; private set; } = [];

    public bool SchemaOutdated { get; private set; }

    public string? SchemaName { get; private set; }

    public List<TargetSchema> Schemas { get; private set; } = [];

    public bool IsPending => Doc.ProcessingStatus is ProcessingStatus.Queued or ProcessingStatus.Processing;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        Schemas = await db.TargetSchema.AsNoTracking().OrderBy(s => s.Name).ToListAsync();
        if (Ctx.Revision is { } revision)
        {
            SchemaOutdated = !await db.TargetSchemaVersion.AnyAsync(v => v.Id == revision.SchemaVersionId && v.State == VersionState.Active);
            SchemaName = Ctx.Report is { } r ? $"{r.SchemaName} v{r.SchemaVersion}" : null;
        }

        BuildSteps();
        return Page();
    }

    private void BuildSteps()
    {
        var f = Ctx.Format;
        var failed = Doc.ProcessingStatus == ProcessingStatus.Failed;
        if (IsPending)
        {
            Steps.Add(new Step("Verarbeitung", "busy", "refresh", Doc.ProcessingStatus == ProcessingStatus.Queued ? "wartet" : "läuft"));
            return;
        }

        Steps.Add(f is null
            ? new Step("Format erkannt", "error", "x-circle", "nicht erkannt")
            : new Step("Format erkannt", f.Kind == FileKind.Unknown ? "error" : "ok", f.Kind == FileKind.Unknown ? "x-circle" : "check-circle", f.Summary, $"/Invoices/Original?id={Id}"));
        var unsure = Ctx.Corrected.Uncertain().Count();
        Steps.Add(Doc.Method is null or "None"
            ? new Step("Daten ausgelesen", "error", "x-circle", "nicht möglich")
            : new Step("Daten ausgelesen", unsure > 0 ? "warn" : "ok", unsure > 0 ? "alert" : "check-circle",
                $"{Labels.Method(Doc.Method)}{(Ctx.Extraction?.OcrConfidence is { } c ? $", Konfidenz {c:0} %" : "")}{(unsure > 0 ? $", {unsure} unsicher" : "")}", $"/Invoices/Data?id={Id}"));
        if (Ctx.Report is { } report)
        {
            var problems = report.Fields.Count(x => x.Status is Core.Mapping.FieldStatus.Invalid or Core.Mapping.FieldStatus.Conflict) + report.Fields.Count(x => x.Required && x.Value is null);
            Steps.Add(new Step("Normalisiert", SchemaOutdated ? "warn" : problems > 0 ? "warn" : "ok", problems > 0 || SchemaOutdated ? "alert" : "check-circle",
                $"{report.SchemaName} v{report.SchemaVersion} über „{report.MappingName}“ v{report.MappingVersion}{(SchemaOutdated ? " (ältere Schemaversion)" : "")}", $"/Invoices/Data?id={Id}#normalized"));
        }
        else if (!failed)
        {
            Steps.Add(new Step("Normalisiert", "plain", "minus", "noch nicht"));
        }

        if (Ctx.Validation is { } v)
        {
            Steps.Add(new Step("Validiert", v.Errors > 0 ? "error" : v.Warnings > 0 ? "warn" : "ok", v.Errors > 0 ? "x-circle" : v.Warnings > 0 ? "alert" : "check-circle",
                $"{InvoiceService.ValidationLabel(v.Status)}: {v.Errors} Fehler, {v.Warnings} Warnungen", $"/Invoices/Validation?id={Id}"));
        }

        var xr = Ctx.Files.FirstOrDefault(x => x.Kind is GeneratedKind.XRechnungUbl or GeneratedKind.XRechnungCii);
        Steps.Add(xr is not null
            ? new Step("XRechnung", "ok", "check-circle", $"erzeugt ({Labels.Of(xr.Kind)}), {Fmt.Moment(xr.CreateDate)}", $"/Invoices/Files?id={Id}")
            : Doc.XRechnungReady ? new Step("XRechnung", "info", "info", "möglich, noch nicht erzeugt")
            : new Step("XRechnung", "plain", "minus", Ctx.XRechnungMissing.Count > 0 ? $"nicht möglich: {Ui.CountNoun(Ctx.XRechnungMissing.Count, "Angaben")} fehlen oder widersprechen sich" : "nicht möglich", $"/Invoices/Validation?id={Id}#xrechnung"));
        var pdf = Ctx.Files.FirstOrDefault(x => x.Kind == GeneratedKind.Pdf);
        Steps.Add(Doc.FileKind == nameof(FileKind.Pdf)
            ? new Step("PDF-Ansicht", "ok", "check-circle", "Original-PDF vorhanden", $"/Invoices/Original?id={Id}")
            : pdf is not null ? new Step("PDF-Ansicht", "ok", "check-circle", "erzeugt", $"/files/generated/{pdf.Id}") : new Step("PDF-Ansicht", "plain", "minus", "noch nicht erzeugt"));
    }

    private async Task<IActionResult> RunAsync(Func<InvoiceDocument, Task> action, string done)
    {
        var doc = await TrackedAsync();
        if (doc is null)
        {
            return NotFound();
        }

        try
        {
            await action(doc);
            counts.Invalidate();
            Flash(done);
        }
        catch (Exception ex) when (ex is InvoiceException or DefinitionException)
        {
            FlashError(ex.Message);
        }

        return Back();
    }

    public Task<IActionResult> OnPostReprocessAsync() => RunAsync(d => Invoices.ReprocessAsync(d), "Der Beleg wird neu ausgelesen.");

    public Task<IActionResult> OnPostRenormalizeAsync(long? schemaId) => RunAsync(d => Invoices.RenormalizeAsync(d, schemaId), "Die Daten wurden neu normalisiert und geprüft.");

    public Task<IActionResult> OnPostXRechnungAsync(string? syntax)
        => RunAsync(d => Invoices.GenerateXRechnungAsync(d, syntax switch { "cii" => InvoiceSyntax.Cii, "ubl" => InvoiceSyntax.Ubl, _ => null }), "Die XRechnung wurde erzeugt und gegengeprüft.");

    public Task<IActionResult> OnPostPdfAsync() => RunAsync(d => Invoices.GeneratePdfAsync(d), "Die PDF-Ansicht wurde erzeugt.");

    public Task<IActionResult> OnPostReviewedAsync() => RunAsync(d => Invoices.MarkReviewedAsync(d), "Als geprüft markiert.");

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var doc = await TrackedAsync();
        if (doc is null)
        {
            return RedirectToPage("/Invoices/Index");
        }

        await Invoices.DeleteAsync(doc);
        await log.LogAsync("Beleg gelöscht", nameof(InvoiceDocument), doc.Id, doc.DisplayName);
        counts.Invalidate();
        Flash("Der Beleg ist aus den Listen entfernt. Original und Verlauf bleiben gespeichert.");
        return RedirectToPage("/Invoices/Index");
    }
}
