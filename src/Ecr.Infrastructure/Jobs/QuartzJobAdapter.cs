using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Місток між Quartz і <see cref="IBackgroundJob"/>.
/// </summary>
/// <remarks>
/// ⚠ Існує саме щоб специфіка Quartz **не протікала в задачі**. Порт
/// <c>IBackgroundJobScheduler</c> уведений заради того, щоб заміна
/// планувальника коштувала день (D-09); якби кожна задача реалізовувала
/// <c>IJob</c>, заміна означала б переписати їх усі.
/// <para>
/// Задача створюється в СВОЄМУ scope: вона працює з <c>DbContext</c>, а той
/// scoped. Виконання в кореневому провайдері дало б один контекст на всі
/// прогони — і перший же паралельний прогін зіпсував би стан другого.
/// </para>
/// </remarks>
[DisallowConcurrentExecution]
public sealed partial class QuartzJobAdapter(
    IServiceProvider services, ILogger<QuartzJobAdapter> logger) : IJob
{
    /// <inheritdoc />
    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var jobId = context.JobDetail.Key.Name;
        var typeName = context.JobDetail.JobDataMap.GetString(QuartzJobScheduler.JobCodeKey);
        var payload = context.JobDetail.JobDataMap.GetString(QuartzJobScheduler.PayloadKey);

        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;

        var job = Resolve(provider, typeName);
        if (job is null)
        {
            // ⛔ Невідома задача — гучна відмова, не тиша. Запис у черзі, який
            // нікому виконувати, інакше просто зникав би: клієнт бачив би
            // «виконується» вічно.
            LogUnknownJob(logger, typeName ?? "—", jobId);
            throw new JobExecutionException($"Задача «{typeName}» не зареєстрована.");
        }

        var progress = provider.GetService<IJobProgressStore>();
        var clock = provider.GetRequiredService<IClock>();

        await StartAsync(progress, jobId, typeName!, clock, context.CancellationToken).ConfigureAwait(false);

        try
        {
            await job.ExecuteAsync(
                payload,
                new StoreJobProgress(progress, jobId, clock),
                context.CancellationToken).ConfigureAwait(false);

            await FinishAsync(progress, jobId, "Succeeded", null, clock, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Скасування — не провал: його попросили. Але й не успіх, і стан
            // мусить це розрізняти.
            await FinishAsync(progress, jobId, "Cancelled", null, clock, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // ⚠ Текст помилки в прогрес — БЕЗ стека (ФВ-6.11, D-11): стек
            // виносить назовні шляхи, імена і подекуди значення.
            await FinishAsync(progress, jobId, "Failed", ex.Message, clock, CancellationToken.None)
                .ConfigureAwait(false);

            LogJobFailed(logger, jobId, typeName ?? "—");
            throw new JobExecutionException(ex, refireImmediately: false);
        }
    }

    /// <summary>Знаходить задачу за повним іменем типу.</summary>
    private static IBackgroundJob? Resolve(IServiceProvider provider, string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        var type = Type.GetType(typeName)
                   ?? AppDomain.CurrentDomain.GetAssemblies()
                       .Select(a => a.GetType(typeName))
                       .FirstOrDefault(t => t is not null);

        return type is null ? null : provider.GetService(type) as IBackgroundJob;
    }

    private static Task StartAsync(
        IJobProgressStore? store, string jobId, string code, IClock clock, CancellationToken ct)
        => store is null ? Task.CompletedTask : store.StartAsync(jobId, code, clock.UtcNow, ct);

    private static Task FinishAsync(
        IJobProgressStore? store, string jobId, string state, string? error, IClock clock, CancellationToken ct)
        => store is null ? Task.CompletedTask : store.FinishAsync(jobId, state, error, clock.UtcNow, ct);

    [LoggerMessage(Level = LogLevel.Error, Message = "Задача {TypeName} ({JobId}) не зареєстрована.")]
    private static partial void LogUnknownJob(ILogger logger, string typeName, string jobId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Задача {JobId} ({TypeName}) завершилася помилкою.")]
    private static partial void LogJobFailed(ILogger logger, string jobId, string typeName);
}

/// <summary>Прогрес, що пишеться у сховище.</summary>
/// <remarks>
/// Прогрес живе в базі, а не в пам'яті: інстансів застосунку кілька, і
/// клієнт, що опитує прогрес, потрапляє не обов'язково на той, який задачу
/// виконує.
/// </remarks>
internal sealed class StoreJobProgress(IJobProgressStore? store, string jobId, IClock clock) : IJobProgress
{
    /// <inheritdoc />
    public Task ReportAsync(int percent, string? message, CancellationToken ct)
        => store is null
            ? Task.CompletedTask
            : store.ReportAsync(jobId, percent, message, clock.UtcNow, ct);
}
