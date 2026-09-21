using System.Text;
using System.Text.RegularExpressions;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Розбір модуля TypeScript рівно настільки, наскільки потрібно сторожам
/// каталогу рядків: де код, де рядок, де коментар — і що стоїть в аргументах
/// виклику.
/// </summary>
/// <remarks>
/// ⚠ Навіщо не регулярний вираз, як у сусідніх сторожах. Регулярка по тексту
/// бачить `t('a.b')`, але не бачить `t(x ? 'a.b' : 'c.d')`, не відрізняє
/// `// t('a.b')` у коментарі від виклику (якщо коментар не вирізано) і
/// вирізає «коментар» усередині рядка (`'http://…'`). Тут кожен символ
/// спершу класифікується, і лише потім шукаються виклики — у КОДІ.
///
/// ⛔ Це не парсер TypeScript і не претендує ним бути. Свідомі спрощення:
/// <list type="bullet">
/// <item>регулярний літерал (<c>/…/</c>) впізнається за попереднім значущим
/// символом; <c>&lt;/</c> і <c>/&gt;</c> — завжди JSX, а не регулярка;</item>
/// <item>одинарна чи подвійна лапка, що не закрилася до кінця рядка, — це
/// апостроф у тексті JSX, а не рядок (у JS такий рядок неможливий);</item>
/// <item><c>//</c> у тексті JSX читається як коментар до кінця рядка.</item>
/// <item>⚠ ДВА апострофи в тексті JSX на одному рядку (<c>Don't … it's</c>)
/// читаються як рядок між ними; виклик <c>t(…)</c> між ними сторож не побачить.
/// Ризик малий — текст інтерфейсу йде через каталог, а не літералами в
/// розмітці, — але вичерпно не перевірений.</item>
/// </list>
/// </remarks>
internal sealed partial class TypeScriptSource
{
    private const char Code = 'c';
    private const char Str = 's';
    private const char Comment = 'x';
    private const char Regex_ = 'r';

    private readonly string _text;
    private readonly char[] _mask;
    private readonly List<StringToken> _strings = [];

    private TypeScriptSource(string text)
    {
        _text = text;
        _mask = new char[text.Length];
        Scan();
    }

    /// <summary>Рядковий літерал модуля.</summary>
    /// <param name="Start">Позиція відкривної лапки.</param>
    /// <param name="End">Позиція ПІСЛЯ закривної лапки.</param>
    /// <param name="Text">Вміст без лапок; для шаблону — лише статичні частини.</param>
    /// <param name="Interpolated">Шаблон із <c>${…}</c>.</param>
    /// <param name="Head">Статична частина шаблону до першого <c>${</c>.</param>
    internal sealed record StringToken(int Start, int End, string Text, bool Interpolated, string Head);

    /// <summary>Аргумент виклику: відрізок тексту.</summary>
    internal readonly record struct Span(int Start, int End);

    /// <summary>Виклик функції з розібраними аргументами верхнього рівня.</summary>
    internal sealed record Call(int Index, IReadOnlyList<Span> Arguments);

    /// <summary>Результат гілки виразу: літерал або щось, чого з тексту не видно.</summary>
    internal sealed record Branch(string? Literal, string? Dynamic);

    /// <summary>Текст модуля.</summary>
    public string Text => _text;

    /// <summary>Рядкові літерали модуля, без тих, що в коментарях.</summary>
    public IReadOnlyList<StringToken> Strings => _strings;

    /// <summary>Розбирає текст модуля.</summary>
    public static TypeScriptSource Parse(string text) => new(text);

