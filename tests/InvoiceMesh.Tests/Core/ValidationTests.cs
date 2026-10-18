using InvoiceMesh.Core.Extraction;
using InvoiceMesh.Core.Formats;
using InvoiceMesh.Core.Output;
using InvoiceMesh.Core.Semantics;
using InvoiceMesh.Core.Validation;

namespace InvoiceMesh.Tests.Core;

public class ValidationTests
{
    private static readonly DateOnly Today = new(2026, 10, 18);

    private static async Task<ExtractionResult> ReadAsync(string sample) => await new InvoiceReader(new SpecificationRegistry()).ReadAsync(Samples.Bytes(sample), sample);

    private static List<Finding> Validate(ExtractionResult r)
        => SemanticValidator.Validate(r.Semantic, new ValidationOptions(r.Format.Rules, r.Format.IsStructured, Today)).Findings;

    [Theory]
    [InlineData("xrechnung-ubl.xml")]
    [InlineData("facturx-en16931.xml")]
    [InlineData("peppol-creditnote.xml")]
    public async Task The_samples_are_valid(string sample)
    {
        var findings = Validate(await ReadAsync(sample));
        Assert.DoesNotContain(findings, f => f.Severity == Severity.Error);
    }

    [Fact]
    public async Task Sums_that_do_not_add_up_are_errors()
    {
        var r = await ReadAsync("xrechnung-ubl.xml");
        r.Semantic.Set("BT-112", "1300.00");
        r.Semantic.Items(SemanticCatalog.Lines)[0].Set("BT-131", "999.00");

        var findings = Validate(r);

        Assert.Contains(findings, f => f.RuleId == "BR-CO-15");
        Assert.Contains(findings, f => f.RuleId == "BR-CO-10");
        Assert.Contains(findings, f => f.RuleId == "BR-S-08");
        Assert.Contains(findings, f => f.RuleId == "PLAUS-LINE");
    }

    [Fact]
    public async Task XRechnung_rules_apply_only_to_XRechnung()
    {
        var r = await ReadAsync("xrechnung-ubl.xml");
        r.Semantic.Set("BT-10", null);
        r.Semantic.Set("BT-43", null);
        Assert.Contains(Validate(r), f => f.RuleId == "BR-DE-15");
        Assert.Contains(Validate(r), f => f.RuleId == "BR-DE-7");

        var facturx = await ReadAsync("facturx-en16931.xml");
        var report = SemanticValidator.Validate(facturx.Semantic, new ValidationOptions(facturx.Format.Rules, true, Today));
        Assert.Contains(report.Checks, c => c.Scope == CheckScope.XRechnung && !c.Performed);
        Assert.DoesNotContain(report.Findings, f => f.Scope == CheckScope.XRechnung);
    }

    [Fact]
    public async Task Peppol_rules()
    {
        var r = await ReadAsync("peppol-creditnote.xml");
        r.Semantic.Set("BT-23", "billing");
        r.Semantic.Set("BT-49", null);
        var findings = Validate(r);
        Assert.Contains(findings, f => f.RuleId == "PEPPOL-EN16931-R007");
        Assert.Contains(findings, f => f.RuleId == "PEPPOL-EN16931-R010");
    }

    [Fact]
    public void Code_lists_and_iban()
    {
        var d = new SemanticDocument();
        d.Set("BT-5", "EURO");
        d.Set("BT-40", "XX");
        d.Set("BT-84", "DE02120300000000202052");
        d.Set("BT-31", "123456789");
        var findings = SemanticValidator.Validate(d, new ValidationOptions(RuleSets.En16931, true, Today)).Findings;

        Assert.Contains(findings, f => f.RuleId == "BR-CL-03");
        Assert.Contains(findings, f => f.RuleId == "BR-CL-14");
        Assert.Contains(findings, f => f.RuleId == "PLAUS-IBAN");
        Assert.Contains(findings, f => f.RuleId == "BR-CO-09");
        Assert.Contains(findings, f => f.RuleId == "BR-01");
    }

