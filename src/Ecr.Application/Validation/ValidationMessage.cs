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
public sealed record ValidationMessage(
    ValidationSeverity Severity,
    string RuleCode,
    string Message,
    int TableDefId,
    string? RowKey,
    string? ColumnCode,
    bool BlocksSave,
    int? SourceTableDefId = null,
    string? SourceColumnCode = null);
