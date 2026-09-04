using Ecr.Application.Ports;
using Microsoft.Data.SqlClient;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Масове завантаження через <see cref="SqlBulkCopy"/> (D-05).
/// </summary>
/// <remarks>
/// <c>Id</c> для <c>TableRow</c> береться з <c>SEQUENCE</c>, а не
/// <c>IDENTITY</c>: значення потрібні **до** вставки, щоб завантажити рядки і
/// комірки одним проходом. З <c>IDENTITY</c> довелося б робити два кроки з
/// <c>OUTPUT</c>, а <c>OUTPUT</c> конфліктує з тригерами (B02 §2.3).
/// </remarks>
public sealed class BulkCellLoader(string connectionString, int batchSize)
{
    /// <summary>Завантажує комірки.</summary>
    public Task LoadAsync(IReadOnlyList<CellRecord> records, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: SqlBulkCopy з BatchSize = batchSize, SqlBulkCopyOptions.TableLock, " +
            "DestinationTableName = \"doc.CellValue\"; колонки мапити явно за іменами. " +
            "Дані подавати через IDataReader-обгортку, а не DataTable: 108 млн рядків " +
            "у DataTable не поміщаються в пам'ять.");

    /// <summary>Резервує діапазон ідентифікаторів із послідовності.</summary>
    public Task<long> ReserveIdsAsync(string sequenceName, int count, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: sp_sequence_get_range — один виклик на весь батч, не на рядок.");
}
