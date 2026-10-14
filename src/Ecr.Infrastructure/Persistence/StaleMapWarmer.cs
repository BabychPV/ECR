using System.Diagnostics;
using Ecr.Domain.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Прогрів карти застарілості (D-430, LP-45): кладе в спільний кеш карти «*» (будь-який автор - саме той ключ, що
/// бере запит переліку без фільтра автора) для ГАРЯЧИХ періодів, щоб перший читач не платив за повний запит
/// (32 с на 202601). Гарячі = календарний поточний і попередній за <c>IClock</c> плюс два найновіші періоди, що
/// мають актуальний прогін.
/// </summary>
/// <remarks>
/// ⛔ Доступу не торкається: карта не залежить від читача (див. <see cref="StaleResultsMapCache"/>); ключі
/// per-author (фільтр «автор правки») не прогріваються - їх бере лише запит із фільтром автора, власним запитом.
/// Помилка періоду не кешується (кеш кладе лише успіх) і лише йде у Warning; прогрів - оптимізація.
/// Один рядок Information на період: період, мс, рядків (без даних).
/// </remarks>
public sealed partial class StaleMapWarmer(
    IServiceScopeFactory scopes, IMemoryCache cache, IClock clock, StaleCountsEpoch epoch, ILogger<StaleMapWarmer> logger)
{
    /// <summary>Скільки найновіших періодів з актуальним прогоном додається до календарних.</summary>
    private const int LatestRunPeriods = 2;

    /// <summary>Один прохід: прогріває гарячі періоди послідовно; повертає перелік успішно оброблених.</summary>
    /// <param name="ct">Токен скасування (зупинка хоста).</param>
    public async Task<IReadOnlyList<int>> WarmOnceAsync(CancellationToken ct)
    {
        var done = new List<int>();
        List<int> periods;
        try
        {
            periods = await HotPeriodsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPeriodsFailed(logger, ex.GetType().Name);
            return done;
        }

        foreach (var period in periods)
        {
            ct.ThrowIfCancellationRequested();
            var timer = Stopwatch.StartNew();
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<EcrDbContext>();

                // Без scopeFactory: SWR вимкнено, завантаження синхронне й single-flight з читачами.
                var rows = await new StaleResultsMapCache(db, cache, clock, epoch, logger)
                    .WarmAsync(period, ct).ConfigureAwait(false);
                LogWarmed(logger, period, timer.ElapsedMilliseconds, rows ?? -1);
                done.Add(period);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogWarmFailed(logger, period, ex.GetType().Name);
            }
        }

        return done;
    }

    private async Task<List<int>> HotPeriodsAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var current = (now.Year * 100) + now.Month;
        var previous = now.Month == 1 ? ((now.Year - 1) * 100) + 12 : current - 1;
        var result = new List<int> { current, previous };

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EcrDbContext>();
        var withRuns = await db.Database
            .SqlQuery<int>($"""
                SELECT PeriodKey AS [Value]
                FROM calc.CalculationRun
                WHERE Status = N'Current' AND PeriodKey IS NOT NULL
                GROUP BY PeriodKey
                """)
            .OrderByDescending(period => period)
            .Take(LatestRunPeriods)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var period in withRuns)
        {
            if (!result.Contains(period))
            {
                result.Add(period);
            }
        }

        return result;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Прогрів карти застарілості: період {PeriodKey}, {ElapsedMs} мс, рядків {Rows} (-1 - переповнення).")]
    private static partial void LogWarmed(ILogger logger, int periodKey, long elapsedMs, int rows);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Прогрів карти застарілості періоду {PeriodKey} не вдався ({ExceptionType}); кеш не змінено, читач завантажить сам.")]
    private static partial void LogWarmFailed(ILogger logger, int periodKey, string exceptionType);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Прогрів карти застарілості: перелік гарячих періодів не отримано ({ExceptionType}).")]
    private static partial void LogPeriodsFailed(ILogger logger, string exceptionType);
}
