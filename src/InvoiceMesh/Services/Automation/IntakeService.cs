using System.Collections.Concurrent;
using InvoiceMesh.Configuration;
using InvoiceMesh.Core.Extraction;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.UiKit;
using InvoiceMesh.Storage;
using MailKit;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Services.Automation;

/// <summary>What came in: the documents (with the one each duplicates, if any) and what could not be taken in, in words.</summary>
public sealed class IntakeOutcome
{
    public List<InvoiceService.AddResult> Added { get; } = [];

    public List<string> Problems { get; } = [];

    public int Duplicates => Added.Count(a => a.DuplicateOf is not null);

    public void Add(IntakeOutcome other)
    {
        Added.AddRange(other.Added);
        Problems.AddRange(other.Problems);
    }
}

/// <summary>
/// Takes files in, whatever way they came (upload, API, folder, mail): one file is one document; a ZIP is unpacked and each file in it is one. Each document
/// keeps where it came from. The intake sources (folders, shares, mailboxes) are looked at when they are due.
/// </summary>
public sealed class IntakeService(
    ImDbContext db,
    InvoiceService invoices,
    ConfigStore config,
    FolderFactory folders,
    MailboxReader mailboxes,
    ActivityLogger log,
    TimeProvider clock,
    ILogger<IntakeService> logger)
{
    /// <summary>A file in a folder is only taken when it has not changed for this long (it may still be being written).</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    private const int MaxFilesPerRun = 200;

    private static readonly ConcurrentDictionary<long, bool> Running = new();

    private long MaxFileBytes => config.Current.Processing.MaxUploadMb * 1024L * 1024L;

    /// <summary>Takes one file in; a ZIP (by name and content) is unpacked first.</summary>
    public async Task<IntakeOutcome> AddAsync(string fileName, byte[] bytes, InvoiceSource source, string? detail, CancellationToken ct = default)
    {
        var outcome = new IntakeOutcome();
        if (!Archives.IsZip(fileName, bytes))
        {
            await AddOneAsync(outcome, fileName, bytes, source, detail, ct);
            return outcome;
        }

        var content = Archives.Expand(bytes, MaxFileBytes, Math.Max(MaxFileBytes * 20, 1024L * 1024 * 1024));
        outcome.Problems.AddRange(content.Skipped.Select(s => $"{fileName}: {s}"));
        if (content.Files.Count == 0 && content.Skipped.Count == 0)
        {
            outcome.Problems.Add($"„{fileName}“ enthält keine Dateien.");
        }

        foreach (var file in content.Files)
        {
            var inner = $"aus {fileName}: {file.Name}";
            await AddOneAsync(outcome, Path.GetFileName(file.Name), file.Bytes, source, detail is null ? inner : $"{detail} · {inner}", ct);
        }

        return outcome;
    }

    private async Task AddOneAsync(IntakeOutcome outcome, string fileName, byte[] bytes, InvoiceSource source, string? detail, CancellationToken ct)
    {
        try
        {
            outcome.Added.Add(await invoices.AddAsync(bytes, fileName, source, detail, ct));
        }
        catch (InvoiceException ex)
        {
            outcome.Problems.Add(ex.Message);
        }
    }

    // ------------------------------------------------------------------------------------------------ intake sources

    /// <summary>Looks at every source that is due. Called by the scheduler every half minute.</summary>
    public async Task RunDueAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var due = await db.IntakeSource.Where(s => s.IsEnabled && (s.NextRunDate == null || s.NextRunDate <= now)).Select(s => s.Id).ToListAsync(ct);
        foreach (var id in due)
        {
            var source = await db.IntakeSource.FirstAsync(s => s.Id == id, ct);
            await RunAsync(source, manual: false, ct);
        }
    }

    /// <summary>Looks at one source now: takes in what is there and writes down how it went. Never throws for a problem of the source itself.</summary>
    public async Task<IntakeOutcome> RunAsync(IntakeSource source, bool manual, CancellationToken ct = default)
    {
        var outcome = new IntakeOutcome();
        if (!Running.TryAdd(source.Id, true))
        {
            outcome.Problems.Add("Dieser Eingang wird gerade schon abgeholt.");
            return outcome;
        }

        var started = clock.GetUtcNow().UtcDateTime;
        string? failure = null;
        try
        {
            outcome = source.Kind == IntakeKind.Mailbox ? await mailboxes.FetchAsync(source, this, ct) : await FetchFolderAsync(source, ct);
        }
        catch (Exception ex) when (ex is StorageException or IOException or UnauthorizedAccessException or MailKit.Security.AuthenticationException or MailKit.Net.Imap.ImapCommandException
                                       or MailKit.Net.Imap.ImapProtocolException or ServiceNotConnectedException or ServiceNotAuthenticatedException or FolderNotFoundException
                                       or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException or TimeoutException)
        {
            failure = ex.Message;
            logger.LogWarning(ex, "The intake source {Name} could not be read.", source.Name);
        }
        finally
        {
            Running.TryRemove(source.Id, out _);
        }

        source.LastRunDate = started;
        source.NextRunDate = started.AddMinutes(Math.Clamp(source.IntervalMinutes, 1, 1440));
        source.LastRunOk = failure is null;
        source.FailedAttempts = failure is null ? 0 : source.FailedAttempts + 1;
        source.LastCount = outcome.Added.Count;
        source.LastStatus = failure is not null ? $"Fehler: {failure}"
            : outcome.Added.Count == 0 && outcome.Problems.Count == 0 ? "Nichts Neues."
            : $"{Ui.CountNoun(outcome.Added.Count, "Belege")} aufgenommen" + (outcome.Duplicates > 0 ? $", {outcome.Duplicates} davon doppelt" : "") + (outcome.Problems.Count > 0 ? $". Nicht aufgenommen: {string.Join(" ", outcome.Problems.Take(3))}" : ".");
        if (source.LastStatus.Length > 1000)
        {
            source.LastStatus = source.LastStatus[..1000];
        }

        await db.SaveChangesAsync(ct);
        if (failure is not null)
        {
            outcome.Problems.Insert(0, failure);
        }

        if (manual || outcome.Added.Count > 0 || failure is not null)
        {
            await log.LogAsync(failure is null ? "Eingang abgeholt" : "Eingang fehlgeschlagen", nameof(IntakeSource), source.Id, $"{source.Name}: {source.LastStatus}");
        }

        return outcome;
    }

    /// <summary>Tries the settings: can the folder or the mailbox be reached? Nothing is taken in.</summary>
    public async Task<(bool Ok, string Message)> CheckAsync(IntakeSource source, CancellationToken ct = default)
    {
        try
        {
            return source.Kind == IntakeKind.Mailbox ? await mailboxes.CheckAsync(source, ct) : await folders.For(source).CheckAsync(ct);
        }
        catch (Exception ex) when (ex is StorageException or IOException or MailKit.Security.AuthenticationException or MailKit.Net.Imap.ImapCommandException or MailKit.Net.Imap.ImapProtocolException
                                       or FolderNotFoundException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException or TimeoutException)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// The files of a folder that are settled: each is taken in, then moved below the folder ("verarbeitet", or "fehler" when nothing of it could be taken)
    /// or, without a done folder, deleted. Files that are still being written wait for the next run.
    /// </summary>
    private async Task<IntakeOutcome> FetchFolderAsync(IntakeSource source, CancellationToken ct)
    {
        var outcome = new IntakeOutcome();
        var folder = folders.For(source);
        var now = clock.GetUtcNow().UtcDateTime;
        var files = (await folder.ListAsync(ct)).Where(f => now - f.ModifiedUtc >= Settle).OrderBy(f => f.ModifiedUtc).Take(MaxFilesPerRun).ToList();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            IntakeOutcome one;
            if (file.Size > Math.Max(MaxFileBytes, 200L * 1024 * 1024))
            {
                one = new IntakeOutcome();
                one.Problems.Add($"„{file.Name}“ ist zu groß.");
            }
            else
            {
                one = await AddAsync(file.Name, await folder.ReadAsync(file.Name, ct), InvoiceSource.Folder, $"{source.Name}: {folder.Describe}", ct);
            }

            outcome.Add(one);
            var stamped = $"{Fmt.Local(now):yyyyMMdd-HHmmss}_{file.Name}";
            if (one.Added.Count == 0)
            {
                await folder.MoveAsync(file.Name, "fehler", stamped, ct);
            }
            else if (string.IsNullOrWhiteSpace(source.DoneFolder))
            {
                await folder.DeleteAsync(file.Name, ct);
            }
            else
            {
                await folder.MoveAsync(file.Name, source.DoneFolder.Trim(), stamped, ct);
            }
        }

        return outcome;
    }

    /// <summary>"alle 15 Minuten", for lists.</summary>
    public static string Describe(IntakeSource s) => s.IntervalMinutes switch
    {
        1 => "jede Minute",
        < 60 => $"alle {s.IntervalMinutes} Minuten",
        60 => "stündlich",
        _ when s.IntervalMinutes % 60 == 0 => $"alle {s.IntervalMinutes / 60} Stunden",
        _ => $"alle {s.IntervalMinutes} Minuten",
    };

    public static string Where(IntakeSource s) => s.Kind == IntakeKind.Mailbox
        ? $"{s.UserName}@{s.Host} · {s.MailFolder}"
        : FolderFactory.Where(s.Kind, s.Path, s.Host, s.Share);
}
