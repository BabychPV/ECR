using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Ключ відмови API, що потрапляє в <c>Details["messageKey"]</c> НЕ літералом, а
/// константою, локальною змінною чи параметром фабрики, теж приходить клієнтові
/// разом із підстановками для кожного <c>{x}</c> свого шаблону.
/// </summary>
/// <remarks>
/// ⛔ Сторож кидків (<see cref="MessageKeyRatchetTests"/>,
/// <c>Кожен_плейсхолдер_шаблону_має_підстановку_в_кидку</c>) бачить лише форму
/// <c>["messageKey"] = "err.…"</c>. Але майже шістдесят місць у <c>src</c> кладуть
/// туди ідентифікатор: фабрику <c>Invalid(string messageKey, …)</c>, яку кличуть із
/// літералом, константу <c>…Key</c>, локальну <c>messageKey = умова ? "err.a" :
/// "err.b"</c>. Шаблон, що чекає <c>{max}</c>, у такому місці не стеріг ніхто —
/// і клієнтський <c>t()</c> показав би людині фігурні дужки (клас дефекту D1).
/// Звіти рядків (ключ полем запису, а не винятком) стереже
/// <see cref="RowReportPlaceholderTests"/>.
///
/// ⚠ Межа — ТЕКСТ одного файлу. Підстановка вважається наявною, якщо її ім'я
/// стоїть рядком <c>"ім'я"</c> у тому самому члені класу, що й присвоєння
/// ключа, або (для фабрики) в аргументах її виклику. Ключ із властивості
/// (<c>diagnostic.MessageKey</c>) і виклик фабрики з іншого файлу звідси не
/// перевірити — їх стережуть прогони справжніх відмов крізь конвеєр
/// (<c>MainPathLocalizedErrorTests</c>).
/// </remarks>
public sealed partial class IndirectMessageKeyPlaceholderTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Ключ_відмови_через_константу_змінну_чи_фабрику_має_підстановки()
    {
        var failures = Check(SourceTree.Production(), Templates(), out var checkedSites);

        // ⛔ Регулярка, що перестала збігатися, дала б нуль перевірок і ЗЕЛЕНЕ.
        Assert.True(checkedSites > 50, $"Сторож перевірив лише {checkedSites} місць — регулярка зламалась?");
        Assert.True(
            failures.Count == 0,
            "Шаблон відмови чекає підстановку, якої в місці створення немає — клієнт покаже фігурні дужки (клас D1). "
            + "Додай поле з таким іменем у Details поруч із messageKey:"
            + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Сторож_бачить_фабрику_константу_й_локальну_змінну()
    {
        // ⛔ Доказ, що сторож уміє червоніти: по одному червоному на кожну форму
        // (фабрика, константа, локальна змінна) і зелені двійники з підстановкою.
        const string sample = """
            class A
            {
                private const string TooLongKey = "err.X-0422.tooLong";
                private const string PlainKey = "err.X-0422.plain";

                private void Calls()
                {
                    throw Invalid("err.X-0422.tooLong", "Задовгий.");
                    throw Invalid("err.X-0422.missing", "Немає.");
                    throw WithExtra("err.X-0422.missing", new() { ["name"] = n });
                }

                private static Exception Invalid(string messageKey, string message)
                    => new BusinessRuleException("X", message, new Dictionary<string, object?> { ["messageKey"] = messageKey, ["max"] = 5 });

                private static Exception WithExtra(string messageKey, Dictionary<string, object?> extra)
                {
                    extra["messageKey"] = messageKey;
                    return new BusinessRuleException("X", "m", extra);
                }

                private void Constant()
                {
                    throw new BusinessRuleException("X", "m", new() { ["messageKey"] = TooLongKey });
                }

                private void Plain()
                {
                    throw new BusinessRuleException("X", "m", new() { ["messageKey"] = PlainKey });
                }

                private void Local(bool a)
                {
                    var messageKey = a ? "err.X-0422.tooLong" : "err.X-0422.plain";
                    throw new BusinessRuleException("X", "m", new() { ["messageKey"] = messageKey });
                }
            }
            """;

        var templates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["err.X-0422.tooLong"] = "Longer than {max}.",
            ["err.X-0422.missing"] = "No {name}.",
            ["err.X-0422.plain"] = "No placeholders.",
        };

        var failures = Check([new SourceFile("src/Sample.cs", sample)], templates, out var checkedSites);

        // Фабрика Invalid: tooLong (зелений — {max} у тілі), missing (червоний);
        // WithExtra: missing з ["name"] у виклику (зелений); константи TooLongKey
        // (червоний) і PlainKey (зелений); локальна: tooLong (червоний) і plain.
        Assert.Equal(7, checkedSites);
        Assert.Equal(3, failures.Count);
        Assert.Contains("src/Sample.cs:9:", failures[0], StringComparison.Ordinal);
        Assert.Contains("{name}", failures[0], StringComparison.Ordinal);
        Assert.Contains("src/Sample.cs:24:", failures[1], StringComparison.Ordinal);
        Assert.Contains("src/Sample.cs:35:", failures[2], StringComparison.Ordinal);
    }

    private static List<string> Check(
        IReadOnlyList<SourceFile> files, Dictionary<string, string> templates, out int checkedSites)
    {
        // Константи ключів — з усього дерева: `RegistryRuleEngine.RuleViolatedErrorKey`
        // оголошено в одному файлі, а береться в іншому. ⚠ Однойменні константи
        // (`MessageKey` є в багатьох класах) розв'язуються за файлом чи типом, не навмання.
        var declared = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.Ordinal);
        var typeFiles = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var own = declared[file.Path] = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (Match m in KeyConstant().Matches(file.Text))
            {
                if (!own.TryGetValue(m.Groups[1].Value, out var set))
                {
                    own[m.Groups[1].Value] = set = new HashSet<string>(StringComparer.Ordinal);
                }

                set.Add(m.Groups[2].Value);
            }

            foreach (Match m in TypeDeclaration().Matches(file.Text))
            {
                if (!typeFiles.TryGetValue(m.Groups[1].Value, out var paths))
                {
                    typeFiles[m.Groups[1].Value] = paths = [];
                }

                paths.Add(file.Path);
            }
        }

        // Ім'я без кваліфікатора поза своїм файлом — лише якщо воно в дереві одне.
        var constants = declared.Values
            .SelectMany(own => own)
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().Value, StringComparer.Ordinal);

        var failures = new List<string>();
        var count = 0;

        foreach (var file in files)
        {
            var text = file.Text;
            var members = Members(text);

            foreach (Match site in KeyAssignment().Matches(text))
            {
                var identifier = site.Groups[1].Value;
                var member = members.LastOrDefault(m => m.Start <= site.Index);
                var scope = text[member.Start..member.End];

                if (Constant(identifier, file.Path) is { } named)
                {
                    Verify(named, scope, site.Index, $"константа {identifier}");
                    continue;
                }

                if (identifier.Contains('.', StringComparison.Ordinal))
                {
                    continue; // Властивість запису чи виразу — текстом не розв'язати.
                }

                if (member.Name is not null
                    && Regex.IsMatch(member.Signature, $@"\bstring\??\s+{Regex.Escape(identifier)}\b"))
                {
                    // Фабрика: ключ приходить параметром — перевіряємо кожен її виклик у файлі.
                    foreach (Match call in Regex.Matches(text, $@"(?<![\w.])(?:[A-Za-z_]\w*\.)?{Regex.Escape(member.Name)}\s*\("))
                    {
                        if (call.Index >= member.Start && call.Index < member.SignatureEnd)
                        {
                            continue; // Саме оголошення.
                        }

                        var arguments = Balanced(text, call.Index + call.Length - 1);
                        Verify(KeysIn(arguments, id => Constant(id, file.Path)), scope + arguments, call.Index, $"фабрика {member.Name}");
                    }

                    continue;
                }

                // Локальна змінна: ключі — з найближчого присвоєння перед місцем у тому самому члені.
                var before = text[member.Start..site.Index];
                var assigned = Regex.Match(
                    before, $@"\b{Regex.Escape(identifier)}\s*=(?![=>])", RegexOptions.RightToLeft);

                if (assigned.Success)
                {
                    var from = member.Start + assigned.Index + assigned.Length;
                    var statement = text[from..StatementEnd(text, from)];
                    Verify(KeysIn(statement, id => Constant(id, file.Path)), scope, site.Index, $"змінна {identifier}");
                }
            }

            HashSet<string>? Constant(string identifier, string path)
            {
                var segments = identifier.Split('.');
                var name = segments[^1];

                if (segments.Length == 1)
                {
                    return declared[path].TryGetValue(name, out var own) ? own
                        : name.EndsWith("Key", StringComparison.Ordinal) && name != "MessageKey" && constants.TryGetValue(name, out var unique) ? unique
                        : null;
                }

                // `Тип.Ім'я` — константа з файлу, де оголошено цей тип; `змінна.Властивість` — ні.
                var type = segments[^2];
                if (!char.IsUpper(type[0]) || !typeFiles.TryGetValue(type, out var paths))
                {
                    return null;
                }

                var found = paths
                    .Select(p => declared[p].GetValueOrDefault(name))
                    .OfType<HashSet<string>>()
                    .ToList();

                return found.Count == 1 ? found[0] : null;
            }

            void Verify(IEnumerable<string> keys, string scope, int index, string via)
            {
                foreach (var key in keys)
                {
                    if (!templates.TryGetValue(key, out var template))
                    {
                        continue;
                    }

                    count++;

                    var missing = Placeholder().Matches(template)
                        .Select(m => m.Groups[1].Value)
                        .Distinct(StringComparer.Ordinal)
                        .Where(name => !scope.Contains($"\"{name}\"", StringComparison.Ordinal))
                        .ToList();

                    if (missing.Count > 0)
                    {
                        failures.Add(
                            $"{file.Path}:{Line(text, index)}: ключ {key} ({via}) — шаблон чекає "
                            + string.Join(", ", missing.Select(n => "{" + n + "}"))
                            + ", а поля з таким іменем поруч немає.");
                    }
                }
            }
        }

        checkedSites = count;
        return failures;
    }

    private static SortedSet<string> KeysIn(string text, Func<string, HashSet<string>?> constant)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match literal in KeyLiteral().Matches(text))
        {
            keys.Add(literal.Groups[1].Value);
        }

        foreach (Match identifier in KeyIdentifier().Matches(text))
        {
            if (constant(identifier.Groups[1].Value) is { } named)
            {
                keys.UnionWith(named);
            }
        }

        return keys;
    }

    /// <summary>
    /// Члени класу за оголошеннями: від початку оголошення до початку наступного.
    /// Тіло локальної функції й лямбди лишається в члені, що їх містить.
    /// </summary>
    private static List<Member> Members(string text)
    {
        var declarations = MemberDeclaration().Matches(text).ToList();
        var members = new List<Member> { new(0, declarations.Count > 0 ? declarations[0].Index : text.Length, null, "", 0) };

        for (var i = 0; i < declarations.Count; i++)
        {
            var d = declarations[i];
            var end = i + 1 < declarations.Count ? declarations[i + 1].Index : text.Length;
            var signature = Balanced(text, d.Index + d.Length - 1);
            members.Add(new Member(d.Index, end, d.Groups[1].Value, signature, d.Index + d.Length - 1 + signature.Length));
        }

        return members;
    }

    /// <summary>Шаблони <c>err.*</c> сіду (en): ключ → текст.</summary>
    private static Dictionary<string, string> Templates()
    {
        var seed = File.ReadAllText(Path.Combine(
            SourceTree.Root, "src", "Ecr.Infrastructure", "Persistence", "Sql", "09-seed.sql"));

        var templates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in SeedTemplate().Matches(seed))
        {
            templates[m.Groups[1].Value] = m.Groups[2].Value;
        }

        Assert.NotEmpty(templates);
        return templates;
    }

    /// <summary>Індекс <c>;</c>, що закриває вираз від <paramref name="start"/>, на нульовій глибині дужок.</summary>
    private static int StatementEnd(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '"':
                    i = SkipString(text, i);
                    break;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    if (--depth < 0)
                    {
                        return i;
                    }

                    break;
                case ';' when depth == 0:
                    return i;
            }
        }

        return text.Length;
    }

    /// <summary>Аргументи від відкритої дужки до парної закритої (рядки — без підрахунку дужок).</summary>
    private static string Balanced(string text, int openParen)
    {
        var depth = 0;
        for (var i = openParen; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '"':
                    i = SkipString(text, i);
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    if (--depth == 0)
                    {
                        return text[openParen..(i + 1)];
                    }

                    break;
            }
        }

        return text[openParen..];
    }

    private static int SkipString(string text, int quote)
    {
        var i = quote + 1;
        while (i < text.Length && text[i] != '"')
        {
            if (text[i] == '\\')
            {
                i++;
            }

            i++;
        }

        return i;
    }

    private static int Line(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private readonly record struct Member(int Start, int End, string? Name, string Signature, int SignatureEnd);

    /// <summary><c>["messageKey"] = ідентифікатор</c> — літерали стереже сторож кидків.</summary>
    [GeneratedRegex(@"\[""messageKey""\]\s*=\s*([A-Za-z_][\w.]*)(?=\s*[,;}\)])")]
    private static partial Regex KeyAssignment();

    /// <summary>Оголошення методу з рядка: модифікатори, тип, ім'я, відкрита дужка.</summary>
    [GeneratedRegex(
        @"^[ \t]*(?:(?:public|private|internal|protected|static|async|override|virtual|sealed|new|extern)\s+)+[\w<>\[\]?,. ]+?\s+([A-Za-z_]\w*)\s*(?:<[^<>()]*>)?\s*\(",
        RegexOptions.Multiline)]
    private static partial Regex MemberDeclaration();

    [GeneratedRegex(@"\b(?:class|record|struct|interface)\s+([A-Z]\w*)")]
    private static partial Regex TypeDeclaration();

    [GeneratedRegex(@"const\s+string\s+(\w+Key)\s*=\s*""(err\.[^""]+)""")]
    private static partial Regex KeyConstant();

    [GeneratedRegex(@"""(err\.[^""]+)""")]
    private static partial Regex KeyLiteral();

    /// <summary>Ідентифікатор константи ключа, з кваліфікатором типу, якщо він є.</summary>
    [GeneratedRegex(@"(?<![\w.])((?:[A-Za-z_]\w*\.)*[A-Z]\w*Key)\b")]
    private static partial Regex KeyIdentifier();

    [GeneratedRegex(@"\(\s*N'(err\.[^']+)'\s*,\s*N'en'\s*,\s*N'((?:[^']|'')*)'\s*,\s*[01]\s*\)")]
    private static partial Regex SeedTemplate();

    [GeneratedRegex(@"\{([A-Za-z_]\w*)\}")]
    private static partial Regex Placeholder();
}
