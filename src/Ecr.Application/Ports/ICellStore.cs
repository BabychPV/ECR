// src/Ecr.Application/Ports/ICellStore.cs

using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Доступ до комірок. Ховає фізичну модель зберігання: нормалізовану або
/// гібридну (D-21). Use-case про різницю не знає.
/// </summary>
public interface ICellStore
{
    /// <summary>
    /// Зріз таблиці за період. Порожні комірки <b>не повертаються</b> — клієнт
    /// бере <c>ColumnDef.DefaultValue</c> (ФВ-3.8).
    /// Бюджет: p95 &lt; 600 мс на 500×60 (tz/08 §8.2).
    /// </summary>
    public Task<IReadOnlyList<CellRecord>> ReadSliceAsync(long tableInstanceId, CancellationToken ct);

    /// <summary>
    /// Зрізи кількох таблиць ОДНИМ запитом; екземпляр без жодної непорожньої
    /// комірки в результат не потрапляє (шукай його ключ через
    /// <see cref="IReadOnlyDictionary{TKey,TValue}.TryGetValue"/>, а не
    /// індексатор).
    /// </summary>
    /// <remarks>
    /// ⛔ Q-165 (аудит фази 2, продуктивність). <see cref="ReadSliceAsync"/> у
    /// циклі по таблицях документа — це похід у базу на кожну з ~90 таблиць;
    /// сам метод-виклювач (<c>ValidateDocumentHandler</c>) вже документує
    /// бюджет 3с p95 на документ, у який ~90 запитів не вкладаються.
    /// </remarks>
    public Task<IReadOnlyDictionary<long, IReadOnlyList<CellRecord>>> ReadSlicesAsync(
        IReadOnlyList<long> tableInstanceIds, CancellationToken ct);

    /// <summary>Значення конкретних комірок.</summary>
    public Task<IReadOnlyDictionary<CellAddress, CellValueData>> ReadCellsAsync(
        IReadOnlyCollection<CellAddress> addresses, CancellationToken ct);

    /// <summary>
    /// Застосовує набір змін однією транзакцією. Часткове застосування
    /// заборонене: або весь батч, або нічого (B04 §2.3).
    /// Бюджет: p95 &lt; 150 мс на 100 комірок.
    /// </summary>
    public Task ApplyAsync(CellChangeSet changes, CancellationToken ct);
}

/// <summary>Комірка з адресою і значенням.</summary>
public sealed record CellRecord(CellAddress Address, int TableDefId, CellValueData Value);

/// <summary>
/// Набір змін комірок в одній транзакції.
/// </summary>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="Upserts">Комірки для запису або оновлення.</param>
/// <param name="Deletes">Комірки для видалення (<c>"value": null</c>, R-B4).</param>
/// <param name="TouchedRowIds">Рядки, яким треба підняти <c>ModifiedAt</c> — інакше
/// <c>RowVersion</c> не зміниться і оптимістичне блокування тихо не працює (B04 §2.4).</param>
/// <param name="ChangedByUserId">Автор зміни (R-A2).</param>
/// <param name="IsLateEdit">Зміна в стані <c>Grace</c> або після <c>Reopen</c> (D-70).</param>
public sealed record CellChangeSet(
    long TableInstanceId,
    IReadOnlyList<CellRecord> Upserts,
    IReadOnlyList<CellAddress> Deletes,
    IReadOnlyList<long> TouchedRowIds,
    int ChangedByUserId,
    bool IsLateEdit);
