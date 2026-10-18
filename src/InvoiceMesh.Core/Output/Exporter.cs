using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using InvoiceMesh.Core.Schemas;

namespace InvoiceMesh.Core.Output;

public enum ExportFormat
{
    Json,
    Csv,
    Xml,
}

public enum CsvLayout
{
    /// <summary>One row per invoice; repeating groups are left out.</summary>
    InvoicePerRow,

    /// <summary>One row per item of <see cref="ExportOptions.RowGroup"/>, the invoice's fields repeated on every row.</summary>
    ItemPerRow,
}

/// <summary>The configuration of an export: format, the groups and fields it holds, and the details of each format.</summary>
public sealed class ExportOptions
{
    public ExportFormat Format { get; set; } = ExportFormat.Json;

    /// <summary>The groups to export (keys); empty: all.</summary>
    public List<string> Groups { get; set; } = [];

    /// <summary>Single fields left out ("seller.phone").</summary>
    public List<string> Exclude { get; set; } = [];

    public string CsvDelimiter { get; set; } = ";";

    /// <summary>"." or ","; numbers are written with it in CSV.</summary>
    public string CsvDecimalSeparator { get; set; } = ",";

    public CsvLayout CsvLayout { get; set; } = CsvLayout.InvoicePerRow;

    /// <summary>The repeating group that makes the rows in <see cref="CsvLayout.ItemPerRow"/> ("lines").</summary>
    public string? RowGroup { get; set; } = "lines";

    /// <summary>Column heads are the labels ("Rechnung: Rechnungsnummer") instead of the keys ("invoice.number").</summary>
    public bool CsvLabels { get; set; }

    public string XmlRoot { get; set; } = "Invoices";

    public string XmlInvoice { get; set; } = "Invoice";

    public string XmlItem { get; set; } = "Item";

    public bool JsonIndented { get; set; } = true;
}

/// <summary>One invoice to export: its id in InvoiceMesh and its data in the schema.</summary>
public sealed record ExportRecord(long Id, string? Reference, JsonObject Data);

/// <summary>Writes normalized data in the form an export configuration describes. All three formats come from the same records and the same schema.</summary>
public static class Exporter
{
    public static (byte[] Bytes, string ContentType, string Extension) Write(IReadOnlyList<ExportRecord> records, SchemaDefinition schema, ExportOptions options)
    {
        var groups = schema.Groups.Where(g => options.Groups.Count == 0 || options.Groups.Contains(g.Key, StringComparer.OrdinalIgnoreCase)).ToList();
        return options.Format switch
        {
            ExportFormat.Csv => (Csv(records, groups, options), "text/csv; charset=utf-8", "csv"),
            ExportFormat.Xml => (Xml(records, groups, options), "application/xml", "xml"),
            _ => (Json(records, groups, options), "application/json", "json"),
        };
    }

    private static bool Included(SchemaGroup g, SchemaField f, ExportOptions o) => !o.Exclude.Contains(SchemaDefinition.Target(g.Key, f.Key), StringComparer.OrdinalIgnoreCase);

