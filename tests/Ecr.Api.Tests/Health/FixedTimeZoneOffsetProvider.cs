using Ecr.Api.Health;

namespace Ecr.Api.Tests.Health;

/// <summary>
/// Підмінена база часових поясів: зсув задає тест, а не машина (F-4).
/// </summary>
/// <remarks>
/// ⚠ Справжню базу ОС тест не змінить, а перевірка, яку неможливо змусити
/// спрацювати, нічого не доводить. Той самий клас тримає стенд <see cref="EcrApiFactory"/>
/// «справною», щоб загальні тести готовності не залежали від того, чи оновлено
/// tzdata/Windows на машині розробника: це питання ловить окремий тест
/// зсувів (<c>KazakhstanSiteZoneOffsetTests</c>), а не всі підряд.
/// </remarks>
/// <param name="offsetOf">Зсув за ідентифікатором поясу; <c>null</c> — пояса в базі немає.</param>
public sealed class FixedTimeZoneOffsetProvider(Func<string, TimeSpan?> offsetOf) : ITimeZoneOffsetProvider
{
    /// <summary>База, що знає перехід: усі пояси Казахстану на +05:00.</summary>
    public static FixedTimeZoneOffsetProvider Current { get; }
        = new(_ => KazakhstanTimeZoneReference.ExpectedOffset);

    /// <summary>Застаріла база: <c>Asia/Almaty</c> ще на +06:00, решта вже на +05:00.</summary>
    public static FixedTimeZoneOffsetProvider StaleAlmaty { get; }
        = new(zone => string.Equals(zone, "Asia/Almaty", StringComparison.Ordinal)
            ? TimeSpan.FromHours(6)
            : KazakhstanTimeZoneReference.ExpectedOffset);

    /// <inheritdoc />
    public TimeSpan? GetUtcOffset(string ianaId, DateTime utc) => offsetOf(ianaId);
}
