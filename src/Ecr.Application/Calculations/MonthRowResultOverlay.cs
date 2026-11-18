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
/// <para>
/// ✎ B80 N0: місячний проєкт з маршрутизованим зберіганням (Q-17, D-474) — місяць <c>m</c> рахує прогін періоду
/// <c>m</c> і пише в екземпляр періоду <c>m</c>. Тому колонка місяця з чужим власником
/// (<see cref="IMonthColumnRouter.ForeignColumnsAsync"/>) читається з екземпляра тієї самої таблиці в періоді-власнику,
/// а не з екземпляра перегляду (там може лежати застарілий результат прогону «усі місяці»). Без роутера, для річного
/// проєкту і для копій аркушів — як раніше.
/// </para>
/// </remarks>
public sealed class MonthRowResultOverlay(
    IMethodologyStore methodologies, ICalculationResultStore results, IMonthColumnRouter? router = null)
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
    public Task<IReadOnlyList<CellRecord>> ApplyAsync(
        long documentId,
        int periodKey,
        long tableInstanceId,
        int tableDefId,
        IReadOnlyDictionary<string, long> rowIdsByKey,
        IReadOnlyList<CellRecord> cells,
        IReadOnlyDictionary<string, int> monthColumns,
        CancellationToken ct)
        => ApplyCoreAsync(documentId, periodKey, tableInstanceId, tableDefId, null, rowIdsByKey, cells, monthColumns, ct);

    /// <summary>
    /// Те саме, що <see cref="ApplyAsync(long, int, long, int, IReadOnlyDictionary{string, long}, IReadOnlyList{CellRecord}, IReadOnlyDictionary{string, int}, CancellationToken)"/>,
    /// але з описом таблиці: на маршрутизованому місячному документі (B80 N0) колонка місяця з чужим власником
    /// читається з екземпляра періоду-власника.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період екземпляра перегляду.</param>
    /// <param name="tableInstanceId">Екземпляр перегляду.</param>
    /// <param name="table">Опис таблиці зі знімка метаданих.</param>
    /// <param name="rowIdsByKey">Рядки екземпляра: <c>RowKey</c> → <c>TableRowId</c>.</param>
    /// <param name="cells">Комірки, прочитані зі сховища.</param>
    /// <param name="monthColumns">Місячні колонки: код → Id (<see cref="MonthColumns"/>).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ті самі комірки, де комірки рядків <c>F_</c> у місячних колонках замінено результатами.</returns>
    public Task<IReadOnlyList<CellRecord>> ApplyAsync(
        long documentId,
        int periodKey,
        long tableInstanceId,
        TableDef table,
        IReadOnlyDictionary<string, long> rowIdsByKey,
        IReadOnlyList<CellRecord> cells,
        IReadOnlyDictionary<string, int> monthColumns,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        return ApplyCoreAsync(documentId, periodKey, tableInstanceId, table.Id, table, rowIdsByKey, cells, monthColumns, ct);
    }

    private async Task<IReadOnlyList<CellRecord>> ApplyCoreAsync(
        long documentId,
        int periodKey,
        long tableInstanceId,
        int tableDefId,
        TableDef? table,
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

        var owners = table is null
            ? []
            : await OwnersAsync(documentId, periodKey, tableInstanceId, table, monthColumns, ct).ConfigureAwait(false);

        // Своє читання — лише місяці, якими володіє перегляд; кожен період-власник — одне вузьке читання свого екземпляра.
        var reads = new Dictionary<int, (long Instance, List<string> Codes)>
        {
            [periodKey] = (tableInstanceId, [.. monthColumns.Keys.Where(c => !owners.ContainsKey(c))]),
        };
        foreach (var (code, owner) in owners)
        {
            if (owner.Instance is not { } instance)
            {
                continue;
            }

            if (!reads.TryGetValue(owner.Period, out var read))
            {
                reads[owner.Period] = read = (instance, []);
            }

            read.Codes.Add(code);
        }

        var byPeriod = new Dictionary<int, ILookup<(string Row, string Output), CalculationResultRow>>();
        foreach (var (period, read) in reads)
        {
            if (read.Codes.Count == 0)
            {
                continue;
            }

            IReadOnlyList<CalculationResultRow> current = results is ICurrentResultFilterReader narrow
                ? await narrow
                    .ReadCurrentMatchingAsync(
                        documentId,
                        period,
                        new CurrentResultFilter([read.Instance], [.. read.Codes], [.. versions]),
                        ct)
                    .ConfigureAwait(false)
                : await results.ReadCurrentAsync(documentId, period, ct).ConfigureAwait(false);

            byPeriod[period] = current
                .Where(r => r.SourceRowKey is not null && versions.Contains(r.MethodologyVersionId))
                .ToLookup(r => (Row: r.SourceRowKey!.ToUpperInvariant(), Output: r.OutputCode.ToUpperInvariant()));
        }

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
                // Чужий місяць без екземпляра у власника — числа немає (хибне число гірше за відсутнє).
                var (period, instance) = owners.TryGetValue(code, out var owner)
                    ? (owner.Period, owner.Instance)
                    : (periodKey, (long?)tableInstanceId);
                if (instance is null || !byPeriod.TryGetValue(period, out var byCell))
                {
                    continue;
                }

                var candidates = byCell[(rowKey.ToUpperInvariant(), code.ToUpperInvariant())].ToList();
                var values = candidates.Where(r => r.TableInstanceId == instance).ToList();
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
    /// B80 N0: колонки місяців з чужим власником → (період-власник, екземпляр тієї самої таблиці в ньому). Порожньо —
    /// роутера немає, документ/екземпляр не маршрутизований або всі місяці свої. Екземпляр <c>null</c> — у власника
    /// немає екземпляра таблиці (або сховище не вміє читати екземпляри року).
    /// </summary>
    private async Task<Dictionary<string, (int Period, long? Instance)>> OwnersAsync(
        long documentId,
        int periodKey,
        long tableInstanceId,
        TableDef table,
        IReadOnlyDictionary<string, int> monthColumns,
        CancellationToken ct)
    {
        var owners = new Dictionary<string, (int Period, long? Instance)>(StringComparer.OrdinalIgnoreCase);
        if (router is null || !router.Enabled || !router.AppliesToTable(table))
        {
            return owners;
        }

        // Той самий вибір, що в MonthRoutedCellStore: маршрутизований лише екземпляр, який роутер розв'язує.
        var routed = await router
            .ResolveInstancesAsync([tableInstanceId], new PeriodKey(periodKey), ct)
            .ConfigureAwait(false);
        if (!routed.TryGetValue(tableInstanceId, out var routing))
        {
            return owners;
        }

        var foreign = (await router.ForeignColumnsAsync(routing.Document, table, routing.PeriodKey, ct).ConfigureAwait(false))
            .Where(f => f.Column.IsMonthColumn
                        && monthColumns.TryGetValue(f.Column.Code, out var id)
                        && id == f.Column.Id)
            .ToList();
        if (foreign.Count == 0)
        {
            return owners;
        }

        IReadOnlyList<int> periods = [.. foreign.Select(f => f.OwnerPeriod.Value).Distinct().Order()];
        var instances = results is ICalculationYearReader year
            ? await year.ReadTableInstancesAsync(documentId, periods, ct).ConfigureAwait(false)
            : [];

        foreach (var column in foreign)
        {
            var owner = instances.FirstOrDefault(i =>
                i.PeriodKey == column.OwnerPeriod.Value && i.TableDefId == table.Id && i.SheetCopyId is null);
            owners[column.Column.Code] = (column.OwnerPeriod.Value, owner?.TableInstanceId);
        }

        return owners;
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
