using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;

namespace Ecr.Infrastructure.Jobs;

/// <summary>Налаштування <see cref="EditPathWarmupService"/>, прочитані на реєстрації.</summary>
/// <param name="Enabled"><see cref="EditPathWarmer.EnabledKey"/>.</param>
/// <param name="MaxVersions"><see cref="EditPathWarmer.MaxVersionsKey"/> (уже обмежене).</param>
/// <param name="WarmQuartz">Чи виконує Quartz ЦЬОГО процесу разові задачі (Api в режимі черги <c>Quartz</c>).</param>
/// <param name="WarmFirstEdit">
/// AN-194: чи гріти перше читання зрізу й перший <c>PATCH</c> (<see cref="FirstEditWarmer"/>) — лише процес, що їх
/// обслуговує (Api), і лише за <see cref="FirstEditWarmer.EnabledKey"/>.
/// </param>
public sealed record EditPathWarmupOptions(bool Enabled, int MaxVersions, bool WarmQuartz, bool WarmFirstEdit = false)
{
    /// <summary>Читає налаштування з конфігурації процесу.</summary>
    /// <param name="configuration">Конфігурація.</param>
    /// <param name="warmQuartz">Чи гріти Quartz (вирішує складання процесу, а не ключ).</param>
    /// <param name="servesEdits">Чи обслуговує процес зріз і <c>PATCH</c> (Api); <c>false</c> — прогріву AN-194 немає.</param>
    public static EditPathWarmupOptions Read(IConfiguration configuration, bool warmQuartz, bool servesEdits = false)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new EditPathWarmupOptions(
            EditPathWarmer.ReadEnabled(configuration),
            EditPathWarmer.ReadMaxVersions(configuration),
            warmQuartz,
            servesEdits && FirstEditWarmer.ReadEnabled(configuration));
    }
}

/// <summary>
/// AN-190: запускає <see cref="EditPathWarmer"/> один раз у фоні після старту процесу (Api — завжди; дочірній воркер
/// перерахунку — бо в режимі <c>Executor = Worker</c> задачу виконує саме він).
/// </summary>
/// <remarks>
/// ⚠ Не блокує ні старт, ні готовність: робота йде після <c>ApplicationStarted</c> окремою задачею, будь-який збій
/// лише в журнал (прогрів — оптимізація, не вимога), зупинка хоста скасовує прохід і дочікується його.
/// Вимикається <see cref="EditPathWarmer.EnabledKey"/> = <c>false</c> (тестові фабрики).
/// AN-194: у процесі Api після метаданих той самий прохід гріє перше читання зрізу й перший <c>PATCH</c>
/// (<see cref="FirstEditWarmer"/>, ключ <see cref="FirstEditWarmer.EnabledKey"/>); служба стартує, якщо ввімкнено хоч один.
/// <para>
/// ⚠ Типово УВІМКНЕНО — судження: прогрів обмежений (<see cref="EditPathWarmer.MaxVersionsKey"/>), читає лише
/// метадані шаблону, які перша правка однаково завантажила б, і кладе їх у той самий кеш; виграш (~2–3 с на першій
/// правці після кожного рестарту) отримує користувач, а не адміністратор, який мав би знати про ключ. Так само
/// типово увімкнені <c>Health:WarmupOnStart</c> і <c>Documents:StaleMapWarm:Enabled</c>.
/// </para>
/// </remarks>
/// <param name="scopes">Коренева фабрика scope.</param>
/// <param name="lifetime">Життєвий цикл хоста.</param>
/// <param name="options">Налаштування.</param>
/// <param name="logger">Журнал служби.</param>
/// <param name="warmerLogger">Журнал прогріву.</param>
/// <param name="firstEditLogger">AN-194: журнал прогріву першої правки (<see cref="FirstEditWarmer"/>).</param>
/// <param name="quartz">Фабрика Quartz; береться лише за <see cref="EditPathWarmupOptions.WarmQuartz"/>.</param>
public sealed partial class EditPathWarmupService(
    IServiceScopeFactory scopes,
    IHostApplicationLifetime lifetime,
    EditPathWarmupOptions options,
    ILogger<EditPathWarmupService> logger,
    ILogger<EditPathWarmer> warmerLogger,
    ILogger<FirstEditWarmer>? firstEditLogger = null,
    ISchedulerFactory? quartz = null) : IHostedService
{
    private CancellationTokenSource? stop;
    private Task? run;

    /// <summary>Завершення фонового проходу (для тестів); <c>null</c> — прохід не запускався.</summary>
    public Task? Completion => run;

    /// <summary>Фабрика прогріву; тести підміняють джерело версій, застосунок бере типову.</summary>
    public Func<IServiceScopeFactory, ILogger<EditPathWarmer>, EditPathWarmer> CreateWarmer { get; init; }
        = (factory, log) => new EditPathWarmer(factory, log);

    /// <summary>AN-194: фабрика прогріву першої правки; тести підміняють кроки, застосунок бере типову.</summary>
    public Func<IServiceScopeFactory, FirstEditWarmer> CreateFirstEditWarmer { get; init; }
        = factory => new FirstEditWarmer(factory, firstEditLogger ?? NullLogger<FirstEditWarmer>.Instance);

    /// <summary>Реєструє службу і її налаштування.</summary>
    /// <param name="services">Колекція сервісів.</param>
    /// <param name="configuration">Конфігурація процесу.</param>
    /// <param name="warmQuartz">Чи виконує Quartz цього процесу разові задачі.</param>
    /// <param name="servesEdits">AN-194: процес обслуговує зріз і <c>PATCH</c> (Api).</param>
    public static void Register(IServiceCollection services, IConfiguration configuration, bool warmQuartz, bool servesEdits = false)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(EditPathWarmupOptions.Read(configuration, warmQuartz, servesEdits));
        services.AddHostedService<EditPathWarmupService>();
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled && !options.WarmFirstEdit)
        {
            return Task.CompletedTask;
        }

        _ = lifetime.ApplicationStarted.Register(() =>
        {
            stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            run = Task.Run(() => RunAsync(stop.Token));
        });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (stop is null || run is null)
        {
            return;
        }

        await stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await run.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Зупинка хоста: прохід завершується сам.
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<int> versions = [];
            if (options.Enabled)
            {
                versions = (await CreateWarmer(scopes, warmerLogger)
                    .WarmAsync(options.MaxVersions, options.WarmQuartz ? quartz : null, ct)
                    .ConfigureAwait(false)).WarmedVersions;
            }

            // AN-194: після метаданих — шлях зрізу й запису (EF-запити, JIT); версії — ті, що щойно прогріто.
            if (options.WarmFirstEdit)
            {
                _ = await CreateFirstEditWarmer(scopes).WarmAsync(versions, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Зупинка хоста посеред прогріву — не збій.
        }
        catch (Exception ex)
        {
            LogWarmupFailed(logger, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Прогрів шляху правки не вдався ({ExceptionType}).")]
    private static partial void LogWarmupFailed(ILogger logger, string exceptionType);
}
