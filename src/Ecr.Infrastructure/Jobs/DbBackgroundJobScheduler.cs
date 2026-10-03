// src/Ecr.Infrastructure/Jobs/DbBackgroundJobScheduler.cs
using System.Text.Json;
using System.Threading.Channels;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Errors;
using Microsoft.Extensions.Configuration;

namespace Ecr.Infrastructure.Jobs;

/// <summary>Хто виконує разові задачі (<c>Jobs:Queue:Mode</c>, MI-02).</summary>
public enum JobQueueMode
{
    /// <summary>Quartz у пам'яті процесу — як досі (<see cref="QuartzJobScheduler"/>).</summary>
    Quartz,

    /// <summary>Черга в базі (<see cref="DbBackgroundJobScheduler"/> + <c>JobWorker</c>).</summary>
    Database,
}

/// <summary>
/// <see cref="IBackgroundJobScheduler"/> на черзі в базі (<see cref="IJobQueue"/>, MI-02, <c>D14-01</c>).
/// </summary>
/// <remarks>
/// ⚠ Разові задачі — у черзі; розклад (cron) лишається в Quartz
/// (<paramref name="cron"/>): його тик сам ставить задачу, і переносити крон у
/// базу — окремий крок (P4/I1). Стан, автор і перелік — з <c>itg.JobProgress</c>,
/// тим самим шляхом, що й у Quartz: обидва світи живуть в одній таблиці.
/// <para>
/// ⛔ Постановка — у ПОТОЧНІЙ транзакції <c>EcrDbContext</c> scope (контракт
/// <see cref="IJobQueue.EnqueueAsync"/>): відкат бізнес-зміни відкочує й задачу.
/// </para>
/// </remarks>
/// <param name="queue">Черга scope.</param>
/// <param name="cron">Quartz — для розкладу і для задач, поставлених до перемикання.</param>
/// <param name="signal">Підказка локальному воркеру «є робота».</param>
/// <param name="correlation">Кореляція запиту-постановника.</param>
public sealed class DbBackgroundJobScheduler(
    IJobQueue queue,
    QuartzJobScheduler cron,
    JobQueueSignal signal,
    ICorrelationIdAccessor? correlation = null) : IBackgroundJobScheduler
{
    /// <summary>Ключ конфігурації режиму.</summary>
    public const string ModeKey = "Jobs:Queue:Mode";

    /// <summary>Роздільник типу й цілі в <c>TargetKey</c> — той самий, що в ідентифікаторі Quartz.</summary>
    private const char TargetSeparator = '~';

    /// <summary>Серіалізація payload — та сама, що в <see cref="QuartzJobScheduler"/> (Web-налаштування).</summary>
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    /// <remarks>⚠ Постановка — сирий SQL у поточній транзакції <c>EcrDbContext</c> scope.</remarks>
    public bool EnlistsInCallerTransaction => true;

    /// <summary>Режим із конфігурації; не задано — <see cref="JobQueueMode.Quartz"/>.</summary>
    public static JobQueueMode ReadMode(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return Enum.TryParse<JobQueueMode>(configuration[ModeKey], ignoreCase: true, out var mode)
            ? mode
            : JobQueueMode.Quartz;
    }

    /// <inheritdoc />
    public Task<string> EnqueueAsync<TJob>(object? payload, CancellationToken ct, int? createdByUserId = null)
        where TJob : IBackgroundJob
        => EnqueueCoreAsync<TJob>(payload, targetKey: null, supersede: false, createdByUserId, ct);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Витіснення — <see cref="JobEnqueueRequest.SupersedeRunning"/>: <c>Running</c>
    /// на ту саму ціль отримує запит скасування, а нова задача стає позаду неї
    /// (claim не бере <c>Queued</c>, доки на ціль є <c>Running</c>). Наявна
    /// <c>Queued</c> на ціль поглинає постановку — повертається її <c>JobId</c>.
    /// </remarks>
    public Task<string> EnqueueExclusiveAsync<TJob>(
        string targetKey, object? payload, CancellationToken ct, int? createdByUserId = null)
        where TJob : IBackgroundJob
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);

        return EnqueueCoreAsync<TJob>(payload, TargetKeyOf<TJob>(targetKey), supersede: true, createdByUserId, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Той самий <c>TargetKey</c>, що й у <see cref="EnqueueExclusiveAsync{TJob}"/>, але
    /// <see cref="JobEnqueueRequest.SupersedeRunning"/> = <c>false</c>: <c>Running</c> на
    /// ціль не отримує запиту скасування, нова задача стає <c>Queued</c> позаду неї, а
    /// наявна <c>Queued</c> поглинає постановку (повертається її <c>JobId</c>).
    /// </remarks>
    public Task<string> EnqueueCoalescedAsync<TJob>(
        string targetKey, object? payload, CancellationToken ct, int? createdByUserId = null)
        where TJob : IBackgroundJob
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);

        return EnqueueCoreAsync<TJob>(payload, TargetKeyOf<TJob>(targetKey), supersede: false, createdByUserId, ct);
    }

    /// <summary><c>{TypeName}~{target}</c> — одне визначення на обидві постановки з ціллю.</summary>
    private static string TargetKeyOf<TJob>(string targetKey)
        => $"{typeof(TJob).Name}{TargetSeparator}{targetKey.Replace(TargetSeparator, '_')}";

    private async Task<string> EnqueueCoreAsync<TJob>(
        object? payload, string? targetKey, bool supersede, int? createdByUserId, CancellationToken ct)
        where TJob : IBackgroundJob
    {
        var json = JsonSerializer.Serialize(payload, PayloadOptions);

        // JobId = {TypeName}-{guid:N} (DbJobQueue.ShortCode від повного імені) — та сама
        // форма, що в Quartz: клієнт і журнал розпізнають задачу за префіксом.
        var result = await queue.EnqueueAsync(
                new JobEnqueueRequest(
                    typeof(TJob).FullName ?? typeof(TJob).Name,
                    JobLaneMap.Of<TJob>(),
                    json,
                    targetKey,
                    createdByUserId,
                    correlation?.CorrelationId is { Length: > 0 } id ? id : Guid.NewGuid().ToString("N"),
                    DocumentIdOf(json),
                    SupersedeRunning: supersede),
                ct)
            .ConfigureAwait(false);

        // ⚠ Лише підказка: у відкритій транзакції рядок ще не видно, і воркер
        // просто візьме його на наступному опитуванні (раз на секунду).
        signal.Notify();

        return result.JobId;
    }

    /// <inheritdoc />
    public Task ScheduleAsync<TJob>(string cronExpression, object? payload, CancellationToken ct)
        where TJob : IBackgroundJob
        => cron.ScheduleAsync<TJob>(cronExpression, payload, ct);

    /// <inheritdoc />
    public Task<bool> UnscheduleAsync<TJob>(object? payload, CancellationToken ct)
        where TJob : IBackgroundJob
        => cron.UnscheduleAsync<TJob>(payload, ct);

    /// <inheritdoc />
    public bool IsValidCron(string expression, out string? error)
        => QuartzJobScheduler.IsValidCron(expression, out error);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Рядка черги немає (<see cref="CancelOutcome.NotFound"/>) — це задача Quartz
    /// (розклад або постановка до перемикання режиму): її скасовує Quartz.
    /// </remarks>
    public async Task CancelAsync(string jobId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        if (await queue.RequestCancelAsync(jobId, ct).ConfigureAwait(false) == CancelOutcome.NotFound)
        {
            await cron.CancelAsync(jobId, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ L2-11: на ціль уже чекає інша задача — відмова 409 з її ідентифікатором
    /// (<c>err.ECR-JOB-0409.restartCoveredBy</c>), а не перехід у Quartz: там деталі
    /// немає, і людина отримувала 404 «деталі не пережили перезапуск сервера» — неправду.
    /// </remarks>
    public async Task<bool> RestartAsync(string jobId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var outcome = await queue.RestartAsync(jobId, ct).ConfigureAwait(false);
        if (outcome.IsRestarted)
        {
            signal.Notify();
            return true;
        }

        if (outcome.CoveringJobId is { } covering)
        {
            throw new BusinessRuleException(
                ErrorCodes.JobStateConflict,
                $"Задачу {jobId} не перезапущено: на ту саму ціль уже чекає задача {covering}, вона й виконає роботу.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-JOB-0409.restartCoveredBy",
                    ["jobId"] = jobId,
                    ["coveredBy"] = covering,
                });
        }

        return await cron.RestartAsync(jobId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<JobStatus> GetStatusAsync(string jobId, CancellationToken ct) => cron.GetStatusAsync(jobId, ct);

    /// <inheritdoc />
    public Task<int?> GetCreatedByUserIdAsync(string jobId, CancellationToken ct)
        => cron.GetCreatedByUserIdAsync(jobId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<JobSummary>> ListRecentAsync(JobListFilter filter, int limit, CancellationToken ct)
        => cron.ListRecentAsync(filter, limit, ct);

    /// <summary>Документ задачі — числова властивість <c>documentId</c> кореня payload (BE-08).</summary>
    /// <remarks>
    /// ⚠ Те саме правило, що <c>QuartzJobScheduler.DocumentIdOf</c> (там воно
    /// приватне; файл поза межами F1c) — дві копії по вісім рядків до I1, де
    /// Quartz-постановка зникає разом із режимом.
    /// </remarks>
    private static long? DocumentIdOf(string json)
    {
        using var doc = JsonDocument.Parse(json);

        return doc.RootElement.ValueKind == JsonValueKind.Object
               && doc.RootElement.TryGetProperty("documentId", out var id)
               && id.ValueKind == JsonValueKind.Number
               && id.TryGetInt64(out var value)
            ? value
            : null;
    }
}

/// <summary>
/// Підказка воркерам цього процесу «щойно поставили задачу» — щоб не чекати
/// наступного опитування.
/// </summary>
/// <remarks>
/// ⚠ Лише підказка: сигнал губиться між процесами й може прийти раніше за
/// коміт постановки. Правду каже тільки опитування черги.
/// </remarks>
public sealed class JobQueueSignal
{
    private readonly Channel<bool> channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    /// <summary>Будить один воркер, що чекає.</summary>
    public void Notify() => channel.Writer.TryWrite(true);

    /// <summary>Чекає сигналу не довше за <paramref name="timeout"/>.</summary>
    internal async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);

        try
        {
            await channel.Reader.ReadAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }
}
