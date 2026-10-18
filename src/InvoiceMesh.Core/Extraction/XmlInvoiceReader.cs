using System.Globalization;
using System.Xml.Linq;
using InvoiceMesh.Core.Formats;
using InvoiceMesh.Core.Semantics;
using InvoiceMesh.Core.Xml;

namespace InvoiceMesh.Core.Extraction;

/// <summary>
/// Reads structured invoices: tells UBL from CII by the root element and its namespace, finds the standard, profile and version by the specification identifier,
/// writes every leaf of the document as a raw field and every business term of <see cref="SemanticCatalog"/> into the semantic model.
/// </summary>
public static class XmlInvoiceReader
{
    public sealed record Classification(InvoiceSyntax Syntax, string? SyntaxVersion, string? DocumentKind, string? SpecificationId, string? BusinessProcess);

    public static Classification Classify(XElement root)
    {
        var ns = root.Name.NamespaceName;
        var local = root.Name.LocalName;
        if (ns == XmlNames.UblInvoice && local == "Invoice" || ns == XmlNames.UblCreditNote && local == "CreditNote")
        {
            var version = XPathLite.Value(root, "cbc:UBLVersionID") ?? "2.1";
            return new Classification(InvoiceSyntax.Ubl, "UBL " + version, local, XPathLite.Value(root, "cbc:CustomizationID"), XPathLite.Value(root, "cbc:ProfileID"));
        }

        if (ns == XmlNames.Rsm && local == "CrossIndustryInvoice")
        {
            return new Classification(InvoiceSyntax.Cii, "CII D16B", "Invoice",
                XPathLite.Value(root, "rsm:ExchangedDocumentContext/ram:GuidelineSpecifiedDocumentContextParameter/ram:ID"),
                XPathLite.Value(root, "rsm:ExchangedDocumentContext/ram:BusinessProcessSpecifiedDocumentContextParameter/ram:ID"));
        }

        if (local == "CrossIndustryDocument" && ns == XmlNames.ZugferdV1Rsm)
        {
            // ZUGFeRD 1.0 uses other namespaces for the aggregates: found by local names.
            var guideline = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "GuidelineSpecifiedDocumentContextParameter")
                ?.Elements().FirstOrDefault(e => e.Name.LocalName == "ID")?.Value.Trim();
            return new Classification(InvoiceSyntax.CiiZugferdV1, "CII D13B (ZUGFeRD 1.0)", "Invoice", guideline, null);
        }

