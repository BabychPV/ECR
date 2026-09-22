using Ecr.Application.Common;

namespace Ecr.Application.Ports;

/// <summary>Журнал прогонів збору (<c>itg.CollectionRun</c>, ФВ-5.23) — лише читання.</summary>
public interface ICollectionRunReader
{
    /// <summary>Сторінка прогонів, новіші першими (спадний <c>Id</c>).</summary>
    public Task<PagedResult<CollectionRunView>> ListAsync(
        CollectionRunFilter filter, CursorRequest page, CancellationToken ct);

    /// <summary>Один прогін із текстом помилки й покриттям; <c>null</c> — такого немає.</summary>
    public Task<CollectionRunDetail?> FindAsync(long id, CancellationToken ct);
}

/// <summary>Фільтри журналу; <c>null</c> — без обмеження.</summary>
/// <param name="DataSourceId">З'єднання (<c>ext.DataSource</c>).</param>
/// <param name="SourceEntityId">Сутність збору.</param>
/// <param name="Status">Стан прогону.</param>
/// <param name="FromUtc">Початок прогону не раніше (включно).</param>
/// <param name="ToUtc">Початок прогону раніше (виключно).</param>
public sealed record CollectionRunFilter(
    int? DataSourceId, int? SourceEntityId, string? Status, DateTime? FromUtc, DateTime? ToUtc);

/// <summary>Рядок журналу прогонів.</summary>
/// <param name="Id">Ідентифікатор прогону.</param>
/// <param name="SourceEntityId">Сутність збору.</param>
/// <param name="SourceEntityCode">Код сутності.</param>
/// <param name="SourceEntityName">Назва сутності; <c>null</c> — не задана.</param>
/// <param name="DataSourceId">З'єднання сутності.</param>
/// <param name="DataSourceCode">Код з'єднання.</param>
/// <param name="RangeFrom">Початок зібраного інтервалу (UTC).</param>
/// <param name="RangeTo">Кінець зібраного інтервалу (UTC).</param>
/// <param name="StartedAt">Початок прогону (UTC).</param>
/// <param name="FinishedAt">Завершення; <c>null</c> — ще триває.</param>
/// <param name="DurationMs">Тривалість; <c>null</c> — прогін ще триває.</param>
/// <param name="Status"><c>Running</c>, <c>Succeeded</c>, <c>Degraded</c> або <c>Failed</c>.</param>
/// <param name="PointsRetrieved">Скільки значень отримано.</param>
/// <param name="IsCatchUp">Наздоганяння пропущеного вікна.</param>
/// <param name="HasError">Чи є текст помилки — сам текст лише в деталі.</param>
/// <param name="TriggeredByUserId">Хто запустив; <c>null</c> — за розкладом.</param>
public sealed record CollectionRunView(
    long Id,
    int SourceEntityId,
    string SourceEntityCode,
    string? SourceEntityName,
    int DataSourceId,
    string DataSourceCode,
    DateTime RangeFrom,
    DateTime RangeTo,
    DateTime StartedAt,
    DateTime? FinishedAt,
    long? DurationMs,
    string Status,
    int PointsRetrieved,
    bool IsCatchUp,
    bool HasError,
    int? TriggeredByUserId);

/// <summary>Деталь прогону.</summary>
/// <param name="Run">Той самий рядок, що в переліку.</param>
/// <param name="ErrorMessage">Текст помилки (до 2000 символів); <c>null</c> — без помилки.</param>
/// <param name="Coverage">Інтервали, які цей прогін покрив, за зростанням.</param>
/// <param name="CoverageTruncated">Інтервалів більше за стелю — показано перші.</param>
public sealed record CollectionRunDetail(
    CollectionRunView Run,
    string? ErrorMessage,
    IReadOnlyList<CollectionRunCoverage> Coverage,
    bool CoverageTruncated);

/// <summary>Покритий прогоном інтервал (UTC).</summary>
/// <param name="CoveredFrom">Початок.</param>
/// <param name="CoveredTo">Кінець.</param>
public sealed record CollectionRunCoverage(DateTime CoveredFrom, DateTime CoveredTo);
