using Ecr.Application.Errors;
using Ecr.Application.RegistryImport;
using Xunit;

namespace Ecr.Application.Tests.RegistryImport;

/// <summary>PSS-22: the engine refuses an oversized grid before any row loop, and its row loops honour cancellation.</summary>
public sealed class RegistryImportParserLimitsTests
{
    private static readonly RegistryImportSpec Era = BuiltInImportProfiles.EraTable33;

    /// <summary>Mutation: remove <c>EnsureWithinLimits</c> → the parse walks 1 048 576 rows (and calls Get) → red.</summary>
    [Theory]
    [InlineData(1_048_576, 10)]
    [InlineData(10, 16_384)]
    [InlineData(100_000, 1_024)]
    public void A_grid_above_the_limits_is_refused_before_any_cell_is_read(int rows, int columns)
    {
        var grid = new HugeGrid(rows, columns);

        var ex = Assert.Throws<BusinessRuleException>(() => RegistryImportParser.Parse(grid, Era));

        Assert.Equal("ECR-IMP-0422", ex.ErrorCode);
        Assert.Equal(0, grid.Reads);
    }

    /// <summary>Mutation: drop the per-row <c>ThrowIfCancellationRequested</c> → the loops run to the end → red.</summary>
    [Fact]
    public void A_cancelled_token_stops_the_row_loops()
    {
        var era = new EraGrid().Group("Group one");
        for (var i = 0; i < 300; i++)
        {
            era.Source(1000d + i, "Source " + i, "T", [EraGrid.Sub("0333", "H2S", 0.0024d, null, 0.07d)]);
        }

        using var cts = new CancellationTokenSource();
        var grid = new CancelAfterReads(era.Grid, cts, 4_000);

        Assert.ThrowsAny<OperationCanceledException>(() => RegistryImportParser.Parse(grid, Era, cts.Token));

        // Without cancellation the same grid parses to the end (control) and reads far more cells.
        var control = new CancelAfterReads(era.Grid, null, 0);
        RegistryImportParser.Parse(control, Era);
        Assert.True(grid.Reads < control.Reads, $"{grid.Reads} reads vs {control.Reads}");
    }

    private sealed class CancelAfterReads(IImportGrid inner, CancellationTokenSource? cts, int after) : IImportGrid
    {
        public int Reads { get; private set; }

        public int RowCount => inner.RowCount;

        public int ColumnCount => inner.ColumnCount;

        public ImportCell Get(int row, int column)
        {
            Reads++;
            if (cts is not null && Reads == after)
            {
                cts.Cancel();
            }

            return inner.Get(row, column);
        }

        public ImportMerge? MergeAt(int row, int column) => inner.MergeAt(row, column);
    }

    private sealed class HugeGrid(int rows, int columns) : IImportGrid
    {
        public int Reads { get; private set; }

        public int RowCount => rows;

        public int ColumnCount => columns;

        public ImportCell Get(int row, int column)
        {
            Reads++;
            return default;
        }

        public ImportMerge? MergeAt(int row, int column) => null;
    }
}
