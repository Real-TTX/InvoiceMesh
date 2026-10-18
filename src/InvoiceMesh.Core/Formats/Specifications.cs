namespace InvoiceMesh.Core.Formats;

/// <summary>The XML syntax of a structured invoice. The standard, the profile and the version are separate questions (see <see cref="Specification"/>).</summary>
public enum InvoiceSyntax
{
    None,
    Ubl,
    Cii,

    /// <summary>The CII of ZUGFeRD 1.0 (CrossIndustryDocument, D13B).</summary>
    CiiZugferdV1,

    /// <summary>XML that is no known invoice syntax.</summary>
    OtherXml,
}

/// <summary>The rule sets InvoiceMesh can apply; a specification says which of them hold for its invoices.</summary>
[Flags]
public enum RuleSets
{
    None = 0,
    En16931 = 1,
    XRechnung = 2,
    Peppol = 4,
}

/// <summary>
/// A standard, profile and version, recognised by the specification identifier of an invoice (BT-24: <c>cbc:CustomizationID</c> in UBL,
/// <c>GuidelineSpecifiedDocumentContextParameter</c> in CII). The built-in table covers XRechnung, Peppol BIS Billing, ZUGFeRD / Factur-X and EN 16931;
/// an administrator adds national formats and new versions as data (Einstellungen &gt; Formate), without a change of the program.
/// </summary>
/// <param name="Identifier">The identifier, or its beginning when <paramref name="IsPrefix"/> is set.</param>
/// <param name="Standard">"XRechnung", "Peppol BIS Billing", "ZUGFeRD / Factur-X", "EN 16931" ...</param>
/// <param name="Profile">"XRechnung (CIUS)", "EN 16931 (Comfort)", "Basic" ...</param>
/// <param name="Version">"3.0", "2.3", "1.0" ...</param>
/// <param name="Syntaxes">The syntaxes the profile exists in (empty: any).</param>
/// <param name="IsBuiltIn">False for the entries an administrator added.</param>
public sealed record Specification(
    string Identifier,
    bool IsPrefix,
    string Standard,
    string Profile,
    string Version,
    RuleSets Rules,
    IReadOnlyList<InvoiceSyntax> Syntaxes,
    string? Note = null,
    bool IsBuiltIn = true)
{
    public bool Matches(string identifier)
        => IsPrefix ? identifier.StartsWith(Identifier, StringComparison.OrdinalIgnoreCase) : string.Equals(identifier, Identifier, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The table of known specification identifiers: built in plus what the administrator added (those win, so that a version can be described better).</summary>
public sealed class SpecificationRegistry
{
    public const string XRechnung30 = "urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0";
    public const string PeppolBis3 = "urn:cen.eu:en16931:2017#compliant#urn:fdc:peppol.eu:2017:poacc:billing:3.0";
    public const string PeppolProcess = "urn:fdc:peppol.eu:2017:poacc:billing:01:1.0";
    public const string En16931 = "urn:cen.eu:en16931:2017";

    private static readonly InvoiceSyntax[] Ubl = [InvoiceSyntax.Ubl];
    private static readonly InvoiceSyntax[] Cii = [InvoiceSyntax.Cii];
    private static readonly InvoiceSyntax[] Both = [InvoiceSyntax.Ubl, InvoiceSyntax.Cii];
    private static readonly InvoiceSyntax[] CiiV1 = [InvoiceSyntax.CiiZugferdV1];

    /// <summary>The built-in identifiers, the most specific first (the first that matches wins).</summary>
    public static readonly IReadOnlyList<Specification> BuiltIn =
    [
        new(XRechnung30 + "#conformant#urn:xeinkauf.de:kosit:extension:xrechnung_3.0", false, "XRechnung", "XRechnung Extension", "3.0", RuleSets.En16931 | RuleSets.XRechnung, Both),
        new(XRechnung30, false, "XRechnung", "XRechnung (CIUS)", "3.0", RuleSets.En16931 | RuleSets.XRechnung, Both),
        new("urn:cen.eu:en16931:2017#compliant#urn:xoev-de:kosit:standard:xrechnung_2.3", false, "XRechnung", "XRechnung (CIUS)", "2.3", RuleSets.En16931 | RuleSets.XRechnung, Both),
        new("urn:cen.eu:en16931:2017#compliant#urn:xoev-de:kosit:standard:xrechnung_2.2", false, "XRechnung", "XRechnung (CIUS)", "2.2", RuleSets.En16931 | RuleSets.XRechnung, Both),
        new("urn:cen.eu:en16931:2017#compliant#urn:xoev-de:kosit:standard:xrechnung_2.1", false, "XRechnung", "XRechnung (CIUS)", "2.1", RuleSets.En16931 | RuleSets.XRechnung, Both),
        new("urn:cen.eu:en16931:2017#compliant#urn:xoev-de:kosit:standard:xrechnung_2.0", false, "XRechnung", "XRechnung (CIUS)", "2.0", RuleSets.En16931 | RuleSets.XRechnung, Both),
        new("urn:cen.eu:en16931:2017#compliant#urn:xoev-de:kosit:standard:xrechnung_1.2", false, "XRechnung", "XRechnung (CIUS)", "1.2", RuleSets.En16931 | RuleSets.XRechnung, Both, "Veraltete Version"),
        new("urn:cen.eu:en16931:2017#compliant#urn:xoev-de:kosit:standard:xrechnung", true, "XRechnung", "XRechnung (CIUS)", "älter", RuleSets.En16931 | RuleSets.XRechnung, Both, "Version nicht eindeutig"),
        new(PeppolBis3, false, "Peppol BIS Billing", "Billing (UBL)", "3.0", RuleSets.En16931 | RuleSets.Peppol, Ubl),
        new("urn:peppol:pint:billing-1", true, "Peppol PINT", "Billing", "1.x", RuleSets.None, Ubl, "Erkannt; geprüft werden nur die allgemeinen Regeln"),
        new("urn:cen.eu:en16931:2017#compliant#urn:fdc:nen.nl:nlcius:v1.0", false, "NLCIUS", "NLCIUS (Niederlande)", "1.0", RuleSets.En16931, Both),
        new("urn:cen.eu:en16931:2017#conformant#urn:factur-x.eu:1p0:extended", false, "ZUGFeRD / Factur-X", "Extended", "2.x / 1.0", RuleSets.En16931, Cii),
        new("urn:cen.eu:en16931:2017#compliant#urn:factur-x.eu:1p0:basic", false, "ZUGFeRD / Factur-X", "Basic", "2.x / 1.0", RuleSets.En16931, Cii),
        new("urn:factur-x.eu:1p0:basicwl", false, "ZUGFeRD / Factur-X", "Basic WL", "2.x / 1.0", RuleSets.None, Cii, "Ohne Positionen: keine vollständige Rechnung nach EN 16931"),
        new("urn:factur-x.eu:1p0:minimum", false, "ZUGFeRD / Factur-X", "Minimum", "2.x / 1.0", RuleSets.None, Cii, "Nur Kopfdaten: keine vollständige Rechnung nach EN 16931"),
        new("urn:cen.eu:en16931:2017#conformant#urn:zugferd.de:2p0:extended", false, "ZUGFeRD", "Extended", "2.0", RuleSets.En16931, Cii),
        new("urn:cen.eu:en16931:2017#compliant#urn:zugferd.de:2p0:basic", false, "ZUGFeRD", "Basic", "2.0", RuleSets.En16931, Cii),
        new("urn:zugferd.de:2p0:basicwl", false, "ZUGFeRD", "Basic WL", "2.0", RuleSets.None, Cii),
        new("urn:zugferd.de:2p0:minimum", false, "ZUGFeRD", "Minimum", "2.0", RuleSets.None, Cii),
        new("urn:ferd:CrossIndustryDocument:invoice:1p0:extended", false, "ZUGFeRD", "Extended", "1.0", RuleSets.None, CiiV1, "ZUGFeRD 1.0: Felder als Rohdaten"),
        new("urn:ferd:CrossIndustryDocument:invoice:1p0:comfort", false, "ZUGFeRD", "Comfort", "1.0", RuleSets.None, CiiV1, "ZUGFeRD 1.0: Felder als Rohdaten"),
        new("urn:ferd:CrossIndustryDocument:invoice:1p0:basic", false, "ZUGFeRD", "Basic", "1.0", RuleSets.None, CiiV1, "ZUGFeRD 1.0: Felder als Rohdaten"),
        new(En16931, false, "EN 16931", "EN 16931 (Comfort)", "2017", RuleSets.En16931, Both),
        new(En16931 + "#", true, "EN 16931", "Unbekannte Spezialisierung", "2017", RuleSets.En16931, Both, "Eine CIUS oder Erweiterung, die InvoiceMesh nicht kennt: geprüft wird nach EN 16931"),
    ];

    private readonly IReadOnlyList<Specification> _all;

    public SpecificationRegistry(IEnumerable<Specification>? custom = null)
        => _all = [.. (custom ?? []).Select(c => c with { IsBuiltIn = false }), .. BuiltIn];

    public IReadOnlyList<Specification> All => _all;

    public Specification? Find(string? identifier)
        => string.IsNullOrWhiteSpace(identifier) ? null : _all.FirstOrDefault(s => s.Matches(identifier.Trim()));
}
