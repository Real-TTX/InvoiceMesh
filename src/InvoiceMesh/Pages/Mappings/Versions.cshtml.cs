using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Pages.Mappings;

/// <summary>The versions of a mapping, newest first, with what changed against the version before and how many revisions were made with each.</summary>
public sealed class VersionsModel(ImDbContext db) : PageModelBase
{
    public sealed record Row(MappingSetVersion Version, List<string> Changes, int Revisions);

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    public List<Row> Rows { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        var set = await db.MappingSet.AsNoTracking().FirstOrDefaultAsync(m => m.Id == Id);
        if (set is null)
        {
            return NotFound();
        }

        Head("Versionen", $"Mapping „{set.Name}“: jede aktivierte Version bleibt erhalten, damit jede Normalisierung nachvollziehbar bleibt.", ("Mappings", "/Mappings"), (set.Name, $"/Mappings/Edit?id={Id}"), ("Versionen", null));
        ViewData["ActiveHref"] = "/Mappings";
        var versions = await db.MappingSetVersion.AsNoTracking().Where(v => v.MappingSetId == Id).OrderBy(v => v.Number).ToListAsync();
        var ids = versions.Select(v => v.Id).ToList();
        var usage = await db.InvoiceRevision.Where(r => ids.Contains(r.MappingVersionId)).GroupBy(r => r.MappingVersionId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        for (var i = 0; i < versions.Count; i++)
        {
            var changes = i == 0 ? [$"Erste Version mit {DefinitionService.Definition(versions[i]).Rules.Count} Regeln."] : DefinitionService.Diff(DefinitionService.Definition(versions[i - 1]), DefinitionService.Definition(versions[i]));
            Rows.Add(new Row(versions[i], changes.Count == 0 ? ["Keine Änderung der Regeln."] : changes, usage.GetValueOrDefault(versions[i].Id)));
        }

        Rows.Reverse();
        return Page();
    }
}
