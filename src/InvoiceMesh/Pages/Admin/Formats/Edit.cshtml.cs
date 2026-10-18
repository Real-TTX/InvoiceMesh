using System.ComponentModel.DataAnnotations;
using InvoiceMesh.Configuration;
using InvoiceMesh.Services;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceMesh.Pages.Admin.Formats;

/// <summary>One specification identifier of the administrator: what it is, which version, which rules apply.</summary>
public sealed class EditModel(ConfigStore config, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib die Kennung ein."), StringLength(300)]
        public string Identifier { get; set; } = "";

        public bool IsPrefix { get; set; }

        [Required(ErrorMessage = "Bitte gib den Standard an."), StringLength(100)]
        public string Standard { get; set; } = "";

        [Required(ErrorMessage = "Bitte gib das Profil an."), StringLength(100)]
        public string Profile { get; set; } = "";

        [Required(ErrorMessage = "Bitte gib die Version an."), StringLength(50)]
        public string Version { get; set; } = "";

        public bool En16931 { get; set; } = true;

        public bool XRechnung { get; set; }

        public bool Peppol { get; set; }

        [StringLength(500)]
        public string? Note { get; set; }
    }

    [BindProperty(SupportsGet = true)]
    public int? Index { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    private void Prepare()
    {
        var title = Index is null ? "Neues Format" : Input.Standard.Length > 0 ? $"{Input.Standard} · {Input.Profile}" : "Format";
        Head(title, "Erkannt wird an der Spezifikationskennung (BT-24): cbc:CustomizationID in UBL, GuidelineSpecifiedDocumentContextParameter in CII.", ("Einstellungen", null), ("Formate", "/Admin/Formats"), (title, null));
        ViewData["ActiveHref"] = "/Admin/Formats";
    }

    public IActionResult OnGet()
    {
        if (Index is { } i)
        {
            var list = config.Current.Formats.Custom;
            if (i < 0 || i >= list.Count)
            {
                return NotFound();
            }

            var c = list[i];
            Input = new InputModel { Identifier = c.Identifier, IsPrefix = c.IsPrefix, Standard = c.Standard, Profile = c.Profile, Version = c.Version, En16931 = c.En16931, XRechnung = c.XRechnung, Peppol = c.Peppol, Note = c.Note };
        }

        Prepare();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Prepare();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var entry = new CustomSpecification
        {
            Identifier = Input.Identifier.Trim(), IsPrefix = Input.IsPrefix, Standard = Input.Standard.Trim(), Profile = Input.Profile.Trim(), Version = Input.Version.Trim(),
            En16931 = Input.En16931, XRechnung = Input.XRechnung, Peppol = Input.Peppol, Note = string.IsNullOrWhiteSpace(Input.Note) ? null : Input.Note.Trim(),
        };
        config.Update(c =>
        {
            if (Index is { } i && i >= 0 && i < c.Formats.Custom.Count)
            {
                c.Formats.Custom[i] = entry;
            }
            else
            {
                c.Formats.Custom.Add(entry);
            }
        });
        await log.LogAsync("Format gespeichert", "Settings", null, entry.Identifier);
        Flash("Gespeichert. Neue Belege werden damit erkannt; vorhandene nach „Neu verarbeiten“.");
        return RedirectToPage("/Admin/Formats/Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (Index is { } i)
        {
            config.Update(c =>
            {
                if (i >= 0 && i < c.Formats.Custom.Count)
                {
                    c.Formats.Custom.RemoveAt(i);
                }
            });
            await log.LogAsync("Format gelöscht", "Settings");
            Flash("Das Format ist entfernt.");
        }

        return RedirectToPage("/Admin/Formats/Index");
    }
}
