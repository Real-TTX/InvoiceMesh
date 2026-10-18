using InvoiceMesh.Core.Validation;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Pages;

/// <summary>The overview: how many documents came in and how they fared, what came in per day and in which formats, what happened last, and what needs a look.</summary>
public sealed class IndexModel(ImDbContext db, ICurrentUser current, TimeProvider clock) : PageModelBase
{
    public sealed record Day(DateOnly Date, int Count);

    public sealed record FormatCount(string Code, int Count);

    public int Total { get; private set; }

    public int Good { get; private set; }

    public int Review { get; private set; }

    public int Failed { get; private set; }

    public int Invalid { get; private set; }

    public int XRechnungen { get; private set; }

    public int Processing { get; private set; }

    public List<Day> Days { get; private set; } = [];

    public List<FormatCount> Formats { get; private set; } = [];

    public List<InvoiceDocument> Recent { get; private set; } = [];

    public List<(InvoiceEvent Event, InvoiceDocument? Doc)> Events { get; private set; } = [];

    public List<long> PendingIds { get; private set; } = [];

    public bool IsAdmin => current.IsAdmin;

    public DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task OnGetAsync()
    {
        var hour = Fmt.Local(Now).Hour;
        var greeting = (hour < 11 ? "Guten Morgen" : hour < 18 ? "Willkommen" : "Guten Abend") + (current.Name is { Length: > 0 } name ? ", " + name.Split(' ')[0] : "");
        Head("Übersicht", $"{greeting}. Hier siehst du, was hereinkam und was zu tun ist.", ("Übersicht", null));

        Total = await db.Invoice.CountAsync();
        Good = await db.Invoice.CountAsync(i => i.ProcessingStatus == ProcessingStatus.Processed && i.ValidationStatus != ValidationStatus.Invalid);
        Review = await db.Invoice.CountAsync(i => i.ProcessingStatus == ProcessingStatus.Processed && i.NeedsReview && i.ReviewedDate == null);
        Failed = await db.Invoice.CountAsync(i => i.ProcessingStatus == ProcessingStatus.Failed);
        Invalid = await db.Invoice.CountAsync(i => i.ProcessingStatus == ProcessingStatus.Processed && i.ValidationStatus == ValidationStatus.Invalid);
        XRechnungen = await db.GeneratedFile.Where(f => f.Kind == GeneratedKind.XRechnungUbl || f.Kind == GeneratedKind.XRechnungCii).Select(f => f.InvoiceId).Distinct().CountAsync();
        PendingIds = await db.Invoice.Where(i => i.ProcessingStatus == ProcessingStatus.Queued || i.ProcessingStatus == ProcessingStatus.Processing).Select(i => i.Id).Take(100).ToListAsync();
        Processing = PendingIds.Count;

        // The last 14 days by the local date of receipt.
        var today = Fmt.Today(clock);
        var since = Fmt.UtcFromLocal(today.AddDays(-13).ToDateTime(TimeOnly.MinValue));
        var received = await db.Invoice.Where(i => i.ReceivedDate >= since).Select(i => i.ReceivedDate).ToListAsync();
        var perDay = received.GroupBy(d => DateOnly.FromDateTime(Fmt.Local(d))).ToDictionary(g => g.Key, g => g.Count());
        Days = [.. Enumerable.Range(0, 14).Select(i => today.AddDays(i - 13)).Select(d => new Day(d, perDay.GetValueOrDefault(d)))];

        Formats = [.. (await db.Invoice.GroupBy(i => i.FormatCode).Select(g => new { g.Key, Count = g.Count() }).ToListAsync())
            .OrderByDescending(g => g.Count).Select(g => new FormatCount(g.Key.Length == 0 ? "?" : g.Key, g.Count))];

        Recent = await db.Invoice.AsNoTracking().OrderByDescending(i => i.ReceivedDate).ThenByDescending(i => i.Id).Take(8).ToListAsync();
        var events = await db.InvoiceEvent.AsNoTracking().OrderByDescending(e => e.OccurredDate).ThenByDescending(e => e.Id).Take(8).ToListAsync();
        var ids = events.Select(e => e.InvoiceId).Distinct().ToList();
        var docs = await db.Invoice.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
        Events = [.. events.Select(e => (e, docs.GetValueOrDefault(e.InvoiceId)))];
    }

    public static string Tone(string kind) => kind switch
    {
        "Verarbeitet" or "XRechnung erzeugt" or "Geprüft" or "PDF erzeugt" => "ok",
        "Verarbeitung fehlgeschlagen" or "XRechnung nicht erzeugt" => "error",
        "Korrektur" or "Korrektur zurückgenommen" or "Neu normalisiert" => "info",
        _ => "info",
    };

    public static string Icon(string kind) => kind switch
    {
        "Eingang" => "upload",
        "Verarbeitet" or "Geprüft" => "check-circle",
        "Verarbeitung fehlgeschlagen" or "XRechnung nicht erzeugt" => "x-circle",
        "XRechnung erzeugt" => "file-code",
        "PDF erzeugt" => "file-text",
        "Korrektur" or "Korrektur zurückgenommen" => "edit",
        "Freigegeben" => "share",
        _ => "refresh",
    };
}
