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

    /// <summary>
    /// Тривалість одного виконання фонової задачі воркером, секунди (теги <c>job</c>, <c>outcome</c> = <c>ok</c>/<c>error</c>).
    /// ⚠ Не <c>ecr.job.duration</c> (той — лише чотири HTTP-дії в Api): тут кожна спроба задачі з черги.
    /// </summary>
    public const string JobRunDuration = "ecr.job.run.duration";

    /// <summary>
    /// Глибина черги задач у базі (B5.10): gauge, теги <c>lane</c> і <c>state</c>
    /// (<c>Queued</c>/<c>Running</c>) — обидва з фіксованих переліків. Значення — КЕШ,
    /// який оновлює <c>JobQueueDepthSampler</c>; читання gauge в базу не ходить.
    /// </summary>
    public const string JobsQueueDepth = "ecr.jobs.queue_depth";

    private static readonly Meter Meter = new(MeterName);
    private static volatile IReadOnlyList<QueueDepthPoint> queueDepth = [];
    private static readonly ObservableGauge<long> QueueDepthGauge =
        Meter.CreateObservableGauge(JobsQueueDepth, ObserveQueueDepth, "{job}", "Задач у черзі в базі за лейном і станом");
    private static readonly Histogram<double> RunDuration =
        Meter.CreateHistogram<double>(JobRunDuration, "s", "Тривалість виконання задачі з черги");
    private static readonly Histogram<double> ProfileBuild =
        Meter.CreateHistogram<double>(AccessProfileBuild, "s", "Побудова AccessProfile");
    private static readonly Counter<long> Failed =
        Meter.CreateCounter<long>(JobFailed, "{job}", "Остаточно провалені фонові задачі");
    private static readonly Counter<long> Hits =
        Meter.CreateCounter<long>(CacheHit, "{lookup}", "Влучання в кеш");
    private static readonly Counter<long> Misses =
        Meter.CreateCounter<long>(CacheMiss, "{lookup}", "Промахи кешу");

    /// <summary>Кладе свіжий знімок глибини черги в кеш gauge <see cref="JobsQueueDepth"/>.</summary>
    public static void PublishQueueDepth(IReadOnlyList<QueueDepthPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        queueDepth = points.ToArray();
        _ = QueueDepthGauge;
    }

    private static IEnumerable<Measurement<long>> ObserveQueueDepth()
        => queueDepth.Select(p => new Measurement<long>(
            p.Count,
            new KeyValuePair<string, object?>("lane", p.Lane),
            new KeyValuePair<string, object?>("state", p.State)));

    /// <summary>Фіксує остаточно провалену задачу; <paramref name="reason"/> — <c>error</c>, <c>overtime</c>, <c>deferral_exhausted</c>.</summary>
    public static void RecordJobFailed(string? jobCode, string reason)
        => Failed.Add(
            1,
            new KeyValuePair<string, object?>("job", jobCode ?? "—"),
            new KeyValuePair<string, object?>("reason", reason));

    /// <summary>Фіксує тривалість спроби задачі; <paramref name="outcome"/> — <c>ok</c> або <c>error</c>.</summary>
    public static void RecordJobRun(string? jobCode, string outcome, double seconds)
        => RunDuration.Record(
            seconds,
            new KeyValuePair<string, object?>("job", jobCode ?? "—"),
            new KeyValuePair<string, object?>("outcome", outcome));

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
