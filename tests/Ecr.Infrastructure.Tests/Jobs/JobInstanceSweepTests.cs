// tests/Ecr.Infrastructure.Tests/Jobs/JobInstanceSweepTests.cs
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Міграція <c>Analiz1JobsCoverageIndexes</c>: <c>itg.JobProgress.InstanceId</c> і
/// закриття на старті рядків ПОПЕРЕДНЬОГО процесу цієї ж машини незалежно від
/// свіжості биття; два нові індекси.
/// </summary>
/// <remarks>
/// ⚠ Ім'я «машини» в тесті — випадкове (<c>t{guid}</c>): закриття глобальне для
/// префікса, і справжнє ім'я машини зачепило б рядки сусідніх тестів цього ж
/// прогону. Моменти — у 2031 році, як в <see cref="AbandonedWorkSweeperTests"/>.
/// <para>
/// Мутаційні докази — в описі коміту.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class JobInstanceSweepTests(SqlServerFixture sql)
{
    private static readonly DateTime At = new(2031, 3, 5, 9, 0, 0, DateTimeKind.Utc);

    private const string Reason = "Застосунок перезапущено: тест.";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Старт_закриває_рядки_попереднього_процесу_цієї_машини_зі_свіжим_биттям_і_не_чіпає_інші()
    {
        var machine = $"t{Guid.NewGuid():N}"[..12];
        var current = JobProgressStore.InstanceIdOf(machine, Guid.NewGuid());
        var previous = JobProgressStore.InstanceIdOf(machine, Guid.NewGuid());

        // ⚠ Інша машина, чиє ім'я ПОЧИНАЄТЬСЯ з імені цієї: префікс мусить
        // закінчуватися роздільником, інакше «t1234» зачепив би «t1234x».
        var otherMachine = JobProgressStore.InstanceIdOf(machine + "x", Guid.NewGuid());

        await using var db = sql.CreateContext();
        var store = new JobProgressStore(db);
        var fresh = At.AddSeconds(-30);

        var prevRunning = await ArrangeAsync(store, db, previous, fresh, queued: false);
        var prevQueued = await ArrangeAsync(store, db, previous, fresh, queued: true);
        var other = await ArrangeAsync(store, db, otherMachine, fresh, queued: false);
        var mine = await ArrangeAsync(store, db, current, fresh, queued: false);
        var all = new[] { prevRunning, prevQueued, other, mine };

        try
        {
            // Періодичний прохід (без процесу, що стартує): биття свіже — не чіпає нікого.
            await new AbandonedWorkSweeper(db, new JobProgressStore(db))
                .SweepAsync(AbandonedWorkSweeper.AbandonedJobReason, At, purge: false, CancellationToken.None);

            Assert.Equal(
                ["Running", "Queued", "Running", "Running"],
                await StatesAsync(all));

            // Старт процесу `current` на цій машині.
            var outcome = await new AbandonedWorkSweeper(db, new JobProgressStore(db))
                .SweepAsync(Reason, At, purge: false, CancellationToken.None, (machine, current));

            Assert.True(outcome.Jobs >= 2);
            Assert.Equal(
                ["Failed", "Failed", "Running", "Running"],
                await StatesAsync(all));

            await using var check = sql.CreateContext();
            var closed = await check.JobProgresses.AsNoTracking().SingleAsync(p => p.JobId == prevRunning);
            Assert.Equal((Reason, At), (closed.Error, closed.UpdatedAt));
        }
        finally
        {
            await db.JobProgresses.Where(p => all.Contains(p.JobId)).ExecuteDeleteAsync();
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Постановка_старт_і_перезапуск_пишуть_ідентифікатор_поточного_процесу()
    {
        // ⚠ Кожен крок — у СВОЄМУ контексті: відстежена сутність зі старим
        // InstanceId у пам'яті не дала б EF побачити зміну, і тест брехав би.
        await using var db = sql.CreateContext();
        var jobId = $"inst-{Guid.NewGuid():N}";

        try
        {
            await using (var queue = sql.CreateContext())
            {
                await new JobProgressStore(queue).QueueAsync(jobId, "Ecr.Test.InstanceJob", At, CancellationToken.None);
            }

            Assert.Equal(JobProgressStore.CurrentInstanceId, await InstanceOfAsync(jobId));

            await SetInstanceAsync(db, jobId, "elsewhere/0");
            await using (var start = sql.CreateContext())
            {
                await new JobProgressStore(start).StartAsync(jobId, "Ecr.Test.InstanceJob", At, CancellationToken.None);
            }

            Assert.Equal(JobProgressStore.CurrentInstanceId, await InstanceOfAsync(jobId));

            await SetInstanceAsync(db, jobId, "elsewhere/0");
            await using (var restart = sql.CreateContext())
            {
                Assert.True(await new JobProgressStore(restart).RestartAsync(jobId, At, CancellationToken.None));
            }

            Assert.Equal(JobProgressStore.CurrentInstanceId, await InstanceOfAsync(jobId));
        }
        finally
        {
            await db.JobProgresses.Where(p => p.JobId == jobId).ExecuteDeleteAsync();
        }

        // Формат: «{машина}/{GUID N}», у межах стовпця.
        Assert.StartsWith(JobProgressStore.CurrentMachineName + "/", JobProgressStore.CurrentInstanceId, StringComparison.Ordinal);
        Assert.True(JobProgressStore.CurrentInstanceId.Length <= Domain.Entities.Integration.JobProgress.MaxInstanceIdLength);
        Assert.Equal(64, JobProgressStore.InstanceIdOf(JobProgressStore.MachineNameOf(new string('m', 80)), Guid.NewGuid()).Length);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData("itg.JobProgress", "IX_JobProgress_CreatedBy_UpdatedAt", "CreatedByUserId,UpdatedAt DESC", "JobCode,State", "")]
    [InlineData(
        "itg.CollectionCoverage", "IX_CollectionCoverage_SourceEntity_CoveredTo", "SourceEntityId,CoveredTo",
        "CoveredFrom", "([Status] IS NULL)")]
    public async Task Індекс_міграції_є_в_базі_з_ключем_INCLUDE_і_фільтром(
        string table, string index, string keys, string includes, string filter)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT STRING_AGG(c.name + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END, ',')
                        WITHIN GROUP (ORDER BY ic.key_ordinal)
                 FROM sys.index_columns ic
                 JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                 WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0),
                (SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY c.name)
                 FROM sys.index_columns ic
                 JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                 WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1),
                ISNULL(i.filter_definition, N'')
            FROM sys.indexes i
            WHERE i.object_id = OBJECT_ID(@table) AND i.name = @index
            """;
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@index", index);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"{index} немає в {table}");
        Assert.Equal((keys, includes, filter), (reader.GetString(0), reader.GetString(1), reader.GetString(2)));
    }

    private static async Task<string> ArrangeAsync(
        JobProgressStore store, EcrDbContext db, string instanceId, DateTime heartbeat, bool queued)
    {
        var jobId = $"inst-{Guid.NewGuid():N}";

        if (queued)
        {
            await store.QueueAsync(jobId, "Ecr.Test.InstanceJob", heartbeat, CancellationToken.None);
        }
        else
        {
            await store.StartAsync(jobId, "Ecr.Test.InstanceJob", heartbeat, CancellationToken.None);
        }

        await SetInstanceAsync(db, jobId, instanceId);
        return jobId;
    }

    private static Task<int> SetInstanceAsync(EcrDbContext db, string jobId, string instanceId)
        => db.JobProgresses
            .Where(p => p.JobId == jobId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.InstanceId, instanceId));

    private async Task<string?> InstanceOfAsync(string jobId)
    {
        await using var check = sql.CreateContext();
        return await check.JobProgresses.AsNoTracking()
            .Where(p => p.JobId == jobId)
            .Select(p => p.InstanceId)
            .SingleAsync();
    }

    private async Task<List<string>> StatesAsync(IReadOnlyList<string> jobIds)
    {
        await using var check = sql.CreateContext();
        var states = await check.JobProgresses.AsNoTracking()
            .Where(p => jobIds.Contains(p.JobId))
            .ToDictionaryAsync(p => p.JobId, p => p.State);

        return [.. jobIds.Select(id => states[id])];
    }
}
