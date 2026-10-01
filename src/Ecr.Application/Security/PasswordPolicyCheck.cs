// src/Ecr.Application/Security/PasswordPolicyCheck.cs
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Security;

namespace Ecr.Application.Security;

/// <summary>
/// Одна перевірка нового пароля для всіх трьох шляхів, де пароль задається:
/// зміна власного (<c>ChangePasswordHandler</c>), скидання адміністратором
/// (<c>ResetUserPasswordHandler</c>) і разовий пароль при створенні
/// (<c>CreateUserHandler</c>) — S15, ФВ-6.4a.
/// </summary>
/// <remarks>
/// ⛔ До S15 кожен шлях перевіряв лише довжину своєю копією умови. Спільна
/// функція — щоб правило не розходилося між трьома копіями знову. Прапорці
/// складності політики (цифра, регістр) не застосовуються — V-16/P-1.
///
/// ⚠ Відмова — той самий код <c>ECR-PWD-0422</c>, що й «закороткий», з окремим
/// <c>messageKey</c> на кожну причину: клієнт показує причину під полем пароля,
/// а нових кодів помилки заради цього не потрібно.
///
/// ⛔ У деталях — лише ВИМОГА, ніколи не введене значення (ФВ-6.11).
/// </remarks>
public static class PasswordPolicyCheck
{
    /// <summary>
    /// Ім'я коротше за це не шукається в паролі: дво-літерне ім'я заборонило б
    /// половину паролів без жодного виграшу в стійкості (той самий поріг, що в
    /// політиці складності Windows).
    /// </summary>
    public const int MinUserNameLengthToMatch = 3;

    /// <summary>
    /// Найдовший пароль, який приймає хешер (<c>PasswordHasher.MaxPasswordLength</c>, Infrastructure).
    /// </summary>
    public const int MaxLength = 256;

    /// <summary>
    /// Часто вживані паролі — невеликий вбудований блок-лист (S15).
    /// </summary>
    /// <remarks>
    /// Джерело: добірка з публічних переліків найпоширеніших паролів (топи
    /// витоків на кшталт SecLists / NCSC «100k most common»), лише записи від
    /// 8 символів — коротші відсікає <see cref="PasswordPolicy.MinLength"/> — і
    /// кілька очевидних сезонних / кириличних варіантів. Порівняння — без
    /// урахування регістру, по всьому паролю. Навмисно не завантажується ззовні:
    /// перевірка не має залежати від мережі чи файлу поруч зі службою.
    /// </remarks>
    private static readonly string[] CommonPasswords =
    [
        "password", "password1", "password12", "password123", "password1234", "password12345",
        "password123!", "passw0rd", "p@ssw0rd", "p@ssword", "p@ssw0rd123", "p@ssw0rd1234",
        "passwordpassword", "12345678", "123456789", "1234567890", "12345678910", "123456789012",
        "1234567890123", "0987654321", "11111111", "111111111111", "00000000", "000000000000",
        "88888888", "87654321", "123123123", "123123123123", "qwertyui", "qwertyuiop",
        "qwertyuiop123", "qwerty123", "qwerty1234", "qwerty123456", "qwertyqwerty", "1234qwer",
        "1q2w3e4r", "1q2w3e4r5t", "1q2w3e4r5t6y", "q1w2e3r4t5y6", "1qaz2wsx", "1qaz2wsx3edc",
        "zaq12wsx", "zaq1zaq1", "qazwsxedc", "qazwsxedcrfv", "123qweasd", "123qweasdzxc",
        "asdfghjkl", "asdfghjkl123", "zxcvbnm123", "abc12345", "abcd1234", "1234abcd",
        "abcdefgh", "abcdefghijkl", "1a2b3c4d", "a1b2c3d4", "iloveyou", "iloveyou123",
        "sunshine", "princess", "football", "baseball", "superman", "starwars", "trustno1",
        "michael1", "jennifer", "computer", "whatever", "dragon123", "monkey123", "master123",
        "shadow123", "welcome1", "welcome123", "welcome2025", "welcome2026", "letmein1",
        "letmein123", "changeme", "changeme123", "default123", "temp1234", "temporary",
        "administrator", "administrator1", "admin123", "admin12345", "adminadmin",
        "summer2025", "summer2026", "winter2025", "winter2026", "spring2026", "autumn2026",
        "parol123", "пароль123", "пароль12345", "йцукенгшщзхъ",
    ];

