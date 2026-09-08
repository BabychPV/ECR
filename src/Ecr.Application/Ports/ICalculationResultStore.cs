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

    /// <summary>
    /// Числа <b>актуального</b> прогону для документа й періоду.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Результати в порядку рядка й коду виходу; порожньо — прогону немає.</returns>
    /// <remarks>
    /// ⛔ Читання живе в тому самому порту, що й запис, бо предмет один:
    /// <c>calc.CalculationResult</c> партиційована за періодом, а «актуальність»
    /// живе на ПРОГОНІ (<c>CalculationRun.CurrentStatus</c>), не на рядку. Другий
    /// порт мав би повторити обидва ці знання — і саме там вони й розійшлися б.
    ///
    /// ⛔ Береться саме актуальний прогін, а не останній за часом. Прогін, який
    /// упав, лишає по собі частину рядків; віддати їх означало б показати в
    /// звіті числа, половина яких порахована старою версією методології.
    ///
    /// ⚠ Без цього методу результат методології не було видно НІДЕ (<c>D-69</c>:
    /// у <c>doc.CellValue</c> він не потрапляє). Перерахунок завершувався
    /// успіхом, число лягало в базу — і єдиним способом його побачити був
    /// <c>SELECT</c>.
    /// </remarks>
    public Task<IReadOnlyList<CalculationResultRow>> ReadCurrentAsync(
        long documentId, int periodKey, CancellationToken ct);
}

/// <summary>Один рядок актуального результату розрахунку.</summary>
/// <param name="MethodologyVersionId">Версія, що дала число: без неї його неможливо пояснити.</param>
/// <param name="SourceRowKey">Рядок документа; <c>null</c> — рівень таблиці.</param>
/// <param name="OutputCode">Код виходу методології.</param>
/// <param name="Value">Значення; <c>decimal</c>, ніколи <c>float</c> (<c>D-30</c>).</param>
/// <param name="UnitId">Одиниця результату — обов'язкова (ФВ-16.6).</param>
/// <param name="SubstanceEntryId">Речовина; <c>null</c> — вихід без речовини.</param>
public sealed record CalculationResultRow(
    int MethodologyVersionId,
    string? SourceRowKey,
    string OutputCode,
    decimal Value,
    int UnitId,
    long? SubstanceEntryId);
