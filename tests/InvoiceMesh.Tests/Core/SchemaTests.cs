using System.Text;
using InvoiceMesh.Core.Extraction;
using InvoiceMesh.Core.Formats;
using InvoiceMesh.Core.Output;
using InvoiceMesh.Core.Xml;

namespace InvoiceMesh.Tests.Core;

/// <summary>The check of the structure against the official XML schemas of UBL 2.1 and CII D16B.</summary>
public class SchemaTests
{
    private static readonly InvoiceReader Reader = new(new SpecificationRegistry());

    [Theory]
    [InlineData("xrechnung-ubl.xml", XmlSchemas.UblSchema)]
    [InlineData("peppol-creditnote.xml", XmlSchemas.UblSchema)]
    [InlineData("facturx-en16931.xml", XmlSchemas.CiiSchema)]
    public async Task The_samples_have_a_valid_structure(string sample, string schema)
    {
        var result = await Reader.ReadAsync(Samples.Bytes(sample), sample);

        Assert.DoesNotContain(result.Messages, m => m.Code == InvoiceReader.Xsd);
        Assert.Contains(result.Messages, m => m.Code == InvoiceReader.XsdValid && m.Text.Contains(schema, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(InvoiceSyntax.Ubl)]
    [InlineData(InvoiceSyntax.Cii)]
    public async Task A_written_XRechnung_has_a_valid_structure(InvoiceSyntax syntax)
    {
        var source = await Reader.ReadAsync(Samples.Bytes("xrechnung-ubl.xml"), "x.xml");
        var xml = XRechnungWriter.Write(source.Semantic, syntax);

        var check = XmlSchemas.Check(xml, syntax, syntax == InvoiceSyntax.Cii ? "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100" : "");

        Assert.NotNull(check);
        Assert.True(check.Total == 0, string.Join(Environment.NewLine, check.Violations.Select(v => $"{v.Line}:{v.Column} {v.Message}")));
    }

    [Fact]
    public async Task Elements_in_the_wrong_order_and_a_wrong_date_are_errors_of_an_XRechnung()
    {
        // The issue date after the due date (wrong order), and a date that is no date.
        var text = Samples.Text("xrechnung-ubl.xml")
            .Replace("<cbc:IssueDate>2026-10-01</cbc:IssueDate>", "", StringComparison.Ordinal)
            .Replace("<cbc:DueDate>2026-10-31</cbc:DueDate>", "<cbc:DueDate>31.10.2026</cbc:DueDate><cbc:IssueDate>2026-10-01</cbc:IssueDate>", StringComparison.Ordinal);

        var result = await Reader.ReadAsync(Encoding.UTF8.GetBytes(text), "x.xml");

        var errors = result.Messages.Where(m => m.Code == InvoiceReader.Xsd && m.Severity == MessageSeverity.Error).ToList();
        Assert.NotEmpty(errors);
        Assert.Contains(errors, m => m.Text.Contains("DueDate", StringComparison.Ordinal));
        Assert.All(errors, m => Assert.DoesNotContain("urn:oasis", m.Text, StringComparison.Ordinal));
        Assert.DoesNotContain(result.Messages, m => m.Code == InvoiceReader.XsdValid);
    }

    [Fact]
    public async Task A_value_of_the_wrong_type_is_reported_in_German()
    {
        var text = Samples.Text("xrechnung-ubl.xml").Replace("<cbc:DueDate>2026-10-31</cbc:DueDate>", "<cbc:DueDate>31.10.2026</cbc:DueDate>", StringComparison.Ordinal);

        var result = await Reader.ReadAsync(Encoding.UTF8.GetBytes(text), "x.xml");

        var error = Assert.Single(result.Messages, m => m.Code == InvoiceReader.Xsd);
        Assert.Contains("„DueDate“: Der Wert „31.10.2026“ passt nicht zum Datentyp", error.Text);
    }

    [Theory]
    [InlineData("The element 'Invoice' in namespace 'urn:x' has invalid child element 'DueDate' in namespace 'urn:y'. List of possible elements expected: 'IssueDate' in namespace 'urn:y'.",
        "In „Invoice“ ist „DueDate“ an dieser Stelle nicht erlaubt. Erwartet wird: 'IssueDate'.")]
    [InlineData("The element 'Party' in namespace 'urn:x' has incomplete content. List of possible elements expected: 'PostalAddress' in namespace 'urn:y'.",
        "In „Party“ fehlt etwas. Erwartet wird: 'PostalAddress'.")]
    [InlineData("The required attribute 'currencyID' is missing.", "Das Pflichtattribut „currencyID“ fehlt.")]
    [InlineData("Something nobody translated.", "Something nobody translated.")]
    public void Messages_of_the_validator_are_made_readable(string message, string expected)
        => Assert.Equal(expected, XmlSchemas.Readable(message));

    [Fact]
    public async Task An_unknown_element_in_an_Extended_profile_is_a_warning()
    {
        var text = Samples.Text("facturx-en16931.xml")
            .Replace("urn:cen.eu:en16931:2017</ram:ID>", "urn:cen.eu:en16931:2017#conformant#urn:factur-x.eu:1p0:extended</ram:ID>", StringComparison.Ordinal)
            .Replace("</rsm:ExchangedDocument>", "<ram:Unbekannt>x</ram:Unbekannt></rsm:ExchangedDocument>", StringComparison.Ordinal);

        var result = await Reader.ReadAsync(Encoding.UTF8.GetBytes(text), "x.xml");

        Assert.Equal("Extended", result.Format.Profile);
        Assert.Contains(result.Messages, m => m.Code == InvoiceReader.Xsd && m.Severity == MessageSeverity.Warning);
        Assert.DoesNotContain(result.Messages, m => m.Code == InvoiceReader.Xsd && m.Severity == MessageSeverity.Error);
    }

    [Fact]
    public async Task XML_that_is_no_invoice_is_not_checked_against_a_schema()
    {
        var result = await Reader.ReadAsync(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><Bestellung><Nummer>1</Nummer></Bestellung>"), "x.xml");

        Assert.DoesNotContain(result.Messages, m => m.Code is InvoiceReader.Xsd or InvoiceReader.XsdValid);
    }
}
