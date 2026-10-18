using System.Xml;
using InvoiceMesh.Core.Formats;
using InvoiceMesh.Core.Xml;

namespace InvoiceMesh.Core.Extraction;

/// <summary>What text recognition made of a scan or an image.</summary>
public sealed record OcrResult(string Text, double? MeanConfidence, string Engine);

/// <summary>The text recognition of the installation (Tesseract in the container). Not part of the core: it runs a program.</summary>
public interface IOcrEngine
{
    /// <summary>False when the program is not installed: scans are then reported as not readable instead of failing.</summary>
    bool IsAvailable { get; }

    Task<OcrResult> RecognizeAsync(byte[] bytes, FileKind kind, CancellationToken ct);
}

/// <summary>
/// The first step of processing a file: what is it, and what is in it. The order is fixed: structured data first (an XML file, the XML inside a hybrid PDF),
/// then the text layer of a PDF, then text recognition of a scan or an image. The file name is only used to say things in words, never to decide.
/// </summary>
public sealed class InvoiceReader(SpecificationRegistry registry, IOcrEngine? ocr = null)
{
    public const long MaxBytes = 50L * 1024 * 1024;

    public async Task<ExtractionResult> ReadAsync(byte[] bytes, string fileName, CancellationToken ct = default)
    {
        var evidence = new List<string>();
        var messages = new List<ExtractionMessage>();
        var kind = Sniff(bytes, out var mediaType, out var imageFormat);
        var format = new DetectedFormat { Kind = kind, MediaType = mediaType, ImageFormat = imageFormat, Evidence = evidence };
        evidence.Add(kind switch
        {
            FileKind.Pdf => "Die Datei beginnt mit %PDF → PDF.",
            FileKind.Image => $"Die Datei beginnt mit der Signatur von {imageFormat} → Bild.",
            FileKind.Xml => "Die Datei beginnt mit „<“ → XML.",
            _ => "Der Inhalt passt zu keinem unterstützten Format (PDF, PNG, JPEG, TIFF, XML).",
        });

        var extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        if (extension.Length > 0 && !ExtensionFits(kind, extension))
        {
            evidence.Add($"Hinweis: Die Endung „.{extension}“ passt nicht zum Inhalt. Entschieden hat der Inhalt.");
        }

        switch (kind)
        {
            case FileKind.Xml:
                return ReadXml(bytes, format, evidence, messages, insidePdf: false, ExtractionMethod.Xml);

            case FileKind.Pdf:
                return await ReadPdfAsync(bytes, format, evidence, messages, ct);

            case FileKind.Image:
                return await RecognizeAsync(bytes, format, evidence, messages, ct);

            default:
                messages.Add(new ExtractionMessage(MessageSeverity.Error, "FORMAT-UNSUPPORTED",
                    "Dieses Dateiformat wird nicht unterstützt. Unterstützt werden PDF, PNG, JPEG, TIFF und XML (UBL, CII)."));
                return new ExtractionResult { Format = format, Method = ExtractionMethod.None, Messages = messages };
        }
    }

    public static FileKind Sniff(ReadOnlySpan<byte> b, out string mediaType, out string? imageFormat)
    {
        imageFormat = null;
        if (b.Length >= 5 && b[0] == '%' && b[1] == 'P' && b[2] == 'D' && b[3] == 'F' && b[4] == '-')
        {
            mediaType = "application/pdf";
            return FileKind.Pdf;
        }

        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G')
        {
            mediaType = "image/png";
            imageFormat = "PNG";
            return FileKind.Image;
        }

        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
        {
            mediaType = "image/jpeg";
            imageFormat = "JPEG";
            return FileKind.Image;
        }

        if (b.Length >= 4 && ((b[0] == 'I' && b[1] == 'I' && b[2] == 42 && b[3] == 0) || (b[0] == 'M' && b[1] == 'M' && b[2] == 0 && b[3] == 42)))
        {
            mediaType = "image/tiff";
            imageFormat = "TIFF";
            return FileKind.Image;
        }

        if (XmlNames.LooksLikeXml(b))
        {
            mediaType = "application/xml";
            return FileKind.Xml;
        }

        mediaType = "application/octet-stream";
        return FileKind.Unknown;
    }

    private static bool ExtensionFits(FileKind kind, string extension) => kind switch
    {
        FileKind.Pdf => extension == "pdf",
        FileKind.Image => extension is "png" or "jpg" or "jpeg" or "tif" or "tiff",
        FileKind.Xml => extension is "xml" or "ubl" or "cii",
        _ => false,
    };

    private ExtractionResult ReadXml(byte[] bytes, DetectedFormat format, List<string> evidence, List<ExtractionMessage> messages, bool insidePdf, ExtractionMethod method, string? text = null)
    {
        System.Xml.Linq.XDocument xml;
        try
        {
            xml = XmlNames.Load(bytes);
        }
        catch (XmlException ex)
        {
            messages.Add(new ExtractionMessage(MessageSeverity.Error, "XML-MALFORMED", $"Das XML ist nicht wohlgeformt: {ex.Message}"));
            return new ExtractionResult { Format = format, Method = ExtractionMethod.None, Messages = messages, Text = text };
        }

        var root = xml.Root!;
        var classification = XmlInvoiceReader.Classify(root);
        var described = XmlInvoiceReader.Describe(classification, registry, format, evidence, insidePdf);
        var raw = XmlInvoiceReader.Flatten(root);
        var semantic = XmlInvoiceReader.ReadSemantic(root, classification.Syntax, messages);
        switch (classification.Syntax)
        {
            case InvoiceSyntax.OtherXml:
                messages.Add(new ExtractionMessage(MessageSeverity.Error, "XML-NOT-INVOICE",
                    $"Das XML (Wurzelelement „{root.Name.LocalName}“) ist keine Rechnung in UBL oder CII. Seine Felder stehen als Rohdaten für ein Mapping bereit."));
                break;
            case InvoiceSyntax.CiiZugferdV1:
                messages.Add(new ExtractionMessage(MessageSeverity.Warning, "ZUGFERD-V1",
                    "ZUGFeRD 1.0 wird erkannt, seine Felder werden aber nicht in das semantische Modell übernommen. Sie stehen als Rohdaten für ein Mapping bereit."));
                break;
        }

        if (described.Note is not null)
        {
            messages.Add(new ExtractionMessage(MessageSeverity.Info, "PROFILE-NOTE", $"{described.Standard} {described.Profile}: {described.Note}."));
        }

        return new ExtractionResult
        {
            Format = described,
            Method = method,
            SourceFields = raw,
            Semantic = semantic,
            Messages = messages,
            Text = text,
            EmbeddedXml = insidePdf ? bytes : null,
        };
    }

