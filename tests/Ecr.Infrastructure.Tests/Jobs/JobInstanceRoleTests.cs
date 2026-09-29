// tests/Ecr.Infrastructure.Tests/Jobs/JobInstanceRoleTests.cs
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// P3 (ФВ-9.8): роль процесу в <c>itg.JobProgress.InstanceId</c>
/// (<c>{машина}/{роль}/{GUID}</c>) і межа «рядки черги в базі закриває лише
/// прострочена оренда» для <c>FailPreviousInstanceAsync</c>/<c>FailStaleAsync</c>.
/// </summary>
/// <remarks>
/// ⚠ Ім'я «машини» — випадкове, як у <see cref="JobInstanceSweepTests"/>:
/// закриття глобальне для префікса. Моменти — у 2031 році.
/// <para>
/// Мутаційні докази (прогнано, див. опис коміту): прибрати роль із префікса →
/// рядок <c>wrk</c> закрито стартом Api, червоний; прибрати <c>Lane == null</c>
/// → рядки черги закрито, червоний.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class JobInstanceRoleTests(SqlServerFixture sql)
{
    private static readonly DateTime At = new(2031, 4, 7, 9, 0, 0, DateTimeKind.Utc);

    private const string Reason = "Застосунок перезапущено: тест P3.";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Старт_Api_не_чіпає_рядки_воркера_цієї_машини_і_закриває_попередній_Api_та_старий_формат()
    {
        var machine = $"t{Guid.NewGuid():N}"[..12];
        var currentApi = JobProgressStore.InstanceIdOf(machine, JobProgressStore.RoleApi, Guid.NewGuid());
        var fresh = At.AddSeconds(-30);

        var previousApi = await InsertAsync(
            JobProgressStore.InstanceIdOf(machine, JobProgressStore.RoleApi, Guid.NewGuid()), fresh);
        var legacy = await InsertAsync($"{machine}/{Guid.NewGuid():N}", fresh);
        var worker = await InsertAsync(
            JobProgressStore.InstanceIdOf(machine, JobProgressStore.RoleWorker, Guid.NewGuid()), fresh);
        var mine = await InsertAsync(currentApi, fresh);
        var all = new[] { previousApi, legacy, worker, mine };

        try
        {
            await using var db = sql.CreateContext();
            var failed = await new JobProgressStore(db).FailPreviousInstanceAsync(
                machine, JobProgressStore.RoleApi, currentApi, Reason, At, CancellationToken.None);

            Assert.Equal(2, failed);
            Assert.Equal(["Failed", "Failed", "Running", "Running"], await StatesAsync(all));

            // Симетрично: старт воркера закриває свій попередній рядок, але не рядки Api.
            var currentWorker = JobProgressStore.InstanceIdOf(machine, JobProgressStore.RoleWorker, Guid.NewGuid());
            var apiAlive = await InsertAsync(
                JobProgressStore.InstanceIdOf(machine, JobProgressStore.RoleApi, Guid.NewGuid()), fresh);
            var legacyAlive = await InsertAsync($"{machine}/{Guid.NewGuid():N}", fresh);
            all = [.. all, apiAlive, legacyAlive];

            await new JobProgressStore(db).FailPreviousInstanceAsync(
                machine, JobProgressStore.RoleWorker, currentWorker, Reason, At, CancellationToken.None);

            Assert.Equal(["Failed", "Running", "Running"], await StatesAsync([worker, apiAlive, legacyAlive]));
        }
        finally
        {
            await DeleteAsync(all);
        }
    }

    /// <remarks>
    /// ⚠ Старий формат обрізав ім'я машини до 31, новий — до 27. Для довгого імені
    /// рядок старого Api (31 символ) мусить закритися стартом нового Api (27).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Старт_Api_з_довгим_іменем_машини_закриває_рядок_старого_формату_з_довшим_обрізанням()
    {
        var longName = $"L{Guid.NewGuid():N}{Guid.NewGuid():N}";
        var machine = JobProgressStore.MachineNameOf(longName);
        Assert.Equal(27, machine.Length);

        var current = JobProgressStore.InstanceIdOf(machine, JobProgressStore.RoleApi, Guid.NewGuid());
        var legacy = await InsertAsync($"{longName[..31]}/{Guid.NewGuid():N}", At.AddSeconds(-30));
        var worker = await InsertAsync(
            JobProgressStore.InstanceIdOf(machine, JobProgressStore.RoleWorker, Guid.NewGuid()), At.AddSeconds(-30));

        try
        {
            await using var db = sql.CreateContext();
            await new JobProgressStore(db).FailPreviousInstanceAsync(
                machine, JobProgressStore.RoleApi, current, Reason, At, CancellationToken.None);

            Assert.Equal(["Failed", "Running"], await StatesAsync([legacy, worker]));
        }
        finally
        {
            await DeleteAsync([legacy, worker]);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task FailPrevious_і_FailStale_не_чіпають_рядки_черги_в_базі()
    {
        var machine = $"t{Guid.NewGuid():N}"[..12];
        var current = JobProgressStore.InstanceIdOf(machine, JobProgressStore.RoleApi, Guid.NewGuid());
        var previous = JobProgressStore.InstanceIdOf(machine, JobProgressStore.RoleApi, Guid.NewGuid());
        var stale = At.AddHours(-1);

        // Однакові за всім, крім Lane: дзеркало Quartz закривається, рядок черги — ні.
        var mirrorPrevious = await InsertAsync(previous, At.AddSeconds(-30));
        var queuePrevious = await InsertAsync(previous, At.AddSeconds(-30), lane: "default");
        var mirrorStale = await InsertAsync($"elsewhere{machine}/wrk/{Guid.NewGuid():N}", stale);
        var queueStale = await InsertAsync($"elsewhere{machine}/wrk/{Guid.NewGuid():N}", stale, lane: "recalc");
        var all = new[] { mirrorPrevious, queuePrevious, mirrorStale, queueStale };

        try
        {
            await using var db = sql.CreateContext();
            var store = new JobProgressStore(db);

            await store.FailPreviousInstanceAsync(
                machine, JobProgressStore.RoleApi, current, Reason, At, CancellationToken.None);
            Assert.Equal(["Failed", "Running"], await StatesAsync([mirrorPrevious, queuePrevious]));

            await store.FailStaleAsync(Reason, At, CancellationToken.None);
            Assert.Equal(["Failed", "Running"], await StatesAsync([mirrorStale, queueStale]));

            // Health рахує застарілі тим самим предикатом, що й прибирання: рядок
            // черги з давнім биттям у лічильник не входить (прибрати його — число те саме).
            var withQueueRow = await store.SummarizeStaleAsync(At, CancellationToken.None);
            await DeleteAsync([queueStale]);
            var withoutQueueRow = await store.SummarizeStaleAsync(At, CancellationToken.None);
            Assert.Equal(withoutQueueRow.Count, withQueueRow.Count);
        }
        finally
        {
            await DeleteAsync(all);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Роль_з_переліку_фіксується_першим_ідентифікатором_і_входить_у_межу_стовпця()
    {
        var id = JobProgressStore.CurrentInstanceId;
        Assert.StartsWith($"{JobProgressStore.CurrentMachineName}/{JobProgressStore.CurrentRole}/", id, StringComparison.Ordinal);
        Assert.Contains(JobProgressStore.CurrentRole, JobProgressStore.Roles);
        Assert.All(JobProgressStore.Roles, r => Assert.Equal(3, r.Length));

        // Та сама роль — без змін; інша після видачі ідентифікатора — відмова.
        JobProgressStore.UseRole(JobProgressStore.CurrentRole);
        var other = JobProgressStore.Roles.First(r => r != JobProgressStore.CurrentRole);
        Assert.Throws<InvalidOperationException>(() => JobProgressStore.UseRole(other));
        Assert.Equal(id, JobProgressStore.CurrentInstanceId);

        Assert.Throws<ArgumentException>(() => JobProgressStore.UseRole("xyz"));
        Assert.Throws<ArgumentException>(() => JobProgressStore.InstanceIdOf("m", "worker", Guid.NewGuid()));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => JobProgressStore.InstanceIdOf(new string('m', 28), JobProgressStore.RoleApi, Guid.NewGuid()));

        // Ідентифікатор чужої ролі не приймається як «поточний» — інакше старт закрив би свої ж рядки.
        await using var db = sql.CreateContext();
        await Assert.ThrowsAsync<ArgumentException>(() => new JobProgressStore(db).FailPreviousInstanceAsync(
            "m", JobProgressStore.RoleApi,
            JobProgressStore.InstanceIdOf("m", JobProgressStore.RoleWorker, Guid.NewGuid()),
            Reason, At, CancellationToken.None));
    }

    private async Task<string> InsertAsync(string instanceId, DateTime heartbeat, string? lane = null)
    {
        var jobId = $"p3-{Guid.NewGuid():N}";

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        // ⚠ SET-опції явно — як у кожному сирому SQL черги (фільтровані індекси).
        command.CommandText = """
            SET QUOTED_IDENTIFIER ON;
            SET ANSI_NULLS ON;
            INSERT INTO itg.JobProgress
                (JobId, JobCode, State, StartedAt, UpdatedAt, HeartbeatAt, InstanceId, Lane, AvailableAt, Payload)
            VALUES (@id, 'Ecr.Test.P3RoleJob', 'Running', @hb, @hb, @hb, @instance, @lane,
                    CASE WHEN @lane IS NOT NULL THEN @hb END,
                    CASE WHEN @lane IS NOT NULL THEN N'{}' END);
            """;
        command.Parameters.AddWithValue("@id", jobId);
        command.Parameters.AddWithValue("@hb", heartbeat);
        command.Parameters.AddWithValue("@instance", instanceId);
        command.Parameters.AddWithValue("@lane", (object?)lane ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();

        return jobId;
    }

    private async Task DeleteAsync(IReadOnlyCollection<string> ids)
    {
        await using var db = sql.CreateContext();
        await db.JobProgresses.Where(p => ids.Contains(p.JobId)).ExecuteDeleteAsync();
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
