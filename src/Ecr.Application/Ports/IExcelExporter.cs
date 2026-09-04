namespace Ecr.Application.Ports;

using Ecr.Application.Documents.Dto;

/// <summary>Експорт документа у <c>.xlsx</c>.</summary>
public interface IExcelExporter
{
    /// <summary>
    /// Формує книгу. Довга операція — виконується у фоні з прогресом
    /// (бюджет 10 с p95, tz/08 §8.2).
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="options">Режим: тільки значення чи з формулами.</param>
    Task<Stream> ExportAsync(long documentId, ExcelExportOptions options, CancellationToken ct);
}

/// <summary>Налаштування експорту.</summary>
/// <param name="IncludeFormulas">Транслювати наші вирази в Excel-синтаксис (ФВ-4.2).</param>
/// <param name="IncludeStyles">Переносити стилі шаблону.</param>
/// <param name="Language">Мова заголовків.</param>
public sealed record ExcelExportOptions(bool IncludeFormulas, bool IncludeStyles, string Language);
