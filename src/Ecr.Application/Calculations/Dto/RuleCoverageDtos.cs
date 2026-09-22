namespace Ecr.Application.Calculations.Dto;

/// <summary>Стан комбінації значень у матриці покриття (ФВ-13.9).</summary>
public enum RuleCoverageState
{
    /// <summary>Жодне правило не збіглося — рядки не рахуються.</summary>
    Gap,

    /// <summary>Два й більше збіги з пріоритетом переможця — вибір залежить від порядку Id.</summary>
    Conflict,

    /// <summary>Є однозначний переможець.</summary>
    Covered,
}

/// <summary>Матриця покриття «рядки реальних даних × правила» версії методології.</summary>
/// <param name="MethodologyVersionId">Версія.</param>
/// <param name="PeriodFrom">Нижня межа вікна періодів (включно), фактично застосована.</param>
/// <param name="PeriodTo">Верхня межа (включно).</param>
/// <param name="TableDefIds">Таблиці, рядки яких бралися.</param>
/// <param name="ColumnDefIds">Колонки осі — ті, що згадують правила; порядок значень у комбінації.</param>
/// <param name="Rules">Активні правила в порядку пріоритету.</param>
/// <param name="Combinations">Комбінації: розриви → конфлікти → покриті, далі за кількістю рядків.</param>
/// <param name="Truncated">Комбінацій більше за стелю; показано найчисленніші.</param>
public sealed record RuleCoverageDto(
    int MethodologyVersionId,
    int PeriodFrom,
    int PeriodTo,
    IReadOnlyList<int> TableDefIds,
    IReadOnlyList<int> ColumnDefIds,
    IReadOnlyList<RuleCoverageRuleDto> Rules,
    IReadOnlyList<RuleCoverageCombinationDto> Combinations,
    bool Truncated);

/// <summary>Правило осі матриці.</summary>
/// <param name="Code">Код правила.</param>
/// <param name="Priority">Пріоритет; менше — вищий.</param>
public sealed record RuleCoverageRuleDto(string Code, int Priority);

/// <summary>Одна комбінація значень (коди, не числа звітності) і що з нею роблять правила.</summary>
/// <param name="Values">Значення в порядку <c>ColumnDefIds</c>; <c>null</c> — порожньо.</param>
/// <param name="State">Стан.</param>
/// <param name="WinnerRuleCode">Правило-переможець; <c>null</c> для розриву.</param>
/// <param name="MatchedRuleCodes">Усі збіжні правила; після переможця — нічия або затінені.</param>
/// <param name="Rows">Кількість рядків.</param>
/// <param name="Documents">Кількість документів.</param>
public sealed record RuleCoverageCombinationDto(
    IReadOnlyList<string?> Values,
    RuleCoverageState State,
    string? WinnerRuleCode,
    IReadOnlyList<string> MatchedRuleCodes,
    long Rows,
    int Documents);
