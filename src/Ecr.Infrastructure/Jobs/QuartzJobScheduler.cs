using Ecr.Application.Ports;
using Quartz;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Реалізація <see cref="IBackgroundJobScheduler"/> на Quartz (Apache-2.0).
/// </summary>
/// <remarks>
/// Порт існує саме для того, щоб заміна на Hangfire коштувала день, якщо ІБ
/// погодить LGPL (D-09). Тому специфіка Quartz не має протікати назовні.
/// </remarks>
public sealed class QuartzJobScheduler(ISchedulerFactory schedulerFactory) : IBackgroundJobScheduler
{
    /// <inheritdoc />
    public Task<string> EnqueueAsync<TJob>(object? payload, CancellationToken ct) where TJob : IBackgroundJob
        => throw new NotImplementedException(
            "TODO: створити JobDetail з унікальним ключем, покласти payload у JobDataMap як JSON, " +
            "запланувати негайний тригер; повернути ключ як jobId.");

    /// <inheritdoc />
    public Task ScheduleAsync<TJob>(string cronExpression, object? payload, CancellationToken ct) where TJob : IBackgroundJob
        => throw new NotImplementedException("TODO: CronScheduleBuilder; ідемпотентно за ключем задачі.");

    /// <inheritdoc />
    public Task CancelAsync(string jobId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: scheduler.DeleteJob за ключем; якщо задача вже виконується — позначити скасування " +
            "через CancellationToken, а не вбивати потік.");

    /// <inheritdoc />
    public Task<JobStatus> GetStatusAsync(string jobId, CancellationToken ct)
        => throw new NotImplementedException("TODO: читати з itg.JobProgress, а не з внутрішнього стану Quartz.");
}
