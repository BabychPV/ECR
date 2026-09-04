using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Нормалізоване сховище комірок — **базова модель** (D-21).
/// </summary>
/// <remarks>
/// Бюджет: <c>ReadSliceAsync</c> — p95 &lt; 600 мс на 500×60,
/// <c>ApplyAsync</c> — p95 &lt; 150 мс на 100 комірок (tz/08 §8.2).
/// Ці числа і є критерієм гейта Етапу 0: якщо не проходить після індексів і
/// стиснення — вибірково по таблицях вмикається гібрид, а не глобально.
/// </remarks>
public sealed class NormalizedCellStore(EcrDbContext db, BulkCellLoader bulk) : ICellStore
{
    /// <inheritdoc />
    public Task<IReadOnlyList<CellRecord>> ReadSliceAsync(long tableInstanceId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: ОДИН запит із AsNoTracking, проєкцією в CellRecord і фільтром по " +
            "(PeriodKey, TableRowId IN ...) — partition elimination має бути видно в плані. " +
            "Порожні комірки не існують у БД і не повертаються (ФВ-3.8). " +
            "Заборонено: Include по графу, ToList() без Take, завантаження сутностей замість проєкції.");

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<CellAddress, CellValueData>> ReadCellsAsync(
        IReadOnlyCollection<CellAddress> addresses, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: згрупувати адреси за PeriodKey і читати пакетно — по одному запиту на партицію, " +
            "не по запиту на комірку.");

    /// <inheritdoc />
    public Task ApplyAsync(CellChangeSet changes, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: одна транзакція:\n" +
            "1) DELETE для changes.Deletes через ExecuteDeleteAsync (не завантажуючи сутності);\n" +
            "2) upsert для changes.Upserts — MERGE або ExecuteUpdate + вставка нових;\n" +
            "3) UPDATE doc.TableRow SET ModifiedAt = @now WHERE ... для TouchedRowIds — " +
            "   ОБОВ'ЯЗКОВО: без цього RowVersion не піднімається і оптимістичне блокування " +
            "   тихо не працює (B04 §2.4);\n" +
            "Транзакція має бути КОРОТКОЮ: під RCSI довга транзакція роздуває version store.");

    /// <inheritdoc />
    public Task BulkInsertAsync(IReadOnlyList<CellRecord> records, CancellationToken ct)
        => throw new NotImplementedException("TODO: делегувати bulk.LoadAsync (SqlBulkCopy).");
}
