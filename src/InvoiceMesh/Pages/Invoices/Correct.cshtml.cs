using System.Text.Json.Nodes;
using InvoiceMesh.Core.Schemas;
using InvoiceMesh.Core.Semantics;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using InvoiceMesh.UiKit;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceMesh.Pages.Invoices;

/// <summary>
/// Manual correction of a document, in two places: the business terms of the source (they feed the normalized data, the validation and the XRechnung) and
/// the fields of the target schema (for what the source does not have, a cost centre for instance). Only what was changed becomes a correction; the extraction
/// itself never changes, and every correction can be taken back in the history.
/// </summary>
public sealed class CorrectModel(InvoiceService invoices, NavCounts counts) : InvoicePage(invoices)
{
    public sealed class Entry
    {
        public string Key { get; set; } = "";

        public string? Value { get; set; }
    }

    public sealed record Input(int Index, string Key, string Label, string? Value, SemanticValue? Current, string? Hint);

    protected override string TabKey => "data";

    [BindProperty]
    public List<Entry> Fields { get; set; } = [];

    [BindProperty]
    public string? Reason { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Tab { get; set; }

    public List<(string Section, List<Input> Inputs)> Sections { get; private set; } = [];

    public List<(SemanticGroup Group, List<List<Input>> Items)> Groups { get; private set; } = [];

    public List<(SchemaGroup Group, List<List<Input>> Items)> Target { get; private set; } = [];

    /// <summary>The business terms of a line, a VAT row ... that the form offers (the others stay as they are).</summary>
    private static readonly Dictionary<string, string[]> GroupFields = new()
    {
        [SemanticCatalog.Lines] = ["BT-126", "BT-153", "BT-154", "BT-129", "BT-130", "BT-146", "BT-131", "BT-151", "BT-152"],
        [SemanticCatalog.VatBreakdown] = ["BT-118", "BT-119", "BT-116", "BT-117", "BT-120"],
        [SemanticCatalog.Allowances] = ["BT-92", "BT-95", "BT-96", "BT-97"],
        [SemanticCatalog.Charges] = ["BT-99", "BT-102", "BT-103", "BT-104"],
    };

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync() || Ctx.Extraction is null)
        {
            return NotFound();
        }

        Head($"Korrigieren: {Doc.DisplayName}", "Nur was du änderst, wird als Korrektur gespeichert; die ausgelesenen Daten bleiben unverändert.", ("Belege", "/Invoices"), (Doc.DisplayName, $"/Invoices/Details?id={Id}"), ("Korrigieren", null));
        Build();
        return Page();
    }

    private void Build()
    {
        var index = 0;
        Input Make(string key, string label, string? value, SemanticValue? current)
        {
            var hint = current is { Confidence: < Confidence.High } ? $"{Labels.Of(current.Confidence)} · {current.Source}" : current?.Source?.StartsWith("Korrektur", StringComparison.Ordinal) == true ? current.Source : null;
            return new Input(index++, key, label, value, current, hint);
        }

        Sections = [.. SemanticCatalog.FieldsOf(null).GroupBy(f => f.Section)
            .Select(g => (g.Key, g.Select(f => Make(f.Id, $"{f.Label} ({f.Id})", Ctx.Corrected.Get(f.Id), Ctx.Corrected.Value(f.Id))).ToList()))];

        foreach (var group in SemanticCatalog.Groups)
        {
            var count = Ctx.Corrected.Items(group.Id).Count;
            var items = new List<List<Input>>();
            for (var i = 0; i <= count; i++)
            {
                // One empty item more than there are: a line the reader missed can be added.
                items.Add([.. GroupFields[group.Id].Select(id =>
                {
                    var key = SemanticDocument.ItemKey(group.Id, i, id);
                    var current = Ctx.Corrected.Lookup(key);
                    return Make(key, $"{SemanticCatalog.LabelOf(id)} ({id})", current?.Value, current);
                })]);
            }

            Groups.Add((group, items));
        }

        // The target fields are a form of their own: their list starts at 0 again.
        index = 0;
        if (Ctx.Schema is { } schema)
        {
            foreach (var group in schema.Groups)
            {
                var items = new List<List<Input>>();
                var count = group.Repeating ? (Ctx.Data?[group.Key] as JsonArray)?.Count ?? 0 : 1;
                for (var i = 0; i < count; i++)
                {
                    items.Add([.. group.Fields.Select(f =>
                    {
                        var key = group.Repeating ? $"{group.Key}[{i + 1}].{f.Key}" : $"{group.Key}.{f.Key}";
                        return Make(key, $"{f.Label}{(f.Required ? " *" : "")}", InvoiceService.FieldText(Ctx.Data, key), null);
                    })]);
                }

                Target.Add((group, items));
            }
        }
    }

    public Task<IActionResult> OnPostSourceAsync() => SaveAsync(CorrectionScope.Source);

    public Task<IActionResult> OnPostTargetAsync() => SaveAsync(CorrectionScope.Target);

    private async Task<IActionResult> SaveAsync(CorrectionScope scope)
    {
        var doc = await TrackedAsync();
        if (doc is null)
        {
            return NotFound();
        }

        var ctx = await Invoices.LoadAsync(Id);
        var changes = Fields.Where(f => !string.IsNullOrWhiteSpace(f.Key)).GroupBy(f => f.Key).ToDictionary(g => g.Key, g => g.Last().Value);
        try
        {
            await Invoices.CorrectAsync(doc, scope, changes, Reason, scope == CorrectionScope.Target ? ctx?.Report?.TargetSchemaId : null);
            counts.Invalidate();
            Flash("Gespeichert. Die Daten wurden neu normalisiert und geprüft.");
        }
        catch (Exception ex) when (ex is InvoiceException or DefinitionException)
        {
            FlashError(ex.Message);
            return RedirectToPage(new { id = Id, tab = scope == CorrectionScope.Target ? "target" : null });
        }

        return Back("/Invoices/Data");
    }
}
