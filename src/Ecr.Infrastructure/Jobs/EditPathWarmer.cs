using System.Collections.Concurrent;
using System.Diagnostics;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// AN-190 (AN-151, причина 1): прогрів шляху ПЕРШОЇ правки після старту процесу. Без нього перша правка платила
/// ~2–3 с один раз на процес: перше завантаження знімка версії шаблону (<see cref="IMetadataCache.GetAsync"/>),
/// перше читання графа залежностей (<see cref="ITemplateVersionStore.ListFormulaDependenciesAsync"/> — той самий
/// виклик, що в <c>RecalculationService.RunAsync</c>) і перший прогін задачі Quartz.
/// </summary>
/// <remarks>
/// ⛔ Межі, і чому саме такі:
/// <list type="bullet">
/// <item>Лише метадані шаблону (структура, граф формул) — <b>жодного значення комірки документа</b> й жодного
/// рішення доступу: прогрів не читає <c>doc.CellValue</c>, не кличе служб доступу й не торкається фільтрів безпеки.
/// Кеш, який він наповнює, — той самий, що наповнив би перший користувач, з тим самим ключем ревізії й тим самим
/// скиданням (<see cref="IMetadataCache.InvalidateAsync"/>), тож свіжості не порушує.</item>
/// <item>Версії — лише ті, що діють у ВІДКРИТИХ періодах (<c>Open</c>/<c>Grace</c>) для документів проєкту; версія —
/// за документом (B74 P2: основний шаблон — <c>doc.fn_VersionOf</c>, власний — <c>doc.fn_TemplateVersionOf</c>). Не більше
/// <see cref="MaxVersionsKey"/> версій, найновіші першими.</item>
/// <item>Кожна версія — у власному scope (свіжий <c>DbContext</c>); збій версії — у журнал, далі наступна.</item>
/// <item>Задача Quartz — сирий <see cref="IJob"/> без <c>QuartzJobAdapter</c>: не пише <c>itg.JobProgress</c> і не
/// видна в <c>/jobs</c>; недурабельна — Quartz прибирає її сам після прогону.</item>
/// </list>
/// Чого НЕ покриває: першу компіляцію EF-запитів самого <c>PATCH</c> і першого читання зрізу (для них потрібне
/// «сухе» читання форми PATCH — [debt]).
/// </remarks>
public sealed partial class EditPathWarmer(IServiceScopeFactory scopes, ILogger<EditPathWarmer> logger)
{
    /// <summary>Ключ вмикання прогріву (типово <c>true</c>).</summary>
    public const string EnabledKey = "Startup:WarmEditPath";

    /// <summary>Ключ стелі числа версій шаблону, що прогріваються.</summary>
    public const string MaxVersionsKey = "Startup:WarmEditPathMaxVersions";

    /// <summary>Стеля за замовчуванням.</summary>
    /// <remarks>
    /// Судження: 20 версій покривають усі чинні шаблони живого стенда (одна-дві версії на шаблон у відкритих
    /// періодах), а кожна версія — два запити до метаданих. Понад <see cref="MaxVersionsLimit"/> прогрів став би
    /// фоновим навантаженням, а не прогрівом.
    /// </remarks>
    public const int DefaultMaxVersions = 20;

    /// <summary>Верхня межа стелі: більше значення конфігурації обрізається до неї.</summary>
    public const int MaxVersionsLimit = 200;

