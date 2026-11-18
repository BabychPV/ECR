using System.Globalization;
using System.Text;

namespace Ecr.Scripting.Lexing;

/// <summary>Результат лексичного аналізу.</summary>
/// <param name="Tokens">Лексеми; остання — <see cref="ScriptTokenKind.EndOfInput"/>.</param>
/// <param name="Diagnostics">Лексичні діагностики.</param>
public sealed record ScriptLexResult(IReadOnlyList<ScriptToken> Tokens, IReadOnlyList<ScriptDiagnostic> Diagnostics);

/// <summary>
/// Лексер «ECR Script» (§3.2, §13.3). Помилки — діагностики; лексер ніколи не кидає винятків.
/// </summary>
/// <remarks>
/// Ідентифікатор може містити шаблонні фрагменти <c>{Ident}</c> після хоча б одного символу
/// (<c>ECW_EC_gsec_{S}</c>) — їх підставляє розгортання <c>foreach</c> у парсері.
/// </remarks>
public static class ScriptLexer
{
    /// <summary>Зарезервовані слова: не можуть бути іменами.</summary>
    public static readonly IReadOnlySet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "import", "rows", "from", "where", "active", "when", "const", "var", "visible", "function", "output",
        "per", "if", "else", "foreach", "substance", "in", "reject", "skip", "true", "false", "null",
        "row", "period", "header", "key", "entry", "table", "param",
    };

    /// <summary>Розбиває текст на лексеми.</summary>
    /// <param name="text">Текст скрипта.</param>
    public static ScriptLexResult Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new Impl(text).Run();
    }

    private sealed class Impl(string text)
    {
        private readonly List<ScriptToken> tokens = [];
        private readonly List<ScriptDiagnostic> diagnostics = [];
        private readonly List<int> lineStarts = BuildLineStarts(text);
        private int i;
        private bool pendingInvalid;

        public ScriptLexResult Run()
        {
            var n = text.Length;
            while (i < n)
            {
                ScriptWork.Step();
                var c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                }
                else if (c == '/' && Peek(1) == '/')
                {
                    while (i < n && text[i] != '\n')
                    {
                        i++;
                    }
                }
                else if (c == '/' && Peek(1) == '*')
                {
                    BlockComment();
                }
                else if (IsIdentStart(c))
                {
                    Identifier();
                }
                else if (c is >= '0' and <= '9')
                {
                    Number();
                }
                else if (c == '.' && Peek(1) is >= '0' and <= '9')
                {
                    LeadingDotNumber();
                }
                else if (c is '"' or '\'')
                {
                    String(c);
                }
                else
                {
                    Operator(c);
                }
            }

            var end = Span(n, 0);
            tokens.Add(new ScriptToken(ScriptTokenKind.EndOfInput, string.Empty, n, 0, end.Line, end.Column));
            return new ScriptLexResult(tokens, diagnostics);
        }

        private static List<int> BuildLineStarts(string source)
        {
            var starts = new List<int> { 0 };
            for (var k = 0; k < source.Length; k++)
            {
                if (source[k] == '\n')
                {
                    starts.Add(k + 1);
                }
            }

            return starts;
        }

        private static bool IsIdentStart(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_';

        private static bool IsIdentPart(char c) => IsIdentStart(c) || c is >= '0' and <= '9';

        private char Peek(int offset) => i + offset < text.Length ? text[i + offset] : '\0';

        private ScriptSpan Span(int start, int length)
        {
            var index = lineStarts.BinarySearch(start);
            if (index < 0)
            {
                index = ~index - 1;
            }

            return new ScriptSpan(start, length, index + 1, start - lineStarts[index] + 1);
        }

        private void Add(ScriptTokenKind kind, int start, int length, object? value = null)
        {
            var s = Span(start, length);
            tokens.Add(new ScriptToken(kind, text.Substring(start, length), start, length, s.Line, s.Column, value, pendingInvalid));
            pendingInvalid = false;
        }

        private void Report(string code, int start, int length, string message, params (string, string)[] pairs)
        {
            if (diagnostics.Count < ScriptDiagnostic.MaxPerCompilation)
            {
                diagnostics.Add(ScriptDiagnostic.At(code, Span(start, length), message, pairs));
            }
        }

        private void BlockComment()
        {
            var start = i;
            var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                Report(ScriptDiagnosticCodes.UnterminatedComment, start, 2, "Comment is not closed: add '*/'.");
                i = text.Length;
                return;
            }

            i = close + 2;
        }

        private void Identifier()
        {
            var start = i;
            while (i < text.Length)
            {
                if (IsIdentPart(text[i]))
                {
                    i++;
                }
                else if (text[i] == '{' && TemplateLength(i) is var len and > 0)
                {
                    i += len;
                }
                else
                {
                    break;
                }
            }

            var word = text[start..i];
            Add(Keywords.Contains(word) ? ScriptTokenKind.Keyword : ScriptTokenKind.Identifier, start, i - start);
        }

        /// <summary>Довжина шаблонного фрагмента <c>{Ident}</c> у позиції; 0 — це не фрагмент.</summary>
        private int TemplateLength(int at)
        {
            if (at == 0 || !(IsIdentPart(text[at - 1]) || text[at - 1] == '}'))
            {
                return 0;
            }

            var k = at + 1;
            if (k >= text.Length || !IsIdentStart(text[k]))
            {
                return 0;
            }

            while (k < text.Length && IsIdentPart(text[k]))
            {
                k++;
            }

            return k < text.Length && text[k] == '}' ? k - at + 1 : 0;
        }

        private void Number()
        {
            var start = i;
            var invalid = false;
            while (Peek(0) is >= '0' and <= '9')
            {
                i++;
            }

            if (Peek(0) == '.')
            {
                i++;
                if (Peek(0) is >= '0' and <= '9')
                {
                    while (Peek(0) is >= '0' and <= '9')
                    {
                        i++;
                    }
                }
                else
                {
                    invalid = true; // «1.» — цифри після крапки обов'язкові
                }
            }

            Exponent();
            FinishNumber(start, invalid);
        }

        private void LeadingDotNumber()
        {
            var start = i;
            i++;
            while (Peek(0) is >= '0' and <= '9')
            {
                i++;
            }

            Exponent();
            FinishNumber(start, true); // «.5» — перед крапкою потрібна цифра
        }

        private void Exponent()
        {
            if (Peek(0) is not ('e' or 'E'))
            {
                return;
            }

            var sign = Peek(1) is '+' or '-' ? 1 : 0;
            if (Peek(1 + sign) is >= '0' and <= '9')
            {
                i += 1 + sign;
                while (Peek(0) is >= '0' and <= '9')
                {
                    i++;
                }
            }
        }

        private void FinishNumber(int start, bool invalid)
        {
            var raw = text[start..i];
            var parseable = raw;
            if (parseable.StartsWith('.'))
            {
                parseable = "0" + parseable;
            }

            parseable = parseable.Replace(".e", "e", StringComparison.OrdinalIgnoreCase);
            if (parseable.EndsWith('.'))
            {
                parseable = parseable[..^1];
            }

            var ok = decimal.TryParse(parseable, NumberStyles.Float, CultureInfo.InvariantCulture, out var value);
            if (invalid)
            {
                Report(ScriptDiagnosticCodes.InvalidNumber, start, i - start,
                    $"Invalid number '{raw}': digits are required on both sides of the decimal point.",
                    ("text", raw));
            }
            else if (!ok)
            {
                Report(ScriptDiagnosticCodes.InvalidNumber, start, i - start,
                    $"Number '{raw}' is out of the decimal range.", ("text", raw));
            }
            else if (!IsExact(parseable, value))
            {
                // decimal тримає 28 значущих цифр і мовчки округлює решту (а надто малі числа — до 0): для коефіцієнтів
                // методологій тиха зміна значення неприпустима.
                Report(ScriptDiagnosticCodes.InvalidNumber, start, i - start,
                    $"Number '{raw}' has more significant digits than decimal can hold (28), so its value would change.",
                    ("text", raw));
            }

            Add(ScriptTokenKind.NumberLiteral, start, i - start, value);
        }

        /// <summary>
        /// Чи <paramref name="value"/> дорівнює числу в тексті точно (хвостові нулі не рахуються): порівнюються значущі
        /// цифри й десятковий порядок. Текст — лексема без знака: <c>цифри[.цифри][e[±]цифри]</c>.
        /// </summary>
        private static bool IsExact(string parseable, decimal value)
        {
            var e = parseable.AsSpan().IndexOfAny('e', 'E');
            var mantissa = e < 0 ? parseable.AsSpan() : parseable.AsSpan(0, e);
            var dot = mantissa.IndexOf('.');
            var fraction = dot < 0 ? 0 : mantissa.Length - dot - 1;
            var digits = new StringBuilder(mantissa.Length);
            foreach (var c in mantissa)
            {
                if (c != '.')
                {
                    digits.Append(c);
                }
            }

            var all = digits.ToString().TrimStart('0');
            if (all.Length == 0)
            {
                return true; // нуль у тексті — нуль точно
            }

            var significant = all.TrimEnd('0');
            long order = -fraction + (all.Length - significant.Length);
            if (e >= 0)
            {
                if (!long.TryParse(parseable.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var exponent))
                {
                    return false;
                }

                order += exponent;
            }

            var bits = decimal.GetBits(value);
            var mid = ((UInt128)(uint)bits[2] << 64) | ((UInt128)(uint)bits[1] << 32) | (uint)bits[0];
            var scale = (bits[3] >> 16) & 0xFF;
            var actual = mid.ToString(CultureInfo.InvariantCulture);
            var actualSignificant = actual.TrimEnd('0');
            long actualOrder = -scale + (actual.Length - actualSignificant.Length);

            return mid != 0 && significant == actualSignificant && order == actualOrder;
        }

        private void String(char quote)
        {
            var start = i;
            i++;
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= text.Length || text[i] == '\n' || (text[i] == '\r' && Peek(1) == '\n'))
                {
                    Report(ScriptDiagnosticCodes.UnterminatedString, start, i - start,
                        "String is not closed: add the closing quote.", ("quote", quote.ToString()));
                    Add(ScriptTokenKind.StringLiteral, start, i - start, sb.ToString());
                    return;
                }

                var c = text[i];
                if (c == quote)
                {
                    i++;
                    Add(ScriptTokenKind.StringLiteral, start, i - start, sb.ToString());
                    return;
                }

                if (c == '\\' && i + 1 < text.Length && text[i + 1] != '\n')
                {
                    var next = text[i + 1];
                    switch (next)
                    {
                        case '"' or '\'' or '\\':
                            sb.Append(next);
                            break;
                        case 'n':
                            sb.Append('\n');
                            break;
                        default:
                            sb.Append(c).Append(next); // невідоме екранування лишається як є
                            break;
                    }

                    i += 2;
                    continue;
                }

                sb.Append(c);
                i++;
            }
        }

        private void Operator(char c)
        {
            var start = i;
            var next = Peek(1);
            switch (c)
            {
                case '=' when next == '=':
                    i += 2;
                    Add(ScriptTokenKind.EqualEqual, start, 2);
                    return;
                case '!' when next == '=':
                    i += 2;
                    Add(ScriptTokenKind.NotEqual, start, 2);
                    return;
                case '<' when next == '=':
                    i += 2;
                    Add(ScriptTokenKind.LessOrEqual, start, 2);
                    return;
                case '>' when next == '=':
                    i += 2;
                    Add(ScriptTokenKind.GreaterOrEqual, start, 2);
                    return;
                case '&' when next == '&':
                    i += 2;
                    Add(ScriptTokenKind.AndAnd, start, 2);
                    return;
                case '|' when next == '|':
                    i += 2;
                    Add(ScriptTokenKind.OrOr, start, 2);
                    return;
                default:
                    break;
            }

            var single = c switch
            {
                '+' => ScriptTokenKind.Plus,
                '-' => ScriptTokenKind.Minus,
                '*' => ScriptTokenKind.Star,
                '/' => ScriptTokenKind.Slash,
                '%' => ScriptTokenKind.Percent,
                '<' => ScriptTokenKind.Less,
                '>' => ScriptTokenKind.Greater,
                '!' => ScriptTokenKind.Bang,
                '?' => ScriptTokenKind.Question,
                ':' => ScriptTokenKind.Colon,
                '(' => ScriptTokenKind.LParen,
                ')' => ScriptTokenKind.RParen,
                '[' => ScriptTokenKind.LBracket,
                ']' => ScriptTokenKind.RBracket,
                '{' => ScriptTokenKind.LBrace,
                '}' => ScriptTokenKind.RBrace,
                ',' => ScriptTokenKind.Comma,
                ';' => ScriptTokenKind.Semicolon,
                '.' => ScriptTokenKind.Dot,
                '=' => ScriptTokenKind.Assign,
                _ => (ScriptTokenKind?)null,
            };

            if (single is { } kind)
            {
                i++;
                Add(kind, start, 1);
                return;
            }

            i++;
            pendingInvalid = true;
            switch (c)
            {
                case '^':
                    Report(ScriptDiagnosticCodes.CaretNotPower, start, 1,
                        "The '^' operator does not exist: power is Pow(a, b).");
                    break;
                case '&':
                    Report(ScriptDiagnosticCodes.UnexpectedToken, start, 1,
                        "Unexpected '&': logical AND is '&&'.", ("token", "&"), ("advice", "&&"));
                    break;
                case '|':
                    Report(ScriptDiagnosticCodes.UnexpectedToken, start, 1,
                        "Unexpected '|': logical OR is '||'.", ("token", "|"), ("advice", "||"));
                    break;
                default:
                    Report(ScriptDiagnosticCodes.UnexpectedToken, start, 1,
                        $"Unexpected character '{c}'.", ("token", c.ToString()));
                    break;
            }
        }
    }
}
