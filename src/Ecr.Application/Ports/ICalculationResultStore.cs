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

    /// <summary>
    /// Робить прогін актуальним: попередній перестає бути таким **у тій самій
    /// транзакції** (ФВ-9.11).
    /// </summary>
    /// <param name="calculationRunId">Прогін, який стає актуальним.</param>
    /// <param name="modulesProfileJson">Профіль по модулях; пишеться завжди.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Прапорець живе на ПРОГОНІ, а не на кожному результаті: у схемі
    /// <c>calc.CalculationResult</c> колонки <c>IsCurrent</c> немає, і це
    /// правильно — інакше «перемикання актуального прогону» означало б
    /// оновити десятки мільйонів рядків, і «одна транзакція» з вимоги стала б
    /// блокуванням партиції на хвилини. Результат актуальний тоді, коли
    /// актуальний його прогін.
    /// <para>
    /// Дві половини — зняти зі старого і поставити новому — мусять бути
    /// нероздільні: між ними існує стан, у якому актуальних прогонів нуль або
    /// два, і звіт, побудований у цю мить, не має правильної відповіді.
    /// </para>
    /// </remarks>
    public Task SwitchCurrentRunAsync(
        long calculationRunId, string modulesProfileJson, CancellationToken ct);
}
