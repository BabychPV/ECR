// src/Ecr.Application/Ports/IDataSourceStore.cs

using Ecr.Domain.Entities.External;

namespace Ecr.Application.Ports;

/// <summary>
/// Читання і запис самих ДЖЕРЕЛ (<c>ext.DataSource</c>) — конфігурації
/// підключення, а не зібраних даних (<c>BE-21</c>, ФВ-14.3).
/// </summary>
/// <remarks>
/// ⚠ Окремий порт, а не метод у <see cref="ICollectionStore"/>. Той порт
/// обслуговує ЗБІР і бачить лише чинні джерела
/// (<see cref="ICollectionStore.FindDataSourceAsync"/> відсіює
/// <c>IsActive = 0</c>) — для збирача це правильно, а для конфігуратора
/// згубно: вимкнене джерело зникло б з екрана, на якому його вмикають назад.
/// </remarks>
public interface IDataSourceStore
{
    /// <summary>Усі джерела за кодом, разом із тим, скільки на них спирається.</summary>
    public Task<IReadOnlyList<DataSourceRow>> ListAsync(CancellationToken ct);

    /// <summary>Джерело для правки; <c>null</c> — немає. Вимкнені теж віддаються.</summary>
    public Task<DataSource?> FindAsync(int dataSourceId, CancellationToken ct);

    /// <summary>Чи зайнятий код іншим джерелом.</summary>
    /// <param name="code">Код джерела.</param>
    /// <param name="exceptId">Джерело, яке саме себе не блокує; <c>null</c> — створення.</param>
    /// <param name="ct">Скасування.</param>
    public Task<bool> IsCodeTakenAsync(string code, int? exceptId, CancellationToken ct);

    /// <summary>Скільки сутностей збору і розкладів спирається на джерело.</summary>
    public Task<DataSourceUsage> CountUsageAsync(int dataSourceId, CancellationToken ct);

    /// <summary>Додає джерело.</summary>
    public void Add(DataSource source);

    /// <summary>Прибирає джерело.</summary>
    public void Remove(DataSource source);
}

/// <summary>Джерело разом із тим, що на нього спирається.</summary>
/// <param name="Source">Саме джерело.</param>
/// <param name="Usage">Скільки сутностей збору і розкладів на ньому висить.</param>
public sealed record DataSourceRow(DataSource Source, DataSourceUsage Usage);

/// <summary>
/// Скільки конфігурації спирається на джерело — рівно те, що робить видалення
/// небезпечним.
/// </summary>
/// <param name="SourceEntities">Сутності збору <c>ext.SourceEntity</c>.</param>
/// <param name="CollectionSchedules">Розклади збору цих сутностей.</param>
public sealed record DataSourceUsage(int SourceEntities, int CollectionSchedules);
