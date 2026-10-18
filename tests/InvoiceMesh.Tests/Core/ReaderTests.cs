using InvoiceMesh.Core.Extraction;
using InvoiceMesh.Core.Formats;
using InvoiceMesh.Core.Semantics;

namespace InvoiceMesh.Tests.Core;

public class ReaderTests
{
    private static readonly InvoiceReader Reader = new(new SpecificationRegistry());

    [Fact]
    public async Task XRechnung_in_UBL_is_recognised_by_content_not_by_name()
    {
        var result = await Reader.ReadAsync(Samples.Bytes("xrechnung-ubl.xml"), "irgendwas.pdf");

        Assert.Equal(FileKind.Xml, result.Format.Kind);
        Assert.Equal(InvoiceSyntax.Ubl, result.Format.Syntax);
        Assert.Equal("UBL 2.1", result.Format.SyntaxVersion);
        Assert.Equal("XRechnung", result.Format.Standard);
        Assert.Equal("3.0", result.Format.Version);
        Assert.True(result.Format.Rules.HasFlag(RuleSets.XRechnung));
        Assert.Contains(result.Format.Evidence, e => e.Contains("Endung", StringComparison.Ordinal));
        Assert.Equal(ExtractionMethod.Xml, result.Method);
    }

    [Fact]
    public async Task UBL_business_terms_are_read_with_their_paths()
    {
        var d = (await Reader.ReadAsync(Samples.Bytes("xrechnung-ubl.xml"), "x.xml")).Semantic;

        Assert.Equal("RE-2026-0815", d.Get("BT-1"));
        Assert.Equal("2026-10-01", d.Get("BT-2"));
        Assert.Equal("Nordlicht IT GmbH", d.Get("BT-27"));
        Assert.Equal("Nordlicht IT", d.Get("BT-28"));
        Assert.Equal("DE123456789", d.Get("BT-31"));
        Assert.Equal("EM", d.Get("BT-34-scheme"));
        Assert.Equal("1243.5", d.Get("BT-112"));
        Assert.Equal("193.5", d.Get("BT-110"));
        Assert.Equal("/Invoice/cbc:ID", d.Value("BT-1")!.Source);
        Assert.Equal(2, d.Items(SemanticCatalog.Lines).Count);
        Assert.Equal("HUR", d.Items(SemanticCatalog.Lines)[0].Get("BT-130"));
        Assert.Equal("Fachbuch", d.Items(SemanticCatalog.Lines)[1].Get("BT-153"));
        Assert.Equal(2, d.Items(SemanticCatalog.VatBreakdown).Count);
        Assert.Equal("7", d.Items(SemanticCatalog.VatBreakdown)[1].Get("BT-119"));
    }

    [Fact]
    public async Task CII_dates_become_iso_and_the_profile_EN16931_is_named()
    {
        var result = await Reader.ReadAsync(Samples.Bytes("facturx-en16931.xml"), "facture.xml");
        var d = result.Semantic;

        Assert.Equal(InvoiceSyntax.Cii, result.Format.Syntax);
        Assert.Equal("EN 16931", result.Format.Standard);
        Assert.Equal("2026-10-05", d.Get("BT-2"));
        Assert.Equal("2026-11-04", d.Get("BT-9"));
        Assert.Equal("FR32123456789", d.Get("BT-31"));
        Assert.Equal("DE987654321", d.Get("BT-48"));
        Assert.Equal("5.5", d.Items(SemanticCatalog.Lines)[0].Get("BT-152"));
        Assert.Equal("233.35", d.Get("BT-115"));
    }

    [Fact]
    public async Task A_UBL_credit_note_reads_its_lines()
    {
        var result = await Reader.ReadAsync(Samples.Bytes("peppol-creditnote.xml"), "cn.xml");

        Assert.Equal("CreditNote", result.Format.DocumentKind);
        Assert.Equal("Peppol BIS Billing", result.Format.Standard);
        Assert.Equal("381", result.Semantic.Get("BT-3"));
        Assert.Equal("4", result.Semantic.Items(SemanticCatalog.Lines)[0].Get("BT-129"));
        Assert.Equal("INV-8800", result.Semantic.Get("BT-25"));
    }

    [Fact]
    public async Task Raw_fields_carry_indexes_for_repeated_elements()
    {
        var raw = (await Reader.ReadAsync(Samples.Bytes("xrechnung-ubl.xml"), "x.xml")).SourceFields;

        Assert.Contains(raw, f => f.Path == "/Invoice/cac:InvoiceLine[2]/cac:Item/cbc:Name" && f.Value == "Fachbuch");
        Assert.Contains(raw, f => f.Path == "/Invoice/cbc:DocumentCurrencyCode" && f.Value == "EUR");
        Assert.Contains(raw, f => f.Path == "/Invoice/cac:InvoiceLine[1]/cbc:InvoicedQuantity/@unitCode" && f.Value == "HUR");
        Assert.Equal("/Invoice/cac:InvoiceLine/cac:Item/cbc:Name", new SourceField("/Invoice/cac:InvoiceLine[2]/cac:Item/cbc:Name", "x").Pattern);
    }

    [Fact]
    public async Task Unsupported_and_broken_files_are_reported_in_words()
    {
        var zip = await Reader.ReadAsync([0x50, 0x4B, 0x03, 0x04, 1, 2, 3], "rechnung.xml");
        Assert.Equal(FileKind.Unknown, zip.Format.Kind);
        Assert.Contains(zip.Messages, m => m.Code == "FORMAT-UNSUPPORTED");

        var broken = await Reader.ReadAsync("<Invoice><cbc:ID>"u8.ToArray(), "x.xml");
        Assert.Contains(broken.Messages, m => m.Code == "XML-MALFORMED" && m.Severity == MessageSeverity.Error);

        var other = await Reader.ReadAsync("<order><id>1</id></order>"u8.ToArray(), "x.xml");
        Assert.Contains(other.Messages, m => m.Code == "XML-NOT-INVOICE");
        Assert.Contains(other.SourceFields, f => f.Path == "/order/id");
    }

    [Fact]
    public async Task A_DTD_is_refused()
    {
        var xxe = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><x>&e;</x>"u8.ToArray();
        var result = await Reader.ReadAsync(xxe, "x.xml");
        Assert.Contains(result.Messages, m => m.Code == "XML-MALFORMED");
    }

    [Fact]
    public async Task A_scan_without_ocr_says_so()
    {
        var png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0 };
        var result = await Reader.ReadAsync(png, "scan.png");
        Assert.Equal(FileKind.Image, result.Format.Kind);
        Assert.Contains(result.Messages, m => m.Code == "OCR-UNAVAILABLE");
    }

    [Fact]
    public async Task A_custom_specification_is_found_before_the_built_in_ones()
    {
        var registry = new SpecificationRegistry([new Specification("urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0", false, "Mein Standard", "Mein Profil", "9.9", RuleSets.En16931, [])]);
        var result = await new InvoiceReader(registry).ReadAsync(Samples.Bytes("xrechnung-ubl.xml"), "x.xml");
        Assert.Equal("Mein Standard", result.Format.Standard);
        Assert.Contains(result.Format.Evidence, e => e.Contains("eigener Eintrag", StringComparison.Ordinal));
    }
}
