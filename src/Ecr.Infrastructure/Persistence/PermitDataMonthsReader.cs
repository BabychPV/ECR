using Ecr.Application.Permits.Validity;
using Ecr.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// <see cref="IPermitDataMonthsReader"/>: one grouped query over the document's cells — distinct
/// <c>(PeriodKey, ColumnDefId)</c> with a numeric value — then months in memory from the column and period metadata.
/// </summary>
/// <remarks>
/// ⚠ Runs only when a permit-type header field changes (rare), so a pass over the document's cells is affordable; the
/// grouping keeps the result small (periods × columns), not the cells themselves.
/// </remarks>
public sealed class PermitDataMonthsReader(EcrDbContext db) : IPermitDataMonthsReader
{
    /// <summary>Ceiling of distinct (period, column) pairs: 13 periods × a few thousand columns is far below it.</summary>
    private const int MaxPairs = 200_000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<DateOnly>> DocumentMonthsWithDataAsync(
        long documentId,
        IReadOnlyCollection<int> excludedTableDefIds,
        CancellationToken ct,
        IReadOnlyCollection<int>? excludedColumnDefIds = null)
    {
        var pairs = await PairsAsync(documentId, excludedTableDefIds, excludedColumnDefIds, withCalculated: false, ct)
            .ConfigureAwait(false);
        return (await MonthsAsync(documentId, pairs, ct).ConfigureAwait(false)).Entered;
    }

    /// <inheritdoc />
    public async Task<PermitDocumentMonths> DocumentMonthsByKindAsync(
        long documentId,
        IReadOnlyCollection<int> excludedTableDefIds,
        CancellationToken ct,
        IReadOnlyCollection<int>? excludedColumnDefIds = null)
    {
        var pairs = await PairsAsync(documentId, excludedTableDefIds, excludedColumnDefIds, withCalculated: true, ct)
            .ConfigureAwait(false);
        var (entered, calculated) = await MonthsAsync(documentId, pairs, ct).ConfigureAwait(false);
        var enteredSet = entered.ToHashSet();
        return new PermitDocumentMonths(entered, [.. calculated.Where(m => !enteredSet.Contains(m))]);
    }

    private async Task<List<DataPair>> PairsAsync(
        long documentId,
        IReadOnlyCollection<int> excludedTableDefIds,
        IReadOnlyCollection<int>? excludedColumnDefIds,
        bool withCalculated,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(excludedTableDefIds);
        var excluded = excludedTableDefIds.ToList();
        var excludedColumns = excludedColumnDefIds?.ToList() ?? [];

        var cells =
            from cell in db.CellValues.AsNoTracking()
            join row in db.TableRows.AsNoTracking()
                on new { P = cell.PeriodKeyValue, I = cell.TableRowId } equals new { P = row.PeriodKeyValue, I = row.Id }
            join inst in db.TableInstances.AsNoTracking().OriginalOnly()
                on new { P = row.PeriodKeyValue, I = row.TableInstanceId } equals new { P = inst.PeriodKeyValue, I = inst.Id }
            where inst.DocumentId == documentId
                  && !row.IsDeleted
                  && !excluded.Contains(inst.TableDefId)
                  && !excludedColumns.Contains(cell.ColumnDefId)
                  && cell.ValueNumeric != null
            select new { cell, inst.TableDefId };

        cells = withCalculated
            // Human 08.11 («Попередження»): a calculated zero is not data (PermitMonthCoverage.HasCalculatedData).
            ? cells.Where(x => !x.cell.IsCalculated || x.cell.ValueNumeric != 0m)
            : cells.Where(x => !x.cell.IsCalculated); // only entered/imported values (coordinator 08.11)

        var pairs = await (
                from x in cells
                group x by new { x.cell.PeriodKeyValue, x.cell.ColumnDefId, x.TableDefId, x.cell.IsCalculated } into g
                orderby g.Key.PeriodKeyValue, g.Key.ColumnDefId
                select new DataPair(g.Key.PeriodKeyValue, g.Key.ColumnDefId, g.Key.TableDefId, g.Key.IsCalculated))
            .Take(MaxPairs + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (pairs.Count > MaxPairs)
        {
            throw new InvalidOperationException(
                $"Документ {documentId}: понад {MaxPairs} пар (період, колонка) з даними — межу перевищено.");
        }

        return pairs;
    }

    /// <summary>Months of the entered pairs and of the calculated pairs (each ascending), one metadata pass for both.</summary>
    private async Task<(IReadOnlyList<DateOnly> Entered, IReadOnlyList<DateOnly> Calculated)> MonthsAsync(
        long documentId, List<DataPair> pairs, CancellationToken ct)
    {
        if (pairs.Count == 0)
        {
            return ([], []);
        }

        var tableIds = pairs.Select(p => p.TableDefId).Distinct().ToList();
        var monthColumns = await db.ColumnDefs.AsNoTracking()
            .Where(c => tableIds.Contains(c.TableDefId) && !c.IsDeleted && c.IsMonthColumn && c.MonthNumber != null)
            .OrderBy(c => c.Id)
            .Select(c => new MonthColumn(c.Id, c.TableDefId, c.MonthNumber!.Value))
            .Take(MaxPairs)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var monthOf = monthColumns.ToDictionary(c => c.Id, c => (int)c.Month);
        var monthTables = monthColumns.Select(c => c.TableDefId).ToHashSet();

        var periodKeys = pairs.Select(p => p.PeriodKeyValue).Distinct().ToList();
        var periods = await (
                from doc in db.Documents.AsNoTracking()
                join period in db.Periods.AsNoTracking() on doc.ProjectId equals period.ProjectId
                where doc.Id == documentId && periodKeys.Contains(period.PeriodKeyValue)
                orderby period.PeriodKeyValue
                select new PeriodSpan(period.PeriodKeyValue, period.PeriodStart, period.PeriodEnd))
            .Take(periodKeys.Count)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var spanOf = periods.ToDictionary(p => p.PeriodKeyValue);

        var enteredMonths = new SortedSet<DateOnly>();
        var calculatedMonths = new SortedSet<DateOnly>();
        foreach (var pair in pairs)
        {
            var months = pair.IsCalculated ? calculatedMonths : enteredMonths;
            if (monthTables.Contains(pair.TableDefId))
            {
                if (monthOf.TryGetValue(pair.ColumnDefId, out var month))
                {
                    months.Add(PermitMonthCoverage.Month(pair.PeriodKeyValue / 100, month));
                }

                continue;
            }

            if (spanOf.TryGetValue(pair.PeriodKeyValue, out var span))
            {
                months.UnionWith(PermitMonthCoverage.MonthsOfPeriod(span.Start, span.End));
            }
        }

        return ([.. enteredMonths], [.. calculatedMonths]);
    }

    private sealed record DataPair(int PeriodKeyValue, int ColumnDefId, int TableDefId, bool IsCalculated);

    private sealed record MonthColumn(int Id, int TableDefId, byte Month);

    private sealed record PeriodSpan(int PeriodKeyValue, DateOnly Start, DateOnly End);
}
