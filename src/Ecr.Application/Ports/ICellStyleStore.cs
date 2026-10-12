// src/Ecr.Application/Ports/ICellStyleStore.cs
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Користувацьке оформлення комірок (<c>doc.CellStyle</c>): розріджена таблиця поза
/// <c>doc.CellValue</c> (CELL-STYLE L3b).
/// </summary>
public interface ICellStyleStore
{
    /// <summary>
    /// Усе оформлення екземпляра таблиці за період — ОДИН запит, без відстеження змін
    /// (для зрізу й експорту).
    /// </summary>
    /// <remarks>
    /// ⚠ Рядки екземпляра беруться з <c>doc.TableRow</c> (ключ <c>PeriodKey</c> + <c>TableInstanceId</c>):
    /// сирота без живого рядка в результат не потрапляє.
    /// </remarks>
    public Task<IReadOnlyList<CellStyle>> ReadInstanceAsync(
        long tableInstanceId, PeriodKey periodKey, CancellationToken ct);

    /// <summary>Те саме для кількох екземплярів одного документа-періоду (експорт): екземпляр → оформлення.</summary>
    public Task<IReadOnlyDictionary<long, IReadOnlyList<CellStyle>>> ReadInstancesAsync(
        IReadOnlyCollection<long> tableInstanceIds, PeriodKey periodKey, CancellationToken ct);

    /// <summary>Наявні рядки оформлення цих рядків таблиці — З відстеженням (для зміни).</summary>
    public Task<IReadOnlyList<CellStyle>> LoadForUpdateAsync(
        PeriodKey periodKey, IReadOnlyCollection<long> rowIds, CancellationToken ct);

    /// <summary>Додає новий рядок оформлення (зберігається разом зі зміною в транзакції).</summary>
    public void Add(CellStyle style);

    /// <summary>Видаляє рядок оформлення (комірку скинуто).</summary>
    public void Remove(CellStyle style);
}
