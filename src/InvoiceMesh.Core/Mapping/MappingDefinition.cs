namespace InvoiceMesh.Core.Mapping;

/// <summary>How several sources of one rule become one value.</summary>
public enum CombineMode
{
    /// <summary>The first source that has a value (the order is the priority); the others are alternatives.</summary>
    First,

    /// <summary>All sources that have a value, joined with <see cref="MappingRule.Separator"/> (fields merged into one).</summary>
    Concat,
}

public enum ConditionOperator
{
    Equals,
    NotEquals,
    Contains,
    StartsWith,
    Matches,
    IsEmpty,
    IsNotEmpty,
}

/// <summary>A rule applies only when its condition holds, e.g. <c>meta:syntax</c> equals <c>CII</c>.</summary>
public sealed class RuleCondition
{
    public string Source { get; set; } = "";

    public ConditionOperator Operator { get; set; } = ConditionOperator.Equals;

    public string? Value { get; set; }
}

/// <summary>One step of a transformation (see <see cref="Transforms"/> for the operations and their arguments).</summary>
public sealed class TransformStep
{
    public string Op { get; set; } = "";

    public string? Arg { get; set; }

    public string? Arg2 { get; set; }
}

/// <summary>
/// How a field of the target schema gets its value. <see cref="Sources"/> are references in priority order:
/// <list type="bullet">
/// <item><c>bt:BT-1</c> a business term of EN 16931 (in a repeating group: of the item, e.g. <c>bt:BT-153</c>)</item>
/// <item><c>head:BT-5</c> a business term of the document, from inside a repeating group</item>
/// <item><c>xml:/Invoice/cbc:ID</c> a raw XML path (in a repeating group relative to the item: <c>xml:cac:Item/cbc:Name</c>)</item>
/// <item><c>text:Rechnungsnummer</c> a "Label: Wert" line of the text of a PDF or a scan</item>
/// <item><c>meta:syntax</c> what the processing knows (fileName, syntax, standard, profile, version, method, index)</item>
/// <item><c>const:EUR</c> a fixed value</item>
/// </list>
/// Several rules may target the same field: the first whose <see cref="Condition"/> holds is used.
/// </summary>
public sealed class MappingRule
{
    /// <summary>"group.field" of the target schema.</summary>
    public string Target { get; set; } = "";

    public List<string> Sources { get; set; } = [];

    public CombineMode Combine { get; set; } = CombineMode.First;

    public string? Separator { get; set; } = " ";

    public List<TransformStep> Transforms { get; set; } = [];

    public RuleCondition? Condition { get; set; }

    /// <summary>The value when no source has one (marked as a default, never as found).</summary>
    public string? DefaultValue { get; set; }

    /// <summary>Different values of the alternatives are expected (a legal and a trading name): they are not reported as a conflict.</summary>
    public bool IgnoreDifferences { get; set; }

    public string? Note { get; set; }
}

/// <summary>Which collection of the source fills a repeating group of the schema: <c>bg:BG-25</c> (the lines) or a raw path (<c>xml:/Invoice/cac:InvoiceLine</c>).</summary>
public sealed class CollectionBinding
{
    public string Group { get; set; } = "";

    public string Source { get; set; } = "";
}

/// <summary>A mapping: the rules from the sources to one target schema. Versioned outside of this class, like the schema.</summary>
public sealed class MappingDefinition
{
    public List<CollectionBinding> Collections { get; set; } = [];

    public List<MappingRule> Rules { get; set; } = [];

    public IEnumerable<MappingRule> RulesFor(string target) => Rules.Where(r => string.Equals(r.Target, target, StringComparison.OrdinalIgnoreCase));

    public string? CollectionOf(string group) => Collections.FirstOrDefault(c => string.Equals(c.Group, group, StringComparison.OrdinalIgnoreCase))?.Source;

    public MappingDefinition Clone() => CoreJson.Deserialize<MappingDefinition>(CoreJson.Serialize(this));
}
