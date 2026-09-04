using Ecr.Application.Ports;

namespace Ecr.Calculations;

/// <summary>Записує результати прогону.</summary>
/// <remarks>
/// Пише **тільки** в <c>calc.CalculationResult</c>. У <c>doc.CellValue</c>
/// результати методологій не потрапляють ніколи (D-69): інакше нічний
/// перерахунок писав би десятки мільйонів рядків у партиції документів і
/// роздував <c>aud.CellChange</c>.
/// </remarks>
public sealed class CalculationOutputWriter(ICalculationResultStore store)
{
    /// <summary>Записує результати і трейс пакетно.</summary>
    public Task WriteAsync(long calculationRunId, IReadOnlyList<CalculationOutput> outputs, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: діапазон Id — store.ReserveResultIdRangeAsync(кількість); " +
            "результати — store.WriteResultsAsync (SqlBulkCopy у calc.CalculationResult); " +
            "трейс — store.WriteTraceAsync, але лише якщо TraceLevel це передбачає; " +
            "після завершення прогону — store.InvalidateReportSnapshotsAsync.");
}
