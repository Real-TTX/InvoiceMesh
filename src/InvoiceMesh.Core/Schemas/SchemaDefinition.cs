using System.Text.RegularExpressions;

namespace InvoiceMesh.Core.Schemas;

/// <summary>The data types a field of a user's schema can have.</summary>
public enum FieldType
{
    Text,
    Integer,
    Decimal,
    Money,
    Percent,
    Date,
    Boolean,
    Code,
    Email,
}

/// <summary>A field (a column) of a user's schema.</summary>
public sealed class SchemaField
{
    /// <summary>The technical name: the key in JSON, the column in CSV, the element in XML. Letters, digits and underscores, starting with a letter.</summary>
    public string Key { get; set; } = "";

    public string Label { get; set; } = "";

    public FieldType Type { get; set; } = FieldType.Text;

    public bool Required { get; set; }

    /// <summary>The value when the mapping finds none. It is written as a default, never as a found value.</summary>
    public string? DefaultValue { get; set; }

    public string? Description { get; set; }

    /// <summary>For a code: the values it may take (empty: any).</summary>
    public List<string> AllowedValues { get; set; } = [];
}

/// <summary>
/// A group of fields: the invoice, a party, the totals; or a repeating group (<see cref="Repeating"/>) such as the lines or the VAT breakdown, which
/// becomes a list of objects in JSON, repeated elements in XML and, in CSV, one row per item when the export says so.
/// </summary>
public sealed class SchemaGroup
{
    public string Key { get; set; } = "";

    public string Label { get; set; } = "";

    public string? Description { get; set; }

    public bool Repeating { get; set; }

    public List<SchemaField> Fields { get; set; } = [];
}

/// <summary>
/// The target schema a user defines: groups of fields in an order, with types, required fields and defaults. It is versioned outside of this class: a
/// version holds one definition and never changes once it is active.
/// </summary>
public sealed partial class SchemaDefinition
{
    public List<SchemaGroup> Groups { get; set; } = [];

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex KeyRegex();

    public static bool IsValidKey(string? key) => key is not null && KeyRegex().IsMatch(key);

    /// <summary>The address of a field in mappings and overrides: "invoice.number".</summary>
    public static string Target(string group, string field) => $"{group}.{field}";

    public SchemaGroup? Group(string key) => Groups.FirstOrDefault(g => string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase));

    public (SchemaGroup Group, SchemaField Field)? Find(string target)
    {
        var dot = target.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0)
        {
            return null;
        }

        var group = Group(target[..dot]);
        var field = group?.Fields.FirstOrDefault(f => string.Equals(f.Key, target[(dot + 1)..], StringComparison.OrdinalIgnoreCase));
        return group is null || field is null ? null : (group, field);
    }

    public IEnumerable<(SchemaGroup Group, SchemaField Field)> AllFields() => Groups.SelectMany(g => g.Fields.Select(f => (g, f)));

    /// <summary>What is wrong with a definition, in words; empty when it can be saved.</summary>
    public List<string> Problems()
    {
        var problems = new List<string>();
        if (Groups.Count == 0)
        {
            problems.Add("Das Schema hat keine Feldgruppe.");
        }

        foreach (var duplicate in Groups.GroupBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            problems.Add($"Der Schlüssel der Gruppe „{duplicate.Key}“ kommt mehrfach vor.");
        }

        foreach (var group in Groups)
        {
            if (!IsValidKey(group.Key))
            {
                problems.Add($"Der Schlüssel „{group.Key}“ der Gruppe „{group.Label}“ ist ungültig (Buchstaben, Ziffern, Unterstrich; zuerst ein Buchstabe).");
            }

            foreach (var duplicate in group.Fields.GroupBy(f => f.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                problems.Add($"In der Gruppe „{group.Label}“ kommt der Feldschlüssel „{duplicate.Key}“ mehrfach vor.");
            }

            foreach (var field in group.Fields.Where(f => !IsValidKey(f.Key)))
            {
                problems.Add($"Der Schlüssel „{field.Key}“ des Felds „{field.Label}“ ist ungültig.");
            }

            foreach (var field in group.Fields.Where(f => f.DefaultValue is { Length: > 0 }))
            {
                if (!FieldValues.TryConvert(field, field.DefaultValue!, out _, out var message))
                {
                    problems.Add($"Der Standardwert von „{field.Label}“ passt nicht zum Datentyp: {message}");
                }
            }
        }

        return problems;
    }

    public SchemaDefinition Clone() => Core.CoreJson.Deserialize<SchemaDefinition>(Core.CoreJson.Serialize(this));
}
