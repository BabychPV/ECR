using Ecr.Infrastructure.Jobs;

namespace Ecr.Api.Startup;

/// <summary>
/// ✎ D-575 / P-1123-18 (людина 23.11: «прибрати їх одразу»): на кожному старті — той самий прохід, що й щогодини в
/// <see cref="PeriodStateJob"/>: у не відкритих (<c>Scheduled</c>) місяцях активних проєктів знімаються застарілі помилки
/// формул і результати методологій, щоб після вікна стара видача не чекала годинної задачі станів.
/// </summary>
/// <remarks>⚠ Ніколи не зупиняє старт: будь-який збій — попередження, наступний прогін задачі станів повторить.</remarks>
internal static partial class AwaitingOpenStartupStep
{
    public static async Task RunAsync(IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        try
        {
            var projects = await scope.ServiceProvider.GetRequiredService<PeriodStateJob>()
                .SweepAwaitingOpenAsync(ct)
                .ConfigureAwait(false);
            LogDone(logger, projects);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSkipped(logger, ex.GetType().Name, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "D-575 on start: not-opened months of {Projects} active projects swept (stale formula errors and methodology results).")]
    private static partial void LogDone(ILogger logger, int projects);

    [LoggerMessage(Level = LogLevel.Warning, Message = "D-575 sweep on start skipped ({Reason}): {Detail}. The hourly period-state job repeats it.")]
    private static partial void LogSkipped(ILogger logger, string reason, string detail);
}
