using InvoiceMesh.Core.Mapping;
using InvoiceMesh.Core.Schemas;
using InvoiceMesh.Core.Semantics;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using InvoiceMesh.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Pages.Mappings;

/// <summary>
/// One rule of a mapping: the sources of a field in their order of priority, how several become one (the first, or all joined), the transformations, an
/// optional condition and a default. Beside it the catalogue of sources with the values of a sample document, and what the rule makes of that document.
/// </summary>
public sealed class RuleModel(ImDbContext db, DefinitionService definitions, InvoiceService invoices, NavCounts counts) : PageModelBase
{
    public const int TransformRows = 6;

    public sealed class StepInput
    {
        public string? Op { get; set; }

        public string? Arg { get; set; }

        public string? Arg2 { get; set; }
    }

    public sealed class InputModel
    {
        public string? Sources { get; set; }

        public CombineMode Combine { get; set; } = CombineMode.First;

        public string? Separator { get; set; } = " ";

        public bool IgnoreDifferences { get; set; }

        public List<StepInput> Steps { get; set; } = [];

        public bool UseCondition { get; set; }

        public string? ConditionSource { get; set; }

        public ConditionOperator ConditionOperator { get; set; } = ConditionOperator.Equals;

        public string? ConditionValue { get; set; }

        public string? DefaultValue { get; set; }

        public string? Note { get; set; }
    }

