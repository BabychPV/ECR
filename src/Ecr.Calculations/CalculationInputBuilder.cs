using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Calculations;

/// <summary>Готує аргументи для методології з даних документа.</summary>
public sealed class CalculationInputBuilder(ICellStore cellStore, IMetadataCache metadata)
{
    /// <summary>Будує входи для набору рядків одним пакетом.</summary>
    /// <remarks>
    /// Пакетність принципова: читання по рядку не вкладається в бюджет
    /// 10 хвилин на річний перерахунок.
    /// </remarks>
    public Task<IReadOnlyList<CalculationInput>> BuildAsync(
        long tableInstanceId, IReadOnlyList<string> rowKeys, PeriodKey periodKey, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: один ReadSliceAsync на таблицю; зіставити колонки з іменами аргументів " +
            "методології; для кожного рядка зібрати CalculationInput. " +
            "Зберегти входи в calc.CalculationInput — це частина доказової бази " +
            "відтворюваності (B-4).");
}