        return new Classification(InvoiceSyntax.OtherXml, null, local, null, null);
    }

    /// <summary>Applies the specification table to a classified document, and writes down why.</summary>
    public static DetectedFormat Describe(Classification c, SpecificationRegistry registry, DetectedFormat baseFormat, List<string> evidence, bool insidePdf)
    {
        evidence.Add(c.Syntax switch
        {
            InvoiceSyntax.Ubl => $"Wurzelelement {c.DocumentKind} im Namensraum von UBL 2 → Syntax UBL ({c.SyntaxVersion}).",
            InvoiceSyntax.Cii => "Wurzelelement CrossIndustryInvoice im Namensraum von UN/CEFACT CII D16B → Syntax CII.",
            InvoiceSyntax.CiiZugferdV1 => "Wurzelelement CrossIndustryDocument (ZUGFeRD 1.0) → Syntax CII in der Fassung D13B.",
            _ => "Das XML ist keine bekannte Rechnungssyntax (weder UBL noch CII).",
        });

        var spec = registry.Find(c.SpecificationId);
        string? standard = null, profile = null, version = null, note = null;
        var rules = RuleSets.None;
        if (spec is not null)
        {
            standard = spec.Standard;
            profile = spec.Profile;
            version = spec.Version;
            rules = spec.Rules;
            note = spec.Note;
            evidence.Add($"Spezifikationskennung (BT-24) „{c.SpecificationId}“ → {spec.Standard}, Profil {spec.Profile}, Version {spec.Version}{(spec.IsBuiltIn ? "" : " (eigener Eintrag)")}.");
            if (spec.Syntaxes.Count > 0 && !spec.Syntaxes.Contains(c.Syntax))
            {
                evidence.Add($"Achtung: {spec.Standard} {spec.Profile} ist für diese Syntax nicht vorgesehen.");
            }

            // The EN 16931 identifier alone inside a PDF is the profile "EN 16931" (Comfort) of ZUGFeRD / Factur-X.
            if (insidePdf && spec.Identifier == SpecificationRegistry.En16931 && c.Syntax == InvoiceSyntax.Cii)
            {
                standard = "ZUGFeRD / Factur-X";
                version = "2.x / 1.0";
            }
        }
        else if (c.SpecificationId is not null)
        {
            evidence.Add($"Spezifikationskennung „{c.SpecificationId}“ ist nicht bekannt. Sie kann unter Einstellungen > Formate ergänzt werden.");
            standard = "Unbekannt";
            profile = c.SpecificationId;
        }
        else if (c.Syntax is InvoiceSyntax.Ubl or InvoiceSyntax.Cii)
        {
            evidence.Add("Keine Spezifikationskennung (BT-24) im Dokument: Standard und Profil sind offen.");
        }

        if (c.BusinessProcess is not null)
        {
            evidence.Add($"Geschäftsprozess (BT-23): {c.BusinessProcess}.");
        }

        return baseFormat with
        {
            Syntax = c.Syntax,
            SyntaxVersion = c.SyntaxVersion,
            DocumentKind = c.DocumentKind,
            SpecificationId = c.SpecificationId,
            BusinessProcess = c.BusinessProcess,
            Standard = standard,
            Profile = profile,
            Version = version,
            Rules = rules,
            Note = note,
            Evidence = evidence,
        };
    }

    /// <summary>Every leaf element and attribute of the document as a raw field, with indexes for repeated elements.</summary>
    public static List<SourceField> Flatten(XElement root)
    {
        var fields = new List<SourceField>();
        Walk(root, "/" + root.Name.LocalName, fields);
        return fields;
    }

    private static void Walk(XElement element, string path, List<SourceField> fields)
    {
        foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Value.Trim().Length > 0))
        {
            fields.Add(new SourceField($"{path}/@{attribute.Name.LocalName}", attribute.Value.Trim()));
        }

        if (!element.HasElements)
        {
            var text = element.Value.Trim();
            if (text.Length > 0)
            {
                fields.Add(new SourceField(path, text));
            }

            return;
        }

        var counts = element.Elements().GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.Count());
        var seen = new Dictionary<XName, int>();
        foreach (var child in element.Elements())
        {
            var name = XmlNames.QualifiedName(child);
            var index = seen[child.Name] = seen.GetValueOrDefault(child.Name) + 1;
            var step = counts[child.Name] > 1 ? $"{name}[{index}]" : name;
            Walk(child, $"{path}/{step}", fields);
        }
    }

    /// <summary>The business terms of a UBL or CII document. The raw path of each value is kept as its source.</summary>
    public static SemanticDocument ReadSemantic(XElement root, InvoiceSyntax syntax, List<ExtractionMessage> messages)
    {
        var doc = new SemanticDocument();
        if (syntax is not (InvoiceSyntax.Ubl or InvoiceSyntax.Cii))
        {
            return doc;
        }

        var ubl = syntax == InvoiceSyntax.Ubl;
        foreach (var field in SemanticCatalog.FieldsOf(null))
        {
            var path = ubl ? field.Ubl : field.Cii;
            if (path is null)
            {
                continue;
            }

            var values = XPathLite.Values(root, path);
            if (values.Count == 0)
            {
                continue;
            }

            // A note may be given several times; anything else takes the first value.
            var value = field.Id == "BT-22" ? string.Join("\n", values) : values[0];
            doc.Set(field.Id, Normalize(field, value, messages), Confidence.High, SourceOf(root, path));
        }

        foreach (var group in SemanticCatalog.Groups)
        {
            var groupPath = ubl ? group.Ubl : group.Cii;
            if (groupPath is null)
            {
                continue;
            }

            var elements = XPathLite.Elements(root, groupPath);
            var items = doc.EnsureGroup(group.Id);
            var position = 0;
            foreach (var element in elements)
            {
                position++;
                var origin = PathOf(element);
                var item = new SemanticItem { Origin = origin };
                foreach (var field in SemanticCatalog.FieldsOf(group.Id))
                {
                    var path = ubl ? field.Ubl : field.Cii;
                    if (path is null)
                    {
                        continue;
                    }

                    var value = XPathLite.Value(element, path);
                    if (value is not null)
                    {
                        item.Set(field.Id, Normalize(field, value, messages), Confidence.High, origin + "/" + path.Split('|')[0]);
                    }
                }

                items.Add(item);
            }

            if (items.Count == 0)
            {
                doc.Groups.Remove(group.Id);
            }
        }

        return doc;
    }

    /// <summary>Dates in the fixed form (CII writes <c>20261018</c>, format 102), numbers invariant without a plus sign.</summary>
    private static string Normalize(SemanticField field, string value, List<ExtractionMessage> messages)
    {
        switch (field.Type)
        {
            case SemanticType.Date:
                if (value.Length == 8 && DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d102))
                {
                    return SemanticDocument.Format(d102);
                }

                if (DateOnly.TryParseExact(value.Length >= 10 ? value[..10] : value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
                {
                    return SemanticDocument.Format(iso);
                }

                messages.Add(new ExtractionMessage(MessageSeverity.Warning, "DATE-FORMAT", $"{SemanticCatalog.Describe(field.Id)}: „{value}“ ist kein gültiges Datum; der Wert wurde unverändert übernommen."));
                return value;

            case SemanticType.Amount or SemanticType.UnitPrice or SemanticType.Quantity or SemanticType.Percent:
                if (decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
                {
                    return SemanticDocument.Format(number);
                }

                messages.Add(new ExtractionMessage(MessageSeverity.Warning, "NUMBER-FORMAT", $"{SemanticCatalog.Describe(field.Id)}: „{value}“ ist keine Zahl im Format des Standards; der Wert wurde unverändert übernommen."));
                return value;

            default:
                return value;
        }
    }

    /// <summary>The raw path of the first element a binding found, as the source of a value.</summary>
    private static string SourceOf(XElement root, string path)
    {
        var first = path.Split('|')[0];
        var attribute = first.LastIndexOf("/@", StringComparison.Ordinal);
        var elementPath = attribute > 0 ? first[..attribute] : first;
        var element = XPathLite.Elements(root, elementPath).FirstOrDefault();
        return element is null ? first : PathOf(element) + (attribute > 0 ? first[attribute..] : "");
    }

    /// <summary>The raw path of an element, in the form of <see cref="Flatten"/>.</summary>
    public static string PathOf(XElement element)
    {
        var steps = new Stack<string>();
        for (var e = element; e is not null; e = e.Parent)
        {
            if (e.Parent is null)
            {
                steps.Push(e.Name.LocalName);
                break;
            }

            var siblings = e.Parent.Elements(e.Name).ToList();
            var name = XmlNames.QualifiedName(e);
            steps.Push(siblings.Count > 1 ? $"{name}[{siblings.IndexOf(e) + 1}]" : name);
        }

        return "/" + string.Join("/", steps);
    }
}
