using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// S1-06 (аудит 3): регулярний вираз перевірки введення не закінчується <c>$</c>.
/// </summary>
/// <remarks>
/// ⛔ У .NET <c>$</c> без <c>RegexOptions.Multiline</c> збігається в кінці рядка І ПЕРЕД кінцевим переведенням рядка:
/// <c>^[A-Z]+$</c> приймає «ABC» + LF. Код ролі, ключ рядка, SID чи ім'я параметра з прихованим LF проходив перевірку,
/// потрапляв у базу, журнал чи лексер виразів, а перевірка «рівно цей формат» була брехнею. Кінець рядка — <c>\z</c>.
///
/// ⚠ Сторож дивиться на вихідні тексти: атрибути <c>[GeneratedRegex(...)]</c> і сталі <c>*Pattern</c>, з яких
/// будуються вирази. Виняток — вираз з <c>Multiline</c> (там <c>$</c> — це межа рядка свідомо, <c>SqlBatches</c>).
/// Мутація: повернути <c>$</c> замість <c>\z</c> у будь-якому з виразів — тест перелічує файл і вираз.
/// </remarks>
public sealed partial class RegexEndAnchorTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Жоден_вираз_перевірки_введення_не_закінчується_знаком_долара()
    {
        var offenders = new List<string>();
        var seen = 0;

        foreach (var file in SourceTree.Production())
        {
            foreach (Match match in GeneratedRegexAttribute().Matches(file.Text))
            {
                seen++;
                var rest = match.Groups["rest"].Value;
                if (rest.Contains("Multiline", StringComparison.Ordinal))
                {
                    continue;
                }

                var pattern = match.Groups["verbatim"].Success
                    ? match.Groups["verbatim"].Value.Replace("\"\"", "\"", StringComparison.Ordinal)
                    : Unescape(match.Groups["regular"].Value);

                if (EndsWithDollar().IsMatch(pattern))
                {
                    offenders.Add($"{file.Path}: {pattern}");
                }
            }

            foreach (Match match in PatternConstant().Matches(file.Text))
            {
                seen++;
                var pattern = match.Groups["verbatim"].Success
                    ? match.Groups["verbatim"].Value.Replace("\"\"", "\"", StringComparison.Ordinal)
                    : Unescape(match.Groups["regular"].Value);

                if (EndsWithDollar().IsMatch(pattern))
                {
                    offenders.Add($"{file.Path}: const {pattern}");
                }
            }
        }

        // Сторож не мовчить на порожньому дереві: виразів у продукті десятки.
        Assert.True(seen > 10, $"Знайдено лише {seen} виразів — сторож не бачить джерел.");

        Assert.True(
            offenders.Count == 0,
            "Вирази, що закінчуються `$` (приймають кінцевий LF) — заміни на `\\z`:" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    private static string Unescape(string literal)
        => literal.Replace("\\\\", "\\", StringComparison.Ordinal).Replace("\\\"", "\"", StringComparison.Ordinal);

    /// <summary>Кінцевий <c>$</c>, перед яким парна кількість зворотних скісних рисок (тобто він не екранований).</summary>
    [GeneratedRegex(@"(?:^|[^\\])(?:\\\\)*\$\z")]
    private static partial Regex EndsWithDollar();

    [GeneratedRegex(
        "GeneratedRegex\\(\\s*(?:@\"(?<verbatim>(?:[^\"]|\"\")*)\"|\"(?<regular>(?:[^\"\\\\]|\\\\.)*)\")(?<rest>[^\\]]*)\\]",
        RegexOptions.Singleline)]
    private static partial Regex GeneratedRegexAttribute();

    [GeneratedRegex(
        "const\\s+string\\s+\\w*Pattern\\w*\\s*=\\s*(?:@\"(?<verbatim>(?:[^\"]|\"\")*)\"|\"(?<regular>(?:[^\"\\\\]|\\\\.)*)\")\\s*;",
        RegexOptions.Singleline)]
    private static partial Regex PatternConstant();
}
