using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using InvoiceMesh.Configuration;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services.Backup;
using InvoiceMesh.Storage;
using InvoiceMesh.UiKit;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Services.Automation;

/// <summary>
/// Runs planned exports: picks the documents (new since the last run, or of the last days), writes the file of the export and delivers it into a folder,
/// onto a share of a NAS or to an address over HTTP. A run that fails is tried again after 15, 30 and 45 minutes, then at the next planned time; the
/// documents it missed stay "new" until a run delivers them.
/// </summary>
public sealed class ExportPlanService(
    ImDbContext db,
    ExportService exports,
    FolderFactory folders,
    SecretProtector secrets,
    IHttpClientFactory http,
    ActivityLogger log,
    TimeProvider clock,
    ILogger<ExportPlanService> logger)
{
    public const string HttpClientName = "webhook";

    private static readonly ConcurrentDictionary<long, bool> Running = new();

    /// <summary>
    /// "New since the last run" takes the documents finished at least this long ago: a document whose processing is being saved at the moment of the run
    /// has its time already but is not visible yet, and would otherwise fall behind the mark of the delivered ones for good. It comes with the next run.
    /// </summary>
    internal static TimeSpan Settle { get; set; } = TimeSpan.FromSeconds(30);

    public sealed record RunResult(bool Ok, string Message, int Count);

    public async Task RunDueAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var plans = await db.ExportPlan.Where(p => p.IsEnabled).ToListAsync(ct);
        foreach (var plan in plans)
        {
            if (plan.NextRunDate is null)
            {
                plan.NextRunDate = BackupSchedule.Next(plan, now, Fmt.Zone);
                await db.SaveChangesAsync(ct);
                continue;
            }

            if (plan.NextRunDate <= now)
            {
                await RunAsync(plan, manual: false, ct);
            }
        }
    }

    public async Task<RunResult> RunAsync(ExportPlan plan, bool manual, CancellationToken ct = default)
    {
        if (!Running.TryAdd(plan.Id, true))
        {
            return new RunResult(false, "Dieser Export läuft gerade schon.", 0);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        RunResult result;
        try
        {
            result = await ExportAsync(plan, now, ct);
        }
        catch (Exception ex) when (ex is DefinitionException or StorageException or IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException { InnerException: TimeoutException })
        {
            logger.LogWarning(ex, "The planned export {Name} failed.", plan.Name);
            result = new RunResult(false, ex.Message, 0);
        }
        finally
        {
            Running.TryRemove(plan.Id, out _);
        }

        plan.LastRunDate = now;
        plan.LastRunOk = result.Ok;
        plan.LastCount = result.Count;
        plan.LastStatus = result.Ok ? result.Message : $"Fehler: {result.Message}";
        plan.FailedAttempts = result.Ok ? 0 : plan.FailedAttempts + 1;
        if (!manual || plan.NextRunDate is null)
        {
            plan.NextRunDate = !result.Ok && plan.FailedAttempts <= 3 ? now.AddMinutes(15 * plan.FailedAttempts) : BackupSchedule.Next(plan, now, Fmt.Zone);
        }

        await db.SaveChangesAsync(ct);
        await log.LogAsync(result.Ok ? "Geplanter Export ausgeführt" : "Geplanter Export fehlgeschlagen", nameof(ExportPlan), plan.Id, $"{plan.Name}: {plan.LastStatus}");
        return result;
    }

    private async Task<RunResult> ExportAsync(ExportPlan plan, DateTime now, CancellationToken ct)
    {
        var profile = await db.ExportProfile.AsNoTracking().FirstOrDefaultAsync(e => e.Id == plan.ExportProfileId, ct)
            ?? throw new DefinitionException("Den Export, den dieser Plan ausführt, gibt es nicht mehr.");
        var days = Math.Clamp(plan.Days, 1, 3660);
        var after = plan.Selection == ExportSelection.New && plan.CoveredUntil is { } covered ? covered : now.AddDays(-days);
        var until = plan.Selection == ExportSelection.New ? now - Settle : now;
        var ids = await exports.SelectProcessedAsync(after, until, plan.OnlyValid, ct);
        if (ids.Count == 0 && plan.SkipEmpty)
        {
            plan.CoveredUntil = until;
            return new RunResult(true, "Nichts Neues, keine Datei geschrieben.", 0);
        }

        var file = await exports.RunAsync(profile, ids, ct);
        await DeliverAsync(plan, file, ct);
        plan.CoveredUntil = until;
        var skipped = file.Skipped > 0 ? $", {file.Skipped} übersprungen" : "";
        return new RunResult(true, $"{Ui.CountNoun(file.Count, "Belege")}{skipped} → {file.FileName} ({Where(plan)}).", file.Count);
    }

    private async Task DeliverAsync(ExportPlan plan, ExportService.ExportResult file, CancellationToken ct)
    {
        if (plan.Delivery != DeliveryKind.Webhook)
        {
            await folders.For(plan).WriteAsync(file.FileName, file.Bytes, ct);
            return;
        }

        using var content = new ByteArrayContent(file.Bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(file.ContentType);
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = file.FileName };
        using var request = Request(plan, content);
        request.Headers.Add("X-InvoiceMesh-Export", plan.Name);
        request.Headers.Add("X-InvoiceMesh-Count", file.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{plan.Url} antwortet {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
    }

    /// <summary>Tries the delivery: writes and removes a file in the folder, or sends a small test message to the address.</summary>
    public async Task<(bool Ok, string Message)> CheckAsync(ExportPlan plan, CancellationToken ct = default)
    {
        try
        {
            if (plan.Delivery != DeliveryKind.Webhook)
            {
                return await folders.For(plan).CheckAsync(ct);
            }

            using var content = new StringContent("{\"test\":true,\"from\":\"InvoiceMesh\"}", Encoding.UTF8, "application/json");
            using var request = Request(plan, content);
            request.Headers.Add("X-InvoiceMesh-Test", "true");
            using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
            return response.IsSuccessStatusCode
                ? (true, $"{plan.Url} hat die Testnachricht angenommen ({(int)response.StatusCode}).")
                : (false, $"{plan.Url} antwortet {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        catch (Exception ex) when (ex is StorageException or IOException or HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            return (false, ex.Message);
        }
    }

    private HttpRequestMessage Request(ExportPlan plan, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, plan.Url) { Content = content };
        if (!string.IsNullOrWhiteSpace(plan.HeaderName))
        {
            request.Headers.TryAddWithoutValidation(plan.HeaderName.Trim(), secrets.Unprotect(plan.HeaderValueProtected));
        }

        return request;
    }

    public static string Where(ExportPlan p) => p.Delivery switch
    {
        DeliveryKind.Webhook => p.Url ?? "",
        DeliveryKind.Smb => $"\\\\{p.Host}\\{p.Share}\\{p.Path}".TrimEnd('\\'),
        _ => string.IsNullOrWhiteSpace(p.Path) ? "Volume: exporte" : p.Path,
    };

    public static string Describe(ExportPlan p) => p.Selection == ExportSelection.New
        ? "neue Belege seit dem letzten Lauf"
        : $"die Belege der letzten {p.Days} Tage";
}
