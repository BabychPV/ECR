// src/Ecr.Application/Ports/ICollectionScheduleStore.cs
using Ecr.Domain.Entities.External;

namespace Ecr.Application.Ports;

/// <summary>
/// Розклади збору (<c>ext.CollectionSchedule</c>) для редагування з інтерфейсу
/// (<c>BE-21b</c>, ФВ-14.3).
/// </summary>
/// <remarks>
/// ⚠ Окремий порт, а не метод у <see cref="ICollectionStore"/>: той обслуговує
/// ПРОГІН збору (прогони, покриття, точки) і живе в адаптерах джерела, а цей —
/// конфігурацію розкладу, яку править людина. Спільний порт змусив би адаптер
/// PI AF знати про редагування, якого він не робить.
/// <para>
/// ⛔ Розклад завжди віддається РАЗОМ із кодом сутності джерела: сам по собі він
/// має лише <c>SourceEntityId</c>, і перелік із голими числами не дає відповіді
/// на єдине питання, заради якого його відкривають, — ЩО саме збирається за цим
/// cron.
/// </para>
/// </remarks>
public interface ICollectionScheduleStore
{
    /// <summary>Розклади; лише для читання.</summary>
    /// <param name="dataSourceCode">
    /// Лише розклади сутностей цього з'єднання; <c>null</c> — усі. ⚠ Фільтр у
    /// самому запиті: після стелі переліку він дав би неповну вибірку.
    /// </param>
    /// <param name="ct">Скасування.</param>
    public Task<IReadOnlyList<ScheduledSourceEntity>> ListAsync(string? dataSourceCode, CancellationToken ct);

    /// <summary>
    /// Розклад для зміни — <b>відстежуваний</b>; <c>null</c> — такого немає.
    /// </summary>
    public Task<ScheduledSourceEntity?> FindAsync(int collectionScheduleId, CancellationToken ct);

    /// <summary>
    /// Сутність джерела разом із тим, чи вже має вона розклад;
    /// <c>null</c> — сутності немає.
    /// </summary>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⛔ ОДИН запит на обидва питання створення розкладу: «чи є така сутність»
    /// (404) і «чи розклад у неї вже є» (409). Двома запитами вони роз'їхалися б
    /// у часі, а відповідь на друге — саме те, що робить <c>POST</c>
    /// ідемпотентним у сенсі, який має значення: другий розклад на ту саму
    /// сутність означає ДВА тригери Quartz із тим самим payload, тобто подвійний
    /// збір, якого ніде не видно.
    /// </remarks>
    public Task<SourceEntityScheduling?> FindSourceEntityAsync(int sourceEntityId, CancellationToken ct);

    /// <summary>Додає розклад; зберігає <see cref="IUnitOfWork"/>.</summary>
    public void Add(CollectionSchedule schedule);

    /// <summary>Прибирає розклад; зберігає <see cref="IUnitOfWork"/>.</summary>
    public void Remove(CollectionSchedule schedule);
}

/// <summary>Сутність джерела очима екрана розкладу.</summary>
/// <param name="Code">Код сутності в джерелі.</param>
/// <param name="Name">Підпис сутності; <c>null</c> — каталог джерела його не дав.</param>
/// <param name="ScheduleId">Розклад, який у неї вже є; <c>null</c> — розкладу немає.</param>
/// <param name="DataSourceId">З'єднання, якому належить сутність.</param>
/// <param name="DataSourceCode">Код цього з'єднання.</param>
public sealed record SourceEntityScheduling(
    string Code, string? Name, int? ScheduleId, int DataSourceId, string DataSourceCode);

/// <summary>Розклад збору разом із сутністю джерела, якій він належить.</summary>
/// <param name="Schedule">Сам розклад.</param>
/// <param name="SourceEntityCode">Код сутності в джерелі.</param>
/// <param name="SourceEntityName">Підпис сутності; <c>null</c> — каталог джерела його не дав.</param>
/// <param name="DataSourceId">З'єднання, якому належить сутність.</param>
/// <param name="DataSourceCode">Код цього з'єднання.</param>
public sealed record ScheduledSourceEntity(
    CollectionSchedule Schedule,
    string SourceEntityCode,
    string? SourceEntityName,
    int DataSourceId,
    string DataSourceCode);