    private static byte[] Json(IReadOnlyList<ExportRecord> records, List<SchemaGroup> groups, ExportOptions o)
    {
        var array = new JsonArray();
        foreach (var r in records)
        {
            var obj = new JsonObject { ["id"] = r.Id };
            foreach (var g in groups)
            {
                var node = r.Data[g.Key];
                if (g.Repeating)
                {
                    var items = new JsonArray();
                    foreach (var item in node as JsonArray ?? [])
                    {
                        items.Add(Pick(item as JsonObject, g, o));
                    }

                    obj[g.Key] = items;
                }
                else
                {
                    obj[g.Key] = Pick(node as JsonObject, g, o);
                }
            }

            array.Add(obj);
        }

        return Encoding.UTF8.GetBytes(array.ToJsonString(new JsonSerializerOptions { WriteIndented = o.JsonIndented, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    private static JsonObject Pick(JsonObject? source, SchemaGroup g, ExportOptions o)
    {
        var obj = new JsonObject();
        foreach (var f in g.Fields.Where(f => Included(g, f, o)))
        {
            obj[f.Key] = source?[f.Key]?.DeepClone();
        }

        return obj;
    }

    private static byte[] Csv(IReadOnlyList<ExportRecord> records, List<SchemaGroup> groups, ExportOptions o)
    {
        var head = groups.Where(g => !g.Repeating).SelectMany(g => g.Fields.Where(f => Included(g, f, o)).Select(f => (Group: g, Field: f))).ToList();
        var rowGroup = o.CsvLayout == CsvLayout.ItemPerRow ? groups.FirstOrDefault(g => g.Repeating && string.Equals(g.Key, o.RowGroup, StringComparison.OrdinalIgnoreCase)) : null;
        var item = rowGroup?.Fields.Where(f => Included(rowGroup, f, o)).Select(f => (Group: rowGroup, Field: f)).ToList() ?? [];

        var sb = new StringBuilder();
        var header = new List<string> { "id" };
        header.AddRange(head.Concat(item).Select(c => o.CsvLabels ? $"{c.Group.Label}: {c.Field.Label}" : SchemaDefinition.Target(c.Group.Key, c.Field.Key)));
        sb.AppendLine(string.Join(o.CsvDelimiter, header.Select(h => Cell(h, o, null))));

        foreach (var r in records)
        {
            var fixedCells = new List<string> { r.Id.ToString(CultureInfo.InvariantCulture) };
            fixedCells.AddRange(head.Select(c => Cell(FieldValues.AsText((r.Data[c.Group.Key] as JsonObject)?[c.Field.Key]), o, c.Field)));
            var items = rowGroup is null ? [] : (r.Data[rowGroup.Key] as JsonArray ?? []).OfType<JsonObject>().ToList();
            if (items.Count == 0)
            {
                sb.AppendLine(string.Join(o.CsvDelimiter, fixedCells.Concat(item.Select(_ => string.Empty))));
                continue;
            }

            foreach (var it in items)
            {
                sb.AppendLine(string.Join(o.CsvDelimiter, fixedCells.Concat(item.Select(c => Cell(FieldValues.AsText(it[c.Field.Key]), o, c.Field)))));
            }
        }

        // With a BOM: Excel opens UTF-8 files with umlauts correctly only then.
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    /// <summary>
    /// One CSV cell: numbers with the chosen decimal separator, quotes where needed, and text that a spreadsheet would run as a formula (= + - @) made
    /// harmless with a leading apostrophe.
    /// </summary>
    private static string Cell(string? value, ExportOptions o, SchemaField? field)
    {
        if (value is null)
        {
            return string.Empty;
        }

        var numeric = field?.Type is FieldType.Decimal or FieldType.Money or FieldType.Percent or FieldType.Integer;
        if (numeric && o.CsvDecimalSeparator == ",")
        {
            value = value.Replace('.', ',');
        }

        if (!numeric && value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.Contains(o.CsvDelimiter, StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal)
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    private static byte[] Xml(IReadOnlyList<ExportRecord> records, List<SchemaGroup> groups, ExportOptions o)
    {
        var root = new XElement(Name(o.XmlRoot, "Invoices"));
        foreach (var r in records)
        {
            var invoice = new XElement(Name(o.XmlInvoice, "Invoice"), new XAttribute("id", r.Id));
            foreach (var g in groups)
            {
                if (g.Repeating)
                {
                    var list = new XElement(Name(g.Key, "group"));
                    foreach (var it in (r.Data[g.Key] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        list.Add(new XElement(Name(o.XmlItem, "Item"), Fields(it, g, o)));
                    }

                    invoice.Add(list);
                }
                else
                {
                    invoice.Add(new XElement(Name(g.Key, "group"), Fields(r.Data[g.Key] as JsonObject, g, o)));
                }
            }

            root.Add(invoice);
        }

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
        {
            new XDocument(new XDeclaration("1.0", "UTF-8", null), root).Save(writer);
        }

        return stream.ToArray();
    }

    private static IEnumerable<XElement> Fields(JsonObject? source, SchemaGroup g, ExportOptions o)
        => g.Fields.Where(f => Included(g, f, o)).Select(f => FieldValues.AsText(source?[f.Key]) is { } v ? new XElement(Name(f.Key, "field"), v) : new XElement(Name(f.Key, "field")));

    private static XName Name(string candidate, string fallback)
    {
        try
        {
            return XmlConvert.VerifyNCName(candidate);
        }
        catch (XmlException)
        {
            return fallback;
        }
    }
}
