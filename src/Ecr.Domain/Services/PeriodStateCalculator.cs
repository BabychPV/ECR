using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Services;

/// <summary>
/// Обчислює стан періоду за offsets. Викликається <c>PeriodStateJob</c>,
/// а не запитом: стан періоду — збережене значення, а не функція від
/// <c>now()</c> (ФВ-1.12).
/// </summary>
public sealed class PeriodStateCalculator
{
    /// <summary>
    /// Визначає, яким має бути стан періоду на вказаний момент.
    /// </summary>
    /// <param name="period">Період із обчисленими межами.</param>
    /// <param name="utcNow">Поточний момент у UTC (з <c>IClock</c>).</param>
    /// <param name="siteTimeZone">Пояс майданчика: межі — саме в ньому (D-68).</param>
    public PeriodState Calculate(Period period, DateTime utcNow, TimeZoneInfo siteTimeZone)
        => throw new NotImplementedException(
            "TODO: якщо ReopenedUntil > utcNow → Grace; " +
            "utcNow < ComputedOpenAt → Scheduled; " +
            "utcNow <= кінець періоду → Open; " +
            "utcNow <= ComputedCloseAt → Grace; інакше Closed. " +
            "Порівняння в UTC, але межі вже обчислені з поясу майданчика — " +
            "повторно конвертувати не треба.");

    /// <summary>
    /// Обирає поточний період проєкту в режимі <c>Auto</c>: найраніший
    /// <c>Open</c>, інакше найпізніший <c>Grace</c>, інакше нічого (D-77).
    /// </summary>
    public Period? SelectCurrentPeriod(IReadOnlyList<Period> periods)
        => throw new NotImplementedException(
            "TODO: спершу шукати State == Open з мінімальним PeriodKeyValue; " +
            "якщо немає — State == Grace з максимальним; інакше null.");
}
