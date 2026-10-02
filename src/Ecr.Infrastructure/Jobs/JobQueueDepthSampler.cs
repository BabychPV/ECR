// src/Ecr.Infrastructure/Jobs/JobQueueDepthSampler.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Observability;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecr.Infrastructure.Jobs;

/// <summary>Налаштування вибірки глибини черги (<c>Jobs:QueueDepth:RefreshSeconds</c>, B5.10).</summary>
/// <param name="Interval">Як часто оновлюється кеш значення gauge.</param>
public sealed record JobQueueDepthOptions(TimeSpan Interval)
{
    /// <summary>Ключ інтервалу, секунди.</summary>
    public const string RefreshSecondsKey = "Jobs:QueueDepth:RefreshSeconds";

    /// <summary>Найменший дозволений інтервал, секунди: частіші запити до черги не мають сенсу.</summary>
    public const int MinRefreshSeconds = 5;

    /// <summary>Умовчання, секунди.</summary>
    public const int DefaultRefreshSeconds = 15;

    /// <summary>Читає налаштування; недійсне значення (нечисло, менше мінімуму) — умовчання.</summary>
    /// <param name="configuration">Конфігурація процесу.</param>
    public static JobQueueDepthOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var seconds = int.TryParse(
                          configuration[RefreshSecondsKey]?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                      && parsed >= MinRefreshSeconds
            ? parsed
            : DefaultRefreshSeconds;

        return new JobQueueDepthOptions(TimeSpan.FromSeconds(seconds));
    }
}

/// <summary>Звідки береться глибина черги; шов для тесту «кеш не б'є в базу на кожне читання».</summary>
public interface IJobQueueDepthSource
{
    /// <summary>Кількість задач черги за парами (лейн, стан) — лише ненульові групи.</summary>
    public Task<IReadOnlyList<QueueDepthPoint>> ReadAsync(CancellationToken ct);
}

/// <summary>Один агрегат по <c>itg.JobProgress</c> (<c>Lane IS NOT NULL</c>, стани <c>Queued</c>/<c>Running</c>).</summary>
/// <remarks>
/// ⚠ Звичайне читання (за RCSI черги — без блокувань, без NOLOCK, як і решта читань черги);
/// відбір іде по префіксу індексу (Lane, State, AvailableAt, JobId).
/// </remarks>
public sealed class DbJobQueueDepthSource(IServiceScopeFactory scopes) : IJobQueueDepthSource
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<QueueDepthPoint>> ReadAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EcrDbContext>();

        var rows = await db.JobProgresses.AsNoTracking()
            .Where(p => p.Lane != null && (p.State == "Queued" || p.State == "Running"))
            .GroupBy(p => new { p.Lane, p.State })
            .Select(g => new { g.Key.Lane, g.Key.State, Count = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(r => new QueueDepthPoint(r.Lane!, r.State, r.Count)).ToList();
    }
}

/// <summary>
/// Раз на <see cref="JobQueueDepthOptions.Interval"/> читає глибину черги й кладе в кеш
/// <see cref="InfrastructureMetrics"/>; gauge <c>ecr.jobs.queue_depth</c> читає лише кеш (B5.10).
/// </summary>
/// <remarks>
/// ⛔ Служба живе в Api (режим черги <c>Database</c>), а НЕ в <c>Ecr.Worker</c>: глибина
/// черги — одне глобальне число, а дочірніх процесів пулу N, і кожен рахував би те саме
/// (N запитів і N дублів ряду). <c>ChildComposition</c> прибирає фонові служби складання
/// Api, тож у воркері gauge існує, але без вимірювань, доки кеш не заповнено.
/// </remarks>
public sealed partial class JobQueueDepthSampler(
    IJobQueueDepthSource source,
    JobQueueDepthOptions options,
    ILogger<JobQueueDepthSampler> logger) : BackgroundService
{
    private static readonly string[] States = ["Queued", "Running"];

    /// <summary>Одне оновлення кешу. Збій бази кеш не чіпає й не кидає: метрика не валить хост.</summary>
    /// <param name="ct">Скасування.</param>
    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var found = await source.ReadAsync(ct).ConfigureAwait(false);

            // Нулі пишемо явно: інакше ряд зникав би, коли черга спорожніла, а не падав до 0.
            var points = new List<QueueDepthPoint>();
            foreach (var lane in JobLanes.All)
            {
                foreach (var state in States)
                {
                    var count = found
                        .Where(p => p.Lane == lane && p.State == state)
                        .Sum(p => p.Count);
                    points.Add(new QueueDepthPoint(lane, state, count));
                }
            }

            InfrastructureMetrics.PublishQueueDepth(points);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Збій вибірки метрики — Warning і наступна спроба, не падіння хоста.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRefreshFailed(logger, ex);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RefreshAsync(stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(options.Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Не вдалося оновити глибину черги задач (ecr.jobs.queue_depth); лишається попереднє значення.")]
    private static partial void LogRefreshFailed(ILogger logger, Exception ex);
}
