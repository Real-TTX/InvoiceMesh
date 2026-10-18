using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Pages.Schemas;

/// <summary>The versions of a schema, newest first: state, who put it in force and when, the note, what changed against the version before, and how many documents use it.</summary>
public sealed class VersionsModel(ImDbContext db) : PageModelBase
{
    public sealed record Row(TargetSchemaVersion Version, List<string> Changes, int Documents);

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    public TargetSchema Schema { get; private set; } = null!;

    public List<Row> Rows { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        var schema = await db.TargetSchema.AsNoTracking().FirstOrDefaultAsync(s => s.Id == Id);
        if (schema is null)
        {
            return NotFound();
        }

        Schema = schema;
        Head("Versionen", $"Schema „{schema.Name}“: jede aktivierte Version bleibt erhalten, damit jede Normalisierung nachvollziehbar bleibt.", ("Zielschemas", "/Schemas"), (schema.Name, $"/Schemas/Edit?id={Id}"), ("Versionen", null));
        ViewData["ActiveHref"] = "/Schemas";
        var versions = await db.TargetSchemaVersion.AsNoTracking().Where(v => v.TargetSchemaId == Id).OrderBy(v => v.Number).ToListAsync();
        var ids = versions.Select(v => v.Id).ToList();
        var usage = await db.Invoice.Where(i => i.SchemaVersionId != null && ids.Contains(i.SchemaVersionId.Value)).GroupBy(i => i.SchemaVersionId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        for (var i = 0; i < versions.Count; i++)
        {
            var changes = i == 0 ? ["Erste Version."] : DefinitionService.Diff(DefinitionService.Definition(versions[i - 1]), DefinitionService.Definition(versions[i]));
            Rows.Add(new Row(versions[i], changes.Count == 0 ? ["Keine Änderung der Felder."] : changes, usage.GetValueOrDefault(versions[i].Id)));
        }

        Rows.Reverse();
        return Page();
    }
}
