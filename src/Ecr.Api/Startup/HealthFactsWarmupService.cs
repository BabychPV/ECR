using Ecr.Application.Health;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Infrastructure.Persistence;

namespace Ecr.Api.Startup;

/// <summary>
/// Прогріває знімок фактів здоров'я (<c>GET /api/v1/health/facts</c>) у фоні після старту,
/// щоб перше відкриття «Здоров'я системи» не платило за холодні запити (12–16 с).
/// </summary>
/// <remarks>
/// ⚠ Не блокує старт: робота йде після <c>ApplicationStarted</c> окремою задачею, будь-який
/// збій лише в журнал (тип винятку) — прогрів оптимізація, не вимога. Вимикається
/// <c>Health:WarmupOnStart=false</c> (тестові фабрики).
/// </remarks>
public sealed partial class HealthFactsWarmupService(
    IServiceScopeFactory scopes,
    IHostApplicationLifetime lifetime,
    IConfiguration configuration,
    ILogger<HealthFactsWarmupService> logger) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue("Health:WarmupOnStart", defaultValue: true))
        {
            return Task.CompletedTask;
        }

        _ = lifetime.ApplicationStarted.Register(() => _ = Task.Run(WarmAsync));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task WarmAsync()
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<ISystemHealthStore>();
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            stop.CancelAfter(TimeSpan.FromSeconds(60));

            var snapshot = await store.ReadAsync(clock.UtcNow, stop.Token).ConfigureAwait(false);

            // B105: прапор складу HSE400 вимкнено, а документ HSE400 поточного року є — Land мовчки читає довідник.
            // Один рядок на старт (той самий знімок, що прогрівається), а не на кожен запит фактів.
            var calculations = SystemFactsCalculationsDto.Of(
                Hse400CompositionSnapshotLoader.IsEnabled(configuration),
                ConfigurationSourceLabel.Of(configuration, Hse400CompositionSnapshotLoader.EnabledKey),
                snapshot.Hse400DocumentInCurrentYear);
            if (calculations.Hse400CompositionWarning)
            {
                LogHse400CompositionOff(logger, Hse400CompositionSnapshotLoader.EnabledKey);
            }
        }
        catch (Exception ex)
        {
            var kind = ex.GetType().Name;
            LogWarmupSkipped(logger, kind);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{Key} is off, but a published HSE400 template and an HSE400 document for the current year exist: "
                  + "Land reads the gas composition from the registry, not from the HSE400 document. "
                  + "Set the key persistently (site config) and restart.")]
    private static partial void LogHse400CompositionOff(ILogger logger, string key);

    [LoggerMessage(Level = LogLevel.Information, Message = "Health facts warm-up skipped: {ExceptionType}.")]
    private static partial void LogWarmupSkipped(ILogger logger, string exceptionType);
}
