// src/Ecr.Domain/ValueObjects/EcrCode.cs

using System.Text.RegularExpressions;
using Ecr.Domain.Abstractions;

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
    /// <remarks>
    /// ⛔ <see cref="DomainException"/>, а не <c>ArgumentException</c>.
    /// Різниця не косметична: конвеєр обробки помилок мапить доменний
    /// виняток у <c>422</c> з кодом і текстом, а <c>ArgumentException</c>
    /// провалюється у гілку «невідомий виняток» — тобто <c>500</c> із
    /// «Внутрішня помилка, зверніться до адміністратора».
    ///
    /// ⚠ Код — ПЕРШЕ поле кожної форми створення: проєкту, шаблону, ролі,
    /// запису довідника. Природна форма «KASH-2027» дефіса не приймає, і
    /// користувач бачив аварію сервера замість пояснення, що саме не так.
    /// </remarks>
    /// <exception cref="DomainException">Код не відповідає <see cref="Pattern"/>.</exception>
    public static EcrCode Create(string value)
        => TryCreate(value, out var code)
            ? code
            : throw new DomainException(
                "ECR-CFG-0422",
                $"Код «{value}» недопустимий: дозволені латинські літери, цифри й підкреслення, "
                + "перший символ — літера, довжина до 64.");

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
