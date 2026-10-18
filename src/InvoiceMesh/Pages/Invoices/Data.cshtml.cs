using InvoiceMesh.Core.Extraction;
using InvoiceMesh.Core.Semantics;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceMesh.Pages.Invoices;

/// <summary>
/// The data of a document side by side: each field of the target schema with the source values it was made from and what became of them; the business terms
/// as they were read (with their confidence and source) and as they are after the corrections; the raw fields; what no rule used.
/// </summary>
public sealed class DataModel(InvoiceService invoices) : InvoicePage(invoices)
{
    public const int MaxRaw = 400;

    public sealed record TermRow(string Key, string Label, SemanticValue? Extracted, SemanticValue? Current, InvoiceCorrection? Correction);

    protected override string TabKey => "data";

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? RawSearch { get; set; }

    public List<(string Section, List<TermRow> Rows)> Header { get; private set; } = [];

    public List<(string Group, string Label, List<List<TermRow>> Items)> Groups { get; private set; } = [];

    public List<SourceField> Raw { get; private set; } = [];

    public int RawTotal { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        var corrections = Ctx.ActiveCorrections(CorrectionScope.Source).ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        TermRow Row(string key, string label) => new(key, label, Ctx.Extracted.Lookup(key), Ctx.Corrected.Lookup(key), corrections.GetValueOrDefault(key));

        // Every term the catalog knows that has a value before or after the corrections, in the order of the catalog; then terms outside the catalog.
        var headerIds = SemanticCatalog.FieldsOf(null).Select(f => f.Id).Concat(Ctx.Extracted.Fields.Keys).Concat(Ctx.Corrected.Fields.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(id => Ctx.Extracted.Has(id) || Ctx.Corrected.Has(id) || corrections.ContainsKey(id)).ToList();
        Header = [.. headerIds.GroupBy(id => SemanticCatalog.Field(id)?.Section ?? "Weitere")
            .Select(g => (g.Key, g.Select(id => Row(id, SemanticCatalog.LabelOf(id))).ToList()))];

        foreach (var group in SemanticCatalog.Groups)
        {
            var count = Math.Max(Ctx.Extracted.Items(group.Id).Count, Ctx.Corrected.Items(group.Id).Count);
            if (count == 0)
            {
                continue;
            }

            var ids = SemanticCatalog.FieldsOf(group.Id).Select(f => f.Id).ToList();
            var items = new List<List<TermRow>>();
            for (var i = 0; i < count; i++)
            {
                items.Add([.. ids.Select(id => Row(SemanticDocument.ItemKey(group.Id, i, id), SemanticCatalog.LabelOf(id))).Where(r => r.Extracted is not null || r.Current is not null)]);
            }

            Groups.Add((group.Id, group.Label, items));
        }

        var raw = Ctx.Raw.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(RawSearch))
        {
            raw = raw.Where(f => f.Path.Contains(RawSearch, StringComparison.OrdinalIgnoreCase) || f.Value.Contains(RawSearch, StringComparison.OrdinalIgnoreCase));
        }

        var all = raw.ToList();
        RawTotal = all.Count;
        Raw = [.. all.Take(MaxRaw)];
        return Page();
    }
}
