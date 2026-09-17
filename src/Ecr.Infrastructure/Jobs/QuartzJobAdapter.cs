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
    /// T10 #40).
    /// </summary>
    /// <remarks>
    /// ⚠ Судження, не факт із документа (жоден тікет не називає число):
    /// три ретраї покривають типову транзієнтну відмову (дедлок, обрив
    /// з'єднання з SQL Server) без нескінченного спаму на систематично
    /// зламаній задачі. Значення суто внутрішнє — конфігурації, яку читав би
    /// хтось іззовні, тут немає.
    /// </remarks>
    public const int MaxRetryAttempts = 3;

    /// <summary>
    /// Базова затримка експоненційного відступу: 30 с, 60 с, 120 с.
    /// </summary>
    public static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var jobId = context.JobDetail.Key.Name;
        var typeName = context.JobDetail.JobDataMap.GetString(QuartzJobScheduler.JobCodeKey);
        var payload = context.JobDetail.JobDataMap.GetString(QuartzJobScheduler.PayloadKey);

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

        // ⛔ Q-223 (`Jobs`-секція): job поставлена через ScheduleAsync (крон) —
        // той самий детермінований ключ зареєстрований на КОЖНОМУ інстансі
        // окремо, тож без координації N інстансів виконали б її N разів на
        // один тик. Лок — негайна спроба, без очікування: якщо інший
        // інстанс уже виконує цю саму job, цей тик просто пропускається, а
        // не чекає й не дублює роботу пізніше.
        var isRecurring = context.JobDetail.JobDataMap.ContainsKey(QuartzJobScheduler.RecurringKey)
            && context.JobDetail.JobDataMap.GetString(QuartzJobScheduler.RecurringKey) == "1";

        await using var distributedLock = isRecurring
            ? await SqlDistributedLock.TryAcquireAsync(
                    provider.GetRequiredService<EcrDbContext>().Database.GetConnectionString()
                        ?? throw new InvalidOperationException("У контексту немає рядка підключення."),
                    $"Ecr.Job.{jobId}", context.CancellationToken)
                .ConfigureAwait(false)
            : null;

        if (isRecurring && distributedLock is null)
        {
            LogJobSkippedElsewhere(logger, jobId, typeName ?? "—");
            return;
        }

        await StartAsync(progress, jobId, typeName!, clock, context.CancellationToken).ConfigureAwait(false);

        // ⛔ Биття серця на весь час виконання. Прибирання на старті
        // (`IJobProgressStore.FailStaleAsync`) відрізняє покинуту задачу від
        // чужої живої саме за ним; без биття довга задача, яка не звітує
        // відсотків (імпорт великого файлу), через п'ять хвилин виглядала б
        // покинутою — і перезапуск сусіднього інстанса вбивав би її так само,
        // як до виправлення. Насос живе в СВОЄМУ scope: `DbContext` scoped і
        // не потокобезпечний, а задача в цю мить користується своїм.
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = HeartbeatLoopAsync(jobId, heartbeatStop.Token);

        try
        {
            await job.ExecuteAsync(
                payload,
                new StoreJobProgress(progress, jobId, clock),
                context.CancellationToken).ConfigureAwait(false);

            await FinishAsync(progress, jobId, "Succeeded", null, clock, context.CancellationToken)
                .ConfigureAwait(false);

            // ⚠ Дурабельність (QuartzJobScheduler.EnqueueCoreAsync) існує
            // ЛИШЕ заради ручного перезапуску провалу — успіх її не потребує,
            // і держати деталь задачі в пам'яті планувальника навічно означало
            // б повільну витік пам'яті на кожен успішний прогін.
            await context.Scheduler.DeleteJob(context.JobDetail.Key, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Скасування — не провал: його попросили. Але й не успіх, і стан
            // мусить це розрізняти.
            await FinishAsync(progress, jobId, "Cancelled", null, clock, CancellationToken.None)
                .ConfigureAwait(false);
            await context.Scheduler.DeleteJob(context.JobDetail.Key, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            var attempt = CurrentAttempt(context);

            if (attempt < MaxRetryAttempts)
            {
                // ⚠ Ретрай — НЕ Failed. Клієнт, що опитує стан, має й далі
                // бачити задачу «у виконанні», а не короткий спалах «провалу»,
                // який за кілька секунд сам собою стає «виконується» знову.
                await ScheduleRetryAsync(context, attempt, progress, clock, ex).ConfigureAwait(false);
                return;
            }

            // ⚠ Текст помилки в прогрес — БЕЗ стека (ФВ-6.11, D-11): стек
            // виносить назовні шляхи, імена і подекуди значення.
            await FinishAsync(progress, jobId, "Failed", ex.Message, clock, CancellationToken.None)
                .ConfigureAwait(false);

            LogJobFailed(logger, jobId, typeName ?? "—");

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
    private async Task HeartbeatLoopAsync(string jobId, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(IJobProgressStore.HeartbeatInterval);

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

                await store.HeartbeatAsync(
                        jobId,
                        scope.ServiceProvider.GetRequiredService<IClock>().UtcNow,
                        CancellationToken.None)
                    .ConfigureAwait(false);
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
        var map = context.Trigger.JobDataMap;

        return map.ContainsKey(QuartzJobScheduler.RetryAttemptKey)
               && int.TryParse(
                   map.GetString(QuartzJobScheduler.RetryAttemptKey), out var attempt)
            ? attempt
            : 0;
    }

    /// <summary>
    /// Планує новий одноразовий триґер того самого <c>JobKey</c> з
    /// експоненційним відступом і пише в прогрес, ЩО задача повторює спробу.
    /// </summary>
    private static async Task ScheduleRetryAsync(
        IJobExecutionContext context, int attempt, IJobProgressStore? progress, IClock clock, Exception ex)
    {
        var jobId = context.JobDetail.Key.Name;
        var nextAttempt = attempt + 1;

        // ⚠ 2^(спроба-1) на базову затримку: 30 с, 60 с, 120 с — типовий
        // експоненційний відступ, а не лінійний, щоб транзієнтна відмова
        // джерела (наприклад, SQL Server під навантаженням) мала час
        // розвантажитися, а не отримувала три удари поспіль за секунди.
        var delay = TimeSpan.FromTicks(RetryBaseDelay.Ticks * (1L << (nextAttempt - 1)));

        // ⚠ Час — через IClock, не DateTimeOffset.UtcNow: годинник підмінний
        // у тестах (ForbiddenApiTests пильнує саме прямі виклики годинника
        // поза SystemClock), і ретрай мусить бути так само відтворюваним, як
        // решта логіки часу в системі.
        var startAt = new DateTimeOffset(clock.UtcNow, TimeSpan.Zero).Add(delay);

        var trigger = TriggerBuilder.Create()
            .ForJob(context.JobDetail.Key)
            .WithIdentity($"{jobId}-retry{nextAttempt}-{Guid.NewGuid():N}-trigger")
            .UsingJobData(QuartzJobScheduler.RetryAttemptKey, nextAttempt.ToString(CultureInfo.InvariantCulture))
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
            var envelope = new JobProgressMessageEnvelope(
                "jobs.retryScheduled",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["attempt"] = nextAttempt.ToString(CultureInfo.InvariantCulture),
                    ["max"] = MaxRetryAttempts.ToString(CultureInfo.InvariantCulture),
                    ["delaySeconds"] = delay.TotalSeconds.ToString("0", CultureInfo.InvariantCulture),
                    ["error"] = ex.Message,
                });

            await progress.ReportAsync(
                jobId, 0, JobProgressMessageCodec.Encode(envelope), clock.UtcNow, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Знаходить задачу за повним іменем типу.</summary>
    private static IBackgroundJob? Resolve(IServiceProvider provider, string? typeName)
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
        IJobProgressStore? store, string jobId, string code, IClock clock, CancellationToken ct)
        => store is null ? Task.CompletedTask : store.StartAsync(jobId, code, clock.UtcNow, ct);

    private static Task FinishAsync(
        IJobProgressStore? store, string jobId, string state, string? error, IClock clock, CancellationToken ct)
        => store is null ? Task.CompletedTask : store.FinishAsync(jobId, state, error, clock.UtcNow, ct);

    [LoggerMessage(Level = LogLevel.Error, Message = "Задача {TypeName} ({JobId}) не зареєстрована.")]
    private static partial void LogUnknownJob(ILogger logger, string typeName, string jobId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Задача {JobId} ({TypeName}) завершилася помилкою.")]
    private static partial void LogJobFailed(ILogger logger, string jobId, string typeName);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Задача {JobId} ({TypeName}) пропущена: інший інстанс уже виконує її зараз.")]
    private static partial void LogJobSkippedElsewhere(ILogger logger, string jobId, string typeName);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Не вдалося записати биття серця задачі {JobId}; наступна спроба за інтервал.")]
    private static partial void LogHeartbeatFailed(ILogger logger, string jobId, Exception exception);
}

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
