using Ecr.Application.Ports;

namespace Ecr.Application.Localization;

/// <summary>
/// Правила каталогу як **чисті функції**: fallback, підстановка ключа і форма
/// <c>ETag</c>.
/// </summary>
/// <remarks>
/// Винесені окремо від сховища навмисно. Це єдине місце, де записано, що
/// **порожнеча не повертається ніколи**, — а перевірити його без бази і без
/// HTTP означає, що правило можна прогнати на кожній збірці, а не «коли
/// дійдуть руки до інтеграційних».
/// </remarks>
public static partial class UiStringResolver
{
    /// <summary>Мова, якою підмінюється відсутній переклад.</summary>
    /// <remarks>
    /// Збігається з <c>sys_ecr.Language.IsDefault</c> у seed. Константа тут —
    /// не дубль, а межа: якщо в базі мову за замовчуванням зняли, каталог має
    /// однаково чимось підмінювати, а не віддавати порожнечу.
    /// </remarks>
    public const string DefaultLanguage = "en";

    /// <summary>Префікс ключів текстів помилок (<c>ФВ-14.9a</c>, <c>D-111</c>).</summary>
    public const string ErrorKeyPrefix = "err.";

    /// <summary>Накладає рядки мови поверх рядків мови за замовчуванням.</summary>
    /// <param name="defaults">Каталог мови за замовчуванням.</param>
    /// <param name="requested">Каталог запитаної мови; може бути неповним.</param>
    /// <returns>Повний набір ключів обох мов.</returns>
    public static IReadOnlyDictionary<string, string> Compose(
        IReadOnlyDictionary<string, string> defaults,
        IReadOnlyDictionary<string, string> requested)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(requested);

        // ⚠ Ключі беруться з ОБОХ мов, а не з запитаної: інакше нова мова з
        // трьома перекладами дала б екран із трьома підписами замість повного
        // з англійськими вкрапленнями. Одна забута локалізація не має ламати
        // екран.
        var result = new Dictionary<string, string>(defaults, StringComparer.Ordinal);
        foreach (var (key, value) in requested)
        {
            if (!string.IsNullOrEmpty(value))
            {
                result[key] = value;
            }
        }

