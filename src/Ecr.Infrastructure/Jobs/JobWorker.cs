// src/Ecr.Infrastructure/Jobs/JobWorker.cs
using System.Collections.Concurrent;
using System.Diagnostics;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;
using Ecr.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecr.Infrastructure.Jobs;

/// <summary>Налаштування <see cref="JobWorker"/>.</summary>
public sealed record JobWorkerOptions
{
    /// <summary>Ключ каталогу причини «перевищено найдовшу тривалість» (<see cref="MaxDuration"/>).</summary>
    public const string MaxDurationKey = "jobs.maxDurationExceeded";

    /// <summary>Лейни, які опитує воркер (<see cref="JobLaneMap.ApiLanes"/> для Api).</summary>
    public required IReadOnlyList<string> Lanes { get; init; }

    /// <summary>
    /// Роль процесу в <c>InstanceId</c> (P3): Api — <see cref="JobProgressStore.RoleApi"/>,
    /// окремий воркер-процес — <see cref="JobProgressStore.RoleWorker"/>.
    /// </summary>
    public string Role { get; init; } = JobProgressStore.RoleApi;

    /// <summary>Скільки задач виконується одночасно.</summary>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>Опитування черги, коли роботи немає.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Оренда задачі (<c>D-208</c>).</summary>
    public TimeSpan Lease { get; init; } = JobQueueLimits.DefaultLease;

    /// <summary>Подовження оренди.</summary>
    public TimeSpan RenewInterval { get; init; } = JobQueueLimits.RenewInterval;

