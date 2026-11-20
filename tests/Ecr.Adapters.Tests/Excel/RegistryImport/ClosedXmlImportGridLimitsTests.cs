using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using Ecr.Adapters.Excel.RegistryImport;
using Ecr.Application.Errors;
using Xunit;

namespace Ecr.Adapters.Tests.Excel.RegistryImport;

/// <summary>
/// PSS-22: a few-KB <c>.xlsx</c> with a full-sheet merge or a cell in the far corner used to be expanded cell by cell
/// (1.7e10 dictionary inserts). The grid now refuses it at construction with <c>ECR-IMP-0422</c>, fast.
/// The crafted packages are built here (sheet XML patched), no binaries are committed.
/// </summary>
public sealed class ClosedXmlImportGridLimitsTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    /// <summary>Mutation: remove the per-range / total merge check → the full-sheet merge is expanded (hang/OOM) → red.</summary>
    [Fact]
    public void A_full_sheet_merge_is_rejected_fast()
    {
        var bytes = Craft("<mergeCells count=\"1\"><mergeCell ref=\"A1:XFD1048576\"/></mergeCells>", cornerCell: false);

        AssertRejectedFast(bytes);
    }

    /// <summary>Mutation: remove the per-range check (only the total stays) → one 20 000-cell merge is indexed → red.</summary>
    [Fact]
    public void One_merge_above_the_per_range_ceiling_is_rejected()
    {
        AssertRejectedFast(Craft("<mergeCells count=\"1\"><mergeCell ref=\"A1:CV200\"/></mergeCells>", cornerCell: false));
    }

    /// <summary>Mutation: remove the total check → 200 merges of 9 999 cells each (≈2 M) are all indexed → red.</summary>
    [Fact]
    public void Many_merges_above_the_total_ceiling_are_rejected_fast()
    {
        var sb = new StringBuilder("<mergeCells count=\"150\">");
        // 150 disjoint 100 x 100 = 10 000-cell blocks stay under the per-range limit but total 1.5 M.
        for (var i = 0; i < 150; i++)
        {
            var firstRow = (i * 100) + 1;
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"<mergeCell ref=\"A{firstRow}:CV{firstRow + 99}\"/>");
        }

        sb.Append("</mergeCells>");

        AssertRejectedFast(Craft(sb.ToString(), cornerCell: false));
    }

    /// <summary>Mutation: remove the row/column check → used range 1 048 576 rows is accepted → red.</summary>
    [Fact]
    public void A_cell_in_the_far_corner_is_rejected_fast()
    {
        AssertRejectedFast(Craft(mergeXml: null, cornerCell: true));
    }

    /// <summary>End to end through the NAE tree reader (gate → open → grid): both crafted shapes are refused with the existing code.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_nae_tree_reader_refuses_the_crafted_workbooks_fast(bool fullSheetMerge)
    {
        var bytes = fullSheetMerge
            ? Craft("<mergeCells count=\"1\"><mergeCell ref=\"A1:XFD1048576\"/></mergeCells>", cornerCell: false)
            : Craft(mergeXml: null, cornerCell: true);

        // Warm-up: the first ClosedXML open in a process pays ~2 s of JIT, which is not the cost of the guard.
        using (var warm = new MemoryStream(Craft(mergeXml: null, cornerCell: false)))
        {
            _ = await new NaeTreeWorkbook().ReadAsync(warm, CancellationToken.None);
        }

        var watch = Stopwatch.StartNew();
        using var stream = new MemoryStream(bytes);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => new NaeTreeWorkbook().ReadAsync(stream, CancellationToken.None));

        watch.Stop();
        Assert.Equal("ECR-IMP-0422", ex.ErrorCode);
        Assert.True(watch.Elapsed < Budget, $"rejected in {watch.Elapsed.TotalMilliseconds:F0} ms");
    }

    /// <summary>Control: an ordinary sheet with a header merge still opens (the limits are not a blanket refusal).</summary>
    [Fact]
    public void An_ordinary_sheet_with_a_group_merge_is_read()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("S");
        ws.Cell(1, 1).Value = "Group";
        ws.Range(1, 1, 1, 8).Merge();
        ws.Cell(2, 1).Value = 5;
        ws.Cell(3, 3).Value = "x";

        var grid = new ClosedXmlImportGrid(ws);

        Assert.Equal(3, grid.RowCount);
        Assert.Equal(3, grid.ColumnCount);
        Assert.Equal(8, grid.MergeAt(1, 5)!.LastColumn);
    }

    private static void AssertRejectedFast(byte[] package)
    {
        var watch = Stopwatch.StartNew();
        using var stream = new MemoryStream(package);
        var ex = Assert.Throws<BusinessRuleException>(() =>
        {
            using var workbook = new XLWorkbook(stream);
            _ = new ClosedXmlImportGrid(workbook.Worksheets.First());
        });
        watch.Stop();

        Assert.Equal("ECR-IMP-0422", ex.ErrorCode);
        Assert.True(watch.Elapsed < Budget, $"rejected in {watch.Elapsed.TotalMilliseconds:F0} ms");
        Assert.True(package.Length < 20_000, "the crafted package stays tiny");
    }

    private static byte[] Craft(string? mergeXml, bool cornerCell)
    {
        byte[] plain;
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Sheet1");
            ws.Cell(1, 1).Value = "a";
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            plain = ms.ToArray();
        }

        using var output = new MemoryStream();
        output.Write(plain);
        using (var zip = new ZipArchive(output, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.GetEntry("xl/worksheets/sheet1.xml")!;
            string xml;
            using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
            {
                xml = reader.ReadToEnd();
            }

            // ClosedXML writes a prefixed namespace (<x:...>): the injected elements must carry the same prefix.
            var p = xml.Contains("<x:sheetData", StringComparison.Ordinal) ? "x:" : string.Empty;
            var closing = $"</{p}sheetData>";
            Assert.Contains(closing, xml, StringComparison.Ordinal);

            if (cornerCell)
            {
                xml = xml.Replace(
                    closing,
                    $"<{p}row r=\"1048576\"><{p}c r=\"XFD1048576\" t=\"inlineStr\"><{p}is><{p}t>z</{p}t></{p}is></{p}c></{p}row>{closing}",
                    StringComparison.Ordinal);
            }

            if (mergeXml is not null)
            {
                var prefixed = p.Length == 0 ? mergeXml : mergeXml.Replace("<mergeCell", $"<{p}mergeCell", StringComparison.Ordinal).Replace("</mergeCells", $"</{p}mergeCells", StringComparison.Ordinal);
                xml = xml.Replace(closing, closing + prefixed, StringComparison.Ordinal);
            }

            entry.Delete();
            var replaced = zip.CreateEntry("xl/worksheets/sheet1.xml");
            using var writer = new StreamWriter(replaced.Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }

        return output.ToArray();
    }
}
