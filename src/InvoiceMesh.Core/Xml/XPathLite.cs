using System.Xml.Linq;

namespace InvoiceMesh.Core.Xml;

/// <summary>
/// The small path language of the bindings: steps <c>cac:Party/cbc:Name</c> with the prefixes of <see cref="XmlNames"/>, a last step <c>@unitCode</c> for an
/// attribute, a condition per step (<c>cac:PartyTaxScheme[cac:TaxScheme/cbc:ID='VAT']</c>, <c>[@schemeID!='VA']</c>) and alternatives separated by <c>|</c>
/// (the first that finds something wins). Enough for the bindings of EN 16931, and much easier to read than XPath with namespace managers.
/// </summary>
public static class XPathLite
{
    private sealed record Step(XName? Name, string? Attribute, string? ConditionPath, string? ConditionValue, bool Negated);

    /// <summary>The elements a path finds below <paramref name="context"/> (the first alternative that finds any).</summary>
    public static IReadOnlyList<XElement> Elements(XElement context, string path)
    {
        foreach (var alternative in Alternatives(path))
        {
            var steps = Parse(alternative);
            if (steps.Count == 0 || steps[^1].Attribute is not null)
            {
                continue;
            }

            var found = Walk(context, steps).ToList();
            if (found.Count > 0)
            {
                return found;
            }
        }

        return [];
    }

    /// <summary>The trimmed, non-empty texts a path finds: of elements, or of an attribute when the last step is one.</summary>
    public static IReadOnlyList<string> Values(XElement context, string path)
    {
        foreach (var alternative in Alternatives(path))
        {
            var steps = Parse(alternative);
            if (steps.Count == 0)
            {
                continue;
            }

            var found = new List<string>();
            if (steps[^1].Attribute is { } attribute)
            {
                IEnumerable<XElement> owners = steps.Count == 1 ? [context] : Walk(context, steps.Take(steps.Count - 1).ToList());
                found.AddRange(owners.Select(e => e.Attribute(attribute)?.Value.Trim()).Where(v => !string.IsNullOrEmpty(v))!);
            }
            else
            {
                found.AddRange(Walk(context, steps).Where(e => !e.HasElements).Select(e => e.Value.Trim()).Where(v => v.Length > 0));
            }

            if (found.Count > 0)
            {
                return found;
            }
        }

        return [];
    }

    public static string? Value(XElement context, string path) => Values(context, path).FirstOrDefault();

    private static IEnumerable<string> Alternatives(string path) => path.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<XElement> Walk(XElement context, IReadOnlyList<Step> steps)
    {
        IEnumerable<XElement> current = [context];
        foreach (var step in steps)
        {
            if (step.Name is null)
            {
                break;
            }

            current = current.SelectMany(e => e.Elements(step.Name)).Where(e => Matches(e, step)).ToList();
        }

        return current;
    }

    private static bool Matches(XElement element, Step step)
    {
        if (step.ConditionPath is null)
        {
            return true;
        }

        // [cac:TaxSubtotal]: the condition is that the child is there.
        if (step.ConditionValue is null)
        {
            return Elements(element, step.ConditionPath).Count > 0 || Values(element, step.ConditionPath).Count > 0;
        }

        var values = Values(element, step.ConditionPath);
        var hit = values.Any(v => string.Equals(v, step.ConditionValue, StringComparison.OrdinalIgnoreCase));
        return step.Negated ? !hit : hit;
    }

    private static List<Step> Parse(string path)
    {
        var steps = new List<Step>();
        foreach (var raw in SplitSteps(path))
        {
            var text = raw.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (text.StartsWith('@'))
            {
                steps.Add(new Step(null, text[1..], null, null, false));
                continue;
            }

            string? conditionPath = null, conditionValue = null;
            var negated = false;
            var bracket = text.IndexOf('[', StringComparison.Ordinal);
            if (bracket > 0 && text.EndsWith(']'))
            {
                var condition = text[(bracket + 1)..^1];
                text = text[..bracket];
                var op = condition.IndexOf("!=", StringComparison.Ordinal);
                negated = op > 0;
                if (op < 0)
                {
                    op = condition.IndexOf('=', StringComparison.Ordinal);
                }

                if (op > 0)
                {
                    conditionPath = condition[..op].Trim();
                    conditionValue = condition[(op + (negated ? 2 : 1))..].Trim().Trim('\'', '"');
                }
                else
                {
                    conditionPath = condition.Trim();
                }
            }

            steps.Add(new Step(Name(text), null, conditionPath, conditionValue, negated));
        }

        return steps;
    }

    /// <summary>Splits at '/' outside of a condition.</summary>
    private static IEnumerable<string> SplitSteps(string path)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < path.Length; i++)
        {
            switch (path[i])
            {
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    break;
                case '/' when depth == 0:
                    yield return path[start..i];
                    start = i + 1;
                    break;
            }
        }

        yield return path[start..];
    }

    private static XName Name(string qualified)
    {
        var colon = qualified.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return XName.Get(qualified);
        }

        var prefix = qualified[..colon];
        var local = qualified[(colon + 1)..];
        return XmlNames.PrefixToNamespace.TryGetValue(prefix, out var ns) ? XName.Get(local, ns) : XName.Get(local);
    }
}
