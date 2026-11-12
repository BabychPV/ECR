using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Ecr.Adapters.Excel.RegistryImport;
using Xunit;

namespace Ecr.Adapters.Tests.Excel.RegistryImport;

/// <summary>
/// B23: the registry-import grid reads a formula cell by its SAVED result and never evaluates the formula. Evaluating (the old
/// <c>IXLCell.Value</c>) made one analyse of our own 2.3 MB document export (51,440 formulas without a saved result) take ~4 min.
/// </summary>
public sealed class ClosedXmlImportGridTests
{
    /// <summary>
    /// B1 = <c>A1*2</c> with a saved result 7 that DIFFERS from the evaluation (10); B2 = the same formula with no saved result.
    /// Mutation: <c>cell.Value</c> instead of <c>CachedValue</c> → B1 reads 10 and B2 reads 10 (evaluated) → red.
    /// </summary>
    [Fact]
    public void A_formula_cell_reads_its_saved_result_and_is_never_evaluated()
    {
        var bytes = WithFormulas(saved: "7");
        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);
        var grid = new ClosedXmlImportGrid(workbook.Worksheets.First());

        var withResult = grid.Get(1, 2);
        var withoutResult = grid.Get(2, 2);

        Assert.True(withResult.HasFormula);
        Assert.Equal(7d, withResult.Value);
        Assert.True(withoutResult.HasFormula);
        Assert.Null(withoutResult.Value);
        Assert.Equal(5d, grid.Get(1, 1).Value);
    }

    /// <summary>A workbook whose formula cells carry exactly the saved results given (ClosedXML would not write a chosen one).</summary>
    private static byte[] WithFormulas(string saved)
    {
        byte[] plain;
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Sheet1");
            ws.Cell(1, 1).Value = 5;
            ws.Cell(2, 1).Value = 5;
            ws.Cell(1, 2).Value = "placeholder-b1";
            ws.Cell(2, 2).Value = "placeholder-b2";
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

            var prefix = CellOf("B1").Match(xml).Groups["p"].Value;
            xml = CellOf("B1").Replace(xml, $"<{prefix}c r=\"B1\"><{prefix}f>A1*2</{prefix}f><{prefix}v>{saved}</{prefix}v></{prefix}c>", 1);
            xml = CellOf("B2").Replace(xml, $"<{prefix}c r=\"B2\"><{prefix}f>A2*2</{prefix}f></{prefix}c>", 1);
            Assert.Contains("<" + prefix + "v>" + saved, xml, StringComparison.Ordinal);

            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("xl/worksheets/sheet1.xml").Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }

        return output.ToArray();
    }

    private static Regex CellOf(string address)
        => new($"<(?<p>\\w+:|)c r=\"{address}\"[^>]*>.*?</\\k<p>c>", RegexOptions.Singleline, TimeSpan.FromSeconds(5));
}
