using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using InvoiceMesh.Core.Extraction;
using InvoiceMesh.Core.Formats;
using InvoiceMesh.Core.Mapping;
using InvoiceMesh.Core.Output;

namespace InvoiceMesh.Tests.Core;

public class ExportTests
{
    private static async Task<List<ExportRecord>> RecordsAsync()
    {
        var list = new List<ExportRecord>();
        long id = 1;
        foreach (var sample in new[] { "xrechnung-ubl.xml", "facturx-en16931.xml" })
        {
            var r = await new InvoiceReader(new SpecificationRegistry()).ReadAsync(Samples.Bytes(sample), sample);
            var data = MappingEngine.Run(Defaults.Schema(), Defaults.Mapping(), new MappingInput { Semantic = r.Semantic, Raw = r.SourceFields }).Data;
            list.Add(new ExportRecord(id++, sample, data));
        }

        return list;
    }

    [Fact]
    public async Task Json_keeps_the_nesting_of_the_schema()
    {
        var (bytes, type, ext) = Exporter.Write(await RecordsAsync(), Defaults.Schema(), new ExportOptions { Format = ExportFormat.Json, Groups = ["invoice", "lines"] });
        var array = JsonNode.Parse(bytes)!.AsArray();

        Assert.Equal("json", ext);
        Assert.Equal(2, array.Count);
        Assert.Equal("RE-2026-0815", (string)array[0]!["invoice"]!["number"]!);
        Assert.Equal(2, array[1]!["lines"]!.AsArray().Count);
        Assert.Null(array[0]!["seller"]);
        Assert.StartsWith("application/json", type, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Csv_one_row_per_line_with_german_decimals()
    {
        var options = new ExportOptions { Format = ExportFormat.Csv, CsvLayout = CsvLayout.ItemPerRow, RowGroup = "lines", Groups = ["invoice", "totals", "lines"], Exclude = ["invoice.note"] };
        var text = Encoding.UTF8.GetString(Exporter.Write(await RecordsAsync(), Defaults.Schema(), options).Bytes).TrimStart('﻿');
        var rows = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length > 1 ? text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries) : text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(5, rows.Length);
        Assert.StartsWith("id;invoice.number;", rows[0], StringComparison.Ordinal);
        Assert.DoesNotContain("invoice.note", rows[0], StringComparison.Ordinal);
        Assert.Contains("1243,5", rows[1], StringComparison.Ordinal);
        Assert.Contains("Café en grains 500 g", rows[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Csv_cells_cannot_become_formulas()
    {
        var records = await RecordsAsync();
        records[0].Data["invoice"]!["note"] = "=HYPERLINK(\"http://x\")";
        var text = Encoding.UTF8.GetString(Exporter.Write(records, Defaults.Schema(), new ExportOptions { Format = ExportFormat.Csv, Groups = ["invoice"] }).Bytes);
        Assert.Contains("\"'=HYPERLINK(\"\"http://x\"\")\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Xml_uses_the_configured_element_names()
    {
        var bytes = Exporter.Write(await RecordsAsync(), Defaults.Schema(), new ExportOptions { Format = ExportFormat.Xml, XmlRoot = "Rechnungen", XmlInvoice = "Rechnung", XmlItem = "Position" }).Bytes;
        var root = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!;

        Assert.Equal("Rechnungen", root.Name.LocalName);
        Assert.Equal(2, root.Elements("Rechnung").Count());
        Assert.Equal("RE-2026-0815", root.Element("Rechnung")!.Element("invoice")!.Element("number")!.Value);
        Assert.Equal(2, root.Element("Rechnung")!.Element("lines")!.Elements("Position").Count());
    }
}
