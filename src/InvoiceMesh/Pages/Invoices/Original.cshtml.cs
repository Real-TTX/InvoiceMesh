using System.Text;
using System.Xml;
using InvoiceMesh.Core.Formats;
using InvoiceMesh.Core.Xml;
using InvoiceMesh.Services;
using InvoiceMesh.Storage;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceMesh.Pages.Invoices;

/// <summary>The original as it came (a PDF or an image shown by the browser, XML as text), the XML found inside a hybrid PDF, the text that was read, and how the format was recognised.</summary>
public sealed class OriginalModel(InvoiceService invoices, FileStoreService files) : InvoicePage(invoices)
{
    private const int MaxShownBytes = 2 * 1024 * 1024;

    protected override string TabKey => "original";

    public string? OriginalXml { get; private set; }

    public string? EmbeddedXml { get; private set; }

    public bool IsPdf => Doc.FileKind == nameof(FileKind.Pdf);

    public bool IsImage => Doc.FileKind == nameof(FileKind.Image);

    public bool IsTiff => Doc.ContentType == "image/tiff";

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        if (Doc.FileKind == nameof(FileKind.Xml) && Doc.SizeBytes <= MaxShownBytes)
        {
            OriginalXml = Pretty(await files.ReadAllAsync(Doc.OriginalFile!, HttpContext.RequestAborted));
        }

        if (Doc.EmbeddedXmlFile is { SizeBytes: <= MaxShownBytes } embedded)
        {
            EmbeddedXml = Pretty(await files.ReadAllAsync(embedded, HttpContext.RequestAborted));
        }

        return Page();
    }

    /// <summary>The XML indented for reading; when it is not well-formed, the text as it is.</summary>
    private static string Pretty(byte[] bytes)
    {
        try
        {
            return XmlNames.Load(bytes).ToString();
        }
        catch (XmlException)
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
