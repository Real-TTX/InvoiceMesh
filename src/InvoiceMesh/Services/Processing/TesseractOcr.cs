using System.Diagnostics;
using System.Globalization;
using System.Text;
using InvoiceMesh.Configuration;
using InvoiceMesh.Core.Extraction;
using InvoiceMesh.Core.Formats;

namespace InvoiceMesh.Services.Processing;

/// <summary>
/// Text recognition with Tesseract (installed in the image with the languages of <see cref="ProcessingConfig.OcrLanguages"/>). A scanned PDF is first turned
/// into images of 300 dpi with <c>pdftoppm</c> (Poppler). The mean confidence of the words comes from Tesseract's TSV output. Both programs run without a
/// shell, with a time limit, in a folder of their own that is removed afterwards.
/// </summary>
public sealed class TesseractOcr(ConfigStore config, ILogger<TesseractOcr> logger) : IOcrEngine
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);

    private static readonly Lazy<string?> Version = new(() => Run("tesseract", ["--version"], null, TimeSpan.FromSeconds(10)).GetAwaiter().GetResult() is { ExitCode: 0 } r
        ? (r.Output + r.Error).Split('\n').FirstOrDefault()?.Trim()
        : null);

    public bool IsAvailable => Version.Value is not null;

    public string? EngineVersion => Version.Value;

    public async Task<OcrResult> RecognizeAsync(byte[] bytes, FileKind kind, CancellationToken ct)
    {
        var folder = Path.Combine(Path.GetTempPath(), "im-ocr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var images = new List<string>();
            if (kind == FileKind.Pdf)
            {
                var pdf = Path.Combine(folder, "in.pdf");
                await File.WriteAllBytesAsync(pdf, bytes, ct);
                var render = await Run("pdftoppm", ["-r", "300", "-gray", "-png", "-l", "10", pdf, Path.Combine(folder, "page")], folder, Limit, ct);
                if (render.ExitCode != 0)
                {
                    throw new InvalidOperationException("Das PDF konnte nicht in Bilder umgewandelt werden: " + render.Error.Trim());
                }

                images.AddRange(Directory.GetFiles(folder, "page*.png").Order(StringComparer.Ordinal));
            }
            else
            {
                var image = Path.Combine(folder, "in.img");
                await File.WriteAllBytesAsync(image, bytes, ct);
                images.Add(image);
            }

            var languages = Sanitize(config.Current.Processing.OcrLanguages);
            var text = new StringBuilder();
            var confidences = new List<double>();
            foreach (var image in images)
            {
                // --psm 6 would force one block; the default (3) finds the columns of an invoice better. TSV gives words with confidences.
                var tsv = await Run("tesseract", [image, "stdout", "-l", languages, "--psm", "3", "tsv"], folder, Limit, ct);
                if (tsv.ExitCode != 0)
                {
                    throw new InvalidOperationException("Tesseract ist fehlgeschlagen: " + tsv.Error.Trim());
                }

                var (pageText, pageConfidences) = ReadTsv(tsv.Output);
                text.AppendLine(pageText);
                confidences.AddRange(pageConfidences);
            }

            var mean = confidences.Count == 0 ? (double?)null : Math.Round(confidences.Average(), 1);
            return new OcrResult(text.ToString(), mean, $"{Version.Value ?? "Tesseract"} ({languages})");
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "The OCR folder {Folder} could not be removed.", folder);
            }
        }
    }

    private sealed record OcrWord(int Left, int Top, int Width, int Height, string Text);

    /// <summary>
    /// Rebuilds the lines from Tesseract's TSV by the position of the words on the page, not by its blocks: Tesseract reads the column of the amounts as a
    /// block of its own, which would separate "Total" from its amount. Words whose vertical middles are close form a line, left to right; a wide gap becomes
    /// three spaces, as in the text of a PDF. The confidences of the words are collected.
    /// </summary>
    internal static (string Text, List<double> Confidences) ReadTsv(string tsv)
    {
        var words = new List<OcrWord>();
        var confidences = new List<double>();
        foreach (var row in tsv.Split('\n').Skip(1))
        {
            var c = row.TrimEnd('\r').Split('\t');
            if (c.Length < 12 || c[0] != "5" || string.IsNullOrWhiteSpace(c[11]))
            {
                continue;
            }

            int I(int i) => int.Parse(c[i], CultureInfo.InvariantCulture);
            words.Add(new OcrWord(I(6), I(7), I(8), I(9), c[11]));
            if (double.TryParse(c[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var conf) && conf >= 0)
            {
                confidences.Add(conf);
            }
        }

        var height = words.Count == 0 ? 10 : words.Select(w => w.Height).Order().ElementAt(words.Count / 2);
        var rows = new List<List<OcrWord>>();
        foreach (var w in words.OrderBy(w => w.Top + w.Height / 2.0))
        {
            var middle = w.Top + w.Height / 2.0;
            var row = rows.LastOrDefault();
            if (row is not null && Math.Abs(row.Average(x => x.Top + x.Height / 2.0) - middle) <= height * 0.6)
            {
                row.Add(w);
            }
            else
            {
                rows.Add([w]);
            }
        }

        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            OcrWord? previous = null;
            foreach (var w in row.OrderBy(x => x.Left))
            {
                if (previous is not null)
                {
                    sb.Append(w.Left - (previous.Left + previous.Width) > height * 1.5 ? "   " : " ");
                }

                sb.Append(w.Text);
                previous = w;
            }

            sb.AppendLine();
        }

        return (sb.ToString(), confidences);
    }

    private static string Sanitize(string languages)
    {
        var parts = languages.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(p => p.All(ch => char.IsAsciiLetterLower(ch) || ch == '_')).ToList();
        return parts.Count == 0 ? "eng" : string.Join('+', parts);
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);

    private static async Task<ProcessResult> Run(string file, IEnumerable<string> arguments, string? workingDirectory, TimeSpan limit, CancellationToken ct = default)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
        };
        foreach (var a in arguments)
        {
            start.ArgumentList.Add(a);
        }

        start.Environment["OMP_THREAD_LIMIT"] = "2";
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException($"{file} could not be started.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new ProcessResult(-1, "", $"{file} ist nicht installiert.");
        }

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(limit);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            return new ProcessResult(process.ExitCode, await output, await error);
        }
    }
}
