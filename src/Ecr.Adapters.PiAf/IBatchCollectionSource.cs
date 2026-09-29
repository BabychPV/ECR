using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;

namespace Ecr.Adapters.PiAf;

/// <summary>
/// Адаптер, що читає кілька атрибутів одним запитом до джерела (P7).
/// </summary>
/// <remarks>
/// ⚠ Необов'язкова здатність, а не зміна <see cref="IExternalDataSource"/>:
/// <see cref="CollectionRunner"/> перевіряє її через <c>is</c> і для адаптера
/// без неї лишається на послідовному <see cref="IExternalDataSource.ReadAsync"/>.
/// Так пакетне читання не зачіпає ні порт застосунку, ні
/// <c>PiSqlClientDataSource</c>.
/// <para>
/// ⛔ Реалізація НЕ ходить у <see cref="ICollectionStore"/>: збирач кличе цей
/// метод паралельно з власними записами в сховище, а сховище — один
/// <c>DbContext</c> на прогін, який паралельних звернень не терпить. Тому
/// джерело передається готовим.
/// </para>
/// </remarks>
public interface IBatchCollectionSource
{
    /// <summary>Читає всі запити; результат — по одному на запит, у тому ж порядку.</summary>
    /// <param name="source">Джерело, вже прочитане збирачем.</param>
    /// <param name="requests">Запити; різні межі часу допустимі.</param>
    /// <param name="ct">Скасування — летить винятком, а не результатом.</param>
    /// <returns>Прочитане або відмова для кожного запиту окремо.</returns>
    public Task<IReadOnlyList<BatchReadItem>> ReadBatchAsync(
        DataSource source, IReadOnlyList<CollectionRequest> requests, CancellationToken ct);
}

/// <summary>Підсумок одного запиту з пакета.</summary>
/// <param name="Collected">Прочитане — з тією ж семантикою, що й у <see cref="IExternalDataSource.ReadAsync"/>.</param>
/// <param name="Error">
/// Виняток, яким <see cref="IExternalDataSource.ReadAsync"/> відмовив би на
/// цей запит; <c>null</c>, якщо <paramref name="Collected"/> заповнено.
/// </param>
/// <remarks>
/// ⚠ Відмова — значенням, а не винятком: відмова одного атрибута пакета не
/// має валити сусідів, прочитаних тим самим запитом.
/// </remarks>
public sealed record BatchReadItem(CollectionResult? Collected, Exception? Error);
