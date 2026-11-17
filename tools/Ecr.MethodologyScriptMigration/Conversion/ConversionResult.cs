namespace Ecr.MethodologyScriptMigration.Conversion;

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

/// <summary>Результат перетворення версії.</summary>
/// <param name="Text">Текст скрипта; <c>null</c> — є помилки.</param>
/// <param name="Issues">Зауваження в порядку виявлення.</param>
public sealed record ConversionResult(string? Text, IReadOnlyList<ConversionIssue> Issues)
{
    /// <summary>Чи є помилки.</summary>
    public bool HasErrors => Issues.Any(i => i.Severity == IssueSeverity.Error);
}

/// <summary>Вираз або конструкцію не можна перетворити.</summary>
public sealed class ConversionException(string code, string message) : Exception(message)
{
    /// <summary>Код зауваження.</summary>
    public string Code { get; } = code;
}
