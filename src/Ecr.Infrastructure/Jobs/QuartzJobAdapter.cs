using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Місток між Quartz і <see cref="IBackgroundJob"/>.
/// </summary>
/// <remarks>
/// ⚠ Існує саме щоб специфіка Quartz **не протікала в задачі**. Порт
/// <c>IBackgroundJobScheduler</c> уведений заради того, щоб заміна
/// планувальника коштувала день (D-09); якби кожна задача реалізовувала
/// <c>IJob</c>, заміна означала б переписати їх усі.
/// <para>
/// Задача створюється в СВОЄМУ scope: вона працює з <c>DbContext</c>, а той
/// scoped. Виконання в кореневому провайдері дало б один контекст на всі
/// прогони — і перший же паралельний прогін зіпсував би стан другого.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed partial class QuartzJobAdapter(
    IServiceProvider services, ILogger<QuartzJobAdapter> logger) : IJob
{
    /// <summary>
    /// Скільки РЕТРАЇВ (не спроб) дозволено після першого провалу (D-134, №11
    /// T10 #40) — <see cref="JobRetryPolicy.MaxRetryAttempts"/>.
    /// </summary>
    public const int MaxRetryAttempts = JobRetryPolicy.MaxRetryAttempts;

    /// <summary>Базова затримка відступу — <see cref="JobRetryPolicy.RetryBaseDelay"/>.</summary>
    public static readonly TimeSpan RetryBaseDelay = JobRetryPolicy.RetryBaseDelay;

    /// <summary>Стан скасованої задачі в <c>itg.JobProgress</c>.</summary>
    private const string CancelledState = "Cancelled";

    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var jobId = context.JobDetail.Key.Name;
        var typeName = context.JobDetail.JobDataMap.GetString(QuartzJobScheduler.JobCodeKey);
        // ⚠ O1: задача злиття (IFormulaRecalculationJob) несе актуальне тіло в
        // QuartzPayloadMerges — з масивами постановок, злитих, поки вона чекала.
        var payload = QuartzPayloadMerges.Take(jobId)
                      ?? context.JobDetail.JobDataMap.GetString(QuartzJobScheduler.PayloadKey);

        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;

        var job = Resolve(provider, typeName);
        if (job is null)
        {
            // ⛔ Невідома задача — гучна відмова, не тиша. Запис у черзі, який
            // нікому виконувати, інакше просто зникав би: клієнт бачив би
            // «виконується» вічно.
            LogUnknownJob(logger, typeName ?? "—", jobId);
            throw new JobExecutionException($"Задача «{typeName}» не зареєстрована.");
        }

        var progress = provider.GetService<IJobProgressStore>();
        var clock = provider.GetRequiredService<IClock>();

        // ⚠ Затримка старту (`ФВ-12.2`, `tz/08` §8.3) — ТУТ, а не після
        // блокування нижче: задача вже взята виконавцем, і все, що йде далі, —
        // це вже її робота, а не чекання в черзі. Міряти після лока означало б
        // домішувати до затримки конкуренцію інстансів.
        //
        // ⚠ Порт НЕОБОВ'ЯЗКОВИЙ (`GetService`, не `GetRequiredService`), як і
        // `IJobProgressStore` вище: у прогонах без метрик задачі мусять
        // виконуватись, а не падати на відсутньому спостерігачі.
        RecordStartLatency(
            provider.GetService<IJobStartMetrics>(),
            context.JobDetail.JobDataMap,
            clock,
            typeName);

        // ⛔ Q-223 (`Jobs`-секція): job поставлена через ScheduleAsync (крон) —
        // той самий детермінований ключ зареєстрований на КОЖНОМУ інстансі
        // окремо, тож без координації N інстансів виконали б її N разів на
        // один тик. Лок — негайна спроба, без очікування: якщо інший
        // інстанс уже виконує цю саму job, цей тик просто пропускається, а
        // не чекає й не дублює роботу пізніше.
        var isRecurring = QuartzJobScheduler.IsRecurring(context.JobDetail);

        await using var distributedLock = isRecurring
            ? await SqlDistributedLock.TryAcquireAsync(
                    provider.GetRequiredService<EcrDbContext>().Database.GetConnectionString()
                        ?? throw new InvalidOperationException("У контексту немає рядка підключення."),
                    QuartzJobScheduler.LockNameOf(jobId), context.CancellationToken)
                .ConfigureAwait(false)
            : null;

        if (isRecurring && distributedLock is null)
        {
            LogJobSkippedElsewhere(logger, jobId, typeName ?? "—");
            return;
        }

        // BE-08: спроба від 1 (ретраї Quartz рахуються від 0), кореляція — та,
        // що вже в логах запиту-постановника; scope несе її в УСІ рядки логу
        // задачі (MEL-scope спільний для всіх логерів цього async-потоку).
        var attemptNumber = CurrentAttempt(context) + 1;
        var correlationId = CorrelationOf(context);
        using var logScope = logger.BeginScope(
            new Dictionary<string, object> { ["CorrelationId"] = correlationId });

        // ⛔ Скасовану задачу з черги не запускаємо. Скасування з ІНШОГО
        // інстанса не може зняти задачу з цієї черги (вона в пам'яті цього
        // процесу) — воно лишає рядок `Cancelled` (`QuartzJobScheduler.CancelAsync`),
        // і саме тут це стає відмовою від старту. Без перевірки `Begin`
        // переписав би `Cancelled` на `Running`, і скасування мовчки не діяло б.
        // Розклад не перевіряється: `Cancelled` його рядка — це про МИНУЛИЙ тик.
        if (!isRecurring && progress is not null
            && await progress.FindAsync(jobId, context.CancellationToken).ConfigureAwait(false)
                is { State: CancelledState })
        {
            LogJobSkippedCancelled(logger, jobId, typeName ?? "—");
            await context.Scheduler.DeleteJob(context.JobDetail.Key, CancellationToken.None).ConfigureAwait(false);
            QuartzPayloadMerges.Forget(jobId);
            return;
        }

        await StartAsync(progress, jobId, typeName!, attemptNumber, correlationId, clock, context.CancellationToken)
            .ConfigureAwait(false);

        // ⚠ Власний токен задачі — зв'язаний із токеном Quartz. Quartz скасовує
        // свій на `Interrupt` (скасування на цьому інстансі, зупинка хоста), а
        // цей додатково скасовує биття серця, коли рядок закрили ЗЗОВНІ —
        // скасуванням з іншого інстанса.
        using var jobCancel = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);

        // ⛔ Биття серця на весь час виконання. Прибирання покинутих
        // (`IJobProgressStore.FailStaleAsync`) відрізняє покинуту задачу від
        // чужої живої саме за ним; без биття довга задача, яка не звітує
        // відсотків (імпорт великого файлу), через п'ять хвилин виглядала б
        // покинутою — і перезапуск сусіднього інстанса вбивав би її так само,
        // як до виправлення. Насос живе в СВОЄМУ scope: `DbContext` scoped і
        // не потокобезпечний, а задача в цю мить користується своїм.
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = HeartbeatLoopAsync(jobId, jobCancel, heartbeatStop.Token);

        try
        {
            await job.ExecuteAsync(
                payload,
                new StoreJobProgress(progress, jobId, clock),
                jobCancel.Token).ConfigureAwait(false);

            await FinishAsync(progress, jobId, "Succeeded", null, clock, context.CancellationToken)
                .ConfigureAwait(false);

            // ⚠ Дурабельність (QuartzJobScheduler.EnqueueCoreAsync) існує
            // ЛИШЕ заради ручного перезапуску провалу — успіх її не потребує,
            // і держати деталь задачі в пам'яті планувальника навічно означало
            // б повільну витік пам'яті на кожен успішний прогін.
            //
            // ⛔ Лише РАЗОВА. У розкладу ключ той самий, що в крон-тригера, а
            // DeleteJob знімає задачу разом із тригерами: сховище в пам'яті,
            // розклади ставляться тільки на старті — тож задача за розкладом
            // відпрацьовувала б рівно один раз до наступного рестарту.
            if (!isRecurring)
            {
                await context.Scheduler.DeleteJob(context.JobDetail.Key, CancellationToken.None)
                    .ConfigureAwait(false);
                QuartzPayloadMerges.Forget(jobId);
            }
        }
        catch (OperationCanceledException)
        {
            // Скасування — не провал: його попросили. Але й не успіх, і стан
            // мусить це розрізняти.
            await FinishAsync(progress, jobId, CancelledState, null, clock, CancellationToken.None)
                .ConfigureAwait(false);

            // ⛔ Скасовано ПОТОЧНИЙ прогін, а не розклад — див. гілку успіху.
            if (!isRecurring)
            {
                await context.Scheduler.DeleteJob(context.JobDetail.Key, CancellationToken.None)
                    .ConfigureAwait(false);
                QuartzPayloadMerges.Forget(jobId);
            }

            throw;
        }
        catch (Exception ex)
        {
            var attempt = CurrentAttempt(context);

            if (JobRetryPolicy.ShouldRetry(attempt, ex))
            {
                // ⚠ Ретрай — НЕ Failed. Клієнт, що опитує стан, має й далі
                // бачити задачу «у виконанні», а не короткий спалах «провалу»,
                // який за кілька секунд сам собою стає «виконується» знову.
                LogJobRetrying(logger, jobId, typeName ?? "—", ex);
                await ScheduleRetryAsync(context, attempt, correlationId, progress, clock, ex).ConfigureAwait(false);

                // У паузі ретраю задача знову чекає — злиття йде в неї (O1).
                QuartzPayloadMerges.Reopen(jobId);
                return;
            }

            // ⚠ Текст помилки в прогрес — БЕЗ стека (ФВ-6.11, D-11): стек
            // виносить назовні шляхи, імена і подекуди значення.
            //
            // ⚠ І вкорочений до межі стовпця: `Error` тримає 2000 символів, а
            // `ex.Message` не обмежений нічим.
            await WriteProgressAsync(
                    jobId,
                    () => FinishAsync(
                        progress,
                        jobId,
                        "Failed",
                        // ⛔ V-03: текст винятку БАЗИ (імена об'єктів, значення
                        // ключа) у `/jobs` не йде — лише в журнал рядком нижче.
                        JobRetryPolicy.FailureText(ex, correlationId),
                        clock,
                        CancellationToken.None,
                        JobRetryPolicy.ErrorCodeOf(ex)))
                .ConfigureAwait(false);

            LogJobFailed(logger, jobId, typeName ?? "—", ex);

            // ⛔ Деталь задачі НЕ видаляється тут. Дурабельна саме на цей
            // випадок (QuartzJobScheduler.EnqueueCoreAsync): без неї
            // IBackgroundJobScheduler.RestartAsync не мав би що перезапускати
            // — Quartz прибрав би задачу сам одразу після цього прогону.
            throw new JobExecutionException(ex, refireImmediately: false);
        }
        finally
        {
            // ⚠ Саме `finally`, а не зупинка в кожній гілці: гілок чотири
            // (успіх, скасування, ретрай, остаточний провал), і та, яку
            // забули б додати п'ятою, лишила б насос бити по задачі, що вже
            // завершилася. Пізнє биття саме по собі нешкідливе
            // (`HeartbeatAsync` фільтрує за станом), але вічний таймер на
            // кожен прогін — це витік.
            await StopHeartbeatAsync(heartbeatStop, heartbeat).ConfigureAwait(false);
        }
    }

    /// <summary>Зупиняє насос биття і дочікується його завершення.</summary>
    private static async Task StopHeartbeatAsync(CancellationTokenSource stop, Task loop)
    {
        await stop.CancelAsync().ConfigureAwait(false);
        await loop.ConfigureAwait(false);
    }

    /// <summary>
    /// Поки задача виконується — підтверджує сховищу, що вона жива.
    /// </summary>
    /// <remarks>
    /// ⚠ Цикл НЕ кидає: його дочікуються у <c>finally</c>, і виняток звідти
    /// підмінив би справжню причину провалу задачі биттям серця. Пропущений
    /// удар не є втратою — межа застарілості
    /// (<see cref="IJobProgressStore.StaleAfter"/>) удесятеро більша за
    /// інтервал, тож збій БД мусить тривати п'ять хвилин поспіль, щоб
    /// вплинути хоч на щось. Помилка не ковтається мовчки: вона йде в лог.
    /// </remarks>
    /// <param name="jobId">Задача.</param>
    /// <param name="jobCancel">Токен задачі — скасовується, коли рядок закрили ззовні як <c>Cancelled</c>.</param>
    /// <param name="ct">Зупинка самого насоса.</param>
    private async Task HeartbeatLoopAsync(string jobId, CancellationTokenSource jobCancel, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(
            services.GetService<JobHeartbeatSettings>()?.Interval ?? IJobProgressStore.HeartbeatInterval);

        // ⚠ Зупинка через Dispose, а не через токен у WaitForNextTickAsync:
        // токен змусив би метод кинути OperationCanceledException рівно в
        // `finally`, а Dispose просто повертає false — цикл виходить негайно
        // й тихо, не чекаючи решти тридцяти секунд.
        using var stop = ct.Register(timer.Dispose);

        while (await timer.WaitForNextTickAsync(CancellationToken.None).ConfigureAwait(false))
        {
            try
            {
                using var scope = services.CreateScope();

                var store = scope.ServiceProvider.GetService<IJobProgressStore>();
                if (store is null)
                {
                    return;
                }

                var alive = await store.HeartbeatAsync(
                        jobId,
                        scope.ServiceProvider.GetRequiredService<IClock>().UtcNow,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                // ⚠ Рядок уже не активний — його закрили ЗЗОВНІ. Лише
                // `Cancelled` означає «зупинись» (скасування з іншого інстанса,
                // де цієї задачі в пам'яті немає). `Failed` від прибирання після
                // довгого збою БД — не прохання: задача жива, доробить і
                // запише справжній результат.
                if (!alive
                    && await store.FindAsync(jobId, CancellationToken.None).ConfigureAwait(false)
                        is { State: CancelledState })
                {
                    LogJobCancelledElsewhere(logger, jobId);
                    await jobCancel.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
            catch (Exception ex)
            {
                LogHeartbeatFailed(logger, jobId, ex);
            }
        }
    }

    /// <summary>Скільки РЕТРАЇВ уже було — з даних триґера, що щойно відпрацював.</summary>
    private static int CurrentAttempt(IJobExecutionContext context)
    {
        var map = context.Trigger?.JobDataMap;

        return map is not null
               && map.ContainsKey(QuartzJobScheduler.RetryAttemptKey)
               && int.TryParse(
                   map.GetString(QuartzJobScheduler.RetryAttemptKey), out var attempt)
            ? attempt
            : 0;
    }

    /// <summary>
    /// Кореляція прогону: триґер (ретрай, ручний перезапуск) → задача
    /// (постановка) → нова (розклад: нічний прогін не має запиту-причини).
    /// </summary>
    private static string CorrelationOf(IJobExecutionContext context)
    {
        foreach (var map in new[] { context.Trigger?.JobDataMap, context.JobDetail.JobDataMap })
        {
            if (map is not null
                && map.ContainsKey(QuartzJobScheduler.CorrelationKey)
                && map.GetString(QuartzJobScheduler.CorrelationKey) is { Length: > 0 } id)
            {
                return id;
            }
        }

        return Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Планує новий одноразовий триґер того самого <c>JobKey</c> з
    /// експоненційним відступом і пише в прогрес, ЩО задача повторює спробу.
    /// </summary>
    private async Task ScheduleRetryAsync(
        IJobExecutionContext context, int attempt, string correlationId, IJobProgressStore? progress,
        IClock clock, Exception ex)
    {
        var jobId = context.JobDetail.Key.Name;
        var nextAttempt = attempt + 1;

        // 30 с, 60 с, 120 с — JobRetryPolicy.DelayBefore (спільно з JobWorker).
        var delay = JobRetryPolicy.DelayBefore(nextAttempt);

        // ⚠ Час — через IClock, не DateTimeOffset.UtcNow: годинник підмінний
        // у тестах (ForbiddenApiTests пильнує саме прямі виклики годинника
        // поза SystemClock), і ретрай мусить бути так само відтворюваним, як
        // решта логіки часу в системі.
        var startAt = new DateTimeOffset(clock.UtcNow, TimeSpan.Zero).Add(delay);

        var trigger = TriggerBuilder.Create()
            .ForJob(context.JobDetail.Key)
            .WithIdentity($"{jobId}-retry{nextAttempt}-{Guid.NewGuid():N}-trigger")
            .UsingJobData(QuartzJobScheduler.RetryAttemptKey, nextAttempt.ToString(CultureInfo.InvariantCulture))
            // Повтор — та сама задача, отже та сама кореляція (і для розкладу теж).
            .UsingJobData(QuartzJobScheduler.CorrelationKey, correlationId)
            .StartAt(startAt)
            .Build();

        await context.Scheduler.ScheduleJob(trigger, CancellationToken.None).ConfigureAwait(false);

        if (progress is not null)
        {
            // ⚠ Q-326: той самий структурований конверт, що й у решти задач —
            // цей виклик пише напряму в `IJobProgressStore` (не через
            // `IJobProgress`/`ReportKeyAsync`), тож конверт кодується вручну.
            // `ex.Message` лишається НЕ перекладеним параметром (дані, як
            // `run.Status` в `ArchiveJob`) — текст винятку вже такий, яким
            // його сформував код, що його кинув, а не готовий UI-рядок.
            var message = JobRetryPolicy.RetryScheduledMessage(nextAttempt, delay, ex, correlationId);

            // ⛔ Саме тут жила найдорожча частина дефекту, знайденого наскрізною
            // перевіркою (`tools/smoke.ps1`, крок 23). `ex.Message` ішов у
            // конверт як є; українське повідомлення в JSON екранується по шість
            // символів на літеру, тож конверт легко переростав `nvarchar(400)`,
            // і SQL Server відповідав `Msg 2628`. Виняток летів із блоку
            // `catch`: стан НІКОЛИ не ставав `Failed` (клієнт вічно бачив
            // «виконується»), а триґер ретраю вже був поставлений рядком вище —
            // задача мовчки перезапускалася кожні 30/60 с і падала знову.
            // Разом із записом губився й текст причини — тобто рівно те, що не
            // влізло.
            await WriteProgressAsync(
                    jobId,
                    () => progress.ReportAsync(
                        jobId,
                        0,
                        message,
                        clock.UtcNow,
                        CancellationToken.None))
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Виконує запис у сховище прогресу так, щоб ЙОГО власний збій не підмінив
    /// собою результат задачі.
    /// </summary>
    /// <param name="jobId">Ідентифікатор задачі — для журналу.</param>
    /// <param name="write">Сам запис.</param>
    /// <remarks>
    /// ⛔ Прогрес — це РОЗПОВІДЬ про задачу, а не сама задача. Виняток звідси
    /// раніше підміняв справжню причину провалу (і скасовував перехід у
    /// <c>Failed</c>) — той самий принцип, що вже діє для насоса биття серця й
    /// для резолву повідомлення (<c>JobProgressMessageResolver</c>): збій
    /// спостерігача йде в журнал, а не в результат.
    ///
    /// ⚠ Ковтається саме ЗАПИС, не задача: <c>JobExecutionException</c> нижче
    /// кидається в будь-якому разі, тож Quartz і ручний перезапуск бачать
    /// провал так само, як бачили.
    /// </remarks>
    private async Task WriteProgressAsync(string jobId, Func<Task> write)
    {
        try
        {
            await write().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Причина — у ⛔ вище: результат задачі важливіший за запис про нього.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogProgressWriteFailed(logger, jobId, ex);
        }
    }

    /// <summary>Знаходить задачу за повним іменем типу.</summary>
    /// <summary>Фіксує затримку «постановка → старт», якщо мітка є (<c>ФВ-12.2</c>).</summary>
    /// <remarks>
    /// ⚠ Мітки немає у ДВОХ законних випадках, і обидва — не помилка: задача
    /// за розкладом (її ніхто не «ставив», момент задає крон) і деталь,
    /// збережена до появи цього ключа. Тоді нічого не пишемо: нуль у
    /// гістограмі був би не «швидко», а вигадкою.
    ///
    /// ⚠ Від'ємне значення відкидається так само. Воно можливе, коли годинники
    /// інстанса-постановника й інстанса-виконавця розійшлися; від'ємна
    /// затримка в метриці не означає нічого, крім зіпсованого перцентиля.
    /// </remarks>
    private static void RecordStartLatency(
        IJobStartMetrics? metrics, JobDataMap data, IClock clock, string? typeName)
    {
        if (metrics is null || !data.ContainsKey(QuartzJobScheduler.EnqueuedAtKey))
        {
            return;
        }

        var elapsed = clock.UtcNow - new DateTime(
            data.GetLongValue(QuartzJobScheduler.EnqueuedAtKey), DateTimeKind.Utc);

        if (elapsed < TimeSpan.Zero)
        {
            return;
        }

        metrics.RecordStartLatency(elapsed.TotalMilliseconds, typeName ?? "—");
    }

    /// <summary>Знаходить задачу за повним іменем типу (спільно з <c>JobWorker</c>).</summary>
    internal static IBackgroundJob? Resolve(IServiceProvider provider, string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        var type = Type.GetType(typeName)
                   ?? AppDomain.CurrentDomain.GetAssemblies()
                       .Select(a => a.GetType(typeName))
                       .FirstOrDefault(t => t is not null);

        return type is null ? null : provider.GetService(type) as IBackgroundJob;
    }

    private static Task StartAsync(
        IJobProgressStore? store, string jobId, string code, int attempt, string correlationId, IClock clock,
        CancellationToken ct)
        => store is null
            ? Task.CompletedTask
            : store.StartAsync(jobId, code, clock.UtcNow, ct, attempt, correlationId);

    private static Task FinishAsync(
        IJobProgressStore? store, string jobId, string state, string? error, IClock clock, CancellationToken ct,
        string? errorCode = null)
        => store is null ? Task.CompletedTask : store.FinishAsync(jobId, state, error, clock.UtcNow, ct, errorCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Задача {TypeName} ({JobId}) не зареєстрована.")]
    private static partial void LogUnknownJob(ILogger logger, string typeName, string jobId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Задача {JobId} ({TypeName}) завершилася помилкою.")]
    private static partial void LogJobFailed(ILogger logger, string jobId, string typeName, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Задача {JobId} ({TypeName}) впала; заплановано повтор.")]
    private static partial void LogJobRetrying(ILogger logger, string jobId, string typeName, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Задача {JobId} ({TypeName}) пропущена: інший інстанс уже виконує її зараз.")]
    private static partial void LogJobSkippedElsewhere(ILogger logger, string jobId, string typeName);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Задача {JobId} ({TypeName}) не запущена: її скасовано, поки вона стояла в черзі.")]
    private static partial void LogJobSkippedCancelled(ILogger logger, string jobId, string typeName);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Задачу {JobId} скасовано з іншого інстанса; зупиняю її тут.")]
    private static partial void LogJobCancelledElsewhere(ILogger logger, string jobId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Не вдалося записати биття серця задачі {JobId}; наступна спроба за інтервал.")]
    private static partial void LogHeartbeatFailed(ILogger logger, string jobId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Не вдалося записати прогрес задачі {JobId}; на результат самої задачі це не впливає.")]
    private static partial void LogProgressWriteFailed(ILogger logger, string jobId, Exception exception);
}

/// <summary>Перевизначення інтервалу биття серця задачі.</summary>
/// <param name="Interval">Інтервал.</param>
/// <remarks>
/// ⚠ Лише для тестів: у застосунку не реєструється, і діє
/// <see cref="IJobProgressStore.HeartbeatInterval"/>. Параметр через DI, а не
/// статичне поле, щоб паралельні прогони не ділили один стан.
/// </remarks>
public sealed record JobHeartbeatSettings(TimeSpan Interval);

/// <summary>Прогрес, що пишеться у сховище.</summary>
/// <remarks>
/// Прогрес живе в базі, а не в пам'яті: інстансів застосунку кілька, і
/// клієнт, що опитує прогрес, потрапляє не обов'язково на той, який задачу
/// виконує.
/// </remarks>
internal sealed class StoreJobProgress(IJobProgressStore? store, string jobId, IClock clock) : IJobProgress
{
    /// <inheritdoc />
    public Task ReportAsync(int percent, string? message, CancellationToken ct)
        => store is null
            ? Task.CompletedTask
            : store.ReportAsync(jobId, percent, message, clock.UtcNow, ct);
}
