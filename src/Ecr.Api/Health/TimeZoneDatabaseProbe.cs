using System.Globalization;

namespace Ecr.Api.Health;

/// <summary>
/// Джерело зсувів UTC для перевірки бази часових поясів (F-4).
/// </summary>
/// <remarks>
/// ⚠ Окремий інтерфейс, а не прямий виклик <see cref="TimeZoneInfo"/>, лише щоб
/// тест міг підмінити «застарілу базу»: справжню базу машини тест не змінить, а
/// перевірка, яку неможливо змусити спрацювати, нічого не доводить.
/// </remarks>
public interface ITimeZoneOffsetProvider
{
    /// <summary>Зсув пояса IANA відносно UTC у заданий момент.</summary>
    /// <param name="ianaId">Ідентифікатор IANA, наприклад <c>Asia/Almaty</c>.</param>
    /// <param name="utc">Момент у UTC.</param>
    /// <returns>Зсув або <c>null</c>, якщо пояс цій системі невідомий.</returns>
    public TimeSpan? GetUtcOffset(string ianaId, DateTime utc);
}

/// <summary>Зсуви з бази часових поясів ОС — тієї самої, що рахує межі періодів.</summary>
public sealed class SystemTimeZoneOffsetProvider : ITimeZoneOffsetProvider
{
    /// <inheritdoc />
    public TimeSpan? GetUtcOffset(string ianaId, DateTime utc)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId).GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }
}

/// <summary>Пояс, чий зсув база ОС повідомила не так, як очікувалося.</summary>
/// <param name="ZoneId">Ідентифікатор IANA.</param>
/// <param name="Actual">Зсув від бази ОС; <c>null</c> — пояса в базі немає.</param>
public readonly record struct TimeZoneDrift(string ZoneId, TimeSpan? Actual)
{
    /// <summary>Коротке подання для логу й тексту картки: <c>Asia/Almaty=+06:00</c>.</summary>
    /// <returns>Пояс і фактичний зсув; <c>?</c>, якщо пояса немає.</returns>
    public string Describe()
        => $"{ZoneId}={(Actual is { } offset ? KazakhstanTimeZoneReference.FormatOffset(offset) : "?")}";
}

/// <summary>
/// Еталон для перевірки бази поясів: з 2024-03-01 увесь Казахстан на UTC+5 (F-4).
/// </summary>
/// <remarks>
/// ⛔ Межі періодів рахуються через <c>TimeZoneInfo.FindSystemTimeZoneById(IANA)</c>,
/// тобто беруться з бази поясів ОС (Windows — реєстр і накопичувальні оновлення,
/// Linux — <c>tzdata</c>); фіксованого зсуву в продукті немає. Машина, що не знає
/// про перехід, вважає <c>Asia/Almaty</c> <c>+06:00</c>, і кожен період проєкту на
/// цьому поясі закривається на годину пізніше, ніж на сервері з чинною базою.
///
/// ⚠ Еталон записаний КОНСТАНТОЮ, а не порахований із тієї ж бази: інакше
/// перевірка порівнювала б базу саму з собою. Майданчик NCOC — Атирау
/// (<c>Asia/Atyrau</c>); <c>Asia/Aqtau</c> — приклад у коді й API, зсув той самий;
/// <c>Asia/Almaty</c> — єдиний із трьох, чий зсув справді змінився, тож саме він
/// виказує застарілу базу.
/// </remarks>
public static class KazakhstanTimeZoneReference
{
    /// <summary>Момент після переходу (2024-03-01), у який звіряється зсув.</summary>
    public static readonly DateTime CheckedAtUtc = new(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Очікуваний зсув усіх перелічених поясів після переходу.</summary>
    public static readonly TimeSpan ExpectedOffset = TimeSpan.FromHours(5);

    /// <summary>Пояси, зсув яких перевіряється.</summary>
    public static IReadOnlyList<string> Zones { get; } = ["Asia/Atyrau", "Asia/Aqtau", "Asia/Almaty"];

    /// <summary>Пояси, чий зсув у базі провайдера не дорівнює очікуваному (або яких там немає).</summary>
    /// <param name="provider">Джерело зсувів.</param>
    /// <returns>Порожній перелік, якщо база знає перехід.</returns>
    public static IReadOnlyList<TimeZoneDrift> FindDrift(ITimeZoneOffsetProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var drift = new List<TimeZoneDrift>();

        foreach (var zone in Zones)
        {
            var actual = provider.GetUtcOffset(zone, CheckedAtUtc);

            if (actual != ExpectedOffset)
            {
                drift.Add(new TimeZoneDrift(zone, actual));
            }
        }

        return drift;
    }

    /// <summary>Зсув у вигляді <c>+05:00</c> / <c>-03:30</c>.</summary>
    /// <param name="offset">Зсув відносно UTC.</param>
    /// <returns>Рядок зі знаком.</returns>
    public static string FormatOffset(TimeSpan offset)
        => (offset < TimeSpan.Zero ? "-" : "+")
           + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
}
