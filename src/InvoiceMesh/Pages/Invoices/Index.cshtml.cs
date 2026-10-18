using InvoiceMesh.Core.Validation;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using InvoiceMesh.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Pages.Invoices;

/// <summary>All documents: search, filter (state, validation, format, XRechnung, outdated schema), sort; the actions below the list.</summary>
public sealed class IndexModel(ImDbContext db, InvoiceService invoices, ActivityLogger log) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public List<long> PendingIds { get; private set; } = [];

    public int Outdated { get; private set; }

    public async Task OnGetAsync()
    {
        Head("Belege", "Alle eingegangenen Rechnungen, wie sie verarbeitet und geprüft wurden.", ("Belege", null));
        var activeSchemaVersions = db.TargetSchemaVersion.Where(v => v.State == VersionState.Active).Select(v => v.Id);
        var formats = await db.Invoice.Select(i => i.FormatCode).Distinct().OrderBy(c => c).ToListAsync();
        var spec = new ListSpec<InvoiceDocument>
        {
            Id = "invoices",
            Action = "/Invoices",
            Label = "Belege",
            Noun = "Belege",
            SearchPlaceholder = "Nummer, Lieferant, Kunde oder Datei ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(i => EF.Functions.ILike(i.InvoiceNumber ?? "", like) || EF.Functions.ILike(i.SellerName ?? "", like) || EF.Functions.ILike(i.BuyerName ?? "", like)
                                    || EF.Functions.ILike(i.FileName, like) || EF.Functions.ILike(i.Standard ?? "", like));
            },
            Filters =
            [
                new FilterSpec<InvoiceDocument>("status", "Verarbeitung",
                [
                    new("pruefen", "Zu prüfen"), new("fehler", "Fehler (nicht lesbar oder ungültig)"), new("verarbeitet", "Verarbeitet"), new("wartet", "Wartet oder in Verarbeitung"), new("geprueft", "Von einer Person geprüft"),
                ], (q, v) => v switch
                {
                    "pruefen" => q.Where(i => i.ProcessingStatus == ProcessingStatus.Processed && i.NeedsReview && i.ReviewedDate == null),
                    "fehler" => q.Where(i => i.ProcessingStatus == ProcessingStatus.Failed || (i.ProcessingStatus == ProcessingStatus.Processed && i.ValidationStatus == ValidationStatus.Invalid)),
                    "verarbeitet" => q.Where(i => i.ProcessingStatus == ProcessingStatus.Processed),
                    "geprueft" => q.Where(i => i.ReviewedDate != null),
                    _ => q.Where(i => i.ProcessingStatus == ProcessingStatus.Queued || i.ProcessingStatus == ProcessingStatus.Processing),
                }),
                new FilterSpec<InvoiceDocument>("validierung", "Validierung", [new("gueltig", "Gültig (auch mit Warnungen)"), new("warnungen", "Mit Warnungen"), new("ungueltig", "Ungültig")], (q, v) => v switch
                {
                    "gueltig" => q.Where(i => i.ValidationStatus == ValidationStatus.Valid || i.ValidationStatus == ValidationStatus.Warnings),
                    "warnungen" => q.Where(i => i.ValidationStatus == ValidationStatus.Warnings),
                    _ => q.Where(i => i.ValidationStatus == ValidationStatus.Invalid),
                }),
                new FilterSpec<InvoiceDocument>("format", "Format", [.. formats.Select(f => new FilterOption(f, f.Length == 0 ? "?" : f))], (q, v) => q.Where(i => i.FormatCode == v)),
                new FilterSpec<InvoiceDocument>("xrechnung", "XRechnung", [new("ja", "Erzeugt"), new("bereit", "Möglich, noch nicht erzeugt"), new("nein", "Nicht möglich")], (q, v) => v switch
                {
                    "ja" => q.Where(i => db.GeneratedFile.Any(f => f.InvoiceId == i.Id && (f.Kind == GeneratedKind.XRechnungUbl || f.Kind == GeneratedKind.XRechnungCii))),
                    "bereit" => q.Where(i => i.XRechnungReady && !db.GeneratedFile.Any(f => f.InvoiceId == i.Id && (f.Kind == GeneratedKind.XRechnungUbl || f.Kind == GeneratedKind.XRechnungCii))),
                    _ => q.Where(i => !i.XRechnungReady),
                }),
                new FilterSpec<InvoiceDocument>("schema", "Schema", [new("veraltet", "Ältere Schemaversion")], (q, _) => q.Where(i => i.SchemaVersionId != null && !activeSchemaVersions.Contains(i.SchemaVersionId.Value))),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<InvoiceDocument>, bool, IOrderedQueryable<InvoiceDocument>>)>
            {
                ["eingang"] = ("Eingang", (q, d) => d ? q.OrderBy(i => i.ReceivedDate) : q.OrderByDescending(i => i.ReceivedDate).ThenByDescending(i => i.Id)),
                ["datum"] = ("Rechnungsdatum", (q, d) => d ? q.OrderByDescending(i => i.InvoiceDate) : q.OrderBy(i => i.InvoiceDate)),
                ["nummer"] = ("Rechnungsnummer", (q, d) => d ? q.OrderByDescending(i => i.InvoiceNumber) : q.OrderBy(i => i.InvoiceNumber)),
                ["lieferant"] = ("Lieferant", (q, d) => d ? q.OrderByDescending(i => i.SellerName) : q.OrderBy(i => i.SellerName)),
                ["betrag"] = ("Betrag", (q, d) => d ? q.OrderBy(i => i.GrossTotal) : q.OrderByDescending(i => i.GrossTotal)),
            },
            DefaultSort = "eingang",
        };

        var result = await ListBuilder.RunAsync(db.Invoice.AsNoTracking(), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        PendingIds = [.. result.Items.Where(i => i.ProcessingStatus is ProcessingStatus.Queued or ProcessingStatus.Processing).Select(i => i.Id)];
        Outdated = await db.Invoice.CountAsync(i => i.SchemaVersionId != null && !activeSchemaVersions.Contains(i.SchemaVersionId.Value));

        Table = TableModel.Of("invoice-table", "Belege", result.Items,
        [
            (new TableColumn("Beleg"), i => Ui.NameWithSub(i.InvoiceNumber ?? i.FileName, $"/Invoices/Details?id={i.Id}", i.InvoiceNumber is null ? i.ProcessingError : i.FileName)),
            (new TableColumn("Lieferant", HideOnPhone: true), i => Ui.NameWithSub(i.SellerName ?? "–", null, i.BuyerName is null ? null : "an " + i.BuyerName)),
            (new TableColumn("Datum", HideOnPhone: true), i => i.InvoiceDate is null ? Ui.None() : Ui.Text(Fmt.Date(i.InvoiceDate))),
            (new TableColumn("Format"), i => Ui.Format(i.FormatCode)),
            (new TableColumn("Betrag", Numeric: true, HideOnPhone: true), i => Ui.Amount(i.GrossTotal, i.Currency)),
            (new TableColumn("Status"), i => Labels.Pill(i)),
            (new TableColumn("Eingang", HideOnPhone: true), i => Ui.Text(Fmt.Moment(i.ReceivedDate))),
        ],
        i => [new RowAction("eye", "Öffnen", $"/Invoices/Details?id={i.Id}"), new RowAction("columns", "Daten", $"/Invoices/Data?id={i.Id}")]);
        Table = Table with
        {
            EmptyTitle = Toolbar.Total == 0 ? "Noch keine Belege" : "Nichts gefunden",
            EmptyText = Toolbar.Total == 0 ? "Lade Rechnungen hoch: strukturierte (UBL, CII, XRechnung, ZUGFeRD) oder als PDF und Scan." : "Ändere die Suche oder den Filter.",
            EmptyIcon = Toolbar.Total == 0 ? "upload" : "search",
        };
    }

    /// <summary>Normalizes every document of an older schema version again with the active versions.</summary>
    public async Task<IActionResult> OnPostRenormalizeAsync()
    {
        var active = db.TargetSchemaVersion.Where(v => v.State == VersionState.Active).Select(v => v.Id);
        var ids = await db.Invoice.Where(i => i.SchemaVersionId != null && !active.Contains(i.SchemaVersionId.Value) && i.CurrentExtractionId != null).Select(i => i.Id).Take(500).ToListAsync();
        var done = 0;
        var failed = 0;
        foreach (var id in ids)
        {
            var ctx = await invoices.LoadAsync(id, HttpContext.RequestAborted);
            try
            {
                await invoices.RenormalizeAsync(ctx!.Doc, null, HttpContext.RequestAborted);
                done++;
            }
            catch (Exception ex) when (ex is DefinitionException or InvoiceException)
            {
                failed++;
            }
        }

        await log.LogAsync("Belege neu normalisiert", nameof(InvoiceDocument), null, $"{done} neu, {failed} fehlgeschlagen");
        Flash(failed == 0 ? $"{Ui.CountNoun(done, "Belege")} mit den aktiven Versionen neu normalisiert." : $"{done} neu normalisiert, {failed} nicht möglich (kein passendes Mapping).");
        return RedirectToPage();
    }
}
