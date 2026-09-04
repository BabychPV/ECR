// src/Ecr.Domain/ValueObjects/EcrCode.cs

using System.Text.RegularExpressions;

namespace Ecr.Domain.ValueObjects;

/// <summary>
/// Код сутності конфігурації. Обмеження продиктоване лексером виразів:
/// код вживається всередині <c>[...]</c> без екранування (R-B6).
/// </summary>
public readonly partial record struct EcrCode
{
    /// <summary>Регулярний вираз допустимого коду.</summary>
    public const string Pattern = "^[A-Za-z][A-Za-z0-9_]{0,63}$";

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex Validator();

    public string Value { get; }

    private EcrCode(string value) => Value = value;

    /// <summary>Створює код або кидає виняток.</summary>
    /// <exception cref="ArgumentException">Код не відповідає <see cref="Pattern"/>.</exception>
    public static EcrCode Create(string value)
        => TryCreate(value, out var code)
            ? code
            : throw new ArgumentException($"Код '{value}' не відповідає шаблону {Pattern}.", nameof(value));

    public static bool TryCreate(string? value, out EcrCode code)
    {
        if (!string.IsNullOrEmpty(value) && Validator().IsMatch(value))
        {
            code = new EcrCode(value);
            return true;
        }
        code = default;
        return false;
    }

    public override string ToString() => Value;
    public static implicit operator string(EcrCode c) => c.Value;
}
