using Ecr.Domain.Enums;

namespace Ecr.Application.Validation;

/// <summary>Повідомлення валідації.</summary>
/// <param name="Severity">Рівень.</param>
/// <param name="RuleCode">Код правила з <c>cfg.ValidationRule</c>.</param>
/// <param name="Message">Локалізований текст.</param>
/// <param name="TableDefId">Таблиця.</param>
/// <param name="RowKey">Рядок; <c>null</c> — рівень таблиці або документа.</param>
/// <param name="ColumnCode">Колонка; <c>null</c> — рівень рядка і вище.</param>
/// <param name="BlocksSave">
/// Чи блокує збереження. <c>true</c> **лише** для коміркового <c>Error</c> (R-B3).
/// </param>
/// <param name="SourceTableDefId">
/// Таблиця ДРУГОЇ сторони зв'язку Check (джерело); текст містить її значення, тож читач без права на неї
/// не бачить повідомлення (T1-01). <c>null</c> — повідомлення однобічне.
/// </param>
/// <param name="SourceColumnCode">Колонка джерела зв'язку Check; <c>null</c> — лише рівень таблиці.</param>
/// <param name="MessageKey">
/// Ключ каталогу <c>sys_ecr.UiString</c> (<see cref="ValidationMessageTemplates"/>) для повідомлень двигуна
/// (Check, структурні, зламане правило): читання підставляє <paramref name="Params"/> у шаблон МОВОЮ ЧИТАЧА
/// (T2-07 / T3-03 / T4-06). <c>null</c> — старий збережений результат або текст правила (його мови несе
/// <c>MessageL10n</c>): тоді працює <paramref name="Message"/> як є.
/// </param>
/// <param name="Params">
/// Підстановки шаблону (значення вже текстом за інваріантною культурою). ⛔ Містять значення джерела Check:
/// назовні НЕ віддаються (DTO несе лише текст), а повідомлення, приховане за <c>HiddenValidationIssues.CanSee</c>,
/// до локалізації не доходить.
/// </param>
public sealed record ValidationMessage(
    ValidationSeverity Severity,
    string RuleCode,
    string Message,
    int TableDefId,
    string? RowKey,
    string? ColumnCode,
    bool BlocksSave,
    int? SourceTableDefId = null,
    string? SourceColumnCode = null,
    string? MessageKey = null,
    IReadOnlyDictionary<string, string>? Params = null);
