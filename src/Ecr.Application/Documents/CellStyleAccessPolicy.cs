// src/Ecr.Application/Documents/CellStyleAccessPolicy.cs
using Ecr.Application.Documents.Dto;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// Ознака «можна фарбувати» для зрізу таблиці (CELL-STYLE P1A): згортає рішення
/// <see cref="IAccessDecisionService.CanStyleSliceAsync"/> (ТЕ САМЕ рішення, що його приймає
/// <c>PATCH …/cell-styles</c>, див. <c>PatchCellStylesHandler</c>) у компактну форму для клієнта.
/// </summary>
/// <remarks>
/// ⛔ Нерозкриття (S6): у згортку входять лише комірки ВИДИМИХ читачеві колонок
/// (<c>visibleColumnCodeById</c>) і рядків зрізу; причини про сховані колонки/аркуші не потрапляють нікуди — ні в
/// <c>DeniedReason</c>, ні в переліки.
///
/// ⚠ Чиста функція: жодних запитів, часу, порядку залежності — результат детермінований (ключі впорядковані
/// ординально).
/// </remarks>
public static class CellStyleAccessPolicy
{
    /// <summary>Ліміт ключів у переліках усього (рядки + колонки + комірки).</summary>
    public const int MaxKeys = 500;

    /// <summary>Згортає рішення в <see cref="CellStyleAccessDto"/>.</summary>
    /// <param name="decisions">Рішення на комірки зрізу (<c>CanStyleSliceAsync</c>); <c>null</c> = немає рішень.</param>
    /// <param name="rowKeyById">Живі рядки зрізу: <c>TableRow.Id</c> → <c>RowKey</c>.</param>
    /// <param name="visibleColumnCodeById">Колонки, які читач бачить: <c>ColumnDef.Id</c> → код.</param>
    public static CellStyleAccessDto Build(
        IReadOnlyDictionary<CellAddress, EditDecision>? decisions,
        IReadOnlyDictionary<long, string> rowKeyById,
        IReadOnlyDictionary<int, string> visibleColumnCodeById)
    {
        ArgumentNullException.ThrowIfNull(rowKeyById);
        ArgumentNullException.ThrowIfNull(visibleColumnCodeById);

        var cells = new List<(string RowKey, string ColumnCode, EditDecision Decision)>();
        if (decisions is not null)
        {
            foreach (var (address, decision) in decisions)
            {
                // Рішення на комірку, якої читач не бачить (рядок без ключа, схована колонка), не береться.
                if (rowKeyById.TryGetValue(address.TableRowId, out var rowKey)
                    && visibleColumnCodeById.TryGetValue(address.ColumnDefId, out var code))
                {
                    cells.Add((rowKey, code, decision));
                }
            }
        }

        if (cells.Count == 0)
        {
            return new CellStyleAccessDto(false, null, [], [], new Dictionary<string, string>(), false);
        }

        var denied = cells.Where(c => !c.Decision.IsAllowed).ToList();

        if (denied.Count == cells.Count)
        {
            // Одна причина на всю таблицю: найчастіша, при рівності — менше значення enum (детермінізм).
            var reason = denied
                .GroupBy(c => c.Decision.Reason)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => (int)g.Key)
                .First().Key;

            return new CellStyleAccessDto(false, reason.ToString(), [], [], new Dictionary<string, string>(), false);
        }

        // Рядок/колонка заборонені ЦІЛКОМ, якщо заборонена кожна їхня видима комірка.
        var rowTotals = cells.GroupBy(c => c.RowKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var columnTotals = cells.GroupBy(c => c.ColumnCode, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var deniedRows = denied.GroupBy(c => c.RowKey, StringComparer.Ordinal)
            .Where(g => g.Count() == rowTotals[g.Key])
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);
        var deniedColumns = denied.GroupBy(c => c.ColumnCode, StringComparer.Ordinal)
            .Where(g => g.Count() == columnTotals[g.Key])
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        var residual = denied
            .Where(c => !deniedRows.Contains(c.RowKey) && !deniedColumns.Contains(c.ColumnCode))
            .OrderBy(c => c.RowKey, StringComparer.Ordinal)
            .ThenBy(c => c.ColumnCode, StringComparer.Ordinal)
            .ToList();

        var budget = MaxKeys;
        var rowKeys = deniedRows.Order(StringComparer.Ordinal).Take(budget).ToList();
        budget -= rowKeys.Count;
        var columnCodes = deniedColumns.Order(StringComparer.Ordinal).Take(budget).ToList();
        budget -= columnCodes.Count;

        var cellMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cell in residual.Take(budget))
        {
            cellMap[$"{cell.RowKey}:{cell.ColumnCode}"] = cell.Decision.Reason.ToString();
        }

        var truncated = rowKeys.Count < deniedRows.Count
                        || columnCodes.Count < deniedColumns.Count
                        || cellMap.Count < residual.Count;

        return new CellStyleAccessDto(true, null, rowKeys, columnCodes, cellMap, truncated);
    }
}
