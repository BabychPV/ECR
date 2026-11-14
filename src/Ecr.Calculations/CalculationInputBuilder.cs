using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Calculations;

/// <summary>Готує аргументи для методології з даних документа.</summary>
public sealed class CalculationInputBuilder(
    ICellStore cellStore,
    IMetadataCache metadata,
    IRowStore rows,

    // ✎ C-5 (рішення PO 11.11): дефолти колонок для незаповнених комірок (власне читання; зріз зіставлення вже доповнено).
    Application.Recalculation.ColumnDefaultValues? defaults = null)
{
    /// <summary>Будує входи для набору рядків одним пакетом.</summary>
    /// <param name="tableInstanceId">Екземпляр таблиці.</param>
    /// <param name="rowKeys">Рядки, які рахуємо.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="methodology">Версія методології, яку виконують.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="read">
    /// Зріз і ключі рядків, уже прочитані зіставленням правил (<see cref="RowMatchOutcome.Read"/>, AN-13 D4);
    /// беруться, лише якщо це той самий екземпляр і той самий період, інакше — власне читання.
    /// </param>
    /// <remarks>
    /// ⚠ Пакетність принципова: читання по рядку не вкладається в бюджет
    /// 10 хвилин на річний перерахунок. Один <c>ReadSliceAsync</c> на таблицю,
    /// один <c>GetRowIdsAsync</c> — і жодного запиту в циклі; з <paramref name="read"/> — жодного.
    /// </remarks>
    public async Task<IReadOnlyList<CalculationInput>> BuildAsync(
        long tableInstanceId,
        IReadOnlyList<string> rowKeys,
        PeriodKey periodKey,
        MethodologyDescriptor methodology,
        CancellationToken ct,
        TableSliceRead? read = null)
    {
        ArgumentNullException.ThrowIfNull(rowKeys);
        ArgumentNullException.ThrowIfNull(methodology);

        if (rowKeys.Count == 0)
        {
            return [];
        }

        var instance = await rows.ResolveTableInstanceAsync(tableInstanceId, ct).ConfigureAwait(false);
        var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
        // O3c: період САМОГО екземпляра (не параметр `periodKey`) — той самий
        // зріз, що без ключа, але з ключем партиції замість пошуку по всіх.
        var instancePeriod = new PeriodKey(instance.PeriodKey);
        var reusable = read is not null && read.TableInstanceId == tableInstanceId && read.PeriodKey == instancePeriod;
        var slice = reusable
            ? read!.Cells
            : await cellStore.ReadSliceAsync(tableInstanceId, instancePeriod, ct).ConfigureAwait(false);
        var rowIds = reusable && read!.PeriodKey == periodKey
            ? read.RowIds
            : await rows.GetRowIdsAsync(tableInstanceId, periodKey, ct).ConfigureAwait(false);
        if (!reusable && defaults is not null)
        {
            // ✎ C-5: аргумент `@Col` незаповненої комірки — дефолт колонки, як у правилі й формулах (02b §6.3).
            slice = (await defaults.ForVersionAsync(instance.TemplateVersionId, ct).ConfigureAwait(false))
                .Complete(slice, instance.TableDefId, rowIds, instancePeriod);
        }

        // ⚠ Ім'я аргументу — це КОД колонки, а не її ідентифікатор: формула
        // методології пише `@Jan`, і зіставлення за числом зробило б її
        // залежною від конкретного шаблону, тобто непереносною.
        var codeById = snapshot.ColumnsById.ToDictionary(p => p.Key, p => p.Value.Code);

        var byRow = slice
            .GroupBy(c => c.Address.TableRowId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // ✎ LAND-YEAR L2a: псевдоніми місячних входів (`cfg.MonthInputAlias`, лише версії YearWithMonthColumns) — аргумент
        // `@Alias` прогону місяця m бере значення місячної колонки m. Місяць — порядковий номер періоду прогону (у місячному
        // проєкті = місяць); псевдонімів немає — жодної роботи.
        var aliases = periodKey.Sequence is >= 1 and <= 12
            ? snapshot.MonthInputAliasesFor(instance.TableDefId, periodKey.Sequence)
            : [];

        var inputs = new List<CalculationInput>(rowKeys.Count);

        foreach (var rowKey in rowKeys)
        {
            if (!rowIds.TryGetValue(rowKey, out var rowId))
            {
                continue;
            }

            var arguments = byRow.TryGetValue(rowId, out var cells)
                ? cells
                    .Where(c => codeById.ContainsKey(c.Address.ColumnDefId))
                    .Select(c => new CalculationArgument(
                        codeById[c.Address.ColumnDefId],
                        c.Value.ValueNumeric,
                        c.Value.ValueString,

                        // Одиниця береться з КОМІРКИ, якщо вона там є
                        // (ФВ-16.8), інакше з колонки. Значення зберігається в
                        // одиниці джерела — конверсія на межі, не в сховищі
                        // (ФВ-16.10, D-79).
                        c.Value.ValueUnitId ?? snapshot.ColumnsById[c.Address.ColumnDefId].UnitId,

                        // Id запису з `Lookup`-комірки — `EntryRef` для `REGFIND`,
                        // `REGFIELD`, `ROW.X = @Arg` (§5.3). Інша комірка його не має.
                        //
                        // ✎ L2-3: id віддається ЗАВЖДИ. Чи бачить його ФОРМУЛА, вирішує
                        // `GenericCalculationModule.ToValue` за режимом: у `Legacy` формули
                        // лишаються побітно такими, як до RT-23a (D-161), а ПРАВИЛО
                        // категорії бачить id в обох режимах (розширення D-161).
                        c.Value.ValueRegistryEntryId))
                    .ToList()
                : [];

            if (aliases.Count > 0 && cells is not null)
            {
                arguments = WithAliases(arguments, cells, aliases, snapshot);
            }

            inputs.Add(new CalculationInput(
                methodology,
                instance.DocumentId,
                tableInstanceId,
                periodKey,
                rowKey,
                // AN-5: похідні аргументи події факела (Total/Duration/FlareUnitMode/…) — лише для рядка
                // події й лише там, де колонки з тим самим кодом немає; нових запитів нема.
                FlareEventArguments.Derive(arguments)));
        }

        return inputs;
    }

    /// <summary>
    /// ✎ LAND-YEAR L2a: додає аргументи псевдонімів місяця прогону — ім'я псевдоніма, значення комірки його колонки в
    /// цьому рядку (той самий вигляд аргументу, що й для колонки: одиниця комірки або колонки, id запису довідника).
    /// </summary>
    /// <remarks>
    /// Незаповнена колонка-ціль — аргументу немає (як і для незаповненої колонки). Ім'я, що вже є серед аргументів (код
    /// живої колонки), не перекривається: публікація таке відхиляє (<c>monthInputAliasInvalid</c>), а тут перемагає
    /// колонка — правда комірки важливіша за псевдонім.
    /// </remarks>
    private static List<CalculationArgument> WithAliases(
        List<CalculationArgument> arguments,
        List<CellRecord> cells,
        IReadOnlyList<Domain.Entities.Configuration.MonthInputAlias> aliases,
        Domain.Entities.Configuration.TemplateVersionSnapshot snapshot)
    {
        var names = arguments.Select(a => a.ArgumentCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byColumn = new Dictionary<int, CellRecord>(cells.Count);
        foreach (var cell in cells)
        {
            byColumn.TryAdd(cell.Address.ColumnDefId, cell);
        }

        foreach (var alias in aliases)
        {
            if (names.Contains(alias.Alias) || !byColumn.TryGetValue(alias.ColumnDefId, out var cell))
            {
                continue;
            }

            names.Add(alias.Alias);
            arguments.Add(new CalculationArgument(
                alias.Alias,
                cell.Value.ValueNumeric,
                cell.Value.ValueString,
                cell.Value.ValueUnitId
                    ?? (snapshot.ColumnsById.TryGetValue(alias.ColumnDefId, out var column) ? column.UnitId : null),
                cell.Value.ValueRegistryEntryId));
        }

        return arguments;
    }
}
