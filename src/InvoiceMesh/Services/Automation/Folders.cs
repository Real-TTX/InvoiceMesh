using InvoiceMesh.Configuration;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Storage;

namespace InvoiceMesh.Services.Automation;

public sealed record FolderFile(string Name, long Size, DateTime ModifiedUtc);

/// <summary>
/// A folder documents are picked up from or exports are put into: on the machine (or mounted into the container), or on a share of a NAS. It knows the files
/// directly in it, nothing below.
/// </summary>
public interface IFileFolder
{
    /// <summary>Where it is, for people ("\\nas\scans\eingang", "/data/eingang").</summary>
    string Describe { get; }

    Task<IReadOnlyList<FolderFile>> ListAsync(CancellationToken ct = default);

    Task<byte[]> ReadAsync(string name, CancellationToken ct = default);

    Task WriteAsync(string name, byte[] bytes, CancellationToken ct = default);

    /// <summary>Moves a file into a folder below this one (made when missing).</summary>
    Task MoveAsync(string name, string subFolder, string newName, CancellationToken ct = default);

    Task DeleteAsync(string name, CancellationToken ct = default);

    /// <summary>Is it there, can a file be written? The message says what is wrong in words.</summary>
    Task<(bool Ok, string Message)> CheckAsync(CancellationToken ct = default);
}

public sealed class LocalFolder(string path) : IFileFolder
{
    public string Describe => path;

    public Task<IReadOnlyList<FolderFile>> ListAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(path))
        {
            return Task.FromResult<IReadOnlyList<FolderFile>>([]);
        }

        IReadOnlyList<FolderFile> files = [.. new DirectoryInfo(path).EnumerateFiles()
            .Where(f => !f.Name.StartsWith('.') && !f.Name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            .Select(f => new FolderFile(f.Name, f.Length, f.LastWriteTimeUtc))];
        return Task.FromResult(files);
    }

    public Task<byte[]> ReadAsync(string name, CancellationToken ct = default) => File.ReadAllBytesAsync(PathOf(name), ct);

    public async Task WriteAsync(string name, byte[] bytes, CancellationToken ct = default)
    {
        Directory.CreateDirectory(path);
        var target = PathOf(name);
        var partial = target + ".partial";
        await File.WriteAllBytesAsync(partial, bytes, ct);
        File.Move(partial, target, overwrite: true);
    }

    public Task MoveAsync(string name, string subFolder, string newName, CancellationToken ct = default)
    {
        var folder = Path.Combine(path, Checked(subFolder));
        Directory.CreateDirectory(folder);
        File.Move(PathOf(name), Path.Combine(folder, Checked(newName)));
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name, CancellationToken ct = default)
    {
        File.Delete(PathOf(name));
        return Task.CompletedTask;
    }

    public async Task<(bool Ok, string Message)> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".im-probe-" + Guid.NewGuid().ToString("N")[..8]);
            await File.WriteAllTextAsync(probe, "probe", ct);
            File.Delete(probe);
            var count = (await ListAsync(ct)).Count;
            return (true, $"Der Ordner „{path}“ ist da und beschreibbar. Darin liegen {Fmt.Integer(count)} Datei(en).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (false, $"Der Ordner „{path}“ kann nicht benutzt werden: {ex.Message}");
        }
    }

    private string PathOf(string name) => Path.Combine(path, Checked(name));

    /// <summary>Names come from a listing or are made by the program; a path in them would leave the folder.</summary>
    private static string Checked(string name)
        => name.Length > 0 && name == Path.GetFileName(name) && name is not ("." or "..") ? name : throw new StorageException($"„{name}“ ist kein gültiger Dateiname.");
}

public sealed class SmbFolder(SmbShare share, string description) : IFileFolder
{
    public string Describe => description;

    public async Task<IReadOnlyList<FolderFile>> ListAsync(CancellationToken ct = default)
        => [.. (await share.ListAsync(string.Empty, ct)).Where(f => !f.Name.StartsWith('.')).Select(f => new FolderFile(f.Name, f.Size, f.ModifiedUtc))];

    public async Task<byte[]> ReadAsync(string name, CancellationToken ct = default)
    {
        await using var stream = await share.OpenReadAsync(Checked(name), ct);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    public Task WriteAsync(string name, byte[] bytes, CancellationToken ct = default) => share.WriteAsync(Checked(name), new MemoryStream(bytes), ct);

    public Task MoveAsync(string name, string subFolder, string newName, CancellationToken ct = default)
        => share.MoveAsync(Checked(name), $"{Checked(subFolder)}/{Checked(newName)}", ct);

    public Task DeleteAsync(string name, CancellationToken ct = default) => share.DeleteAsync(Checked(name), ct);

    public async Task<(bool Ok, string Message)> CheckAsync(CancellationToken ct = default)
    {
        var check = await share.CheckAsync(ct);
        return (check.Ok, check.Message);
    }

    private static string Checked(string name)
        => name.Length > 0 && !name.Contains('/') && !name.Contains('\\') && name is not ("." or "..") ? name : throw new StorageException($"„{name}“ ist kein gültiger Dateiname.");
}

/// <summary>Makes the folder of an intake source or of a planned export from its settings.</summary>
public sealed class FolderFactory(ConfigStore config, SecretProtector secrets)
{
    public IFileFolder For(IntakeSource s) => s.Kind == IntakeKind.Smb
        ? Smb(s.Host, s.Share, s.Path, s.Domain, s.UserName, s.PasswordProtected)
        : new LocalFolder(Local(s.Path, "eingang"));

    public IFileFolder For(ExportPlan p) => p.Delivery == DeliveryKind.Smb
        ? Smb(p.Host, p.Share, p.Path, p.Domain, p.UserName, p.PasswordProtected)
        : new LocalFolder(Local(p.Path, "exporte"));

    /// <summary>"\\host\share\folder" or the folder, for lists.</summary>
    public static string Where(IntakeKind kind, string path, string? host, string? share) => kind switch
    {
        IntakeKind.Smb => $"\\\\{host}\\{share}\\{path}".TrimEnd('\\'),
        _ => string.IsNullOrWhiteSpace(path) ? "Volume: eingang" : path,
    };

    private IFileFolder Smb(string? host, string? share, string path, string? domain, string? user, string? password)
        => new SmbFolder(new SmbShare(new SmbOptions(host ?? "", share ?? "", path, domain, user, secrets.Unprotect(password))), $"\\\\{host}\\{share}\\{path}".TrimEnd('\\'));

    /// <summary>A relative folder is inside the data volume.</summary>
    private string Local(string path, string fallback) => Path.IsPathRooted(path) ? path : Path.Combine(config.DataDir, string.IsNullOrWhiteSpace(path) ? fallback : path);
}
