using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Permits.Validity;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// Rule (h), human 08.11: a row bound to a permit (any permit-type Lookup column, any sheet) may hold data only in months
/// the permit covers (<see cref="PermitMonthCoverage"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>h1/h2/h5 — the permit is picked or changed.</b> All months of the row that hold data (after this batch) must be
/// covered by the new permit. A row without data in a month table: the permit must touch the year at all.
/// </para>
/// <para>
/// <b>h3 — a value is entered.</b> Only the months this batch writes are checked, not the whole row: a row that already
/// violates the rule (h4) must stay fixable — clearing January or entering March is not refused because of January.
/// </para>
/// <para>
/// ⚠ The row is read LOGICALLY (<c>ICellStore.ReadSliceAsync</c>): with Q-17 month routing the month cells live in the
/// owner instances and the permit in the year anchor, and the routed store merges them onto the view instance's rows.
/// </para>
/// <para>
/// ⚠ Hot path: a table without Lookup columns, or a batch that neither changes a Lookup nor writes data, costs nothing.
/// The permit-type test is in memory (<c>IPermitRegistryIndex</c>, no round-trip); the slice and the entries are read only
/// when a permit-type column of the table is picked, or data is written in a row of a table that has one.
/// </para>
/// <para>
/// Refusal: the existing <c>422 ECR-CELL-4223</c> (Lookup reference not usable) with its own <c>messageKey</c>
/// <c>err.ECR-CELL-4223.permitNotCoveringMonths</c>; <c>cells</c> names the cells to mark (the permit cell for h1/h2, the
/// month cells for h3).
/// </para>
/// </remarks>
public sealed partial class PatchCellsHandler
{
    /// <summary>Ids of the permit-type Lookup columns of the table (memoized on the context).</summary>
    private async Task<IReadOnlySet<int>> PermitColumnIdsAsync(RequestContext context, CancellationToken ct)
    {
        if (context.PermitColumnIds is { } known)
        {
            return known;
        }

        var lookups = context.Table.Columns
            .Where(c => !c.IsDeleted && c.DataType == CellDataType.Lookup && c.LookupRegistryDefId is not null)
            .ToList();

        if (permitTargets is null || lookups.Count == 0)
        {
            return context.PermitColumnIds = new HashSet<int>();
        }

        // In memory (IPermitRegistryIndex): a Land PATCH pays no round-trip and never asks the group resolver.
        var isPermitTarget = await PermitTargets.LoadAsync(permitTargets, ct).ConfigureAwait(false);

        return context.PermitColumnIds = lookups
            .Where(c => isPermitTarget(c.LookupRegistryDefId!.Value))
            .Select(c => c.Id)
            .ToHashSet();
    }