    /// <summary>Номер рядка (з 1) для позиції — для повідомлень сторожа.</summary>
    public int LineOf(int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < _text.Length; i++)
        {
            if (_text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    /// <summary>
    /// Виклики функції <paramref name="name"/> у КОДІ: не в рядку, не в
    /// коментарі, не метод (<c>x.t(</c>) і не оголошення (<c>function t(</c>).
    /// </summary>
    public IEnumerable<Call> Calls(string name)
    {
        var pattern = new Regex(
            @"(?<![A-Za-z0-9_$.])" + Regex.Escape(name) + @"\s*\(",
            RegexOptions.CultureInvariant);

        foreach (Match match in pattern.Matches(_text))
        {
            if (_mask[match.Index] != Code)
            {
                continue;
            }

            if (DeclarationBefore().IsMatch(_text[Math.Max(0, match.Index - 20)..match.Index]))
            {
                continue;
            }

            var open = match.Index + match.Length - 1;
            var arguments = Arguments(open);
            if (arguments is not null)
            {
                yield return new Call(match.Index, arguments);
            }
        }
    }

    /// <summary>
    /// Можливі значення виразу, якщо він — літерал або умовний вираз над
    /// літералами; решта гілок повертається як <see cref="Branch.Dynamic"/>.
    /// </summary>
    /// <param name="span">Відрізок виразу.</param>
    /// <param name="resolve">
    /// Додатковий розбір відомого будівника ключа (напр. <c>statusKey('a', 'b')</c>);
    /// <c>null</c> — не впізнано.
    /// </param>
    public List<Branch> Branches(Span span, Func<string, string?>? resolve = null)
    {
        span = Trim(span);

        if (span.End <= span.Start)
        {
            return [new Branch(null, string.Empty)];
        }

        // Зайві дужки навколо всього виразу.
        if (_text[span.Start] == '(' && _mask[span.Start] == Code
            && Matching(span.Start) == span.End - 1)
        {
            return Branches(new Span(span.Start + 1, span.End - 1), resolve);
        }

        var token = _strings.FirstOrDefault(s => s.Start == span.Start && s.End == span.End);
        if (token is not null)
        {
            return token.Interpolated
                ? [new Branch(null, Normalize(span))]
                : [new Branch(token.Text, null)];
        }

        var question = TopLevelQuestion(span);
        if (question >= 0)
        {
            var colon = MatchingColon(question + 1, span.End);
            if (colon >= 0)
            {
                return
                [
                    .. Branches(new Span(question + 1, colon), resolve),
                    .. Branches(new Span(colon + 1, span.End), resolve),
                ];
            }
        }

        var normalized = Normalize(span);
        var resolved = resolve?.Invoke(normalized);

        return resolved is null
            ? [new Branch(null, normalized)]
            : [new Branch(resolved, null)];
    }

    /// <summary>
    /// Текст відрізка без коментарів, із пробілами, зведеними до одного, —
    /// щоб перенос рядка чи коментар усередині аргументу не міняли підпису.
    /// </summary>
    public string Normalize(Span span)
    {
        var builder = new StringBuilder();
        var space = false;

        for (var i = span.Start; i < span.End; i++)
        {
            var c = _mask[i] == Comment ? ' ' : _text[i];

            if (char.IsWhiteSpace(c) && _mask[i] != Str)
            {
                space = true;
                continue;
            }

            if (space && builder.Length > 0)
            {
                builder.Append(' ');
            }

            space = false;
            builder.Append(c);
        }

        return builder.ToString();
    }

    private Span Trim(Span span)
    {
        var (start, end) = (span.Start, span.End);

        while (start < end && (char.IsWhiteSpace(_text[start]) || _mask[start] == Comment))
        {
            start++;
        }

        while (end > start && (char.IsWhiteSpace(_text[end - 1]) || _mask[end - 1] == Comment))
        {
            end--;
        }

        return new Span(start, end);
    }

    /// <summary>Перший <c>?</c> умовного виразу на верхньому рівні відрізка.</summary>
    /// <remarks><c>?.</c> і <c>??</c> — не умовний вираз.</remarks>
    private int TopLevelQuestion(Span span)
    {
        var depth = 0;

        for (var i = span.Start; i < span.End; i++)
        {
            if (_mask[i] != Code)
            {
                continue;
            }

            var c = _text[i];
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
            }
            else if (c == '?' && depth == 0)
            {
                var next = i + 1 < span.End ? _text[i + 1] : '\0';
                var previous = i > span.Start ? _text[i - 1] : '\0';

                if (next == '?' || previous == '?' || (next == '.' && !char.IsDigit(At(i + 2))))
                {
                    continue;
                }

                return i;
            }
        }

        return -1;
    }

    /// <summary><c>:</c>, що закриває умовний вираз, з урахуванням вкладених.</summary>
    private int MatchingColon(int from, int end)
    {
        var depth = 0;
        var nested = 0;

        for (var i = from; i < end; i++)
        {
            if (_mask[i] != Code)
            {
                continue;
            }

            var c = _text[i];
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
            }
            else if (depth == 0 && c == '?' && At(i + 1) != '?' && At(i - 1) != '?' && At(i + 1) != '.')
            {
                nested++;
            }
            else if (depth == 0 && c == ':')
            {
                if (nested == 0)
                {
                    return i;
                }

                nested--;
            }
        }

        return -1;
    }

