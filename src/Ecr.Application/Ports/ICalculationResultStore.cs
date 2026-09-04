// src/Ecr.Application/Ports/ICalculationResultStore.cs

using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>
/// Запис результатів прогону розрахунку.
/// </summary>
/// <remarks>
/// ⚠ <b>Порт уведений за рішенням Q-018 (варіант B).</b> До цього
/// <c>Ecr.Calculations.CalculationOutputWriter</c> був типізований напряму на
/// <c>EcrDbContext</c> і <c>BulkCellLoader</c>.
///
/// Пише <b>тільки</b> в <c>calc.CalculationResult</c> і <c>calc.CalculationStep</c>.
/// У <c>doc.CellValue</c> результати методологій не потрапляють ніколи (D-69):
/// інакше нічний перерахунок писав би десятки мільйонів рядків у партиції
/// документів і роздував <c>aud.CellChange</c>.
/// </remarks>
public interface ICalculationResultStore
{
    /// <summary>
    /// Резервує діапазон ідентифікаторів із <c>calc.CalculationResultSeq</c>
    /// одним викликом <c>sp_sequence_get_range</c>.
    /// </summary>
    public Task<long> ReserveResultIdRangeAsync(int count, CancellationToken ct);

    /// <summary>
    /// Пише результати пакетно (<c>SqlBulkCopy</c>). <c>SaveChanges</c> у циклі
    /// заборонений: бюджет річного перерахунку — 10 хвилин (ПРД-13).
    /// </summary>
    public Task WriteResultsAsync(long calculationRunId, IReadOnlyList<CalculationOutput> outputs, CancellationToken ct);

    /// <summary>
    /// Пише трейс — лише те, що передбачає <paramref name="traceLevel"/>.
    /// Керуємо тим, <b>що</b> пишемо, а не скільки зберігаємо (ЗБР-3).
    /// </summary>
    public Task WriteTraceAsync(
        long calculationRunId, IReadOnlyList<CalculationOutput> outputs,
        TraceLevel traceLevel, CancellationToken ct);

    /// <summary>Інвалідує залежні зрізи <c>rpt.*</c> після завершення прогону.</summary>
    public Task InvalidateReportSnapshotsAsync(long calculationRunId, CancellationToken ct);
}
