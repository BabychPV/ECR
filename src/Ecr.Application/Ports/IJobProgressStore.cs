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
    /// <summary>
    /// Фіксує ПОСТАНОВКУ задачі в чергу.
    /// </summary>
    /// <param name="jobId">Ідентифікатор задачі.</param>
    /// <param name="jobCode">Код задачі.</param>
    /// <param name="utcNow">Момент постановки в UTC.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⚠ Окремий крок від <see cref="StartAsync"/>, і не з педантизму. Між
    /// відповіддю <c>202</c> з <c>jobId</c> і стартом задачі минає час; без
    /// цього запису клієнт, який одразу опитує стан, отримує <c>404</c> на
    /// задачу, яку щойно прийняли, — і вважає, що вона загубилася.
    /// </remarks>
    /// <param name="createdByUserId">Хто поставив задачу; <c>null</c> — системна (Q-156).</param>
    public Task QueueAsync(
        string jobId, string jobCode, DateTime utcNow, CancellationToken ct, int? createdByUserId = null);

    public Task StartAsync(string jobId, string jobCode, DateTime utcNow, CancellationToken ct);

    /// <summary>Оновлює прогрес.</summary>
    public Task ReportAsync(string jobId, int percent, string? message, DateTime utcNow, CancellationToken ct);

    /// <summary>Фіксує завершення.</summary>
    public Task FinishAsync(
        string jobId, string state, string? errorMessage, DateTime utcNow, CancellationToken ct);

    /// <summary>Стан задачі; <c>null</c> — такої немає.</summary>
    public Task<JobStatus?> FindAsync(string jobId, CancellationToken ct);

    /// <summary>Хто поставив задачу; <c>null</c> — системна, або такої немає (Q-156).</summary>
    public Task<int?> GetCreatedByUserIdAsync(string jobId, CancellationToken ct);

    /// <summary>Останні задачі, найновіші перші.</summary>
    /// <param name="limit">Скільки повернути.</param>
    /// <param name="ct">Скасування.</param>
    public Task<IReadOnlyList<JobSummary>> ListRecentAsync(int limit, CancellationToken ct);

    /// <summary>
    /// Позначає застарілі <c>Running</c>/<c>Queued</c> задачі <c>Failed</c>.
    /// </summary>
    /// <param name="reason">Причина, що йде в <c>Error</c>.</param>
    /// <param name="utcNow">Момент позначення в UTC.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Скільки записів позначено.</returns>
    /// <remarks>
    /// ⛔ Викликається один раз при СТАРТІ застосунку. Задача, яку процес
    /// виконував у момент падіння, лишається `Running` у базі назавжди — сам
    /// процес, який мав позначити її `Failed`, уже не існує. Без цього методу
    /// такий запис показує оператору задачу, що «виконується» місяцями.
    /// </remarks>
    public Task<int> FailStaleAsync(string reason, DateTime utcNow, CancellationToken ct);
}
