using Ecr.Domain.Enums;
using Ecr.Expressions;

namespace Ecr.Calculations;

/// <summary>
/// Будує календарний контекст періоду згідно з <see cref="CalendarMode"/>
/// версії методології.
/// </summary>
/// <remarks>
/// Це не косметика. Перерахунок у <c>г/с</c> ділить на кількість секунд у
/// періоді: для січня 2026 в режимі <c>Actual</c> це 2 678 400 с, у
/// <c>Fixed360</c> — 2 592 000 с. Різниця на тих самих даних — **3.3 %**, і
/// виглядає вона як помилка формули, а не як різниця конвенції (D-78,
/// перевіряється фікстурою 02c §7).
/// </remarks>
public sealed class CalendarContext
{
    /// <summary>Створює контекст для періоду.</summary>
    public PeriodContext Build(DateOnly start, DateOnly end, CalendarMode mode, int year, byte sequence)
        => new(start, end, mode, year, sequence);
}