    private static readonly HashSet<string> Common = new(CommonPasswords, StringComparer.OrdinalIgnoreCase);

    /// <summary>Чи є пароль у вбудованому блок-листі.</summary>
    /// <param name="password">Пароль.</param>
    public static bool IsCommon(string password) => Common.Contains(password);

    /// <summary>
    /// Перевіряє новий пароль за політикою; кидає <c>ECR-PWD-0422</c> з ключем причини.
    /// </summary>
    /// <param name="policy">Політика облікового запису.</param>
    /// <param name="password">Новий пароль.</param>
    /// <param name="userName">Ім'я входу власника пароля.</param>
    /// <param name="subject">Що перевіряється — для запасного тексту («Новий пароль», «Разовий пароль»).</param>
    /// <exception cref="BusinessRuleException">Пароль порушує політику.</exception>
    public static void Ensure(
        PasswordPolicy policy, [NotNull] string? password, string userName, string subject)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var minLength = policy.MinLength.ToString(CultureInfo.InvariantCulture);

        if (password is null || password.Length < policy.MinLength)
        {
            // ⚠ Рядком: резолвер підставляє в шаблон лише `string`.
            throw new BusinessRuleException(
                "ECR-PWD-0422",
                $"{subject} коротший за {minLength} символів.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PWD-0422.tooShort",
                    ["minLength"] = minLength,
                });
        }

        // ⛔ Стеля хешера (`PasswordHasher`, захист PBKDF2 від багатомегабайтного «пароля»):
        // без цієї перевірки задовгий пароль доходив до `Hash` і падав `ArgumentException` —
        // 500 на створенні користувача, скиданні й зміні пароля.
        if (password.Length > MaxLength)
        {
            throw new BusinessRuleException(
                "ECR-PWD-0422",
                $"{subject} довший за {MaxLength} символів.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PWD-0422.tooLong",
                    ["maxLength"] = MaxLength.ToString(CultureInfo.InvariantCulture),
                });
        }

        // ⛔ V-16/P-1: цифра не вимагається; вмикається лише рішенням людини.
        // `PasswordPolicy.RequireDigit` навмисно НЕ читається: у розгорнутій
        // базі стовпець має умовчання 1 (`DF_PwdP_Dig`), і читання прапорця
        // мовчки зробило б цифру обов'язковою скрізь.

        if (!string.IsNullOrWhiteSpace(userName)
            && userName.Trim().Length >= MinUserNameLengthToMatch
            && password.Contains(userName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            // ⛔ Ім'я входу не є секретом: його бачать у шапці, журналах і
            // переліку користувачів. Пароль, що його містить, підбирається першим.
            throw new BusinessRuleException(
                "ECR-PWD-0422",
                $"{subject} містить ім'я користувача.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-PWD-0422.containsUserName" });
        }

        if (IsCommon(password))
        {
            throw new BusinessRuleException(
                "ECR-PWD-0422",
                $"{subject} є серед найпоширеніших паролів.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-PWD-0422.tooCommon" });
        }
    }

    /// <summary>
    /// Забороняє «змінити» пароль на чинний — лише для зміни власного пароля.
    /// </summary>
    /// <param name="hasher">Той самий хешер, яким зберігається пароль.</param>
    /// <param name="newPassword">Новий пароль (уже пройшов <see cref="Ensure"/>).</param>
    /// <param name="currentHash">Хеш чинного пароля.</param>
    /// <remarks>
    /// ⚠ Порівняння ХЕШЕМ, а не з введеним «чинним» паролем: так правило не
    /// залежить від того, що саме клієнт надіслав у полі чинного пароля.
    /// </remarks>
    /// <exception cref="BusinessRuleException">Новий пароль збігається з чинним.</exception>
    public static void EnsureNotCurrent(IPasswordHasher hasher, string newPassword, string? currentHash)
    {
        ArgumentNullException.ThrowIfNull(hasher);

        if (currentHash is not null && hasher.Verify(newPassword, currentHash))
        {
            throw new BusinessRuleException(
                "ECR-PWD-0422",
                "Новий пароль збігається з чинним.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-PWD-0422.sameAsCurrent" });
        }
    }
}
