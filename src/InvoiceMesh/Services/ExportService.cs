using System.Text.Json.Nodes;
using InvoiceMesh.Core;
using InvoiceMesh.Core.Output;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Services;

/// <summary>
/// Runs a configured export over documents: the normalized data in the export's schema (the stored revision when it is in the active version of that schema,
/// otherwise normalized for the export without storing anything), written as JSON, CSV or XML by the options of the export.
/// </summary>
public sealed class ExportService(ImDbContext db, DefinitionService definitions, InvoiceService invoices, TimeProvider clock)
{
    public sealed record ExportResult(byte[] Bytes, string ContentType, string FileName, int Count, int Skipped);

    public static ExportOptions Options(ExportProfile p) => CoreJson.Deserialize<ExportOptions>(p.OptionsJson);

    public async Task<ExportResult> RunAsync(ExportProfile profile, IReadOnlyList<long> invoiceIds, CancellationToken ct = default)
    {
        var schemaVersion = await definitions.ActiveSchemaVersionAsync(profile.TargetSchemaId, ct) ?? throw new DefinitionException("Das Schema des Exports hat keine aktive Version.");
        var schema = DefinitionService.Definition(schemaVersion);
        var records = new List<ExportRecord>();
        var skipped = 0;
        foreach (var id in invoiceIds)
        {
            var ctx = await invoices.LoadAsync(id, ct);
            if (ctx?.Extraction is null || ctx.Format is null)
            {
                skipped++;
                continue;
            }

            JsonObject? data;
            if (ctx.Revision?.SchemaVersionId == schemaVersion.Id && ctx.Data is not null)
            {
                data = ctx.Data;
            }
            else if (await definitions.SelectMappingAsync(profile.TargetSchemaId, ctx.Format, ct) is { } mapping)
            {
                data = InvoiceService.NormalizeInMemory(ctx, schema, DefinitionService.Definition(mapping.Version), profile.TargetSchemaId).Data;
            }
            else
            {
                skipped++;
                continue;
            }

            records.Add(new ExportRecord(ctx.Doc.Id, ctx.Doc.InvoiceNumber, data));
        }

        var (bytes, type, extension) = Exporter.Write(records, schema, Options(profile));
        var stamp = Fmt.Local(clock.GetUtcNow().UtcDateTime).ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture);
        var name = Storage.FileStoreService.SafeFileName($"{profile.Name.Replace(' ', '-')}-{stamp}.{extension}");
        return new ExportResult(bytes, type, name, records.Count, skipped);
    }

    /// <summary>The ids of the documents an export takes: the processed ones in a time span of their receipt (local dates), newest first.</summary>
    public async Task<List<long>> SelectAsync(DateOnly? from, DateOnly? to, bool onlyValid, CancellationToken ct = default)
    {
        var query = db.Invoice.Where(i => i.ProcessingStatus == ProcessingStatus.Processed);
        if (from is { } f)
        {
            var start = Fmt.UtcFromLocal(f.ToDateTime(TimeOnly.MinValue));
            query = query.Where(i => i.ReceivedDate >= start);
        }

        if (to is { } t)
        {
            var end = Fmt.UtcFromLocal(t.AddDays(1).ToDateTime(TimeOnly.MinValue));
            query = query.Where(i => i.ReceivedDate < end);
        }

        if (onlyValid)
        {
            query = query.Where(i => i.ValidationStatus != Core.Validation.ValidationStatus.Invalid);
        }

        return await query.OrderByDescending(i => i.ReceivedDate).Select(i => i.Id).Take(5000).ToListAsync(ct);
    }
}