    /// <summary>Закриття простроченого, яке claim уже не переклеймить (<see cref="IJobQueue.ExpireAsync"/>).</summary>
    public TimeSpan ExpireInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Пауза після «черга непридатна» (RCSI вимкнено).</summary>
    public TimeSpan RcsiBackoff { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Пауза після іншої помилки захоплення (база недоступна).</summary>
    public TimeSpan ErrorBackoff { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Затримка перед ретраєм N (від 1); лише тести підміняють.</summary>
    public Func<int, TimeSpan> RetryDelay { get; init; } = JobRetryPolicy.DelayBefore;

    /// <summary>
    /// Найдовше виконання однієї задачі (<c>Jobs:Workers:MaxDuration</c>, I1);
    /// <c>null</c> — без межі (Api). Задача, що перевищила межу, скасовується і
    /// закривається <c>Failed</c> з конвертом <see cref="MaxDurationKey"/>.
    /// </summary>
    public TimeSpan? MaxDuration { get; init; }

    /// <summary>
    /// Стеля сумарного відкладення однієї задачі від першого (борг O1,
    /// <see cref="JobDeferral.MaxDeferral"/>): перевищила — <c>Failed</c> з конвертом
    /// <see cref="JobDeferral.ExhaustedKey"/>, без ретраю.
    /// </summary>
    public TimeSpan MaxDeferral { get; init; } = JobDeferral.MaxDeferral;
}

/// <summary>
/// Виконавець черги задач у базі (MI-02, <c>D14-01</c>, <c>D-208</c>).
/// </summary>
/// <remarks>
/// ⚠ Виконання задачі — тим самим шляхом, що в <see cref="QuartzJobAdapter"/>:
/// той самий резолв за повним іменем типу, payload рядком JSON, прогрес через
/// <see cref="StoreJobProgress"/>, кореляція в scope журналу, ретраї — одна
/// <see cref="JobRetryPolicy"/>. Відмінне лише те, що дає черга: оренда з
/// токеном замість процесу-власника.
/// <para>
/// ⛔ Кожна задача — СВІЙ DI-scope з прив'язаною орендою
/// (<see cref="JobLeaseContext"/>): fencing видимості читає її звідти. Дії з
/// орендою (подовження, завершення) — у ВЛАСНИХ коротких scope: <c>DbContext</c>
/// задачі не потокобезпечний і в цю мить зайнятий нею самою.
/// </para>
/// <para>
/// ⛔ Втрачена оренда — нічого не писати: рядок уже належить іншому виконавцю.
/// Усі записи власника (<c>Complete</c>/<c>Fail</c>/<c>Requeue</c>) і так
/// звірені з токеном у SQL, тож пізній запис не зашкодить — але й не потрібен.
/// </para>
/// </remarks>
public sealed partial class JobWorker(
    IServiceScopeFactory scopes,
    JobWorkerOptions options,
    JobQueueSignal signal,
    ILogger<JobWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Task> running = new();

    /// <summary>Причина для рядків попереднього процесу цієї машини й ролі.</summary>
    private const string PreviousInstanceReason = "Воркер перезапущено: задача не завершилася до зупинки процесу.";

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ⛔ P3: роль — ДО першої задачі. Перше читання CurrentInstanceId фіксує
        // її, і рядки воркера з роллю api закривав би старт Api цієї машини.
        JobProgressStore.UseRole(options.Role);
        var owner = JobProgressStore.CurrentInstanceId;

        await CloseOwnPreviousInstanceAsync(owner).ConfigureAwait(false);

        using var slots = new SemaphoreSlim(options.MaxConcurrency, options.MaxConcurrency);
        var lastExpire = 0L;
        var queueUnusableReported = false;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (lastExpire == 0 || Stopwatch.GetElapsedTime(lastExpire) >= options.ExpireInterval)
                {
                    lastExpire = Stopwatch.GetTimestamp();
                    await ExpireAsync().ConfigureAwait(false);
                }

                await slots.WaitAsync(stoppingToken).ConfigureAwait(false);

                ClaimedJob? job = null;
                TimeSpan? backoff = null;

                try
                {
                    job = await ClaimAsync(owner).ConfigureAwait(false);
                    queueUnusableReported = false;
                }
                catch (InvalidOperationException ex) when (ex.GetType() == typeof(InvalidOperationException))
                {
                    // ⛔ «RCSI вимкнено» (DbJobQueue.EnsureRcsiAsync) — стан бази, не збій
                    // дороги: щосекундний повтор лише засипав би журнал тим самим
                    // рядком. Critical ОДИН раз на епізод, далі — пауза хвилинами;
                    // стан і так Unhealthy (DatabaseHealthCheck).
                    if (!queueUnusableReported)
                    {
                        LogQueueUnusable(logger, options.RcsiBackoff, ex);
                        queueUnusableReported = true;
                    }

                    backoff = options.RcsiBackoff;
                }
#pragma warning disable CA1031 // Цикл воркера не падає від збою бази: пауза і наступна спроба.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogClaimFailed(logger, options.ErrorBackoff, ex);
                    backoff = options.ErrorBackoff;
                }

                if (job is null)
                {
                    slots.Release();

                    // ⚠ Сигнал постановки будить лише зі звичайного очікування: у
                    // паузі після відмови кожна постановка інакше знову будила б цикл.
                    if (backoff is { } pause)
                    {
                        await Task.Delay(pause, stoppingToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await signal.WaitAsync(options.PollInterval, stoppingToken).ConfigureAwait(false);
                    }

                    continue;
                }

                Prune();
                running[job.Claim.Token] = RunAsync(job, slots, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Зупинка хоста: задачі отримали скасування і повертаються в чергу (RunAsync).
            await Task.WhenAll(running.Values).ConfigureAwait(false);
        }
    }

    private void Prune()
    {
        foreach (var (token, task) in running)
        {
            if (task.IsCompleted)
            {
                running.TryRemove(token, out _);
            }
        }
    }

    private async Task<ClaimedJob?> ClaimAsync(string owner)
    {
        await using var scope = scopes.CreateAsyncScope();

        // ⚠ CancellationToken.None: скасований посеред UPDATE…OUTPUT claim міг би
        // закомітити захоплення, а задачу ніхто б не отримав — до кінця оренди.
        return await scope.ServiceProvider.GetRequiredService<IJobQueue>()
            .ClaimAsync(options.Lanes, owner, options.Lease, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task ExpireAsync()
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IJobQueue>()
                .ExpireAsync(JobQueueLimits.MaxReclaims, CancellationToken.None)
                .ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Прибирання — не робота воркера: збій іде в журнал, цикл живе.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogExpireFailed(logger, ex);
        }
    }

    /// <summary>
    /// Старт окремого воркер-процесу закриває рядки ПОПЕРЕДНЬОГО процесу цієї
    /// машини й ролі (P3). Api це вже робить у <c>StartupSequence</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Лише рядки дзеркала Quartz (<c>Lane IS NULL</c>, правка Г): рядки черги
    /// закриває прострочена оренда — їх перехопить і довиконає інший виконавець.
    /// </remarks>
    private async Task CloseOwnPreviousInstanceAsync(string owner)
    {
        if (string.Equals(options.Role, JobProgressStore.RoleApi, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetService<IJobProgressStore>();
            if (store is not null)
            {
                await store.FailPreviousInstanceAsync(
                        JobProgressStore.CurrentHostName, JobProgressStore.CurrentRole, owner, PreviousInstanceReason,
                        scope.ServiceProvider.GetRequiredService<IClock>().UtcNow, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // Прибирання чужих рядків не зупиняє виконання нових задач.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogPreviousInstanceFailed(logger, ex);
        }
    }

    private async Task RunAsync(ClaimedJob job, SemaphoreSlim slots, CancellationToken stopping)
    {
        try
        {
            // Свій потік: задача не займає цикл захоплення навіть до першого await.
            await Task.Run(() => ExecuteClaimedAsync(job, stopping), CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Збій одного виконання не зупиняє воркер; причина — у журналі.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogWorkerFault(logger, job.Claim.JobId, ex);
        }
        finally
        {
            slots.Release();
        }
    }

    private async Task ExecuteClaimedAsync(ClaimedJob job, CancellationToken stopping)
    {
        var claim = job.Claim;
        var correlationId = job.CorrelationId is { Length: > 0 } id ? id : Guid.NewGuid().ToString("N");

        // Кореляція постановника — у КОЖНОМУ рядку журналу задачі (BE-08), як у Quartz.
        using var logScope = logger.BeginScope(
            new Dictionary<string, object> { ["CorrelationId"] = correlationId, ["JobId"] = claim.JobId });

        await using var scope = scopes.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        provider.GetRequiredService<JobLeaseContext>().Bind(claim);

        var instance = QuartzJobAdapter.Resolve(provider, job.JobCode);
        if (instance is null)
        {
            // ⛔ Невідома задача — гучна відмова, не вічне «виконується».
            LogUnknownJob(logger, job.JobCode, claim.JobId);
            await SettleAsync(claim.JobId, q => q.FailAsync(
                    claim, $"Задача «{job.JobCode}» не зареєстрована.", ErrorCodes.Internal, CancellationToken.None))
                .ConfigureAwait(false);
            return;
        }

        var clock = provider.GetRequiredService<IClock>();

        // ФВ-12.2: скільки задача чекала в черзі до початку виконання (від AvailableAt, годинник
        // СУБД). Метрики в процесі може не бути (дочірній воркер) — тоді просто пропуск.
        if (job.QueueWaitMs is { } waitedMs)
        {
            provider.GetService<IJobStartMetrics>()?.RecordStartLatency((double)waitedMs, job.JobCode, job.Lane);
        }

        var lease = new LeaseWatch();
        using var jobCancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        using var renewStop = new CancellationTokenSource();
        var renew = RenewLoopAsync(claim, lease, jobCancel, renewStop.Token);

        // ⛔ I1: межа тривалості — скасуванням токена задачі, як і запит скасування.
        // Процес живе далі й бере наступну задачу; оренда без межі подовжувалася б
        // вічно, і зависла задача тримала б місце пулу назавжди.
        using var overtime = new CancellationTokenSource();
        using var overtimeHook = overtime.Token.Register(() =>
        {
            lease.TimedOut = true;
            jobCancel.Cancel();
        });
        if (options.MaxDuration is { } maxDuration)
        {
            overtime.CancelAfter(maxDuration);
        }

        Exception? failure = null;
        JobDeferredException? deferred = null;
        var cancelled = false;
        var runStarted = Stopwatch.GetTimestamp();

        try
        {
            await instance.ExecuteAsync(
                    job.PayloadJson,
                    new StoreJobProgress(provider.GetService<IJobProgressStore>(), claim.JobId, clock),
                    jobCancel.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (JobDeferredException ex)
        {
            deferred = ex;
        }
#pragma warning disable CA1031 // Будь-який провал задачі класифікує JobRetryPolicy нижче.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failure = ex;
        }
        finally
        {
            await renewStop.CancelAsync().ConfigureAwait(false);
            await renew.ConfigureAwait(false);
        }

        if (lease.Lost || failure is JobLeaseLostException)
        {
            LogLeaseLost(logger, claim.JobId);
            return;
        }

        // ⚠ Скасована посеред запиту до бази задача може впасти не OCE, а винятком
        // драйвера («Operation cancelled by user») — межа однаково її причина.
        if (lease.TimedOut && !lease.CancelRequested && !stopping.IsCancellationRequested
            && (cancelled || failure is not null))
        {
            await FailOvertimeAsync(job, clock).ConfigureAwait(false);
            return;
        }

        // ⛔ O1 (I2 ФВ-9.8): ресурс зайнятий (лок документа перерахунку) — у чергу
        // через відступ, без спроби ретраю; слот звільняється одразу, а не після
        // очікування чужого прогону.
        if (deferred is not null)
        {
            // ⛔ Стеля (борг O1): лок, що не звільняється, — не вічні повтори, а Failed.
            var since = JobDeferral.SinceOf(job.PayloadJson);
            if (JobDeferral.IsExhausted(since, clock.UtcNow, options.MaxDeferral))
            {
                // Д-1 (огляд O1): комірки задачі злиття — у нову задачу цілі ДО Failed. Навпаки
                // збій між двома записами губив би їх; так — щонайбільше зайвий перерахунок.
                await RequeueExhaustedAsync(job).ConfigureAwait(false);
                await FailDeferralExhaustedAsync(job, deferred, clock.UtcNow - since!.Value, clock).ConfigureAwait(false);
                return;
            }

            LogJobDeferred(logger, claim.JobId, job.JobCode, deferred.Delay);
            await SettleAsync(claim.JobId, q => q.DeferAsync(claim, deferred.Delay, CancellationToken.None))
                .ConfigureAwait(false);
            return;
        }

        if (cancelled)
        {
            // Зупинка хоста без запиту скасування — задачу не скасовано, її повертають у чергу.
            var shutdown = stopping.IsCancellationRequested && !lease.CancelRequested;
            await SettleAsync(claim.JobId, q => shutdown
                    ? q.RequeueAsync(claim, TimeSpan.Zero, CancellationToken.None)
                    : q.AcknowledgeCancelAsync(claim, CancellationToken.None))
                .ConfigureAwait(false);
            return;
        }

        // ФВ-12.7: тривалість спроби — лише для завершених (ok) і провалених (error); відступ,
        // скасування й втрата оренди — не «робота», їх не міряємо.
        Observability.InfrastructureMetrics.RecordJobRun(
            job.JobCode, failure is null ? "ok" : "error", Stopwatch.GetElapsedTime(runStarted).TotalSeconds);

        if (failure is null)
        {
            await SettleAsync(claim.JobId, q => q.CompleteAsync(claim, CancellationToken.None)).ConfigureAwait(false);
            return;
        }

        var retriesDone = Math.Max(0, job.Attempt - 1);
        if (JobRetryPolicy.ShouldRetry(retriesDone, failure))
        {
            var delay = options.RetryDelay(retriesDone + 1);
            LogJobRetrying(logger, claim.JobId, job.JobCode, failure);

            await WriteProgressAsync(claim.JobId, store => store.ReportAsync(
                    claim.JobId, 0, JobRetryPolicy.RetryScheduledMessage(retriesDone + 1, delay, failure, correlationId),
                    clock.UtcNow, CancellationToken.None))
                .ConfigureAwait(false);
            await SettleAsync(claim.JobId, q => q.RequeueAsync(claim, delay, CancellationToken.None)).ConfigureAwait(false);
            return;
        }

        LogJobFailed(logger, claim.JobId, job.JobCode, failure);
        Observability.InfrastructureMetrics.RecordJobFailed(job.JobCode, "error");
        await SettleAsync(claim.JobId, q => q.FailAsync(
                claim, JobRetryPolicy.FailureText(failure, correlationId), JobRetryPolicy.ErrorCodeOf(failure),
                CancellationToken.None))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Подовжує оренду, поки задача виконується; запит скасування чи втрата
    /// оренди скасовують токен задачі.
    /// </summary>
    /// <remarks>⚠ Не кидає: його дочікуються у <c>finally</c>, і виняток підмінив би причину провалу задачі.</remarks>
    private async Task RenewLoopAsync(
        JobClaimToken claim, LeaseWatch lease, CancellationTokenSource jobCancel, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(options.RenewInterval);
        using var stop = ct.Register(timer.Dispose);

        while (await timer.WaitForNextTickAsync(CancellationToken.None).ConfigureAwait(false))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var state = await scope.ServiceProvider.GetRequiredService<IJobQueue>()
                    .RenewAsync(claim, options.Lease, CancellationToken.None)
                    .ConfigureAwait(false);

                if (state == LeaseState.Lost)
                {
                    lease.Lost = true;
                    await jobCancel.CancelAsync().ConfigureAwait(false);
                    return;
                }

                if (state == LeaseState.CancelRequested && !lease.CancelRequested)
                {
                    // Скасування з будь-якого хоста: позначка в рядку, токен — тут.
                    lease.CancelRequested = true;
                    LogCancelRequested(logger, claim.JobId);
                    await jobCancel.CancelAsync().ConfigureAwait(false);
                }
            }
#pragma warning disable CA1031 // Пропущене подовження — не втрата: оренда вчетверо довша за інтервал.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogRenewFailed(logger, claim.JobId, ex);
            }
        }
    }

    /// <summary>
    /// Задача перевищила <see cref="JobWorkerOptions.MaxDuration"/>: <c>Failed</c> без
    /// ретраю, причина — конверт <see cref="JobWorkerOptions.MaxDurationKey"/> у
    /// <c>Message</c> (як у отруйної задачі), текст для журналу — в <c>Error</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Без ретраю: та сама задача на тих самих даних упреться в ту саму межу, і
    /// повтор лише втричі довше тримав би місце пулу.
    /// </remarks>
    private async Task FailOvertimeAsync(ClaimedJob job, IClock clock)
    {
        var claim = job.Claim;
        var limit = options.MaxDuration!.Value.ToString("c", System.Globalization.CultureInfo.InvariantCulture);
        LogJobOvertime(logger, claim.JobId, job.JobCode, limit);
        Observability.InfrastructureMetrics.RecordJobFailed(job.JobCode, "overtime");

        var envelope = JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(
            JobWorkerOptions.MaxDurationKey,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["limit"] = limit }));

        await WriteProgressAsync(claim.JobId, async store =>
            {
                // Відсоток лишається тим, до якого задача дійшла: видно, де її зупинили.
                var percent = (await store.FindAsync(claim.JobId, CancellationToken.None).ConfigureAwait(false))?.Percent ?? 0;
                await store.ReportAsync(claim.JobId, percent, envelope, clock.UtcNow, CancellationToken.None)
                    .ConfigureAwait(false);
            })
            .ConfigureAwait(false);

        await SettleAsync(claim.JobId, q => q.FailAsync(
                claim, $"The job ran longer than the limit of {limit} (Jobs:Workers:MaxDuration) and was stopped.",
                errorCode: null, CancellationToken.None))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Відкладення вичерпало <see cref="JobWorkerOptions.MaxDeferral"/>: <c>Failed</c> без
    /// ретраю, конверт <see cref="JobDeferral.ExhaustedKey"/> у <c>Message</c>.
    /// </summary>
    private async Task FailDeferralExhaustedAsync(
        ClaimedJob job, JobDeferredException deferred, TimeSpan waited, IClock clock)
    {
        var claim = job.Claim;
        var shown = JobDeferral.Format(waited);
        LogDeferralExhausted(logger, claim.JobId, job.JobCode, deferred.Resource ?? "—", shown);
        Observability.InfrastructureMetrics.RecordJobFailed(job.JobCode, "deferral_exhausted");

        await WriteProgressAsync(claim.JobId, store => store.ReportAsync(
                claim.JobId, 0, JobDeferral.Envelope(deferred.Resource, waited), clock.UtcNow, CancellationToken.None))
            .ConfigureAwait(false);

        await SettleAsync(claim.JobId, q => q.FailAsync(
                claim,
                $"The job was deferred for {shown} waiting for resource {deferred.Resource ?? "—"} and was stopped.",
                errorCode: null, CancellationToken.None))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Стелю вичерпала задача злиття (Д-1 огляду O1): її комірки — нова задача на ту саму
    /// ціль, раз (<see cref="JobDeferral.RequeuePayload"/>).
    /// </summary>
    /// <remarks>
    /// ⚠ Поки ця задача <c>Running</c>, нова стоїть <c>Queued</c> позаду (claim не бере ціль
    /// з <c>Running</c>) або зливається в ту, що вже чекає. Збій — у журнал: задача однаково
    /// закривається <c>Failed</c> з конвертом.
    /// </remarks>
    private async Task RequeueExhaustedAsync(ClaimedJob job)
    {
        if (!string.Equals(job.JobCode, JobPayloadMerge.MergeableJobCode, StringComparison.Ordinal)
            || JobDeferral.RequeuePayload(job.PayloadJson) is not { } again)
        {
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var requeued = await scope.ServiceProvider.GetRequiredService<IJobQueue>()
                .EnqueueAsync(
                    new JobEnqueueRequest(
                        job.JobCode, job.Lane, again, job.TargetKey, job.CreatedByUserId, job.CorrelationId,
                        job.DocumentId),
                    CancellationToken.None)
                .ConfigureAwait(false);
            LogDeferralRequeued(logger, job.Claim.JobId, requeued.JobId);
        }
#pragma warning disable CA1031 // Перепостановка — страховка; провал задачі записується нижче в будь-якому разі.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogDeferralRequeueFailed(logger, job.Claim.JobId, ex);
        }
    }

    /// <summary>Завершальна дія власника оренди у власному scope; <c>false</c> — оренду вже втрачено.</summary>
    private async Task SettleAsync(string jobId, Func<IJobQueue, Task<bool>> action)
    {
        await using var scope = scopes.CreateAsyncScope();

        if (!await action(scope.ServiceProvider.GetRequiredService<IJobQueue>()).ConfigureAwait(false))
        {
            LogLeaseLost(logger, jobId);
        }
    }

    /// <summary>Запис прогресу, чий збій не підміняє результат задачі (як у Quartz).</summary>
    private async Task WriteProgressAsync(string jobId, Func<IJobProgressStore, Task> write)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<IJobProgressStore>() is { } store)
            {
                await write(store).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // Прогрес — розповідь про задачу, а не сама задача.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogProgressWriteFailed(logger, jobId, ex);
        }
    }

    /// <summary>Що повідомило подовження оренди.</summary>
    private sealed class LeaseWatch
    {
        private volatile bool lost;
        private volatile bool cancelRequested;
        private volatile bool timedOut;

        public bool Lost { get => lost; set => lost = value; }

        public bool CancelRequested { get => cancelRequested; set => cancelRequested = value; }

        /// <summary>Спрацювала межа <see cref="JobWorkerOptions.MaxDuration"/>.</summary>
        public bool TimedOut { get => timedOut; set => timedOut = value; }
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Задача {JobId} ({TypeName}) перевищила найдовшу тривалість {Limit}; скасовано, стан Failed.")]
    private static partial void LogJobOvertime(ILogger logger, string jobId, string typeName, string limit);

    [LoggerMessage(
        Level = LogLevel.Critical,
        Message = "Черга задач не захоплює задач (READ_COMMITTED_SNAPSHOT вимкнено?); наступна спроба за {Backoff}. Повідомлення не повторюватиметься до відновлення.")]
    private static partial void LogQueueUnusable(ILogger logger, TimeSpan backoff, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Не вдалося захопити задачу з черги; наступна спроба за {Backoff}.")]
    private static partial void LogClaimFailed(ILogger logger, TimeSpan backoff, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Прибирання черги (прострочені оренди) не вдалося.")]
    private static partial void LogExpireFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Не вдалося закрити рядки попереднього процесу цієї машини й ролі.")]
    private static partial void LogPreviousInstanceFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Задача {TypeName} ({JobId}) не зареєстрована.")]
    private static partial void LogUnknownJob(ILogger logger, string typeName, string jobId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Задача {JobId} ({TypeName}) завершилася помилкою.")]
    private static partial void LogJobFailed(ILogger logger, string jobId, string typeName, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Задача {JobId} ({TypeName}) впала; повернуто в чергу з затримкою.")]
    private static partial void LogJobRetrying(ILogger logger, string jobId, string typeName, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Задача {JobId} ({TypeName}) відкладена на {Delay}: ресурс зайнятий; спробу не зараховано.")]
    private static partial void LogJobDeferred(ILogger logger, string jobId, string typeName, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Задача {JobId} ({TypeName}) відкладалась {Waited}, чекаючи ресурс {Resource}; стеля відкладень вичерпана, стан Failed.")]
    private static partial void LogDeferralExhausted(ILogger logger, string jobId, string typeName, string resource, string waited);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Комірки задачі {JobId}, що вичерпала стелю відкладень, перепоставлено задачею {RequeuedJobId}.")]
    private static partial void LogDeferralRequeued(ILogger logger, string jobId, string requeuedJobId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Не вдалося перепоставити комірки задачі {JobId}, що вичерпала стелю відкладень.")]
    private static partial void LogDeferralRequeueFailed(ILogger logger, string jobId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Оренду задачі {JobId} втрачено: її виконує інший виконавець; результат цього виконання не записано.")]
    private static partial void LogLeaseLost(ILogger logger, string jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Задачу {JobId} просили скасувати; зупиняю її тут.")]
    private static partial void LogCancelRequested(ILogger logger, string jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Не вдалося подовжити оренду задачі {JobId}; наступна спроба за інтервал.")]
    private static partial void LogRenewFailed(ILogger logger, string jobId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Не вдалося записати прогрес задачі {JobId}; на результат самої задачі це не впливає.")]
    private static partial void LogProgressWriteFailed(ILogger logger, string jobId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Збій виконавця черги на задачі {JobId}.")]
    private static partial void LogWorkerFault(ILogger logger, string jobId, Exception exception);
}
