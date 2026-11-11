// src/Ecr.Application/Calculations/MonthRowResultOverlay.cs
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Calculations;

/// <summary>
/// Накладає результати модуля «місяці в колонках» на розрахункові рядки <c>F_&lt;X&gt;</c> таблиці (HSE400):
/// значення рядка за місяць показується в самій сітці документа, у колонці цього місяця.
/// </summary>
/// <remarks>
/// ⛔ <c>D-69</c>: результат методології живе в <c>calc.CalculationResult</c> і в документ приходить посиланням, а не
/// копією в <c>doc.CellValue</c>. <see cref="CalculatedCellOverlay"/> розв'язує посилання лише для колонок
/// <c>Calculated</c> і викидає всі введені комірки прив'язаної колонки — місячну колонку так прив'язати не можна:
/// у ній і ввід (рядки складу), і результат (рядки <c>F_</c>). Тут правило інше, і воно дзеркалить запис модуля:
/// <list type="bullet">
/// <item>рядок — <c>SourceRowKey</c> = ключ рядка <c>F_&lt;X&gt;</c>;</item>
/// <item>колонка — місячна колонка, чий <c>Code</c> дорівнює <c>OutputCode</c> результату;</item>
/// <item>версія — будь-яка версія методологій, прив'язаних до таблиці (<c>cfg.CalculationBinding</c>);</item>
/// <item>екземпляр — свій; результат без екземпляра береться, лише коли свого немає; чужий ігнорується (D-401);</item>
/// <item>кілька різних значень однієї комірки — комірка лишається порожньою (хибне число гірше за відсутнє).</item>
/// </list>
/// Рядки вводу не зачіпаються. Таблиця іншої розкладки або без рядків <c>F_</c> не коштує жодного запиту.
/// </remarks>
public sealed class MonthRowResultOverlay(IMethodologyStore methodologies, ICalculationResultStore results)
{
    /// <summary>Префікс ключа розрахункового рядка; той самий, що в модулі <c>month-columns</c>.</summary>
    public const string CalculatedRowPrefix = "F_";

    /// <summary>Чи рядок розрахунковий (<c>F_&lt;X&gt;</c>).</summary>
    public static bool IsCalculatedRow(string rowKey)
        => rowKey is not null
           && rowKey.Length > CalculatedRowPrefix.Length
           && rowKey.StartsWith(CalculatedRowPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Місячні колонки таблиці «місяці в колонках»: код колонки → її Id; порожньо — таблиця не така.</summary>
    public static IReadOnlyDictionary<string, int> MonthColumns(TableDef table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (table.LayoutKind != TableLayoutKind.MonthsInColumns)
        {
            return columns;
        }

        foreach (var column in table.Columns)
        {
            if (!column.IsDeleted && column.IsMonthColumn && column.MonthNumber is >= 1 and <= 12)
            {
                columns.TryAdd(column.Code, column.Id);
            }
        }

        return columns;
    }

    /// <summary>Накладає результати на комірки розрахункових рядків одного екземпляра таблиці.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період екземпляра.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці.</param>
    /// <param name="tableDefId">Опис таблиці.</param>
    /// <param name="rowIdsByKey">Рядки екземпляра: <c>RowKey</c> → <c>TableRowId</c>.</param>
    /// <param name="cells">Комірки, прочитані зі сховища (і вже накладені колонки <c>Calculated</c>).</param>
    /// <param name="monthColumns">Місячні колонки: код → Id (<see cref="MonthColumns"/>).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ті самі комірки, де комірки рядків <c>F_</c> у місячних колонках замінено результатами.</returns>
    public async Task<IReadOnlyList<CellRecord>> ApplyAsync(
        long documentId,
        int periodKey,
        long tableInstanceId,
        int tableDefId,
        IReadOnlyDictionary<string, long> rowIdsByKey,
        IReadOnlyList<CellRecord> cells,
        IReadOnlyDictionary<string, int> monthColumns,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rowIdsByKey);
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(monthColumns);

        if (monthColumns.Count == 0)
        {
            return cells;
        }

        var calculatedRows = rowIdsByKey.Where(r => IsCalculatedRow(r.Key)).ToList();
        if (calculatedRows.Count == 0)
        {
            return cells;
        }

        var bindings = await methodologies.GetColumnResultBindingsAsync([tableDefId], ct).ConfigureAwait(false);
        var versions = bindings.Where(b => b.TableDefId == tableDefId).SelectMany(b => b.VersionIds).ToHashSet();
        if (versions.Count == 0)
        {
            return cells;
        }

        IReadOnlyList<CalculationResultRow> current = results is ICurrentResultFilterReader narrow
            ? await narrow
                .ReadCurrentMatchingAsync(
                    documentId,
                    periodKey,
                    new CurrentResultFilter([tableInstanceId], [.. monthColumns.Keys], [.. versions]),
                    ct)
                .ConfigureAwait(false)
            : await results.ReadCurrentAsync(documentId, periodKey, ct).ConfigureAwait(false);

        var byCell = current
            .Where(r => r.SourceRowKey is not null && versions.Contains(r.MethodologyVersionId))
            .ToLookup(r => (Row: r.SourceRowKey!.ToUpperInvariant(), Output: r.OutputCode.ToUpperInvariant()));

        var key = new PeriodKey(periodKey);
        var monthColumnIds = monthColumns.Values.ToHashSet();
        var calculatedRowIds = calculatedRows.Select(r => r.Value).ToHashSet();

        // ⚠ Введене в розрахунковий рядок значення — не правда: модуль його не читає, а рядок у шаблоні «лише читання».
        var overlaid = new List<CellRecord>(cells.Count + (calculatedRows.Count * monthColumns.Count));
        overlaid.AddRange(cells.Where(c =>
            !(calculatedRowIds.Contains(c.Address.TableRowId) && monthColumnIds.Contains(c.Address.ColumnDefId))));

        foreach (var (rowKey, rowId) in calculatedRows)
        {
            foreach (var (code, columnId) in monthColumns)
            {
                var candidates = byCell[(rowKey.ToUpperInvariant(), code.ToUpperInvariant())].ToList();
                var values = candidates.Where(r => r.TableInstanceId == tableInstanceId).ToList();
                if (values.Count == 0)
                {
                    values = [.. candidates.Where(r => r.TableInstanceId is null)];
                }

                if (values.Count == 0)
                {
                    continue;
                }

                var first = values[0];
                if (values.Exists(v => v.Value != first.Value || v.UnitId != first.UnitId))
                {
                    continue;
                }

                overlaid.Add(new CellRecord(
                    new CellAddress(key, rowId, columnId),
                    tableDefId,
                    new CellValueData
                    {
                        ValueNumeric = first.Value,
                        ValueUnitId = first.UnitId,
                        IsCalculated = true,
                    }));
            }
        }

        return overlaid;
    }

