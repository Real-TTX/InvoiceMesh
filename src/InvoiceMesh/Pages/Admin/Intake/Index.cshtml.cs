using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using InvoiceMesh.Services.Automation;
using InvoiceMesh.UiKit;
using Microsoft.AspNetCore.Html;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Pages.Admin.Intake;

/// <summary>The ways documents come in by themselves: folders, shares of a NAS, mailboxes.</summary>
public sealed class IndexModel(ImDbContext db) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public static string Of(IntakeKind k) => k switch
    {
        IntakeKind.Folder => "Ordner",
        IntakeKind.Smb => "NAS (SMB-Freigabe)",
        _ => "E-Mail-Postfach (IMAP)",
    };

    public static IHtmlContent Last(IntakeSource s) => s.LastRunDate is null ? Ui.Text("noch nie")
        : s.LastRunOk ? Ui.Pill(s.LastCount > 0 ? $"{s.LastCount} neu" : "ok", "ok") : Ui.Pill("fehlgeschlagen", "error");

    public async Task OnGetAsync()
    {
        Head("Eingänge", "Ordner, NAS-Freigaben und Postfächer, aus denen Belege von selbst kommen.", ("Einstellungen", null), ("Eingänge", null));
        ViewData["ActiveHref"] = "/Admin/Intake";

        var spec = new ListSpec<IntakeSource>
        {
            Id = "intake",
            Action = "/Admin/Intake",
            Label = "Eingänge",
            Noun = "Eingänge",
            SearchPlaceholder = "Name, Ordner oder Server ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(s => EF.Functions.ILike(s.Name, like) || EF.Functions.ILike(s.Path, like) || EF.Functions.ILike(s.Host ?? "", like) || EF.Functions.ILike(s.UserName ?? "", like));
            },
            Filters =
            [
                new FilterSpec<IntakeSource>("art", "Art", ListBuilder.EnumOptions<IntakeKind>(Of), (q, v) => q.Where(s => s.Kind == Enum.Parse<IntakeKind>(v))),
                new FilterSpec<IntakeSource>("status", "Status", [new("aktiv", "Aktiv"), new("aus", "Ausgeschaltet"), new("fehler", "Zuletzt fehlgeschlagen")],
                    (q, v) => v switch { "aus" => q.Where(s => !s.IsEnabled), "fehler" => q.Where(s => !s.LastRunOk), _ => q.Where(s => s.IsEnabled) }),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<IntakeSource>, bool, IOrderedQueryable<IntakeSource>>)>
            {
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(s => s.Name) : q.OrderBy(s => s.Name)),
                ["zuletzt"] = ("Zuletzt abgeholt", (q, d) => d ? q.OrderByDescending(s => s.LastRunDate) : q.OrderBy(s => s.LastRunDate)),
            },
            DefaultSort = "name",
        };

        var result = await ListBuilder.RunAsync(db.IntakeSource.AsNoTracking(), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        Table = TableModel.Of("intake-table", "Eingänge", result.Items,
        [
            (new TableColumn("Name"), s => Ui.NameWithSub(s.Name, $"/Admin/Intake/Edit?id={s.Id}", Of(s.Kind))),
            (new TableColumn("Ort", Mono: true, HideOnPhone: true), s => Ui.Text(IntakeService.Where(s))),
            (new TableColumn("Abholen", HideOnPhone: true), s => Ui.Text(IntakeService.Describe(s))),
            (new TableColumn("Zuletzt", HideOnPhone: true), s => Ui.Text(s.LastRunDate is null ? "" : Fmt.Moment(s.LastRunDate))),
            (new TableColumn("Ergebnis"), Last),
            (new TableColumn("Status"), s => s.IsEnabled ? Ui.Pill("aktiv", "ok") : Ui.Pill("aus", "plain")),
        ],
        s => [new RowAction("edit", "Bearbeiten", $"/Admin/Intake/Edit?id={s.Id}")]);
        Table = Table with
        {
            EmptyTitle = Toolbar.Total == 0 ? "Noch kein Eingang" : "Nichts gefunden",
            EmptyText = Toolbar.Total == 0 ? "Lege einen Ordner, eine NAS-Freigabe oder ein Postfach an, aus dem Rechnungen von selbst eingelesen werden." : "Ändere die Suche oder den Filter.",
            EmptyIcon = "folder-open",
        };
    }
}
