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
///
/// ⛔ S6 (ФВ-6.6): <paramref name="HiddenTableDefIds"/> і
/// <paramref name="HiddenColumnDefIds"/> — таблиці й колонки під забороною
/// для того, хто замовив експорт. Задача виконується в черзі без
/// користувача, тож межі читання рахує <c>ExportDocumentHandler</c> з профілю
/// і кладе СЮДИ; з тіла запиту вони не приходять (<c>ExportRequest</c> таких
/// полів не має, а обробник перезаписує їх завжди).
///
/// ⚠ <c>null</c> — завдання, поставлене ДО S6 (у збереженому JSON полів
/// немає): такий експорт іде без фільтра, як і було. Нові завдання несуть
/// списки завжди, хай і порожні.
/// </remarks>
/// <param name="HiddenTableDefIds">Таблиці, яких немає в книзі.</param>
/// <param name="HiddenColumnDefIds">Колонки, яких немає в книзі.</param>
/// <param name="DocumentBusinessKey">
/// Аудит 2026-11 X-4: File Number (<c>BusinessKey</c>) документа для аркуша шапки; <c>null</c> — рядка немає
/// (завдання до X-4 у черзі). Заповнює <c>ExportDocumentHandler</c>, не тіло запиту.
/// </param>
/// <param name="TemplateVersionLabel">X-4: версія шаблону документа за період; <c>null</c> — рядка немає.</param>
/// <param name="PeriodKind">
/// X-4: періодичність проєкту (<c>PeriodKind.ToString()</c>) — для підпису періоду в аркуші шапки; <c>null</c> — як місячний.
/// </param>
public sealed record ExcelExportOptions(
    bool IncludeFormulas, bool IncludeStyles, string Language, int PeriodKey,
    IReadOnlyList<int>? HiddenTableDefIds = null, IReadOnlyList<int>? HiddenColumnDefIds = null,
    string? DocumentBusinessKey = null, string? TemplateVersionLabel = null, string? PeriodKind = null);
