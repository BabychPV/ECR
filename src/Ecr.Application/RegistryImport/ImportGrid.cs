namespace Ecr.Application.RegistryImport;

/// <summary>
/// Raw view of one worksheet for the generic registry import engine (N4).
/// </summary>
/// <remarks>
/// The engine never touches ClosedXML: the Excel adapter wraps a worksheet into this
/// interface, tests build it in memory. Coordinates are 1-based, like Excel.
/// A cell inside a merged range other than the anchor is EMPTY here (as in the file);
/// merge-aware reading is the engine's decision, not the grid's.
/// </remarks>
public interface IImportGrid
{
    /// <summary>Last used row.</summary>
    public int RowCount { get; }

    /// <summary>Last used column.</summary>
    public int ColumnCount { get; }

    /// <summary>The cell as stored in the file.</summary>
    public ImportCell Get(int row, int column);

    /// <summary>The merged range that contains the cell; <c>null</c> when it is not merged.</summary>
    public ImportMerge? MergeAt(int row, int column);
}

/// <summary>A raw cell: <see cref="Value"/> is <c>null</c>, <c>string</c>, <c>double</c>, <c>DateTime</c> or <c>bool</c>.</summary>
/// <param name="Value">Stored (for a formula - cached) value.</param>
/// <param name="HasFormula">The cell holds a formula.</param>
public readonly record struct ImportCell(object? Value, bool HasFormula = false);

/// <summary>A merged range (1-based, inclusive).</summary>
/// <param name="FirstRow">Anchor row.</param>
/// <param name="FirstColumn">Anchor column.</param>
/// <param name="LastRow">Last row.</param>
/// <param name="LastColumn">Last column.</param>
public sealed record ImportMerge(int FirstRow, int FirstColumn, int LastRow, int LastColumn)
{
    /// <summary>Number of columns the range spans.</summary>
    public int Width => LastColumn - FirstColumn + 1;

    /// <summary>Number of rows the range spans.</summary>
    public int Height => LastRow - FirstRow + 1;
}

/// <summary>An in-memory grid (tests, synthetic fixtures, dry runs).</summary>
public sealed class InMemoryImportGrid : IImportGrid
{
    private readonly Dictionary<(int Row, int Column), ImportCell> _cells = [];
    private readonly List<ImportMerge> _merges = [];
    private readonly Dictionary<(int Row, int Column), ImportMerge> _mergeIndex = [];

    /// <inheritdoc />
    public int RowCount { get; private set; }

    /// <inheritdoc />
    public int ColumnCount { get; private set; }

    /// <summary>Sets a cell; <c>null</c> clears it.</summary>
    public InMemoryImportGrid Set(int row, int column, object? value, bool hasFormula = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(row, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(column, 1);

        if (value is int i)
        {
            value = (double)i;
        }

        if (value is null)
        {
            _cells.Remove((row, column));
        }
        else
        {
            _cells[(row, column)] = new ImportCell(value, hasFormula);
        }

        RowCount = Math.Max(RowCount, row);
        ColumnCount = Math.Max(ColumnCount, column);
        return this;
    }

    /// <summary>Merges a range; the anchor keeps its value, the others are cleared.</summary>
    public InMemoryImportGrid Merge(int firstRow, int firstColumn, int lastRow, int lastColumn)
    {
        var merge = new ImportMerge(firstRow, firstColumn, lastRow, lastColumn);
        _merges.Add(merge);

        for (var r = firstRow; r <= lastRow; r++)
        {
            for (var c = firstColumn; c <= lastColumn; c++)
            {
                _mergeIndex[(r, c)] = merge;
                if (r != firstRow || c != firstColumn)
                {
                    _cells.Remove((r, c));
                }
            }
        }

        RowCount = Math.Max(RowCount, lastRow);
        ColumnCount = Math.Max(ColumnCount, lastColumn);
        return this;
    }

    /// <inheritdoc />
    public ImportCell Get(int row, int column)
        => _cells.TryGetValue((row, column), out var cell) ? cell : default;

    /// <inheritdoc />
    public ImportMerge? MergeAt(int row, int column)
        => _mergeIndex.GetValueOrDefault((row, column));
}
