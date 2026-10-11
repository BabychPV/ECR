using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// S2-06 (аудит 2026-10-09b): ім'я завдання SQL Agent унікальне на весь інстанс, тож завдання без бази в імені
/// означало «одне на інстанс»: <c>-FirstDeployment</c> для другої бази ECR видаляв завдання першої.
/// </summary>
/// <remarks>
/// ⚠ Це перевірка ТЕКСТУ <c>14-agent-jobs.sql</c>, а не виконання: SQL Server Agent у середовищі розробки й CI
/// немає (NEEDS-ENVIRONMENT — прогін двох баз на одному інстансі з Agent, <c>msdb.dbo.sysjobsteps</c>).
/// Тести/мутація — CI, локально не запускались.
/// </remarks>
public sealed partial class AgentJobsPerDatabaseTests
{
    private static string Script()
        => File.ReadAllText(Path.Combine(SourceTree.Root, "src", "Ecr.Infrastructure", "Persistence", "Sql", "14-agent-jobs.sql"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    // Виклик msdb-процедури з літеральним іменем завдання: @job_name = N'ECR: …'.
    [GeneratedRegex(@"EXEC\s+msdb\.dbo\.sp_(add_job|add_jobstep|add_jobschedule|add_jobserver)\b[^;]*?@job_name\s*=\s*N'", RegexOptions.Singleline)]
    private static partial Regex LiteralJobNameCall();

    /// <remarks>
    /// Мутація: повернути <c>@job_name = N'ECR: Partitions ahead'</c> у будь-який виклик → червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Імена_завдань_Agent_у_викликах_msdb_містять_базу()
    {
        var script = Script();

        Assert.DoesNotMatch(LiteralJobNameCall(), script);
        Assert.Contains("LEFT(N'ECR: Partitions ahead [' + @db + N']', 128)", script, StringComparison.Ordinal);
        Assert.Contains("LEFT(N'ECR: Physical checks [' + @db + N']', 128)", script, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Count(script, @"sp_add_jobstep\s+@job_name = @job,"));
        Assert.Equal(2, Regex.Count(script, @"sp_add_jobserver @job_name = @job;"));
    }

    /// <remarks>
    /// Старе завдання (без бази в імені) видаляється лише коли його крок виконується в ЦІЙ базі: інакше розгортання
    /// другої бази знову видаляло б чуже завдання. Мутація: прибрати <c>s.database_name = @db</c> → червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Старе_завдання_без_бази_видаляється_лише_для_своєї_бази()
    {
        var script = Script();

        foreach (var legacy in new[] { "ECR: Partitions ahead", "ECR: Physical checks" })
        {
            var guarded = new Regex(
                @"JOIN msdb\.dbo\.sysjobsteps AS s ON s\.job_id = j\.job_id\s+WHERE j\.name = N'" + Regex.Escape(legacy)
                + @"' AND s\.database_name = @db\)\s+EXEC msdb\.dbo\.sp_delete_job @job_name = N'" + Regex.Escape(legacy) + "'");
            Assert.Matches(guarded, script);

            // Безумовного видалення за старим іменем немає.
            Assert.DoesNotContain($"WHERE name = N'{legacy}')", script, StringComparison.Ordinal);
        }
    }
}
