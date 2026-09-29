using System.Globalization;

namespace Ecr.Application.Localization;

/// <summary>
/// Культура розбору чисел, набраних ЛЮДИНОЮ текстом, — за мовою користувача.
/// </summary>
/// <remarks>
/// ⛔ Рішення людини 2026-09-29: «загалом en-US, але ти маєш це визначати
/// автоматично». Джерело мови — той самий <see cref="Common.ICurrentUser.Language"/>,
/// яким сервер локалізує повідомлення (профіль → <c>Accept-Language</c> → <c>en</c>):
/// окремого порту немає свідомо — друге джерело мови розійшлося б із першим, і
/// відмова приходила б однією мовою, а число читалося б за іншою.
///
/// ⚠ Не <see cref="CultureInfo.CurrentCulture"/> сервера: вона залежить від
/// машини (саме так на сервері з uk-UA «12.5» ставало 125 — аудит <c>C1</c>).
/// Мови немає (фонова задача, анонімний запит) — <c>ICurrentUser</c> уже віддає
/// <c>en</c>, тобто en-US, роздільники якої збігаються з Invariant, яким розбір
/// ішов доти.
///
/// ⚠ Для ru — <c>ru-RU</c>: у проєкті ніде не прийнято <c>ru-KZ</c>, а
/// роздільники в обох однакові (кома й нерозривний пробіл). Для казахської
/// реєстр тримає код <c>kz</c>, а культура — <c>kk-KZ</c> (<see cref="LanguageCodes"/>).
/// </remarks>
public static class NumberCulture
{
    /// <summary>Культура за замовчуванням — рішення людини.</summary>
    public static readonly CultureInfo Default = CultureInfo.GetCultureInfo("en-US");

    /// <summary>Внутрішній код мови → культура; решта — через тег BCP-47.</summary>
    private static readonly Dictionary<string, string> CultureByCode =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "en-US",
            ["ru"] = "ru-RU",
            ["kz"] = "kk-KZ",
            ["uk"] = "uk-UA",
        };

    /// <summary>Культура розбору для внутрішнього коду мови або тега BCP-47.</summary>
    /// <param name="language">
    /// Код мови з реєстру (<c>en</c>, <c>ru</c>, <c>kz</c>) або тег (<c>kk-KZ</c>);
    /// порожнє — <see cref="Default"/>.
    /// </param>
    public static CultureInfo ForLanguage(string? language)
    {
        var code = LanguageCodes.FromTag(language);
        if (code.Length == 0)
        {
            return Default;
        }

        if (CultureByCode.TryGetValue(code, out var name))
        {
            return CultureInfo.GetCultureInfo(name);
        }

        // ⚠ Четверта мова реєстру не потребує правки коду: культура береться з
        // ICU за тегом; невідома ICU — культура за замовчуванням, а не виняток.
        try
        {
            var culture = CultureInfo.CreateSpecificCulture(language!.Trim());

            return string.IsNullOrEmpty(culture.Name) ? Default : culture;
        }
        catch (CultureNotFoundException)
        {
            return Default;
        }
    }
}