    private async Task<ExtractionResult> ReadPdfAsync(byte[] bytes, DetectedFormat format, List<string> evidence, List<ExtractionMessage> messages, CancellationToken ct)
    {
        PdfContent pdf;
        try
        {
            pdf = PdfContent.Read(bytes);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            messages.Add(new ExtractionMessage(MessageSeverity.Error, "PDF-BROKEN", $"Das PDF lässt sich nicht lesen: {ex.Message}"));
            return new ExtractionResult { Format = format, Method = ExtractionMethod.None, Messages = messages };
        }

        format = format with { PageCount = pdf.PageCount, HasTextLayer = pdf.HasTextLayer };
        evidence.Add($"{pdf.PageCount} Seite(n), {(pdf.HasTextLayer ? "mit Textebene" : "ohne nennenswerte Textebene (Scan)")}.");

        // Structured data inside come first: the attachment with a known name, else any XML attachment that is an invoice.
        var candidates = pdf.Attachments
            .OrderBy(a => Array.FindIndex(PdfContent.InvoiceAttachmentNames, n => string.Equals(n, a.Name, StringComparison.OrdinalIgnoreCase)) is var i && i >= 0 ? i : 99)
            .Where(a => XmlNames.LooksLikeXml(a.Bytes))
            .ToList();
        if (pdf.Attachments.Count > 0)
        {
            evidence.Add($"Anhänge im PDF: {string.Join(", ", pdf.Attachments.Select(a => a.Name))}.");
        }

        foreach (var (name, xmlBytes) in candidates)
        {
            try
            {
                var root = XmlNames.Load(xmlBytes).Root!;
                if (XmlInvoiceReader.Classify(root).Syntax is InvoiceSyntax.OtherXml)
                {
                    continue;
                }
            }
            catch (XmlException)
            {
                messages.Add(new ExtractionMessage(MessageSeverity.Warning, "PDF-XML-MALFORMED", $"Der Anhang „{name}“ ist kein wohlgeformtes XML und wurde übergangen."));
                continue;
            }

            evidence.Add($"Der Anhang „{name}“ ist eine strukturierte Rechnung → hybride Rechnung, die Daten werden aus dem XML gelesen.");
            var hybrid = format with { IsHybrid = true, EmbeddedFileName = name };
            return ReadXml(xmlBytes, hybrid, evidence, messages, insidePdf: true, ExtractionMethod.EmbeddedXml, pdf.Text);
        }

        if (pdf.HasTextLayer)
        {
            evidence.Add("Keine strukturierten Daten im PDF → die Angaben werden aus der Textebene gelesen.");
            var result = TextInvoiceExtractor.Extract(pdf.Text);
            messages.AddRange(result.Messages);
            return new ExtractionResult
            {
                Format = format,
                Method = ExtractionMethod.PdfText,
                SourceFields = result.RawFields,
                Semantic = result.Semantic,
                Text = pdf.Text,
                Messages = messages,
            };
        }

        evidence.Add("Keine strukturierten Daten und kaum Text → Texterkennung (OCR).");
        return await RecognizeAsync(bytes, format, evidence, messages, ct);
    }

    private async Task<ExtractionResult> RecognizeAsync(byte[] bytes, DetectedFormat format, List<string> evidence, List<ExtractionMessage> messages, CancellationToken ct)
    {
        if (ocr is null || !ocr.IsAvailable)
        {
            messages.Add(new ExtractionMessage(MessageSeverity.Error, "OCR-UNAVAILABLE", "Die Texterkennung ist auf diesem System nicht verfügbar; der Scan kann nicht gelesen werden."));
            return new ExtractionResult { Format = format, Method = ExtractionMethod.None, Messages = messages };
        }

        OcrResult recognized;
        try
        {
            recognized = await ocr.RecognizeAsync(bytes, format.Kind, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            messages.Add(new ExtractionMessage(MessageSeverity.Error, "OCR-FAILED", $"Die Texterkennung ist fehlgeschlagen: {ex.Message}"));
            return new ExtractionResult { Format = format, Method = ExtractionMethod.None, Messages = messages };
        }

        evidence.Add($"Texterkennung mit {recognized.Engine}.");
        var result = TextInvoiceExtractor.Extract(recognized.Text, recognized.MeanConfidence);
        messages.AddRange(result.Messages);
        return new ExtractionResult
        {
            Format = format,
            Method = ExtractionMethod.Ocr,
            SourceFields = result.RawFields,
            Semantic = result.Semantic,
            Text = recognized.Text,
            OcrConfidence = recognized.MeanConfidence,
            OcrEngine = recognized.Engine,
            Messages = messages,
        };
    }
}
