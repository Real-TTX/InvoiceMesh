using System.ComponentModel.DataAnnotations;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Pages.Invoices;

/// <summary>Makes a link that shows this document to somebody without an account (the role "Anonym"): for a time, optionally with a password, with or without downloads.</summary>
public sealed class ShareModel(InvoiceService invoices, ShareService shares, ImDbContext db, ActivityLogger log) : InvoicePage(invoices)
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib einen Titel ein."), StringLength(200)]
        public string Title { get; set; } = "";

        [StringLength(200)]
        public string? RecipientName { get; set; }

        [StringLength(2000)]
        public string? Message { get; set; }

        [Range(1, 365, ErrorMessage = "Zwischen 1 und 365 Tagen.")]
        public int Days { get; set; } = 30;

        public bool UsePassword { get; set; }

        [StringLength(200, MinimumLength = 8, ErrorMessage = "Mindestens 8 Zeichen.")]
        public string? Password { get; set; }

        public bool AllowOriginal { get; set; } = true;

        public bool AllowGenerated { get; set; } = true;
    }

    protected override string TabKey => "overview";

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public List<ShareLink> Existing { get; private set; } = [];

    public string LinkOf(ShareLink l) => shares.LinkOf(l);

    public DateTime Now => DateTime.UtcNow;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await PrepareAsync())
        {
            return NotFound();
        }

        Input = new InputModel { Title = Doc.InvoiceNumber is { } n ? $"Rechnung {n}" : Doc.FileName, Days = shares.DefaultDays };
        return Page();
    }

    private async Task<bool> PrepareAsync()
    {
        if (!await LoadAsync())
        {
            return false;
        }

        Head($"Freigeben: {Doc.DisplayName}", "Ein Link für Personen ohne Konto. Sie sehen nur diesen Beleg, nur lesend.", ("Belege", "/Invoices"), (Doc.DisplayName, $"/Invoices/Details?id={Id}"), ("Freigeben", null));
        Existing = await db.ShareLink.AsNoTracking().Where(l => l.InvoiceId == Id).OrderByDescending(l => l.CreateDate).ToListAsync();
        return true;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Input.UsePassword && string.IsNullOrWhiteSpace(Input.Password))
        {
            ModelState.AddModelError("Input.Password", "Bitte gib ein Passwort ein.");
        }

        if (!Input.UsePassword)
        {
            ModelState.Remove("Input.Password");
        }

        if (!ModelState.IsValid)
        {
            return await PrepareAsync() ? Page() : NotFound();
        }

        var doc = await TrackedAsync();
        if (doc is null)
        {
            return NotFound();
        }

        var link = await shares.CreateAsync(doc, Input.AllowOriginal, Input.AllowGenerated, Input.Title, Input.Message, Input.Days, Input.UsePassword ? Input.Password : null, Input.RecipientName);
        await Invoices.EventAsync(doc, "Freigegeben", $"Link „{link.Title}“{(link.RecipientName is null ? "" : $" für {link.RecipientName}")}, gültig bis {Fmt.Date(link.ExpiresDate)}.");
        await log.LogAsync("Freigabe angelegt", nameof(ShareLink), link.Id, link.Title);
        Flash("Der Link ist angelegt. Kopiere ihn unten.");
        return RedirectToPage(new { id = Id });
    }
}
