using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace InvoiceMesh.Core.Extraction;

/// <summary>What a PDF holds for InvoiceMesh: its attachments (the XML of a hybrid invoice) and its text in lines, in reading order.</summary>
public sealed record PdfContent(int PageCount, IReadOnlyList<(string Name, byte[] Bytes)> Attachments, string Text, int Letters)
{
    /// <summary>
    /// Whether the pages carry text. A scan has (almost) none; a page with a stamp or a footer printed onto a scan may have a few letters,
    /// so the line is drawn at an average of 40 letters per page.
    /// </summary>
    public bool HasTextLayer => Letters >= 40 * Math.Max(1, PageCount);

    /// <summary>The file names of hybrid invoices, in the order they are looked for.</summary>
    public static readonly string[] InvoiceAttachmentNames = ["factur-x.xml", "xrechnung.xml", "zugferd-invoice.xml", "ZUGFeRD-invoice.xml"];

    public static PdfContent Read(byte[] bytes, int maxPages = 20)
    {
        using var pdf = PdfDocument.Open(bytes);
        var attachments = new List<(string, byte[])>();
        if (pdf.Advanced.TryGetEmbeddedFiles(out var files))
        {
            attachments.AddRange(files.Select(f => (f.Name, f.Bytes.ToArray())));
        }

        var sb = new StringBuilder();
        var letters = 0;
        foreach (var page in pdf.GetPages().Take(maxPages))
        {
            var words = page.GetWords().ToList();
            letters += words.Sum(w => w.Text.Count(char.IsLetterOrDigit));
            foreach (var line in Lines(words))
            {
                sb.AppendLine(line);
            }

            sb.AppendLine();
        }

        return new PdfContent(pdf.NumberOfPages, attachments, sb.ToString(), letters);
    }

    /// <summary>
    /// Groups the words of a page into lines by their baseline, left to right. A wide gap between two words becomes three spaces, so that the columns of a
    /// table stay apart for the reader of the positions.
    /// </summary>
    private static IEnumerable<string> Lines(List<Word> words)
    {
        if (words.Count == 0)
        {
            yield break;
        }

        var height = words.Select(w => w.BoundingBox.Height).Where(h => h > 0).DefaultIfEmpty(10).Average();
        var tolerance = Math.Max(2, height * 0.45);
        var rows = new List<List<Word>>();
        foreach (var word in words.OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
        {
            var row = rows.FirstOrDefault(r => Math.Abs(r[0].BoundingBox.Bottom - word.BoundingBox.Bottom) <= tolerance);
            if (row is null)
            {
                rows.Add([word]);
            }
            else
            {
                row.Add(word);
            }
        }

        foreach (var row in rows.OrderByDescending(r => r[0].BoundingBox.Bottom))
        {
            var sb = new StringBuilder();
            Word? previous = null;
            foreach (var word in row.OrderBy(w => w.BoundingBox.Left))
            {
                if (previous is not null)
                {
                    var gap = word.BoundingBox.Left - previous.BoundingBox.Right;
                    sb.Append(gap > height * 1.5 ? "   " : " ");
                }

                sb.Append(word.Text);
                previous = word;
            }

            yield return sb.ToString();
        }
    }
}
