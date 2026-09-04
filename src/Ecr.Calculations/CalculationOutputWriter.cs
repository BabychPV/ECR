using Ecr.Application.Ports;

namespace Ecr.Calculations;

/// <summary>Записує результати прогону.</summary>
/// <remarks>
/// Пише **тільки** в <c>calc.CalculationResult</c>. У <c>doc.CellValue</c>
/// результати методологій не потрапляють ніколи (D-69): інакше нічний
/// перерахунок писав би десятки мільйонів рядків у партиції документів і
/// роздував <c>aud.CellChange</c>.
/// </remarks>
public sealed class CalculationOutputWriter(
    Ecr.Infrastructure.Persistence.BulkCellLoader bulk,
    Ecr.Infrastructure.Persistence.EcrDbContext db)
{
    /// <summary>Записує результати і трейс пакетно.</summary>
    public Task WriteAsync(long calculationRunId, IReadOnlyList<CalculationOutput> outputs, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: Id узяти з calc.CalculationResultSeq одним викликом sp_sequence_get_range; " +
            "писати SqlBulkCopy у calc.CalculationResult; трейс — у calc.CalculationStep, " +
            "але лише якщо TraceLevel це передбачає. " +
            "Після завершення прогону — інвалідувати залежні зрізи rpt.*.");
}
