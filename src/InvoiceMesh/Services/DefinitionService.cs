using InvoiceMesh.Core;
using InvoiceMesh.Core.Formats;
using InvoiceMesh.Core.Mapping;
using InvoiceMesh.Core.Output;
using InvoiceMesh.Core.Schemas;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Services;

public sealed class DefinitionException(string message) : Exception(message);

/// <summary>
/// The versioned definitions of the user: target schemas and mappings. Each has versions; only a draft can change, activating it retires the version that was
/// active before, and the documents keep the version they were normalized with, so that every result can be reproduced. A change to an active definition
/// therefore always goes through a draft (made on the first change), which can be tried in the preview before it is activated.
/// </summary>
public sealed class DefinitionService(ImDbContext db, ICurrentUser user, TimeProvider clock)
{
    // ------------------------------------------------------------------------------------------------------------ start

    /// <summary>A new installation gets the standard schema, the mapping for all formats and four exports.</summary>
    public async Task EnsureDefaultsAsync(CancellationToken ct = default)
    {
        if (await db.TargetSchema.IgnoreQueryFilters().AnyAsync(ct))
        {
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var schema = new TargetSchema { Name = "Standard", Description = "Die üblichen Angaben einer Rechnung, mit Positionen und Steuern.", IsDefault = true };
        schema.Versions.Add(new TargetSchemaVersion
        {
            Number = 1, State = VersionState.Active, ActivatedDate = now, ActivatedBy = "System", ChangeNote = "Erste Version",
            DefinitionJson = CoreJson.Serialize(Defaults.Schema()),
        });
        db.TargetSchema.Add(schema);
        await db.SaveChangesAsync(ct);

        var mapping = new MappingSet
        {
            Name = "EN 16931 – alle Formate", TargetSchemaId = schema.Id,
            Description = "Füllt das Standardschema aus den Geschäftsbegriffen von EN 16931. Gilt für UBL, CII, PDF-Text und Scans gleichermaßen.",
        };
        mapping.Versions.Add(new MappingSetVersion
        {
            Number = 1, State = VersionState.Active, ActivatedDate = now, ActivatedBy = "System", ChangeNote = "Erste Version",
            DefinitionJson = CoreJson.Serialize(Defaults.Mapping()),
        });
        db.MappingSet.Add(mapping);

        db.ExportProfile.AddRange(
            new ExportProfile { Name = "JSON (vollständig)", TargetSchemaId = schema.Id, Description = "Alle Gruppen, verschachtelt.", OptionsJson = CoreJson.Serialize(new ExportOptions { Format = ExportFormat.Json }) },
            new ExportProfile { Name = "CSV je Rechnung", TargetSchemaId = schema.Id, Description = "Eine Zeile je Rechnung, für die Buchhaltung.", OptionsJson = CoreJson.Serialize(new ExportOptions { Format = ExportFormat.Csv, Groups = ["invoice", "seller", "buyer", "totals"], CsvLabels = true }) },
            new ExportProfile { Name = "CSV je Position", TargetSchemaId = schema.Id, Description = "Eine Zeile je Rechnungsposition.", OptionsJson = CoreJson.Serialize(new ExportOptions { Format = ExportFormat.Csv, CsvLayout = CsvLayout.ItemPerRow, RowGroup = "lines", Groups = ["invoice", "seller", "lines"] }) },
            new ExportProfile { Name = "XML", TargetSchemaId = schema.Id, Description = "Alle Gruppen als XML.", OptionsJson = CoreJson.Serialize(new ExportOptions { Format = ExportFormat.Xml }) });
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------------------------------------------------ schemas

    public static SchemaDefinition Definition(TargetSchemaVersion v) => CoreJson.Deserialize<SchemaDefinition>(v.DefinitionJson);

    public async Task<TargetSchema> DefaultSchemaAsync(CancellationToken ct = default)
        => await db.TargetSchema.OrderByDescending(s => s.IsDefault).ThenBy(s => s.Id).FirstOrDefaultAsync(ct)
           ?? throw new DefinitionException("Es gibt kein Zielschema. Lege eines an.");

    public Task<TargetSchemaVersion?> ActiveSchemaVersionAsync(long schemaId, CancellationToken ct = default)
        => db.TargetSchemaVersion.Where(v => v.TargetSchemaId == schemaId && v.State == VersionState.Active).OrderByDescending(v => v.Number).FirstOrDefaultAsync(ct);

    public Task<TargetSchemaVersion?> SchemaDraftAsync(long schemaId, CancellationToken ct = default)
        => db.TargetSchemaVersion.FirstOrDefaultAsync(v => v.TargetSchemaId == schemaId && v.State == VersionState.Draft, ct);

    /// <summary>The draft of a schema; made from the active version on the first change.</summary>
    public async Task<TargetSchemaVersion> EnsureSchemaDraftAsync(long schemaId, CancellationToken ct = default)
    {
        if (await SchemaDraftAsync(schemaId, ct) is { } draft)
        {
            return draft;
        }

        var active = await ActiveSchemaVersionAsync(schemaId, ct);
        var number = await db.TargetSchemaVersion.Where(v => v.TargetSchemaId == schemaId).MaxAsync(v => (int?)v.Number, ct) ?? 0;
        draft = new TargetSchemaVersion
        {
            TargetSchemaId = schemaId,
            Number = number + 1,
            State = VersionState.Draft,
            DefinitionJson = active?.DefinitionJson ?? CoreJson.Serialize(new SchemaDefinition { Groups = [new SchemaGroup { Key = "invoice", Label = "Rechnung" }] }),
        };
        db.TargetSchemaVersion.Add(draft);
        await db.SaveChangesAsync(ct);
        return draft;
    }

    public async Task SaveSchemaDraftAsync(TargetSchemaVersion draft, SchemaDefinition definition, CancellationToken ct = default)
    {
        if (draft.State != VersionState.Draft)
        {
            throw new DefinitionException("Nur ein Entwurf kann geändert werden.");
        }

        draft.DefinitionJson = CoreJson.Serialize(definition);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Puts a draft in force. The problems of the definition stop it; the documents keep their old version until they are normalized again.</summary>
    public async Task ActivateSchemaAsync(long versionId, string? note, CancellationToken ct = default)
    {
        var draft = await db.TargetSchemaVersion.FirstOrDefaultAsync(v => v.Id == versionId && v.State == VersionState.Draft, ct)
                    ?? throw new DefinitionException("Es gibt keinen Entwurf zum Aktivieren.");
        var problems = Definition(draft).Problems();
        if (problems.Count > 0)
        {
            throw new DefinitionException(string.Join(" ", problems));
        }

        await db.TargetSchemaVersion.Where(v => v.TargetSchemaId == draft.TargetSchemaId && v.State == VersionState.Active)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.State, VersionState.Retired), ct);
        draft.State = VersionState.Active;
        draft.ActivatedDate = clock.GetUtcNow().UtcDateTime;
        draft.ActivatedBy = user.Name;
        draft.ChangeNote = string.IsNullOrWhiteSpace(note) ? draft.ChangeNote : note.Trim();
        await db.SaveChangesAsync(ct);
    }

    public async Task DiscardSchemaDraftAsync(long schemaId, CancellationToken ct = default)
        => await db.TargetSchemaVersion.Where(v => v.TargetSchemaId == schemaId && v.State == VersionState.Draft).ExecuteDeleteAsync(ct);

    /// <summary>What changed between two versions of a schema, in words (for the version history).</summary>
    public static List<string> Diff(SchemaDefinition before, SchemaDefinition after)
    {
        var changes = new List<string>();
        foreach (var g in after.Groups.Where(g => before.Group(g.Key) is null))
        {
            changes.Add($"Gruppe „{g.Label}“ hinzugefügt ({g.Fields.Count} Felder).");
        }

        foreach (var g in before.Groups.Where(g => after.Group(g.Key) is null))
        {
            changes.Add($"Gruppe „{g.Label}“ entfernt.");
        }

        foreach (var g in after.Groups)
        {
            if (before.Group(g.Key) is not { } old)
            {
                continue;
            }

            if (old.Label != g.Label)
            {
                changes.Add($"Gruppe „{old.Label}“ umbenannt in „{g.Label}“.");
            }

            if (old.Repeating != g.Repeating)
            {
                changes.Add($"Gruppe „{g.Label}“ ist jetzt {(g.Repeating ? "wiederholt" : "einfach")}.");
            }

            foreach (var f in g.Fields)
            {
                var o = old.Fields.FirstOrDefault(x => string.Equals(x.Key, f.Key, StringComparison.OrdinalIgnoreCase));
                if (o is null)
                {
                    changes.Add($"{g.Label}: Feld „{f.Label}“ ({f.Key}) hinzugefügt.");
                    continue;
                }

                if (o.Label != f.Label)
                {
                    changes.Add($"{g.Label}: „{o.Label}“ umbenannt in „{f.Label}“.");
                }

                if (o.Type != f.Type)
                {
                    changes.Add($"{g.Label} · {f.Label}: Datentyp {FieldValues.TypeLabel(o.Type)} → {FieldValues.TypeLabel(f.Type)}.");
                }

                if (o.Required != f.Required)
                {
                    changes.Add($"{g.Label} · {f.Label}: {(f.Required ? "jetzt Pflichtfeld" : "kein Pflichtfeld mehr")}.");
                }

                if (o.DefaultValue != f.DefaultValue)
                {
                    changes.Add($"{g.Label} · {f.Label}: Standardwert „{o.DefaultValue}“ → „{f.DefaultValue}“.");
                }
            }

            foreach (var f in old.Fields.Where(o => !g.Fields.Any(x => string.Equals(x.Key, o.Key, StringComparison.OrdinalIgnoreCase))))
            {
                changes.Add($"{g.Label}: Feld „{f.Label}“ ({f.Key}) entfernt.");
            }

            var oldOrder = old.Fields.Select(f => f.Key).Where(k => g.Fields.Any(x => x.Key == k)).ToList();
            var newOrder = g.Fields.Select(f => f.Key).Where(k => old.Fields.Any(x => x.Key == k)).ToList();
            if (!oldOrder.SequenceEqual(newOrder))
            {
                changes.Add($"{g.Label}: Reihenfolge der Felder geändert.");
            }
        }

        if (!before.Groups.Select(g => g.Key).Where(k => after.Group(k) is not null).SequenceEqual(after.Groups.Select(g => g.Key).Where(k => before.Group(k) is not null)))
        {
            changes.Add("Reihenfolge der Gruppen geändert.");
        }

        return changes;
    }

    // ------------------------------------------------------------------------------------------------------------ mappings

    public static MappingDefinition Definition(MappingSetVersion v) => CoreJson.Deserialize<MappingDefinition>(v.DefinitionJson);

    public Task<MappingSetVersion?> ActiveMappingVersionAsync(long mappingSetId, CancellationToken ct = default)
        => db.MappingSetVersion.Where(v => v.MappingSetId == mappingSetId && v.State == VersionState.Active).OrderByDescending(v => v.Number).FirstOrDefaultAsync(ct);

    public Task<MappingSetVersion?> MappingDraftAsync(long mappingSetId, CancellationToken ct = default)
        => db.MappingSetVersion.FirstOrDefaultAsync(v => v.MappingSetId == mappingSetId && v.State == VersionState.Draft, ct);

    public async Task<MappingSetVersion> EnsureMappingDraftAsync(long mappingSetId, CancellationToken ct = default)
    {
        if (await MappingDraftAsync(mappingSetId, ct) is { } draft)
        {
            return draft;
        }

        var active = await ActiveMappingVersionAsync(mappingSetId, ct);
        var number = await db.MappingSetVersion.Where(v => v.MappingSetId == mappingSetId).MaxAsync(v => (int?)v.Number, ct) ?? 0;
        draft = new MappingSetVersion
        {
            MappingSetId = mappingSetId,
            Number = number + 1,
            State = VersionState.Draft,
            DefinitionJson = active?.DefinitionJson ?? CoreJson.Serialize(new MappingDefinition()),
            SampleInvoiceId = active?.SampleInvoiceId,
        };
        db.MappingSetVersion.Add(draft);
        await db.SaveChangesAsync(ct);
        return draft;
    }

    public async Task SaveMappingDraftAsync(MappingSetVersion draft, MappingDefinition definition, CancellationToken ct = default)
    {
        if (draft.State != VersionState.Draft)
        {
            throw new DefinitionException("Nur ein Entwurf kann geändert werden.");
        }

        draft.DefinitionJson = CoreJson.Serialize(definition);
        await db.SaveChangesAsync(ct);
    }

    public async Task ActivateMappingAsync(long versionId, string? note, CancellationToken ct = default)
    {
        var draft = await db.MappingSetVersion.Include(v => v.MappingSet).FirstOrDefaultAsync(v => v.Id == versionId && v.State == VersionState.Draft, ct)
                    ?? throw new DefinitionException("Es gibt keinen Entwurf zum Aktivieren.");
        var schema = await ActiveSchemaVersionAsync(draft.MappingSet!.TargetSchemaId, ct) ?? throw new DefinitionException("Das Zielschema hat keine aktive Version.");
        var unknown = Definition(draft).Rules.Where(r => Definition(schema).Find(r.Target) is null).Select(r => r.Target).ToList();
        if (unknown.Count > 0)
        {
            throw new DefinitionException($"Regeln zeigen auf Felder, die das aktive Schema nicht hat: {string.Join(", ", unknown)}. Entferne sie oder aktiviere zuerst das Schema.");
        }

        await db.MappingSetVersion.Where(v => v.MappingSetId == draft.MappingSetId && v.State == VersionState.Active)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.State, VersionState.Retired), ct);
        draft.State = VersionState.Active;
        draft.ActivatedDate = clock.GetUtcNow().UtcDateTime;
        draft.ActivatedBy = user.Name;
        draft.ChangeNote = string.IsNullOrWhiteSpace(note) ? draft.ChangeNote : note.Trim();
        await db.SaveChangesAsync(ct);
    }

    public async Task DiscardMappingDraftAsync(long mappingSetId, CancellationToken ct = default)
        => await db.MappingSetVersion.Where(v => v.MappingSetId == mappingSetId && v.State == VersionState.Draft).ExecuteDeleteAsync(ct);

    /// <summary>
    /// The mapping a document is normalized with: of the enabled mappings of the schema with an active version, those whose selector fits the format; the most
    /// specific wins, then the higher priority.
    /// </summary>
    public async Task<(MappingSet Set, MappingSetVersion Version)?> SelectMappingAsync(long schemaId, DetectedFormat format, CancellationToken ct = default)
    {
        var sets = await db.MappingSet.Where(m => m.TargetSchemaId == schemaId && m.IsEnabled).ToListAsync(ct);
        var candidates = sets.Where(m => Fits(m, format)).OrderByDescending(m => m.Specificity).ThenByDescending(m => m.Priority).ThenBy(m => m.Id);
        foreach (var set in candidates)
        {
            if (await ActiveMappingVersionAsync(set.Id, ct) is { } version)
            {
                return (set, version);
            }
        }

        return null;
    }

    public static bool Fits(MappingSet m, DetectedFormat f)
    {
        var syntax = f.IsStructured ? f.Syntax.ToString() : "None";
        return (m.Syntax is null || string.Equals(m.Syntax, syntax, StringComparison.OrdinalIgnoreCase))
               && (m.Standard is null || (f.Standard ?? "").Contains(m.Standard, StringComparison.OrdinalIgnoreCase))
               && (m.Profile is null || (f.Profile ?? "").Contains(m.Profile, StringComparison.OrdinalIgnoreCase))
               && (m.FormatVersion is null || string.Equals(f.Version, m.FormatVersion, StringComparison.OrdinalIgnoreCase));
    }

    public static string SelectorText(MappingSet m)
    {
        var parts = new List<string>();
        if (m.Syntax is not null)
        {
            parts.Add(m.Syntax switch { "Ubl" => "UBL", "Cii" => "CII", "None" => "PDF-Text und Scans", _ => m.Syntax });
        }

        if (m.Standard is not null)
        {
            parts.Add(m.Standard);
        }

        if (m.Profile is not null)
        {
            parts.Add("Profil " + m.Profile);
        }

        if (m.FormatVersion is not null)
        {
            parts.Add("Version " + m.FormatVersion);
        }

        return parts.Count == 0 ? "Alle Formate" : string.Join(" · ", parts);
    }

    /// <summary>What changed between two versions of a mapping, in words.</summary>
    public static List<string> Diff(MappingDefinition before, MappingDefinition after)
    {
        var changes = new List<string>();
        string Describe(MappingRule r) => string.Join(", ", r.Sources) + (r.Transforms.Count > 0 ? " | " + string.Join(", ", r.Transforms.Select(t => t.Op)) : "")
                                          + (r.Condition is null ? "" : $" | wenn {r.Condition.Source} {r.Condition.Operator} {r.Condition.Value}") + (r.DefaultValue is null ? "" : $" | Standard „{r.DefaultValue}“");
        var oldRules = before.Rules.GroupBy(r => r.Target, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(Describe).ToList(), StringComparer.OrdinalIgnoreCase);
        var newRules = after.Rules.GroupBy(r => r.Target, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(Describe).ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var (target, rules) in newRules)
        {
            if (!oldRules.TryGetValue(target, out var old))
            {
                changes.Add($"{target}: neue Regel ({string.Join(" / ", rules)}).");
            }
            else if (!old.SequenceEqual(rules))
            {
                changes.Add($"{target}: {string.Join(" / ", old)} → {string.Join(" / ", rules)}.");
            }
        }

        changes.AddRange(oldRules.Keys.Where(k => !newRules.ContainsKey(k)).Select(k => $"{k}: Regel entfernt."));
        foreach (var c in after.Collections.Where(c => before.CollectionOf(c.Group) != c.Source))
        {
            changes.Add($"Liste der Gruppe {c.Group}: {before.CollectionOf(c.Group) ?? "keine"} → {c.Source}.");
        }

        return changes;
    }
}
