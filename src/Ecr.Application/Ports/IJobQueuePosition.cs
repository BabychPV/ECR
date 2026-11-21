namespace Ecr.Application.Ports;

/// <summary>
/// ✎ PERF-10 (D-564 (в), рішення людини 21.11 §6 п. 3): місце задачі перерахунку в черзі й орієнтовний час старту — щоб
/// людина, яка чекає «Перерахувати», бачила не лише «у черзі».
/// </summary>
/// <remarks>
/// Лише черга в базі й лейн перерахунку; режим Quartz (черги в базі немає) — без реалізації, поля стану порожні.
/// </remarks>
public interface IJobQueuePosition
{
    /// <summary>Місце задачі в черзі; <c>null</c> — задача не чекає в лейні перерахунку (виконується, завершена, інший лейн).</summary>
    /// <param name="jobId">Задача.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<JobQueuePosition?> GetAsync(string jobId, CancellationToken ct);
}

/// <summary>Місце в черзі перерахунку.</summary>
/// <param name="Position">
/// Скільки задач буде взято раніше за цю, плюс один (1 — наступна): порядок claim лейна (спершу ручні — по черзі людей, далі
/// FIFO); задачі, що виконуються, не рахуються.
/// </param>
/// <param name="EstimatedStartAt">
/// ОРІЄНТОВНИЙ старт (UTC): медіана тривалості кроку перерахунку за останню добу × кроки попереду ÷ місця глобальної стелі.
/// <c>null</c> — оцінити нема з чого (за добу не завершився жоден крок).
/// </param>
public sealed record JobQueuePosition(int Position, DateTime? EstimatedStartAt);
