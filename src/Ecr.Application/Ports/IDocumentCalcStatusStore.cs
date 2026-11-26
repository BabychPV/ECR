// src/Ecr.Application/Ports/IDocumentCalcStatusStore.cs
namespace Ecr.Application.Ports;

/// <summary>
/// ✎ D-577 (L3): факти стану розрахунку документа — застарілі місяці, задачі перерахунку в черзі й у роботі, останній
/// актуальний прогін і останній провал. Лише читання; правила (які місяці можна перерахувати, видимість) — у
/// <c>GetDocumentCalcStatusHandler</c>.
/// </summary>
public interface IDocumentCalcStatusStore
{
    /// <summary>Застарілі місяці документа — той самий предикат, що картка (<c>StaleResultsByIdsQuery</c>), по місяцю.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="projectId">Проєкт документа.</param>
    /// <param name="periodKey">Період запиту.</param>
    /// <param name="year">Рік річної розкладки: усі місяці року з актуальним прогоном проєкту; <c>null</c> — лише <paramref name="periodKey"/>.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Застарілі місяці за зростанням (UTC); порожньо — застарілих немає.</returns>
    public Task<IReadOnlyList<StaleMonthFact>> StaleMonthsAsync(
        long documentId, int projectId, int periodKey, int? year, CancellationToken ct);

    /// <summary>Перерахунки документа (<c>IRecalculationJob</c>) у станах <c>Queued</c>/<c>Running</c>.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<ActiveRecalculationFact>> ActiveAsync(long documentId, CancellationToken ct);

    /// <summary>Кінець останнього актуального прогону документа (власного чи проєктного) за періоди.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="projectId">Проєкт документа.</param>
    /// <param name="periodKeys">Періоди.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<DateTime?> CalculatedAtAsync(
        long documentId, int projectId, IReadOnlyCollection<int> periodKeys, CancellationToken ct);

    /// <summary>Останній провалений перерахунок документа, новіший за <paramref name="sinceUtc"/>.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sinceUtc">Нижня межа (не включно), UTC.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<RecalculationFailureFact?> LastFailureAsync(long documentId, DateTime sinceUtc, CancellationToken ct);
}

/// <summary>Застарілий місяць.</summary>
/// <param name="PeriodKey">Місяць.</param>
/// <param name="Since">Відколи, UTC.</param>
public sealed record StaleMonthFact(int PeriodKey, DateTime? Since);

/// <summary>Задача перерахунку документа в черзі чи в роботі.</summary>
/// <param name="JobId">Задача.</param>
/// <param name="State"><c>Queued</c>/<c>Running</c>.</param>
/// <param name="Percent">Прогрес.</param>
/// <param name="TargetKey">Ключ цілі черги в базі; <c>null</c> — Quartz (ціль — у <paramref name="JobId"/>).</param>
/// <param name="CreatedByUserId">Автор; <c>null</c> — системна.</param>
/// <param name="CreatedByDisplayName">Ім'я автора.</param>
/// <param name="QueuedAt">Перша постановка.</param>
/// <param name="StartedAt">Старт виконання; <c>null</c> — у черзі.</param>
/// <param name="StepDone">Ланцюжок року: зроблені місяці до цього кроку.</param>
/// <param name="StepTotal">Ланцюжок року: місяців разом.</param>
/// <param name="CurrentPeriodKey">Місяць кроку (чи період задачі).</param>
public sealed record ActiveRecalculationFact(
    string JobId,
    string State,
    int Percent,
    string? TargetKey,
    int? CreatedByUserId,
    string? CreatedByDisplayName,
    DateTime? QueuedAt,
    DateTime? StartedAt,
    int? StepDone,
    int? StepTotal,
    int? CurrentPeriodKey);

/// <summary>Провалений перерахунок.</summary>
/// <param name="JobId">Задача.</param>
/// <param name="FailedAt">Коли.</param>
/// <param name="ErrorCode">Код каталогу.</param>
/// <param name="CorrelationId">Кореляція.</param>
/// <param name="TargetKey">Ключ цілі; <c>null</c> — Quartz.</param>
/// <param name="CreatedByUserId">Автор; <c>null</c> — системна.</param>
public sealed record RecalculationFailureFact(
    string JobId, DateTime FailedAt, string? ErrorCode, string? CorrelationId, string? TargetKey, int? CreatedByUserId);
