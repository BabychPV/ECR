using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Calculations;

/// <summary>
/// Оркеструє прогін розрахунку: підбирає методології, будує порядок, виконує
/// модулі, записує результати.
/// </summary>
/// <remarks>
/// **Бюджет повного річного перерахунку — ≤ 10 хвилин** (ПРД-13). Базова лінія
/// чинної системи — 20 хвилин, тому «не гірше» тут не працює: потрібне
/// щонайменше дворазове прискорення. Це і диктує архітектуру нижче.
/// </remarks>
public sealed class CalculationOrchestrator(
    MethodologyResolver resolver,
    IEnumerable<ICalculationModule> modules,
    CalculationInputBuilder inputBuilder,
    CalculationOutputWriter outputWriter,
    IFormulaEngine formulaEngine)
{
    /// <summary>Виконує прогін.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="periodKey">Період; <c>null</c> — повний рік.</param>
    /// <param name="triggeredByUserId">Хто запустив; <c>null</c> — за розкладом.</param>
    /// <param name="progress">Канал прогресу для UI.</param>
    public Task<long> RunAsync(int projectId, PeriodKey? periodKey, int? triggeredByUserId,
                               IJobProgress progress, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO — усе нижче випливає з бюджету 10 хвилин:\n" +
            "1) створити calc.CalculationRun;\n" +
            "2) підібрати методології ПРАВИЛАМИ (MethodologyRule.MatchJson), не жорстким списком;\n" +
            "3) побудувати граф залежностей МІЖ методологіями (MethodologyDependency бере участь " +
            "   у топологічному порядку нарівні з формулами) і розбити на РІВНІ;\n" +
            "4) виконувати рівень за рівнем, усередині рівня — ПАРАЛЕЛЬНО " +
            "   (Parallel.ForEachAsync з обмеженням): послідовний прогін у 10 хвилин не вкладеться;\n" +
            "5) входи читати ПАКЕТНО — один запит на методологію × період, не N запитів на рядок;\n" +
            "6) результати писати SqlBulkCopy через outputWriter; SaveChanges у циклі заборонений;\n" +
            "7) проміжні значення тримати в пам'яті воркера;\n" +
            "8) заповнити ModulesProfileJson — профіль по модулях: очікується, що топ-5 дають " +
            "   ~80% часу, і оптимізувати треба саме їх (питання J-1);\n" +
            "9) ⚠ ЗАКРИТІ ПЕРІОДИ автоматично не перераховувати НІКОЛИ (ФВ-9.7): це окрема " +
            "   операція з власним погодженням, інакше публікація методології заднім числом " +
            "   змінює подану звітність.");
}
