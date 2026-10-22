using InvoiceMesh.Configuration;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using InvoiceMesh.Services.Automation;
using InvoiceMesh.UiKit;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceMesh.Pages.Invoices;

/// <summary>Takes documents in: any number of files at once, ZIPs unpacked. Each is stored as it came and queued; the processing runs in the background.</summary>
public sealed class UploadModel(IntakeService intake, ConfigStore config, ActivityLogger log, NavCounts counts, ICurrentUser user) : PageModelBase
{
    public const int MaxFiles = 50;

    [BindProperty]
    public List<IFormFile> Files { get; set; } = [];

    public int MaxMb => config.Current.Processing.MaxUploadMb;

    public bool IsAdmin => user.IsAdmin;

    public void OnGet() => Head("Belege hochladen", "Strukturierte Rechnungen, PDFs und Scans: das Format wird am Inhalt erkannt.", ("Belege", "/Invoices"), ("Hochladen", null));

    public async Task<IActionResult> OnPostAsync()
    {
        OnGet();
        if (Files.Count == 0)
        {
            ModelState.AddModelError(nameof(Files), "Bitte wähle mindestens eine Datei.");
            return Page();
        }

        var outcome = new IntakeOutcome();
        foreach (var file in Files.Take(MaxFiles))
        {
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, HttpContext.RequestAborted);
            outcome.Add(await intake.AddAsync(file.FileName, buffer.ToArray(), InvoiceSource.Upload, user.Name is { } name ? $"hochgeladen von {name}" : null, HttpContext.RequestAborted));
        }

        var added = outcome.Added.Select(a => a.Document.Id).ToList();
        var problems = outcome.Problems;
        var duplicates = outcome.Duplicates;

        counts.Invalidate();
        if (added.Count > 0)
        {
            await log.LogAsync("Belege hochgeladen", "Invoice", added.Count == 1 ? added[0] : null, $"{added.Count} Datei(en)");
        }

        if (problems.Count > 0)
        {
            FlashError(string.Join(" ", problems));
        }

        if (added.Count == 0)
        {
            return RedirectToPage();
        }

        Flash($"{Ui.CountNoun(added.Count, "Belege")} aufgenommen und in der Warteschlange." + (duplicates > 0 ? $" {duplicates} davon gab es schon (gleiche Datei)." : ""));
        return added.Count == 1 ? RedirectToPage("/Invoices/Details", new { id = added[0] }) : RedirectToPage("/Invoices/Index");
    }
}
