// src/Ecr.Application/Ports/ICellStore.cs
namespace Ecr.Application.Ports;

using Ecr.Domain.ValueObjects;

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
    Task<IReadOnlyList<CellRecord>> ReadSliceAsync(long tableInstanceId, CancellationToken ct);

    /// <summary>Значення конкретних комірок.</summary>
    Task<IReadOnlyDictionary<CellAddress, CellValueData>> ReadCellsAsync(
        IReadOnlyCollection<CellAddress> addresses, CancellationToken ct);

    /// <summary>
    /// Застосовує набір змін однією транзакцією. Часткове застосування
    /// заборонене: або весь батч, або нічого (B04 §2.3).
    /// Бюджет: p95 &lt; 150 мс на 100 комірок.
    /// </summary>
    Task ApplyAsync(CellChangeSet changes, CancellationToken ct);

    /// <summary>Масове завантаження через <c>SqlBulkCopy</c>: імпорт, генератор, міграція.</summary>
    Task BulkInsertAsync(IReadOnlyList<CellRecord> records, CancellationToken ct);
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
