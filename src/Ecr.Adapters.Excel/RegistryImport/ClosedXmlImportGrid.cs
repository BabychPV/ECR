using ClosedXML.Excel;
using Ecr.Application.RegistryImport;

namespace Ecr.Adapters.Excel.RegistryImport;

/// <summary>A ClosedXML worksheet as an <see cref="IImportGrid"/>.</summary>
public sealed class ClosedXmlImportGrid : IImportGrid
{
    private readonly IXLWorksheet _sheet;
    private readonly Dictionary<(int Row, int Column), ImportMerge> _merges = [];

    /// <summary>Wraps a worksheet.</summary>
    public ClosedXmlImportGrid(IXLWorksheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        _sheet = sheet;
        RowCount = sheet.LastRowUsed()?.RowNumber() ?? 0;
        ColumnCount = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;

        foreach (var range in sheet.MergedRanges)
        {
            var merge = new ImportMerge(
                range.RangeAddress.FirstAddress.RowNumber,
                range.RangeAddress.FirstAddress.ColumnNumber,
                range.RangeAddress.LastAddress.RowNumber,
                range.RangeAddress.LastAddress.ColumnNumber);

            // Merged areas can reach past the used range (a header merged over empty columns): index only what we can ask for.
            RowCount = Math.Max(RowCount, merge.FirstRow);
            ColumnCount = Math.Max(ColumnCount, merge.FirstColumn);

            for (var r = merge.FirstRow; r <= merge.LastRow; r++)
            {
                for (var c = merge.FirstColumn; c <= merge.LastColumn; c++)
                {
                    _merges[(r, c)] = merge;
                }
            }
        }
    }

    /// <inheritdoc />
    public int RowCount { get; }

    /// <inheritdoc />
    public int ColumnCount { get; }

    /// <inheritdoc />
    public ImportCell Get(int row, int column)
    {
        var cell = _sheet.Cell(row, column);
        var value = cell.Value;

        object? raw = value.Type switch
        {
            XLDataType.Blank => null,
            XLDataType.Boolean => value.GetBoolean(),
            XLDataType.Number => value.GetNumber(),
            XLDataType.Text => value.GetText(),
            XLDataType.DateTime => value.GetDateTime(),
            XLDataType.TimeSpan => value.GetTimeSpan().ToString("c", System.Globalization.CultureInfo.InvariantCulture),
            XLDataType.Error => value.GetError() == XLError.NoValueAvailable ? "#N/A" : "#ERR",
            _ => null,
        };

        return new ImportCell(raw, cell.HasFormula);
    }

    /// <inheritdoc />
    public ImportMerge? MergeAt(int row, int column) => _merges.GetValueOrDefault((row, column));
}
