using System.Text.RegularExpressions;
using Ecr.Application.Ports;

namespace Ecr.Application.Localization;

/// <summary>
/// ✎ integ-row-label-messages (live doc 100: «Row adc95dc2bdb84fa0bce2d80e0defc581 (B12 diesel generator DG-1)»): ключ рядка,
/// доданого людиною, — GUID без дефісів (32 hex); у тексті для людини він не показується ніколи. Єдине місце, де це
/// правило записано для серверних повідомлень (<c>messageKey</c> + подробиці, повідомлення валідації): підстановки з
/// ключами рядків проходять <see cref="Humanize"/>, а сторож-тест (<c>RowKeyTextGuardTests</c>) тримає, що жоден шаблон
/// не бере ключ рядка поза цим шляхом.
/// </summary>
/// <remarks>
/// Короткі коди рядків шаблону («LTD-A02», «F_B12») — як є: це підпис, який людина бачить на екрані. Рядок, якому відомі
/// назва чи номер (дозвіли, блокування), називається власним параметром <c>{row}</c> виробника повідомлення.
/// </remarks>
public static partial class RowKeyText
{
    /// <summary>Ключ каталогу: «(added by a user)» — замість службового ключа в реченні «row {rowKey}».</summary>
    public const string AddedByUserKey = "row.addedByUser";

    /// <summary>
    /// Підстановки повідомлень, у яких можуть лежати ключі рядків (текстом; перелік — через кому). <c>row</c> — назва чи
    /// номер рядка від виробника, але й ключ (діагностика узгодженості); заміна чіпає лише 24+ hex поспіль.
    /// </summary>
    public static readonly IReadOnlyList<string> KeyParameters = ["rowKey", "rowKeys", "sourceRow", "row"];

    /// <summary>
    /// Подробиця відмови з назвами рядків (<see cref="EncodeNames"/>): ключ → «N (назва)». Виробник, що знає номер і підпис
    /// рядка (<c>RowDisplayNames</c>), кладе її поруч із сирим ключем; <see cref="Humanize"/> підставляє назву в текст.
    /// </summary>
    public const string RowNamesParameter = "rowNames";

    /// <summary>Кодує назви в один рядок: <c>ключ	назва</c>, записи через новий рядок (назви без табуляцій і переносів).</summary>
    /// <param name="names">Ключ → назва.</param>
    public static string EncodeNames(IReadOnlyDictionary<string, string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        static string Clean(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

        return string.Join('\n', names.Select(pair => $"{pair.Key}\t{Clean(pair.Value)}"));
    }

    private static Dictionary<string, string> DecodeNames(string? encoded)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in (encoded ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab > 0)
            {
                result[line[..tab]] = line[(tab + 1)..];
            }
        }

        return result;
    }

    /// <summary>Запасні тексти (мова без каталогу — повідомлення «в момент запуску»).</summary>
    private static string Fallback(string? language) => language switch
    {
        "ru" => "(добавлена пользователем)",
        "kz" => "(пайдаланушы қосқан)",
        _ => "(added by a user)",
    };

    [GeneratedRegex("(?<![0-9A-Za-z])[0-9a-fA-F]{24,}(?![0-9A-Za-z])")]
    private static partial Regex Opaque();

    /// <summary>Службові ключі рядків у значеннях подробиць відмови (<see cref="KeyParameters"/>) — без повторів.</summary>
    /// <param name="details">Подробиці відмови.</param>
    public static IReadOnlyList<string> OpaqueKeysIn(IReadOnlyDictionary<string, object?>? details)
    {
        if (details is null)
        {
            return [];
        }

        return [.. KeyParameters
            .Select(name => details.TryGetValue(name, out var value) ? value as string : null)
            .Where(text => !string.IsNullOrEmpty(text))
            .SelectMany(text => Opaque().Matches(text!).Select(m => m.Value))
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Чи містить текст службовий ключ рядка (GUID без дефісів).</summary>
    /// <param name="text">Текст або значення підстановки.</param>
    public static bool ContainsOpaqueKey(string? text) => !string.IsNullOrEmpty(text) && Opaque().IsMatch(text);

    /// <summary>
    /// Підстановки, у яких службові ключі рядків замінено позначкою «(added by a user)» мовою читача; короткі коди —
    /// без змін. Нічого замінювати — повертає ті самі <paramref name="parameters"/>.
    /// </summary>
    /// <param name="parameters">Підстановки шаблону.</param>
    /// <param name="language">Мова (для запасного тексту).</param>
    /// <param name="catalog">Каталог мови читача; <c>null</c> — запасні тексти.</param>
    public static IReadOnlyDictionary<string, string> Humanize(
        IReadOnlyDictionary<string, string> parameters, string? language, UiStringCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        Dictionary<string, string>? result = null;
        string? phrase = null;
        var names = parameters.TryGetValue(RowNamesParameter, out var encoded) ? DecodeNames(encoded) : null;
        foreach (var name in KeyParameters)
        {
            if (!parameters.TryGetValue(name, out var value) || !ContainsOpaqueKey(value))
            {
                continue;
            }

            phrase ??= catalog is not null && catalog.Strings.TryGetValue(AddedByUserKey, out var text) && !string.IsNullOrWhiteSpace(text)
                ? text
                : Fallback(language);
            result ??= new Dictionary<string, string>(parameters, StringComparer.Ordinal);
            result[name] = Opaque().Replace(value, match => names is not null && names.TryGetValue(match.Value, out var named) ? named : phrase);
        }

        return result ?? parameters;
    }
}
