using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InvoiceMesh.Core.Extraction;
using InvoiceMesh.Core.Schemas;
using InvoiceMesh.Core.Semantics;

namespace InvoiceMesh.Core.Mapping;

/// <summary>What the mapping has to work with: the semantic model (after corrections), the raw fields and what the processing knows about the file.</summary>
public sealed class MappingInput
{
    public SemanticDocument Semantic { get; init; } = new();

    public IReadOnlyList<SourceField> Raw { get; init; } = [];

    /// <summary>fileName, kind, syntax, standard, profile, version, method, receivedDate ...</summary>
    public IReadOnlyDictionary<string, string> Meta { get; init; } = new Dictionary<string, string>();
}

/// <summary>The state of one field after the mapping.</summary>
public enum FieldStatus
{
    /// <summary>A source had a value.</summary>
    Mapped,

    /// <summary>No source had one: the default of the rule or of the schema was used.</summary>
    Default,

    /// <summary>A person set the value by hand.</summary>
    Manual,

    /// <summary>The sources disagree; the value of the first was used.</summary>
    Conflict,

    /// <summary>The value does not fit the type of the field (or a transformation failed).</summary>
    Invalid,

    /// <summary>A rule exists but no source had a value, and there is no default.</summary>
    Missing,

    /// <summary>No rule fills this field.</summary>
    NotMapped,
}

/// <summary>One candidate source of a field: what it had and whether it was the one used.</summary>
public sealed record SourceCandidate(string Source, string? Value, bool Used);

/// <summary>The outcome of one field (of one item in a repeating group): the value, where it came from, what was wrong.</summary>
public sealed record FieldOutcome(
    string Target,
    string GroupLabel,
    string FieldLabel,
    int? Item,
    string? Value,
    FieldStatus Status,
    bool Required,
    string? Source,
    string? Message,
    IReadOnlyList<SourceCandidate> Candidates,
    IReadOnlyList<string> Applied)
{
    /// <summary>"invoice.number" or "lines[2].quantity" (1-based like the pages).</summary>
    public string Address => Item is { } i ? Target.Replace(".", $"[{i}].", StringComparison.Ordinal) : Target;
}

/// <summary>A source value no rule used: shown so that nothing gets lost unnoticed.</summary>
public sealed record UnusedSource(string Reference, string Label, string Value);

/// <summary>What the mapping did, for the preview, the comparison and the validation.</summary>
public sealed class MappingReport
{
    public List<FieldOutcome> Fields { get; } = [];

    public List<UnusedSource> Unused { get; } = [];

    /// <summary>Problems of the definition itself: rules for fields the schema does not have, unknown operations.</summary>
    public List<string> DefinitionProblems { get; } = [];

    public int Count(FieldStatus status) => Fields.Count(f => f.Status == status);

    public IEnumerable<FieldOutcome> MissingRequired => Fields.Where(f => f.Required && f.Value is null);
}

public sealed record NormalizationResult(JsonObject Data, MappingReport Report);

/// <summary>
/// Fills a user's schema from the sources by a mapping: for each field the first rule whose condition holds, its sources in order, its transformations,
/// the type of the field, the default. Every step is written down (<see cref="FieldOutcome"/>), so that the result can be traced back to the source.
/// The same code runs for the preview of a draft and for the processing: what the preview shows is what the processing will do.
/// </summary>
public static class MappingEngine
{
    /// <summary>The context a source reference is resolved in: the document, or one item of a collection.</summary>
    private sealed record Context(SemanticItem? Item, string? RawPrefix, int? Index);

