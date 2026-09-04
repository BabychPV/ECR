// src/Ecr.Application/Ports/ICollectionStore.cs

using Ecr.Domain.Entities.External;

namespace Ecr.Application.Ports;

/// <summary>
/// Стан і результати збору із зовнішніх джерел.
/// </summary>
/// <remarks>
/// ⚠ <b>Порт уведений за рішенням Q-018 (варіант B).</b> До цього
/// <c>Ecr.Adapters.PiAf.CollectionRunner</c> і <c>CatchUpPlanner</c> були
/// типізовані напряму на <c>EcrDbContext</c>, хоча <c>05-skeleton.md</c> §4
/// дозволяє адаптерам знати лише <c>Domain</c> і <c>Application</c>.
///
/// Джерело істини щодо того, за які інтервали дані вже є, — це
/// <c>itg.CollectionCoverage</c>, а не <c>Watermark</c> у розкладі:
/// watermark — оптимізація, а не стан, і його втрата не має коштувати даних
/// (ER-I-03).
/// </remarks>
public interface ICollectionStore
{
    /// <summary>Сутність джерела; <c>null</c>, якщо її немає або вона вимкнена.</summary>
    public Task<SourceEntity?> FindSourceEntityAsync(int sourceEntityId, CancellationToken ct);

    /// <summary>Джерело — воно визначає транспорт. Вибір транспорту це налаштування, не гілка коду (ФВ-11.2).</summary>
    public Task<DataSource?> FindDataSourceAsync(int dataSourceId, CancellationToken ct);

    /// <summary>Створює <c>itg.CollectionRun</c> і повертає його ідентифікатор.</summary>
    public Task<long> StartRunAsync(
        int sourceEntityId, DateTime fromUtc, DateTime toUtc,
        bool isCatchUp, int? triggeredByUserId, CancellationToken ct);

    /// <summary>Завершує прогін. Відмова джерела — теж завершення, зі статусом і кодом.</summary>
    public Task FinishRunAsync(
        long collectionRunId, string status, int pointsRetrieved,
        string? errorMessage, CancellationToken ct);

    /// <summary>
    /// Upsert точок за природним ключем <c>(SourceEntityId, SourcePath, Timestamp)</c> —
    /// повторний запуск того самого діапазону не дублює даних (ФВ-11.3).
    /// Значення зберігаються <b>в одиниці джерела</b> (ФВ-16.9).
    /// </summary>
    /// <returns>Скільки точок фактично записано.</returns>
    public Task<int> UpsertRawPointsAsync(
        long collectionRunId, int sourceEntityId,
        IReadOnlyList<SourceDataPoint> points, CancellationToken ct);

    /// <summary>Записує покриті інтервали в <c>itg.CollectionCoverage</c>.</summary>
    public Task WriteCoverageAsync(
        long collectionRunId, int sourceEntityId,
        IReadOnlyList<TimeInterval> covered, CancellationToken ct);

    /// <summary>
    /// Покриті інтервали від <paramref name="notBefore"/> — основа для пошуку
    /// прогалин. Ознака здоров'я інтеграції — саме журнал покриття, а не тиша (ІНТ-3.3).
    /// </summary>
    public Task<IReadOnlyList<TimeInterval>> GetCoverageAsync(
        int sourceEntityId, DateTime notBefore, CancellationToken ct);
}
