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

/// <summary>
/// The outputs of a document, each on its own: the original, the embedded XML, the normalized data, the extracted data, the validation report and the files
/// InvoiceMesh made (XRechnung, PDF view), with a preview of the XML. Files made from an older revision are marked as such.
/// </summary>
public sealed class FilesModel(InvoiceService invoices, FileStoreService files, NavCounts counts) : InvoicePage(invoices)
{
    protected override string TabKey => "files";

    [BindProperty(SupportsGet = true)]
    public long? Show { get; set; }

    public GeneratedFile? Shown { get; private set; }

    public string? ShownXml { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        Shown = Show is { } s ? Ctx.Files.FirstOrDefault(f => f.Id == s) : Ctx.Files.FirstOrDefault(f => f.Kind is GeneratedKind.XRechnungUbl or GeneratedKind.XRechnungCii);
        if (Shown is { StoredFile: { } stored } && Shown.Kind is GeneratedKind.XRechnungUbl or GeneratedKind.XRechnungCii)
        {
            var bytes = await files.ReadAllAsync(stored, HttpContext.RequestAborted);
            try
            {
                ShownXml = XmlNames.Load(bytes).ToString();
            }
            catch (XmlException)
            {
                ShownXml = Encoding.UTF8.GetString(bytes);
            }
        }

        return Page();
    }

    public bool IsCurrent(GeneratedFile f) => f.RevisionId is null || f.RevisionId == Doc.CurrentRevisionId;

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
