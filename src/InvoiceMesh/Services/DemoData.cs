using System.Text;
using InvoiceMesh.Data;
using InvoiceMesh.Data.Entities;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace InvoiceMesh.Services;

/// <summary>
/// Sample documents for trying InvoiceMesh out, into an empty installation only: an XRechnung in UBL, a ZUGFeRD / Factur-X PDF with its XML inside, a
/// Peppol credit note, a PDF with text only, a scan (an image, read by text recognition), an XRechnung whose sums do not add up, and a file that is no
/// invoice. They go through the same processing as an upload.
/// </summary>
public sealed class DemoData(ImDbContext db, InvoiceService invoices)
{
    public async Task<bool> IsEmptyAsync(CancellationToken ct = default) => !await db.Invoice.IgnoreQueryFilters().AnyAsync(ct);

    public async Task<bool> CreateAsync(CancellationToken ct = default)
    {
        if (!await IsEmptyAsync(ct))
        {
            return false;
        }

        var ubl = Resource("xrechnung-ubl.xml");
        var cii = Resource("facturx-en16931.xml");
        var credit = Resource("peppol-creditnote.xml");
        var paperDe = Encoding.UTF8.GetString(Resource("paper-de.txt"));
        var paperEn = Encoding.UTF8.GetString(Resource("paper-en.txt"));

        await invoices.AddAsync(ubl, "RE-2026-0815.xml", InvoiceSource.Demo, ct: ct);
        await invoices.AddAsync(Hybrid(cii, "Torréfaction Lumière SARL", "Facture FA-2026-117"), "facture_FA-2026-117.pdf", InvoiceSource.Demo, ct: ct);
        await invoices.AddAsync(credit, "CN-9001.xml", InvoiceSource.Demo, ct: ct);
        await invoices.AddAsync(TextPdf(paperDe), "Rechnung_2026-10477.pdf", InvoiceSource.Demo, ct: ct);
        await invoices.AddAsync(Scan(paperEn), "scan_brightside.png", InvoiceSource.Demo, ct: ct);

        // An XRechnung whose total is wrong: the validation must find it.
        var wrong = Encoding.UTF8.GetString(ubl).Replace("RE-2026-0815", "RE-2026-0816", StringComparison.Ordinal)
            .Replace("<cbc:TaxInclusiveAmount currencyID=\"EUR\">1243.50</cbc:TaxInclusiveAmount>", "<cbc:TaxInclusiveAmount currencyID=\"EUR\">1234.50</cbc:TaxInclusiveAmount>", StringComparison.Ordinal);
        await invoices.AddAsync(Encoding.UTF8.GetBytes(wrong), "RE-2026-0816.xml", InvoiceSource.Demo, ct: ct);

        // Not an invoice at all: the processing must say so in words.
        await invoices.AddAsync([0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00, 0x08, 0x00], "Notizen.docx", InvoiceSource.Demo, ct: ct);
        return true;
    }

    private static byte[] Resource(string name)
    {
        using var stream = typeof(DemoData).Assembly.GetManifestResourceStream("Demo." + name) ?? throw new InvalidOperationException($"The sample {name} is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>A page of plain text, as a PDF with a text layer (like an invoice printed from a word processor).</summary>
    public static byte[] TextPdf(string text) => TextDocument(text).GeneratePdf();

    /// <summary>The same page as an image of 200 dpi: what a scanner gives.</summary>
    public static byte[] Scan(string text) => TextDocument(text).GenerateImages(new ImageGenerationSettings { RasterDpi = 200, ImageFormat = ImageFormat.Png }).First();

    private static Document TextDocument(string text) => Document.Create(doc => doc.Page(page =>
    {
        page.Size(PageSizes.A4);
        page.Margin(16, Unit.Millimetre);
        page.PageColor(Colors.White);
        page.DefaultTextStyle(x => x.FontSize(9.5f).FontFamily("DejaVu Sans Mono", Fonts.Courier).FontColor(Colors.Black));
        page.Content().Column(col =>
        {
            foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                col.Item().Text(line.Length == 0 ? " " : line);
            }
        });
    }));

    /// <summary>A hybrid invoice: a readable PDF with the CII inside as <c>factur-x.xml</c>, the way ZUGFeRD and Factur-X carry it.</summary>
    public static byte[] Hybrid(byte[] xml, string seller, string title)
    {
        var pdf = Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(20, Unit.Millimetre);
            page.DefaultTextStyle(x => x.FontSize(10));
            page.Content().Column(col =>
            {
                col.Item().Text(seller).FontSize(16).SemiBold();
                col.Item().PaddingTop(10).Text(title).FontSize(13);
                col.Item().PaddingTop(10).Text("Les données structurées de cette facture (Factur-X, profil EN 16931) sont jointes à ce PDF.");
            });
        })).GeneratePdf();

        var folder = Path.Combine(Path.GetTempPath(), "im-demo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var source = Path.Combine(folder, "in.pdf");
            var xmlPath = Path.Combine(folder, "factur-x.xml");
            var target = Path.Combine(folder, "out.pdf");
            File.WriteAllBytes(source, pdf);
            File.WriteAllBytes(xmlPath, xml);
            DocumentOperation.LoadFile(source)
                .AddAttachment(new DocumentOperation.DocumentAttachment
                {
                    Key = "factur-x.xml",
                    FilePath = xmlPath,
                    AttachmentName = "factur-x.xml",
                    MimeType = "text/xml",
                    Description = "Factur-X Invoice",
                    Relationship = DocumentOperation.DocumentAttachmentRelationship.Alternative,
                })
                .Save(target);
            return File.ReadAllBytes(target);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // a temp folder that stays is no harm
            }
        }
    }
}
