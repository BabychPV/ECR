using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

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

            _ = await store.ReadAsync(clock.UtcNow, stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var kind = ex.GetType().Name;
            LogWarmupSkipped(logger, kind);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Health facts warm-up skipped: {ExceptionType}.")]
    private static partial void LogWarmupSkipped(ILogger logger, string exceptionType);
}
