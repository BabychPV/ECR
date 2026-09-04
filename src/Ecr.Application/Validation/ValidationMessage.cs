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
public sealed record ValidationMessage(
    ValidationSeverity Severity,
    string RuleCode,
    string Message,
    int TableDefId,
    string? RowKey,
    string? ColumnCode,
    bool BlocksSave);
