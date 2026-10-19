using System.Text;
using System.Xml;
using InvoiceMesh.Core.Formats;
using InvoiceMesh.Core.Xml;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using InvoiceMesh.UiKit;
using InvoiceMesh.Storage;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceMesh.Pages.Invoices;

/// <summary>What the preview of the files page shows: a PDF in a frame, or a text (XML, JSON).</summary>
public sealed record FilePreview(string Key, string Title, string? Sub, string? FrameUrl, string? Text);

/// <summary>
/// The outputs of a document, each on its own: the original, the embedded XML, the normalized data, the extracted data, the validation report and the files
/// InvoiceMesh made (XRechnung, PDF view), each with a preview. Files made from an older revision are marked as such.
/// </summary>
public sealed class FilesModel(InvoiceService invoices, FileStoreService files, NavCounts counts) : InvoicePage(invoices)
{
    protected override string TabKey => "files";

    /// <summary>Which file the preview shows: embedded, extracted, normalized, report, or g{id} for a generated file.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Show { get; set; }

    public FilePreview? Preview { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        // Without a choice the preview shows the XRechnung, else the PDF view: what InvoiceMesh made.
        var key = Show ?? Ctx.Files.OrderBy(f => f.Kind is GeneratedKind.XRechnungUbl or GeneratedKind.XRechnungCii ? 0 : 1).Select(f => $"g{f.Id}").FirstOrDefault();
        Preview = key is null ? null : await PreviewAsync(key, HttpContext.RequestAborted);
        return Page();
    }

    public bool IsCurrent(GeneratedFile f) => f.RevisionId is null || f.RevisionId == Doc.CurrentRevisionId;

    /// <summary>The link that shows a file in the preview.</summary>
    public string PreviewUrl(string key) => $"/Invoices/Files?id={Doc.Id}&show={key}#vorschau";

    public bool IsShown(string key) => Preview?.Key == key;

    private async Task<FilePreview?> PreviewAsync(string key, CancellationToken ct)
    {
        switch (key)
        {
            case "embedded" when Doc.EmbeddedXmlFile is { } xml:
                return new FilePreview(key, "Eingebettetes XML", xml.FileName, null, PrettyXml(await files.ReadAllAsync(xml, ct)));
            case "extracted" when Ctx.ExtractedJson() is { } json:
                return new FilePreview(key, "Ausgelesene Daten", "JSON", null, json);
            case "normalized" when Ctx.NormalizedJson() is { } json:
                return new FilePreview(key, "Normalisierte Daten", $"JSON nach „{Ctx.Report?.SchemaName}“ v{Ctx.Report?.SchemaVersion}", null, json);
            case "report" when Ctx.ValidationReport is not null:
                return new FilePreview(key, "Validierungsbericht", "PDF", $"/files/{Doc.Id}/report.pdf", null);
        }

        if (!key.StartsWith('g') || !long.TryParse(key[1..], out var id) || Ctx.Files.FirstOrDefault(f => f.Id == id) is not { StoredFile: { } stored } generated)
        {
            return null;
        }

        return generated.Kind is GeneratedKind.XRechnungUbl or GeneratedKind.XRechnungCii
            ? new FilePreview(key, Labels.Of(generated.Kind), generated.Check, null, PrettyXml(await files.ReadAllAsync(stored, ct)))
            : new FilePreview(key, Labels.Of(generated.Kind), generated.Check, $"/files/generated/{generated.Id}", null);
    }

    private static string PrettyXml(byte[] bytes)
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

    private async Task<IActionResult> RunAsync(Func<InvoiceDocument, Task> action, string done)
    {
        var doc = await TrackedAsync();
        if (doc is null)
        {
            return NotFound();
        }

        try
        {
            await action(doc);
            counts.Invalidate();
            Flash(done);
        }
        catch (Exception ex) when (ex is InvoiceException or DefinitionException)
        {
            FlashError(ex.Message);
        }

        return Back("/Invoices/Files");
    }

    public Task<IActionResult> OnPostXRechnungAsync(string? syntax)
        => RunAsync(d => Invoices.GenerateXRechnungAsync(d, syntax == "cii" ? InvoiceSyntax.Cii : InvoiceSyntax.Ubl), "Die XRechnung wurde erzeugt und gegengeprüft.");

    public Task<IActionResult> OnPostPdfAsync() => RunAsync(d => Invoices.GeneratePdfAsync(d), "Die PDF-Ansicht wurde erzeugt.");
}
