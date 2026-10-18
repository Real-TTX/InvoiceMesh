using InvoiceMesh.Core.Output;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using InvoiceMesh.UiKit;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Pages.Exports;

/// <summary>The configured exports: which schema, which format (JSON, CSV, XML) and how. Each can be run over the documents of a period.</summary>
public sealed class IndexModel(ImDbContext db) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Exporte", "Normalisierte Daten als JSON, CSV oder XML, so wie du sie brauchst.", ("Exporte", null));
        var spec = new ListSpec<ExportProfile>
        {
            Id = "exports",
            Action = "/Exports",
            Label = "Exporte",
            Noun = "Exporte",
            SearchPlaceholder = "Name ...",
            Search = (q, text) => q.Where(e => EF.Functions.ILike(e.Name, Like.Contains(text)) || EF.Functions.ILike(e.Description ?? "", Like.Contains(text))),
            Sorts = new Dictionary<string, (string, Func<IQueryable<ExportProfile>, bool, IOrderedQueryable<ExportProfile>>)>
            {
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(e => e.Name) : q.OrderBy(e => e.Name)),
            },
            DefaultSort = "name",
        };
        var result = await ListBuilder.RunAsync(db.ExportProfile.AsNoTracking().Include(e => e.TargetSchema), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        Table = TableModel.Of("export-table", "Exporte", result.Items,
        [
            (new TableColumn("Export"), e => Ui.NameWithSub(e.Name, $"/Exports/Edit?id={e.Id}", e.Description)),
            (new TableColumn("Format"), e => Ui.Format(ExportService.Options(e).Format.ToString().ToUpperInvariant())),
            (new TableColumn("Zielschema", HideOnPhone: true), e => Ui.Text(e.TargetSchema?.Name)),
            (new TableColumn("Aufbau", HideOnPhone: true), e => Ui.Text(Describe(ExportService.Options(e)))),
        ],
        e => [new RowAction("download", "Ausführen", $"/Exports/Run?id={e.Id}"), new RowAction("edit", "Bearbeiten", $"/Exports/Edit?id={e.Id}")])
        with { EmptyTitle = "Noch kein Export", EmptyText = "Lege einen Export an: Schema, Format, Felder.", EmptyIcon = "download" };
    }

    public static string Describe(ExportOptions o) => o.Format switch
    {
        ExportFormat.Csv => (o.CsvLayout == CsvLayout.ItemPerRow ? $"eine Zeile je Eintrag von „{o.RowGroup}“" : "eine Zeile je Beleg") + $", Trenner „{o.CsvDelimiter}“, Dezimal „{o.CsvDecimalSeparator}“",
        ExportFormat.Xml => $"<{o.XmlRoot}> / <{o.XmlInvoice}> / <{o.XmlItem}>",
        _ => o.JsonIndented ? "eingerückt" : "kompakt",
    } + (o.Groups.Count > 0 ? $" · Gruppen: {string.Join(", ", o.Groups)}" : " · alle Gruppen");
}
