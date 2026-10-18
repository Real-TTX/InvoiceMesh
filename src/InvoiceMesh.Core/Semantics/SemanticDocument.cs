using System.Globalization;

namespace InvoiceMesh.Core.Semantics;

/// <summary>
/// How sure the reader is of a value. Values of structured data (UBL, CII) are <see cref="High"/>; values read from the text of a PDF or a scan are
/// <see cref="Medium"/> when a label stood right before them and <see cref="Low"/> when they were guessed from their position or shape.
/// Anything below <see cref="High"/> is shown as "unsicher" and asks for a look.
/// </summary>
public enum Confidence
{
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <summary>One value of the semantic model, always as text in a fixed form: dates <c>yyyy-MM-dd</c>, numbers invariant (<c>1234.5</c>).</summary>
/// <param name="Source">Where it came from: an XML path, "Text Zeile 12", "Korrektur".</param>
public sealed record SemanticValue(string Value, Confidence Confidence = Confidence.High, string? Source = null);

/// <summary>One item of a repeating business group: a line (BG-25), a row of the VAT breakdown (BG-23), an allowance (BG-20) or a charge (BG-21).</summary>
public sealed class SemanticItem
{
    public Dictionary<string, SemanticValue> Fields { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The XML path of the element the item was read from ("/Invoice/cac:InvoiceLine[2]"), for the provenance of its values.</summary>
    public string? Origin { get; init; }

    public string? Get(string id) => Fields.TryGetValue(id, out var v) ? v.Value : null;

    public decimal? Decimal(string id) => SemanticDocument.ParseDecimal(Get(id));

    public void Set(string id, string? value, Confidence confidence = Confidence.High, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Fields.Remove(id);
            return;
        }

        Fields[id] = new SemanticValue(value.Trim(), confidence, source);
    }

    public SemanticItem Clone() => new() { Fields = new Dictionary<string, SemanticValue>(Fields, StringComparer.OrdinalIgnoreCase), Origin = Origin };
}

/// <summary>
/// The invoice in the semantic model of EN 16931, independent of its syntax: business terms (<c>BT-1</c> ... <c>BT-165</c>) of the document and items of
/// the repeating groups (<c>BG-25</c> lines, <c>BG-23</c> VAT breakdown, <c>BG-20</c> allowances, <c>BG-21</c> charges). Every reader writes it, the rules check it,
/// the XRechnung writers read it. The user's schema is a different thing: it is filled from this model (and from the raw fields) by a mapping.
/// </summary>
public sealed class SemanticDocument
{
    public Dictionary<string, SemanticValue> Fields { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<SemanticItem>> Groups { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string id) => Fields.TryGetValue(id, out var v) ? v.Value : null;

    public SemanticValue? Value(string id) => Fields.GetValueOrDefault(id);

    public bool Has(string id) => Fields.ContainsKey(id);

    public decimal? Decimal(string id) => ParseDecimal(Get(id));

    public DateOnly? Date(string id) => ParseDate(Get(id));

    public IReadOnlyList<SemanticItem> Items(string groupId) => Groups.TryGetValue(groupId, out var list) ? list : [];

    public List<SemanticItem> EnsureGroup(string groupId)
    {
        if (!Groups.TryGetValue(groupId, out var list))
        {
            list = [];
            Groups[groupId] = list;
        }

        return list;
    }

    public void Set(string id, string? value, Confidence confidence = Confidence.High, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Fields.Remove(id);
            return;
        }

        Fields[id] = new SemanticValue(value.Trim(), confidence, source);
    }

    /// <summary>All values below <see cref="Confidence.High"/>, with the key they are under ("BT-1", "BG-25[2]/BT-129").</summary>
    public IEnumerable<(string Key, SemanticValue Value)> Uncertain()
    {
        foreach (var (k, v) in Fields.Where(f => f.Value.Confidence < Confidence.High))
        {
            yield return (k, v);
        }

        foreach (var (group, items) in Groups)
        {
            for (var i = 0; i < items.Count; i++)
            {
                foreach (var (k, v) in items[i].Fields.Where(f => f.Value.Confidence < Confidence.High))
                {
                    yield return (ItemKey(group, i, k), v);
                }
            }
        }
    }

    /// <summary>The key of a value inside an item: "BG-25[2]/BT-129" (the index counts from 1, like XPath).</summary>
    public static string ItemKey(string group, int index, string field) => $"{group}[{index + 1}]/{field}";

    /// <summary>Reads a key of <see cref="ItemKey"/>; false for a key of the document level.</summary>
    public static bool TryParseItemKey(string key, out string group, out int index, out string field)
    {
        group = field = string.Empty;
        index = -1;
        var open = key.IndexOf('[', StringComparison.Ordinal);
        var close = key.IndexOf("]/", StringComparison.Ordinal);
        if (open <= 0 || close < open || !int.TryParse(key[(open + 1)..close], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
        {
            return false;
        }

        group = key[..open];
        index = n - 1;
        field = key[(close + 2)..];
        return field.Length > 0;
    }

    /// <summary>The value under a key of the document level or of an item.</summary>
    public SemanticValue? Lookup(string key)
    {
        if (TryParseItemKey(key, out var group, out var index, out var field))
        {
            var items = Items(group);
            return index < items.Count ? items[index].Fields.GetValueOrDefault(field) : null;
        }

        return Value(key);
    }

    /// <summary>Sets a value under a key of the document level or of an item; an item that is not there yet is added (a correction may add a line).</summary>
    public void Put(string key, string? value, Confidence confidence, string? source)
    {
        if (TryParseItemKey(key, out var group, out var index, out var field))
        {
            var items = EnsureGroup(group);
            while (items.Count <= index)
            {
                items.Add(new SemanticItem());
            }

            items[index].Set(field, value, confidence, source);
            return;
        }

        Set(key, value, confidence, source);
    }

    public SemanticDocument Clone() => new()
    {
        Fields = new Dictionary<string, SemanticValue>(Fields, StringComparer.OrdinalIgnoreCase),
        Groups = Groups.ToDictionary(g => g.Key, g => g.Value.Select(i => i.Clone()).ToList(), StringComparer.OrdinalIgnoreCase),
    };

    public static decimal? ParseDecimal(string? text)
        => decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var d) ? d : null;

    public static DateOnly? ParseDate(string? text)
        => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public static string Format(decimal value) => value.ToString("0.############", CultureInfo.InvariantCulture);

    public static string Format(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
