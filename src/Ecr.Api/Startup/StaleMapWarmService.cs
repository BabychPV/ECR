using Ecr.Infrastructure.Persistence;

namespace Ecr.Api.Startup;

/// <summary>
/// D-430 (LP-45): прогрів карти застарілості гарячих періодів після старту й періодичний фоновий рефреш, щоб
/// список документів не платив за повний запит (32 с «холодного» першого відкриття).
/// </summary>
/// <remarks>
/// ⚠ Не блокує старт і запити: робота йде після <c>ApplicationStarted</c> окремою задачею; збій лише в журнал.
/// Інтервал за замовчуванням 60 с свідомо МЕНШИЙ за TTL 15 с + вікно SWR 120 с карти: запис не встигає «вмерти»
/// між проходами. Вимикається <c>Documents:StaleMapWarm:Enabled=false</c> (тестові фабрики); Worker цю службу не
/// реєструє. Старт у <c>Validate</c>/<c>Migrate</c> уже пройшов до запуску хоста (<c>StartupSequence</c>).
/// </remarks>
public sealed partial class StaleMapWarmService(
    IServiceScopeFactory scopes,
    IHostApplicationLifetime lifetime,
    IConfiguration configuration,
    ILogger<StaleMapWarmService> logger) : IHostedService
{
    /// <summary>Ключ вмикання.</summary>
    public const string EnabledKey = "Documents:StaleMapWarm:Enabled";

    /// <summary>Ключ інтервалу рефрешу, секунди.</summary>
    public const string IntervalKey = "Documents:StaleMapWarm:IntervalSeconds";

    /// <summary>Мінімальний інтервал, секунди.</summary>
    public const int MinIntervalSeconds = 10;

    private CancellationTokenSource? stop;
    private Task? loop;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue(EnabledKey, defaultValue: true))
        {
            return Task.CompletedTask;
        }

        _ = lifetime.ApplicationStarted.Register(() =>
        {
            stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            loop = Task.Run(() => RunAsync(stop.Token));
        });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (stop is null || loop is null)
        {
            return;
        }

        await stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Зупинка хоста: цикл завершується сам.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Прогрів карти застарілості: збій проходу ({ExceptionType}).")]
    private static partial void LogPassFailed(ILogger logger, string exceptionType);

    private async Task RunAsync(CancellationToken ct)
    {
        var seconds = Math.Max(MinIntervalSeconds, configuration.GetValue(IntervalKey, defaultValue: 60));
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var warmer = scope.ServiceProvider.GetRequiredService<StaleMapWarmer>();
                    _ = await warmer.WarmOnceAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogPassFailed(logger, ex.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Зупинка.
        }
    }
}