    public static NormalizationResult Run(SchemaDefinition schema, MappingDefinition mapping, MappingInput input, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var report = new MappingReport();
        var data = new JsonObject();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CheckDefinition(schema, mapping, report);

        foreach (var group in schema.Groups)
        {
            if (!group.Repeating)
            {
                var obj = new JsonObject();
                foreach (var field in group.Fields)
                {
                    obj[field.Key] = MapField(group, field, mapping, input, new Context(null, null, null), overrides, report, used);
                }

                data[group.Key] = obj;
                continue;
            }

            var array = new JsonArray();
            var items = Items(mapping.CollectionOf(group.Key), input, report, group);
            var count = Math.Max(items.Count, OverrideCount(group, overrides));
            for (var i = 0; i < count; i++)
            {
                var context = i < items.Count ? items[i] : new Context(new SemanticItem(), null, i + 1);
                var obj = new JsonObject();
                foreach (var field in group.Fields)
                {
                    obj[field.Key] = MapField(group, field, mapping, input, context, overrides, report, used);
                }

                array.Add(obj);
            }

            data[group.Key] = array;
        }

        CollectUnused(input, used, report);
        return new NormalizationResult(data, report);
    }

    private static void CheckDefinition(SchemaDefinition schema, MappingDefinition mapping, MappingReport report)
    {
        foreach (var rule in mapping.Rules.Where(r => schema.Find(r.Target) is null))
        {
            report.DefinitionProblems.Add($"Die Regel für „{rule.Target}“ zeigt auf ein Feld, das es im Schema nicht gibt.");
        }

        foreach (var step in mapping.Rules.SelectMany(r => r.Transforms.Select(t => (r.Target, t.Op))).Where(x => Transforms.Info(x.Op) is null))
        {
            report.DefinitionProblems.Add($"Die Regel für „{step.Target}“ nutzt die unbekannte Transformation „{step.Op}“.");
        }

        foreach (var binding in mapping.Collections.Where(c => schema.Group(c.Group) is not { Repeating: true }))
        {
            report.DefinitionProblems.Add($"„{binding.Group}“ ist keine wiederholte Gruppe des Schemas; die Zuordnung der Liste „{binding.Source}“ wirkt nicht.");
        }

        foreach (var duplicate in mapping.Rules.Where(r => r.Condition is null).GroupBy(r => r.Target, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            report.DefinitionProblems.Add($"Für „{duplicate.Key}“ gibt es {duplicate.Count()} Regeln ohne Bedingung; es gilt nur die erste.");
        }
    }

    private static int OverrideCount(SchemaGroup group, IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null)
        {
            return 0;
        }

        var max = 0;
        var regex = new Regex($@"^{Regex.Escape(group.Key)}\[(\d+)\]\.", RegexOptions.IgnoreCase);
        foreach (var key in overrides.Keys)
        {
            var m = regex.Match(key);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
            {
                max = Math.Max(max, n);
            }
        }

        return max;
    }

    private static List<Context> Items(string? source, MappingInput input, MappingReport report, SchemaGroup group)
    {
        if (source is null)
        {
            return [];
        }

        if (source.StartsWith("bg:", StringComparison.OrdinalIgnoreCase))
        {
            return [.. input.Semantic.Items(source[3..]).Select((item, i) => new Context(item, item.Origin, i + 1))];
        }

        if (source.StartsWith("xml:", StringComparison.OrdinalIgnoreCase))
        {
            var pattern = source[4..].TrimEnd('/');
            var depth = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
            var prefixes = new List<string>();
            foreach (var field in input.Raw)
            {
                if (!field.Pattern.StartsWith(pattern + "/", StringComparison.Ordinal))
                {
                    continue;
                }

                var steps = field.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var prefix = "/" + string.Join("/", steps.Take(depth));
                if (!prefixes.Contains(prefix))
                {
                    prefixes.Add(prefix);
                }
            }

            // An item of a raw list has no business terms of its own: bt: finds nothing in it rather than the terms of the document.
            return [.. prefixes.Select((p, i) => new Context(new SemanticItem(), p, i + 1))];
        }

        report.DefinitionProblems.Add($"Die Liste „{source}“ für die Gruppe „{group.Label}“ ist keine gültige Quelle (bg:BG-25 oder xml:/Pfad).");
        return [];
    }

