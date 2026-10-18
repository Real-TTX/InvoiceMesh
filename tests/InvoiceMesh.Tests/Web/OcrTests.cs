using InvoiceMesh.Services.Processing;

namespace InvoiceMesh.Tests.Web;

public class OcrTests
{
    private static string Word(int block, int left, int top, string text, double conf = 95)
        => $"5\t1\t{block}\t1\t1\t1\t{left}\t{top}\t40\t20\t{conf.ToString(System.Globalization.CultureInfo.InvariantCulture)}\t{text}";

    [Fact]
    public void A_label_and_its_amount_in_another_block_end_up_on_one_line()
    {
        // Tesseract reads the column of the amounts as a block of its own (block 2): the line is rebuilt by the position on the page.
        var tsv = string.Join('\n',
            "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext",
            Word(1, 10, 100, "Subtotal"),
            Word(1, 10, 140, "Total"),
            Word(1, 60, 141, "Due"),
            Word(2, 600, 102, "2,000.00"),
            Word(2, 600, 139, "2,000.00", 60),
            "4\t1\t2\t1\t1\t0\t0\t0\t0\t0\t-1\t");

        var (text, confidences) = TesseractOcr.ReadTsv(tsv);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal(["Subtotal   2,000.00", "Total Due   2,000.00"], lines);
        Assert.Equal(5, confidences.Count);
    }
}
