using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceMesh.Pages.Share;

/// <summary>
/// The page behind a share link (the role "Anonym"): one document, read only: its main data, its positions, the result of the validation and the files the
/// link allows. It says in words why it cannot show it (a link that has run out is no secret).
/// </summary>
[AllowAnonymous]
public sealed class IndexModel(ShareService shares, InvoiceService invoices, TimeProvider clock) : PageModelBase
{
    [BindProperty(SupportsGet = true)]
    public Guid Token { get; set; }

    [BindProperty]
    public string? Password { get; set; }

    public ShareLink Link { get; private set; } = null!;

    public ShareState State { get; private set; } = ShareState.Unknown;

    public bool NeedsPassword { get; private set; }

    public bool Locked { get; private set; }

    public bool CanShow => State == ShareState.Active && !NeedsPassword && Ctx is not null;

    public InvoiceContext? Ctx { get; private set; }

    public string RemainingText => ShareService.Remaining(Link, clock.GetUtcNow().UtcDateTime);

    public async Task<IActionResult> OnGetAsync()
    {
        if (await InitAsync())
        {
            await shares.CountViewAsync(Link, HttpContext.RequestAborted);
            Ctx = await invoices.LoadAsync(Link.InvoiceId, HttpContext.RequestAborted);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostPasswordAsync()
    {
        await InitAsync();
        if (State != ShareState.Active)
        {
            return Page();
        }

        if (!NeedsPassword || shares.TryPassword(Link, Password, HttpContext))
        {
            return RedirectToPage(new { token = Token });
        }

        Locked = shares.IsLocked(Link);
        ModelState.AddModelError(nameof(Password), Locked ? "Zu viele falsche Versuche. Bitte warte einige Minuten." : "Das Passwort stimmt nicht.");
        return Page();
    }

    private async Task<bool> InitAsync()
    {
        ViewData["Wide"] = true;
        ViewData["Title"] = "Beleg";
        var resolution = await shares.ResolveAsync(Token, HttpContext.RequestAborted);
        State = resolution.State;
        if (resolution.Link is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return false;
        }

        Link = resolution.Link;
        if (State != ShareState.Active)
        {
            Response.StatusCode = StatusCodes.Status410Gone;
            return false;
        }

        NeedsPassword = shares.NeedsPassword(Link, HttpContext);
        Locked = NeedsPassword && shares.IsLocked(Link);
        ViewData["Title"] = NeedsPassword ? "Beleg" : Link.Title;
        return !NeedsPassword;
    }
}
