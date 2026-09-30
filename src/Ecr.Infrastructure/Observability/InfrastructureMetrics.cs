using System.Diagnostics.Metrics;

namespace Ecr.Infrastructure.Observability;

/// <summary>
/// Метрики <c>ФВ-12.7</c> / <c>НФ-8.6.2</c>, що емітуються з Infrastructure: провали задач і
/// влучність кешів. Meter <c>Ecr</c> — той самий, що в <c>EcrMetrics</c> (OpenTelemetry
/// підписується за назвою).
/// </summary>
/// <remarks>
/// ⚠ Статичний Meter, а не <c>IMeterFactory</c>: <c>Ecr.Worker</c> не має <c>EcrMetrics</c>,
/// а кеші й виконавці задач створюються й без DI (прямі <c>new</c> у тестах). Теги — лише
/// код задачі, причина й назва кешу: жодних ідентифікаторів користувачів чи документів.
/// </remarks>
public static class InfrastructureMetrics
{
    /// <summary>Назва Meter — та сама, що в <c>EcrMetrics.MeterName</c>.</summary>
    public const string MeterName = "Ecr";

    /// <summary>Лічильник остаточно провалених задач (без ретраїв).</summary>
    public const string JobFailed = "ecr.job.failed";

    /// <summary>Влучання в кеш.</summary>
    public const string CacheHit = "ecr.cache.hit";

    /// <summary>Промахи кешу.</summary>
    public const string CacheMiss = "ecr.cache.miss";

    /// <summary>Кеш метаданих шаблону — значення тегу <c>cache</c>.</summary>
    public const string MetadataCacheName = "metadata";

    /// <summary>Кеш профілю доступу — значення тегу <c>cache</c>.</summary>
    public const string AccessProfileCacheName = "access_profile";

    /// <summary>Тривалість реальної побудови профілю доступу (не з кешу), секунди.</summary>
    public const string AccessProfileBuild = "ecr.access.profile.build";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> ProfileBuild =
        Meter.CreateHistogram<double>(AccessProfileBuild, "s", "Побудова AccessProfile");
    private static readonly Counter<long> Failed =
        Meter.CreateCounter<long>(JobFailed, "{job}", "Остаточно провалені фонові задачі");
    private static readonly Counter<long> Hits =
        Meter.CreateCounter<long>(CacheHit, "{lookup}", "Влучання в кеш");
    private static readonly Counter<long> Misses =
        Meter.CreateCounter<long>(CacheMiss, "{lookup}", "Промахи кешу");

    /// <summary>Фіксує остаточно провалену задачу; <paramref name="reason"/> — <c>error</c>, <c>overtime</c>, <c>deferral_exhausted</c>.</summary>
    public static void RecordJobFailed(string? jobCode, string reason)
        => Failed.Add(
            1,
            new KeyValuePair<string, object?>("job", jobCode ?? "—"),
            new KeyValuePair<string, object?>("reason", reason));

    /// <summary>Фіксує тривалість побудови профілю доступу.</summary>
    public static void RecordAccessProfileBuild(double seconds) => ProfileBuild.Record(seconds);

    /// <summary>Фіксує влучання (<paramref name="hit"/>) чи промах кешу <paramref name="cache"/>.</summary>
    public static void RecordCache(string cache, bool hit)
    {
        var tag = new KeyValuePair<string, object?>("cache", cache);
        if (hit)
        {
            Hits.Add(1, tag);
        }
        else
        {
            Misses.Add(1, tag);
        }
    }
}
