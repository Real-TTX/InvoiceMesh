using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using InvoiceMesh.Configuration;
using InvoiceMesh.Services;
using InvoiceMesh.Services.Processing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace InvoiceMesh.Pages.Admin;

/// <summary>How documents are processed: the languages of the text recognition, what is made by itself (XRechnung, PDF view), the tolerance of the sum checks, the size of uploads.</summary>
public sealed partial class ProcessingModel(ConfigStore config, ActivityLogger log, TesseractOcr ocr) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib mindestens eine Sprache an.")]
        public string OcrLanguages { get; set; } = "";

        public bool AutoXRechnung { get; set; }

        public XRechnungSyntaxChoice XRechnungSyntax { get; set; }

        public bool AutoPdf { get; set; }

        [Range(0, 1, ErrorMessage = "Zwischen 0 und 1.")]
        public decimal Tolerance { get; set; }

        [Range(1, 200, ErrorMessage = "Zwischen 1 und 200 MB.")]
        public int MaxUploadMb { get; set; }

        public bool WarnDuplicates { get; set; }
    }

    [GeneratedRegex("^[a-z_]+(\\+[a-z_]+)*$")]
    private static partial Regex LanguagesRegex();

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public bool OcrAvailable => ocr.IsAvailable;

    public string? OcrVersion => ocr.EngineVersion;

    public static List<SelectListItem> Syntaxes =>
    [
        new("Wie die Quelle (UBL bleibt UBL, CII bleibt CII; PDF und Scans: UBL)", nameof(XRechnungSyntaxChoice.Auto)),
        new("Immer UBL", nameof(XRechnungSyntaxChoice.Ubl)),
        new("Immer CII", nameof(XRechnungSyntaxChoice.Cii)),
    ];

    public void OnGet()
    {
        Head("Verarbeitung", "Was beim Eingang eines Belegs von selbst passiert.", ("Einstellungen", null), ("Verarbeitung", null));
        var p = config.Current.Processing;
        Input = new InputModel
        {
            OcrLanguages = p.OcrLanguages, AutoXRechnung = p.AutoXRechnung, XRechnungSyntax = p.XRechnungSyntax, AutoPdf = p.AutoPdf, Tolerance = p.Tolerance,
            MaxUploadMb = p.MaxUploadMb, WarnDuplicates = p.WarnDuplicates,
        };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Head("Verarbeitung", "Was beim Eingang eines Belegs von selbst passiert.", ("Einstellungen", null), ("Verarbeitung", null));
        var languages = (Input.OcrLanguages ?? "").Trim().ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal);
        if (!LanguagesRegex().IsMatch(languages))
        {
            ModelState.AddModelError("Input.OcrLanguages", "Sprachcodes von Tesseract, verbunden mit +, z. B. deu+eng+fra.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        config.Update(c =>
        {
            c.Processing.OcrLanguages = languages;
            c.Processing.AutoXRechnung = Input.AutoXRechnung;
            c.Processing.XRechnungSyntax = Input.XRechnungSyntax;
            c.Processing.AutoPdf = Input.AutoPdf;
            c.Processing.Tolerance = Input.Tolerance;
            c.Processing.MaxUploadMb = Input.MaxUploadMb;
            c.Processing.WarnDuplicates = Input.WarnDuplicates;
        });
        await log.LogAsync("Verarbeitung eingestellt", "Settings");
        Flash("Gespeichert. Es gilt für die nächsten Belege.");
        return RedirectToPage();
    }
}
