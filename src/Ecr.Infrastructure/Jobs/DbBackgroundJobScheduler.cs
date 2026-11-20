// src/Ecr.Infrastructure/Jobs/DbBackgroundJobScheduler.cs
using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
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
/// <param name="services">
/// Провайдер scope — лише щоб ЛІНИВО взяти <see cref="ApprovedDependentsFallback"/> після скасування людиною (B80 N5):
/// той залежить від тригера перерахунку, а тригер — від цього планувальника, тож пряма залежність була б циклом.
/// <c>null</c> (ручна збірка в тестах) — без повернення покритих задач.
/// </param>
public sealed class DbBackgroundJobScheduler(
    IJobQueue queue,
    QuartzJobScheduler cron,
    JobQueueSignal signal,
    ICorrelationIdAccessor? correlation = null,
    IServiceProvider? services = null) : IBackgroundJobScheduler
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

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ <see cref="JobEnqueueRequest.Delay"/>: <c>AvailableAt = зараз + delay</c>; злиття з тією, що чекає, лишає
    /// раніший момент (<c>DbJobQueue.EnqueueSql</c>), тож вікно не подовжується (PSS-07).
    /// </remarks>
    public Task<string> EnqueueCoalescedDelayedAsync<TJob>(
        string targetKey, object? payload, TimeSpan delay, CancellationToken ct, int? createdByUserId = null)
        where TJob : IBackgroundJob
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);

        return EnqueueCoreAsync<TJob>(payload, TargetKeyOf<TJob>(targetKey), supersede: false, createdByUserId, ct, delay);
    }

    /// <summary><c>{TypeName}~{target}</c> — одне визначення на обидві постановки з ціллю.</summary>
    internal static string TargetKeyOf<TJob>(string targetKey)
        => $"{typeof(TJob).Name}{TargetSeparator}{targetKey.Replace(TargetSeparator, '_')}";

    private async Task<string> EnqueueCoreAsync<TJob>(
        object? payload, string? targetKey, bool supersede, int? createdByUserId, CancellationToken ct,
        TimeSpan? delay = null)
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
                    Delay: delay is { } d && d > TimeSpan.Zero ? d : null,
                    SupersedeRunning: supersede),
                ct)
            .ConfigureAwait(false);

        // ⛔ B125: у відкритій транзакції рядок ще не видно — сигнал іде ПІСЛЯ її коміту (при відкаті не йде
        // зовсім), інакше воркер прокидався раніше за коміт, нічого не бачив і спав PollInterval (~1 с).
        // Без транзакції — одразу. B85: з лейном — будить і окремий/резервний цикл, що опитує цей лейн.
        signal.NotifyAfterCommit((queue as DbJobQueue)?.Context, JobLaneMap.Of<TJob>());

        return result.JobId;
    }

    /// <inheritdoc />
    public Task<string?> FindQueuedAsync<TJob>(string targetKey, CancellationToken ct)
        where TJob : IBackgroundJob
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);

        // ⛔ Див. зауваження порту: у відкритій транзакції викликача покриття без локу небезпечне — «немає».
        if ((queue as DbJobQueue)?.Context.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
        {
            return Task.FromResult<string?>(null);
        }

        return queue.FindQueuedByTargetAsync(TargetKeyOf<TJob>(targetKey), ct);
    }

    /// <inheritdoc />
    public Task<bool> CancelQueuedAsync<TJob>(string targetKey, string? coveredByJobId, CancellationToken ct)
        where TJob : IBackgroundJob
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);

        return queue.CancelQueuedByTargetAsync(TargetKeyOf<TJob>(targetKey), coveredByJobId, ct);
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
    /// <para>
    /// ⛔ B80 N5: задача, що чекала, скасована ОДРАЗУ — якщо це «залежні з затвердженими», вона могла покривати звичайний
    /// перерахунок залежних того самого документа й періоду; той повертається (<see cref="ApprovedDependentsFallback"/>).
    /// Виконувану скасовує виконавець (запит скасування) — повернення там же, у <c>JobWorker</c>.
    /// </para>
    /// </remarks>
    public async Task CancelAsync(string jobId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var outcome = await queue.RequestCancelAsync(jobId, ct).ConfigureAwait(false);
        if (outcome == CancelOutcome.NotFound)
        {
            await cron.CancelAsync(jobId, ct).ConfigureAwait(false);
            return;
        }

        if (outcome == CancelOutcome.Cancelled
            && services?.GetService(typeof(ApprovedDependentsFallback)) is ApprovedDependentsFallback fallback)
        {
            await fallback.OnEndedAsync([jobId], ct).ConfigureAwait(false);
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
            // B125: перепостановка — так само після коміту транзакції викликача (лейн тут невідомий — будимо всіх).
            signal.NotifyAfterCommit((queue as DbJobQueue)?.Context, lane: null);
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
/// <para>
/// ⛔ B85: основний цикл воркера чекає спільного каналу (будить ОДНОГО). Окремий і резервний цикли мають ВЛАСНІ
/// підписки на свої лейни (<see cref="Subscribe"/>): раніше вони спали <c>PollInterval</c> без сигналу, і задача
/// лейна <c>excel</c> чекала старту 150–1100 мс (замір B85, N = 24, при claim ≈ 5 мс). Власна підписка не «з'їдає»
/// сигнал основного циклу — для нього лишається спільний канал.
/// </para>
/// <para>
/// ⛔ B125: постановка в транзакції викликача будить воркер ПІСЛЯ коміту (<see cref="NotifyAfterCommit"/>): сигнал
/// відкладається до <c>TransactionCommitted</c> цієї транзакції (сінглтон підключено перехоплювачем
/// <c>EcrDbContext</c>), відкат чи збій його скасовують. До B125 сигнал ішов до коміту — воркер прокидався, не бачив
/// рядка і спав <c>PollInterval</c>. Ключ — сама <see cref="DbTransaction"/>: транзакція, звільнена без коміту, забирає
/// відкладений сигнал із собою (<see cref="ConditionalWeakTable{TKey, TValue}"/>).
/// </para>
/// </remarks>
public sealed class JobQueueSignal : DbTransactionInterceptor
{
    private readonly Channel<bool> channel = NewChannel();

    private readonly ConcurrentDictionary<LaneWaiter, byte> waiters = new();

    private readonly ConditionalWeakTable<DbTransaction, PendingLanes> afterCommit = [];

    /// <summary>
    /// B125: сигнал постановки, зробленої через <paramref name="db"/>. Відкрита транзакція контексту, до якого цей
    /// сигнал підключено перехоплювачем, — після її коміту; інакше (автокоміт, контекст без перехоплювача) — одразу.
    /// </summary>
    /// <param name="db">Контекст постановки (<c>null</c> — невідомий, сигнал одразу).</param>
    /// <param name="lane">Лейн задачі (<c>null</c> — невідомий, будити всі цикли).</param>
    public void NotifyAfterCommit(DbContext? db, string? lane)
    {
        var transaction = db?.Database.CurrentTransaction?.GetDbTransaction();
        if (transaction is null || !InterceptsFor(db!))
        {
            NotifyNow(lane);
            return;
        }

        afterCommit.GetOrCreateValue(transaction).Add(lane);
    }

    /// <inheritdoc />
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        Release(transaction, notify: true);
        base.TransactionCommitted(transaction, eventData);
    }

    /// <inheritdoc />
    public override Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Release(transaction, notify: true);
        return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
    }

    /// <inheritdoc />
    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    {
        Release(transaction, notify: false);
        base.TransactionRolledBack(transaction, eventData);
    }

    /// <inheritdoc />
    public override Task TransactionRolledBackAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Release(transaction, notify: false);
        return base.TransactionRolledBackAsync(transaction, eventData, cancellationToken);
    }

    /// <inheritdoc />
    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
    {
        Release(transaction, notify: false);
        base.TransactionFailed(transaction, eventData);
    }

    /// <inheritdoc />
    public override Task TransactionFailedAsync(
        DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Release(transaction, notify: false);
        return base.TransactionFailedAsync(transaction, eventData, cancellationToken);
    }

    private void Release(DbTransaction transaction, bool notify)
    {
        if (!afterCommit.TryGetValue(transaction, out var pending) || !afterCommit.Remove(transaction))
        {
            return;
        }

        if (notify)
        {
            foreach (var lane in pending.Drain())
            {
                NotifyNow(lane);
            }
        }
    }

    private void NotifyNow(string? lane)
    {
        if (lane is null)
        {
            Notify();
        }
        else
        {
            Notify(lane);
        }
    }

    /// <summary>Чи підключено цей сигнал перехоплювачем до <paramref name="db"/> (інакше коміту він не побачить).</summary>
    private bool InterceptsFor(DbContext db)
        => ((IInfrastructure<IServiceProvider>)db).Instance.GetService(typeof(IDbContextOptions)) is IDbContextOptions options
           && options.FindExtension<CoreOptionsExtension>()?.Interceptors?.Contains(this) == true;

    /// <summary>Лейни, відкладені до коміту однієї транзакції.</summary>
    private sealed class PendingLanes
    {
        private readonly List<string?> lanes = [];

        public void Add(string? lane)
        {
            lock (lanes)
            {
                if (!lanes.Contains(lane))
                {
                    lanes.Add(lane);
                }
            }
        }

        public string?[] Drain()
        {
            lock (lanes)
            {
                return [.. lanes];
            }
        }
    }

    /// <summary>Будить основний цикл і всі підписки (лейн невідомий).</summary>
    public void Notify()
    {
        channel.Writer.TryWrite(true);
        foreach (var waiter in waiters.Keys)
        {
            waiter.Wake();
        }
    }

    /// <summary>Будить основний цикл і підписки, що опитують <paramref name="lane"/>.</summary>
    public void Notify(string lane)
    {
        channel.Writer.TryWrite(true);
        foreach (var waiter in waiters.Keys)
        {
            if (waiter.Covers(lane))
            {
                waiter.Wake();
            }
        }
    }

    /// <summary>Чекає сигналу спільного каналу не довше за <paramref name="timeout"/>.</summary>
    internal Task WaitAsync(TimeSpan timeout, CancellationToken ct) => WaitOnAsync(channel, timeout, ct);

    /// <summary>Власна підписка циклу на <paramref name="lanes"/>; Dispose — відписка.</summary>
    internal LaneWaiter Subscribe(IReadOnlyCollection<string> lanes)
    {
        var waiter = new LaneWaiter(this, lanes);
        waiters[waiter] = 0;
        return waiter;
    }

    private static Channel<bool> NewChannel() => Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private static async Task WaitOnAsync(Channel<bool> source, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);

        try
        {
            await source.Reader.ReadAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>Підписка одного циклу: свій канал місткістю 1 — сигнал між claim і очікуванням не губиться.</summary>
    internal sealed class LaneWaiter(JobQueueSignal owner, IReadOnlyCollection<string> lanes) : IDisposable
    {
        private readonly Channel<bool> own = NewChannel();

        private readonly HashSet<string> covered = new(lanes, StringComparer.Ordinal);

        internal bool Covers(string lane) => covered.Contains(lane);

        internal void Wake() => own.Writer.TryWrite(true);

        /// <summary>Чекає сигналу свого лейна не довше за <paramref name="timeout"/>.</summary>
        internal Task WaitAsync(TimeSpan timeout, CancellationToken ct) => WaitOnAsync(own, timeout, ct);

        public void Dispose() => owner.waiters.TryRemove(this, out _);
    }
}
