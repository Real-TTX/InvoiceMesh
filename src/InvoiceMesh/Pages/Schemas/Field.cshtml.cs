using System.ComponentModel.DataAnnotations;
using InvoiceMesh.Core.Schemas;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using InvoiceMesh.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Pages.Schemas;

/// <summary>
/// A field of a schema (new or existing), always in the draft. Renaming the key renames the rules that fill it in the drafts of the mappings of this schema,
/// so that a rename does not silently cut a field off its sources.
/// </summary>
public sealed class FieldModel(ImDbContext db, DefinitionService definitions, NavCounts counts) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib eine Bezeichnung ein."), StringLength(200)]
        public string Label { get; set; } = "";

        [Required(ErrorMessage = "Bitte gib einen Schlüssel ein."), RegularExpression("^[A-Za-z][A-Za-z0-9_]{0,63}$", ErrorMessage = "Buchstaben, Ziffern und Unterstrich; zuerst ein Buchstabe.")]
        public string Key { get; set; } = "";

        public FieldType Type { get; set; } = FieldType.Text;

        public bool Required { get; set; }

        [StringLength(500)]
        public string? DefaultValue { get; set; }

        [StringLength(2000)]
        public string? Description { get; set; }

        [StringLength(4000)]
        public string? AllowedValues { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Group { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public string? Field { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public TargetSchema Schema { get; private set; } = null!;

    public SchemaGroup GroupDef { get; private set; } = null!;

    public bool IsNew => Field is null;

    public List<Microsoft.AspNetCore.Mvc.Rendering.SelectListItem> Types => Controls.Options.Of<FieldType>(FieldValues.TypeLabel);

    private async Task<(TargetSchemaVersion Version, SchemaDefinition Def)?> LoadAsync(bool forChange)
    {
        var schema = await db.TargetSchema.AsNoTracking().FirstOrDefaultAsync(s => s.Id == Id);
        if (schema is null)
        {
            return null;
        }

        Schema = schema;
        var version = forChange ? await definitions.EnsureSchemaDraftAsync(Id) : await definitions.SchemaDraftAsync(Id) ?? await definitions.ActiveSchemaVersionAsync(Id);
        if (version is null)
        {
            return null;
        }

        var def = DefinitionService.Definition(version);
        if (def.Group(Group) is not { } group)
        {
            return null;
        }

        GroupDef = group;
        var title = IsNew ? "Neues Feld" : group.Fields.FirstOrDefault(f => f.Key == Field)?.Label ?? Field!;
        Head(title, $"Gruppe „{group.Label}“ im Schema „{schema.Name}“. Die Änderung landet im Entwurf.", ("Zielschemas", "/Schemas"), (schema.Name, $"/Schemas/Edit?id={Id}"), (title, null));
        ViewData["ActiveHref"] = "/Schemas";
        return (version, def);
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await LoadAsync(false) is null)
        {
            return NotFound();
        }

        if (!IsNew)
        {
            var f = GroupDef.Fields.FirstOrDefault(x => x.Key == Field);
            if (f is null)
            {
                return NotFound();
            }

            Input = new InputModel { Label = f.Label, Key = f.Key, Type = f.Type, Required = f.Required, DefaultValue = f.DefaultValue, Description = f.Description, AllowedValues = string.Join("\n", f.AllowedValues) };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return await LoadAsync(false) is null ? NotFound() : Page();
        }

        var loaded = await LoadAsync(true);
        if (loaded is not { } l)
        {
            return NotFound();
        }

        var group = l.Def.Group(Group)!;
        var existing = IsNew ? null : group.Fields.FirstOrDefault(x => x.Key == Field);
        if (!IsNew && existing is null)
        {
            return NotFound();
        }

        if (group.Fields.Any(x => x != existing && string.Equals(x.Key, Input.Key, StringComparison.OrdinalIgnoreCase)))
        {
            ModelState.AddModelError("Input.Key", "Diesen Schlüssel gibt es in der Gruppe schon.");
            return Page();
        }

        var field = existing ?? new SchemaField();
        var oldKey = field.Key;
        field.Label = Input.Label.Trim();
        field.Key = Input.Key.Trim();
        field.Type = Input.Type;
        field.Required = Input.Required;
        field.DefaultValue = string.IsNullOrWhiteSpace(Input.DefaultValue) ? null : Input.DefaultValue.Trim();
        field.Description = string.IsNullOrWhiteSpace(Input.Description) ? null : Input.Description.Trim();
        field.AllowedValues = Input.Type == FieldType.Code ? [.. (Input.AllowedValues ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] : [];
        if (field.DefaultValue is { } dv && !FieldValues.TryConvert(field, dv, out _, out var message))
        {
            ModelState.AddModelError("Input.DefaultValue", message ?? "Der Standardwert passt nicht zum Typ.");
            return Page();
        }

        if (existing is null)
        {
            group.Fields.Add(field);
        }

        await definitions.SaveSchemaDraftAsync(l.Version, l.Def);
        var renamed = 0;
        if (existing is not null && oldKey != field.Key)
        {
            renamed = await RenameInMappingsAsync($"{Group}.{oldKey}", $"{Group}.{field.Key}");
        }

        counts.Invalidate();
        Flash((IsNew ? "Feld angelegt (im Entwurf)." : "Feld gespeichert (im Entwurf).") + (renamed > 0 ? $" {renamed} Mapping-Regel(n) wurden im Entwurf ihres Mappings mit umbenannt." : ""));
        return Redirect($"/Schemas/Edit?id={Id}#group-{Group}");
    }

    private async Task<int> RenameInMappingsAsync(string from, string to)
    {
        var count = 0;
        foreach (var set in await db.MappingSet.Where(m => m.TargetSchemaId == Id).ToListAsync())
        {
            var draft = await definitions.EnsureMappingDraftAsync(set.Id);
            var def = DefinitionService.Definition(draft);
            var hits = def.Rules.Where(r => string.Equals(r.Target, from, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0)
            {
                continue;
            }

            hits.ForEach(r => r.Target = to);
            count += hits.Count;
            await definitions.SaveMappingDraftAsync(draft, def);
        }

        return count;
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var loaded = await LoadAsync(true);
        if (loaded is not { } l || Field is null)
        {
            return NotFound();
        }

        l.Def.Group(Group)!.Fields.RemoveAll(f => f.Key == Field);
        await definitions.SaveSchemaDraftAsync(l.Version, l.Def);
        counts.Invalidate();
        Flash("Feld entfernt (im Entwurf). Regeln, die es füllen, werden im Mapping als „Feld unbekannt“ gemeldet, bis du sie entfernst.");
        return Redirect($"/Schemas/Edit?id={Id}#group-{Group}");
    }
}
