using System.ComponentModel.DataAnnotations;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using InvoiceMesh.Services.Automation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Pages.Admin.ApiKeys;

/// <summary>
/// A key of the API: its name, what it may do, until when. The key itself is made when the record is made and shown once, on the page that follows;
/// "Neu erzeugen" replaces it (the old one stops working at once).
/// </summary>
public sealed class EditModel(ImDbContext db, ApiKeyService keys, ActivityLogger log, TimeProvider clock) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib dem Schlüssel einen Namen.")]
        [Display(Name = "Name")]
        public string Name { get; set; } = "";

        public bool CanImport { get; set; } = true;

        public bool CanRead { get; set; } = true;

        public bool IsEnabled { get; set; } = true;

        [Display(Name = "Gültig bis")]
        public DateOnly? ExpiresDate { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public ApiKey? Key { get; private set; }

    /// <summary>The key just made: shown once, then never again.</summary>
    [TempData]
    public string? NewKey { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await PrepareAsync())
        {
            return NotFound();
        }

        if (Key is { } k)
        {
            Input = new InputModel { Name = k.Name, CanImport = k.CanImport, CanRead = k.CanRead, IsEnabled = k.IsEnabled, ExpiresDate = k.ExpiresDate is { } e ? DateOnly.FromDateTime(Fmt.Local(e)) : null };
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await PrepareAsync())
        {
            return NotFound();
        }

        if (!Input.CanImport && !Input.CanRead)
        {
            ModelState.AddModelError("Input.CanImport", "Ein Schlüssel ohne Rechte nützt nichts: Er sollte einliefern, lesen oder beides dürfen.");
        }

        if (Input.ExpiresDate is { } date && date <= Fmt.Today(clock))
        {
            ModelState.AddModelError("Input.ExpiresDate", "Das Datum liegt nicht in der Zukunft.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        // valid through the whole day of the date, on the clock of the installation
        var expires = Input.ExpiresDate is { } d ? Fmt.UtcFromLocal(d.AddDays(1).ToDateTime(TimeOnly.MinValue)) : (DateTime?)null;
        if (Key is null)
        {
            var (created, plain) = await keys.CreateAsync(Input.Name, Input.CanImport, Input.CanRead, expires, HttpContext.RequestAborted);
            created.IsEnabled = Input.IsEnabled;
            await db.SaveChangesAsync();
            await log.LogAsync("API-Schlüssel angelegt", nameof(ApiKey), created.Id, created.Name);
            NewKey = plain;
            return RedirectToPage(new { id = created.Id });
        }

        var key = await db.ApiKey.FirstAsync(k => k.Id == Key.Id);
        key.Name = Input.Name.Trim();
        key.CanImport = Input.CanImport;
        key.CanRead = Input.CanRead;
        key.IsEnabled = Input.IsEnabled;
        key.ExpiresDate = expires;
        await db.SaveChangesAsync();
        await log.LogAsync("API-Schlüssel gespeichert", nameof(ApiKey), key.Id, key.Name);
        Flash("Der Schlüssel ist gespeichert.");
        return RedirectToPage("/Admin/ApiKeys/Index");
    }

    /// <summary>Makes a new key for the record; the old one stops working at once.</summary>
    public async Task<IActionResult> OnPostRenewAsync()
    {
        var key = await db.ApiKey.FirstOrDefaultAsync(k => k.Id == Id);
        if (key is null)
        {
            return NotFound();
        }

        var plain = ApiKeyService.NewKey();
        key.KeyHash = ApiKeyService.Hash(plain);
        key.KeyPrefix = plain[..10];
        key.LastUsedDate = null;
        await db.SaveChangesAsync();
        await log.LogAsync("API-Schlüssel neu erzeugt", nameof(ApiKey), key.Id, key.Name);
        NewKey = plain;
        return RedirectToPage(new { id = key.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var key = await db.ApiKey.FirstOrDefaultAsync(k => k.Id == Id);
        if (key is not null)
        {
            db.ApiKey.Remove(key);
            await db.SaveChangesAsync();
            await log.LogAsync("API-Schlüssel gelöscht", nameof(ApiKey), key.Id, key.Name);
            Flash("Der Schlüssel ist gelöscht. Er funktioniert ab sofort nicht mehr.");
        }

        return RedirectToPage("/Admin/ApiKeys/Index");
    }

    private async Task<bool> PrepareAsync()
    {
        if (Id is { } id)
        {
            Key = await db.ApiKey.AsNoTracking().FirstOrDefaultAsync(k => k.Id == id);
            if (Key is null)
            {
                return false;
            }
        }

        var title = Key is null ? "Neuer API-Schlüssel" : Key.Name;
        Head(title, null, ("Einstellungen", null), ("API", "/Admin/ApiKeys"), (title, null));
        ViewData["ActiveHref"] = "/Admin/ApiKeys";
        ViewData["Width"] = "narrow";
        return true;
    }
}
