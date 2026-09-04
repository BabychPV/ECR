using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Quartz;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Реалізація <see cref="IBackgroundJobScheduler"/> на Quartz (Apache-2.0).
/// </summary>
/// <remarks>
/// Порт існує саме для того, щоб заміна на Hangfire коштувала день, якщо ІБ
/// погодить LGPL (D-09). Тому специфіка Quartz не має протікати назовні.
///
/// ⚠ <paramref name="schedulerFactory"/> необов'язковий, і це не зручність.
/// Поки планувальник не піднято (Етап 5), реалізація має **існувати і бути
/// зареєстрованою**: без неї не резолвиться <c>RecalculateDocumentHandler</c>,
/// без нього не створюється <c>DocumentsController</c>, і <c>500</c>
/// отримують ВСІ його ендпоінти — включно з поданням, яке черги не потребує
/// взагалі. Одна відсутня реєстрація вимикала цілий контролер, і побачити це
/// можна було лише на живому запиті.
///
/// Тому без фабрики методи черги відмовляють зрозуміло — <c>ECR-SYS-0503</c>, —
/// а все, що черги не потребує, працює.
/// </remarks>
public sealed class QuartzJobScheduler(ISchedulerFactory? schedulerFactory = null) : IBackgroundJobScheduler
{
    private const string UnavailableCode = "ECR-SYS-0503";

    private const string UnavailableMessage =
        "Фонові задачі ще не налаштовані: планувальник вмикається на Етапі 5 (D-09). " +
        "Операція потребує черги і тому недоступна.";

    /// <summary>Чи піднято планувальник.</summary>
    public bool IsConfigured => schedulerFactory is not null;

    /// <inheritdoc />
    public Task<string> EnqueueAsync<TJob>(object? payload, CancellationToken ct)
        where TJob : IBackgroundJob
        => IsConfigured
            ? throw new NotImplementedException(
                "TODO (Етап 5): створити JobDetail з унікальним ключем, покласти payload у JobDataMap " +
                "як JSON, запланувати негайний тригер; повернути ключ як jobId.")
            : throw Unavailable(typeof(TJob).Name);

    /// <inheritdoc />
    public Task ScheduleAsync<TJob>(string cronExpression, object? payload, CancellationToken ct)
        where TJob : IBackgroundJob
        => IsConfigured
            ? throw new NotImplementedException(
                "TODO (Етап 5): CronScheduleBuilder; ідемпотентно за ключем задачі.")
            : throw Unavailable(typeof(TJob).Name);

    /// <inheritdoc />
    public Task CancelAsync(string jobId, CancellationToken ct)
        => IsConfigured
            ? throw new NotImplementedException(
                "TODO (Етап 5): scheduler.DeleteJob за ключем; якщо задача вже виконується — позначити " +
                "скасування через CancellationToken, а не вбивати потік.")
            : throw Unavailable(jobId);

    /// <inheritdoc />
    /// <remarks>
    /// Єдиний метод, що без планувальника **не кидає**: питання «як там
    /// задача» має отримати відповідь, а не помилку — інакше екран прогресу
    /// ламається на порожньому місці.
    /// </remarks>
    public Task<JobStatus> GetStatusAsync(string jobId, CancellationToken ct)
        => IsConfigured
            ? throw new NotImplementedException(
                "TODO (Етап 5): читати з itg.JobProgress, а не з внутрішнього стану Quartz.")
            : Task.FromResult(new JobStatus(jobId, "Unavailable", 0, UnavailableMessage, UnavailableCode));

    private static BusinessRuleException Unavailable(string what)
        => new(UnavailableCode, UnavailableMessage, new Dictionary<string, object?> { ["job"] = what });
}
