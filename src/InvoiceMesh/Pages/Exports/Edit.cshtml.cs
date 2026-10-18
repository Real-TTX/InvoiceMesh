using System.ComponentModel.DataAnnotations;
using InvoiceMesh.Core;
using InvoiceMesh.Core.Output;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Pages.Exports;

/// <summary>An export configuration: schema, format, groups and fields, and the details of the format. Only the fields of the chosen format are shown.</summary>
public sealed class EditModel(ImDbContext db, DefinitionService definitions, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib einen Namen ein."), StringLength(200)]
        public string Name { get; set; } = "";

        [StringLength(2000)]
        public string? Description { get; set; }

        public long TargetSchemaId { get; set; }

        public ExportFormat Format { get; set; } = ExportFormat.Csv;

        /// <summary>Comma separated, as the picker posts it.</summary>
        public string? Groups { get; set; }

        public string? Exclude { get; set; }

        [StringLength(3)]
        public string CsvDelimiter { get; set; } = ";";

        public string CsvDecimalSeparator { get; set; } = ",";

        public CsvLayout CsvLayout { get; set; } = CsvLayout.InvoicePerRow;

        public string? RowGroup { get; set; }

        public bool CsvLabels { get; set; }

        [RegularExpression("^[A-Za-z_][A-Za-z0-9_.-]*$", ErrorMessage = "Ein gültiger XML-Name.")]
        public string XmlRoot { get; set; } = "Invoices";

        [RegularExpression("^[A-Za-z_][A-Za-z0-9_.-]*$", ErrorMessage = "Ein gültiger XML-Name.")]
        public string XmlInvoice { get; set; } = "Invoice";

        [RegularExpression("^[A-Za-z_][A-Za-z0-9_.-]*$", ErrorMessage = "Ein gültiger XML-Name.")]
        public string XmlItem { get; set; } = "Item";

        public bool JsonIndented { get; set; } = true;
    }

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public ExportProfile? Profile { get; private set; }

    public List<SelectListItem> Schemas { get; private set; } = [];

    public List<SelectListItem> GroupItems { get; private set; } = [];

    public List<SelectListItem> RepeatingItems { get; private set; } = [];

    public List<SelectListItem> FieldItems { get; private set; } = [];

    public static List<SelectListItem> Formats => [new("CSV", nameof(ExportFormat.Csv)), new("JSON", nameof(ExportFormat.Json)), new("XML", nameof(ExportFormat.Xml))];

    public static List<SelectListItem> Layouts => [new("Eine Zeile je Beleg", nameof(CsvLayout.InvoicePerRow)), new("Eine Zeile je Eintrag einer Liste", nameof(CsvLayout.ItemPerRow))];

    public static List<SelectListItem> Delimiters => [new("Semikolon ;", ";"), new("Komma ,", ","), new("Tabulator", "\t"), new("Senkrechter Strich |", "|")];

    public static List<SelectListItem> Decimals => [new("Komma (1234,50)", ","), new("Punkt (1234.50)", ".")];

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id is { } id)
        {
            Profile = await db.ExportProfile.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);
            if (Profile is null)
            {
                return NotFound();
            }

            var o = ExportService.Options(Profile);
            Input = new InputModel
            {
                Name = Profile.Name, Description = Profile.Description, TargetSchemaId = Profile.TargetSchemaId, Format = o.Format, Groups = string.Join(", ", o.Groups), Exclude = string.Join(", ", o.Exclude),
                CsvDelimiter = o.CsvDelimiter, CsvDecimalSeparator = o.CsvDecimalSeparator, CsvLayout = o.CsvLayout, RowGroup = o.RowGroup, CsvLabels = o.CsvLabels,
                XmlRoot = o.XmlRoot, XmlInvoice = o.XmlInvoice, XmlItem = o.XmlItem, JsonIndented = o.JsonIndented,
            };
        }
        else
        {
            Input.TargetSchemaId = (await definitions.DefaultSchemaAsync()).Id;
        }

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Schemas = [.. (await db.TargetSchema.AsNoTracking().OrderBy(s => s.Name).ToListAsync()).Select(s => new SelectListItem(s.Name, s.Id.ToString()))];
        if (await definitions.ActiveSchemaVersionAsync(Input.TargetSchemaId) is { } version)
        {
            var schema = DefinitionService.Definition(version);
            GroupItems = [.. schema.Groups.Select(g => new SelectListItem(g.Label, g.Key) { Group = new SelectListGroup { Name = g.Key + (g.Repeating ? " (Liste)" : "") } })];
            RepeatingItems = [.. schema.Groups.Where(g => g.Repeating).Select(g => new SelectListItem(g.Label, g.Key))];
            FieldItems = [.. schema.AllFields().Select(x => new SelectListItem($"{x.Group.Label}: {x.Field.Label}", $"{x.Group.Key}.{x.Field.Key}") { Group = new SelectListGroup { Name = $"{x.Group.Key}.{x.Field.Key}" } })];
        }

        var title = Profile?.Name ?? (Id is null ? "Neuer Export" : Input.Name);
        Head(title, "Felder, die nicht exportiert werden sollen, wählst du unter „Ohne diese Felder“.", ("Exporte", "/Exports"), (title, null));
        ViewData["ActiveHref"] = "/Exports";
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var profile = Id is { } id ? await db.ExportProfile.FirstOrDefaultAsync(e => e.Id == id) : null;
        if (Id is not null && profile is null)
        {
            return NotFound();
        }

        if (Input.Format == ExportFormat.Csv && Input.CsvLayout == CsvLayout.ItemPerRow && string.IsNullOrWhiteSpace(Input.RowGroup))
        {
            ModelState.AddModelError("Input.RowGroup", "Wähle die Liste, deren Einträge die Zeilen sind.");
        }

        if (!ModelState.IsValid)
        {
            Profile = profile;
            await LoadAsync();
            return Page();
        }

        static List<string> Split(string? s) => [.. (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        var options = new ExportOptions
        {
            Format = Input.Format, Groups = Split(Input.Groups), Exclude = Split(Input.Exclude), CsvDelimiter = string.IsNullOrEmpty(Input.CsvDelimiter) ? ";" : Input.CsvDelimiter,
            CsvDecimalSeparator = Input.CsvDecimalSeparator == "." ? "." : ",", CsvLayout = Input.CsvLayout, RowGroup = Input.RowGroup, CsvLabels = Input.CsvLabels,
            XmlRoot = Input.XmlRoot, XmlInvoice = Input.XmlInvoice, XmlItem = Input.XmlItem, JsonIndented = Input.JsonIndented,
        };

        var isNew = profile is null;
        profile ??= db.ExportProfile.Add(new ExportProfile()).Entity;
        profile.Name = Input.Name.Trim();
        profile.Description = string.IsNullOrWhiteSpace(Input.Description) ? null : Input.Description.Trim();
        profile.TargetSchemaId = Input.TargetSchemaId;
        profile.OptionsJson = CoreJson.Serialize(options);
        await db.SaveChangesAsync();
        await log.LogAsync(isNew ? "Export angelegt" : "Export geändert", nameof(ExportProfile), profile.Id, profile.Name);
        Flash("Gespeichert.");
        return RedirectToPage(new { id = profile.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var profile = Id is { } id ? await db.ExportProfile.FirstOrDefaultAsync(e => e.Id == id) : null;
        if (profile is not null)
        {
            db.ExportProfile.Remove(profile);
            await db.SaveChangesAsync();
            await log.LogAsync("Export gelöscht", nameof(ExportProfile), profile.Id, profile.Name);
            Flash("Der Export ist gelöscht.");
        }

        return RedirectToPage("/Exports/Index");
    }
}
