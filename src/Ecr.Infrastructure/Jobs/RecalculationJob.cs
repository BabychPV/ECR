using Ecr.Application.Ports;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Перерахунок: інкрементний за dirty-set або повний за адміністративною
/// командою.
/// </summary>
/// <remarks>
/// **Бюджет повного річного перерахунку — ≤ 10 хвилин** (ПРД-13). Базова лінія
/// чинної системи — 20 хвилин, і формулювання «не гірше» тут не застосовується.
/// Звідси вимоги: паралельне виконання за рівнями топологічного графа, пакетне
/// читання входів, <c>SqlBulkCopy</c> результатів, проміжні значення в пам'яті.
/// </remarks>
public sealed class RecalculationJob(
    Ecr.Application.Recalculation.RecalculationService recalc,
    ISqlCapabilities capabilities) : IBackgroundJob
{
    /// <inheritdoc />
    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: розібрати payload (документ+період або проєкт+рік); " +
            "виконати рівні графа ПАРАЛЕЛЬНО (Parallel.ForEachAsync із обмеженням " +
            "MaxParallelRecalculation), рівень за рівнем; " +
            "результати писати SqlBulkCopy, а не SaveChanges у циклі; " +
            "ІЗОЛЯЦІЯ від інтерактивного піку обов'язкова: на Enterprise — Resource Governor, " +
            "на Standard — знижена стеля воркерів у дні піку (АРХ-7). " +
            "Перерахунок на 10 хвилин не має права з'їсти бюджет p95 операторів.");
}
