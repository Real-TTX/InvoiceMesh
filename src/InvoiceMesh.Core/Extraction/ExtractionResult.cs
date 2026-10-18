using InvoiceMesh.Core.Formats;
using InvoiceMesh.Core.Semantics;

namespace InvoiceMesh.Core.Extraction;

/// <summary>How the data of an invoice were obtained.</summary>
public enum ExtractionMethod
{
    None,

    /// <summary>A structured XML file (UBL, CII).</summary>
    Xml,

    /// <summary>The XML inside a hybrid PDF (ZUGFeRD, Factur-X).</summary>
    EmbeddedXml,

    /// <summary>The text layer of a PDF without structured data.</summary>
    PdfText,

    /// <summary>Text recognition of a scan or an image.</summary>
    Ocr,
}

public enum MessageSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>A remark of the reader: what it could not read, what it chose, what it guessed.</summary>
public sealed record ExtractionMessage(MessageSeverity Severity, string Code, string Text);

/// <summary>
/// A raw field of the source, as it is: an XML path (<c>/Invoice/cac:InvoiceLine[2]/cbc:ID</c>) or a label found in the text (<c>text:Rechnungsnummer</c>).
/// Mappings can use these besides the business terms, for what the standard does not name.
/// </summary>
public sealed record SourceField(string Path, string Value, Confidence Confidence = Confidence.High)
{
    /// <summary>The path without the indexes of repeated elements, the form a mapping refers to ("/Invoice/cac:InvoiceLine/cbc:ID").</summary>
    public string Pattern => StripIndexes(Path);

    public static string StripIndexes(string path)
    {
        if (!path.Contains('['))
        {
            return path;
        }

        var sb = new System.Text.StringBuilder(path.Length);
        var depth = 0;
        foreach (var c in path)
        {
            if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                depth--;
            }
            else if (depth == 0)
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}

/// <summary>Everything a reader got out of a file. The original file is never part of it: it stays as it came, elsewhere.</summary>
public sealed class ExtractionResult
{
    public required DetectedFormat Format { get; init; }

    public ExtractionMethod Method { get; init; }

    public List<SourceField> SourceFields { get; init; } = [];

    public SemanticDocument Semantic { get; init; } = new();

    /// <summary>The text of a PDF or the recognised text of a scan (null for XML).</summary>
    public string? Text { get; init; }

    /// <summary>The mean confidence of the text recognition, 0 to 100.</summary>
    public double? OcrConfidence { get; init; }

    /// <summary>The engine that recognised the text ("Tesseract 5.3, deu+eng").</summary>
    public string? OcrEngine { get; init; }

    /// <summary>The XML inside a hybrid PDF, kept so that it can be stored next to the original.</summary>
    public byte[]? EmbeddedXml { get; init; }

    public List<ExtractionMessage> Messages { get; init; } = [];

    public bool HasErrors => Messages.Any(m => m.Severity == MessageSeverity.Error);
}
