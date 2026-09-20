// src/Ecr.Application/Reporting/ReportColumnNames.cs
using Ecr.Application.Errors;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Reporting;

/// <summary>
/// Назви колонок зрізу мовами каталогу (<c>R9</c>): перевірка при СТВОРЕННІ
/// версії і вибір мови на ВИДАЧІ — одним кодом.
/// </summary>
/// <remarks>
/// ⛔ Назва — це ПОДАННЯ, а не дані зрізу: у <c>rpt.ReportRow</c> вона не
/// потрапляє і <c>ContentHash</c> її не бачить. Та сама причина, що й у макета
/// (<c>R8</c>): сума доводить, що не змінилися ДАНІ, і перейменована колонка в
/// ній читалася б як підміна звіту.
///
/// ⚠ Тип той самий, що й у решти локалізованих назв, — <see cref="LocalizedText"/>
/// (<c>ФВ-2.2</c>): ключі порівнюються без урахування регістру, і додати мову
/// означає запис у реєстр, а не збірку. Перелік мов тут НЕ закритий свідомо:
/// сервер приймає переклад будь-якою мовою (див. <c>IUiStringCatalog.ListLanguagesAsync</c>),
/// і четверта мова в <c>sys_ecr.Language</c> не має вимагати релізу коду.
///
/// ⛔ Ланцюг фолбеку — рівно <c>мова запиту → en → КОД колонки</c>, а не
/// <see cref="LocalizedText.Get(string, string)"/>: той третьою ланкою бере
/// «першу наявну» мову, а порядок у <c>Dictionary</c> не визначений — та сама
/// книга дістала б різні заголовки в різних прогонах. Для держформи заголовок,
/// що плаває, гірший за код колонки.
/// </remarks>
public static class ReportColumnNames
{
    /// <summary>Мова, якою підписано колонку, коли мови запиту в описі немає.</summary>
    /// <remarks>Збігається з <c>sys_ecr.Language.IsDefault</c> у сіді.</remarks>
    public const string Fallback = "en";

    /// <summary>Ключ каталогу для відмови на зламаній назві колонки.</summary>
    private const string MessageKey = "err.ECR-RPT-0422.columnName";

    /// <summary>Найдовший код мови, який ще є кодом мови (<c>BCP-47</c>, первинний субтег).</summary>
    private const int MaxLanguageLength = 8;

    /// <summary>
    /// Підпис колонки для мови запиту; <c>мова → en → код колонки</c>.
    /// </summary>
    /// <param name="code">Код колонки — остання ланка ланцюга.</param>
    /// <param name="nameL10n">Назви з опису версії; <c>null</c> — опис назв не має.</param>
    /// <param name="language">
    /// Внутрішній код мови запиту (<c>ICurrentUser.Language</c>, уже переведений
    /// із тега <c>LanguageCodes.FromTag</c>).
    /// </param>
    public static string Of(string code, IReadOnlyDictionary<string, string>? nameL10n, string? language)
    {
        if (nameL10n is null or { Count: 0 })
        {
            // Опис без назв поводиться рівно як до R9: колонка підписана кодом.
            return code;
        }

        var names = new LocalizedText(nameL10n);

        return Named(names, language) ?? Named(names, Fallback) ?? code;
    }

    /// <summary>Відмовляє, якщо назви колонки збережені зламаними.</summary>
    /// <param name="code">Код колонки.</param>
    /// <param name="nameL10n">Назви з запиту; <c>null</c> — перевіряти нічого.</param>
    /// <exception cref="BusinessRuleException">
    /// Код мови не є кодом мови або назва порожня — <c>ECR-RPT-0422</c>.
    /// </exception>
    /// <remarks>
    /// ⛔ Порожня назва — не «немає назви»: вона доїхала б до заголовка книги
    /// порожньою коміркою, і колонка держформи лишилася б без підпису взагалі.
    /// Відсутність назви мовою виражається ВІДСУТНІСТЮ ключа.
    /// </remarks>
    public static void Require(string code, IReadOnlyDictionary<string, string>? nameL10n)
    {
        if (nameL10n is null)
        {
            return;
        }

        foreach (var (language, name) in nameL10n)
        {
            // ⚠ Мова запиту зводиться до ПЕРВИННОГО субтега (`LanguageCodes.FromTag`),
            // тож ключ із регіоном (`en-GB`) не був би вибраний ніколи — це
            // назва, якої ніхто не побачить, а не переклад.
            if (string.IsNullOrWhiteSpace(language)
                || language.Length is < 2 or > MaxLanguageLength
                || !language.All(char.IsAsciiLetter))
            {
                throw Invalid(code, language, "language");
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                throw Invalid(code, language, "text");
            }
        }
    }

    /// <summary>Назва саме цією мовою; <c>null</c> — її немає.</summary>
    private static string? Named(LocalizedText names, string? language)
        => !string.IsNullOrWhiteSpace(language)
           && names.Values.TryGetValue(language, out var name)
           && !string.IsNullOrWhiteSpace(name)
            ? name
            : null;

    private static BusinessRuleException Invalid(string code, string language, string part)
        => new(
            ErrorCodes.ReportInvalid,
            $"Назва колонки «{code}» для мови «{language}» не складається ({part}).",
            new Dictionary<string, object?>
            {
                ["messageKey"] = MessageKey,
                ["columnCode"] = code,
                ["language"] = language,
                ["part"] = part,
            });
}