    private char At(int i) => i >= 0 && i < _text.Length ? _text[i] : '\0';

    /// <summary>Аргументи верхнього рівня від відкривної дужки.</summary>
    private List<Span>? Arguments(int open)
    {
        var close = Matching(open);
        if (close < 0)
        {
            return null;
        }

        var result = new List<Span>();
        var depth = 0;
        var start = open + 1;

        for (var i = open + 1; i < close; i++)
        {
            if (_mask[i] != Code)
            {
                continue;
            }

            var c = _text[i];
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                result.Add(new Span(start, i));
                start = i + 1;
            }
        }

        if (Trim(new Span(start, close)).End > Trim(new Span(start, close)).Start)
        {
            result.Add(new Span(start, close));
        }

        return result;
    }

    /// <summary>Парна дужка до <paramref name="open"/> у коді; <c>-1</c> — немає.</summary>
    private int Matching(int open)
    {
        var depth = 0;

        for (var i = open; i < _text.Length; i++)
        {
            if (_mask[i] != Code)
            {
                continue;
            }

            var c = _text[i];
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    // ── класифікація символів ───────────────────────────────────────────

    private void Scan()
    {
        // Відкриті шаблони: для кожного — глибина фігурних дужок у поточній
        // вставці `${…}` і будівник його статичного тексту.
        var holes = new Stack<(int Depth, TemplateBuilder Template)>();
        var i = 0;

        while (i < _text.Length)
        {
            var c = _text[i];
            var next = At(i + 1);

            if (c == '/' && next == '/')
            {
                var eol = _text.IndexOf('\n', i);
                i = Fill(i, eol < 0 ? _text.Length : eol, Comment);
                continue;
            }

            if (c == '/' && next == '*')
            {
                var close = _text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = Fill(i, close < 0 ? _text.Length : close + 2, Comment);
                continue;
            }

            if (c is '\'' or '"')
            {
                var end = QuotedEnd(i, c);
                if (end < 0)
                {
                    // Апостроф у тексті JSX: рядок у JS не переходить рядок.
                    _mask[i++] = Code;
                    continue;
                }

                _strings.Add(new StringToken(i, end, _text[(i + 1)..(end - 1)], false, _text[(i + 1)..(end - 1)]));
                i = Fill(i, end, Str);
                continue;
            }

            if (c == '`')
            {
                var template = new TemplateBuilder(i);
                _mask[i++] = Str;
                i = ScanTemplate(i, template, holes);
                continue;
            }

            if (c == '/' && IsRegexStart(i))
            {
                var end = RegexEnd(i);
                if (end > 0)
                {
                    i = Fill(i, end, Regex_);
                    continue;
                }
            }

            if (holes.Count > 0 && c == '{')
            {
                var (depth, template) = holes.Pop();
                holes.Push((depth + 1, template));
            }
            else if (holes.Count > 0 && c == '}')
            {
                var (depth, template) = holes.Pop();
                if (depth == 0)
                {
                    // Кінець вставки: далі знову текст шаблону.
                    _mask[i++] = Str;
                    i = ScanTemplate(i, template, holes);
                    continue;
                }

                holes.Push((depth - 1, template));
            }

            _mask[i++] = Code;
        }
    }

    /// <summary>
    /// Текст шаблону від <paramref name="i"/> до кінця або до <c>${</c>.
    /// </summary>
    private int ScanTemplate(int i, TemplateBuilder template, Stack<(int, TemplateBuilder)> holes)
    {
        while (i < _text.Length)
        {
            var c = _text[i];

            if (c == '\\')
            {
                template.Append(c);
                template.Append(At(i + 1));
                _mask[i] = Str;
                if (i + 1 < _text.Length)
                {
                    _mask[i + 1] = Str;
                }

                i += 2;
                continue;
            }

            if (c == '`')
            {
                _mask[i++] = Str;
                _strings.Add(template.Build(i));
                return i;
            }

            if (c == '$' && At(i + 1) == '{')
            {
                _mask[i] = Str;
                _mask[i + 1] = Str;
                template.Hole();
                holes.Push((0, template));
                return i + 2;
            }

            template.Append(c);
            _mask[i++] = Str;
        }

        return i;
    }

    private int Fill(int from, int to, char kind)
    {
        for (var i = from; i < to; i++)
        {
            _mask[i] = kind;
        }

        return to;
    }

    /// <summary>Позиція після закривної лапки; <c>-1</c> — рядок не закрився на цьому рядку.</summary>
    private int QuotedEnd(int open, char quote)
    {
        for (var i = open + 1; i < _text.Length; i++)
        {
            var c = _text[i];
            if (c == '\\')
            {
                i++;
                continue;
            }

            if (c == quote)
            {
                return i + 1;
            }

            if (c == '\n')
            {
                return -1;
            }
        }

        return -1;
    }

    /// <summary>Чи починає <c>/</c> регулярний літерал, а не ділення чи JSX.</summary>
    private bool IsRegexStart(int i)
    {
        // `</tag>` і `<Tag />` — JSX.
        if (At(i - 1) == '<' || At(i + 1) == '>')
        {
            return false;
        }

        var j = i - 1;
        while (j >= 0 && (char.IsWhiteSpace(_text[j]) || _mask[j] == Comment))
        {
            j--;
        }

        if (j < 0)
        {
            return true;
        }

        var previous = _text[j];
        if ("(,=:[!&|?{};+-*%~^".Contains(previous, StringComparison.Ordinal))
        {
            return true;
        }

        // Стрілка: `x => /re/.test(x)`.
        if (previous == '>' && At(j - 1) == '=')
        {
            return true;
        }

        return RegexKeywordBefore().IsMatch(_text[Math.Max(0, j - 10)..(j + 1)]);
    }

    /// <summary>Кінець регулярного літерала з прапорцями; <c>-1</c> — це не він.</summary>
    private int RegexEnd(int open)
    {
        var inClass = false;

        for (var i = open + 1; i < _text.Length; i++)
        {
            var c = _text[i];
            if (c == '\n')
            {
                return -1;
            }

            if (c == '\\')
            {
                i++;
                continue;
            }

            if (c == '[')
            {
                inClass = true;
            }
            else if (c == ']')
            {
                inClass = false;
            }
            else if (c == '/' && !inClass)
            {
                var end = i + 1;
                while (end < _text.Length && char.IsAsciiLetter(_text[end]))
                {
                    end++;
                }

                return end;
            }
        }

        return -1;
    }

    [GeneratedRegex(@"\bfunction\s*$")]
    private static partial Regex DeclarationBefore();

    [GeneratedRegex(@"(?:^|[^A-Za-z0-9_$])(?:return|typeof|case|in|of|void|delete|throw|else|yield|await)$")]
    private static partial Regex RegexKeywordBefore();

    /// <summary>Збирає статичний текст шаблону, поки сканер ходить у вставки й назад.</summary>
    private sealed class TemplateBuilder(int start)
    {
        private readonly StringBuilder _text = new();
        private string? _head;

        public void Append(char c) => _text.Append(c);

        public void Hole()
        {
            _head ??= _text.ToString();
            _text.Append("${}");
        }

        public StringToken Build(int end)
            => new(start, end, _text.ToString(), _head is not null, _head ?? _text.ToString());
    }
}