    /// <summary>Refuses a batch that would leave permit-bound data outside the permit validity.</summary>
    private async Task EnsurePermitValidityCoversDataAsync(
        RequestContext context,
        CellChangeLists changes,
        IReadOnlyDictionary<CellAddress, CellValueData> previous,
        CancellationToken ct)
    {
        if (permitTargets is null || changes.Upserts.Count == 0)
        {
            return;
        }

        var columns = context.Table.Columns.Where(c => !c.IsDeleted).ToDictionary(c => c.Id);
        if (!columns.Values.Any(c => c.DataType == CellDataType.Lookup))
        {
            return;
        }

        var monthOf = PermitMonthCoverage.MonthColumns(context.Table).ToDictionary(c => c.Id, c => (int)c.MonthNumber!.Value);
        var monthTable = monthOf.Count > 0;

        bool Changed(CellRecord u) => !(previous.TryGetValue(u.Address, out var was) && was == u.Value);

        // Cheap pre-filter on the batch alone: a changed Lookup, or newly written data.
        var lookupChanges = changes.Upserts
            .Where(u => columns.TryGetValue(u.Address.ColumnDefId, out var c) && c.DataType == CellDataType.Lookup
                        && u.Value.ValueRegistryEntryId is not null && Changed(u))
            .ToList();
        var dataWrites = changes.Upserts
            .Where(u => PermitMonthCoverage.HasData(u.Value) && Changed(u)
                        && (monthTable ? monthOf.ContainsKey(u.Address.ColumnDefId) : columns.ContainsKey(u.Address.ColumnDefId)))
            .ToList();

        if (lookupChanges.Count == 0 && dataWrites.Count == 0)
        {
            return;
        }

        var permitColumns = await PermitColumnIdsAsync(context, ct).ConfigureAwait(false);
        if (permitColumns.Count == 0)
        {
            return;
        }

        var pickedRows = lookupChanges
            .Where(u => permitColumns.Contains(u.Address.ColumnDefId))
            .Select(u => (u.Address.TableRowId, u.Address.ColumnDefId))
            .ToHashSet();
        var writtenMonths = dataWrites
            .Where(u => !permitColumns.Contains(u.Address.ColumnDefId))
            .GroupBy(u => u.Address.TableRowId)
            .ToDictionary(g => g.Key, g => g.ToList());

        if (pickedRows.Count == 0 && writtenMonths.Count == 0)
        {
            return;
        }

        var rowIds = pickedRows.Select(p => p.TableRowId).Concat(writtenMonths.Keys).ToHashSet();
        var rows = await LogicalRowsAsync(context, changes, rowIds, ct).ConfigureAwait(false);

        // ✎ Feature (j) P2: rows with a permit history — each month against the permits in force in it.
        var history = await RowHistoryAsync(context, changes, rowIds, ct).ConfigureAwait(false);

        var entryIds = rows.Values
            .SelectMany(cells => permitColumns.Select(id => cells.GetValueOrDefault(id)?.ValueRegistryEntryId))
            .OfType<long>()
            .Concat(history.Values.SelectMany(segments => segments.Select(s => s.RegistryEntryId)))
            .Distinct()
            .ToList();
        if (entryIds.Count == 0)
        {
            return;
        }

        var entries = (await registries.FindEntriesByIdsAsync(entryIds, ct).ConfigureAwait(false)).ToDictionary(e => e.Id);
        if (!entries.Values.Any(e => e.ValidFrom is not null || e.ValidTo is not null))
        {
            return; // no permit with a window — nothing can be uncovered
        }

        var bounds = await periods
            .FindPeriodBoundsAsync(context.Instance.DocumentId, context.PeriodKey.Value, ct)
            .ConfigureAwait(false);
        var year = context.PeriodKey.Year;

        IReadOnlyList<DateOnly> PeriodMonths()
            => monthTable
                ? [.. Enumerable.Range(1, 12).Select(m => PermitMonthCoverage.Month(year, m))]
                : bounds is { } b ? PermitMonthCoverage.MonthsOfPeriod(b.PeriodStart, b.PeriodEnd) : [];

        IReadOnlyList<DateOnly> RowMonths(IReadOnlyDictionary<int, CellValueData> cells)
            => PermitMonthCoverage.RowMonthsWithData(monthOf, year, cells, permitColumns, PeriodMonths());

        var violations = new List<PermitViolation>();

        foreach (var (rowId, cells) in rows.OrderBy(r => r.Key))
        {
            foreach (var permitColumn in permitColumns.Order())
            {
                if (cells.GetValueOrDefault(permitColumn)?.ValueRegistryEntryId is not { } entryId
                    || !entries.TryGetValue(entryId, out var entry))
                {
                    continue;
                }

                IReadOnlyList<DateOnly> months;
                IReadOnlyList<int> markColumns;

                if (pickedRows.Contains((rowId, permitColumn)))
                {
                    // h1/h2: the new permit against every month with data; no data — it must touch the year/period.
                    var withData = RowMonths(cells);
                    months = withData.Count > 0 ? withData : PeriodMonths();
                    markColumns = [permitColumn];

                    if (withData.Count == 0 && PermitMonthCoverage.Uncovered(entry.ValidFrom, entry.ValidTo, months).Count < months.Count)
                    {
                        continue; // touches the year: fine while the row has no data
                    }
                }
                else if (writtenMonths.TryGetValue(rowId, out var written))
                {
                    // h3: only the months this batch writes.
                    months = monthTable
                        ? [.. written.Select(u => PermitMonthCoverage.Month(year, monthOf[u.Address.ColumnDefId]))]
                        : PeriodMonths();
                    markColumns = [.. written.Select(u => u.Address.ColumnDefId).Distinct()];
                }
                else
                {
                    continue;
                }

                if (history.TryGetValue((rowId, permitColumn), out var segments))
                {
                    // A permit history: the picked (base) permit answers only for the months it is in force in; a written
                    // month answers to every permit in force in it, each on its own days.
                    var spans = Permits.History.PermitHistoryCoverage.SpansOf(entryId, segments);
                    IReadOnlyList<DateOnly> scoped = pickedRows.Contains((rowId, permitColumn))
                        ? [.. months.Where(m => Permits.History.PermitTimeline.InMonth(spans, m).Any(s => s.From is null))]
                        : months;
                    var missed = Permits.History.PermitHistoryCoverage.Uncovered(spans, entries, scoped);
                    if (missed.Count > 0)
                    {
                        var guilty = Permits.History.PermitHistoryCoverage.FirstUncoveredEntry(spans, entries, missed[0]) is { } id
                                     && entries.TryGetValue(id, out var e) ? e : entry;
                        violations.Add(new PermitViolation(rowId, permitColumn, guilty, missed, markColumns));
                    }

                    continue;
                }

                var uncovered = PermitMonthCoverage.Uncovered(entry.ValidFrom, entry.ValidTo, months);
                if (uncovered.Count > 0)
                {
                    violations.Add(new PermitViolation(rowId, permitColumn, entry, uncovered, markColumns));
                }
            }
        }

        if (violations.Count > 0)
        {
            // ⛔ S6: the refusal names the permit and months — only what the author can read: a hidden month column's data
            // must not show through «Jan 2026», a hidden permit column's value not through its number. The refusal
            // itself stands either way.
            var scope = (await access.ReadScopeAsync(context.Profile, context.Instance.DocumentId, ct).ConfigureAwait(false))
                .InPeriod(context.PeriodKey);
            var hiddenMonths = monthOf
                .Where(p => !scope.CanReadColumn(p.Key))
                .Select(p => PermitMonthCoverage.Month(year, p.Value))
                .ToHashSet();

            throw PermitRefusal(context, changes, violations, hiddenMonths, scope.CanReadColumn(violations[0].PermitColumnId));
        }
    }

