using Ecr.Application.Ports;
using Ecr.Domain.Enums;

namespace Ecr.Calculations;

/// <summary>Записує результати прогону.</summary>
/// <remarks>
/// Пише **тільки** в <c>calc.CalculationResult</c>. У <c>doc.CellValue</c>
/// результати методологій не потрапляють ніколи (D-69): інакше нічний
/// перерахунок писав би десятки мільйонів рядків у партиції документів і
/// роздував <c>aud.CellChange</c>.
/// </remarks>
public sealed class CalculationOutputWriter(ICalculationResultStore store, IUnitOfWork uow)
{
    /// <summary>Записує результати і трейс пакетно.</summary>
    /// <param name="calculationRunId">Прогін.</param>
    /// <param name="outputs">Результати рядків.</param>
    /// <param name="traceLevel">Рівень трейсу версії методології.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task WriteAsync(
        long calculationRunId,
        IReadOnlyList<CalculationOutput> outputs,
        TraceLevel traceLevel,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outputs);

        if (outputs.Count == 0)
        {
            return;
        }

        // Пакетно, одним проходом: SaveChanges у циклі заборонений — бюджет
        // річного перерахунку 10 хвилин (ПРД-13), і коміт на рядок з'їдає його
        // сам собою.
        await store.WriteResultsAsync(calculationRunId, outputs, ct).ConfigureAwait(false);

        // Трейс — лише те, що передбачає рівень версії. Керуємо тим, ЩО
        // пишемо, а не скільки зберігаємо (ЗБР-3).
        await store.WriteTraceAsync(calculationRunId, outputs, traceLevel, ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
