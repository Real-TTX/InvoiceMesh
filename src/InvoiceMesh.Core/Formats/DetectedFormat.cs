namespace InvoiceMesh.Core.Formats;

/// <summary>What a file is, by its content (never by its name): a PDF, an image, XML, or something else.</summary>
public enum FileKind
{
    Unknown,
    Pdf,
    Image,
    Xml,
}

/// <summary>
/// What InvoiceMesh found out about a file, in separate answers: the kind of file, the XML syntax and its version, the standard, the profile and its version,
/// whether it is a hybrid (a PDF with the XML inside) or a scan. <see cref="Evidence"/> says why, in words, so that a person can follow the decision.
/// </summary>
public sealed record DetectedFormat
{
    public FileKind Kind { get; init; }

    public string MediaType { get; init; } = "application/octet-stream";

    /// <summary>PNG, JPEG, TIFF for an image.</summary>
    public string? ImageFormat { get; init; }

    public InvoiceSyntax Syntax { get; init; } = InvoiceSyntax.None;

    /// <summary>"UBL 2.1", "CII D16B".</summary>
    public string? SyntaxVersion { get; init; }

    /// <summary>"Invoice" or "CreditNote" (the root element of UBL; CII has one root for both).</summary>
    public string? DocumentKind { get; init; }

    /// <summary>BT-24 as the document says it.</summary>
    public string? SpecificationId { get; init; }

    /// <summary>BT-23 as the document says it.</summary>
    public string? BusinessProcess { get; init; }

    public string? Standard { get; init; }

    public string? Profile { get; init; }

    public string? Version { get; init; }

    public RuleSets Rules { get; init; }

    /// <summary>A PDF with structured invoice data inside (ZUGFeRD, Factur-X, an XRechnung attached to a PDF).</summary>
    public bool IsHybrid { get; init; }

    /// <summary>The name of the attachment the XML was taken from ("factur-x.xml").</summary>
    public string? EmbeddedFileName { get; init; }

    /// <summary>A PDF whose pages carry text (false for a scan).</summary>
    public bool HasTextLayer { get; init; }

    public int? PageCount { get; init; }

    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>A remark of the specification table ("Nur Kopfdaten ...").</summary>
    public string? Note { get; init; }

    public bool IsStructured => Syntax is InvoiceSyntax.Ubl or InvoiceSyntax.Cii;

    /// <summary>One line for lists: "XRechnung 3.0 · UBL 2.1", "ZUGFeRD / Factur-X EN 16931 (Comfort) · PDF mit XML", "PDF (Scan)".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Standard is not null)
            {
                parts.Add(Profile is null || Standard == Profile ? $"{Standard} {Version}".Trim()
                    : Profile.Contains(Standard, StringComparison.OrdinalIgnoreCase) ? $"{Profile} {Version}".Trim()
                    : $"{Standard} {Profile}" + (Version is null ? "" : $" {Version}"));
            }

            if (SyntaxVersion is not null)
            {
                parts.Add(SyntaxVersion);
            }

            parts.Add(Kind switch
            {
                FileKind.Pdf when IsHybrid => "PDF mit XML",
                FileKind.Pdf when !HasTextLayer => "PDF (Scan)",
                FileKind.Pdf => "PDF",
                FileKind.Image => ImageFormat ?? "Bild",
                FileKind.Xml => "XML",
                _ => "Unbekannt",
            });
            return string.Join(" · ", parts.Distinct());
        }
    }

    /// <summary>The short code of the lists and filters: UBL, CII, PDF, Scan, Bild, XML, ?</summary>
    public string ShortCode => Kind switch
    {
        FileKind.Xml or FileKind.Pdf when IsStructured && IsHybrid => Syntax == InvoiceSyntax.Ubl ? "PDF+UBL" : "PDF+CII",
        FileKind.Xml when Syntax == InvoiceSyntax.Ubl => "UBL",
        FileKind.Xml when Syntax is InvoiceSyntax.Cii or InvoiceSyntax.CiiZugferdV1 => "CII",
        FileKind.Xml => "XML",
        FileKind.Pdf when !HasTextLayer => "Scan",
        FileKind.Pdf => "PDF",
        FileKind.Image => "Bild",
        _ => "?",
    };
}
