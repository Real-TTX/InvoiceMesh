using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Pages.Exports;

/// <summary>Runs an export over the processed documents of a period and sends the file.</summary>
public sealed class RunModel(ImDbContext db, ExportService exports, ActivityLogger log, TimeProvider clock) : PageModelBase
{
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public DateOnly? From { get; set; }

    [BindProperty]
    public DateOnly? To { get; set; }

    [BindProperty]
    public bool OnlyValid { get; set; } = true;

    public ExportProfile Profile { get; private set; } = null!;

    public int Available { get; private set; }

    private async Task<bool> LoadAsync()
    {
        var profile = await db.ExportProfile.AsNoTracking().Include(e => e.TargetSchema).FirstOrDefaultAsync(e => e.Id == Id);
        if (profile is null)
        {
            return false;
        }

        Profile = profile;
        Head($"Ausführen: {profile.Name}", $"{IndexModel.Describe(ExportService.Options(profile))} · Schema „{profile.TargetSchema?.Name}“", ("Exporte", "/Exports"), (profile.Name, $"/Exports/Edit?id={Id}"), ("Ausführen", null));
        ViewData["ActiveHref"] = "/Exports";
        Available = await db.Invoice.CountAsync(i => i.ProcessingStatus == ProcessingStatus.Processed);
        return true;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        var today = Fmt.Today(clock);
        From = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        To = today;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        var ids = await exports.SelectAsync(From, To, OnlyValid, HttpContext.RequestAborted);
        if (ids.Count == 0)
        {
            ModelState.AddModelError(nameof(From), "In diesem Zeitraum gibt es keine passenden verarbeiteten Belege.");
            return Page();
        }

        try
        {
            var result = await exports.RunAsync(Profile, ids, HttpContext.RequestAborted);
            await log.LogAsync("Export ausgeführt", nameof(ExportProfile), Profile.Id, $"{result.Count} Belege, {result.Skipped} übersprungen");
            Response.Headers.CacheControl = "private, no-store";
            return File(result.Bytes, result.ContentType, result.FileName);
        }
        catch (DefinitionException ex)
        {
            ModelState.AddModelError(nameof(From), ex.Message);
            return Page();
        }
    }
}
