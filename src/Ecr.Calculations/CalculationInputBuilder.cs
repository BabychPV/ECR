using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Calculations;

/// <summary>Готує аргументи для методології з даних документа.</summary>
public sealed class CalculationInputBuilder(ICellStore cellStore, IMetadataCache metadata, IRowStore rows)
{
    /// <summary>Будує входи для набору рядків одним пакетом.</summary>
    /// <param name="tableInstanceId">Екземпляр таблиці.</param>
    /// <param name="rowKeys">Рядки, які рахуємо.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="methodology">Версія методології, яку виконують.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Пакетність принципова: читання по рядку не вкладається в бюджет
    /// 10 хвилин на річний перерахунок. Один <c>ReadSliceAsync</c> на таблицю,
    /// один <c>GetRowIdsAsync</c> — і жодного запиту в циклі.
    /// </remarks>
    public async Task<IReadOnlyList<CalculationInput>> BuildAsync(
        long tableInstanceId,
        IReadOnlyList<string> rowKeys,
        PeriodKey periodKey,
        MethodologyDescriptor methodology,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rowKeys);
        ArgumentNullException.ThrowIfNull(methodology);

        if (rowKeys.Count == 0)
        {
            return [];
        }

        var instance = await rows.ResolveTableInstanceAsync(tableInstanceId, ct).ConfigureAwait(false);
        var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
        var slice = await cellStore.ReadSliceAsync(tableInstanceId, ct).ConfigureAwait(false);
        var rowIds = await rows.GetRowIdsAsync(tableInstanceId, periodKey, ct).ConfigureAwait(false);

        // ⚠ Ім'я аргументу — це КОД колонки, а не її ідентифікатор: формула
        // методології пише `@Jan`, і зіставлення за числом зробило б її
        // залежною від конкретного шаблону, тобто непереносною.
        var codeById = snapshot.ColumnsById.ToDictionary(p => p.Key, p => p.Value.Code);

        var byRow = slice
            .GroupBy(c => c.Address.TableRowId)
            .ToDictionary(g => g.Key, g => g.ToList());

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
                        c.Value.ValueUnitId ?? snapshot.ColumnsById[c.Address.ColumnDefId].UnitId))
                    .ToList()
                : [];

            inputs.Add(new CalculationInput(
                methodology,
                instance.DocumentId,
                tableInstanceId,
                periodKey,
                rowKey,
                arguments));
        }

        return inputs;
    }
}
