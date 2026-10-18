using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using InvoiceMesh.UiKit;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceMesh.Pages.Invoices;

/// <summary>
/// The pages of one document share this: the id in the address, the loaded context, the head with the breadcrumb and the tabs (each a page of its own:
/// Übersicht, Original, Daten, Validierung, Dateien, Verlauf).
/// </summary>
public abstract class InvoicePage(InvoiceService invoices) : PageModelBase
{
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    public InvoiceContext Ctx { get; private set; } = null!;

    public InvoiceDocument Doc => Ctx.Doc;

    public LinkTabsModel Tabs { get; private set; } = null!;

    protected InvoiceService Invoices => invoices;

    protected abstract string TabKey { get; }

    protected async Task<bool> LoadAsync()
    {
        var ctx = await invoices.LoadAsync(Id, HttpContext.RequestAborted);
        if (ctx is null)
        {
            return false;
        }

        Ctx = ctx;
        var title = Doc.InvoiceNumber is { } n ? $"{PdfTitle(Doc.TypeCode)} {n}" : Doc.FileName;
        var sub = string.Join(" · ", new[] { Doc.SellerName, Doc.FormatSummary, Fmt.Moment(Doc.ReceivedDate) }.Where(s => !string.IsNullOrWhiteSpace(s)));
        Head(title, sub, ("Belege", "/Invoices"), (title, null));
        ViewData["ActiveHref"] = "/Invoices";
        var findings = Ctx.ValidationReport is { } r ? r.Errors + r.Warnings : (int?)null;
        Tabs = new LinkTabsModel("Bereiche des Belegs",
        [
            new("overview", "Übersicht", $"/Invoices/Details?id={Id}", "dashboard"),
            new("original", "Original", $"/Invoices/Original?id={Id}", "file"),
            new("data", "Daten", $"/Invoices/Data?id={Id}", "columns"),
            new("validation", "Validierung", $"/Invoices/Validation?id={Id}", "shield", findings is > 0 ? findings : null),
            new("files", "Dateien", $"/Invoices/Files?id={Id}", "download", Ctx.Files.Count > 0 ? Ctx.Files.Count : null),
            new("history", "Verlauf", $"/Invoices/History?id={Id}", "history"),
        ], TabKey);
        return true;
    }

    private static string PdfTitle(string? typeCode) => InvoicePdf.TypeLabel(typeCode);

    /// <summary>Loads the document for a handler that changes it; null when it is not there.</summary>
    protected async Task<InvoiceDocument?> TrackedAsync() => (await invoices.LoadAsync(Id, HttpContext.RequestAborted))?.Doc;

    protected IActionResult Back(string page = "/Invoices/Details") => RedirectToPage(page, new { id = Id });
}
