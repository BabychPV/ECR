namespace Ecr.Application.Localization;

/// <summary>
/// Переведення між ТЕГОМ мови BCP-47 (як його надсилає зовнішній світ) і
/// внутрішнім КОДОМ мови з реєстру <c>sys_ecr.Language</c>.
/// </summary>
/// <remarks>
/// ⛔ Два алфавіти справді різні, і це не теорія. Казахська в реєстрі записана
/// кодом <c>kz</c> — це код КРАЇНИ (ISO 3166), історично використаний як код
/// мови; тег BCP-47 тієї самої мови — <c>kk</c> (ISO 639-1). Браузер із
/// казахською локаллю надсилає <c>Accept-Language: kk-KZ</c>, і зріз до
/// первинного субтега дає <c>kk</c> — код, якого в реєстрі НЕМАЄ. Для решти
/// мов (<c>en</c>, <c>ru</c>) тег і код збігаються, тому розбіжність не
/// помітна ніде, крім єдиної мови, заради якої вона й виникла.
///
/// ⚠ Одне джерело відповідності на весь сервер, а не літерал у кожному місці
/// зчитування: друга рукописна копія розійшлася б із першою непомітно —
/// обидві виглядали б правдоподібно. Клієнт тримає свою копію тієї самої
/// відповідності (<c>LanguageTagOverrides</c> у <c>shared/i18n/index.ts</c>)
/// і так само будує зворотний напрямок інверсією, а не другим літералом.
///
/// ⚠ Це НЕ перелік дозволених мов. Невідомий тег проходить як є: інакше
/// четверта мова в реєстрі вимагала б правки коду й перезбирання — рівно те,
/// що забороняє «додати мову = запис у реєстр, а не збірка» (<c>ФВ-2.2</c>,
/// <c>ФВ-14.9</c>, <c>D-95</c>).
///
/// ⛔ Межа свідомо вузька: переводиться лише ВХІД. Реєстр не мігрується
/// (<c>kz</c> → <c>kk</c>) — це зачепило б <c>sys_ecr.Language</c>, ключі
/// <c>LocalizedText</c> у JSON документів, збережені налаштування
/// користувачів і вже видані токени, тобто міграцію даних. Ціна вузької межі:
/// поки реєстр лишається на <c>kz</c>, кожне нове місце, де код мови
/// приходить ззовні, мусить пройти через <see cref="FromTag"/> — і про це
/// легко забути, бо для <c>en</c>/<c>ru</c> пропуск непомітний.
/// </remarks>
public static class LanguageCodes
{
    /// <summary>
    /// Єдиний літерал відповідності: внутрішній код → тег BCP-47.
    /// </summary>
    /// <remarks>
    /// Напрямок «код → тег» канонічний тому, що ключ тут — те, що записано в
    /// реєстрі, а тег є похідним поданням назовні. Зворотний пошук будується
    /// інверсією саме цієї мапи (див. <see cref="CodeByTag"/>).
    /// </remarks>
    private static readonly Dictionary<string, string> TagByCode =
        new(StringComparer.OrdinalIgnoreCase) { ["kz"] = "kk" };

    /// <summary>Інверсія <see cref="TagByCode"/>: тег BCP-47 → внутрішній код.</summary>
    private static readonly Dictionary<string, string> CodeByTag =
        TagByCode.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Внутрішній код мови для тега BCP-47.
    /// </summary>
    /// <param name="tag">
    /// Тег BCP-47, із регіоном або без нього (<c>kk-KZ</c>, <c>kk</c>,
    /// <c>ru-RU</c>). Регістр не має значення. Уже відкинути вагу
    /// (<c>;q=…</c>) має викликач: ваги — синтаксис заголовка HTTP, а не тега.
    /// </param>
    /// <returns>
    /// Код у нижньому регістрі; порожній рядок, якщо тега фактично немає.
    /// Невідомий тег повертається як є (див. ⚠ вище).
    /// </returns>
    public static string FromTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return string.Empty;
        }

        // Первинний субтег: регіон/письмо для вибору мови значення не мають —
        // реєстр не розрізняє «kk-KZ» і «kk-Cyrl-KZ».
        var primary = tag.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .FirstOrDefault();

        return string.IsNullOrWhiteSpace(primary)
            ? string.Empty
            : CodeByTag.TryGetValue(primary, out var code) ? code : primary.ToLowerInvariant();
    }
}
