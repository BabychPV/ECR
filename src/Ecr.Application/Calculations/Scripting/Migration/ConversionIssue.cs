namespace Ecr.Application.Calculations.Scripting.Migration;

/// <summary>Серйозність зауваження перетворювача.</summary>
public enum IssueSeverity : byte
{
    /// <summary>Інформація: рішення перетворювача (перейменування тощо).</summary>
    Info = 0,

    /// <summary>Джерело має дефект, але текст згенеровано (наприклад константа без значення).</summary>
    Warning = 1,

    /// <summary>Не перетворюється: тексту немає.</summary>
    Error = 2,
}

/// <summary>Зауваження перетворювача.</summary>
/// <param name="Severity">Серйозність.</param>
/// <param name="Code">Стабільний код (camelCase).</param>
/// <param name="Message">Пояснення.</param>
/// <param name="Subject">Про що (код формули, константи тощо).</param>
public sealed record ConversionIssue(IssueSeverity Severity, string Code, string Message, string? Subject = null);
