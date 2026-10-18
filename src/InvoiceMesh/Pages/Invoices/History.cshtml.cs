using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Pages.Invoices;

/// <summary>
/// Everything that happened to a document, in order: the events, the corrections (which can be taken back), the revisions of the normalized data with the
/// versions of schema and mapping they used, the extractions and the validations. Nothing here is ever overwritten.
/// </summary>
public sealed class HistoryModel(InvoiceService invoices, ImDbContext db) : InvoicePage(invoices)
{
    public sealed record RevisionRow(InvoiceRevision Revision, string Schema, string Mapping);

    protected override string TabKey => "history";

    public List<InvoiceEvent> Events { get; private set; } = [];

    public List<RevisionRow> Revisions { get; private set; } = [];

    public List<InvoiceExtraction> Extractions { get; private set; } = [];

    public List<InvoiceValidation> Validations { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        Events = await db.InvoiceEvent.AsNoTracking().Where(e => e.InvoiceId == Id).OrderByDescending(e => e.OccurredDate).ThenByDescending(e => e.Id).ToListAsync();
        Revisions = await db.InvoiceRevision.AsNoTracking().Where(r => r.InvoiceId == Id).OrderByDescending(r => r.Number)
            .Select(r => new RevisionRow(r, r.SchemaVersion!.TargetSchema!.Name + " v" + r.SchemaVersion.Number, r.MappingVersion!.MappingSet!.Name + " v" + r.MappingVersion.Number))
            .ToListAsync();
        Extractions = await db.InvoiceExtraction.AsNoTracking().Where(e => e.InvoiceId == Id).OrderByDescending(e => e.Number)
            .Select(e => new InvoiceExtraction { Id = e.Id, Number = e.Number, Method = e.Method, CreateDate = e.CreateDate, DurationMs = e.DurationMs, OcrConfidence = e.OcrConfidence, OcrEngine = e.OcrEngine })
            .ToListAsync();
        Validations = await db.InvoiceValidation.AsNoTracking().Where(v => v.InvoiceId == Id).OrderByDescending(v => v.Id)
            .Select(v => new InvoiceValidation { Id = v.Id, Status = v.Status, Errors = v.Errors, Warnings = v.Warnings, XRechnungReady = v.XRechnungReady, CreateDate = v.CreateDate })
            .ToListAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostRevertAsync(long correctionId)
    {
        var doc = await TrackedAsync();
        if (doc is null)
        {
            return NotFound();
        }

        try
        {
            await Invoices.RevertCorrectionAsync(doc, correctionId);
            Flash("Die Korrektur ist zurückgenommen; die Daten wurden neu erzeugt.");
        }
        catch (Exception ex) when (ex is InvoiceException or DefinitionException)
        {
            FlashError(ex.Message);
        }

        return Back("/Invoices/History");
    }
}
