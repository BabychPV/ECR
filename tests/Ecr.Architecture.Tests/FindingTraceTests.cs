using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Кожна знахідка аудиту названа тестом, який її ловить (<c>D-134</c>, <c>D-145</c>).
/// </summary>
/// <remarks>
/// ⛔ Знахідка, закрита словами «виправив», через місяць невідрізненна від
/// невиправленої: код той самий, коментар той самий, і єдиний спосіб
/// перевірити — прочитати весь діф. Названий тест перетворює це питання на
/// прогін.
///
/// ⚠ Сторож перевіряє **існування** названого тесту, а не його змістовність:
/// довести, що тест падає на невиправленому коді, машина не може — це робить
/// людина при закритті знахідки. Але посилання на тест, якого немає, вона
/// ловить, а саме так найчастіше й розсипається журнал знахідок:
/// тест перейменували, рядок лишився.
/// </remarks>
public sealed partial class FindingTraceTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожна_знахідка_називає_тест_який_існує()
    {
        var path = Path.Combine(SolutionRoot(), "contracts", "findings.md");
        Assert.True(File.Exists(path), $"Немає {path}: журнал знахідок не ведеться.");

        var text = File.ReadAllText(path);

        var findings = FindingHeaderRegex.Matches(text).Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(findings);

        // Кожна знахідка — це заголовок, за яким до наступного заголовка має
        // стояти або рядок `Тест:`, або явна позначка прийнятого ризику.
        var sections = FindingSectionRegex.Matches(text);
        Assert.Equal(findings.Count, sections.Count);

        var known = KnownTestNames();
        Assert.NotEmpty(known);

        var broken = new List<string>();

        foreach (Match section in sections)
        {
            var id = section.Groups["id"].Value;
            var body = section.Groups["body"].Value;

            var test = TestLineRegex.Match(body);

            if (!test.Success)
            {
                // ⚠ Ризик без тесту — легальний стан, але він мусить бути
                // НАЗВАНИЙ. Мовчазна відсутність рядка `Тест:` і свідоме
                // «перевірити неможливо» виглядають однаково, а це різні речі.
                if (!body.Contains("БЕЗ ТЕСТУ", StringComparison.Ordinal))
                {
                    broken.Add($"{id}: немає ані рядка «Тест:», ані позначки «⚠ БЕЗ ТЕСТУ»");
                }

                continue;
            }

            // ⚠ Зворотні лапки — розмітка markdown, а не частина назви.
            // Без цього рядка сторож доповідав про десять неіснуючих
            // тестів, і кожен із них існував.
            var name = test.Groups[1].Value.Trim().Trim('`').Trim();

            // Правило-лінт замість тесту теж законне: `A7-44` закривається
            // саме ним. Такий рядок називає файл конфігурації, а не метод.
            if (name.Contains("eslint", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var method = name.Split('.')[^1];

            if (!known.Contains(method))
            {
                broken.Add($"{id}: тест «{method}» не знайдено в жодному тестовому проєкті");
            }
        }

        Assert.True(
            broken.Count == 0,
            "Знахідки, які посилаються в порожнечу:" + Environment.NewLine
            + string.Join(Environment.NewLine, broken.Order(StringComparer.Ordinal)));
    }

    /// <summary>Імена всіх тестових методів рішення.</summary>
    /// <remarks>
    /// ⚠ Включно з клієнтськими: частина знахідок закривається тестом на
    /// TypeScript, і вимагати від них жити в .NET означало б виштовхнути
    /// перевірку туди, де перевіряти нічого.
    /// </remarks>
    private static HashSet<string> KnownTestNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var root = SolutionRoot();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match method in TestMethodRegex.Matches(File.ReadAllText(file)))
            {
                names.Add(method.Groups[1].Value);
            }
        }

        var web = Path.Combine(root, "src", "Ecr.Web", "src");
        if (Directory.Exists(web))
        {
            foreach (var file in Directory.EnumerateFiles(web, "*.test.ts*", SearchOption.AllDirectories))
            {
                foreach (Match spec in ClientTestRegex.Matches(File.ReadAllText(file)))
                {
                    names.Add(spec.Groups[1].Value);
                }
            }
        }

        // ⚠ І прогони в браузері. Вони живуть в `e2e/` під розширенням
        // `.spec.ts`, а не `.test.ts`, і без цього рядка знахідка, закрита
        // прогоном Playwright, вважалася б посиланням у порожнечу —
        // тобто сторож валив би саме те, що зроблено найретельніше.
        var e2e = Path.Combine(root, "src", "Ecr.Web", "e2e");
        if (Directory.Exists(e2e))
        {
            foreach (var file in Directory.EnumerateFiles(e2e, "*.spec.ts", SearchOption.AllDirectories))
            {
                foreach (Match spec in ClientTestRegex.Matches(File.ReadAllText(file)))
                {
                    names.Add(spec.Groups[1].Value);
                }
            }
        }

        return names;
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Немає Ecr.sln.");
    }

    /// <summary>Заголовок знахідки: <c>## A7-39 ⛔⛔ …</c>.</summary>
    [GeneratedRegex(@"^## (A7-\d+[a-z]?) ", RegexOptions.Multiline)]
    private static partial Regex FindingHeaderRegex { get; }

    /// <summary>Знахідка разом із текстом до наступного заголовка.</summary>
    [GeneratedRegex(@"^## (?<id>A7-\d+[a-z]?) (?<body>.*?)(?=^## |^---)", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex FindingSectionRegex { get; }

    /// <summary>Рядок, що називає тест.</summary>
    [GeneratedRegex(@"^Тест:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex TestLineRegex { get; }

    /// <summary>Назва тестового методу .NET.</summary>
    [GeneratedRegex(@"public\s+(?:async\s+Task|void)\s+(\w+)\s*\(")]
    private static partial Regex TestMethodRegex { get; }

    /// <summary>Назва тесту клієнта.</summary>
    [GeneratedRegex(@"^\s*(?:it|test)\(\s*['""`](.+?)['""`]", RegexOptions.Multiline)]
    private static partial Regex ClientTestRegex { get; }
}
