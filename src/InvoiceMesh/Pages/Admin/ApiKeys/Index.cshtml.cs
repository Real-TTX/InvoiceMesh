using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using InvoiceMesh.UiKit;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Pages.Admin.ApiKeys;

/// <summary>The keys of the API, and how the API is used.</summary>
public sealed class IndexModel(ImDbContext db, PublicUrl urls, TimeProvider clock) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    /// <summary>The address of the API of this installation, for the examples.</summary>
    public string Base { get; private set; } = "";

    public static string Rights(ApiKey k) => (k.CanImport, k.CanRead) switch
    {
        (true, true) => "Einliefern und lesen",
        (true, false) => "Nur einliefern",
        (false, true) => "Nur lesen",
        _ => "Keine Rechte",
    };

    public async Task OnGetAsync()
    {
        Head("API", "Schlüssel, mit denen andere Systeme Belege einliefern und abrufen.", ("Einstellungen", null), ("API", null));
        ViewData["ActiveHref"] = "/Admin/ApiKeys";
        Base = urls.Absolute("/api/v1");
        var now = clock.GetUtcNow().UtcDateTime;

        var spec = new ListSpec<ApiKey>
        {
            Id = "apikeys",
            Action = "/Admin/ApiKeys",
            Label = "API-Schlüssel",
            Noun = "Schlüssel",
            SearchPlaceholder = "Name ...",
            Search = (q, text) => q.Where(k => EF.Functions.ILike(k.Name, Like.Contains(text)) || EF.Functions.ILike(k.KeyPrefix, Like.Contains(text))),
            Filters =
            [
                new FilterSpec<ApiKey>("status", "Status", [new("aktiv", "Aktiv"), new("aus", "Ausgeschaltet oder abgelaufen")],
                    (q, v) => v == "aus" ? q.Where(k => !k.IsEnabled || k.ExpiresDate <= now) : q.Where(k => k.IsEnabled && (k.ExpiresDate == null || k.ExpiresDate > now))),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<ApiKey>, bool, IOrderedQueryable<ApiKey>>)>
            {
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(k => k.Name) : q.OrderBy(k => k.Name)),
                ["benutzt"] = ("Zuletzt benutzt", (q, d) => d ? q.OrderByDescending(k => k.LastUsedDate) : q.OrderBy(k => k.LastUsedDate)),
            },
            DefaultSort = "name",
        };

        var result = await ListBuilder.RunAsync(db.ApiKey.AsNoTracking(), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        Table = TableModel.Of("apikey-table", "API-Schlüssel", result.Items,
        [
            (new TableColumn("Name"), k => Ui.NameWithSub(k.Name, $"/Admin/ApiKeys/Edit?id={k.Id}", Rights(k))),
            (new TableColumn("Schlüssel", Mono: true, HideOnPhone: true), k => Ui.Text(k.KeyPrefix + "…")),
            (new TableColumn("Zuletzt benutzt", HideOnPhone: true), k => Ui.Text(k.LastUsedDate is null ? "noch nie" : Fmt.Moment(k.LastUsedDate))),
            (new TableColumn("Läuft ab", HideOnPhone: true), k => Ui.Text(k.ExpiresDate is null ? "nie" : Fmt.Date(k.ExpiresDate))),
            (new TableColumn("Status"), k => !k.IsEnabled ? Ui.Pill("aus", "plain") : k.ExpiresDate <= now ? Ui.Pill("abgelaufen", "warn") : Ui.Pill("aktiv", "ok")),
        ],
        k => [new RowAction("edit", "Bearbeiten", $"/Admin/ApiKeys/Edit?id={k.Id}")]);
        Table = Table with
        {
            EmptyTitle = Toolbar.Total == 0 ? "Noch kein Schlüssel" : "Nichts gefunden",
            EmptyText = Toolbar.Total == 0 ? "Lege einen Schlüssel für jedes System an, das Belege einliefern oder abrufen soll." : "Ändere die Suche oder den Filter.",
            EmptyIcon = "key",
        };
    }
}