    [Fact]
    public void A_paper_invoice_is_not_failed_for_codes_that_are_never_printed()
    {
        var r = TextInvoiceExtractor.Extract(Samples.Text("paper-de.txt"));
        var report = SemanticValidator.Validate(r.Semantic, new ValidationOptions(RuleSets.None, false, Today));

        Assert.DoesNotContain(report.Findings, f => f.RuleId is "BR-01" or "BR-09" or "BR-23" or "BR-21");
        Assert.DoesNotContain(report.Findings, f => f.Severity == Severity.Error);
        Assert.Contains(report.Findings, f => f.RuleId == "PLAUS-UNSURE");
        Assert.Contains(report.Checks, c => c.Scope == CheckScope.Content);
    }

    [Fact]
    public void A_paper_invoice_is_not_ready_for_XRechnung_and_says_why()
    {
        var r = TextInvoiceExtractor.Extract(Samples.Text("paper-de.txt"));
        var missing = SemanticValidator.XRechnungReadiness(r.Semantic, Today);

        Assert.Contains(missing, f => f.RuleId == "BR-DE-15");
        Assert.Contains(missing, f => f.RuleId == "BR-DE-1");
        Assert.Contains(missing, f => f.RuleId == "BR-09");
    }

    [Theory]
    [InlineData(InvoiceSyntax.Ubl)]
    [InlineData(InvoiceSyntax.Cii)]
    public async Task An_XRechnung_written_from_a_complete_model_reads_back_the_same(InvoiceSyntax syntax)
    {
        var source = await ReadAsync("xrechnung-ubl.xml");
        Assert.Empty(SemanticValidator.XRechnungReadiness(source.Semantic, Today));

        var xml = XRechnungWriter.Write(source.Semantic, syntax);

        // The CI hands these files to the validator of the KoSIT (the official check of an XRechnung) after the tests.
        if (Environment.GetEnvironmentVariable("INVOICEMESH_XRECHNUNG_OUT") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(Path.Combine(folder, $"roundtrip-{syntax.ToString().ToLowerInvariant()}.xml"), xml);
        }

        var back = await new InvoiceReader(new SpecificationRegistry()).ReadAsync(xml, "out.xml");

        Assert.Equal(syntax, back.Format.Syntax);
        Assert.Equal("XRechnung", back.Format.Standard);
        Assert.DoesNotContain(Validate(back), f => f.Severity == Severity.Error);
        foreach (var id in new[] { "BT-1", "BT-2", "BT-9", "BT-10", "BT-27", "BT-31", "BT-34", "BT-49", "BT-84", "BT-106", "BT-110", "BT-112", "BT-115" })
        {
            Assert.Equal(source.Semantic.Get(id), back.Semantic.Get(id));
        }

        Assert.Equal(2, back.Semantic.Items(SemanticCatalog.Lines).Count);
        Assert.Equal("HUR", back.Semantic.Items(SemanticCatalog.Lines)[0].Get("BT-130"));
        Assert.Equal("7", back.Semantic.Items(SemanticCatalog.VatBreakdown)[1].Get("BT-119"));
    }

    [Fact]
    public async Task A_credit_note_is_written_as_UBL_CreditNote()
    {
        var source = await ReadAsync("peppol-creditnote.xml");
        source.Semantic.Set("BT-10", "991-12345-67");
        var xml = System.Text.Encoding.UTF8.GetString(XRechnungWriter.Write(source.Semantic, InvoiceSyntax.Ubl));
        Assert.Contains("<CreditNote", xml, StringComparison.Ordinal);
        Assert.Contains("<cac:CreditNoteLine>", xml, StringComparison.Ordinal);
        Assert.Contains("<cbc:CreditedQuantity unitCode=\"C62\">4</cbc:CreditedQuantity>", xml, StringComparison.Ordinal);
    }
}
