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
                + "перший символ — літера, довжина до 64.",
                // ⛔ Domain не має і ніколи не матиме доступу до IUiStringCatalog
                // (шар нижче за Application/Infrastructure) — тому це не готове
                // речення каталогу, а СТРУКТУРОВАНИЙ ключ + підстановка, яку
                // резолвить ExceptionHandlingMiddleware.LocalizedDetailAsync,
                // маючи каталог. Українське речення вище лишається як message
                // для логів/трасування, а не для показу користувачу — саме
                // воно й доїжджало клієнту сирим до цього фіксу (Q-30x): цей
                // код валідує КОЖНЕ поле «код» у застосунку (роль, проєкт,
                // методологія, шаблон, довідник), і жоден із цих викликів не
                // має доступу до каталогу рядків у Domain-шарі.
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-CFG-0422.invalidCode", ["code"] = value });

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
