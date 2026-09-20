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
    /// <summary>Усі розклади за ідентифікатором; лише для читання.</summary>
    public Task<IReadOnlyList<ScheduledSourceEntity>> ListAsync(CancellationToken ct);

    /// <summary>
    /// Розклад для зміни — <b>відстежуваний</b>; <c>null</c> — такого немає.
    /// </summary>
    public Task<ScheduledSourceEntity?> FindAsync(int collectionScheduleId, CancellationToken ct);

    /// <summary>Прибирає розклад; зберігає <see cref="IUnitOfWork"/>.</summary>
    public void Remove(CollectionSchedule schedule);
}

/// <summary>Розклад збору разом із сутністю джерела, якій він належить.</summary>
/// <param name="Schedule">Сам розклад.</param>
/// <param name="SourceEntityCode">Код сутності в джерелі.</param>
/// <param name="SourceEntityName">Підпис сутності; <c>null</c> — каталог джерела його не дав.</param>
public sealed record ScheduledSourceEntity(
    CollectionSchedule Schedule, string SourceEntityCode, string? SourceEntityName);
