using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Api.Startup;

/// <summary>
/// Ставить постійні розклади після того, як застосунок піднявся.
/// </summary>
/// <remarks>
/// ⚠ Саме <b>hosted service</b>, а не крок послідовності старту. Планувальник
/// Quartz піднімається власним hosted service і стає придатним лише після
/// <c>ApplicationStarted</c>; звернення до <c>ISchedulerFactory</c> раніше
/// відбувається на провайдері, який тестовий хост уже встиг закрити, і падає
/// <c>ObjectDisposedException</c> — тобто застосунок не стартує взагалі.
/// <para>
/// ⛔ Помилка постановки розкладів <b>валить старт</b>. Розклад — не
/// оптимізація: без нього вночі мовчазно не відбувається жодна перевірка, і
/// дізнаються про це через місяць по відсутніх зрізах. Застосунок, який
/// піднявся без розкладів, гірший за той, що не піднявся.
/// </para>
/// </remarks>
public sealed partial class RecurringScheduleService(
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    ILogger<RecurringScheduleService> logger,
    IConfiguration configuration) : IHostedService, IDisposable
{
    /// <inheritdoc />
    public void Dispose() => sweepStop.Dispose();

    /// <summary>Cron нічних перевірок: 02:15, поза вікном роботи людей.</summary>
    public const string NightlyCron = "0 15 2 * * ?";

    /// <summary>Cron погодинних задач: на 5-й хвилині кожної години.</summary>
    public const string HourlyCron = "0 5 * * * ?";

    /// <summary>
    /// Стеля розкладів збору.
    /// </summary>
    /// <remarks>
    /// Тисяча активних розкладів — це вже не конфігурація, а наслідок помилки
    /// імпорту; поставити їх усі в планувальник означало б покласти джерело.
    /// </remarks>
    public const int MaxCollectionSchedules = 1_000;

    /// <summary>Як часто прибирати покинуту роботу (U4, U11).</summary>
    /// <remarks>
    /// ⚠ Удесятеро частіше за межу застарілості
    /// (<see cref="IJobProgressStore.StaleAfter"/>, 5 хв): підтвердження
    /// локальної черги мусить устигати задовго до того, як сусідній інстанс
    /// визнає її рядки покинутими. Хвилина — це «покинута задача зникає за
    /// ~6 хв після зупинки процесу» замість «до наступного довгого перезапуску».
    /// </remarks>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>Як часто виконувати ретенцію завершених записів прогресу (аудит P2).</summary>
    public static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);

    /// <summary>Скільки чекати, поки задачі отримають сигнал зупинки.</summary>
    private static readonly TimeSpan InterruptWait = TimeSpan.FromSeconds(10);

    /// <summary>Зупинка циклу прибирання.</summary>
    private readonly CancellationTokenSource sweepStop = new();

    /// <summary>Цикл прибирання; <c>null</c> — ще не запущено.</summary>
    private Task? sweepLoop;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Постановка відкладається до ApplicationStarted: до цього моменту
        // планувальник ще не піднято, а решта hosted-сервісів ще стартує.
        lifetime.ApplicationStarted.Register(() =>
        {
            _ = ScheduleSafelyAsync();
            sweepLoop = SweepLoopAsync(sweepStop.Token);
        });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ U8: сигнал скасування задачам, що виконуються. Без нього задача про
    /// зупинку не дізнавалася зовсім: її обривав кінець процесу по
    /// <c>ShutdownTimeout</c>, а рядок лишався <c>Running</c>. Із сигналом вона
    /// виходить через свою гілку скасування й пише <c>Cancelled</c>.
    /// <para>
    /// ⚠ Саме тут і саме ДО Quartz: hosted services зупиняються у ЗВОРОТНОМУ
    /// порядку реєстрації, а Quartz реєструється раніше (<c>AddEcrInfrastructure</c>
    /// у <c>Program.cs</c> іде перед цим сервісом). Тобто цей <c>StopAsync</c>
    /// відпрацьовує раніше, ніж <c>QuartzHostedService</c> почне
    /// <c>Shutdown(waitForJobsToComplete: true)</c>, і тому Quartz дочікується вже
    /// задач, які зупиняються, а не тих, що рахують далі.
    /// </para>
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await sweepStop.CancelAsync().ConfigureAwait(false);

        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(InterruptWait);

            var interrupted = await InterruptRunningJobsAsync(services, wait.Token).ConfigureAwait(false);
            if (interrupted > 0)
            {
                LogJobsInterrupted(logger, interrupted);
            }
        }
        catch (Exception ex)
        {
            // ⚠ Зупинка не має зависнути чи впасти через планувальник, який уже
            // недоступний: без сигналу задачі однаково дочекається Quartz.
            LogInterruptFailed(logger, ex.Message);
        }

        if (sweepLoop is not null)
        {
            await sweepLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Надсилає скасування всім задачам, що виконуються в цьому процесі (U8).
    /// </summary>
    /// <param name="provider">Кореневий провайдер застосунку.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Скільки задач отримали сигнал.</returns>
    public static async Task<int> InterruptRunningJobsAsync(IServiceProvider provider, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);

        await using var scope = provider.CreateAsyncScope();

        return scope.ServiceProvider.GetService<IBackgroundJobScheduler>() is Infrastructure.Jobs.QuartzJobScheduler quartz
            ? await quartz.InterruptAllAsync(ct).ConfigureAwait(false)
            : 0;
    }

    /// <summary>Періодичне прибирання, поки застосунок живий.</summary>
    private async Task SweepLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        DateTime? lastPurge = null;

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await using var scope = services.CreateAsyncScope();
                    var now = scope.ServiceProvider.GetRequiredService<Domain.Abstractions.IClock>().UtcNow;
                    var purge = lastPurge is null || now - lastPurge >= PurgeInterval;

                    var outcome = await SweepOnceAsync(scope.ServiceProvider, purge, ct).ConfigureAwait(false);

                    if (purge)
                    {
                        lastPurge = now;
                    }

                    if (outcome.Any)
                    {
                        LogSwept(logger, outcome.Jobs, outcome.CollectionRuns, outcome.MaintenanceRuns, outcome.Purged);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // ⚠ Прохід, що впав, не зупиняє цикл: наступний за хвилину
                    // спробує знову, а мовчки загублений цикл — це рівно та
                    // сама «вічна Running», яку він прибирає.
                    LogSweepFailed(logger, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Зупинка застосунку — штатний вихід із циклу.
        }
    }

    /// <summary>
    /// Один прохід: підтвердити локальну чергу, потім прибрати покинуте.
    /// </summary>
    /// <param name="provider">Провайдер області проходу.</param>
    /// <param name="purge">Чи виконувати ретенцію завершених записів прогресу.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⛔ Порядок значущий: спершу підтвердження СВОЄЇ черги, потім прибирання.
    /// У зворотному порядку задача, що просто довго стоїть у черзі цього ж
    /// інстанса, була б визнана покинутою його ж проходом.
    /// </remarks>
    public static async Task<Infrastructure.Jobs.SweepOutcome> SweepOnceAsync(
        IServiceProvider provider, bool purge, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (provider.GetService<IBackgroundJobScheduler>() is Infrastructure.Jobs.QuartzJobScheduler quartz)
        {
            await quartz.KeepAliveLocalJobsAsync(ct).ConfigureAwait(false);
        }

        if (provider.GetService<IJobProgressStore>() is not { } progress)
        {
            return new Infrastructure.Jobs.SweepOutcome(0, 0, 0, 0);
        }

        var sweeper = new Infrastructure.Jobs.AbandonedWorkSweeper(
            provider.GetRequiredService<EcrDbContext>(), progress);

        return await sweeper
            .SweepAsync(
                Infrastructure.Jobs.AbandonedWorkSweeper.AbandonedJobReason,
                provider.GetRequiredService<Domain.Abstractions.IClock>().UtcNow,
                purge,
                ct)
            .ConfigureAwait(false);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Прибирання: задач позначено Failed — {Jobs}; прогонів збору закрито — {CollectionRuns}; "
            + "прогонів обслуговування закрито — {MaintenanceRuns}; завершених записів прогресу видалено — {Purged}.")]
    private static partial void LogSwept(
        ILogger logger, int jobs, int collectionRuns, int maintenanceRuns, int purged);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Прибирання покинутих задач не вдалося ({Reason}); наступна спроба за інтервал.")]
    private static partial void LogSweepFailed(ILogger logger, string reason);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Зупинка: сигнал скасування надіслано задачам, що виконуються, — {Count}.")]
    private static partial void LogJobsInterrupted(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Зупинка: не вдалося надіслати задачам сигнал скасування ({Reason}).")]
    private static partial void LogInterruptFailed(ILogger logger, string reason);

    /// <summary>
    /// Ставить розклади і <b>зупиняє застосунок</b>, якщо не вдалося.
    /// </summary>
    /// <remarks>
    /// ⛔ Виняток тут не можна ані проковтнути, ані лишити незавершеною
    /// задачею. Постановка йде з коллбека <c>ApplicationStarted</c>, який
    /// нічого не чекає: невдача перетворилася б на unobserved task, застосунок
    /// працював би далі — і вночі мовчазно не відбувалася б жодна перевірка.
    /// Дізналися б про це через місяць по відсутніх зрізах.
    /// <para>
    /// Тому провал зупиняє застосунок: він одразу видимий і не дає працювати
    /// системі, у якої половина механізмів вимкнена без попередження.
    /// </para>
    /// </remarks>
    private async Task ScheduleSafelyAsync()
    {
        try
        {
            await ScheduleAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogSchedulesFailed(logger, ex.Message);
            lifetime.StopApplication();
        }
    }

    /// <summary>
    /// Ставить постійні розклади.
    /// </summary>
    /// <remarks>
    /// ⛔ <c>ArchiveJob</c> сюди НЕ входить свідомо. Архівація року — свідомий
    /// крок людини, який змінює фізичне розміщення даних; задача, що робить це
    /// «за розкладом», рано чи пізно заархівує рік, який ще правлять.
    /// </remarks>
    private async Task ScheduleAsync()
    {
        await using var scope = services.CreateAsyncScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<IBackgroundJobScheduler>();

        await scheduler
            .ScheduleAsync<Infrastructure.Jobs.PartitionCheckJob>(NightlyCron, null, CancellationToken.None)
            .ConfigureAwait(false);

        await scheduler
            .ScheduleAsync<Infrastructure.Jobs.ConsistencyCheckJob>(NightlyCron, null, CancellationToken.None)
            .ConfigureAwait(false);

        await scheduler
            .ScheduleAsync<Infrastructure.Jobs.OrphanScanJob>(NightlyCron, null, CancellationToken.None)
            .ConfigureAwait(false);

        // ⛔ Q-2xx (аудит фази 3, звітність). Без цього рядка задача існувала
        // б у коді й ніколи не виконувалася б: rpt.ReportSnapshot/ReportRow
        // росли б вічно — на кожен звіт, кожен проєкт, кожен період, кожну
        // повторну побудову (B16 §4, D-71).
        await scheduler
            .ScheduleAsync<Infrastructure.Jobs.ReportRetentionJob>(NightlyCron, null, CancellationToken.None)
            .ConfigureAwait(false);

        // Класифікація формату суми старих зрізів (рішення 2026-09-21): у запиті
        // переліку її не робить ніхто, тож без цього рядка старі лишалися б `unknown`.
        await scheduler
            .ScheduleAsync<Infrastructure.Jobs.ReportSnapshotFormatJob>(NightlyCron, null, CancellationToken.None)
            .ConfigureAwait(false);

        await scheduler
            .ScheduleAsync<Infrastructure.Jobs.PeriodStateJob>(HourlyCron, null, CancellationToken.None)
            .ConfigureAwait(false);

        // ⛔ І ОДИН РАЗ ОДРАЗУ. Стан періоду — функція від дати, а не подія:
        // поки задача не спрацювала вперше, кожен період лишається в стані, у
        // якому його створили. Після розгортання це означало, що система до
        // години показує «період ще не відкрито» на кожну комірку — тобто не
        // дає працювати, і причина, яку вона називає, неправдива (`A7-24`).
        //
        // ⚠ Те саме стосується будь-якого перезапуску посеред доби: пропущену
        // межу періоду ніхто не наздоганяє, бо cron не має пам'яті.
        await RunPeriodStateOnceAsync(scope.ServiceProvider, logger).ConfigureAwait(false);

        await scheduler
            .ScheduleAsync<Infrastructure.Jobs.NotificationJob>(HourlyCron, null, CancellationToken.None)
            .ConfigureAwait(false);

        // HSE301 A1: повтор підтягування вікон рядків за пізніми даними PI (§4.4).
        await scheduler
            .ScheduleAsync<Infrastructure.Jobs.RowWindowRefetchJob>(HourlyCron, null, CancellationToken.None)
            .ConfigureAwait(false);

        var db = scope.ServiceProvider.GetRequiredService<EcrDbContext>();

        // ⛔ Прогалини 4+5 директиви паритету зі старою системою (Q-327 →
        // Q-331): нічний повний перерахунок — ОПЦІЯ, вимкнена за
        // замовчуванням (`NightlyRecalculationScheduling.EnabledKey`), не
        // завжди-увімкнений режим для всіх проєктів одразу. Коли вимкнена
        // (типовий стан) — нічого не ставиться, і поведінка проду не
        // змінюється.
        var nightlyRecalcCount = await NightlyRecalculationScheduling
            .ScheduleAsync(configuration, db, scheduler, CancellationToken.None)
            .ConfigureAwait(false);

        // ⚠ Збір ставиться ОКРЕМО на кожну сутність джерела, з її власним
        // cron: у розкладі саме сутність, а не «інтеграція взагалі». Спільна
        // задача на всі джерела означала б, що недоступність одного затримує
        // решту.
        // ⚠ З відстеженням: постановка лишає на розкладі стан (`LastError`),
        // і зберігається він одним `SaveChanges` після циклу — лише змінені рядки.
        var schedules = await db.CollectionSchedules
            .Where(s => s.IsEnabled)
            .OrderBy(s => s.Id)
            .Take(MaxCollectionSchedules)
            .ToListAsync()
            .ConfigureAwait(false);

        var localSourceEntityIds = await LocalSourceEntityIdsAsync(db, CancellationToken.None).ConfigureAwait(false);

        var applied = await ApplyCollectionSchedulesAsync(
                schedules,
                localSourceEntityIds,
                scheduler,
                scope.ServiceProvider.GetRequiredService<Application.Integration.CollectionScheduleApplier>(),
                logger,
                scope.ServiceProvider.GetRequiredService<Domain.Abstractions.IClock>().UtcNow,
                CancellationToken.None)
            .ConfigureAwait(false);

        await SaveCollectionScheduleStateAsync(db, logger, CancellationToken.None).ConfigureAwait(false);

        LogSchedulesDone(logger, applied, nightlyRecalcCount);
    }

    /// <summary>
    /// Зберігає стан постановки (<c>LastError</c>), який лишив цикл.
    /// </summary>
    /// <remarks>
    /// ⚠ Збій запису старту НЕ валить: розклади вже стоять у планувальнику, а
    /// стан — лише підказка для інтерфейсу. Але й не мовчить — помилка в журналі.
    /// Конфлікт версії рядка (розклад саме правлять) — той самий випадок.
    /// </remarks>
    public static async Task SaveCollectionScheduleStateAsync(EcrDbContext db, ILogger logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            LogCollectionStateNotSaved(logger, ex.InnerException?.Message ?? ex.Message);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Старт: стан постановки розкладів збору (LastError) НЕ збережено: {Reason}. "
            + "Розклади поставлено; в інтерфейсі стан може бути застарілим.")]
    private static partial void LogCollectionStateNotSaved(ILogger logger, string reason);

    /// <summary>
    /// Текст <c>LastError</c> пропущеного розкладу власної форми (ФВ-12.8).
    /// </summary>
    /// <remarks>
    /// ⚠ Сирий текст, а не ключ каталогу — як і решта <c>LastError</c>: вкладка
    /// розкладу показує його в <c>Code</c> під заголовком «не поставлено».
    /// </remarks>
    public const string LocalEntityScheduleSkipped =
        "Сутність — власна форма ECR (SourceKind = Local): розклад збору для неї заборонено (ФВ-12.8), "
        + "у планувальник не поставлено. Вимкніть або видаліть розклад.";

    /// <summary>
    /// Сутності джерела-власні форми ECR (<see cref="Domain.Enums.RegistrySourceKind.Local"/>),
    /// які мають увімкнений розклад збору.
    /// </summary>
    /// <param name="db">Контекст бази.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⚠ Окремим запитом, а не приєднанням до переліку розкладів: внутрішнє
    /// приєднання мовчки викинуло б розклад без рядка сутності, і той перестав
    /// би бути видимим навіть як пропущений.
    /// </remarks>
    public static async Task<IReadOnlySet<int>> LocalSourceEntityIdsAsync(EcrDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var ids = await db.SourceEntities
            .AsNoTracking()
            .Where(e => e.SourceKind == Domain.Enums.RegistrySourceKind.Local
                        && db.CollectionSchedules.Any(s => s.IsEnabled && s.SourceEntityId == e.Id))
            .Select(e => e.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return ids.ToHashSet();
    }

    /// <summary>
    /// Ставить розклади збору; повертає, скільки поставлено.
    /// </summary>
    /// <remarks>
    /// ⛔ Невалідний cron ОДНОГО розкладу старту НЕ валить: розклад
    /// пропускається з помилкою в журналі, решта ставляться. Відколи cron
    /// редагується з інтерфейсу, інакше одна описка була б «кнопкою зламати
    /// прод із затримкою» — до найближчого перезапуску. Збій САМОГО
    /// планувальника — як і раніше виняток, і старт він валить.
    /// <para>
    /// ⚠ Метод лишає стан НА СУТНОСТЯХ (пропущений — <c>MarkInvalid</c>,
    /// поставлений — <c>ClearError</c>); зберігає його той, хто викликав.
    /// </para>
    /// <para>
    /// ⛔ ФВ-12.8: розклад власної форми (<paramref name="localSourceEntityIds"/>),
    /// заведений ДО заборони, у планувальник НЕ ставиться — але й не вимикається
    /// та не видаляється: це рішення людини. Пропуск не мовчазний — причина в
    /// <c>LastError</c> (її бачить вкладка розкладу) і <c>Warning</c> у журналі.
    /// </para>
    /// </remarks>
    public static async Task<int> ApplyCollectionSchedulesAsync(
        IReadOnlyList<Domain.Entities.External.CollectionSchedule> schedules,
        IReadOnlySet<int> localSourceEntityIds,
        IBackgroundJobScheduler scheduler,
        Application.Integration.CollectionScheduleApplier applier,
        ILogger logger,
        DateTime utcNow,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(localSourceEntityIds);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(applier);

        // ⚠ Рівно стеля — майже напевно обрізано: `.Take()` мовчить про решту.
        if (schedules.Count >= MaxCollectionSchedules)
        {
            LogCollectionSchedulesTruncated(logger, MaxCollectionSchedules);
        }

        var applied = 0;

        foreach (var schedule in schedules)
        {
            if (schedule.IsEnabled && localSourceEntityIds.Contains(schedule.SourceEntityId))
            {
                LogCollectionLocalEntitySkipped(logger, schedule.Id, schedule.SourceEntityId);
                schedule.MarkInvalid(LocalEntityScheduleSkipped, utcNow);
                continue;
            }

            if (!scheduler.IsValidCron(schedule.CronExpression, out var cronError))
            {
                LogCollectionCronInvalid(
                    logger, schedule.SourceEntityId, schedule.CronExpression, cronError ?? string.Empty);

                // Журнал читає оператор, а cron правив адміністратор інтеграції:
                // без стану в рядку пропущений розклад з інтерфейсу не видно.
                schedule.MarkInvalid(
                    string.IsNullOrWhiteSpace(cronError) ? "Invalid cron expression." : cronError, utcNow);
                continue;
            }

            // ⛔ Q-235: тут мав бути порт `ICollectionJob`, а не конкретний
            // клас `Infrastructure.Jobs.CollectionJob`. DI реєструє задачу
            // ЛИШЕ під портом (`DependencyInjection.cs`:
            // `services.AddScoped<ICollectionJob, Jobs.CollectionJob>()`) —
            // так само, як і ручний запуск «зібрати зараз»
            // (`IntegrationHandlers.cs`: `EnqueueAsync<ICollectionJob>`).
            // `QuartzJobAdapter.Resolve` бере код задачі з
            // `typeof(TJob).FullName` і питає ним контейнер: конкретний клас,
            // не зареєстрований сам собою, контейнер не віддає — `Resolve`
            // мовчки повертає `null`, і `Execute` падає РАНІШЕ, ніж встигає
            // хоч раз записати щось у `itg.JobProgress`. Тому щотиковий збір
            // за розкладом (crontab на кожну `ext.CollectionSchedule`) не
            // відбувався ЖОДНОГО разу: ні ретраїв, ні `itg.CollectionRun`, ні
            // рядка в зведенні `NotificationJob` — збір мовчав місяцями, а не
            // «затримувався» (ФВ-11.3 порушено найгіршим способом: тиша
            // замість затримки). Тепер тип і payload знає лише
            // `CollectionScheduleApplier` — той самий, що його кличе редагування.
            await applier.ApplyAsync(schedule, ct).ConfigureAwait(false);
            schedule.ClearError();
            applied++;
        }

        return applied;
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Старт: розклад збору сутності {SourceEntityId} ПРОПУЩЕНО — невалідний cron "
            + "«{CronExpression}»: {CronError}. Збір за ним не відбувається, доки cron не виправлять.")]
    private static partial void LogCollectionCronInvalid(
        ILogger logger, int sourceEntityId, string cronExpression, string cronError);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Старт: розклад збору {CollectionScheduleId} сутності {SourceEntityId} ПРОПУЩЕНО — "
            + "сутність є власною формою ECR (SourceKind = Local), розклад для неї заборонено (ФВ-12.8). "
            + "Розклад не вимкнено й не видалено: це рішення людини.")]
    private static partial void LogCollectionLocalEntitySkipped(
        ILogger logger, int collectionScheduleId, int sourceEntityId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Старт: увімкнених розкладів збору не менше за стелю {Max} — перелік обрізано, "
            + "решта розкладів НЕ поставлена.")]
    private static partial void LogCollectionSchedulesTruncated(ILogger logger, int max);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Старт: стани періодів вирівняно за датами.")]
    private static partial void LogPeriodStateAligned(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Старт: вирівняти стани періодів не вдалося ({Reason}); повторить погодинна задача.")]
    private static partial void LogPeriodStateFailed(ILogger logger, string reason);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Старт: вирівнювання станів періодів пропущено — інший інстанс уже виконує його зараз.")]
    private static partial void LogPeriodStateSkippedElsewhere(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Старт: постійні розклади поставлено, зокрема збору: {Count}; "
            + "нічного перерахунку: {NightlyRecalcCount} проєктів (0 — опція вимкнена).")]
    private static partial void LogSchedulesDone(ILogger logger, int count, int nightlyRecalcCount);

    [LoggerMessage(
        Level = LogLevel.Critical,
        Message = "Старт: постійні розклади НЕ поставлено ({Reason}); застосунок зупиняється.")]
    private static partial void LogSchedulesFailed(ILogger logger, string reason);
    /// <summary>
    /// Виконує вирівнювання станів періодів негайно, у цьому ж процесі.
    /// </summary>
    /// <param name="provider">Область служб старту.</param>
    /// <param name="logger">Журнал старту.</param>
    /// <remarks>
    /// ⚠ Не через планувальник, а прямим викликом: постановка «виконати зараз»
    /// у Quartz — це ще один тригер, який треба чистити, і він виконався б уже
    /// після того, як перший користувач відкрив документ.
    ///
    /// ⚠ Невдача тут НЕ валить старт, на відміну від постановки розкладів.
    /// Різниця по суті: розклад, якого немає, мовчки не працює вічно; а це
    /// разове вирівнювання, яке за годину повторить сама задача.
    ///
    /// ⛔ Q-223 (`Jobs`-секція): виклик прямий, тобто НЕ проходить через
    /// <see cref="Infrastructure.Jobs.QuartzJobAdapter"/> і не бере
    /// міжінстансовий лок автоматично. Якщо кілька інстансів стартують
    /// близько одне до одного (типово при rolling-розгортанні), кожен
    /// намагається вирівняти ті самі періоди одночасно — той самий лок тут
    /// узято явно, тим самим ресурсом, яким узяв би собі
    /// <see cref="Infrastructure.Jobs.QuartzJobScheduler.ScheduleAsync{TJob}"/>
    /// для цієї ж задачі.
    /// </remarks>
    public static async Task RunPeriodStateOnceAsync(IServiceProvider provider, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            var connectionString = provider.GetRequiredService<EcrDbContext>().Database.GetConnectionString()
                ?? throw new InvalidOperationException("У контексту немає рядка підключення.");

            await using var distributedLock = await Infrastructure.Jobs.SqlDistributedLock
                .TryAcquireAsync(
                    connectionString,
                    // ⛔ Та сама назва, що бере адаптер на погодинний тик (payload
                    // `null` — як у постановці розкладу вище). Окрема назва
                    // (`…:startup`) не конкурувала з тиком, і обидва бігли паралельно.
                    Infrastructure.Jobs.QuartzJobScheduler.RecurringLockName<Infrastructure.Jobs.PeriodStateJob>(null),
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (distributedLock is null)
            {
                LogPeriodStateSkippedElsewhere(logger);
                return;
            }

            var job = provider.GetRequiredService<Infrastructure.Jobs.PeriodStateJob>();

            await job.ExecuteAsync(null, new NullProgress(), CancellationToken.None).ConfigureAwait(false);

            LogPeriodStateAligned(logger);
        }
        catch (Exception ex)
        {
            LogPeriodStateFailed(logger, ex.Message);
        }
    }

    /// <summary>Прогрес, який нікуди не пише: разовий старт нікого не цікавить.</summary>
    private sealed class NullProgress : IJobProgress
    {
        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }

}
