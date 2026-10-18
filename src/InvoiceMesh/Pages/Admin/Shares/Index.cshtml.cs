using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using InvoiceMesh.UiKit;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Pages.Admin.Shares;

/// <summary>Every link that shows a document to somebody without an account: who has it, how long it holds, how often it was opened.</summary>
public sealed class IndexModel(ImDbContext db, TimeProvider clock) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Freigaben", "Links auf einen Beleg für Personen ohne Konto (Rolle „Anonym“): lesen und herunterladen, für begrenzte Zeit.", ("Einstellungen", null), ("Freigaben", null));
        var now = clock.GetUtcNow().UtcDateTime;

        var spec = new ListSpec<ShareLink>
        {
            Id = "shares",
            Action = "/Admin/Shares",
            Label = "Freigaben",
            Noun = "Freigaben",
            SearchPlaceholder = "Titel, Empfänger, Rechnungsnummer oder Datei ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(l => EF.Functions.ILike(l.Title, like) || EF.Functions.ILike(l.RecipientName ?? "", like) || (l.Invoice != null && (EF.Functions.ILike(l.Invoice.InvoiceNumber ?? "", like) || EF.Functions.ILike(l.Invoice.FileName, like))));
            },
            Filters =
            [
                new FilterSpec<ShareLink>("status", "Status", [new("aktiv", "Gültig"), new("abgelaufen", "Abgelaufen oder widerrufen")], (q, v) => v == "aktiv" ? q.Where(l => l.RevokedDate == null && l.ExpiresDate > now) : q.Where(l => l.RevokedDate != null || l.ExpiresDate <= now)),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<ShareLink>, bool, IOrderedQueryable<ShareLink>>)>
            {
                ["created"] = ("Angelegt", (q, d) => d ? q.OrderBy(l => l.CreateDate) : q.OrderByDescending(l => l.CreateDate)),
                ["expires"] = ("Gültig bis", (q, d) => d ? q.OrderByDescending(l => l.ExpiresDate) : q.OrderBy(l => l.ExpiresDate)),
                ["views"] = ("Aufrufe", (q, d) => d ? q.OrderBy(l => l.ViewCount) : q.OrderByDescending(l => l.ViewCount)),
            },
            DefaultSort = "created",
        };

        var result = await ListBuilder.RunAsync(db.ShareLink.AsNoTracking().Include(l => l.Invoice), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        Table = TableModel.Of("share-table", "Freigaben", result.Items,
        [
            (new TableColumn("Link"), l => Ui.NameWithSub(l.Title, $"/Admin/Shares/Edit?id={l.Id}", l.RecipientName is { Length: > 0 } ? "für " + l.RecipientName : null)),
            (new TableColumn("Beleg", HideOnPhone: true), l => l.Invoice is null ? Ui.None() : Ui.Link(l.Invoice.DisplayName, $"/Invoices/Details?id={l.InvoiceId}")),
            (new TableColumn("Gültig bis"), l => Ui.Text(Fmt.Date(l.ExpiresDate))),
            (new TableColumn("Aufrufe", Numeric: true, HideOnPhone: true), l => Ui.Text(l.ViewCount.ToString())),
            (new TableColumn("Status"), l => l.RevokedDate is not null ? Ui.Pill("widerrufen", "plain") : l.ExpiresDate <= now ? Ui.Pill("abgelaufen", "plain") : Ui.Pill("gültig", "ok")),
        ],
        l => [new RowAction("edit", "Öffnen", $"/Admin/Shares/Edit?id={l.Id}")])
        with { EmptyTitle = Toolbar.Total == 0 ? "Noch keine Freigabe" : "Nichts gefunden", EmptyText = Toolbar.Total == 0 ? "Links entstehen auf der Seite eines Belegs, mit „Freigeben“." : "Ändere die Suche oder den Filter.", EmptyIcon = "share" };
    }
}
