using System.Xml;
using System.Xml.Linq;

namespace InvoiceMesh.Core.Xml;

/// <summary>
/// The namespaces of the e-invoice syntaxes and the prefixes InvoiceMesh writes them with. A document may use any prefix it likes; paths in InvoiceMesh
/// (bindings, raw source fields, mappings) always use these, so that a mapping written for one supplier works for the next.
/// </summary>
public static class XmlNames
{
    public const string UblInvoice = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
    public const string UblCreditNote = "urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2";
    public const string Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    public const string Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    public const string Ext = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2";
    public const string Rsm = "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100";
    public const string Ram = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100";
    public const string Udt = "urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100";
    public const string Qdt = "urn:un:unece:uncefact:data:standard:QualifiedDataType:100";

    /// <summary>ZUGFeRD 1.0 (2014): CII in an older version (D13B "CrossIndustryDocument"). It is recognised; its fields are offered as raw fields.</summary>
    public const string ZugferdV1Rsm = "urn:ferd:CrossIndustryDocument:invoice:1p0";

    public static readonly IReadOnlyDictionary<string, string> PrefixToNamespace = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ubl"] = UblInvoice,
        ["cn"] = UblCreditNote,
        ["cbc"] = Cbc,
        ["cac"] = Cac,
        ["ext"] = Ext,
        ["rsm"] = Rsm,
        ["ram"] = Ram,
        ["udt"] = Udt,
        ["qdt"] = Qdt,
    };

    private static readonly Dictionary<string, string> NamespaceToPrefix = PrefixToNamespace.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);

    /// <summary>The prefix InvoiceMesh writes a namespace with: a known one, or the document's own, or none.</summary>
    public static string PrefixOf(XElement element)
    {
        var ns = element.Name.NamespaceName;
        if (ns.Length == 0)
        {
            return string.Empty;
        }

        if (NamespaceToPrefix.TryGetValue(ns, out var known))
        {
            return known;
        }

        return element.GetPrefixOfNamespace(element.Name.Namespace) ?? "ns";
    }

    /// <summary>The name of an element in a path: <c>cbc:ID</c>.</summary>
    public static string QualifiedName(XElement element)
    {
        var prefix = PrefixOf(element);
        return prefix.Length == 0 ? element.Name.LocalName : prefix + ":" + element.Name.LocalName;
    }

    /// <summary>
    /// Parses XML safely: no DTD, no external entities, a limit on the size of entities. An e-invoice never needs any of these, and an attacker would.
    /// </summary>
    public static XDocument Load(byte[] bytes)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 1024,
            MaxCharactersInDocument = 64L * 1024 * 1024,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        using var stream = new MemoryStream(bytes);
        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    /// <summary>Whether the first bytes look like XML (after a BOM and white space).</summary>
    public static bool LooksLikeXml(ReadOnlySpan<byte> bytes)
    {
        var i = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            i = 3;
        }

        while (i < bytes.Length && bytes[i] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t')
        {
            i++;
        }

        return i < bytes.Length && bytes[i] == (byte)'<';
    }
}
