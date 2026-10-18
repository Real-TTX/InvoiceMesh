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

namespace InvoiceMesh.Pages.Mappings;

/// <summary>
/// A mapping: to which schema, for which documents (syntax, standard, profile, version), with which priority; which source list fills each repeating group;
/// and the rules for every field, shown against the fields of the schema so that a field without a rule is plain to see. Changes go into a draft.
/// </summary>
public sealed class EditModel(ImDbContext db, DefinitionService definitions, ActivityLogger log, NavCounts counts) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib einen Namen ein."), StringLength(200)]
        public string Name { get; set; } = "";

        [StringLength(2000)]
        public string? Description { get; set; }

        public long TargetSchemaId { get; set; }

        public string? Syntax { get; set; }

        [StringLength(100)]
        public string? Standard { get; set; }

        [StringLength(100)]
        public string? Profile { get; set; }

        [StringLength(50)]
        public string? FormatVersion { get; set; }

        [Range(-1000, 1000)]
        public int Priority { get; set; }

        public bool IsEnabled { get; set; } = true;

        /// <summary>For a new mapping: "empty", "standard" or the id of a mapping to copy.</summary>
        public string Template { get; set; } = "standard";
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty(SupportsGet = true, Name = "schema")]
    public long? SchemaFromQuery { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public MappingSet? Set { get; private set; }

    public MappingSetVersion? Active { get; private set; }

    public MappingSetVersion? Draft { get; private set; }

    public MappingDefinition? Definition { get; private set; }

    public SchemaDefinition? Schema { get; private set; }

    public bool SchemaIsDraft { get; private set; }

    public List<string> Changes { get; private set; } = [];

    public List<string> Problems { get; private set; } = [];

    public List<SelectListItem> Schemas { get; private set; } = [];

    public List<SelectListItem> Templates { get; private set; } = [];

    public static readonly List<SelectListItem> Syntaxes =
    [
        new("Beliebig", ""), new("UBL", "Ubl"), new("CII", "Cii"), new("PDF-Text und Scans", "None"),
    ];

    /// <summary>The lists of the source a repeating group can be filled from.</summary>
    public static readonly List<SelectListItem> Lists =
    [
        new("Keine", ""), new("Positionen (BG-25)", "bg:BG-25"), new("Umsatzsteueraufschlüsselung (BG-23)", "bg:BG-23"), new("Nachlässe (BG-20)", "bg:BG-20"), new("Zuschläge (BG-21)", "bg:BG-21"),
    ];

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (Set is not null)
        {
            Input = new InputModel
            {
                Name = Set.Name, Description = Set.Description, TargetSchemaId = Set.TargetSchemaId, Syntax = Set.Syntax, Standard = Set.Standard, Profile = Set.Profile,
                FormatVersion = Set.FormatVersion, Priority = Set.Priority, IsEnabled = Set.IsEnabled,
            };
        }
        else if (SchemaFromQuery is { } sid)
        {
            Input.TargetSchemaId = sid;
        }

        return Page();
    }

    private async Task<bool> LoadAsync()
    {
        Schemas = [.. (await db.TargetSchema.AsNoTracking().OrderBy(s => s.Name).ToListAsync()).Select(s => new SelectListItem(s.Name, s.Id.ToString()))];
        if (Id is { } id)
        {
            Set = await db.MappingSet.AsNoTracking().Include(m => m.TargetSchema).FirstOrDefaultAsync(m => m.Id == id);
            if (Set is null)
            {
                return false;
            }

            Active = await definitions.ActiveMappingVersionAsync(id);
            Draft = await definitions.MappingDraftAsync(id);
            var working = Draft ?? Active;
            Definition = working is null ? new MappingDefinition() : DefinitionService.Definition(working);
            if (Draft is not null && Active is not null)
            {
                Changes = DefinitionService.Diff(DefinitionService.Definition(Active), DefinitionService.Definition(Draft));
            }

            var schemaDraft = await definitions.SchemaDraftAsync(Set.TargetSchemaId);
            var schemaVersion = schemaDraft ?? await definitions.ActiveSchemaVersionAsync(Set.TargetSchemaId);
            SchemaIsDraft = schemaDraft is not null;
            Schema = schemaVersion is null ? null : DefinitionService.Definition(schemaVersion);
            if (Schema is not null)
            {
                Problems = MappingEngine.Run(Schema, Definition, new MappingInput()).Report.DefinitionProblems;
            }
        }
        else
        {
            Templates =
            [
                new("Regeln für das Standardschema (EN 16931)", "standard"),
                new("Leer", "empty"),
                .. (await db.MappingSet.AsNoTracking().OrderBy(m => m.Name).ToListAsync()).Select(m => new SelectListItem($"Kopie von „{m.Name}“", m.Id.ToString())),
            ];
        }

        var title = Set?.Name ?? "Neues Mapping";
        Head(title, Set is null ? "Für welches Schema und welche Belege." : $"{DefinitionService.SelectorText(Set)} → {Set.TargetSchema?.Name}" + (Active is null ? " · noch keine aktive Version" : $" · aktiv: v{Active.Number}") + (Draft is null ? "" : $" · Entwurf: v{Draft.Number}"),
            ("Mappings", "/Mappings"), (title, null));
        ViewData["ActiveHref"] = "/Mappings";
        return true;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var set = Id is { } id ? await db.MappingSet.FirstOrDefaultAsync(m => m.Id == id) : null;
        if (Id is not null && set is null)
        {
            return NotFound();
        }

        if (!await db.TargetSchema.AnyAsync(s => s.Id == Input.TargetSchemaId))
        {
            ModelState.AddModelError("Input.TargetSchemaId", "Bitte wähle ein Zielschema.");
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        var isNew = set is null;
        if (set is null)
        {
            set = new MappingSet { TargetSchemaId = Input.TargetSchemaId };
            db.MappingSet.Add(set);
        }

        string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        set.Name = Input.Name.Trim();
        set.Description = Clean(Input.Description);
        set.Syntax = Clean(Input.Syntax);
        set.Standard = Clean(Input.Standard);
        set.Profile = Clean(Input.Profile);
        set.FormatVersion = Clean(Input.FormatVersion);
        set.Priority = Input.Priority;
        set.IsEnabled = Input.IsEnabled;
        await db.SaveChangesAsync();

        if (isNew)
        {
            var template = Input.Template switch
            {
                "standard" => Defaults.Mapping(),
                "empty" => new MappingDefinition(),
                var other when long.TryParse(other, out var copyId) && (await definitions.MappingDraftAsync(copyId) ?? await definitions.ActiveMappingVersionAsync(copyId)) is { } v => DefinitionService.Definition(v),
                _ => new MappingDefinition(),
            };
            var draft = await definitions.EnsureMappingDraftAsync(set.Id);
            draft.ChangeNote = "Erste Version";
            await definitions.SaveMappingDraftAsync(draft, template);
        }

        await log.LogAsync(isNew ? "Mapping angelegt" : "Mapping geändert", nameof(MappingSet), set.Id, set.Name);
        counts.Invalidate();
        Flash(isNew ? "Das Mapping ist angelegt, als Entwurf. Prüfe die Regeln, teste sie und aktiviere es." : "Gespeichert.");
        return RedirectToPage(new { id = set.Id });
    }

    /// <summary>Which source list fills each repeating group.</summary>
    public async Task<IActionResult> OnPostListsAsync(Dictionary<string, string> lists, Dictionary<string, string> paths)
    {
        if (Id is not { } id)
        {
            return NotFound();
        }

        var draft = await definitions.EnsureMappingDraftAsync(id);
        var def = DefinitionService.Definition(draft);
        def.Collections.Clear();
        foreach (var (group, choice) in lists)
        {
            var source = choice == "xml" ? paths.GetValueOrDefault(group) is { Length: > 0 } p ? "xml:" + p.Trim() : null : string.IsNullOrWhiteSpace(choice) ? null : choice;
            if (source is not null)
            {
                def.Collections.Add(new CollectionBinding { Group = group, Source = source });
            }
        }

        await definitions.SaveMappingDraftAsync(draft, def);
        counts.Invalidate();
        Flash("Die Listen sind gespeichert (im Entwurf).");
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostActivateAsync(string? note)
    {
        if (Id is not { } id || await definitions.MappingDraftAsync(id) is not { } draft)
        {
            return RedirectToPage(new { id = Id });
        }

        try
        {
            await definitions.ActivateMappingAsync(draft.Id, note);
            await log.LogAsync("Mapping aktiviert", nameof(MappingSet), id, $"Version {draft.Number}");
            counts.Invalidate();
            Flash($"Version {draft.Number} ist aktiv. Belege werden ab jetzt damit normalisiert; bestehende behalten ihre Revision, bis du sie neu normalisierst.");
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
            await definitions.DiscardMappingDraftAsync(id);
            counts.Invalidate();
            Flash("Der Entwurf ist verworfen.");
        }

        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnGetExportAsync()
    {
        if (!await LoadAsync() || Set is null || Definition is null)
        {
            return NotFound();
        }

        return File(Encoding.UTF8.GetBytes(CoreJson.Serialize(Definition, indented: true)), "application/json", Storage.FileStoreService.SafeFileName($"mapping-{Set.Name}.json"));
    }

    public async Task<IActionResult> OnPostImportAsync(IFormFile? file)
    {
        if (Id is not { } id || file is null || file.Length == 0)
        {
            FlashError("Bitte wähle eine JSON-Datei.");
            return RedirectToPage(new { id = Id });
        }

        try
        {
            using var reader = new StreamReader(file.OpenReadStream());
            var def = CoreJson.Deserialize<MappingDefinition>(await reader.ReadToEndAsync());
            var draft = await definitions.EnsureMappingDraftAsync(id);
            await definitions.SaveMappingDraftAsync(draft, def);
            counts.Invalidate();
            Flash($"{Ui.CountNoun(def.Rules.Count, "Regeln")} aus der Datei stehen im Entwurf. Teste sie und aktiviere den Entwurf.");
        }
        catch (JsonException ex)
        {
            FlashError("Die Datei ist kein Mapping: " + ex.Message);
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var set = Id is { } id ? await db.MappingSet.FirstOrDefaultAsync(m => m.Id == id) : null;
        if (set is null)
        {
            return RedirectToPage("/Mappings/Index");
        }

        // Soft delete: revisions keep their mapping version.
        db.MappingSet.Remove(set);
        await db.SaveChangesAsync();
        await log.LogAsync("Mapping gelöscht", nameof(MappingSet), set.Id, set.Name);
        counts.Invalidate();
        Flash("Das Mapping ist gelöscht. Belege, die damit normalisiert wurden, behalten ihre Daten.");
        return RedirectToPage("/Mappings/Index");
    }

    public IEnumerable<MappingRule> RulesFor(string target) => Definition?.RulesFor(target) ?? [];

    public static string Describe(TransformStep t) => (Transforms.Info(t.Op)?.Label ?? t.Op) + (t.Arg is null ? "" : $" „{t.Arg}“") + (t.Arg2 is null ? "" : $" „{t.Arg2}“");
}