    /// <summary>
    /// The rows' cells as the reader sees them (logical slice) with this batch applied on top: upserts replace, deletes
    /// clear. New rows (placeholders) have only the batch.
    /// </summary>
    private async Task<Dictionary<long, Dictionary<int, CellValueData>>> LogicalRowsAsync(
        RequestContext context, CellChangeLists changes, IReadOnlySet<long> rowIds, CancellationToken ct)
    {
        var rows = rowIds.ToDictionary(id => id, _ => new Dictionary<int, CellValueData>());

        if (rowIds.Any(id => !IsPlaceholder(id)))
        {
            var slice = await cellStore
                .ReadSliceAsync(context.Instance.TableInstanceId, context.PeriodKey, ct)
                .ConfigureAwait(false);

            foreach (var cell in slice)
            {
                if (rows.TryGetValue(cell.Address.TableRowId, out var row))
                {
                    row[cell.Address.ColumnDefId] = cell.Value;
                }
            }
        }

        foreach (var delete in changes.Deletes)
        {
            if (rows.TryGetValue(delete.TableRowId, out var row))
            {
                row.Remove(delete.ColumnDefId);
            }
        }

        foreach (var upsert in changes.Upserts)
        {
            if (rows.TryGetValue(upsert.Address.TableRowId, out var row))
            {
                row[upsert.Address.ColumnDefId] = upsert.Value;
            }
        }

        return rows;
    }

