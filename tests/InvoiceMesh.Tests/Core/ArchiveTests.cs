using System.IO.Compression;
using System.Text;
using InvoiceMesh.Core.Extraction;

namespace InvoiceMesh.Tests.Core;

public class ArchiveTests
{
    public static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(bytes);
            }
        }

        return buffer.ToArray();
    }

    [Fact]
    public void Only_a_file_called_zip_that_is_a_zip_is_an_archive()
    {
        var zip = Zip(("a.xml", "<a/>"u8.ToArray()));

        Assert.True(Archives.IsZip("rechnungen.ZIP", zip));
        Assert.False(Archives.IsZip("angebot.docx", zip));
        Assert.False(Archives.IsZip("kaputt.zip", "PDF"u8.ToArray()));
    }

    [Fact]
    public void Files_folders_and_a_zip_inside_are_unpacked_and_what_systems_leave_behind_is_not()
    {
        var inner = Zip(("c.xml", "<c/>"u8.ToArray()));
        var zip = Zip(
            ("a.xml", "<a/>"u8.ToArray()),
            ("Oktober/b.pdf", "%PDF-1.7"u8.ToArray()),
            ("__MACOSX/Oktober/._b.pdf", [1, 2, 3]),
            (".DS_Store", [0]),
            ("Oktober/Thumbs.db", [0]),
            ("Oktober/", []),
            ("mehr.zip", inner));

        var content = Archives.Expand(zip, 1024 * 1024, 10 * 1024 * 1024);

        Assert.Equal(["a.xml", "Oktober/b.pdf", "mehr.zip/c.xml"], content.Files.Select(f => f.Name));
        Assert.Equal("<c/>", Encoding.UTF8.GetString(content.Files[2].Bytes));
        Assert.Empty(content.Skipped);
    }

    [Fact]
    public void A_file_beyond_the_limit_is_left_out_whatever_the_archive_claims()
    {
        var zip = Zip(("klein.xml", "<a/>"u8.ToArray()), ("gross.pdf", new byte[5000]));

        var content = Archives.Expand(zip, maxFileBytes: 1000, maxTotalBytes: 1_000_000);

        Assert.Equal(["klein.xml"], content.Files.Select(f => f.Name));
        Assert.Contains(content.Skipped, s => s.Contains("gross.pdf", StringComparison.Ordinal));
    }

    [Fact]
    public void The_total_is_limited_as_well()
    {
        var zip = Zip(("1.pdf", new byte[600]), ("2.pdf", new byte[600]), ("3.pdf", new byte[600]));

        var content = Archives.Expand(zip, maxFileBytes: 1000, maxTotalBytes: 1300);

        Assert.Equal(2, content.Files.Count);
        Assert.Contains(content.Skipped, s => s.Contains("größer", StringComparison.Ordinal));
    }

    [Fact]
    public void A_broken_zip_is_reported()
    {
        var content = Archives.Expand([0x50, 0x4B, 0x03, 0x04, 1, 2, 3], 1000, 1000);

        Assert.Empty(content.Files);
        Assert.Single(content.Skipped);
    }
}
