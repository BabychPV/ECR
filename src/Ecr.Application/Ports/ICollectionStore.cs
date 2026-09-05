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

    /// <summary>
    /// Мапінги полів сутності — саме вони несуть <b>оголошену</b> одиницю
    /// джерела.
    /// </summary>
    /// <remarks>
    /// ⚠ Без цього збирач не має з чим порівняти UOM, який джерело повернуло
    /// фактично, — і вимога «зміна UOM атрибута зупиняє збір» (ФВ-16.9,
    /// <c>ECR-INT-0422</c>) лишилася б написаною, але нічиєю.
    /// </remarks>
    public Task<IReadOnlyList<EntityFieldMap>> GetFieldMapsAsync(
        int sourceEntityId, CancellationToken ct);
    /// <summary>
    /// Перелік сутностей збору з ознаками здоров'я.
    /// </summary>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⚠ Перелік потрібен конфігуратору: імена сутностей джерела <b>обираються
    /// зі списку</b>, а не вводяться руками (ФВ-13.13). Друкарська помилка в
    /// шляху AF виявляється не при налаштуванні, а через місяць порожнім
    /// збором.
    /// <para>
    /// ⚠ Разом із кожною сутністю віддається <b>найстаріша непокрита
    /// прогалина</b>. Ознака здоров'я інтеграції — журнал покриття, а не тиша
    /// (ІНТ-3.3): джерело, яке щоночі успішно віддає нуль точок, і джерело,
    /// яке віддає дані, у переліку останніх прогонів виглядають однаково.
    /// </para>
    /// </remarks>
    public Task<IReadOnlyList<SourceEntityStatus>> ListSourceEntitiesAsync(CancellationToken ct);
}

/// <summary>Сутність збору разом зі станом останнього прогону.</summary>
/// <param name="Id">Ідентифікатор сутності.</param>
/// <param name="Code">Код у джерелі.</param>
/// <param name="DisplayName">Підпис для конфігуратора.</param>
/// <param name="EntityPath">Шлях в ієрархії джерела.</param>
/// <param name="Transport">Транспорт джерела (ФВ-11.2).</param>
/// <param name="IsActive">Чи ввімкнено збір.</param>
/// <param name="LastRun">Останній прогін; <c>null</c> — не збирали жодного разу.</param>
/// <param name="OldestGap">Початок найстарішої непокритої прогалини; <c>null</c> — покриття суцільне.</param>
public sealed record SourceEntityStatus(
    int Id,
    string Code,
    string? DisplayName,
    string? EntityPath,
    string Transport,
    bool IsActive,
    CollectionRunStatus? LastRun,
    DateTime? OldestGap);

/// <summary>Підсумок прогону збору.</summary>
/// <param name="FinishedAt">Коли завершився; <c>null</c> — ще виконується.</param>
/// <param name="Status">Статус: <c>Succeeded</c>, <c>Degraded</c>, <c>Failed</c>.</param>
/// <param name="PointsRetrieved">Скільки точок отримано.</param>
public sealed record CollectionRunStatus(DateTime? FinishedAt, string Status, int PointsRetrieved);

