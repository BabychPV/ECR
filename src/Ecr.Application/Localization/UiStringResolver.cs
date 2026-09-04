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
public static class UiStringResolver
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
