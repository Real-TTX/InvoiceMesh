using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InvoiceMesh.Configuration;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using InvoiceMesh.Services.Automation;
using InvoiceMesh.Storage;
using InvoiceMesh.UiKit;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Api;

/// <summary>
/// The API for other systems, under /api/v1, with a key instead of a sign-in ("Authorization: Bearer im_…" or "X-Api-Key"). A key may take documents in,
/// read them, or both. Taking in is the same as an upload (ZIPs unpacked, duplicates reported, processing in the background); reading gives the state, the
/// data, the report and the files of a document. Described in docs/OPERATIONS.md.
/// </summary>
public static class InvoiceApi
{
    private const string KeyItem = "im.apikey";
    private const int MaxFilesPerRequest = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void MapInvoiceApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").AllowAnonymous().DisableAntiforgery();
        api.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var key = await http.RequestServices.GetRequiredService<ApiKeyService>().ValidateAsync(ApiKeyService.Presented(http.Request), http.RequestAborted);
            if (key is null)
            {
                http.Response.Headers.WWWAuthenticate = "Bearer";
                return Error(401, "Kein gültiger API-Schlüssel. Er gehört in den Header „Authorization: Bearer im_…“ oder „X-Api-Key“.");
            }

            http.Items[KeyItem] = key;
            http.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });

        api.MapPost("/invoices", ImportAsync);
        api.MapGet("/invoices", ListAsync);
        api.MapGet("/invoices/{id:long}", GetAsync);
        api.MapGet("/invoices/{id:long}/normalized", async (long id, HttpContext http, InvoiceService invoices)
            => await ReadAsync(http, invoices, id, ctx => ctx.NormalizedJson(), "Der Beleg ist noch nicht normalisiert."));
        api.MapGet("/invoices/{id:long}/extracted", async (long id, HttpContext http, InvoiceService invoices)
            => await ReadAsync(http, invoices, id, ctx => ctx.ExtractedJson(), "Der Beleg ist noch nicht ausgelesen."));
        api.MapGet("/invoices/{id:long}/report", async (long id, HttpContext http, InvoiceService invoices)
            => await ReadAsync(http, invoices, id, ctx => ctx.ReportJson(), "Der Beleg ist noch nicht geprüft."));
        api.MapGet("/invoices/{id:long}/original", OriginalAsync);
        api.MapGet("/invoices/{id:long}/xrechnung", XRechnungAsync);
        api.MapGet("/invoices/{id:long}/files/{fileId:long}", FileAsync);
    }

    private static ApiKey Key(HttpContext http) => (ApiKey)http.Items[KeyItem]!;

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, Json, statusCode: status);

    private static IResult? Forbidden(HttpContext http, bool import)
        => (import ? Key(http).CanImport : Key(http).CanRead) ? null : Error(403, import ? "Dieser API-Schlüssel darf keine Belege einliefern." : "Dieser API-Schlüssel darf keine Belege lesen.");

    /// <summary>
    /// Takes documents in: a multipart form with one or more files (any field name), or the bytes of one file as the body with its name in
    /// "X-File-Name" or "?fileName=". Answers 202 with the new documents; 422 when none could be taken.
    /// </summary>
    private static async Task<IResult> ImportAsync(HttpContext http, IntakeService intake, ConfigStore config, ActivityLogger log, NavCounts counts)
    {
        if (Forbidden(http, import: true) is { } forbidden)
        {
            return forbidden;
        }

        var key = Key(http);
        var detail = $"API-Schlüssel „{key.Name}“";
        var outcome = new IntakeOutcome();
        var ct = http.RequestAborted;
        if (http.Request.HasFormContentType)
        {
            var form = await http.Request.ReadFormAsync(ct);
            if (form.Files.Count == 0)
            {
                return Error(400, "Das Formular enthält keine Datei.");
            }

            foreach (var file in form.Files.Take(MaxFilesPerRequest))
            {
                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer, ct);
                outcome.Add(await intake.AddAsync(file.FileName, buffer.ToArray(), InvoiceSource.Api, detail, ct));
            }
        }
        else
        {
            var name = http.Request.Headers["X-File-Name"].FirstOrDefault() ?? http.Request.Query["fileName"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(name))
            {
                return Error(400, "Der Dateiname fehlt: Header „X-File-Name“ oder „?fileName=“. Mehrere Dateien gehen als multipart/form-data.");
            }

            using var buffer = new MemoryStream();
            await http.Request.Body.CopyToAsync(buffer, ct);
            outcome.Add(await intake.AddAsync(Path.GetFileName(Uri.UnescapeDataString(name)), buffer.ToArray(), InvoiceSource.Api, detail, ct));
        }

        counts.Invalidate();
        if (outcome.Added.Count > 0)
        {
            await log.LogAsync("Belege über die API eingeliefert", "Invoice", outcome.Added.Count == 1 ? outcome.Added[0].Document.Id : null, $"{Ui.CountNoun(outcome.Added.Count, "Belege")}, {detail}");
        }

        var body = new
        {
            documents = outcome.Added.Select(a => new
            {
                id = a.Document.Id,
                fileName = a.Document.FileName,
                processingStatus = a.Document.ProcessingStatus,
                duplicateOf = a.DuplicateOf?.Id,
                url = $"/api/v1/invoices/{a.Document.Id}",
            }),
            problems = outcome.Problems,
        };
        return Results.Json(body, Json, statusCode: outcome.Added.Count > 0 ? 202 : 422);
    }

    /// <summary>The documents, newest first: since a moment of receipt, by processing state, at most 500.</summary>
    private static async Task<IResult> ListAsync(HttpContext http, ImDbContext db, DateTime? since, string? status, int? limit)
    {
        if (Forbidden(http, import: false) is { } forbidden)
        {
            return forbidden;
        }

        var query = db.Invoice.AsNoTracking();
        if (since is { } s)
        {
            var utc = s.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : s.ToUniversalTime();
            query = query.Where(i => i.ReceivedDate >= utc);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<ProcessingStatus>(status, ignoreCase: true, out var wanted))
            {
                return Error(400, $"Unbekannter Status „{status}“. Möglich: {string.Join(", ", Enum.GetNames<ProcessingStatus>().Select(JsonNamingPolicy.CamelCase.ConvertName))}.");
            }

            query = query.Where(i => i.ProcessingStatus == wanted);
        }

        var documents = await query.OrderByDescending(i => i.ReceivedDate).Take(Math.Clamp(limit ?? 100, 1, 500)).ToListAsync(http.RequestAborted);
        return Results.Json(new { documents = documents.Select(Summary) }, Json);
    }

    private static async Task<IResult> GetAsync(long id, HttpContext http, InvoiceService invoices)
    {
        if (Forbidden(http, import: false) is { } forbidden)
        {
            return forbidden;
        }

        var ctx = await invoices.LoadAsync(id, http.RequestAborted);
        if (ctx is null)
        {
            return Error(404, "Den Beleg gibt es nicht.");
        }

        var d = ctx.Doc;
        var body = new
        {
            summary = Summary(d),
            format = ctx.Format,
            processingError = d.ProcessingError,
            schema = ctx.Report is { } r ? new { name = r.SchemaName, version = r.SchemaVersion, mapping = r.MappingName, mappingVersion = r.MappingVersion } : null,
            files = ctx.Files.Select(f => new
            {
                id = f.Id,
                kind = f.Kind,
                fileName = f.StoredFile?.FileName,
                current = f.RevisionId is null || f.RevisionId == d.CurrentRevisionId,
                check = f.Check,
                url = $"/api/v1/invoices/{d.Id}/files/{f.Id}",
            }),
            links = new
            {
                normalized = $"/api/v1/invoices/{d.Id}/normalized",
                extracted = $"/api/v1/invoices/{d.Id}/extracted",
                report = $"/api/v1/invoices/{d.Id}/report",
                original = $"/api/v1/invoices/{d.Id}/original",
                xrechnung = $"/api/v1/invoices/{d.Id}/xrechnung",
            },
        };
        return Results.Json(body, Json);
    }

    private static object Summary(InvoiceDocument d) => new
    {
        id = d.Id,
        fileName = d.FileName,
        source = d.Source,
        sourceDetail = d.SourceDetail,
        receivedDate = DateTime.SpecifyKind(d.ReceivedDate, DateTimeKind.Utc),
        processingStatus = d.ProcessingStatus,
        processedDate = d.ProcessedDate is { } p ? DateTime.SpecifyKind(p, DateTimeKind.Utc) : (DateTime?)null,
        validationStatus = d.ValidationStatus,
        errors = d.ErrorCount,
        warnings = d.WarningCount,
        needsReview = d.NeedsReview,
        xrechnungReady = d.XRechnungReady,
        format = d.FormatSummary ?? d.FormatCode,
        invoiceNumber = d.InvoiceNumber,
        invoiceDate = d.InvoiceDate,
        dueDate = d.DueDate,
        seller = d.SellerName,
        buyer = d.BuyerName,
        currency = d.Currency,
        grossTotal = d.GrossTotal,
        sha256 = d.Sha256,
    };

    private static async Task<IResult> ReadAsync(HttpContext http, InvoiceService invoices, long id, Func<InvoiceContext, string?> json, string notYet)
    {
        if (Forbidden(http, import: false) is { } forbidden)
        {
            return forbidden;
        }

        var ctx = await invoices.LoadAsync(id, http.RequestAborted);
        if (ctx is null)
        {
            return Error(404, "Den Beleg gibt es nicht.");
        }

        return json(ctx) is { } text ? Results.Text(text, "application/json", Encoding.UTF8) : Error(409, notYet);
    }

    private static async Task<IResult> OriginalAsync(long id, HttpContext http, ImDbContext db, FileStoreService files)
    {
        if (Forbidden(http, import: false) is { } forbidden)
        {
            return forbidden;
        }

        var doc = await db.Invoice.AsNoTracking().Include(i => i.OriginalFile).FirstOrDefaultAsync(i => i.Id == id, http.RequestAborted);
        return doc?.OriginalFile is null ? Error(404, "Den Beleg gibt es nicht.") : await FileResults.SendAsync(http, files, doc.OriginalFile, doc.FileName, forceDownload: true);
    }

    /// <summary>The newest XRechnung of the current data (UBL or CII, whichever was made last).</summary>
    private static async Task<IResult> XRechnungAsync(long id, HttpContext http, ImDbContext db, FileStoreService files)
    {
        if (Forbidden(http, import: false) is { } forbidden)
        {
            return forbidden;
        }

        var doc = await db.Invoice.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, http.RequestAborted);
        if (doc is null)
        {
            return Error(404, "Den Beleg gibt es nicht.");
        }

        var file = await db.GeneratedFile.AsNoTracking().Include(f => f.StoredFile)
            .Where(f => f.InvoiceId == id && (f.Kind == GeneratedKind.XRechnungUbl || f.Kind == GeneratedKind.XRechnungCii) && (f.RevisionId == null || f.RevisionId == doc.CurrentRevisionId))
            .OrderByDescending(f => f.Id).FirstOrDefaultAsync(http.RequestAborted);
        return file?.StoredFile is null
            ? Error(409, doc.XRechnungReady ? "Für die aktuellen Daten wurde noch keine XRechnung erzeugt." : "Aus diesem Beleg kann keine XRechnung erzeugt werden; der Prüfbericht sagt, was fehlt.")
            : await FileResults.SendAsync(http, files, file.StoredFile, file.StoredFile.FileName, forceDownload: true);
    }

    private static async Task<IResult> FileAsync(long id, long fileId, HttpContext http, ImDbContext db, FileStoreService files)
    {
        if (Forbidden(http, import: false) is { } forbidden)
        {
            return forbidden;
        }

        var file = await db.GeneratedFile.AsNoTracking().Include(f => f.StoredFile).FirstOrDefaultAsync(f => f.Id == fileId && f.InvoiceId == id, http.RequestAborted);
        return file?.StoredFile is null ? Error(404, "Die Datei gibt es nicht.") : await FileResults.SendAsync(http, files, file.StoredFile, file.StoredFile.FileName, forceDownload: true);
    }
}
