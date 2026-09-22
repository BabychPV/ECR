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
    /// <summary>
    /// Вікно мемоїзації ревізії за замовчуванням.
    /// </summary>
    /// <remarks>
    /// ⛔ Рівно ті самі п'ять секунд і з тієї самої причини, що в
    /// <c>SecurityStampValidator</c> (`RD-05`): без цього вікна кожен
    /// <c>IMetadataCache.GetAsync</c> ходить до <c>cfg.TemplateVersion</c> по
    /// одне число, а на один зріз таких викликів ≥ 2.
    ///
    /// ⚠ Ціна названа прямо: стеля несвіжості ПРЕЗЕНТАЦІЙНОЇ правки стає 5 с
    /// замість миттєвої. Це свідомий компроміс директиви №14 (`RD-05`), а не
    /// побічний ефект. <c>Publish</c> і міграція від цього не страждають:
    /// <c>MetadataCache.InvalidateAsync</c> знімає мемоїзоване число разом зі
    /// знімками, тобто діє негайно.
    /// </remarks>
    public static readonly TimeSpan DefaultRevision = TimeSpan.FromSeconds(5);

    /// <summary>Стеля метаданих за замовчуванням, хв — те саме число, що в <c>appsettings.json</c>.</summary>
    /// <remarks>
    /// ⚠ До вирівнювання тут стояло 30 проти 240 у файлі; одне джерело правди
    /// стереже <c>ConfigurationKeysTests</c>.
    /// </remarks>
    public const int DefaultMetadataMinutes = 240;

    /// <summary>Стеля профілю доступу за замовчуванням, хв (у файлі — 60).</summary>
    public const int DefaultAccessProfileMinutes = 60;

    /// <summary>Значення за замовчуванням — ті самі, що в <c>appsettings.json</c>.</summary>
    public static CacheLifetimes Default { get; } = new(
        TimeSpan.FromMinutes(DefaultMetadataMinutes), TimeSpan.FromMinutes(DefaultAccessProfileMinutes));

    /// <summary>Створює набір строків.</summary>
    /// <param name="metadata">Стеля для знімка метаданих шаблону.</param>
    /// <param name="accessProfile">Стеля для профілю доступу.</param>
    /// <param name="revision">
    /// Вікно мемоїзації ревізії; <c>null</c> — <see cref="DefaultRevision"/>,
    /// <see cref="TimeSpan.Zero"/> — мемоїзація вимкнена (ревізія читається на
    /// кожен виклик, як було до `RD-05`).
    /// </param>
    public CacheLifetimes(TimeSpan metadata, TimeSpan accessProfile, TimeSpan? revision = null)
    {
        Metadata = metadata;
        AccessProfile = accessProfile;
        Revision = revision ?? DefaultRevision;
    }

    /// <summary>Стеля життя знімка метаданих (<c>Cache:MetadataSlidingMinutes</c>).</summary>
    public TimeSpan Metadata { get; }

    /// <summary>Стеля життя профілю доступу (<c>Cache:AccessProfileSlidingMinutes</c>).</summary>
    public TimeSpan AccessProfile { get; }

    /// <summary>
    /// Вікно, протягом якого ревізія версії шаблону не перечитується з бази.
    /// </summary>
    /// <remarks>
    /// ⚠ Ручки в <c>appsettings.json</c> тут навмисно НЕМАЄ, і це не недогляд.
    /// Сторож <c>ConfigurationKeysTests</c> вимагає двобічної згоди файла й
    /// коду, а файл лежить поза межами цього PR (`RD-05` пише лише в
    /// <c>Caching/**</c> і <c>DependencyInjection.cs</c>). Ключ
    /// <c>Cache:RevisionSeconds</c> — окремий крок; до нього значення
    /// задається конструктором, чим і користуються тести.
    /// </remarks>
    public TimeSpan Revision { get; }

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
            Minutes(configuration, "Cache:MetadataSlidingMinutes", DefaultMetadataMinutes),
            Minutes(configuration, "Cache:AccessProfileSlidingMinutes", DefaultAccessProfileMinutes));
    }

    private static TimeSpan Minutes(IConfiguration configuration, string key, int fallbackMinutes)
        => TimeSpan.FromMinutes(
            int.TryParse(configuration[key], CultureInfo.InvariantCulture, out var minutes) && minutes > 0
                ? minutes
                : fallbackMinutes);
}
