using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using InvoiceMesh.UiKit;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Pages.Mappings;

/// <summary>The mappings: how the sources fill a schema, per format, profile or version. The most specific one that fits a document is used.</summary>
public sealed class IndexModel(ImDbContext db) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Mappings", "Wie Quellfelder aus UBL, CII, XRechnung, ZUGFeRD und dem Text von PDFs und Scans auf die Felder deiner Schemas abgebildet werden.", ("Mappings", null));
        var schemas = await db.TargetSchema.AsNoTracking().OrderBy(s => s.Name).ToListAsync();
        var spec = new ListSpec<MappingSet>
        {
            Id = "mappings",
            Action = "/Mappings",
            Label = "Mappings",
            Noun = "Mappings",
            SearchPlaceholder = "Name, Standard oder Profil ...",
            Search = (q, text) => q.Where(m => EF.Functions.ILike(m.Name, Like.Contains(text)) || EF.Functions.ILike(m.Standard ?? "", Like.Contains(text)) || EF.Functions.ILike(m.Profile ?? "", Like.Contains(text))),
            Filters =
            [
                new FilterSpec<MappingSet>("schema", "Zielschema", [.. schemas.Select(s => new FilterOption(s.Id.ToString(), s.Name))], (q, v) => q.Where(m => m.TargetSchemaId == long.Parse(v))),
                new FilterSpec<MappingSet>("aktiv", "Eingeschaltet", [new("ja", "Eingeschaltet"), new("nein", "Ausgeschaltet")], (q, v) => q.Where(m => m.IsEnabled == (v == "ja"))),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<MappingSet>, bool, IOrderedQueryable<MappingSet>>)>
            {
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(m => m.Name) : q.OrderBy(m => m.Name)),
                ["prio"] = ("Priorität", (q, d) => d ? q.OrderBy(m => m.Priority) : q.OrderByDescending(m => m.Priority)),
            },
            DefaultSort = "name",
        };

        var result = await ListBuilder.RunAsync(db.MappingSet.AsNoTracking().Include(m => m.TargetSchema).Include(m => m.Versions), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        Table = TableModel.Of("mapping-table", "Mappings", result.Items,
        [
            (new TableColumn("Mapping"), m => Ui.NameWithSub(m.Name, $"/Mappings/Edit?id={m.Id}", m.Description)),
            (new TableColumn("Zielschema", HideOnPhone: true), m => Ui.Link(m.TargetSchema?.Name ?? "–", $"/Schemas/Edit?id={m.TargetSchemaId}")),
            (new TableColumn("Gilt für"), m => Ui.Text(DefinitionService.SelectorText(m))),
            (new TableColumn("Priorität", Numeric: true, HideOnPhone: true), m => Ui.Text(m.Priority.ToString())),
            (new TableColumn("Version", HideOnPhone: true), m => m.Versions.Where(v => v.State == VersionState.Active).Select(v => (int?)v.Number).FirstOrDefault() is { } n
                ? Ui.Concat(Ui.Text("v" + n + " "), m.Versions.Any(v => v.State == VersionState.Draft) ? Labels.Pill(VersionState.Draft) : Ui.None())
                : Ui.Pill("keine aktive", "warn")),
            (new TableColumn("Status"), m => m.IsEnabled ? Ui.Pill("an", "ok") : Ui.Pill("aus", "plain")),
        ],
        m => [new RowAction("edit", "Bearbeiten", $"/Mappings/Edit?id={m.Id}"), new RowAction("flask", "Testen", $"/Mappings/Test?id={m.Id}")]);
    }
}
