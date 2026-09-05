using Ecr.Application.Documents.Dto;

namespace Ecr.Application.Ports;

/// <summary>Експорт документа у <c>.xlsx</c>.</summary>
public interface IExcelExporter
{
    /// <summary>
    /// Формує книгу. Довга операція — виконується у фоні з прогресом
    /// (бюджет 10 с p95, tz/08 §8.2).
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="options">Режим: тільки значення чи з формулами.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<Stream> ExportAsync(long documentId, ExcelExportOptions options, CancellationToken ct);
}

/// <summary>Налаштування експорту.</summary>
/// <param name="IncludeFormulas">Транслювати наші вирази в Excel-синтаксис (ФВ-4.2).</param>
/// <param name="IncludeStyles">Переносити стилі шаблону.</param>
/// <param name="Language">Мова заголовків.</param>
/// <param name="PeriodKey">Період вивантаження (R-A6).</param>
/// <remarks>
/// ⚠ <paramref name="PeriodKey"/> обовʼязковий, і це не зручність. Усе інше в
/// системі — подання, затвердження, перерахунок — працює <b>за період</b>;
/// експорт «усього документа» означав би книгу з дванадцятьма копіями кожної
/// таблиці, у якій неможливо сказати, який стовпчик за який місяць.
/// Значення «поточний період» тут теж не годиться: звіт вивантажують у перші
/// дні наступного, і мовчазний вибір давав би порожню книгу.
/// </remarks>
public sealed record ExcelExportOptions(
    bool IncludeFormulas, bool IncludeStyles, string Language, int PeriodKey);
