using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Api.Startup;

/// <summary>
/// Наздоганяє нічні задачі обслуговування, пропущені через те, що застосунок не працював о 02:15.
/// </summary>
/// <remarks>
/// ⛔ Аудит live 12.11: у журналі <c>itg.MaintenanceRun</c> немає жодної нічної задачі за 15, 17, 20, 23, 25, 31.10,
/// 01.11 і 12.11 — застосунок не працював о 02:15 (живі вікна, перезапуски), а cron не має пам'яті: пропущений тик
/// ніхто не повторює. Перевірка узгодженості й прибирання неактуальних прогонів мовчки пропускали ніч, і ніхто не
/// дізнавався. Тепер на старті кожна нічна задача, остання відмітка якої в журналі старша за
/// <see cref="MaxAge"/>, ставиться в чергу одразу.
/// <para>
/// ⚠ Задача, якої в журналі немає ЗОВСІМ, не наздоганяється: це свіжа база (розгортання, стенд smoke/e2e), де
/// наздоганяти нічого — перший прохід зробить нічний cron. Наздоганяється лише пропуск після хоч одного проходу.
/// </para>
/// <para>
/// ⚠ Постановка — <see cref="IBackgroundJobScheduler.EnqueueCoalescedAsync{TJob}"/> з ключем на задачу: два інстанси,
/// що стартують поруч, не ставлять одну задачу двічі. Прибирання — ПЕРШИМ у переліку, перевірка узгодженості —
/// останньою: черга може виконати їх і паралельно, але межа <c>NON_CURRENT_RUN_ROWS</c> (старт останнього
/// успішного прибирання) не дає перевірці лічити рядки, які прибирання саме видаляє.
/// </para>
/// <para>
/// ⚠ Вимикається ключем <see cref="EnabledKey"/> (за замовчуванням увімкнено). Вимикає його лише тестовий хост
/// (<c>EcrApiFactory</c>): там журнал спільної бази колекції заповнюють інші тести старими годинниками, і наздоганяння
/// запускало б справжні нічні задачі на кожному старті хоста.
/// </para>
/// </remarks>
public static class NightlyCatchUp
{
    /// <summary>Ключ конфігурації: <c>false</c> вимикає наздоганяння.</summary>
    public const string EnabledKey = "Jobs:NightlyCatchUp:Enabled";

    /// <summary>Вік останньої відмітки, після якого задача вважається пропущеною.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>Нічні задачі, що пишуть <c>itg.MaintenanceRun</c>, у порядку постановки.</summary>
    /// <remarks>
    /// ⚠ <c>OrphanScanJob</c> сюди не входить: журналу обслуговування він не пише, а його роботу (перерахунок
    /// <c>IsOrphaned</c>) робить і сама перевірка узгодженості (<c>IOrphanScanner.ScanAllAsync</c>).
    /// </remarks>
    public static IReadOnlyList<string> Codes { get; } =
    [
        Infrastructure.Jobs.SupersededResultsSweepJob.Code,
        Infrastructure.Jobs.PartitionCheckJob.Code,
        Infrastructure.Jobs.ReportRetentionJob.Code,
        Infrastructure.Jobs.ReportSnapshotFormatJob.Code,
        Infrastructure.Jobs.SubmissionSnapshotRetentionJob.Code,
        Infrastructure.Jobs.ConsistencyCheckJob.Code,
    ];

