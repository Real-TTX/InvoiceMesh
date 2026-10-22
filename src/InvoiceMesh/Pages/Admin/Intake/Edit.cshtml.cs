using System.ComponentModel.DataAnnotations;
using InvoiceMesh.Configuration;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using InvoiceMesh.Services.Automation;
using InvoiceMesh.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Pages.Admin.Intake;

/// <summary>
/// An intake source: a folder, a share of a NAS or a mailbox, how often it is looked at and what happens to what was taken. Passwords are kept protected.
/// "Prüfen" tries the settings in the form, "Jetzt abholen" takes in what is there now.
/// </summary>
public sealed class EditModel(ImDbContext db, SecretProtector secrets, IntakeService intake, NavCounts counts, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib dem Eingang einen Namen.")]
        [Display(Name = "Name")]
        public string Name { get; set; } = "";

        [Display(Name = "Art")]
        public IntakeKind Kind { get; set; } = IntakeKind.Folder;

        public bool IsEnabled { get; set; } = true;

        [Range(1, 1440, ErrorMessage = "Das Intervall liegt zwischen 1 und 1440 Minuten.")]
        [Display(Name = "Alle … Minuten")]
        public int IntervalMinutes { get; set; } = 15;

        [Display(Name = "Ordner")]
        public string? Path { get; set; }

        [Display(Name = "Danach verschieben nach")]
        public string? DoneFolder { get; set; }

        [Display(Name = "Server")]
        public string? Host { get; set; }

        [Range(1, 65535, ErrorMessage = "Der Port liegt zwischen 1 und 65535.")]
        [Display(Name = "Port")]
        public int Port { get; set; } = 993;

        [Display(Name = "Verschlüsselung")]
        public MailSecurity Security { get; set; } = MailSecurity.Tls;

        [Display(Name = "Freigabe")]
        public string? Share { get; set; }

        [Display(Name = "Domäne (optional)")]
        public string? Domain { get; set; }

        [Display(Name = "Benutzername")]
        public string? UserName { get; set; }

        [Display(Name = "Passwort")]
        public string? Password { get; set; }

        [Display(Name = "Postfach-Ordner")]
        public string MailFolder { get; set; } = "INBOX";
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IntakeSource? Source { get; private set; }

    public string? Test { get; private set; }

    public bool TestOk { get; private set; }

    public bool HasPassword => !string.IsNullOrEmpty(Source?.PasswordProtected);

    public static string Of(MailSecurity s) => s switch
    {
        MailSecurity.Tls => "TLS (IMAPS, Port 993)",
        MailSecurity.StartTls => "STARTTLS (Port 143)",
        _ => "Keine (nur im eigenen Netz)",
    };

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await PrepareAsync())
        {
            return NotFound();
        }

        if (Source is { } s)
        {
            Input = new InputModel
            {
                Name = s.Name, Kind = s.Kind, IsEnabled = s.IsEnabled, IntervalMinutes = s.IntervalMinutes, Path = s.Path, DoneFolder = s.DoneFolder, Host = s.Host, Port = s.Port,
                Security = s.Security, Share = s.Share, Domain = s.Domain, UserName = s.UserName, MailFolder = s.MailFolder,
            };
        }
        else
        {
            Input.Path = "eingang";
            Input.DoneFolder = "verarbeitet";
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await PrepareAsync())
        {
            return NotFound();
        }

        Validate();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var source = Source is { } existing ? await db.IntakeSource.FirstAsync(s => s.Id == existing.Id) : db.IntakeSource.Add(new IntakeSource()).Entity;
        Apply(source);
        source.NextRunDate = null;
        await db.SaveChangesAsync();
        await log.LogAsync("Eingang gespeichert", nameof(IntakeSource), source.Id, source.Name);
        Flash(source.IsEnabled ? "Der Eingang ist gespeichert und wird gleich zum ersten Mal abgeholt." : "Der Eingang ist gespeichert. Er ist ausgeschaltet.");
        return RedirectToPage("/Admin/Intake/Index");
    }

    /// <summary>Tries the settings in the form (not yet saved).</summary>
    public async Task<IActionResult> OnPostTestAsync()
    {
        if (!await PrepareAsync())
        {
            return NotFound();
        }

        Validate();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var probe = new IntakeSource { PasswordProtected = Source?.PasswordProtected };
        Apply(probe);
        (TestOk, Test) = await intake.CheckAsync(probe, HttpContext.RequestAborted);
        return Page();
    }

    /// <summary>Takes in what is there now (with the saved settings).</summary>
    public async Task<IActionResult> OnPostRunAsync()
    {
        var source = await db.IntakeSource.FirstOrDefaultAsync(s => s.Id == Id);
        if (source is null)
        {
            return NotFound();
        }

        var outcome = await intake.RunAsync(source, manual: true, HttpContext.RequestAborted);
        counts.Invalidate();
        if (source.LastRunOk)
        {
            Flash(source.LastStatus ?? "Abgeholt.");
        }
        else
        {
            FlashError(source.LastStatus ?? string.Join(" ", outcome.Problems));
        }

        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var source = await db.IntakeSource.FirstOrDefaultAsync(s => s.Id == Id);
        if (source is not null)
        {
            db.IntakeSource.Remove(source);
            await db.SaveChangesAsync();
            await log.LogAsync("Eingang gelöscht", nameof(IntakeSource), source.Id, source.Name);
            Flash("Der Eingang ist gelöscht. Die Belege, die über ihn kamen, bleiben.");
        }

        return RedirectToPage("/Admin/Intake/Index");
    }

    private void Validate()
    {
        if (Input.Kind == IntakeKind.Folder)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Input.Host))
        {
            ModelState.AddModelError("Input.Host", Input.Kind == IntakeKind.Smb ? "Bitte gib den Server (Namen oder Adresse) des NAS an." : "Bitte gib den IMAP-Server an, zum Beispiel imap.example.com.");
        }

        if (Input.Kind == IntakeKind.Smb && string.IsNullOrWhiteSpace(Input.Share))
        {
            ModelState.AddModelError("Input.Share", "Bitte gib den Namen der Freigabe an.");
        }

        if (Input.Kind == IntakeKind.Mailbox && string.IsNullOrWhiteSpace(Input.UserName))
        {
            ModelState.AddModelError("Input.UserName", "Bitte gib den Benutzernamen des Postfachs an.");
        }

        if (Input.Kind == IntakeKind.Mailbox && string.IsNullOrWhiteSpace(Input.MailFolder))
        {
            ModelState.AddModelError("Input.MailFolder", "Bitte gib den Ordner an, der gelesen wird (meist INBOX).");
        }
    }

    private void Apply(IntakeSource source)
    {
        source.Name = Input.Name.Trim();
        source.Kind = Input.Kind;
        source.IsEnabled = Input.IsEnabled;
        source.IntervalMinutes = Input.IntervalMinutes;
        source.DoneFolder = string.IsNullOrWhiteSpace(Input.DoneFolder) ? null : Input.DoneFolder.Trim().Trim('/', '\\');
        source.Path = (Input.Path ?? "").Trim();
        if (Input.Kind == IntakeKind.Folder)
        {
            return;
        }

        source.Host = Input.Host?.Trim();
        source.UserName = Input.UserName?.Trim();
        if (!string.IsNullOrEmpty(Input.Password))
        {
            source.PasswordProtected = secrets.Protect(Input.Password);
        }

        if (Input.Kind == IntakeKind.Smb)
        {
            source.Share = Input.Share?.Trim();
            source.Domain = Input.Domain?.Trim();
            return;
        }

        source.Port = Input.Port;
        source.Security = Input.Security;
        source.MailFolder = Input.MailFolder.Trim();
    }

    private async Task<bool> PrepareAsync()
    {
        if (Id is { } id)
        {
            Source = await db.IntakeSource.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
            if (Source is null)
            {
                return false;
            }
        }

        var title = Source is null ? "Neuer Eingang" : Source.Name;
        Head(title, null, ("Einstellungen", null), ("Eingänge", "/Admin/Intake"), (title, null));
        ViewData["ActiveHref"] = "/Admin/Intake";
        ViewData["Width"] = "narrow";
        return true;
    }
}
