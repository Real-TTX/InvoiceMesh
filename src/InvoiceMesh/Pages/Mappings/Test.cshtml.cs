using InvoiceMesh.Core.Mapping;
using InvoiceMesh.Core.Schemas;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Pages.Mappings;

/// <summary>
/// The preview of a mapping before it is put in force: the draft (or the active version) on a sample document, field by field next to what the active version
/// makes of it; and a trial run over the latest documents the mapping fits. Nothing is stored; the documents are not touched.
/// </summary>
public sealed class TestModel(ImDbContext db, DefinitionService definitions, InvoiceService invoices) : PageModelBase
{
    public const int TrialSize = 20;

    public sealed record Line(FieldOutcome Outcome, string? ActiveValue, bool Changed);

    public sealed record Trial(InvoiceDocument Doc, int Missing, int Invalid, int Conflicts, int Changed);

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public long? Sample { get; set; }

    /// <summary>"draft" or "active".</summary>
    [BindProperty(SupportsGet = true)]
    public string? Version { get; set; }

    public MappingSet Set { get; private set; } = null!;

    public MappingSetVersion? Active { get; private set; }

    public MappingSetVersion? Draft { get; private set; }

    public MappingSetVersion? Tested { get; private set; }

    public InvoiceDocument? SampleDoc { get; private set; }

    public MappingReport? Report { get; private set; }

    public List<Line> Lines { get; private set; } = [];

    public List<Trial> Trials { get; private set; } = [];

    public List<SelectListItem> Samples { get; private set; } = [];

    public bool Compared { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var set = await db.MappingSet.AsNoTracking().Include(m => m.TargetSchema).FirstOrDefaultAsync(m => m.Id == Id);
        if (set is null)
        {
            return NotFound();
        }

        Set = set;
        Head($"Vorschau: {set.Name}", "Probiere das Mapping an Belegen aus, bevor du es aktivierst. Es wird nichts gespeichert.", ("Mappings", "/Mappings"), (set.Name, $"/Mappings/Edit?id={Id}"), ("Vorschau", null));
        ViewData["ActiveHref"] = "/Mappings";
        Active = await definitions.ActiveMappingVersionAsync(Id);
        Draft = await definitions.MappingDraftAsync(Id);
        Tested = Version == "active" ? Active ?? Draft : Draft ?? Active;
        if (Tested is null)
        {
            return Page();
        }

        var testDraft = Tested.State == VersionState.Draft;
        var schemaVersion = (testDraft ? await definitions.SchemaDraftAsync(set.TargetSchemaId) : null) ?? await definitions.ActiveSchemaVersionAsync(set.TargetSchemaId);
        if (schemaVersion is null)
        {
            return Page();
        }

        var schema = DefinitionService.Definition(schemaVersion);
        var mapping = DefinitionService.Definition(Tested);
        var activeSchemaVersion = await definitions.ActiveSchemaVersionAsync(set.TargetSchemaId);
        var compareWith = testDraft && Active is not null && activeSchemaVersion is not null ? (Schema: DefinitionService.Definition(activeSchemaVersion), Mapping: DefinitionService.Definition(Active)) : ((SchemaDefinition Schema, MappingDefinition Mapping)?)null;
        Compared = compareWith is not null;

        var docs = await db.Invoice.AsNoTracking().Where(i => i.ProcessingStatus == ProcessingStatus.Processed && i.CurrentExtractionId != null)
            .OrderByDescending(i => i.ReceivedDate).Take(300).ToListAsync();
        Samples = [.. docs.Select(d => new SelectListItem($"{d.DisplayName} · {d.FormatCode}", d.Id.ToString()))];
        var fitting = new List<InvoiceContext>();
        foreach (var d in docs)
        {
            if (fitting.Count >= TrialSize)
            {
                break;
            }

            var ctx = await invoices.LoadAsync(d.Id);
            if (ctx?.Format is not null && DefinitionService.Fits(set, ctx.Format))
            {
                fitting.Add(ctx);
            }
        }

        var sampleId = Sample ?? Tested.SampleInvoiceId ?? fitting.FirstOrDefault()?.Doc.Id ?? docs.FirstOrDefault()?.Id;
        var sample = sampleId is { } sid ? fitting.FirstOrDefault(c => c.Doc.Id == sid) ?? await invoices.LoadAsync(sid) : null;
        if (sample is not null)
        {
            SampleDoc = sample.Doc;
            Sample = sample.Doc.Id;
            Report = InvoiceService.NormalizeInMemory(sample, schema, mapping, set.TargetSchemaId).Report;
            var before = compareWith is { } cw ? InvoiceService.NormalizeInMemory(sample, cw.Schema, cw.Mapping, set.TargetSchemaId).Report.Fields.ToDictionary(f => f.Address, f => f.Value) : null;
            Lines = [.. Report.Fields.Select(f => new Line(f, before?.GetValueOrDefault(f.Address), before is not null && (!before.TryGetValue(f.Address, out var old) || old != f.Value)))];
        }

        foreach (var ctx in fitting)
        {
            var report = InvoiceService.NormalizeInMemory(ctx, schema, mapping, set.TargetSchemaId).Report;
            var changed = 0;
            if (compareWith is { } cw)
            {
                var before = InvoiceService.NormalizeInMemory(ctx, cw.Schema, cw.Mapping, set.TargetSchemaId).Report.Fields.ToDictionary(f => f.Address, f => f.Value);
                changed = report.Fields.Count(f => !before.TryGetValue(f.Address, out var old) || old != f.Value);
            }

            Trials.Add(new Trial(ctx.Doc, report.MissingRequired.Count(), report.Count(FieldStatus.Invalid), report.Count(FieldStatus.Conflict), changed));
        }

        return Page();
    }

    /// <summary>Remembers the sample document for this version (the rule editor and the preview open with it).</summary>
    public async Task<IActionResult> OnPostRememberAsync(long sample, string? version)
    {
        var target = version == "active" ? await definitions.ActiveMappingVersionAsync(Id) : await definitions.MappingDraftAsync(Id) ?? await definitions.ActiveMappingVersionAsync(Id);
        if (target is not null)
        {
            await db.MappingSetVersion.Where(v => v.Id == target.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.SampleInvoiceId, sample));
            Flash("Dieser Beleg ist jetzt das Beispiel des Mappings.");
        }

        return RedirectToPage(new { id = Id, sample, version });
    }
}
