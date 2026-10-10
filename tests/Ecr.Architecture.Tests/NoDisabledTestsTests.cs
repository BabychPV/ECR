using System.Text;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// L10-11 (аудит 2026-10-09): жоден тест не вимкнено атрибутом (Skip/Explicit/Ignore) і не пропускається в
/// рантаймі — навіть коли аргумент розбито на кілька рядків.
/// </summary>
/// <remarks>
/// ⛔ Предмет. <c>.github/guards/honesty-guard.sh</c> дивиться на ДОДАНІ рядки по одному й шукає <c>Skip =</c>
/// в одному рядку; атрибут, розбитий переносом, чи <c>SkipUnless</c>/<c>Explicit</c> проходили повз. Цей сторож
/// читає ВЕСЬ код тестів (без коментарів і рядкових літералів, <see cref="SourceTree.CodeLines"/>) і розбирає
/// список аргументів атрибута до закривної дужки, тож перенос рядка нічого не міняє. Дозволених винятків немає:
/// платформенний тест у цьому репозиторії перевіряє поведінку «не Windows» й повертається (JobObjectTests).
/// Мутація (CI): додати до будь-якого <c>[Fact]</c> перенесений аргумент <c>Skip</c> → тест червоний.
/// </remarks>
public sealed class NoDisabledTestsTests
{
    // Початок списку аргументів тестового атрибута: Fact(, Theory(, SqlServerFact( — з дужкою «[» попереду.
    private static readonly Regex TestAttributeStart =
        new("\\[\\s*(?:[A-Za-z_][\\w.]*\\.)?\\w*(?:Fact|Theory)\\w*\\s*\\(", RegexOptions.CultureInvariant);

    // Skip = …, SkipUnless = …, SkipWhen = …, Explicit = … (але не ==).
    private static readonly Regex DisablingArgument =
        new("\\b(?:Skip\\w*|Explicit)\\s*=(?!=)", RegexOptions.CultureInvariant);

    // [Ignore], [Ignore(…)], [Ignore, …] (NUnit/MSTest-стиль, якщо хтось його принесе).
    private static readonly Regex IgnoreAttribute =
        new("\\[\\s*(?:[A-Za-z_][\\w.]*\\.)?Ignore(?:Attribute)?\\s*[\\](,]", RegexOptions.CultureInvariant);

    // Assert.Skip…(…) і SkipException — пропуск у рантаймі.
    private static readonly Regex RuntimeSkip =
        new("\\bAssert\\s*\\.\\s*Skip\\w*\\s*\\(|\\bSkipException\\b", RegexOptions.CultureInvariant);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void У_тестах_немає_вимкнених_і_пропущених_тестів()
    {
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var path in SourceTree.Walk(Path.Combine(SourceTree.Root, "tests")).Where(f => f.EndsWith(".cs", StringComparison.Ordinal)))
        {
            scanned++;
            var relative = Path.GetRelativePath(SourceTree.Root, path).Replace('\\', '/');
            var code = CodeOf(new SourceFile(relative, File.ReadAllText(path)));
            offenders.AddRange(FindDisabledTests(code).Select(snippet => $"{relative}: {snippet}"));
        }

        // ⛔ Самоперевірка: порожній обхід дав би зелене саме тоді, коли каталог тестів змінив місце.
        Assert.True(scanned > 100, $"знайдено лише {scanned} файлів тестів — сторож дивиться не туди");
        Assert.True(
            offenders.Count == 0,
            "Тест вимкнено або пропускається (Skip/Explicit/Ignore, Assert.Skip): " + string.Join("; ", offenders)
            + ". Вимкнений тест — не доказ; для платформенного тесту перевіряй поведінку «не Windows» і повертайся (див. JobObjectTests).");
    }

    /// <remarks>
    /// Доказ самого сторожа на синтетичному коді: однорядковий, перенесений, SkipUnless/Explicit, Ignore, Assert.Skip —
    /// червоні; звичайні атрибути, <c>Skip</c> як метод Linq і не-тестові атрибути — ні.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("[Fact(Skip = \"x\")]\npublic void A() { }", 1)]
    [InlineData("[Fact(\n    Skip\n        = \"x\")]\npublic void A() { }", 1)]
    [InlineData("[Theory(DisplayName = \"d\",\n    Skip =\n \"x\")]\npublic void A(int i) { }", 1)]
    [InlineData("[SqlServerFact(SkipUnless = nameof(Ok))]\npublic void A() { }", 1)]
    [InlineData("[Fact(Explicit = true)]\npublic void A() { }", 1)]
    [InlineData("[Ignore]\npublic void A() { }", 1)]
    [InlineData("public void A() { Assert.Skip(\"x\"); }", 1)]
    [InlineData("[Fact]\npublic void A() { }", 0)]
    [InlineData("[Fact(DisplayName = \"d\")]\npublic void A() { var x = items.Skip(1); }", 0)]
    [InlineData("[Trait(\"Skip\", \"x\")]\n[Fact]\npublic void A() { }", 0)]
    [InlineData("public enum Outcome { Skip = 1 }", 0)]
    public void Сторож_бачить_вимкнений_тест_і_не_чіпає_легітимний_код(string code, int expected)
        => Assert.Equal(expected, FindDisabledTests(code).Count());

    private static string CodeOf(SourceFile file)
    {
        var builder = new StringBuilder();
        foreach (var (_, text) in file.CodeLines())
        {
            builder.Append(text).Append('\n');
        }

        return builder.ToString();
    }

    private static IEnumerable<string> FindDisabledTests(string code)
    {
        foreach (Match start in TestAttributeStart.Matches(code))
        {
            var begin = start.Index + start.Length;
            var depth = 1;
            var end = begin;
            while (end < code.Length && depth > 0)
            {
                depth += code[end] switch { '(' => 1, ')' => -1, _ => 0 };
                end++;
            }

            var arguments = code[begin..Math.Max(begin, end - 1)];
            if (DisablingArgument.IsMatch(arguments))
            {
                yield return Collapse(code[start.Index..end]);
            }
        }

        foreach (Match match in IgnoreAttribute.Matches(code))
        {
            yield return Collapse(match.Value);
        }

        foreach (Match match in RuntimeSkip.Matches(code))
        {
            yield return Collapse(match.Value);
        }
    }

    private static string Collapse(string text)
        => Regex.Replace(text, "\\s+", " ", RegexOptions.CultureInvariant).Trim();
}
