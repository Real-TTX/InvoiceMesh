using System.IO.Compression;

namespace InvoiceMesh.Core.Extraction;

/// <summary>One file taken out of an archive: its name (with the folders inside the archive) and its bytes.</summary>
public sealed record ArchiveEntry(string Name, byte[] Bytes);

/// <summary>What an archive held: the files, and what was left out and why.</summary>
public sealed record ArchiveContent(IReadOnlyList<ArchiveEntry> Files, IReadOnlyList<string> Skipped);

/// <summary>
/// ZIP files that hold many documents. A file is only treated as an archive when it is called *.zip and is a ZIP: an Office document is a ZIP as well and
/// stays a document. Limits keep a "zip bomb" out: the number of files, the size of each file and the total are checked while unpacking, not trusted
/// from the archive's own directory. A ZIP inside the ZIP is unpacked as well (one level).
/// </summary>
public static class Archives
{
    public const int MaxEntries = 500;

    public static bool IsZip(string fileName, ReadOnlySpan<byte> bytes)
        => fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && (bytes[2] is 0x03 or 0x05) && (bytes[3] is 0x04 or 0x06);

    /// <summary>Unpacks the files of a ZIP. <paramref name="maxFileBytes"/> is the limit of one file, <paramref name="maxTotalBytes"/> of all together.</summary>
    public static ArchiveContent Expand(byte[] zip, long maxFileBytes, long maxTotalBytes, int depth = 0)
    {
        var files = new List<ArchiveEntry>();
        var skipped = new List<string>();
        long total = 0;

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            return new ArchiveContent([], [$"Das ZIP lässt sich nicht öffnen: {ex.Message}"]);
        }

        using (archive)
        {
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith('/') || IsNoise(name))
                {
                    continue;
                }

                if (files.Count >= MaxEntries)
                {
                    skipped.Add($"Das ZIP enthält mehr als {MaxEntries} Dateien; der Rest wurde nicht ausgepackt.");
                    break;
                }

                var bytes = Read(entry, maxFileBytes);
                if (bytes is null)
                {
                    skipped.Add($"„{name}“ ist größer als {maxFileBytes / 1024 / 1024} MB.");
                    continue;
                }

                total += bytes.LongLength;
                if (total > maxTotalBytes)
                {
                    skipped.Add($"Das ZIP ist ausgepackt größer als {maxTotalBytes / 1024 / 1024} MB; der Rest wurde nicht ausgepackt.");
                    break;
                }

                if (depth == 0 && IsZip(name, bytes))
                {
                    var inner = Expand(bytes, maxFileBytes, maxTotalBytes - total + bytes.LongLength, depth + 1);
                    files.AddRange(inner.Files.Select(f => f with { Name = $"{name}/{f.Name}" }));
                    total += inner.Files.Sum(f => f.Bytes.LongLength) - bytes.LongLength;
                    skipped.AddRange(inner.Skipped);
                    continue;
                }

                if (IsZip(name, bytes))
                {
                    skipped.Add($"„{name}“ ist ein ZIP in einem ZIP in einem ZIP; das wird nicht ausgepackt.");
                    continue;
                }

                files.Add(new ArchiveEntry(name, bytes));
            }
        }

        return new ArchiveContent(files, skipped);
    }

    /// <summary>What an operating system leaves in archives: the resource forks of macOS, thumbnails and hidden files.</summary>
    private static bool IsNoise(string name)
    {
        var file = name[(name.LastIndexOf('/') + 1)..];
        return name.StartsWith("__MACOSX/", StringComparison.Ordinal) || file.StartsWith('.') || file.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) || file.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads at most the limit; null when there is more (whatever the archive claims the size to be).</summary>
    private static byte[]? Read(ZipArchiveEntry entry, long max)
    {
        using var source = entry.Open();
        using var target = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (target.Length + read > max)
            {
                return null;
            }

            target.Write(buffer, 0, read);
        }

        return target.ToArray();
    }
}