    /// <summary>
    /// Рішення редагування зрізу, де комірки розрахункових рядків у місячних колонках — «обчислювана комірка»:
    /// сітка показує їх розрахунковими, а не просто заблокованими.
    /// </summary>
    /// <param name="decisions">Рішення зрізу (<c>CanEditSliceAsync</c>).</param>
    /// <param name="rowIdsByKey">Рядки екземпляра.</param>
    /// <param name="monthColumns">Місячні колонки (<see cref="MonthColumns"/>).</param>
    /// <returns>Ті самі рішення, якщо таблиця не має розрахункових рядків; інакше копія з позначками.</returns>
    /// <remarks>
    /// ⚠ Лише заміна причини показу: адрес не додає (рішення, якого не було, не з'являється), а відмову з іншої
    /// причини, ніж «рядок лише для читання», не перекриває — заборона доступу лишається забороною доступу.
    /// </remarks>
    public static IReadOnlyDictionary<CellAddress, EditDecision> MarkCalculated(
        IReadOnlyDictionary<CellAddress, EditDecision> decisions,
        IReadOnlyDictionary<string, long> rowIdsByKey,
        IReadOnlyDictionary<string, int> monthColumns)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(rowIdsByKey);
        ArgumentNullException.ThrowIfNull(monthColumns);

        if (monthColumns.Count == 0)
        {
            return decisions;
        }

        var rowIds = rowIdsByKey.Where(r => IsCalculatedRow(r.Key)).Select(r => r.Value).ToHashSet();
        if (rowIds.Count == 0)
        {
            return decisions;
        }

        var columnIds = monthColumns.Values.ToHashSet();
        var marked = new Dictionary<CellAddress, EditDecision>(decisions.Count);
        foreach (var (address, decision) in decisions)
        {
            var calculated = rowIds.Contains(address.TableRowId)
                             && columnIds.Contains(address.ColumnDefId)
                             && (decision.IsAllowed || decision.Reason == EditDenyReason.RowReadOnly);
            marked[address] = calculated ? EditDecision.Deny(EditDenyReason.CalculatedCell) : decision;
        }

        return marked;
    }
}
