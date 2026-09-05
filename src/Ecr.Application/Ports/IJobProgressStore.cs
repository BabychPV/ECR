// src/Ecr.Application/Ports/IJobProgressStore.cs

namespace Ecr.Application.Ports;

/// <summary>
/// Сховище прогресу фонових задач (<c>itg.JobProgress</c>).
/// </summary>
/// <remarks>
/// ⚠ Прогрес живе в БАЗІ, а не в пам'яті планувальника. Інстансів застосунку
/// кілька, і клієнт, що опитує стан задачі, потрапляє не обов'язково на той,
/// який її виконує: стан у пам'яті відповів би «немає такої» — і екран
/// прогресу показав би помилку на цілком успішній задачі.
/// </remarks>
public interface IJobProgressStore
{
    /// <summary>Реєструє початок задачі.</summary>
    public Task StartAsync(string jobId, string jobCode, DateTime utcNow, CancellationToken ct);

    /// <summary>Оновлює прогрес.</summary>
    public Task ReportAsync(string jobId, int percent, string? message, DateTime utcNow, CancellationToken ct);

    /// <summary>Фіксує завершення.</summary>
    public Task FinishAsync(
        string jobId, string state, string? errorMessage, DateTime utcNow, CancellationToken ct);

    /// <summary>Стан задачі; <c>null</c> — такої немає.</summary>
    public Task<JobStatus?> FindAsync(string jobId, CancellationToken ct);
}
