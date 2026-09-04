using Ecr.Domain.Enums;

namespace Ecr.Expressions;

/// <summary>
/// Календарний контекст періоду.
/// </summary>
/// <remarks>
/// Значення залежать від <see cref="CalendarMode"/> і це **не косметика**:
/// перерахунок у <c>г/с</c> ділить на <see cref="Seconds"/>, тому різниця між
/// 365 і 366 днями змінює **всі** числа звіту — і виглядає як помилка формули,
/// а не як різниця конвенції (D-78).
/// </remarks>
public sealed class PeriodContext
{
    /// <summary>Створює контекст.</summary>
    /// <param name="start">Перший день періоду.</param>
    /// <param name="end">Останній день періоду.</param>
    /// <param name="mode">Календарна конвенція версії методології.</param>
    /// <param name="year">Рік.</param>
    /// <param name="sequence">Порядковий номер періоду в році.</param>
    public PeriodContext(DateOnly start, DateOnly end, CalendarMode mode, int year, byte sequence)
    {
        Start = start;
        End = end;
        Mode = mode;
        Year = year;
        Sequence = sequence;
    }

    public DateOnly Start { get; }
    public DateOnly End { get; }
    public CalendarMode Mode { get; }
    public int Year { get; }
    public byte Sequence { get; }

    /// <summary>Днів у періоді згідно з <see cref="Mode"/>.</summary>
    public int Days => throw new NotImplementedException(
        "TODO: Actual → фактична кількість днів (End - Start + 1); " +
        "Fixed365 → місяць як фактичний, але рік завжди 365; " +
        "Fixed360 → місяць 30, рік 360. Перевіряється тестом на обидва режими (02c §7).");

    /// <summary>Годин у періоді.</summary>
    public int Hours => Days * 24;

    /// <summary>Секунд у періоді — дільник при перерахунку в <c>г/с</c>.</summary>
    public long Seconds => (long)Days * 86400;
}