        return result;
    }

    /// <summary>
    /// Те саме зіставлення, але **без підміни**: переклад як він є (<c>BE-13</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Дзеркало <see cref="Compose"/>, і визначення «перекладу немає» в них
    /// мусить бути одне: ключа немає АБО значення порожнє. Якби порожній рядок
    /// тут рахувався перекладом, покриття показувало б 100 % для мови, половина
    /// якої на екрані англійська.
    ///
    /// ⚠ Ключі — лише з мови за замовчуванням: рядок, якого в еталоні немає,
    /// перекладати нема з чого, і в покритті він дав би «перекладено більше, ніж
    /// усього».
    /// </remarks>
    /// <param name="defaults">Каталог мови за замовчуванням.</param>
    /// <param name="requested">Каталог запитаної мови; може бути неповним.</param>
    public static IReadOnlyList<UiStringRawRow> ComposeRaw(
        IReadOnlyDictionary<string, string> defaults,
        IReadOnlyDictionary<string, string> requested)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(requested);

        return [.. defaults
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new UiStringRawRow(
                pair.Key,
                pair.Value,
                requested.TryGetValue(pair.Key, out var value) && !string.IsNullOrEmpty(value) ? value : null))];
    }

    /// <summary>Текст за ключем або **сам ключ**, якщо його немає ніде.</summary>
    /// <param name="catalog">Каталог мови.</param>
    /// <param name="key">Ключ.</param>
    public static string Resolve(UiStringCatalog catalog, string key)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrEmpty(key);

        // ⛔ Порожній рядок не повертається ніколи. Порожній підпис у UI — це
        // кнопка без назви: користувач не знає ні що вона робить, ні кому про
        // це сказати. Ключ принаймні називає місце.
        return catalog.Strings.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)
            ? value
            : key;
    }

    /// <summary>Текст помилки з ТОГО САМОГО каталогу (<c>ФВ-14.9a</c>).</summary>
    /// <param name="catalog">Каталог мови.</param>
    /// <param name="errorCode">Код із каталогу помилок, напр. <c>ECR-PWD-0428</c>.</param>
    public static string ResolveError(UiStringCatalog catalog, string errorCode)
    {
        ArgumentException.ThrowIfNullOrEmpty(errorCode);
        return Resolve(catalog, ErrorKeyPrefix + errorCode);
    }

    /// <summary>
    /// Підставляє <c>{name}</c> у шаблон значеннями з <paramref name="parameters"/>
    /// (`Q-304`).
    /// </summary>
    /// <remarks>
    /// ⚠ Той самий синтаксис підстановки, що й клієнтський <c>t()</c>
    /// (`shared/i18n/index.ts`): один формат плейсхолдера в обох місцях, де
    /// текст каталогу підставляється зі змінними частинами — сервер (тут,
    /// `health.*`) і клієнт (`err.*`, підписи інтерфейсу). Ключ без
    /// підстановки в <paramref name="parameters"/> лишається як є — так само,
    /// як клієнтський `t()` лишає невідомий `{ім'я}` непідставленим, а не
    /// кидає виняток на кожному застарілому шаблоні каталогу.
    /// </remarks>
    /// <param name="template">Текст із каталогу, вже розв'язаний за ключем.</param>
    /// <param name="parameters">Підстановки; <c>null</c> — текст лишається без змін.</param>
    public static string Format(string template, IReadOnlyDictionary<string, string>? parameters)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (parameters is null || parameters.Count == 0)
        {
            return template;
        }

        return PlaceholderPattern().Replace(
            template,
            match => parameters.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);
    }

    /// <summary>Набір плейсхолдерів шаблону (<c>{0}</c>, <c>{name}</c>), без повторів, упорядкований.</summary>
    /// <param name="template">Текст каталогу.</param>
    public static IReadOnlyList<string> Placeholders(string template)
    {
        ArgumentNullException.ThrowIfNull(template);

        return [.. PlaceholderPattern().Matches(template)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Чи збігається НАБІР плейсхолдерів перекладу з оригіналом (<c>BE-13</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Набір, а не послідовність: переклад має право переставити
    /// <c>{from}</c> і <c>{to}</c> місцями й повторити один двічі — порядок слів
    /// у мовах різний. Загублений чи перейменований плейсхолдер — дефект:
    /// <see cref="Format"/> лишить <c>{nmae}</c> у тексті як є, і побачить це
    /// вже користувач. Регістр значущий — так само, як у підстановці.
    /// </remarks>
    /// <param name="reference">Текст мовою за замовчуванням.</param>
    /// <param name="translation">Переклад.</param>
    public static bool SamePlaceholders(string reference, string translation)
        => Placeholders(reference).SequenceEqual(Placeholders(translation), StringComparer.Ordinal);

    [System.Text.RegularExpressions.GeneratedRegex(@"\{(\w+)\}")]
    private static partial System.Text.RegularExpressions.Regex PlaceholderPattern();

    /// <summary>
    /// <c>ETag</c> області.
    /// </summary>
    /// <remarks>
    /// ⚠ Версія каталогу одна на систему (<c>sys_ecr.UiStringRevision</c> має
    /// <c>CHECK (Id = 1)</c>), тому область мусить входити в сам <c>ETag</c>.
    /// Інакше клієнт, що закешував публічний зріз, отримав би <c>304</c> на
    /// запит приватного — і показав би сторінку входу замість застосунку.
    /// </remarks>
    /// <param name="scope">Область.</param>
    /// <param name="languageCode">Мова: різні мови — різний вміст.</param>
    /// <param name="revision">Версія каталогу.</param>
    public static string ETag(UiStringScope scope, string languageCode, int revision)
        => $"\"{scope.ToString().ToLowerInvariant()}-{languageCode}-{revision}\"";

    /// <summary>Чи збігається <c>If-None-Match</c> із поточним <c>ETag</c>.</summary>
    /// <param name="ifNoneMatch">Заголовок запиту; може містити кілька значень.</param>
    /// <param name="currentETag">Поточний <c>ETag</c> області.</param>
    /// <returns><c>true</c> — віддавати <c>304</c>.</returns>
    public static bool IsNotModified(string? ifNoneMatch, string currentETag)
    {
        if (string.IsNullOrWhiteSpace(ifNoneMatch))
        {
            return false;
        }

        foreach (var candidate in ifNoneMatch.Split(','))
        {
            var trimmed = candidate.Trim();

            // Слабкий валідатор (`W/"…"`) для каталогу рівносильний сильному:
            // байтова тотожність нас не цікавить, збіг версії — цілком.
            if (trimmed.StartsWith("W/", StringComparison.Ordinal))
            {
                trimmed = trimmed[2..];
            }

            if (string.Equals(trimmed, currentETag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
