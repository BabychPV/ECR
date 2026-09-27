// src/Ecr.Infrastructure/Integration/PeriodUtcRange.cs
using Ecr.Domain.ValueObjects;

namespace Ecr.Infrastructure.Integration;

/// <summary>
/// Межі звітного періоду в UTC: <c>[опівніч PeriodStart, опівніч PeriodEnd + 1)</c>
/// у поясі проєкту (<c>D-68</c>, D16-03).
/// </summary>
/// <param name="StartUtc">Початок періоду, включно.</param>
/// <param name="EndUtc">Кінець періоду, виключно.</param>
/// <remarks>
/// ⛔ Перетворення «дата → UTC» — те саме, що в <c>Period.ToUtc</c>
/// (<c>Period.cs</c>, опівніч дати в поясі майданчика), а не власна
/// арифметика. Власний метод тут лише тому, що <c>Period.ToUtc</c> приватний;
/// розбіжність із ним тримає сторож <c>PeriodUtcRangeTests</c>, який порівнює
/// результат із <c>ComputedOpenAt</c>/<c>ComputedGraceAt</c> самого періоду.
///
/// ⚠ Межі в UTC, а не в поясі сервера й не в UTC-датах: точка о 23:30 31 січня
/// місцевого часу належить січню, хоча в UTC це вже може бути інша доба.
/// </remarks>
public readonly record struct PeriodUtcRange(DateTime StartUtc, DateTime EndUtc)
{
    /// <summary>Межі періоду за його датами і поясом проєкту.</summary>
    /// <param name="periodStart">Перший день періоду (<c>Period.PeriodStart</c>).</param>
    /// <param name="periodEnd">Останній день періоду, включно (<c>Period.PeriodEnd</c>).</param>
    /// <param name="timeZoneId">Пояс проєкту (<c>Project.TimeZoneId</c>, IANA).</param>
    public static PeriodUtcRange Of(DateOnly periodStart, DateOnly periodEnd, string timeZoneId)
    {
        var zone = SiteTimeZone.Create(timeZoneId).ToTimeZoneInfo();

        return new PeriodUtcRange(Midnight(periodStart, zone), Midnight(periodEnd.AddDays(1), zone));
    }

    /// <summary>Чи перетинає період напіввідкритий інтервал <c>[fromUtc, toUtc)</c>.</summary>
    public bool Overlaps(DateTime fromUtc, DateTime toUtc) => StartUtc < toUtc && fromUtc < EndUtc;

    /// <summary>Опівніч дати в поясі проєкту, переведена в UTC.</summary>
    private static DateTime Midnight(DateOnly date, TimeZoneInfo zone)
        => TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified),
            zone);
}