    /// <summary>The refusal: names the first violation's permit, validity and months; marks all violating cells.</summary>
    /// <remarks>
    /// <c>cells</c> name only columns this batch writes (the picked permit cell for h1/h2, the month cells for h3) — an
    /// echo of the request. The permit column of h3 (not written) is named only when the author can read it.
    /// </remarks>
    private BusinessRuleException PermitRefusal(
        RequestContext context,
        CellChangeLists changes,
        List<PermitViolation> violations,
        HashSet<DateOnly> hiddenMonths,
        bool permitReadable)
    {
        var first = violations[0];
        var language = currentUser.Language;
        var codeOf = context.Snapshot.ColumnsById;
        string RowKeyOf(long rowId) => changes.RowKeyById.GetValueOrDefault(rowId) ?? string.Empty;
        string CodeOf(int columnId) => codeOf.TryGetValue(columnId, out var c) ? c.Code : string.Empty;

        var cells = violations
            .SelectMany(v => v.MarkColumns.Select(column => new
            {
                RowKey = RowKeyOf(v.RowId),
                ColumnCode = CodeOf(column),
                EntryId = v.Entry.Id,
            }))
            .Distinct()
            .ToList<object>();

        var permit = permitReadable ? PermitName(first.Entry, language) : "…";
        var validity = permitReadable ? PermitMonthCoverage.DescribeValidity(first.Entry.ValidFrom, first.Entry.ValidTo) : "…";
        var permitColumnCode = permitReadable ? CodeOf(first.PermitColumnId) : string.Empty;
        var shownMonths = first.Uncovered.Where(m => !hiddenMonths.Contains(m)).ToList();
        var months = shownMonths.Count > 0 ? PermitMonthCoverage.DescribeMonths(shownMonths, language) : "…";

        return new BusinessRuleException(
            ErrorCodes.CellRegistryEntryMissing,
            $"Дозвіл «{permit}» ({validity}) не покриває місяців з даними: {months}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-CELL-4223.permitNotCoveringMonths",
                ["permit"] = permit,
                ["validity"] = validity,
                ["months"] = months,
                ["rowKey"] = RowKeyOf(first.RowId),
                ["columnCode"] = permitColumnCode,
                ["cellCount"] = cells.Count.ToString(CultureInfo.InvariantCulture),
                ["cells"] = cells,
            });
    }

    /// <summary>
    /// Feature (j) P2: the permit segments of the batch's rows (logical address: table, sheet copy, row key, permit column),
    /// keyed by (row id, permit column). Empty — no history (every row without one costs at most one query per batch).
    /// </summary>
    private async Task<Dictionary<(long RowId, int ColumnId), IReadOnlyList<Domain.Entities.Documents.PermitSegment>>> RowHistoryAsync(
        RequestContext context, CellChangeLists changes, HashSet<long> rowIds, CancellationToken ct)
    {
        var result = new Dictionary<(long RowId, int ColumnId), IReadOnlyList<Domain.Entities.Documents.PermitSegment>>();
        if (permitHistory is null || rowIds.Count == 0)
        {
            return result;
        }

        var keys = rowIds
            .Select(id => (Id: id, Key: changes.RowKeyById.GetValueOrDefault(id)))
            .Where(p => p.Key is not null)
            .ToDictionary(p => p.Key!, p => p.Id, StringComparer.Ordinal);
        if (keys.Count == 0)
        {
            return result;
        }

        var all = await permitHistory.ListAsync(context.Instance.DocumentId, ct).ConfigureAwait(false);
        foreach (var group in all
                     .Select(r => r.Segment)
                     .Where(s => s.TableDefId == context.Instance.TableDefId && s.SheetCopyId == context.Instance.SheetCopyId
                                 && s.RowKey is not null && keys.ContainsKey(s.RowKey))
                     .GroupBy(s => (keys[s.RowKey!], s.ColumnDefId!.Value)))
        {
            result[group.Key] = [.. group.OrderBy(s => s.ValidFrom)];
        }

        return result;
    }

    /// <summary>
    /// Feature (j) j3: a single entered/imported value into a month the row shares between two permits is refused
    /// (<c>422 ECR-CELL-4225 splitRequired</c>) — the month is split (<c>PUT …/permit-history/splits</c>), never one value.
    /// The split handler's own total write (<see cref="Permits.History.PermitSplitWrite"/>), calculated values and
    /// non-human origins (collection, recalculation) are not refused.
    /// </summary>
    private async Task EnsureSharedMonthsAreSplitAsync(
        RequestContext context,
        CellChangeLists changes,
        IReadOnlyDictionary<CellAddress, CellValueData> previous,
        string origin,
        CancellationToken ct)
    {
        if (permitHistory is null || permitTargets is null || Permits.History.PermitSplitWrite.Active
            || changes.Upserts.Count == 0 || !CellChangeOrigins.IsHuman(origin))
        {
            return;
        }

        var monthOf = PermitMonthCoverage.MonthColumns(context.Table).ToDictionary(c => c.Id, c => (int)c.MonthNumber!.Value);
        var numeric = context.Table.Columns
            .Where(c => !c.IsDeleted && c.DataType is CellDataType.Decimal or CellDataType.Int)
            .Select(c => c.Id)
            .ToHashSet();
        var writes = changes.Upserts
            .Where(u => PermitMonthCoverage.HasData(u.Value) && u.Value.ValueNumeric is not null
                        && (monthOf.Count > 0 ? monthOf.ContainsKey(u.Address.ColumnDefId) : numeric.Contains(u.Address.ColumnDefId))
                        && !(previous.TryGetValue(u.Address, out var was) && was == u.Value))
            .ToList();
        if (writes.Count == 0)
        {
            return;
        }

        var permitColumns = await PermitColumnIdsAsync(context, ct).ConfigureAwait(false);
        if (permitColumns.Count == 0)
        {
            return;
        }

        var rowIds = writes.Select(w => w.Address.TableRowId).ToHashSet();
        var history = await RowHistoryAsync(context, changes, rowIds, ct).ConfigureAwait(false);
        if (history.Count == 0)
        {
            return;
        }

        var year = context.PeriodKey.Year;
        DateOnly MonthOf(int columnId)
            => monthOf.TryGetValue(columnId, out var m) ? new DateOnly(year, m, 1) : new DateOnly(year, context.PeriodKey.Sequence, 1);

        var hits = writes
            .Select(w => (Write: w, Month: MonthOf(w.Address.ColumnDefId),
                          History: history.FirstOrDefault(h => h.Key.RowId == w.Address.TableRowId
                              && Permits.History.PermitTimeline.SharedMonths(
                                  Permits.History.PermitHistoryCoverage.SpansOf(0, h.Value)).Contains(MonthOf(w.Address.ColumnDefId)))))
            .Where(h => h.History.Value is not null)
            .ToList();
        if (hits.Count == 0)
        {
            return;
        }

        // Names of the permits of the first refused month (base permit = the row's permit cell).
        var first = hits[0];
        var cells = await LogicalRowsAsync(context, changes, new HashSet<long> { first.Write.Address.TableRowId }, ct).ConfigureAwait(false);
        var baseId = cells[first.Write.Address.TableRowId].GetValueOrDefault(first.History.Key.ColumnId)?.ValueRegistryEntryId ?? 0;
        var spans = Permits.History.PermitHistoryCoverage.SpansOf(baseId, first.History.Value);
        var inMonth = Permits.History.PermitTimeline.InMonth(spans, first.Month);
        var entries = (await registries.FindEntriesByIdsAsync([.. inMonth.Select(s => s.EntryId).Distinct()], ct).ConfigureAwait(false))
            .ToDictionary(e => e.Id);
        var language = currentUser.Language;
        var permits = string.Join(" / ", inMonth.Select(s => entries.TryGetValue(s.EntryId, out var e) ? PermitName(e, language) : "…"));
        string CodeOf(int columnId) => context.Snapshot.ColumnsById.TryGetValue(columnId, out var c) ? c.Code : string.Empty;
        var marked = hits
            .Select(h => new
            {
                RowKey = changes.RowKeyById.GetValueOrDefault(h.Write.Address.TableRowId) ?? string.Empty,
                ColumnCode = CodeOf(h.Write.Address.ColumnDefId),
            })
            .Distinct()
            .ToList<object>();

        throw new BusinessRuleException(
            ErrorCodes.CellSplitRequired,
            $"Місяць {first.Month:MM.yyyy} рядка ділиться між дозволами ({permits}) — введіть частину для кожного дозволу.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-CELL-4225.splitRequired",
                ["rowKey"] = changes.RowKeyById.GetValueOrDefault(first.Write.Address.TableRowId) ?? string.Empty,
                ["month"] = first.Month.ToString("MM.yyyy", CultureInfo.InvariantCulture),
                ["permits"] = permits,
                ["cellCount"] = marked.Count.ToString(CultureInfo.InvariantCulture),
                ["cells"] = marked,
            });
    }

    /// <summary>The permit as people know it: its number (display text in the reader's language), else its code.</summary>
    internal static string PermitName(RegistryEntry entry, string? language)
        => entry.DisplayL10n.Get(language ?? "en") is { Length: > 0 } shown ? shown : entry.Code;

    /// <summary>One row whose permit does not cover some months.</summary>
    private sealed record PermitViolation(
        long RowId, int PermitColumnId, RegistryEntry Entry, IReadOnlyList<DateOnly> Uncovered, IReadOnlyList<int> MarkColumns);
}
