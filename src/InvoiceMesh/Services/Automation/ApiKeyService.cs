using System.Security.Cryptography;
using System.Text;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Services.Automation;

/// <summary>
/// The keys of the API. A key is "im_" and 43 random characters (256 bits); it is shown once when it is made, the database keeps its SHA-256 only, so a key
/// that got lost is replaced, never recovered.
/// </summary>
public sealed class ApiKeyService(ImDbContext db, TimeProvider clock)
{
    public const string Prefix = "im_";

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    public static string NewKey() => Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Makes a key. The plain key is returned here and nowhere else.</summary>
    public async Task<(ApiKey Key, string Plain)> CreateAsync(string name, bool canImport, bool canRead, DateTime? expiresUtc, CancellationToken ct = default)
    {
        var plain = NewKey();
        var key = new ApiKey
        {
            Name = name.Trim(),
            KeyHash = Hash(plain),
            KeyPrefix = plain[..10],
            CanImport = canImport,
            CanRead = canRead,
            ExpiresDate = expiresUtc,
        };
        db.ApiKey.Add(key);
        await db.SaveChangesAsync(ct);
        return (key, plain);
    }

    /// <summary>The key a request presents, when it is known, switched on and not expired. The time of its last use is kept (at most once a minute).</summary>
    public async Task<ApiKey?> ValidateAsync(string? presented, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(presented) || !presented.StartsWith(Prefix, StringComparison.Ordinal) || presented.Length > 200)
        {
            return null;
        }

        var hash = Hash(presented.Trim());
        var key = await db.ApiKey.FirstOrDefaultAsync(k => k.KeyHash == hash, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        if (key is null || !key.IsEnabled || key.ExpiresDate is { } expires && expires <= now)
        {
            return null;
        }

        if (key.LastUsedDate is null || now - key.LastUsedDate > TimeSpan.FromMinutes(1))
        {
            key.LastUsedDate = now;
            await db.SaveChangesAsync(ct);
        }

        return key;
    }

    /// <summary>The key of a request: "Authorization: Bearer im_…" or "X-Api-Key: im_…".</summary>
    public static string? Presented(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authorization["Bearer ".Length..].Trim();
        }

        return request.Headers["X-Api-Key"].FirstOrDefault()?.Trim();
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
