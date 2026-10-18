using InvoiceMesh.Data;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Services;

/// <summary>What the database says about itself: server version and size.</summary>
public sealed record DatabaseInfo(string? Version, long Bytes);

/// <summary>What the system page shows about the database, and the maintenance it can start. The page itself never touches the database.</summary>
public sealed class SystemService(ImDbContext db, TimeProvider clock)
{
    public async Task<DatabaseInfo> GetDatabaseAsync(CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        try
        {
            await using var version = connection.CreateCommand();
            version.CommandText = "SELECT version()";
            var text = System.Text.RegularExpressions.Regex.Match((string?)await version.ExecuteScalarAsync(ct) ?? "", @"^PostgreSQL [\d.]+").Value;
            await using var size = connection.CreateCommand();
            size.CommandText = "SELECT pg_database_size(current_database())";
            return new DatabaseInfo(text, Convert.ToInt64(await size.ExecuteScalarAsync(ct)));
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    /// <summary>How many rows the main tables hold, in the order the page lists them.</summary>
    public async Task<Dictionary<string, int>> CountRowsAsync(CancellationToken ct) => new()
    {
        ["Benutzer"] = await db.User.CountAsync(ct),
        ["Belege"] = await db.Invoice.CountAsync(ct),
        ["Auslesungen"] = await db.InvoiceExtraction.CountAsync(ct),
        ["Revisionen"] = await db.InvoiceRevision.CountAsync(ct),
        ["Korrekturen"] = await db.InvoiceCorrection.CountAsync(ct),
        ["Erzeugte Dateien"] = await db.GeneratedFile.CountAsync(ct),
        ["Schemas"] = await db.TargetSchema.CountAsync(ct),
        ["Mappings"] = await db.MappingSet.CountAsync(ct),
        ["Dateien"] = await db.StoredFile.CountAsync(ct),
        ["Freigabe-Links"] = await db.ShareLink.CountAsync(ct),
    };

    /// <summary>Removes old sessions, run-out links and old logs now (the daily maintenance does the same).</summary>
    public Task<MaintenanceResult> MaintainAsync(CancellationToken ct) => MaintenanceService.RunAsync(db, clock, ct);
}
