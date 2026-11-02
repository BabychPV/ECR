using System.Globalization;
using System.Security.Cryptography;
using ClosedXML.Excel;
using Ecr.Application.RegistryImport;
using Ecr.Application.Registries.Nae;

namespace Ecr.Adapters.Excel.RegistryImport;

/// <summary>
/// Excel side of the NAE tree import: the first visible sheet as text, behind <see cref="XlsxSafetyGate"/> (the same gate as
/// the document and registry imports), and the empty template.
/// </summary>
public sealed class NaeTreeWorkbook : INaeTreeWorkbook
{
    /// <summary>Most rows read (a tree of this size is far beyond the real one; a larger sheet is refused, not cut).</summary>
    public const int MaxRows = 20_000;

    /// <inheritdoc />
    public async Task<NaeTreeSheet> ReadAsync(Stream file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);

        var seekable = await XlsxSafetyGate.SeekableAsync(file, ct).ConfigureAwait(false)
                       ?? throw XlsxSafetyGate.Rejection("package is larger than the ceiling");
        var buffered = ReferenceEquals(seekable, file) ? null : seekable;

        try
        {
            var start = seekable.Position;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(seekable, ct).ConfigureAwait(false));
            seekable.Position = start;

            using var workbook = ExcelImporter.Open(seekable);
            var sheet = workbook.Worksheets.FirstOrDefault(w => w.Visibility == XLWorksheetVisibility.Visible)
                        ?? workbook.Worksheets.First();
            var grid = new ClosedXmlImportGrid(sheet);
            if (grid.RowCount > MaxRows)
            {
                throw XlsxSafetyGate.Rejection($"more than {MaxRows} rows");
            }

            var width = Math.Min(grid.ColumnCount, 64);
            var rows = new List<IReadOnlyList<string?>>(grid.RowCount);
            for (var r = 1; r <= grid.RowCount; r++)
            {
                var row = new string?[width];
                for (var c = 1; c <= width; c++)
                {
                    row[c - 1] = Text(grid.Get(r, c).Value);
                }

                rows.Add(row);
            }

            return new NaeTreeSheet(hash, sheet.Name, rows);
        }
        finally
        {
            if (buffered is not null)
            {
                await buffered.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public byte[] BuildTemplate()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(NaeTreeImportLayout.SheetName);
        for (var i = 0; i < NaeTreeImportLayout.Columns.Count; i++)
        {
            var cell = sheet.Cell(1, i + 1);
            cell.Value = NaeTreeImportLayout.Columns[i];
            cell.Style.Font.Bold = true;
        }

        // NAE codes are text ("0036", "0036_pod"): a number format would drop the leading zeros.
        sheet.Column(NaeTreeImportLayout.Columns.ToList().IndexOf(NaeTreeImportLayout.NaeCode) + 1).Style.NumberFormat.Format = "@";
        sheet.SheetView.FreezeRows(1);
        sheet.Columns(1, NaeTreeImportLayout.Columns.Count).Width = 18;

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static string? Text(object? raw)
    {
        var text = raw switch
        {
            null => null,
            string s => s,
            double d => d.ToString("0.##########", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            bool b => b ? "TRUE" : "FALSE",
            _ => Convert.ToString(raw, CultureInfo.InvariantCulture),
        };

        if (text is null)
        {
            return null;
        }

        text = string.Join(' ', text.Replace(' ', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return text.Length == 0 ? null : text;
    }
}
