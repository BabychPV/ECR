using System.Globalization;

namespace Ecr.Expressions.Lexing;

/// <summary>Лексичний аналізатор виразів.</summary>
/// <param name="syntax">
/// Синтаксис діалекту: звідси береться символ роздільника аргументів.
/// </param>
/// <remarks>
/// ⛔ Лексер залежить від діалекту саме через <see cref="DialectSyntax"/>, а не
/// через <c>ExpressionDialect</c>: інакше кожна нова діалектна відмінність
/// дописувала б сюди ще одне <c>if (dialect == …)</c>, і роздільник знову
/// виявився б літералом — тепер уже в двох гілках замість однієї.
/// </remarks>
public sealed class Lexer(DialectSyntax syntax)
{
    /// <summary>Ключові слова; порівняння регістронезалежне (02b §1).</summary>
    private static readonly Dictionary<string, TokenType> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AND"] = TokenType.And,
        ["OR"] = TokenType.Or,
        ["NOT"] = TokenType.Not,
        ["TRUE"] = TokenType.Boolean,
        ["FALSE"] = TokenType.Boolean,
        ["NULL"] = TokenType.Null,
    };

    /// <summary>Розбиває вираз на лексеми.</summary>
    /// <param name="expression">Текст виразу.</param>
    /// <returns>Послідовність лексем, остання — <see cref="TokenType.EndOfInput"/>.</returns>
    /// <exception cref="LexicalException">Недопустимий символ або незакритий рядок.</exception>
    public IReadOnlyList<Token> Tokenize(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        var tokens = new List<Token>();

        // ⚠ Глибина дужок — не оптимізація, а вимога граматики. Усередині
        // `[...]` живуть RowKey (`7001001`), які починаються з цифри й містять
        // дефіси; поза дужками таке — недопустимий ідентифікатор (R-B6).
        // Одного прапорця мало: предикат `[WHERE [Category] = 'Fuel']` вкладає
        // дужки в дужки.
        var depth = 0;
        var i = 0;

        while (i < expression.Length)
        {
            var c = expression[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            var start = i;

            switch (c)
            {
                case '[':
                    depth++;
                    tokens.Add(new Token(TokenType.LBracket, "[", start, 1));
                    i++;
                    continue;

                case ']':
                    if (depth == 0)
                    {
                        throw new LexicalException("Закривна дужка ']' без відкривної.", start);
                    }

                    depth--;
                    tokens.Add(new Token(TokenType.RBracket, "]", start, 1));
                    i++;
                    continue;

                case '\'':
                    tokens.Add(ReadString(expression, ref i));
                    continue;

                case '{':
                    // Плейсхолдер колонки: `{Month}`, `{Period}` (02b §1).
                    tokens.Add(ReadPlaceholder(expression, ref i));
                    continue;
            }

            if (char.IsDigit(c))
            {
                tokens.Add(ReadNumberOrKey(expression, ref i, depth, tokens));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                tokens.Add(ReadWord(expression, ref i, depth));
                continue;
            }

            tokens.Add(ReadOperator(expression, ref i, syntax.ArgumentSeparator));
        }

        if (depth != 0)
        {
            throw new LexicalException("Незакрита квадратна дужка.", expression.Length);
        }

        tokens.Add(new Token(TokenType.EndOfInput, string.Empty, expression.Length, 0));
        return tokens;
    }

    /// <summary>Рядковий літерал; подвоєний апостроф дає один символ.</summary>
    private static Token ReadString(string s, ref int i)
    {
        var start = i;
        i++;                                  // відкривний апостроф
        var text = new System.Text.StringBuilder();

        while (true)
        {
            if (i >= s.Length)
            {
                throw new LexicalException("Незакритий рядковий літерал.", start);
            }

            if (s[i] == '\'')
            {
                if (i + 1 < s.Length && s[i + 1] == '\'')
                {
                    text.Append('\'');
                    i += 2;
                    continue;
                }

                i++;
                break;
            }

            text.Append(s[i]);
            i++;
        }

        return new Token(TokenType.String, text.ToString(), start, i - start);
    }

    /// <summary>Плейсхолдер поточної колонки: <c>{Month}</c>, <c>{Period}</c>.</summary>
    private static Token ReadPlaceholder(string s, ref int i)
    {
        var start = i;
        var close = s.IndexOf('}', i);
        if (close < 0)
        {
            throw new LexicalException("Незакритий плейсхолдер '{'.", start);
        }

        i = close + 1;
        return new Token(TokenType.Identifier, s[start..i], start, i - start);
    }

    /// <summary>
    /// Число поза дужками — або <c>RowKey</c> усередині них.
    /// </summary>
    /// <remarks>
    /// ⚠ Формат числа **інваріантний**: роздільник тільки <c>.</c>, розділювачів
    /// тисяч немає. Кома десятковим роздільником не є в жодній культурі — саме
    /// тому парсинг іде через <see cref="CultureInfo.InvariantCulture"/>, а не
    /// через поточну культуру потоку.
    /// </remarks>
    private static Token ReadNumberOrKey(string s, ref int i, int depth, List<Token> emitted)
    {
        var start = i;

        // ⚠ Усередині дужок цифра означає RowKey ЛИШЕ в позиції селектора
        // рядків — тобто одразу після `[` або після `:` у діапазоні. У решті
        // позицій це звичайне число: у предикаті `[WHERE [Amount] > 1.5]`
        // прочитати `1.5` ключем рядка означало б не розібрати умову взагалі.
        var isRowKeyPosition = depth > 0
            && emitted.Count > 0
            && emitted[^1].Type is TokenType.LBracket or TokenType.Colon;

        if (isRowKeyPosition)
        {
            // Усередині дужок цифра починає RowKey: цифри, літери, дефіси,
            // підкреслення. Крапка НЕ входить — вона розділяє ланки посилання.
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '-'))
            {
                i++;
            }

            return new Token(TokenType.Identifier, s[start..i], start, i - start);
        }

        while (i < s.Length && char.IsDigit(s[i]))
        {
            i++;
        }

        if (i < s.Length && s[i] == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1]))
        {
            i++;
            while (i < s.Length && char.IsDigit(s[i]))
            {
                i++;
            }
        }

        // `7abc` поза дужками — не число і не ідентифікатор: R-B6 забороняє
        // EcrCode, що починається з цифри. Мовчки віддати `7` і `abc` двома
        // лексемами означало б розібрати помилку як конкатенацію.
        if (i < s.Length && (char.IsLetter(s[i]) || s[i] == '_'))
        {
            throw new LexicalException(
                "Ідентифікатор не може починатися з цифри (R-B6).", start);
        }

        var text = s[start..i];
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _))
        {
            throw new LexicalException($"Некоректне число '{text}'.", start);
        }

        return new Token(TokenType.Number, text, start, i - start);
    }

    /// <summary>Ідентифікатор або ключове слово.</summary>
    private static Token ReadWord(string s, ref int i, int depth)
    {
        var start = i;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || (depth > 0 && s[i] == '-')))
        {
            i++;
        }

        var text = s[start..i];

        // Усередині дужок ключових слів немає: `[AND]` — це код колонки з
        // такою назвою, а не оператор. Виняток лише для предиката, який
        // парсер розбирає окремо за токенами Identifier.
        if (depth == 0 && Keywords.TryGetValue(text, out var keyword))
        {
            return new Token(keyword, text, start, i - start);
        }

        if (depth > 0 && (text.Equals("AND", StringComparison.OrdinalIgnoreCase)
                          || text.Equals("OR", StringComparison.OrdinalIgnoreCase)
                          || text.Equals("NOT", StringComparison.OrdinalIgnoreCase)))
        {
            return new Token(Keywords[text], text, start, i - start);
        }

        return new Token(TokenType.Identifier, text, start, i - start);
    }

    /// <summary>Оператор або розділовий знак.</summary>
    private static Token ReadOperator(string s, ref int i, char argumentSeparator)
    {
        var start = i;

        // Двосимвольні — перевіряються ПЕРШИМИ, інакше `<=` розпадається на
        // `<` і `=`, а `!=` на `!` і `=`.
        if (i + 1 < s.Length)
        {
            var two = s.Substring(i, 2);
            var type = two switch
            {
                "==" => TokenType.Equal,
                "<>" => TokenType.NotEqual,
                "!=" => TokenType.NotEqual,
                "<=" => TokenType.LessOrEqual,
                ">=" => TokenType.GreaterOrEqual,
                "&&" => TokenType.And,
                "||" => TokenType.Or,
                _ => (TokenType?)null,
            };

            if (type is not null)
            {
                i += 2;
                return new Token(type.Value, two, start, 2);
            }
        }

        // ⛔ Роздільник аргументів перевіряється ОКРЕМО від таблиці нижче, бо
        // він єдиний, чий символ задає діалект, а не граматика. У таблиці він
        // був би літералом `,` — тобто твердженням, що кома означає роздільник
        // у будь-якому діалекті, хоча діалект A цього питання ще не вирішив.
        if (s[i] == argumentSeparator)
        {
            i++;
            return new Token(TokenType.ArgumentSeparator, s[start..i], start, 1);
        }

        var single = s[i] switch
        {
            '(' => TokenType.LParen,
            ')' => TokenType.RParen,
            '.' => TokenType.Dot,
            ':' => TokenType.Colon,
            '?' => TokenType.Question,
            '@' => TokenType.At,
            '!' => TokenType.Bang,
            '&' => TokenType.Ampersand,
            '+' => TokenType.Plus,
            '-' => TokenType.Minus,
            '*' => TokenType.Star,
            '/' => TokenType.Slash,
            '%' => TokenType.Percent,
            '^' => TokenType.Caret,
            '=' => TokenType.Equal,
            '<' => TokenType.Less,
            '>' => TokenType.Greater,
            _ => (TokenType?)null,
        };

        if (single is null)
        {
            throw new LexicalException($"Недопустимий символ '{s[i]}'.", start);
        }

        i++;
        return new Token(single.Value, s[start..i], start, 1);
    }
}

/// <summary>Помилка лексичного аналізу.</summary>
public sealed class LexicalException(string message, int position) : Exception(message)
{
    /// <summary>Позиція проблемного символу.</summary>
    public int Position { get; } = position;
}
