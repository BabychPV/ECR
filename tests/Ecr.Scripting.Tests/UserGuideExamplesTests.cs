using System.Text.RegularExpressions;
using Ecr.Expressions.Functions;
using Ecr.Scripting.Lexing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scripting.Tests;

/// <summary>
/// Посібник еколога <c>docs/user/methodology-script.md</c>: кожен приклад скрипта справді компілюється, кожен
/// «приклад помилки» дає названий код, а перелік зарезервованих слів і функцій збігається з кодом.
/// </summary>
/// <remarks>
/// Мітки блоків у посібнику: <c>ecr</c> — повний скрипт методології (без діагностик); <c>ecr-lib</c> — бібліотека
/// (ім'я — у першому рядку <c>// бібліотека: Ім'я</c>), її бачать наступні блоки; <c>ecr-error</c> — навмисно хибний
/// приклад (код очікуваної діагностики — у першому рядку <c>// помилка: код</c>). Перевірки оточення (таблиці,
/// колонки, довідники, одиниці) потребують бази й тут не виконуються — це робить редактор при збереженні.
/// </remarks>
public sealed partial class UserGuideExamplesTests
{
    private const string GuidePath = "docs/user/methodology-script.md";

    private static readonly string[] KnownLabels = ["ecr", "ecr-lib", "ecr-error", "text"];

    private static string ReadGuide()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, GuidePath)).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^```(?<label>[^\n]*)\n(?<body>.*?)\n```$", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex Fence();

    [GeneratedRegex(@"^// бібліотека: (?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.None)]
    private static partial Regex LibraryHeader();

    [GeneratedRegex(@"^// помилка: (?<code>[A-Za-z]+)", RegexOptions.None)]
    private static partial Regex ErrorHeader();

    private static List<(string Label, string Body, int Line)> Blocks(string guide)
        => [.. Fence().Matches(guide).Select(m => (
            m.Groups["label"].Value,
            m.Groups["body"].Value,
            guide[..m.Index].Count(c => c == '\n') + 1))];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Блоки_коду_мають_лише_відомі_мітки()
    {
        var blocks = Blocks(ReadGuide());

        Assert.DoesNotContain(blocks, b => b.Label == "csharp");
        Assert.All(blocks, b => Assert.Contains(b.Label, KnownLabels));
        Assert.True(blocks.Count(b => b.Label == "ecr") >= 20, "у посібнику мало прикладів скрипта");
        Assert.True(blocks.Count(b => b.Label == "ecr-error") >= 10, "у посібнику мало прикладів помилок");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Кожен_приклад_скрипта_з_посібника_компілюється_без_діагностик()
    {
        var libraries = new Dictionary<string, CompiledScript>(StringComparer.Ordinal);
        var failures = new List<string>();
        var compiled = 0;

        foreach (var (label, body, line) in Blocks(ReadGuide()))
        {
            if (label == "ecr-lib")
            {
                var header = LibraryHeader().Match(body);
                Assert.True(header.Success, $"рядок {line}: бібліотека без «// бібліотека: Ім'я»");
                var lib = T.Compile(body, library: true);
                if (!lib.IsSuccess)
                {
                    failures.Add($"рядок {line} (бібліотека): {T.Describe(lib)}");
                    continue;
                }

                libraries[header.Groups["name"].Value] = lib.Script!;
            }
            else if (label == "ecr")
            {
                var result = T.Compile(body, import: name => libraries.GetValueOrDefault(name));
                compiled++;
                if (!result.IsSuccess)
                {
                    failures.Add($"рядок {line}: {T.Describe(result)}");
                }
            }
        }

        Assert.True(compiled > 0);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Кожен_приклад_помилки_дає_названий_у_ньому_код()
    {
        var failures = new List<string>();
        var checkedCount = 0;

        foreach (var (label, body, line) in Blocks(ReadGuide()).Where(b => b.Label == "ecr-error"))
        {
            var header = ErrorHeader().Match(body);
            Assert.True(header.Success, $"рядок {line}: приклад помилки без «// помилка: код»");
            var expected = header.Groups["code"].Value;

            var result = T.Compile(body);
            checkedCount++;
            if (!T.Codes(result).Contains(expected))
            {
                failures.Add($"рядок {line}: очікувався {expected}, є [{T.Describe(result)}]");
            }
        }

        Assert.True(checkedCount > 0);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Перелік_зарезервованих_слів_у_посібнику_збігається_з_лексером()
    {
        var guide = ReadGuide();
        var match = Regex.Match(guide, @"Зарезервовані слова[^`]*`(?<words>[^`]+)`");
        Assert.True(match.Success, "у посібнику немає переліку зарезервованих слів");

        var documented = match.Groups["words"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal([.. ScriptLexer.Keywords.Order(StringComparer.Ordinal)], [.. documented.Order(StringComparer.Ordinal)]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Перелік_вбудованих_функцій_у_посібнику_збігається_з_каталогом()
    {
        var guide = ReadGuide();
        var start = guide.IndexOf("## 17. Вбудовані функції", StringComparison.Ordinal);
        var end = guide.IndexOf("## 18.", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);

        var documented = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var row in guide[start..end].Split('\n').Where(l => l.StartsWith("| `", StringComparison.Ordinal)))
        {
            var firstColumn = row.Split('|')[1];
            foreach (Match name in Regex.Matches(firstColumn, @"`([A-Za-z][A-Za-z0-9_]*)[^`]*`"))
            {
                documented.Add(name.Groups[1].Value);
            }
        }

        // `if` і `in` — не функції скрипта: вони пишуться як `? :` та `x in (…)`.
        var actual = DialectCatalog.Names.Where(n => n is not ("if" or "in")).Order(StringComparer.Ordinal);

        Assert.Equal([.. actual], [.. documented]);
    }
}
