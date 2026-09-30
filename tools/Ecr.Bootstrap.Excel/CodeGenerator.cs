using System.Globalization;
using System.Text;

namespace Ecr.Bootstrap.Excel;

/// <summary>
/// Коди аркушів, таблиць, колонок і ключі рядків із тексту книги.
/// </summary>
/// <remarks>
/// ⚠ Код — ідентичність (ФВ-2.8): на нього посилаються формули, мапінги і
/// клони версій. Тому він виводиться детерміновано з тексту (транслітерація
/// кирилиці), а не з позиції: вставка колонки в книзі не має перейменовувати
/// всі наступні. Збіг у межах батька розводиться суфіксом <c>_2</c>, <c>_3</c>.
/// </remarks>
public sealed class CodeGenerator
{
    /// <summary>Найдовший код, який генерується (межа <c>EcrCode</c> — 64).</summary>
    public const int MaxLength = 40;

    private static readonly Dictionary<char, string> Translit = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "h", ['ґ'] = "g", ['д'] = "d", ['е'] = "e",
        ['є'] = "ie", ['ж'] = "zh", ['з'] = "z", ['и'] = "y", ['і'] = "i", ['ї'] = "i", ['й'] = "i",
        ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r",
        ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch",
        ['ш'] = "sh", ['щ'] = "shch", ['ь'] = "", ['ю'] = "iu", ['я'] = "ia",
        ['ё'] = "e", ['ы'] = "y", ['э'] = "e", ['ъ'] = "",
        ['ә'] = "a", ['ғ'] = "g", ['қ'] = "q", ['ң'] = "n", ['ө'] = "o", ['ұ'] = "u", ['ү'] = "u", ['һ'] = "h",
    };

    private readonly HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Код, унікальний серед виданих цим екземпляром (екземпляр — на один батьківський об'єкт).
    /// </summary>
    /// <param name="text">Текст, з якого виводиться код.</param>
    /// <param name="fallbackPrefix">Префікс, якщо з тексту нічого не лишилось (<c>C</c> → <c>C3</c>).</param>
    /// <param name="ordinal">Позиція — для запасного коду.</param>
    /// <returns>Код за шаблоном <c>^[A-Za-z][A-Za-z0-9_]*$</c>.</returns>
    public string Next(string? text, string fallbackPrefix, int ordinal)
    {
        var baseCode = Slug(text);
        if (baseCode.Length == 0)
        {
            baseCode = fallbackPrefix + ordinal.ToString(CultureInfo.InvariantCulture);
        }
        else if (!char.IsAsciiLetter(baseCode[0]))
        {
            baseCode = fallbackPrefix + "_" + baseCode;
        }

        if (baseCode.Length > MaxLength)
        {
            baseCode = baseCode[..MaxLength].TrimEnd('_');
        }

        var code = baseCode;
        for (var n = 2; !_taken.Add(code); n++)
        {
            code = baseCode + "_" + n.ToString(CultureInfo.InvariantCulture);
        }

        return code;
    }

    /// <summary>
    /// Транслітерує текст і лишає лише латиницю, цифри й <c>_</c>, у верхньому регістрі.
    /// </summary>
    /// <param name="text">Вихідний текст.</param>
    /// <returns>Слаг; порожній, якщо з тексту нічого не лишилось.</returns>
    public static string Slug(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        foreach (var raw in text.Trim())
        {
            var ch = char.ToLowerInvariant(raw);
            if (Translit.TryGetValue(ch, out var latin))
            {
                sb.Append(latin);
            }
            else if (char.IsAsciiLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else if (sb.Length > 0 && sb[^1] != '_')
            {
                sb.Append('_');
            }
        }

        return sb.ToString().Trim('_').ToUpperInvariant();
    }
}
