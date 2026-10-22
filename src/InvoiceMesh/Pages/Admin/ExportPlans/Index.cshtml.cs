using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using InvoiceMesh.Services.Automation;
using InvoiceMesh.Services.Backup;
using InvoiceMesh.UiKit;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Pages.Admin.ExportPlans;

/// <summary>Exports that run by themselves and deliver their file: into a folder, onto a NAS, to a webhook.</summary>
public sealed class IndexModel(ImDbContext db) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public static string Of(DeliveryKind k) => k switch
    {
        DeliveryKind.Folder => "Ordner",
        DeliveryKind.Smb => "NAS (SMB-Freigabe)",
        _ => "Webhook (HTTP POST)",
    };

    public async Task OnGetAsync()
    {
        Head("Geplante Exporte", "Exporte, die von selbst laufen und ihre Datei abliefern.", ("Einstellungen", null), ("Geplante Exporte", null));
        ViewData["ActiveHref"] = "/Admin/ExportPlans";

        var spec = new ListSpec<ExportPlan>
        {
            Id = "exportplans",
            Action = "/Admin/ExportPlans",
            Label = "Geplante Exporte",
            Noun = "Pläne",
            SearchPlaceholder = "Name oder Ziel ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(p => EF.Functions.ILike(p.Name, like) || EF.Functions.ILike(p.Path, like) || EF.Functions.ILike(p.Url ?? "", like) || EF.Functions.ILike(p.Host ?? "", like));
            },
            Filters =
            [
                new FilterSpec<ExportPlan>("ziel", "Ziel", ListBuilder.EnumOptions<DeliveryKind>(Of), (q, v) => q.Where(p => p.Delivery == Enum.Parse<DeliveryKind>(v))),
                new FilterSpec<ExportPlan>("status", "Status", [new("aktiv", "Aktiv"), new("aus", "Ausgeschaltet"), new("fehler", "Zuletzt fehlgeschlagen")],
                    (q, v) => v switch { "aus" => q.Where(p => !p.IsEnabled), "fehler" => q.Where(p => !p.LastRunOk), _ => q.Where(p => p.IsEnabled) }),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<ExportPlan>, bool, IOrderedQueryable<ExportPlan>>)>
            {
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(p => p.Name) : q.OrderBy(p => p.Name)),
                ["next"] = ("Nächster Lauf", (q, d) => d ? q.OrderByDescending(p => p.NextRunDate) : q.OrderBy(p => p.NextRunDate)),
            },
            DefaultSort = "name",
        };

        var result = await ListBuilder.RunAsync(db.ExportPlan.AsNoTracking().Include(p => p.ExportProfile), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        Table = TableModel.Of("exportplan-table", "Geplante Exporte", result.Items,
        [
            (new TableColumn("Name"), p => Ui.NameWithSub(p.Name, $"/Admin/ExportPlans/Edit?id={p.Id}", $"{p.ExportProfile?.Name ?? "Export gelöscht"} · {ExportPlanService.Describe(p)}")),
            (new TableColumn("Ziel", Mono: true, HideOnPhone: true), p => Ui.Text(ExportPlanService.Where(p))),
            (new TableColumn("Zeitplan", HideOnPhone: true), p => Ui.Text(BackupSchedule.Describe(p))),
            (new TableColumn("Nächster", HideOnPhone: true), p => Ui.Text(p.IsEnabled ? Fmt.Moment(p.NextRunDate) : "")),
            (new TableColumn("Zuletzt"), p => p.LastRunDate is null ? Ui.Text("noch nie") : p.LastRunOk ? Ui.Pill(p.LastCount > 0 ? $"{p.LastCount} Belege" : "ok", "ok") : Ui.Pill("fehlgeschlagen", "error")),
            (new TableColumn("Status"), p => p.IsEnabled ? Ui.Pill("aktiv", "ok") : Ui.Pill("aus", "plain")),
        ],
        p => [new RowAction("edit", "Bearbeiten", $"/Admin/ExportPlans/Edit?id={p.Id}")]);
        Table = Table with
        {
            EmptyTitle = Toolbar.Total == 0 ? "Noch kein geplanter Export" : "Nichts gefunden",
            EmptyText = Toolbar.Total == 0 ? "Lege fest, welcher Export wann läuft und wohin seine Datei geht." : "Ändere die Suche oder den Filter.",
            EmptyIcon = "route",
        };
    }
}
