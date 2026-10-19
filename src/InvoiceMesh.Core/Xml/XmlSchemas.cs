using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Schema;
using InvoiceMesh.Core.Formats;

namespace InvoiceMesh.Core.Xml;

/// <summary>One violation of the XML schema, where it is in the file and what is wrong.</summary>
public sealed record SchemaViolation(int Line, int Column, string Message);

/// <summary>The result of a check against an XML schema: which schema, and what violates it (empty: the structure is valid).</summary>
public sealed record SchemaCheck(string Schema, IReadOnlyList<SchemaViolation> Violations, int Total);

/// <summary>
/// Checks the structure of a UBL 2.1 or CII D16B document against the official XML schemas (embedded from <c>Xsd/</c>): element names and order,
/// cardinalities, data types. The schemas are compiled once; a compiled schema set may be used by several threads at the same time.
/// </summary>
public static partial class XmlSchemas
{
    public const string UblSchema = "UBL 2.1";
    public const string CiiSchema = "CII D16B";

    private const string CiiNamespace = "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100";
    private const int MaxViolations = 25;

    private static readonly Lazy<XmlSchemaSet> Ubl = new(() => Compile("ubl/maindoc/UBL-Invoice-2.1.xsd", "ubl/maindoc/UBL-CreditNote-2.1.xsd"));
    private static readonly Lazy<XmlSchemaSet> Cii = new(() => Compile("cii/CrossIndustryInvoice_100pD16B.xsd"));

    [GeneratedRegex(@" in namespace '[^']*'")]
    private static partial Regex NamespaceRegex();

    [GeneratedRegex(@"'(?:urn|https?):[^']*:([^':]+)'")]
    private static partial Regex QualifiedNameRegex();

    /// <summary>The messages of the .NET validator that come up most, in German (anything else stays as the validator wrote it).</summary>
    private static readonly (Regex Pattern, string German)[] Translations =
    [
        (new(@"^The element '([^']+)' has invalid child element '([^']+)'\. List of possible elements expected: (.+?)\.?$"), "In „$1“ ist „$2“ an dieser Stelle nicht erlaubt. Erwartet wird: $3."),
        (new(@"^The element '([^']+)' has invalid child element '([^']+)'\.?$"), "In „$1“ ist „$2“ an dieser Stelle nicht erlaubt."),
        (new(@"^The element '([^']+)' has incomplete content\. List of possible elements expected: (.+?)\.?$"), "In „$1“ fehlt etwas. Erwartet wird: $2."),
        (new(@"^The '([^']+)' element is invalid - The value '([^']*)' is invalid according to its datatype '([^']+)'.*$"), "„$1“: Der Wert „$2“ passt nicht zum Datentyp $3."),
        (new(@"^The '([^']+)' attribute is invalid - The value '([^']*)' is invalid according to its datatype '([^']+)'.*$"), "Attribut „$1“: Der Wert „$2“ passt nicht zum Datentyp $3."),
        (new(@"^The required attribute '([^']+)' is missing\.?$"), "Das Pflichtattribut „$1“ fehlt."),
        (new(@"^The '([^']+)' attribute is not declared\.?$"), "Das Attribut „$1“ ist im Schema nicht vorgesehen."),
        (new(@"^The element '([^']+)' cannot contain text\..*$"), "„$1“ darf keinen Text enthalten."),
    ];

    /// <summary>The schema for a syntax and root namespace, or null when there is none (ZUGFeRD 1.0, any other XML).</summary>
    public static string? SchemaFor(InvoiceSyntax syntax, string rootNamespace) => syntax switch
    {
        InvoiceSyntax.Ubl => UblSchema,
        InvoiceSyntax.Cii when rootNamespace == CiiNamespace => CiiSchema,
        _ => null,
    };

    /// <summary>Validates the document against the schema of its syntax. Returns null when there is no schema for it.</summary>
    public static SchemaCheck? Check(byte[] xml, InvoiceSyntax syntax, string rootNamespace)
    {
        var schema = SchemaFor(syntax, rootNamespace);
        if (schema is null)
        {
            return null;
        }

        var violations = new List<SchemaViolation>();
        var total = 0;
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            ValidationType = ValidationType.Schema,
            Schemas = schema == UblSchema ? Ubl.Value : Cii.Value,
        };
        settings.ValidationEventHandler += (_, e) =>
        {
            total++;
            if (violations.Count < MaxViolations)
            {
                violations.Add(new SchemaViolation(e.Exception?.LineNumber ?? 0, e.Exception?.LinePosition ?? 0, Readable(e.Message)));
            }
        };

        try
        {
            using var reader = XmlReader.Create(new MemoryStream(xml), settings);
            while (reader.Read())
            {
            }
        }
        catch (XmlException ex)
        {
            total++;
            violations.Add(new SchemaViolation(ex.LineNumber, ex.LinePosition, ex.Message));
        }

        return new SchemaCheck(schema, violations, total);
    }

    /// <summary>The message of the validator without the long namespace names (the prefixes of the file say the same), in German where it is a known one.</summary>
    internal static string Readable(string message)
    {
        var plain = QualifiedNameRegex().Replace(NamespaceRegex().Replace(message, ""), "'$1'");
        foreach (var (pattern, german) in Translations)
        {
            if (pattern.IsMatch(plain))
            {
                return pattern.Replace(plain, german);
            }
        }

        return plain;
    }

    private static XmlSchemaSet Compile(params string[] roots)
    {
        var resolver = new EmbeddedResolver();
        var set = new XmlSchemaSet { XmlResolver = resolver };
        foreach (var root in roots)
        {
            var uri = new Uri(EmbeddedResolver.Base, root);
            using var reader = XmlReader.Create(resolver.Open(uri), EmbeddedResolver.SchemaReading, uri.AbsoluteUri);
            set.Add(null, reader);
        }

        set.Compile();
        return set;
    }

    /// <summary>Resolves the imports and includes of the schemas to the embedded files (never to the network or the disk).</summary>
    private sealed class EmbeddedResolver : XmlResolver
    {
        public static readonly Uri Base = new("xsd://invoicemesh/");

        // The xmldsig schema of UBL declares its namespace as an entity in an internal DTD: trusted, embedded, so a DTD is allowed here (only here).
        public static readonly XmlReaderSettings SchemaReading = new() { DtdProcessing = DtdProcessing.Parse, XmlResolver = null };

        private static readonly Assembly Assembly = typeof(XmlSchemas).Assembly;

        private static readonly Dictionary<string, string> Resources = Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("xsd/", StringComparison.Ordinal))
            .ToDictionary(n => n["xsd/".Length..].Replace('\\', '/'), n => n, StringComparer.OrdinalIgnoreCase);

        public Stream Open(Uri uri)
        {
            var path = uri.AbsolutePath.TrimStart('/');
            return Resources.TryGetValue(path, out var name) && Assembly.GetManifestResourceStream(name) is { } stream
                ? stream
                : throw new FileNotFoundException($"The schema {path} is not embedded.");
        }

        public override object GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn) => Open(absoluteUri);

        public override Uri ResolveUri(Uri? baseUri, string? relativeUri) => new(baseUri ?? Base, relativeUri ?? "");
    }
}
