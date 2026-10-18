using InvoiceMesh.Core.Extraction;
using InvoiceMesh.Core.Semantics;

namespace InvoiceMesh.Tests.Core;

public class TextExtractionTests
{
    [Fact]
    public void A_German_paper_invoice_is_read_with_labels_and_lines()
    {
        var r = TextInvoiceExtractor.Extract(Samples.Text("paper-de.txt"));
        var d = r.Semantic;

        Assert.Equal("2026-10477", d.Get("BT-1"));
        Assert.Equal(Confidence.Medium, d.Value("BT-1")!.Confidence);
        Assert.Equal("2026-10-14", d.Get("BT-2"));
        Assert.Equal("2026-10-28", d.Get("BT-9"));
        Assert.Equal("B-88123", d.Get("BT-13"));
        Assert.Equal("EUR", d.Get("BT-5"));
        Assert.Equal("Müller Bürobedarf GmbH", d.Get("BT-27"));
        Assert.Equal("Hansen & Söhne KG", d.Get("BT-44"));
        Assert.Equal("DE811907980", d.Get("BT-31"));
        Assert.Equal("DE02120300000000202051", d.Get("BT-84"));
        Assert.Equal(Confidence.High, d.Value("BT-84")!.Confidence);
        Assert.Equal("BYLADEM1001", d.Get("BT-86"));
        Assert.Equal("143/123/45678", d.Get("BT-32"));
        Assert.Equal("130", d.Get("BT-109"));
        Assert.Equal("24.7", d.Get("BT-110"));
        Assert.Equal("154.7", d.Get("BT-112"));
        Assert.Equal("80331", d.Get("BT-38"));
        Assert.Equal("München", d.Get("BT-37"));

        var lines = d.Items(SemanticCatalog.Lines);
        Assert.Equal(3, lines.Count);
        Assert.Equal("Kopierpapier A4, 500 Blatt", lines[0].Get("BT-153"));
        Assert.Equal("10", lines[0].Get("BT-129"));
        Assert.Equal("H87", lines[0].Get("BT-130"));
        Assert.Equal("4.5", lines[0].Get("BT-146"));
        Assert.Equal("79.8", lines[1].Get("BT-131"));
        Assert.All(lines, l => Assert.Equal(Confidence.Low, l.Fields["BT-153"].Confidence));

        // Nothing is made up: the payment means and the tax category are not on the paper.
        Assert.Null(d.Get("BT-81"));
        Assert.Null(d.Get("BT-40"));
        Assert.Contains(r.RawFields, f => f.Path == "text:Rechnungsnummer" && f.Value == "2026-10477");
    }

    [Fact]
    public void An_English_invoice_reads_month_names_and_thousands_separators()
    {
        var d = TextInvoiceExtractor.Extract(Samples.Text("paper-en.txt")).Semantic;

        Assert.Equal("BD-3391", d.Get("BT-1"));
        Assert.Equal("2026-10-09", d.Get("BT-2"));
        Assert.Equal("2026-11-08", d.Get("BT-9"));
        Assert.Equal("GBP", d.Get("BT-5"));
        Assert.Equal("GB123456789", d.Get("BT-31"));
        Assert.Equal("2000", d.Get("BT-109"));
        Assert.Equal("2000", d.Get("BT-115"));
        Assert.Equal("Alpine Trading AG", d.Get("BT-44"));
        Assert.Equal(2, d.Items(SemanticCatalog.Lines).Count);
        Assert.Equal("1200", d.Items(SemanticCatalog.Lines)[0].Get("BT-131"));
    }

    [Fact]
    public void A_poor_scan_marks_everything_uncertain()
    {
        var r = TextInvoiceExtractor.Extract(Samples.Text("paper-de.txt"), ocrConfidence: 45);
        Assert.All(r.Semantic.Fields.Values, v => Assert.Equal(Confidence.Low, v.Confidence));
        Assert.Contains(r.Messages, m => m.Code == "OCR-QUALITY");
    }

    [Theory]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("1234,5", 1234.5)]
    [InlineData("12,00-", -12)]
    [InlineData("(99.90)", -99.9)]
    [InlineData("1'234.50", 1234.5)]
    [InlineData("€ 4,50", 4.5)]
    public void Amounts_in_several_notations(string text, double expected)
    {
        Assert.True(TextValues.TryParseAmount(text, out var v));
        Assert.Equal((decimal)expected, v);
    }

    [Theory]
    [InlineData("am 14.10.2026", false, "2026-10-14", false)]
    [InlineData("2026-03-01", false, "2026-03-01", false)]
    [InlineData("18 octobre 2026", false, "2026-10-18", false)]
    [InlineData("October 18, 2026", true, "2026-10-18", false)]
    [InlineData("03/04/2026", true, "2026-03-04", true)]
    [InlineData("25/04/2026", true, "2026-04-25", false)]
    public void Dates_in_several_languages(string text, bool monthFirst, string expected, bool ambiguous)
    {
        Assert.True(TextValues.TryFindDate(text, monthFirst, out var d, out var a));
        Assert.Equal(expected, SemanticDocument.Format(d));
        Assert.Equal(ambiguous, a);
    }

    [Theory]
    [InlineData("DE02120300000000202051", true)]
    [InlineData("DE02 1203 0000 0000 2020 51", true)]
    [InlineData("DE02120300000000202052", false)]
    [InlineData("FR7630006000011234567890189", true)]
    public void Iban_check_digits(string iban, bool valid) => Assert.Equal(valid, TextValues.IsValidIban(iban));
}
