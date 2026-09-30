using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// T-SQL застосунку й розгортання не виходить за підлогу SQL Server 2016 SP1
/// (<c>D-101</c>, <c>D-206</c>: система ставиться на Standard або Enterprise).
/// </summary>
/// <remarks>
/// ⛔ Уже траплялося: <c>01-filegroups.sql</c> будував DDL через
/// <c>STRING_AGG</c> (з'явився у 2017), і на 2016 розгортання падало першим же
/// пакетом. Інстанси розробки й CI — 2019+ (Developer), тож жоден інтеграційний
/// тест цього не бачить у принципі: функція там просто існує.
/// <para>
/// ⚠ Коментарі вирізаються перед пошуком — пояснення «чому не <c>STRING_AGG</c>»
/// поруч із заміною не має робити сторож червоним. У <c>.cs</c> — лише цілі
/// рядки <c>//</c> (усередині рядкового літералу SQL <c>//</c> не буває
/// коментарем), і регістр враховується, щоб <c>text.Trim()</c> не був
/// «викликом <c>TRIM</c>». У <c>.sql</c> — усе після <c>--</c>, регістр ні.
/// </para>
/// </remarks>
public sealed partial class SqlServer2016FloorTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "D-101")]
    public void T_SQL_не_використовує_функцій_новіших_за_SQL_Server_2016()
    {
        var root = SourceTree.Root;
        var src = Path.Combine(root, "src");

        var scannedSql = 0;
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains("/bin/", StringComparison.Ordinal)
                || relative.Contains("/node_modules/", StringComparison.Ordinal))
            {
                continue;
            }

            var isSql = path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase);
            if (!isSql && !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (isSql)
            {
                scannedSql++;
            }

            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = isSql ? StripSqlComment(lines[i]) : StripCsComment(lines[i]);
                var regex = isSql ? SqlRegex() : CsRegex();
                foreach (Match match in regex.Matches(code))
                {
                    offenders.Add($"{relative}:{i + 1}: {match.Groups[1].Value}");
                }
            }
        }

        // ⚠ Порожній знаменник доводить не відсутність порушень, а відсутність
        // перевірки: переїдуть скрипти — сторож має почервоніти, а не мовчати.
        Assert.True(scannedSql >= 10, $"Знайдено лише {scannedSql} .sql під src — шлях до скриптів змінився?");

        Assert.Empty(offenders.Order(StringComparer.Ordinal));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("SELECT @sql = STRING_AGG(x, N',')", true)]
    [InlineData("select string_agg (x, ',')", true)]
    [InlineData("SET @a = TRIM(@b);", true)]
    [InlineData("SET @a = LTRIM(RTRIM(@b));", false)]
    [InlineData("-- ⛔ FOR XML PATH, а не STRING_AGG(...)", false)]
    [InlineData("SELECT 1; -- STRING_AGG(x)", false)]
    public void Сторож_бачить_функцію_і_не_бачить_коментар(string line, bool expected)
    {
        // Доказ, що сторож не зелений «сам собою»: той самий пошук, що вище.
        Assert.Equal(expected, SqlRegex().IsMatch(StripSqlComment(line)));
    }

    private static string StripSqlComment(string line)
    {
        var at = line.IndexOf("--", StringComparison.Ordinal);
        return at < 0 ? line : line[..at];
    }

    private static string StripCsComment(string line)
        => line.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : line;

    /// <summary>Функції T-SQL, яких немає у 2016 SP1 (2017+: STRING_AGG, CONCAT_WS, TRIM, TRANSLATE; 2022+: решта).</summary>
    private const string Functions =
        @"\b(STRING_AGG|CONCAT_WS|TRIM|TRANSLATE|GREATEST|LEAST|GENERATE_SERIES|DATE_BUCKET|APPROX_PERCENTILE_CONT|APPROX_PERCENTILE_DISC)\s*\(";

    [GeneratedRegex(Functions, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SqlRegex();

    [GeneratedRegex(Functions, RegexOptions.CultureInvariant)]
    private static partial Regex CsRegex();
}
