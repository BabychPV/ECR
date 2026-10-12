using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// ФВ-1.15: сторож «проєкти й періоди не видаляються» не має обходитися ні масовим
/// <c>ExecuteDelete</c>, ні сирим SQL у C#.
/// </summary>
/// <remarks>
/// ⛔ REQ-COVERAGE-2026-10-11 (§5 п. 8): наявний сторож
/// (<see cref="Проєкти_і_періоди_не_видаляються_ні_кодом_ні_скриптом"/>) шукає лише
/// <c>.Remove(змінна з «project»/«period» в імені)</c> у C# і <c>DELETE FROM doc.Project|Period</c>
/// у <c>Persistence/Sql/*.sql</c>. <c>db.Periods.Where(…).ExecuteDeleteAsync()</c>, <c>db.Projects.Remove(x)</c>
/// з іншою назвою змінної і <c>ExecuteSqlRaw("DELETE FROM doc.Period …")</c> проходили мовчки.
/// Захисту в самій БД (DENY/тригер) немає — це окреме рішення DBA; доки його немає, сторож у коді —
/// єдина лінія.
///
/// Мутаційний доказ (detached-worktree, не комітиться): додати у будь-який файл
/// <c>src/Ecr.Infrastructure</c> рядок <c>await db.Periods.Where(p =&gt; p.Id == 1).ExecuteDeleteAsync();</c>
/// — червоніє <see cref="Масове_видалення_проєктів_і_періодів_не_обходить_сторожа"/>; а
/// <see cref="Сторож_проєктів_і_періодів_упізнає_обхідні_форми_видалення"/> лишається зеленим лише поки
/// регулярки справді розрізняють ці форми.
/// </remarks>
public sealed partial class PrincipleTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-1.15")]
    public void Масове_видалення_проєктів_і_періодів_не_обходить_сторожа()
    {
        var offenders = new List<string>();

        foreach (var file in SourceTree.Production())
        {
            var text = WithoutComments(file.Text);

            foreach (var regex in new[] { DbSetBulkDeleteRegex, DbSetRemoveRegex, ProjectPeriodSqlDeleteRegex })
            {
                foreach (Match match in regex.Matches(text))
                {
                    offenders.Add($"{file.Path}: {Compact(match.Value)}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    /// Самі регулярки справді ловлять обхідні форми й не чіпають законне: інакше сторож вище лишався б
    /// зеленим на будь-якому коді (порожній результат — найгірший різновид сторожа).
    /// </summary>
    [Theory]
    [InlineData("await db.Periods.Where(p => p.ProjectId == id).ExecuteDeleteAsync(ct);", true)]
    [InlineData("db.Projects\n    .Where(p => p.Id == id)\n    .ExecuteDelete();", true)]
    [InlineData("_db.Periods.Remove(entity);", true)]
    [InlineData("context.Projects.RemoveRange(list);", true)]
    [InlineData("await db.Database.ExecuteSqlRawAsync(\"DELETE FROM doc.Period WHERE Id = 1\");", true)]
    [InlineData("var sql = \"delete from [doc].[Project] where Id = @id\";", true)]
    [InlineData("var sql = \"TRUNCATE TABLE doc.Period\";", true)]
    [InlineData("await db.ApprovalStates.Where(a => a.DocumentId == id).ExecuteDeleteAsync(ct);", false)]
    [InlineData("await db.Periods.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.State, 3));", false)]
    [InlineData("var sql = \"DELETE FROM doc.PeriodAccess WHERE Id = 1\";", false)]
    [InlineData("var n = await db.Projects.CountAsync(ct); await db.Documents.Where(d => d.Id == 1).ExecuteDeleteAsync();", false)]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-1.15")]
    public void Сторож_проєктів_і_періодів_упізнає_обхідні_форми_видалення(string code, bool expectedOffender)
    {
        var found = DbSetBulkDeleteRegex.IsMatch(code)
                    || DbSetRemoveRegex.IsMatch(code)
                    || ProjectPeriodSqlDeleteRegex.IsMatch(code);

        Assert.Equal(expectedOffender, found);
    }

    private static string Compact(string value)
        => Regex.Replace(value, @"\s+", " ").Trim();

    /// <summary>
    /// <c>….Projects|Periods …ExecuteDelete[Async](</c>: ланцюг до найближчої крапки з комою, тобто в
    /// межах одного оператора, — інакше <c>Periods.Count…; X.ExecuteDelete()</c> давало б хибний збіг.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:Projects|Periods)\b(?:(?!;)[\s\S]){0,500}?\.\s*ExecuteDelete(?:Async)?\s*\(",
        RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex DbSetBulkDeleteRegex { get; }

    /// <summary><c>db.Projects.Remove(x)</c> / <c>RemoveRange</c> незалежно від назви змінної.</summary>
    [GeneratedRegex(@"\b(?:Projects|Periods)\s*\.\s*Remove(?:Range)?\s*\(", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex DbSetRemoveRegex { get; }

    /// <summary>Сирий SQL у C#: <c>DELETE</c>/<c>TRUNCATE</c> саме таблиці <c>doc.Project</c> чи <c>doc.Period</c>.</summary>
    [GeneratedRegex(
        @"(?:DELETE|TRUNCATE\s+TABLE)\s+(?:FROM\s+)?\[?doc\]?\.\[?(?:Project|Period)\]?(?![\w\]])",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ProjectPeriodSqlDeleteRegex { get; }
}