    /// <summary>Скільки чекати на прогін задачі Quartz, перш ніж визнати прогрів Quartz невдалим.</summary>
    public static readonly TimeSpan QuartzTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Скільки чекати, поки QuartzHostedService запустить планувальник (тести зменшують).</summary>
    public TimeSpan QuartzStartWait { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Група ключів задач прогріву Quartz.</summary>
    public const string QuartzGroup = "ecr.warmup";

    /// <summary>Чи увімкнено прогрів (<see cref="EnabledKey"/>, типово <c>true</c>).</summary>
    /// <param name="configuration">Конфігурація процесу.</param>
    public static bool ReadEnabled(IConfiguration configuration)
        => configuration.GetValue(EnabledKey, defaultValue: true);

    /// <summary>Стеля версій (<see cref="MaxVersionsKey"/>), обмежена 1…<see cref="MaxVersionsLimit"/>.</summary>
    /// <param name="configuration">Конфігурація процесу.</param>
    public static int ReadMaxVersions(IConfiguration configuration)
        => Math.Clamp(configuration.GetValue(MaxVersionsKey, defaultValue: DefaultMaxVersions), 1, MaxVersionsLimit);

    /// <summary>
    /// Джерело версій: провайдер scope, стеля, токен → ідентифікатори версій. Тести підміняють, щоб перевіряти
    /// прохід без бази; у застосунку — <see cref="OpenPeriodVersionsAsync"/>.
    /// </summary>
    public Func<IServiceProvider, int, CancellationToken, Task<IReadOnlyList<int>>> ListVersions { get; init; }
        = (provider, cap, ct) => OpenPeriodVersionsAsync(provider.GetRequiredService<EcrDbContext>(), cap, ct);

    /// <summary>
    /// Версії шаблону, що діють у відкритих (<c>Open</c>/<c>Grace</c>) періодах проєктів із документами, найновіші
    /// першими, не більше <paramref name="cap"/>.
    /// </summary>
    /// <param name="db">Контекст.</param>
    /// <param name="cap">Стеля.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<IReadOnlyList<int>> OpenPeriodVersionsAsync(EcrDbContext db, int cap, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, 1);

        // ⚠ Стани — числа PeriodState (Open = 1, Grace = 2; конвертер HasConversion<byte>). Версія — за документом
        // (B74 P2): документ без власного шаблону — doc.fn_VersionOf (основний шаблон проєкту, те саме правило, що в
        // IProjectVersionTimeline), із власним — doc.fn_TemplateVersionOf; NULL (без версії) відсікається.
        const byte open = (byte)Ecr.Domain.Enums.PeriodState.Open;
        const byte grace = (byte)Ecr.Domain.Enums.PeriodState.Grace;

        return await db.Database
            .SqlQuery<int>($"""
                SELECT v.TemplateVersionId AS [Value]
                FROM (
                    SELECT DISTINCT
                        CASE WHEN d.TemplateId IS NULL THEN doc.fn_VersionOf(d.ProjectId, p.PeriodKey)
                             ELSE doc.fn_TemplateVersionOf(d.ProjectId, d.TemplateId, p.PeriodKey) END AS TemplateVersionId
                    FROM doc.Period AS p
                    JOIN doc.Document AS d ON d.ProjectId = p.ProjectId
                    WHERE p.State IN ({open}, {grace})
                ) AS v
                WHERE v.TemplateVersionId IS NOT NULL
                """)
            .OrderByDescending(id => id)
            .Take(cap)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>Один прохід прогріву. Не кидає нічого, крім скасування.</summary>
    /// <param name="maxVersions">Стеля версій.</param>
    /// <param name="quartz">Фабрика планувальника, якщо разові задачі виконує Quartz цього процесу; інакше <c>null</c>.</param>
    /// <param name="ct">Скасування (зупинка хоста).</param>
    /// <returns>Підсумок проходу.</returns>
    /// <exception cref="OperationCanceledException">Прогрів скасовано.</exception>
    public async Task<EditPathWarmResult> WarmAsync(int maxVersions, ISchedulerFactory? quartz, CancellationToken ct)
    {
        var cap = Math.Clamp(maxVersions, 1, MaxVersionsLimit);
        var total = Stopwatch.StartNew();

        // Quartz — першим: він не ходить у базу і найдешевший, а перша правка може прийти будь-якої миті.
        long? quartzMs = null;
        if (quartz is not null)
        {
            var timer = Stopwatch.StartNew();
            if (await WarmQuartzAsync(quartz, QuartzTimeout, ct).ConfigureAwait(false))
            {
                quartzMs = timer.ElapsedMilliseconds;
            }
        }

        IReadOnlyList<int> versions;
        try
        {
            var scope = scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                versions = await ListVersions(scope.ServiceProvider, cap, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogVersionsFailed(logger, ex.GetType().Name);
            return new EditPathWarmResult([], 0, quartzMs, total.ElapsedMilliseconds);
        }

        var warmed = new List<int>();
        var failed = 0;
        foreach (var versionId in versions.Take(cap))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var scope = scopes.CreateAsyncScope();
                await using (scope.ConfigureAwait(false))
                {
                    var provider = scope.ServiceProvider;
                    _ = await provider.GetRequiredService<IMetadataCache>().GetAsync(versionId, ct).ConfigureAwait(false);
                    _ = await provider.GetRequiredService<ITemplateVersionStore>()
                        .ListFormulaDependenciesAsync(versionId, ct)
                        .ConfigureAwait(false);
                }

                warmed.Add(versionId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                LogVersionFailed(logger, versionId, ex.GetType().Name);
            }
        }

        var result = new EditPathWarmResult(warmed, failed, quartzMs, total.ElapsedMilliseconds);
        LogWarmed(logger, warmed.Count, failed, quartzMs ?? -1, result.ElapsedMs);
        return result;
    }

    /// <summary>
    /// Проганяє одну порожню задачу Quartz (<see cref="EditPathWarmupJob"/>) і чекає її завершення: перший прогін
    /// платить за старт потоку пулу, фабрику задач і шлях триґера — один раз на процес.
    /// </summary>
    /// <param name="factory">Фабрика планувальника.</param>
    /// <param name="timeout">Скільки чекати на прогін.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns><c>true</c> — задача відпрацювала; <c>false</c> — планувальник не запущено, збій або таймаут.</returns>
    /// <exception cref="OperationCanceledException">Скасовано.</exception>
    public async Task<bool> WarmQuartzAsync(ISchedulerFactory factory, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var name = $"edit-path-{Guid.NewGuid():N}";
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EditPathWarmupJob.Pending[name] = done;
        try
        {
            var scheduler = await factory.GetScheduler(ct).ConfigureAwait(false);

            // ⚠ QuartzHostedService запускає планувальник уже ПІСЛЯ ApplicationStarted (там само, де й цей прогрів),
            // тож на мить старту він ще може стояти: чекаємо обмежено (замір AN-190: без очікування прогрів Quartz
            // пропускався в кожному з трьох стартів).
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                wait.CancelAfter(QuartzStartWait);
                while (!scheduler.IsStarted && !scheduler.IsShutdown && !wait.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(100), wait.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Вичерпано очікування старту — рішення нижче.
                    }
                }
            }

            ct.ThrowIfCancellationRequested();

            // ⛔ Не запущений (дочірній воркер, де Quartz лише зареєстровано) або вже зупинений — задача ніколи б не
            // спрацювала, і прогрів чекав би до таймауту.
            if (!scheduler.IsStarted || scheduler.IsShutdown || scheduler.InStandbyMode)
            {
                LogQuartzSkipped(logger);
                return false;
            }

            var job = JobBuilder.Create<EditPathWarmupJob>().WithIdentity(name, QuartzGroup).Build();
            var trigger = TriggerBuilder.Create().WithIdentity($"{name}-trigger", QuartzGroup).StartNow().Build();
            await scheduler.ScheduleJob(job, trigger, ct).ConfigureAwait(false);

            await done.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogQuartzFailed(logger, ex.GetType().Name);
            return false;
        }
        finally
        {
            EditPathWarmupJob.Pending.TryRemove(name, out _);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Прогрів шляху правки: версій {Versions}, збоїв {Failed}, Quartz {QuartzMs} мс (-1 - не грівся), усього {ElapsedMs} мс.")]
    private static partial void LogWarmed(ILogger logger, int versions, int failed, long quartzMs, long elapsedMs);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Прогрів шляху правки: перелік версій відкритих періодів не отримано ({ExceptionType}); прогрів пропущено.")]
    private static partial void LogVersionsFailed(ILogger logger, string exceptionType);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Прогрів шляху правки: версію {TemplateVersionId} не прогріто ({ExceptionType}); перша правка завантажить її сама.")]
    private static partial void LogVersionFailed(ILogger logger, int templateVersionId, string exceptionType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Прогрів шляху правки: Quartz не запущено в цьому процесі; прогрів Quartz пропущено.")]
    private static partial void LogQuartzSkipped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Прогрів шляху правки: порожня задача Quartz не відпрацювала ({ExceptionType}).")]
    private static partial void LogQuartzFailed(ILogger logger, string exceptionType);
}

/// <summary>Підсумок проходу <see cref="EditPathWarmer.WarmAsync"/>.</summary>
/// <param name="WarmedVersions">Прогріті версії в порядку проходу.</param>
/// <param name="FailedVersions">Скільки версій не прогріто через збій.</param>
/// <param name="QuartzMs">Скільки тривав прогін порожньої задачі Quartz; <c>null</c> — Quartz не грівся.</param>
/// <param name="ElapsedMs">Тривалість усього проходу.</param>
public sealed record EditPathWarmResult(
    IReadOnlyList<int> WarmedVersions, int FailedVersions, long? QuartzMs, long ElapsedMs);

/// <summary>
/// Порожня задача Quartz прогріву (<see cref="EditPathWarmer.WarmQuartzAsync"/>): лише сигналізує про свій прогін.
/// </summary>
/// <remarks>⚠ Без залежностей і без бази: її єдина робота — пройти шлях Quartz «триґер → фабрика → потік пулу».</remarks>
public sealed class EditPathWarmupJob : IJob
{
    /// <summary>Очікувані прогони за ім'ям ключа задачі.</summary>
    internal static readonly ConcurrentDictionary<string, TaskCompletionSource> Pending = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Pending.TryGetValue(context.JobDetail.Key.Name, out var done))
        {
            done.TrySetResult();
        }

        return Task.CompletedTask;
    }
}