    public sealed record CatalogEntry(string Reference, string Label, string? Sample, string Kind);

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Target { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public int Index { get; set; }

    [BindProperty(SupportsGet = true)]
    public long? Sample { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public MappingSet Set { get; private set; } = null!;

    public SchemaGroup Group { get; private set; } = null!;

    public SchemaField Field { get; private set; } = null!;

    public bool IsNew { get; private set; }

    public string? Collection { get; private set; }

    public List<CatalogEntry> Catalog { get; private set; } = [];

    public List<SelectListItem> Samples { get; private set; } = [];

    public InvoiceDocument? SampleDoc { get; private set; }

    public List<FieldOutcome> Outcomes { get; private set; } = [];

    public static List<SelectListItem> Operations => [new("—", ""), .. Transforms.All.Select(t => new SelectListItem(t.Label, t.Op))];

    public static List<SelectListItem> Operators =>
    [
        new("ist gleich", nameof(ConditionOperator.Equals)), new("ist nicht", nameof(ConditionOperator.NotEquals)), new("enthält", nameof(ConditionOperator.Contains)),
        new("beginnt mit", nameof(ConditionOperator.StartsWith)), new("passt zum Muster", nameof(ConditionOperator.Matches)), new("ist leer", nameof(ConditionOperator.IsEmpty)),
        new("ist nicht leer", nameof(ConditionOperator.IsNotEmpty)),
    ];

    private async Task<(MappingSetVersion Version, MappingDefinition Def, SchemaDefinition Schema)?> LoadAsync(bool forChange)
    {
        var set = await db.MappingSet.AsNoTracking().Include(m => m.TargetSchema).FirstOrDefaultAsync(m => m.Id == Id);
        if (set is null)
        {
            return null;
        }

        Set = set;
        var version = forChange ? await definitions.EnsureMappingDraftAsync(Id) : await definitions.MappingDraftAsync(Id) ?? await definitions.ActiveMappingVersionAsync(Id);
        var schemaVersion = await definitions.SchemaDraftAsync(set.TargetSchemaId) ?? await definitions.ActiveSchemaVersionAsync(set.TargetSchemaId);
        if (version is null || schemaVersion is null)
        {
            return null;
        }

        var def = DefinitionService.Definition(version);
        var schema = DefinitionService.Definition(schemaVersion);
        if (schema.Find(Target) is not { } found)
        {
            return null;
        }

        (Group, Field) = found;
        Collection = def.CollectionOf(Group.Key);
        var rules = def.RulesFor(Target).ToList();
        IsNew = Index >= rules.Count;
        Head($"Regel: {Group.Label} · {Field.Label}", $"Zielfeld {Target} ({FieldValues.TypeLabel(Field.Type)}{(Field.Required ? ", Pflichtfeld" : "")}) im Mapping „{set.Name}“. Die Änderung landet im Entwurf.",
            ("Mappings", "/Mappings"), (set.Name, $"/Mappings/Edit?id={Id}"), (Field.Label, null));
        ViewData["ActiveHref"] = "/Mappings";
        await LoadSampleAsync(version, schema, def);
        return (version, def, schema);
    }

    /// <summary>The sample document: the chosen one, the one of the version, or the newest processed document the mapping fits.</summary>
    private async Task LoadSampleAsync(MappingSetVersion version, SchemaDefinition schema, MappingDefinition def)
    {
        var docs = await db.Invoice.AsNoTracking().Where(i => i.ProcessingStatus == ProcessingStatus.Processed && i.CurrentExtractionId != null)
            .OrderByDescending(i => i.ReceivedDate).Take(200).ToListAsync();
        Samples = [.. docs.Select(d => new SelectListItem($"{d.DisplayName} · {d.FormatCode}", d.Id.ToString()))];
        var id = Sample ?? version.SampleInvoiceId ?? docs.FirstOrDefault(d => FitsSelector(d))?.Id ?? docs.FirstOrDefault()?.Id;
        var ctx = id is { } sid ? await invoices.LoadAsync(sid) : null;
        SampleDoc = ctx?.Doc;
        Sample = SampleDoc?.Id;
        BuildCatalog(ctx);
        if (ctx is not null)
        {
            Outcomes = [.. InvoiceService.NormalizeInMemory(ctx, schema, def, Set.TargetSchemaId).Report.Fields.Where(f => f.Target == Target)];
        }
    }

    private bool FitsSelector(InvoiceDocument d)
        => (Set.Syntax is null || string.Equals(Set.Syntax, d.Syntax ?? "None", StringComparison.OrdinalIgnoreCase))
           && (Set.Standard is null || (d.Standard ?? "").Contains(Set.Standard, StringComparison.OrdinalIgnoreCase));

    private void BuildCatalog(InvoiceContext? ctx)
    {
        var semanticGroup = Group.Repeating && Collection?.StartsWith("bg:", StringComparison.Ordinal) == true ? Collection[3..] : null;
        var firstItem = semanticGroup is null ? null : ctx?.Corrected.Items(semanticGroup).FirstOrDefault();
        if (Group.Repeating)
        {
            if (semanticGroup is not null)
            {
                Catalog.AddRange(SemanticCatalog.FieldsOf(semanticGroup).Select(f => new CatalogEntry("bt:" + f.Id, f.Label, firstItem?.Get(f.Id), "Eintrag (EN 16931)")));
            }

            Catalog.AddRange(SemanticCatalog.FieldsOf(null).Select(f => new CatalogEntry("head:" + f.Id, f.Label, ctx?.Corrected.Get(f.Id), "Rechnung (EN 16931)")));
            Catalog.Add(new CatalogEntry("meta:index", "Nummer des Eintrags (1, 2, …)", "1", "Verarbeitung"));
            var prefix = Collection?.StartsWith("xml:", StringComparison.Ordinal) == true ? Collection[4..] : firstItem?.Origin is { } origin ? Core.Extraction.SourceField.StripIndexes(origin) : null;
            if (prefix is not null && ctx is not null)
            {
                Catalog.AddRange(ctx.Raw.Where(r => r.Pattern.StartsWith(prefix + "/", StringComparison.Ordinal))
                    .GroupBy(r => r.Pattern[(prefix.Length + 1)..]).Select(g => new CatalogEntry("xml:" + g.Key, "XML im Eintrag", g.First().Value, "Rohdaten")));
            }
        }
        else
        {
            Catalog.AddRange(SemanticCatalog.FieldsOf(null).Select(f => new CatalogEntry("bt:" + f.Id, f.Label, ctx?.Corrected.Get(f.Id), "EN 16931")));
            foreach (var (key, label) in new[] { ("fileName", "Dateiname"), ("format", "Format (Zusammenfassung)"), ("syntax", "Syntax"), ("standard", "Standard"), ("profile", "Profil"), ("version", "Version"), ("method", "Verfahren"), ("receivedDate", "Eingangsdatum"), ("sha256", "SHA-256 des Originals"), ("documentId", "Belegnummer in InvoiceMesh") })
            {
                Catalog.Add(new CatalogEntry("meta:" + key, label, ctx is null ? null : InvoiceService.BuildInput(ctx.Doc, new ExtractionResultParts(ctx.Format ?? new(), ctx.Raw, ctx.Extracted, ctx.Messages), ctx.Corrected).Meta.GetValueOrDefault(key), "Verarbeitung"));
            }

            if (ctx is not null)
            {
                Catalog.AddRange(ctx.Raw.Where(r => !r.Path.Contains('[') || r.Path.StartsWith("text:", StringComparison.Ordinal))
                    .Select(r => new CatalogEntry(r.Path.StartsWith("text:", StringComparison.Ordinal) ? r.Path : "xml:" + r.Path, r.Path.StartsWith("text:", StringComparison.Ordinal) ? "Bezeichnung im Text" : "XML", r.Value, "Rohdaten")));
            }
        }
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var loaded = await LoadAsync(false);
        if (loaded is not { } l)
        {
            return NotFound();
        }

        var rule = l.Def.RulesFor(Target).ElementAtOrDefault(Index);
        Input = rule is null
            ? new InputModel { UseCondition = Index > 0 }
            : new InputModel
            {
                Sources = string.Join("\n", rule.Sources),
                Combine = rule.Combine,
                Separator = rule.Separator,
                IgnoreDifferences = rule.IgnoreDifferences,
                Steps = [.. rule.Transforms.Select(t => new StepInput { Op = t.Op, Arg = t.Arg, Arg2 = t.Arg2 })],
                UseCondition = rule.Condition is not null,
                ConditionSource = rule.Condition?.Source,
                ConditionOperator = rule.Condition?.Operator ?? ConditionOperator.Equals,
                ConditionValue = rule.Condition?.Value,
                DefaultValue = rule.DefaultValue,
                Note = rule.Note,
            };
        while (Input.Steps.Count < TransformRows)
        {
            Input.Steps.Add(new StepInput());
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var sources = (Input.Sources ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        foreach (var bad in sources.Where(s => !s.Contains(':', StringComparison.Ordinal)))
        {
            ModelState.AddModelError("Input.Sources", $"„{bad}“ ist keine Quelle (Form bt:BT-1, xml:/Pfad, text:Bezeichnung, meta:Name, const:Wert).");
        }

        if (sources.Count == 0 && string.IsNullOrWhiteSpace(Input.DefaultValue))
        {
            ModelState.AddModelError("Input.Sources", "Gib mindestens eine Quelle oder einen Standardwert an.");
        }

        if (Input.UseCondition && string.IsNullOrWhiteSpace(Input.ConditionSource))
        {
            ModelState.AddModelError("Input.ConditionSource", "Die Bedingung braucht eine Quelle.");
        }

        var loaded = await LoadAsync(ModelState.IsValid);
        if (loaded is not { } l)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            while (Input.Steps.Count < TransformRows)
            {
                Input.Steps.Add(new StepInput());
            }

            return Page();
        }

        var rule = new MappingRule
        {
            Target = Target,
            Sources = sources,
            Combine = Input.Combine,
            Separator = Input.Combine == CombineMode.Concat ? Input.Separator ?? " " : null,
            IgnoreDifferences = Input.IgnoreDifferences,
            Transforms = [.. Input.Steps.Where(s => !string.IsNullOrWhiteSpace(s.Op)).Select(s => new TransformStep { Op = s.Op!, Arg = string.IsNullOrEmpty(s.Arg) ? null : s.Arg, Arg2 = string.IsNullOrEmpty(s.Arg2) ? null : s.Arg2 })],
            Condition = Input.UseCondition ? new RuleCondition { Source = Input.ConditionSource!.Trim(), Operator = Input.ConditionOperator, Value = Input.ConditionValue } : null,
            DefaultValue = string.IsNullOrWhiteSpace(Input.DefaultValue) ? null : Input.DefaultValue,
            Note = string.IsNullOrWhiteSpace(Input.Note) ? null : Input.Note.Trim(),
        };

        Replace(l.Def, rule);
        await definitions.SaveMappingDraftAsync(l.Version, l.Def);
        counts.Invalidate();
        Flash("Regel gespeichert (im Entwurf). Prüfe sie in der Vorschau.");
        return RedirectToPage(new { id = Id, target = Target, index = Index, sample = Sample });
    }

    /// <summary>Puts the rule at its place among the rules of the same target (the n-th of them), or adds it after them.</summary>
    private void Replace(MappingDefinition def, MappingRule? rule)
    {
        var positions = def.Rules.Select((r, i) => (r, i)).Where(x => string.Equals(x.r.Target, Target, StringComparison.OrdinalIgnoreCase)).Select(x => x.i).ToList();
        if (Index < positions.Count)
        {
            if (rule is null)
            {
                def.Rules.RemoveAt(positions[Index]);
            }
            else
            {
                def.Rules[positions[Index]] = rule;
            }
        }
        else if (rule is not null)
        {
            var at = positions.Count > 0 ? positions[^1] + 1 : def.Rules.Count;
            def.Rules.Insert(at, rule);
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var loaded = await LoadAsync(true);
        if (loaded is not { } l)
        {
            return NotFound();
        }

        Replace(l.Def, null);
        await definitions.SaveMappingDraftAsync(l.Version, l.Def);
        counts.Invalidate();
        Flash("Regel entfernt (im Entwurf).");
        return RedirectToPage("/Mappings/Edit", new { id = Id });
    }
}
