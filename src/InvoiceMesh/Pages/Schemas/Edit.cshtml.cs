using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using InvoiceMesh.Core;
using InvoiceMesh.Core.Mapping;
using InvoiceMesh.Core.Schemas;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using InvoiceMesh.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Pages.Schemas;

/// <summary>
/// A target schema: its name, its groups and fields in their order, and its versions. Every change of the fields goes into the draft (made from the active
/// version on the first change); activating the draft puts it in force for documents processed from then on.
/// </summary>
public sealed class EditModel(ImDbContext db, DefinitionService definitions, ActivityLogger log, NavCounts counts) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib einen Namen ein."), StringLength(200)]
        public string Name { get; set; } = "";

        [StringLength(2000)]
        public string? Description { get; set; }

        public bool IsDefault { get; set; }

        /// <summary>For a new schema: "empty", "standard" or the id of a schema to copy.</summary>
        public string Template { get; set; } = "standard";
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public TargetSchema? Schema { get; private set; }

    public TargetSchemaVersion? Active { get; private set; }

    public TargetSchemaVersion? Draft { get; private set; }

    public SchemaDefinition? Definition { get; private set; }

    public List<string> Changes { get; private set; } = [];

    public int OutdatedDocuments { get; private set; }

    public List<MappingSet> Mappings { get; private set; } = [];

    public List<SelectListItem> Templates { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (Schema is not null)
        {
            Input = new InputModel { Name = Schema.Name, Description = Schema.Description, IsDefault = Schema.IsDefault };
        }

        return Page();
    }

    private async Task<bool> LoadAsync()
    {
        if (Id is { } id)
        {
            Schema = await db.TargetSchema.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
            if (Schema is null)
            {
                return false;
            }

            Active = await definitions.ActiveSchemaVersionAsync(id);
            Draft = await definitions.SchemaDraftAsync(id);
            var working = Draft ?? Active;
            Definition = working is null ? null : DefinitionService.Definition(working);
            if (Draft is not null && Active is not null)
            {
                Changes = DefinitionService.Diff(DefinitionService.Definition(Active), DefinitionService.Definition(Draft));
            }

            OutdatedDocuments = await db.Invoice.CountAsync(i => i.SchemaVersionId != null && db.TargetSchemaVersion.Any(v => v.Id == i.SchemaVersionId && v.TargetSchemaId == id && v.State != VersionState.Active));
            Mappings = await db.MappingSet.AsNoTracking().Where(m => m.TargetSchemaId == id).OrderBy(m => m.Name).ToListAsync();
        }
        else
        {
            Templates =
            [
                new("Standardfelder einer Rechnung", "standard"),
                new("Leer (eine Gruppe „Rechnung“)", "empty"),
                .. (await db.TargetSchema.AsNoTracking().OrderBy(s => s.Name).ToListAsync()).Select(s => new SelectListItem($"Kopie von „{s.Name}“", s.Id.ToString())),
            ];
        }

        var title = Schema?.Name ?? "Neues Schema";
        Head(title, Schema is null ? "Felder und Gruppen legst du nach dem Anlegen fest." : Active is null ? "Noch keine aktive Version." : $"Aktiv: Version {Active.Number}" + (Draft is null ? "" : $" · Entwurf: Version {Draft.Number}"),
            ("Zielschemas", "/Schemas"), (title, null));
        ViewData["ActiveHref"] = "/Schemas";
        return true;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var schema = Id is { } id ? await db.TargetSchema.FirstOrDefaultAsync(s => s.Id == id) : null;
        if (Id is not null && schema is null)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        var isNew = schema is null;
        if (schema is null)
        {
            schema = new TargetSchema();
            db.TargetSchema.Add(schema);
        }

        schema.Name = Input.Name.Trim();
        schema.Description = string.IsNullOrWhiteSpace(Input.Description) ? null : Input.Description.Trim();
        if (Input.IsDefault)
        {
            await db.TargetSchema.Where(s => s.Id != schema.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsDefault, false));
            schema.IsDefault = true;
        }
        else if (!isNew && schema.IsDefault)
        {
            ModelState.AddModelError("Input.IsDefault", "Ein Schema bleibt der Standard, bis ein anderes dazu gemacht wird.");
            await LoadAsync();
            return Page();
        }

        await db.SaveChangesAsync();
        if (isNew)
        {
            var template = Input.Template switch
            {
                "standard" => Defaults.Schema(),
                "empty" => new SchemaDefinition { Groups = [new SchemaGroup { Key = "invoice", Label = "Rechnung", Fields = [new SchemaField { Key = "number", Label = "Rechnungsnummer", Required = true }] }] },
                var other when long.TryParse(other, out var copyId) && await definitions.ActiveSchemaVersionAsync(copyId) is { } v => DefinitionService.Definition(v),
                _ => Defaults.Schema(),
            };
            var draft = await definitions.EnsureSchemaDraftAsync(schema.Id);
            draft.ChangeNote = "Erste Version";
            await definitions.SaveSchemaDraftAsync(draft, template);
        }

        await log.LogAsync(isNew ? "Schema angelegt" : "Schema geändert", nameof(TargetSchema), schema.Id, schema.Name);
        counts.Invalidate();
        Flash(isNew ? "Das Schema ist angelegt, als Entwurf. Passe die Felder an und aktiviere es dann." : "Gespeichert.");
        return RedirectToPage(new { id = schema.Id });
    }

    /// <summary>Puts the draft in force.</summary>
    public async Task<IActionResult> OnPostActivateAsync(string? note)
    {
        if (Id is not { } id || await definitions.SchemaDraftAsync(id) is not { } draft)
        {
            return RedirectToPage(new { id = Id });
        }

        try
        {
            await definitions.ActivateSchemaAsync(draft.Id, note);
            await log.LogAsync("Schema aktiviert", nameof(TargetSchema), id, $"Version {draft.Number}");
            counts.Invalidate();
            Flash($"Version {draft.Number} ist aktiv. Neue Belege werden mit ihr normalisiert; bestehende behalten ihre Version, bis du sie neu normalisierst.");
        }
        catch (DefinitionException ex)
        {
            FlashError(ex.Message);
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDiscardAsync()
    {
        if (Id is { } id)
        {
            await definitions.DiscardSchemaDraftAsync(id);
            counts.Invalidate();
            Flash("Der Entwurf ist verworfen.");
        }

        return RedirectToPage(new { id = Id });
    }

    /// <summary>Moves a group (field null) or a field one place up or down, in the draft.</summary>
    public async Task<IActionResult> OnPostMoveAsync(string group, string? field, int dir)
    {
        if (Id is not { } id)
        {
            return NotFound();
        }

        var draft = await definitions.EnsureSchemaDraftAsync(id);
        var def = DefinitionService.Definition(draft);
        if (field is null)
        {
            Move(def.Groups, def.Groups.FindIndex(g => g.Key == group), dir);
        }
        else if (def.Group(group) is { } g)
        {
            Move(g.Fields, g.Fields.FindIndex(f => f.Key == field), dir);
        }

        await definitions.SaveSchemaDraftAsync(draft, def);
        counts.Invalidate();
        return Redirect($"/Schemas/Edit?id={id}#group-{group}");
    }

    private static void Move<T>(List<T> list, int index, int dir)
    {
        var target = index + Math.Sign(dir);
        if (index < 0 || target < 0 || target >= list.Count)
        {
            return;
        }

        (list[index], list[target]) = (list[target], list[index]);
    }

    /// <summary>The definition (draft or active) as JSON, to keep it or to take it into another installation.</summary>
    public async Task<IActionResult> OnGetExportAsync()
    {
        if (!await LoadAsync() || Definition is null || Schema is null)
        {
            return NotFound();
        }

        return File(Encoding.UTF8.GetBytes(CoreJson.Serialize(Definition, indented: true)), "application/json", Storage.FileStoreService.SafeFileName($"schema-{Schema.Name}.json"));
    }

    /// <summary>Replaces the draft by a definition from a JSON file (checked before it is taken).</summary>
    public async Task<IActionResult> OnPostImportAsync(IFormFile? file)
    {
        if (Id is not { } id || file is null || file.Length == 0)
        {
            FlashError("Bitte wähle eine JSON-Datei.");
            return RedirectToPage(new { id = Id });
        }

        SchemaDefinition def;
        try
        {
            using var reader = new StreamReader(file.OpenReadStream());
            def = CoreJson.Deserialize<SchemaDefinition>(await reader.ReadToEndAsync());
        }
        catch (JsonException ex)
        {
            FlashError("Die Datei ist kein Schema: " + ex.Message);
            return RedirectToPage(new { id });
        }

        if (def.Problems() is { Count: > 0 } problems)
        {
            FlashError(string.Join(" ", problems));
            return RedirectToPage(new { id });
        }

        var draft = await definitions.EnsureSchemaDraftAsync(id);
        await definitions.SaveSchemaDraftAsync(draft, def);
        counts.Invalidate();
        Flash("Das Schema aus der Datei steht im Entwurf. Prüfe die Änderungen und aktiviere es.");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var schema = Id is { } id ? await db.TargetSchema.FirstOrDefaultAsync(s => s.Id == id) : null;
        if (schema is null)
        {
            return RedirectToPage("/Schemas/Index");
        }

        if (schema.IsDefault)
        {
            FlashError("Das Standardschema kann nicht gelöscht werden. Mache zuerst ein anderes zum Standard.");
            return RedirectToPage(new { id = schema.Id });
        }

        if (await db.MappingSet.AnyAsync(m => m.TargetSchemaId == schema.Id) || await db.ExportProfile.AnyAsync(e => e.TargetSchemaId == schema.Id))
        {
            FlashError("Zu diesem Schema gibt es noch Mappings oder Exporte. Lösche sie zuerst.");
            return RedirectToPage(new { id = schema.Id });
        }

        // Soft delete: documents normalized with it keep their revisions and their versions.
        db.TargetSchema.Remove(schema);
        await db.SaveChangesAsync();
        await log.LogAsync("Schema gelöscht", nameof(TargetSchema), schema.Id, schema.Name);
        Flash("Das Schema ist gelöscht. Belege, die damit normalisiert wurden, behalten ihre Daten.");
        return RedirectToPage("/Schemas/Index");
    }
}
