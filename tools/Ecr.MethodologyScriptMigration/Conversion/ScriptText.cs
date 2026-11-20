using System.Text;
using System.Text.RegularExpressions;
using Ecr.Scripting.Lexing;

namespace Ecr.MethodologyScriptMigration.Conversion;

/// <summary>Дрібні правила тексту «ECR Script»: ідентифікатори, рядки.</summary>
public static partial class ScriptText
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\z")]
    private static partial Regex IdentPattern();

    /// <summary>Чи має ім'я форму ідентифікатора (ключове слово теж підходить).</summary>
    /// <param name="name">Ім'я.</param>
    public static bool IsIdentifier(string? name) => name is not null && IdentPattern().IsMatch(name);

    /// <summary>Чи це ідентифікатор мови (не ключове слово).</summary>
    /// <param name="name">Ім'я.</param>
    public static bool IsName(string? name)
        => name is not null && IdentPattern().IsMatch(name) && !ScriptLexer.Keywords.Contains(name);

    /// <summary>Чи це ключове слово мови.</summary>
    /// <param name="name">Ім'я.</param>
    public static bool IsKeyword(string name) => ScriptLexer.Keywords.Contains(name);

    /// <summary>
    /// Ключ категорії без пробільних символів: так старий рушій порівнює ключ правила з категоріями констант
    /// (<c>GenericCalculationModule.IsKnownCategory</c>, <c>ConstantResolver</c>); у скрипті всі ключі пишуться саме так.
    /// </summary>
    /// <param name="key">Ключ.</param>
    public static string BareCategory(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return string.Concat(key.Where(ch => !char.IsWhiteSpace(ch)));
    }

    /// <summary>Рядковий літерал у лапках із екрануванням <c>\\ \" \n</c>.</summary>
    /// <param name="value">Значення.</param>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }

        return sb.Append('"').ToString();
    }
}