    private static JsonNode? MapField(SchemaGroup group, SchemaField field, MappingDefinition mapping, MappingInput input, Context context,
        IReadOnlyDictionary<string, string>? overrides, MappingReport report, HashSet<string> used)
    {
        var target = SchemaDefinition.Target(group.Key, field.Key);
        var candidates = new List<SourceCandidate>();
        var applied = new List<string>();

        // A value set by hand wins over everything; the rule still shows what the sources had.
        var overrideKey = context.Index is { } idx ? $"{group.Key}[{idx}].{field.Key}" : target;
        string? manual = null;
        if (overrides is not null && (overrides.TryGetValue(overrideKey, out var m1) || overrides.TryGetValue(overrideKey.ToLowerInvariant(), out m1)))
        {
            manual = m1;
        }

        var rule = mapping.RulesFor(target).FirstOrDefault(r => r.Condition is null || Holds(r.Condition, input, context, used));
        string? value = null;
        string? source = null;
        string? message = null;
        var status = FieldStatus.NotMapped;

        if (rule is not null)
        {
            var values = new List<(string Source, string Value)>();
            foreach (var reference in rule.Sources)
            {
                var v = Resolve(reference, input, context, used);
                candidates.Add(new SourceCandidate(reference, v, false));
                if (!string.IsNullOrWhiteSpace(v))
                {
                    values.Add((reference, v));
                }
            }

            if (values.Count > 0)
            {
                if (rule.Combine == CombineMode.Concat)
                {
                    value = string.Join(rule.Separator ?? " ", values.Select(v => v.Value.Trim()));
                    source = string.Join(" + ", values.Select(v => v.Source));
                    MarkUsed(candidates, values.Select(v => v.Source));
                }
                else
                {
                    value = values[0].Value;
                    source = values[0].Source;
                    MarkUsed(candidates, [values[0].Source]);
                }

                status = FieldStatus.Mapped;
                if (rule.Combine == CombineMode.First && !rule.IgnoreDifferences)
                {
                    var distinct = values.Select(v => Normalize(v.Value)).Distinct().ToList();
                    if (distinct.Count > 1)
                    {
                        status = FieldStatus.Conflict;
                        message = $"Die Quellen sind sich nicht einig: {string.Join(" / ", values.Select(v => $"{v.Source} = „{v.Value}“"))}. Verwendet wurde die erste.";
                    }
                }
            }

            foreach (var step in rule.Transforms)
            {
                var before = value;
                value = Transforms.Apply(value, step, out var error);
                if (error is not null)
                {
                    status = FieldStatus.Invalid;
                    message = $"Transformation „{Transforms.Info(step.Op)?.Label ?? step.Op}“: {error}";
                }
                else if (!string.Equals(before, value, StringComparison.Ordinal))
                {
                    applied.Add(Transforms.Info(step.Op)?.Label ?? step.Op);
                }
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                value = null;
                if (rule.DefaultValue is { Length: > 0 } ruleDefault)
                {
                    value = ruleDefault;
                    status = FieldStatus.Default;
                    source = "Standardwert der Regel";
                }
                else if (status != FieldStatus.Invalid)
                {
                    status = FieldStatus.Missing;
                }
            }
        }

        if (value is null && field.DefaultValue is { Length: > 0 } schemaDefault)
        {
            value = schemaDefault;
            status = FieldStatus.Default;
            source = "Standardwert des Schemas";
        }

        if (manual is not null)
        {
            value = string.IsNullOrWhiteSpace(manual) ? null : manual;
            status = FieldStatus.Manual;
            source = "Manuelle Korrektur";
            message = null;
        }

        JsonNode? node = null;
        if (value is not null)
        {
            if (FieldValues.TryConvert(field, value, out var typed, out var typeError))
            {
                node = typed;
                value = FieldValues.AsText(typed);
            }
            else
            {
                status = FieldStatus.Invalid;
                message = typeError;
            }
        }

        if (value is null && field.Required && status is FieldStatus.NotMapped or FieldStatus.Missing)
        {
            message ??= status == FieldStatus.NotMapped ? "Pflichtfeld ohne Regel im Mapping." : "Pflichtfeld: keine Quelle hatte einen Wert.";
        }

        report.Fields.Add(new FieldOutcome(target, group.Label, field.Label, context.Index, value, status, field.Required, source, message, candidates, applied));
        return node;
    }

