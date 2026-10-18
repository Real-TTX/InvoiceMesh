using System.ComponentModel.DataAnnotations;
using InvoiceMesh.Core.Schemas;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using InvoiceMesh.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Pages.Schemas;

/// <summary>A group of fields of a schema (new or existing): the invoice, a party, the totals, or a repeating group such as the lines. Changes go into the draft.</summary>
public sealed class GroupModel(ImDbContext db, DefinitionService definitions, NavCounts counts) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib eine Bezeichnung ein."), StringLength(200)]
        public string Label { get; set; } = "";

        [Required(ErrorMessage = "Bitte gib einen Schlüssel ein."), RegularExpression("^[A-Za-z][A-Za-z0-9_]{0,63}$", ErrorMessage = "Buchstaben, Ziffern und Unterstrich; zuerst ein Buchstabe.")]
        public string Key { get; set; } = "";

        [StringLength(2000)]
        public string? Description { get; set; }

        public bool Repeating { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Group { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool IsNew => Group is null;

    private async Task<(TargetSchemaVersion Version, SchemaDefinition Def)?> LoadAsync(bool forChange)
    {
        var schema = await db.TargetSchema.AsNoTracking().FirstOrDefaultAsync(s => s.Id == Id);
        var version = schema is null ? null : forChange ? await definitions.EnsureSchemaDraftAsync(Id) : await definitions.SchemaDraftAsync(Id) ?? await definitions.ActiveSchemaVersionAsync(Id);
        if (schema is null || version is null)
        {
            return null;
        }

        var def = DefinitionService.Definition(version);
        if (!IsNew && def.Group(Group!) is null)
        {
            return null;
        }

        var title = IsNew ? "Neue Gruppe" : def.Group(Group!)!.Label;
        Head(title, $"Schema „{schema.Name}“. Die Änderung landet im Entwurf.", ("Zielschemas", "/Schemas"), (schema.Name, $"/Schemas/Edit?id={Id}"), (title, null));
        ViewData["ActiveHref"] = "/Schemas";
        return (version, def);
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var loaded = await LoadAsync(false);
        if (loaded is not { } l)
        {
            return NotFound();
        }

        if (!IsNew)
        {
            var g = l.Def.Group(Group!)!;
            Input = new InputModel { Label = g.Label, Key = g.Key, Description = g.Description, Repeating = g.Repeating };
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

        var group = IsNew ? null : l.Def.Group(Group!);
        if (l.Def.Groups.Any(g => g != group && string.Equals(g.Key, Input.Key, StringComparison.OrdinalIgnoreCase)))
        {
            ModelState.AddModelError("Input.Key", "Diesen Schlüssel gibt es im Schema schon.");
            return Page();
        }

        if (group is null)
        {
            group = new SchemaGroup();
            l.Def.Groups.Add(group);
        }

        var oldKey = group.Key;
        group.Label = Input.Label.Trim();
        group.Key = Input.Key.Trim();
        group.Description = string.IsNullOrWhiteSpace(Input.Description) ? null : Input.Description.Trim();
        group.Repeating = Input.Repeating;
        await definitions.SaveSchemaDraftAsync(l.Version, l.Def);

        // A new key of the group is carried into the drafts of the mappings (the rules and the list the group is filled from).
        var renamed = 0;
        if (!IsNew && oldKey != group.Key)
        {
            foreach (var set in await db.MappingSet.Where(m => m.TargetSchemaId == Id).ToListAsync())
            {
                var draft = await definitions.EnsureMappingDraftAsync(set.Id);
                var def = DefinitionService.Definition(draft);
                foreach (var rule in def.Rules.Where(r => r.Target.StartsWith(oldKey + ".", StringComparison.OrdinalIgnoreCase)))
                {
                    rule.Target = group.Key + rule.Target[oldKey.Length..];
                    renamed++;
                }

                def.Collections.Where(c => string.Equals(c.Group, oldKey, StringComparison.OrdinalIgnoreCase)).ToList().ForEach(c => c.Group = group.Key);
                await definitions.SaveMappingDraftAsync(draft, def);
            }
        }

        counts.Invalidate();
        Flash((IsNew ? "Gruppe angelegt (im Entwurf). Füge ihr Felder hinzu." : "Gruppe gespeichert (im Entwurf).") + (renamed > 0 ? $" {renamed} Mapping-Regel(n) wurden mit umbenannt." : ""));
        return Redirect($"/Schemas/Edit?id={Id}#group-{group.Key}");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var loaded = await LoadAsync(true);
        if (loaded is not { } l || IsNew)
        {
            return NotFound();
        }

        l.Def.Groups.RemoveAll(g => g.Key == Group);
        await definitions.SaveSchemaDraftAsync(l.Version, l.Def);
        counts.Invalidate();
        Flash("Gruppe entfernt (im Entwurf).");
        return RedirectToPage("/Schemas/Edit", new { id = Id });
    }
}
