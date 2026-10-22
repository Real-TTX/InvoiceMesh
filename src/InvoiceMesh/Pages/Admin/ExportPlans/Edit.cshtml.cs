using System.ComponentModel.DataAnnotations;
using InvoiceMesh.Configuration;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using InvoiceMesh.Services.Automation;
using InvoiceMesh.Services.Backup;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Pages.Admin.ExportPlans;

/// <summary>
/// A planned export: which export, which documents, when, and where the file goes. Passwords and the value of the webhook header are kept protected.
/// "Prüfen" tries the delivery, "Jetzt ausführen" runs the plan now (the documents of the run count as delivered).
/// </summary>
public sealed class EditModel(ImDbContext db, SecretProtector secrets, ExportPlanService plans, ActivityLogger log, TimeProvider clock) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib dem Plan einen Namen.")]
        [Display(Name = "Name")]
        public string Name { get; set; } = "";

        [Display(Name = "Export")]
        public long? ExportProfileId { get; set; }

        public bool IsEnabled { get; set; } = true;

        [Display(Name = "Welche Belege")]
        public ExportSelection Selection { get; set; } = ExportSelection.New;

        [Range(1, 3660, ErrorMessage = "Bitte gib zwischen 1 und 3660 Tage an.")]
        [Display(Name = "Tage")]
        public int Days { get; set; } = 7;

        public bool OnlyValid { get; set; } = true;

        public bool SkipEmpty { get; set; } = true;

        public ScheduleKind Schedule { get; set; } = ScheduleKind.Daily;

        [Range(1, 168, ErrorMessage = "Das Intervall liegt zwischen 1 und 168 Stunden.")]
        public int IntervalHours { get; set; } = 6;

        public string TimeOfDay { get; set; } = "06:00";

        public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Monday;

        [Range(1, 31, ErrorMessage = "Der Tag liegt zwischen 1 und 31.")]
        public int DayOfMonth { get; set; } = 1;

        [Display(Name = "Wohin")]
        public DeliveryKind Delivery { get; set; } = DeliveryKind.Folder;

        [Display(Name = "Ordner")]
        public string? Path { get; set; }

        [Display(Name = "Server")]
        public string? Host { get; set; }

        [Display(Name = "Freigabe")]
        public string? Share { get; set; }

        public string? Domain { get; set; }

        [Display(Name = "Benutzername")]
        public string? UserName { get; set; }

        public string? Password { get; set; }

        [Display(Name = "Adresse")]
        public string? Url { get; set; }

        [Display(Name = "Header")]
        public string? HeaderName { get; set; }

        public string? HeaderValue { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public ExportPlan? Plan { get; private set; }

    public List<SelectListItem> Profiles { get; private set; } = [];

    public string? Test { get; private set; }

    public bool TestOk { get; private set; }

    public bool HasPassword => !string.IsNullOrEmpty(Plan?.PasswordProtected);

    public bool HasHeaderValue => !string.IsNullOrEmpty(Plan?.HeaderValueProtected);

    public static string Of(ExportSelection s) => s == ExportSelection.New ? "Neue seit dem letzten Lauf" : "Die der letzten Tage, jedes Mal";

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await PrepareAsync())
        {
            return NotFound();
        }

        if (Plan is { } p)
        {
            Input = new InputModel
            {
                Name = p.Name, ExportProfileId = p.ExportProfileId, IsEnabled = p.IsEnabled, Selection = p.Selection, Days = p.Days, OnlyValid = p.OnlyValid, SkipEmpty = p.SkipEmpty,
                Schedule = p.Schedule, IntervalHours = p.IntervalHours, TimeOfDay = p.TimeOfDay.ToString("HH\\:mm"), DayOfWeek = p.DayOfWeek, DayOfMonth = p.DayOfMonth,
                Delivery = p.Delivery, Path = p.Path, Host = p.Host, Share = p.Share, Domain = p.Domain, UserName = p.UserName, Url = p.Url, HeaderName = p.HeaderName,
            };
        }
        else
        {
            Input.ExportProfileId = Profiles.Count > 0 ? long.Parse(Profiles[0].Value) : null;
            Input.Path = "exporte";
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await PrepareAsync())
        {
            return NotFound();
        }

        var time = Validate();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var plan = Plan is { } existing ? await db.ExportPlan.FirstAsync(p => p.Id == existing.Id) : db.ExportPlan.Add(new ExportPlan()).Entity;
        Apply(plan, time);
        plan.NextRunDate = BackupSchedule.Next(plan, clock.GetUtcNow().UtcDateTime, Fmt.Zone);
        await db.SaveChangesAsync();
        await log.LogAsync("Geplanter Export gespeichert", nameof(ExportPlan), plan.Id, plan.Name);
        Flash(plan.IsEnabled ? $"Der Plan ist gespeichert. Er läuft zum ersten Mal am {Fmt.Moment(plan.NextRunDate)}." : "Der Plan ist gespeichert. Er ist ausgeschaltet.");
        return RedirectToPage("/Admin/ExportPlans/Index");
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        if (!await PrepareAsync())
        {
            return NotFound();
        }

        var time = Validate();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var probe = new ExportPlan { PasswordProtected = Plan?.PasswordProtected, HeaderValueProtected = Plan?.HeaderValueProtected };
        Apply(probe, time);
        (TestOk, Test) = await plans.CheckAsync(probe, HttpContext.RequestAborted);
        return Page();
    }

    public async Task<IActionResult> OnPostRunAsync()
    {
        var plan = await db.ExportPlan.FirstOrDefaultAsync(p => p.Id == Id);
        if (plan is null)
        {
            return NotFound();
        }

        var result = await plans.RunAsync(plan, manual: true, HttpContext.RequestAborted);
        if (result.Ok)
        {
            Flash(result.Message);
        }
        else
        {
            FlashError($"Der Export ist fehlgeschlagen: {result.Message}");
        }

        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var plan = await db.ExportPlan.FirstOrDefaultAsync(p => p.Id == Id);
        if (plan is not null)
        {
            db.ExportPlan.Remove(plan);
            await db.SaveChangesAsync();
            await log.LogAsync("Geplanter Export gelöscht", nameof(ExportPlan), plan.Id, plan.Name);
            Flash("Der Plan ist gelöscht. Die Dateien, die er abgeliefert hat, bleiben.");
        }

        return RedirectToPage("/Admin/ExportPlans/Index");
    }

    private TimeOnly Validate()
    {
        if (Input.ExportProfileId is null || !Profiles.Any(p => p.Value == Input.ExportProfileId.ToString()))
        {
            ModelState.AddModelError("Input.ExportProfileId", "Bitte wähle einen Export. Exporte legst du unter „Exporte“ an.");
        }

        if (!TimeOnly.TryParseExact(Input.TimeOfDay, "HH:mm", out var time) && !TimeOnly.TryParse(Input.TimeOfDay, out time))
        {
            ModelState.AddModelError("Input.TimeOfDay", "Bitte gib die Uhrzeit so ein: 06:00.");
        }

        switch (Input.Delivery)
        {
            case DeliveryKind.Smb when string.IsNullOrWhiteSpace(Input.Host):
                ModelState.AddModelError("Input.Host", "Bitte gib den Server (Namen oder Adresse) des NAS an.");
                break;
            case DeliveryKind.Smb when string.IsNullOrWhiteSpace(Input.Share):
                ModelState.AddModelError("Input.Share", "Bitte gib den Namen der Freigabe an.");
                break;
            case DeliveryKind.Webhook when !Uri.TryCreate(Input.Url?.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"):
                ModelState.AddModelError("Input.Url", "Bitte gib eine vollständige Adresse an, zum Beispiel https://erp.example.com/rechnungen.");
                break;
        }

        if (!string.IsNullOrWhiteSpace(Input.HeaderName) && Input.HeaderName.Trim().Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
        {
            ModelState.AddModelError("Input.HeaderName", "Ein Header-Name besteht aus Buchstaben, Ziffern und Bindestrichen, zum Beispiel Authorization.");
        }

        return time;
    }

    private void Apply(ExportPlan plan, TimeOnly time)
    {
        plan.Name = Input.Name.Trim();
        plan.ExportProfileId = Input.ExportProfileId ?? 0;
        plan.IsEnabled = Input.IsEnabled;
        plan.Selection = Input.Selection;
        plan.Days = Input.Days;
        plan.OnlyValid = Input.OnlyValid;
        plan.SkipEmpty = Input.SkipEmpty;
        plan.Schedule = Input.Schedule;
        plan.IntervalHours = Input.IntervalHours;
        plan.TimeOfDay = time;
        plan.DayOfWeek = Input.DayOfWeek;
        plan.DayOfMonth = Input.DayOfMonth;
        plan.Delivery = Input.Delivery;
        plan.Path = (Input.Path ?? "").Trim();
        switch (Input.Delivery)
        {
            case DeliveryKind.Smb:
                plan.Host = Input.Host?.Trim();
                plan.Share = Input.Share?.Trim();
                plan.Domain = Input.Domain?.Trim();
                plan.UserName = Input.UserName?.Trim();
                if (!string.IsNullOrEmpty(Input.Password))
                {
                    plan.PasswordProtected = secrets.Protect(Input.Password);
                }

                break;
            case DeliveryKind.Webhook:
                plan.Url = Input.Url?.Trim();
                plan.HeaderName = string.IsNullOrWhiteSpace(Input.HeaderName) ? null : Input.HeaderName.Trim();
                if (plan.HeaderName is null)
                {
                    plan.HeaderValueProtected = null;
                }
                else if (!string.IsNullOrEmpty(Input.HeaderValue))
                {
                    plan.HeaderValueProtected = secrets.Protect(Input.HeaderValue);
                }

                break;
        }
    }

    private async Task<bool> PrepareAsync()
    {
        if (Id is { } id)
        {
            Plan = await db.ExportPlan.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
            if (Plan is null)
            {
                return false;
            }
        }

        Profiles = await db.ExportProfile.AsNoTracking().OrderBy(e => e.Name).Select(e => new SelectListItem(e.Name, e.Id.ToString())).ToListAsync();
        var title = Plan is null ? "Neuer geplanter Export" : Plan.Name;
        Head(title, null, ("Einstellungen", null), ("Geplante Exporte", "/Admin/ExportPlans"), (title, null));
        ViewData["ActiveHref"] = "/Admin/ExportPlans";
        ViewData["Width"] = "narrow";
        return true;
    }
}
