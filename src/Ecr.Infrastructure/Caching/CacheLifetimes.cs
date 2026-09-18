using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Ecr.Infrastructure.Caching;

/// <summary>
/// Стелі життя записів у кешах, узяті з конфігурації (<c>Cache:*</c>).
/// </summary>
/// <remarks>
/// ⛔ До `D14-06` обидва ключі (<c>Cache:MetadataSlidingMinutes</c>,
/// <c>Cache:AccessProfileSlidingMinutes</c>) лежали в <c>appsettings.json</c>
/// без ЖОДНОГО читача, а в коді стояло жорстке <c>TimeSpan.FromMinutes(30)</c>
/// (`S-13`). Адміністратор, який виставив би 240 хвилин, отримав би 30 — і
/// ніде б цього не побачив. Це справжня ручка, тому ключі підключено, а не
/// видалено.
///
/// ⚠ Імена ключів кажуть «Sliding», а механізм — <c>AbsoluteExpiration</c>
/// (тобто стеля від запису, не від останнього звернення). Це не описка
/// реалізації: обидва кеші інвалідуються КЛЮЧЕМ (ревізія шаблону, штамп
/// безпеки), а строк потрібен лише проти вічного зростання пам'яті — ковзний
/// строк тримав би гарячий запис у пам'яті назавжди, тобто саме те, від чого
/// стеля й лікує. Перейменування ключа зачіпає
/// <c>tools/deploy-ecr.ps1</c> і <c>11-install-guide.md</c>, тому лишено
/// окремим кроком; семантика тут — та, що була.
///
/// ⚠ Нуль і від'ємне трактуються як «не задано» і дають дефолт: кеш без
/// строку — це витік пам'яті, і мовчки його вмикати не можна.
/// </remarks>
public sealed class CacheLifetimes
{
    /// <summary>Значення за замовчуванням — ті самі 30 хвилин, що були жорстко в коді.</summary>
    public static CacheLifetimes Default { get; } = new(
        TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));

    /// <summary>Створює набір строків.</summary>
    /// <param name="metadata">Стеля для знімка метаданих шаблону.</param>
    /// <param name="accessProfile">Стеля для профілю доступу.</param>
    public CacheLifetimes(TimeSpan metadata, TimeSpan accessProfile)
    {
        Metadata = metadata;
        AccessProfile = accessProfile;
    }

    /// <summary>Стеля життя знімка метаданих (<c>Cache:MetadataSlidingMinutes</c>).</summary>
    public TimeSpan Metadata { get; }

    /// <summary>Стеля життя профілю доступу (<c>Cache:AccessProfileSlidingMinutes</c>).</summary>
    public TimeSpan AccessProfile { get; }

    /// <summary>Читає строки з конфігурації.</summary>
    /// <param name="configuration">Конфігурація застосунку.</param>
    /// <remarks>
    /// Розбір ручний, а не <c>GetValue&lt;int&gt;</c>, з тієї самої причини, що
    /// й у <c>DependencyInjection.ReadInt</c>: <c>Ecr.Infrastructure</c>
    /// навмисно тримається самих <c>Configuration.Abstractions</c>.
    /// </remarks>
    public static CacheLifetimes FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new CacheLifetimes(
            Minutes(configuration, "Cache:MetadataSlidingMinutes", Default.Metadata),
            Minutes(configuration, "Cache:AccessProfileSlidingMinutes", Default.AccessProfile));
    }

    private static TimeSpan Minutes(IConfiguration configuration, string key, TimeSpan fallback)
        => int.TryParse(configuration[key], CultureInfo.InvariantCulture, out var minutes) && minutes > 0
            ? TimeSpan.FromMinutes(minutes)
            : fallback;
}