    private static void MarkUsed(List<SourceCandidate> candidates, IEnumerable<string> sources)
    {
        var set = sources.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < candidates.Count; i++)
        {
            if (set.Contains(candidates[i].Source))
            {
                candidates[i] = candidates[i] with { Used = true };
            }
        }
    }

    private static string Normalize(string value) => Regex.Replace(value.Trim(), @"\s+", " ").ToUpperInvariant();

    private static bool Holds(RuleCondition condition, MappingInput input, Context context, HashSet<string> used)
    {
        var value = Resolve(condition.Source, input, context, used, countAsUsed: false) ?? string.Empty;
        var expected = condition.Value ?? string.Empty;
        return condition.Operator switch
        {
            ConditionOperator.Equals => string.Equals(value.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase),
            ConditionOperator.NotEquals => !string.Equals(value.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase),
            ConditionOperator.Contains => value.Contains(expected, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.StartsWith => value.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
            ConditionOperator.Matches => SafeMatch(value, expected),
            ConditionOperator.IsEmpty => string.IsNullOrWhiteSpace(value),
            _ => !string.IsNullOrWhiteSpace(value),
        };
    }

    private static bool SafeMatch(string value, string pattern)
    {
        try
        {
            return Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>The value of a source reference in a context (see <see cref="MappingRule"/> for the forms).</summary>
    private static string? Resolve(string reference, MappingInput input, Context context, HashSet<string> used, bool countAsUsed = true)
    {
        var colon = reference.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return null;
        }

        var kind = reference[..colon].ToLowerInvariant();
        var key = reference[(colon + 1)..];
        string? value;
        switch (kind)
        {
            case "bt":
                value = context.Item is not null ? context.Item.Get(key) : input.Semantic.Get(key);
                if (countAsUsed)
                {
                    used.Add(context.Item is not null ? "item:" + key : "bt:" + key);
                }

                return value;

            case "head":
                if (countAsUsed)
                {
                    used.Add("bt:" + key);
                }

                return input.Semantic.Get(key);

            case "xml":
                return RawValue(key, input, context);

            case "text":
                return input.Raw.FirstOrDefault(f => string.Equals(f.Path, reference, StringComparison.OrdinalIgnoreCase))?.Value;

            case "meta":
                if (string.Equals(key, "index", StringComparison.OrdinalIgnoreCase))
                {
                    return context.Index?.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                return input.Meta.TryGetValue(key, out var meta) ? meta : null;

            case "const":
                return key;

            default:
                return null;
        }
    }

    private static string? RawValue(string path, MappingInput input, Context context)
    {
        string full;
        if (path.StartsWith('/'))
        {
            full = path;
        }
        else if (context.RawPrefix is not null)
        {
            full = context.RawPrefix + "/" + path;
        }
        else
        {
            return null;
        }

        var exact = input.Raw.FirstOrDefault(f => string.Equals(f.Path, full, StringComparison.Ordinal));
        if (exact is not null)
        {
            return exact.Value;
        }

        // A path without indexes takes the first element it stands for, inside the item when there is one.
        var pattern = SourceField.StripIndexes(full);
        return input.Raw.FirstOrDefault(f => f.Pattern == pattern && (context.RawPrefix is null || f.Path.StartsWith(context.RawPrefix + "/", StringComparison.Ordinal)))?.Value;
    }

    private static void CollectUnused(MappingInput input, HashSet<string> used, MappingReport report)
    {
        foreach (var (id, value) in input.Semantic.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (!used.Contains("bt:" + id))
            {
                report.Unused.Add(new UnusedSource("bt:" + id, SemanticCatalog.LabelOf(id), value.Value));
            }
        }

        foreach (var (group, items) in input.Semantic.Groups)
        {
            var ids = items.SelectMany(i => i.Fields.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal);
            foreach (var id in ids.Where(id => !used.Contains("item:" + id)))
            {
                var sample = items.Select(i => i.Get(id)).FirstOrDefault(v => v is not null) ?? string.Empty;
                report.Unused.Add(new UnusedSource($"{group} · bt:{id}", SemanticCatalog.LabelOf(id), items.Count > 1 ? $"{sample} (… {items.Count} Einträge)" : sample));
            }
        }
    }
}
