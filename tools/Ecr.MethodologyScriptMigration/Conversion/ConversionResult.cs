namespace Ecr.MethodologyScriptMigration.Conversion;

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
