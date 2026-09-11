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
    ILogger<RecurringScheduleService> logger) : IHostedService
{
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

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Постановка відкладається до ApplicationStarted: до цього моменту
        // планувальник ще не піднято, а решта hosted-сервісів ще стартує.
        lifetime.ApplicationStarted.Register(() => _ = ScheduleSafelyAsync());

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

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
        await RunPeriodStateOnceAsync(scope.ServiceProvider).ConfigureAwait(false);

        await scheduler
            .ScheduleAsync<Infrastructure.Jobs.NotificationJob>(HourlyCron, null, CancellationToken.None)
            .ConfigureAwait(false);

        // ⚠ Збір ставиться ОКРЕМО на кожну сутність джерела, з її власним
        // cron: у розкладі саме сутність, а не «інтеграція взагалі». Спільна
        // задача на всі джерела означала б, що недоступність одного затримує
        // решту.
        var db = scope.ServiceProvider.GetRequiredService<EcrDbContext>();

        var schedules = await db.CollectionSchedules
            .AsNoTracking()
            .Where(s => s.IsEnabled)
            .OrderBy(s => s.Id)
            .Take(MaxCollectionSchedules)
            .Select(s => new { s.SourceEntityId, s.CronExpression })
            .ToListAsync()
            .ConfigureAwait(false);

        foreach (var schedule in schedules)
        {
            // ⛔ Q-234: тут мав бути порт `ICollectionJob`, а не конкретний
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
            // замість затримки).
            await scheduler
                .ScheduleAsync<ICollectionJob>(
                    schedule.CronExpression,
                    new Application.Integration.CollectionTask(schedule.SourceEntityId, null, null),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        LogSchedulesDone(logger, schedules.Count);
    }

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
        Message = "Старт: постійні розклади поставлено, зокрема збору: {Count}.")]
    private static partial void LogSchedulesDone(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Critical,
        Message = "Старт: постійні розклади НЕ поставлено ({Reason}); застосунок зупиняється.")]
    private static partial void LogSchedulesFailed(ILogger logger, string reason);
    /// <summary>
    /// Виконує вирівнювання станів періодів негайно, у цьому ж процесі.
    /// </summary>
    /// <param name="provider">Область служб старту.</param>
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
    private async Task RunPeriodStateOnceAsync(IServiceProvider provider)
    {
        try
        {
            var connectionString = provider.GetRequiredService<EcrDbContext>().Database.GetConnectionString()
                ?? throw new InvalidOperationException("У контексту немає рядка підключення.");

            await using var distributedLock = await Infrastructure.Jobs.SqlDistributedLock
                .TryAcquireAsync(connectionString, "Ecr.Job.PeriodStateJob:startup", CancellationToken.None)
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
