// src/Ecr.Application/Documents/Dto/PatchCellsResponse.cs
namespace Ecr.Application.Documents.Dto;

/// <summary>Результат пакетної зміни.</summary>
/// <param name="AppliedCells">Скільки комірок записано.</param>
/// <param name="RowVersions">Нові версії зачеплених рядків: <c>RowKey</c> → hex.</param>
/// <param name="Validation">Результати валідації рівнів, які не блокують запис (R-B3).</param>
public sealed record PatchCellsResponse(
    int AppliedCells,
    IReadOnlyDictionary<string, string> RowVersions,
    IReadOnlyList<ValidationMessageDto> Validation);

/// <summary>Повідомлення валідації.</summary>
/// <param name="Severity">Рівень: <c>Info</c>/<c>Warning</c>/<c>Error</c>.</param>
/// <param name="RuleCode">Код правила з <c>cfg.ValidationRule</c>.</param>
/// <param name="Message">Локалізований текст.</param>
/// <param name="RowKey">Рядок, якого стосується; <c>null</c> — рівень таблиці.</param>
/// <param name="ColumnCode">Колонка; <c>null</c> — рівень рядка.</param>
public sealed record ValidationMessageDto(
    string Severity,
    string RuleCode,
    string Message,
    string? RowKey,
    string? ColumnCode);
