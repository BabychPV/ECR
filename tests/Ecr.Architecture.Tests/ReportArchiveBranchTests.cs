using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// R-1 (аудит 2026-11): архівна гілка статичних сирих вʼюх SSRS — ДОСЛІВНА копія гарячої із заміною таблиць
/// <c>doc.*</c> на <c>arc.*</c>, позначкою <c>IsArchived = 1</c> і умовою «екземпляра немає в <c>doc.*</c>».
/// </summary>
/// <remarks>
/// ⛔ Дві гілки <c>UNION ALL</c> — це той самий текст двічі. Правка лише однієї (нова колонка, інший JOIN) дала б
/// різні дані для живого й заархівованого року без жодної помилки: звіт SSRS мовчки показав би інше. Сторож
/// порівнює гілки посимвольно. Стабільні вʼюхи збирає процедура з однієї голови й одного хвоста — там розбіжності
/// бути не може, їх перевіряє <c>ArchivedYearReportViewsTests</c>.
/// </remarks>
public sealed class ReportArchiveBranchTests
{
    private const string RptFile = "src/Ecr.Infrastructure/Persistence/Sql/05-rpt-views.sql";

    private const string NotInHot =
        "\n  AND NOT EXISTS (SELECT 1 FROM doc.TableInstance AS hot WHERE hot.PeriodKey = ti.PeriodKey AND hot.Id = ti.Id)";

    private static string Sql() => File.ReadAllText(
            Path.Combine(SourceTree.Root, RptFile.Replace('/', Path.DirectorySeparatorChar)))
        .Replace("\r\n", "\n", StringComparison.Ordinal);

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "R-1")]
    [InlineData("rpt.v_DocumentCells", new[] { "doc.CellValue ", "doc.TableRow ", "doc.TableInstance " })]
    [InlineData(
        "rpt.v_DocumentCellsByPermit",
        new[] { "doc.CellValue ", "doc.TableRow ", "doc.TableInstance ", "doc.CellPermitPart " })]
    public void Архівна_гілка_вʼюхи_дослівно_повторює_гарячу(string view, string[] archivedTables)
    {
        var sql = Sql();
        var head = $"CREATE OR ALTER VIEW {view}\nAS\n";
        var start = sql.IndexOf(head, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Вʼюху {view} не знайдено.");
        start += head.Length;
        var end = sql.IndexOf("\nGO", start, StringComparison.Ordinal);
        var body = sql[start..end];

        var branches = body.Split("\nUNION ALL\n");
        Assert.Equal(2, branches.Length);

        var hot = branches[0];
        Assert.Contains("CAST(0 AS bit) AS IsArchived", hot, StringComparison.Ordinal);

        var expected = hot;
        foreach (var table in archivedTables)
        {
            Assert.Contains(table, hot, StringComparison.Ordinal);
            expected = expected.Replace(table, string.Concat("arc.", table.AsSpan("doc.".Length)), StringComparison.Ordinal);
        }

        expected = expected.Replace("CAST(0 AS bit) AS IsArchived", "CAST(1 AS bit) AS IsArchived", StringComparison.Ordinal)
                   + NotInHot;

        Assert.EndsWith(";", branches[1], StringComparison.Ordinal);
        Assert.Equal(expected, branches[1][..^1]);

        // Таблиці, що архівуються, у гарячій гілці — лише з doc.*, в архівній — лише з arc.* (окрім умови «немає в doc.*»).
        var archive = branches[1][..^(NotInHot.Length + 1)];
        foreach (var table in archivedTables)
        {
            Assert.DoesNotContain(table, archive, StringComparison.Ordinal);
        }
    }
}