    /// <summary>Чи увімкнено наздоганяння.</summary>
    /// <param name="configuration">Конфігурація застосунку.</param>
    public static bool IsEnabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return !bool.TryParse(configuration[EnabledKey], out var enabled) || enabled;
    }

    /// <summary>Остання відмітка старту кожної нічної задачі в журналі.</summary>
    /// <param name="db">Контекст бази.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<IReadOnlyDictionary<string, DateTime>> LastRunsAsync(EcrDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var codes = Codes.ToList();

        return await db.MaintenanceRuns
            .AsNoTracking()
            .Where(r => codes.Contains(r.JobCode))
            .GroupBy(r => r.JobCode)
            .Select(g => new { Code = g.Key, Last = g.Max(r => r.StartedAt) })
            .ToDictionaryAsync(x => x.Code, x => x.Last, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Ставить у чергу пропущені задачі; повертає їхні коди.</summary>
    /// <exception cref="InvalidOperationException">
    /// Постановка хоч однієї задачі не вдалася — після спроби поставити ВСІ інші; повідомлення називає невдалі й
    /// поставлені.
    /// </exception>
    /// <param name="lastRuns">Остання відмітка старту за кодом задачі (<see cref="LastRunsAsync"/>).</param>
    /// <param name="scheduler">Планувальник.</param>
    /// <param name="utcNow">Тепер.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<IReadOnlyList<string>> EnqueueMissedAsync(
        IReadOnlyDictionary<string, DateTime> lastRuns,
        IBackgroundJobScheduler scheduler,
        DateTime utcNow,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lastRuns);
        ArgumentNullException.ThrowIfNull(scheduler);

        var missed = Codes
            .Where(code => lastRuns.TryGetValue(code, out var last) && utcNow - last > MaxAge)
            .ToList();

        // ⛔ Live 13.11 (приймання): збій постановки ОДНІЄЇ задачі обривав foreach, і решта — перевірка узгодженості
        // стоїть останньою — не наздоганялась ніколи. Тепер кожна ставиться окремо; невдалі збираються й після
        // проходу всього переліку повертаються одним винятком (його пише в журнал RecurringScheduleService).
        // Скасування не ковтається.
        List<string>? failures = null;
        var enqueued = new List<string>(missed.Count);
        foreach (var code in missed)
        {
            try
            {
                await EnqueueAsync(code, scheduler, ct).ConfigureAwait(false);
                enqueued.Add(code);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                (failures ??= []).Add($"{code}: {ex.Message}");
            }
        }

        if (failures is not null)
        {
            throw new InvalidOperationException(
                $"Не поставлено: {string.Join("; ", failures)}. "
                + $"Поставлено: {(enqueued.Count > 0 ? string.Join(", ", enqueued) : "нічого")}.");
        }

        return missed;
    }

    /// <summary>Ціль постановки наздоганяння (<see cref="IBackgroundJobScheduler.EnqueueCoalescedAsync{TJob}"/>).</summary>
    /// <remarks>
    /// ⛔ Live 13.11 (приймання): ціль була <c>nightly-catch-up:{код}</c>, і ідентифікатор задачі Quartz
    /// (<c>{Тип}~{ціль}~{Guid N}</c>) для <c>SubmissionSnapshotRetentionJob</c> виходив 110 символів при
    /// <c>itg.JobProgress.JobId nvarchar(100)</c> — SqlException. Код задачі в цілі зайвий: тип задачі вже стоїть
    /// у префіксі ідентифікатора, і коалесценція йде за парою «тип + ціль». Найдовший тип переліку (30) дає 80.
    /// </remarks>
    private const string TargetKey = "nightly-catch-up";

    private static Task<string> EnqueueAsync(string code, IBackgroundJobScheduler scheduler, CancellationToken ct)
    {
        const string key = TargetKey;

        return code switch
        {
            _ when code == Infrastructure.Jobs.SupersededResultsSweepJob.Code
                => scheduler.EnqueueCoalescedAsync<Infrastructure.Jobs.SupersededResultsSweepJob>(key, null, ct),
            _ when code == Infrastructure.Jobs.PartitionCheckJob.Code
                => scheduler.EnqueueCoalescedAsync<Infrastructure.Jobs.PartitionCheckJob>(key, null, ct),
            _ when code == Infrastructure.Jobs.ReportRetentionJob.Code
                => scheduler.EnqueueCoalescedAsync<Infrastructure.Jobs.ReportRetentionJob>(key, null, ct),
            _ when code == Infrastructure.Jobs.ReportSnapshotFormatJob.Code
                => scheduler.EnqueueCoalescedAsync<Infrastructure.Jobs.ReportSnapshotFormatJob>(key, null, ct),
            _ when code == Infrastructure.Jobs.SubmissionSnapshotRetentionJob.Code
                => scheduler.EnqueueCoalescedAsync<Infrastructure.Jobs.SubmissionSnapshotRetentionJob>(key, null, ct),
            _ when code == Infrastructure.Jobs.ConsistencyCheckJob.Code
                => scheduler.EnqueueCoalescedAsync<Infrastructure.Jobs.ConsistencyCheckJob>(key, null, ct),
            _ => throw new InvalidOperationException($"Нічна задача без постановки наздоганяння: {code}."),
        };
    }
}
