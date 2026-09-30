// tests/Ecr.Infrastructure.Tests/Jobs/DbJobQueueTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Спільне для тестів <see cref="DbJobQueue"/>: «хост» — окремий
/// <see cref="EcrDbContext"/> (окреме з'єднання), як у різних процесів.
/// </summary>
/// <remarks>
/// ⚠ Лейни фіксовані й спільні для колекції, тож кожен тест стартує з
/// порожньої черги (<c>Lane IS NOT NULL</c>) і лишає її порожньою. Колекція
/// <c>SqlServer</c> послідовна — чужих рядків черги в цю мить немає.
/// </remarks>
public abstract class DbJobQueueTestsBase(SqlServerFixture sql) : IAsyncLifetime
{
    protected const string Code = "Ecr.Test.DbJobQueueJob";

    protected static readonly string[] DefaultLane = [JobLanes.Default];

    protected SqlServerFixture Sql => sql;

    public Task InitializeAsync() => PurgeAsync();

    public Task DisposeAsync() => PurgeAsync();

    protected static string Target() => $"{Code}~{Guid.NewGuid():N}";

    protected Host NewHost() => new(sql.CreateContext());

    protected async Task<string> EnqueueAsync(string? target = null, TimeSpan? delay = null)
    {
        await using var host = NewHost();
        return (await host.Queue.EnqueueAsync(Request(target, delay), CancellationToken.None)).JobId;
    }

    protected static JobEnqueueRequest Request(string? target = null, TimeSpan? delay = null, string payload = "{}")
        => new(Code, JobLanes.Default, payload, target, Delay: delay);

    protected async Task<JobProgress?> RowAsync(string jobId)
    {
        await using var db = sql.CreateContext();
        return await db.JobProgresses.AsNoTracking().SingleOrDefaultAsync(p => p.JobId == jobId);
    }

    /// <summary>Сирий SQL поза чергою — «зупинити годинник» оренди чи підставити лічильник.</summary>
    protected async Task ExecAsync(string sqlText, string jobId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        command.Parameters.AddWithValue("@id", jobId);
        await command.ExecuteNonQueryAsync();
    }

    protected Task ExpireLeaseAsync(string jobId)
        => ExecAsync("UPDATE itg.JobProgress SET LeaseUntil = DATEADD(second, -5, SYSUTCDATETIME()) WHERE JobId = @id;", jobId);

    private async Task PurgeAsync()
    {
        await using var db = sql.CreateContext();
        await db.JobProgresses.Where(p => p.Lane != null).ExecuteDeleteAsync();
    }

    /// <summary>Хост черги: власний контекст і власне з'єднання.</summary>
    protected sealed class Host(EcrDbContext db) : IAsyncDisposable
    {
        public EcrDbContext Db { get; } = db;

        public DbJobQueue Queue { get; } = new(db, new SystemClock());

        public Task<ClaimedJob?> ClaimAsync(string owner = "test/host")
            => Queue.ClaimAsync(DefaultLane, owner, JobQueueLimits.DefaultLease, CancellationToken.None);

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}

/// <summary>
/// Життєвий цикл задачі в <see cref="DbJobQueue"/>: транзакція постановки,
/// переклейм, скасування, ретрай і поглинання, отруйна задача, межі входу.
/// </summary>
/// <remarks>Мутаційні докази — в описі коміту.</remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class DbJobQueueTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    [Fact]
    public async Task Відкат_транзакції_викликача_відкочує_постановку_коміт_лишає()
    {
        await using var host = NewHost();

        string rolledBack;
        await using (var tx = await host.Db.Database.BeginTransactionAsync())
        {
            rolledBack = (await host.Queue.EnqueueAsync(Request(), CancellationToken.None)).JobId;
            await tx.RollbackAsync();
        }

        string committed;
        await using (var tx = await host.Db.Database.BeginTransactionAsync())
        {
            committed = (await host.Queue.EnqueueAsync(Request(), CancellationToken.None)).JobId;
            await tx.CommitAsync();
        }

        Assert.Null(await RowAsync(rolledBack));
        Assert.Equal("Queued", (await RowAsync(committed))?.State);
    }

    [Fact]
    [Trait("Requirement", "ФВ-12.3")]
    public async Task Прострочена_оренда_переклеймлюється_тим_самим_JobId_а_старий_токен_втрачає_все()
    {
        var jobId = await EnqueueAsync();
        await using var a = NewHost();
        await using var b = NewHost();

        var first = await a.ClaimAsync("host/a");
        Assert.Equal((jobId, 1, 0, false), (first!.Claim.JobId, first.Attempt, first.ReclaimCount, first.Reclaimed));

        // Живу оренду ніхто не бере.
        Assert.Null(await b.ClaimAsync("host/b"));

        await ExpireLeaseAsync(jobId);
        var second = await b.ClaimAsync("host/b");

        Assert.NotNull(second);
        Assert.Equal((jobId, 1, 1, true), (second.Claim.JobId, second.Attempt, second.ReclaimCount, second.Reclaimed));
        Assert.NotEqual(first.Claim.Token, second.Claim.Token);
        Assert.Equal("host/b", (await RowAsync(jobId))!.InstanceId);

        // Старий власник: оренда втрачена для кожної дії.
        Assert.Equal(LeaseState.Lost, await a.Queue.RenewAsync(first.Claim, JobQueueLimits.DefaultLease, CancellationToken.None));
        Assert.False(await a.Queue.CompleteAsync(first.Claim, CancellationToken.None));
        Assert.False(await a.Queue.FailAsync(first.Claim, "x", null, CancellationToken.None));
        Assert.False(await a.Queue.RequeueAsync(first.Claim, TimeSpan.Zero, CancellationToken.None));
        await using (var tx = await a.Db.Database.BeginTransactionAsync())
        {
            Assert.False(await a.Queue.FenceAsync(first.Claim, CancellationToken.None));
        }

        Assert.Equal(LeaseState.Held, await b.Queue.RenewAsync(second.Claim, JobQueueLimits.DefaultLease, CancellationToken.None));
        Assert.True(await b.Queue.CompleteAsync(second.Claim, CancellationToken.None));
        Assert.Equal("Succeeded", (await RowAsync(jobId))!.State);
    }

    [Fact]
    public async Task Скасування_з_іншого_хоста_Queued_одразу_Running_через_Renew_і_Acknowledge()
    {
        await using var worker = NewHost();
        await using var api = NewHost();

        var queued = await EnqueueAsync();
        Assert.Equal(CancelOutcome.Cancelled, await api.Queue.RequestCancelAsync(queued, CancellationToken.None));
        Assert.Equal("Cancelled", (await RowAsync(queued))!.State);
        Assert.Null(await worker.ClaimAsync());

        var running = await EnqueueAsync();
        var claim = (await worker.ClaimAsync())!.Claim;
        Assert.False(await api.Queue.IsCancelRequestedAsync(running, CancellationToken.None));

        Assert.Equal(CancelOutcome.CancelRequested, await api.Queue.RequestCancelAsync(running, CancellationToken.None));
        Assert.True(await api.Queue.IsCancelRequestedAsync(running, CancellationToken.None));
        Assert.Equal(LeaseState.CancelRequested, await worker.Queue.RenewAsync(claim, JobQueueLimits.DefaultLease, CancellationToken.None));
        Assert.Equal("Running", (await RowAsync(running))!.State);

        Assert.True(await worker.Queue.AcknowledgeCancelAsync(claim, CancellationToken.None));
        Assert.Equal("Cancelled", (await RowAsync(running))!.State);
        Assert.Equal(CancelOutcome.AlreadyFinished, await api.Queue.RequestCancelAsync(running, CancellationToken.None));
        Assert.Equal(CancelOutcome.NotFound, await api.Queue.RequestCancelAsync($"nope-{Guid.NewGuid():N}", CancellationToken.None));
    }

    [Fact]
    public async Task Requeue_без_черги_позаду_повертає_в_Queued_і_наступний_claim_це_спроба_2()
    {
        var jobId = await EnqueueAsync();
        await using var host = NewHost();

        var first = await host.ClaimAsync();
        Assert.True(await host.Queue.RequeueAsync(first!.Claim, TimeSpan.Zero, CancellationToken.None));

        var row = await RowAsync(jobId);
        Assert.Equal(("Queued", (Guid?)null), (row!.State, row.ClaimToken));

        var second = await host.ClaimAsync();
        Assert.Equal((jobId, 2, false), (second!.Claim.JobId, second.Attempt, second.Reclaimed));
    }

    [Fact]
    public async Task Requeue_при_Queued_позаду_поглинається_конвертом_jobs_absorbedBy_а_позаду_стає_доступнішою()
    {
        var target = Target();
        var runningId = await EnqueueAsync(target);
        await using var host = NewHost();
        var running = await host.ClaimAsync();

        var behind = await EnqueueAsync(target, delay: TimeSpan.FromHours(1));
        Assert.NotEqual(runningId, behind);

        Assert.True(await host.Queue.RequeueAsync(running!.Claim, TimeSpan.FromSeconds(30), CancellationToken.None));

        var ours = await RowAsync(runningId);
        Assert.Equal("Cancelled", ours!.State);
        Assert.True(JobProgressMessageCodec.TryDecode(ours.Message, out var envelope));
        Assert.Equal("jobs.absorbedBy", envelope.Key);
        Assert.Equal(behind, envelope.Params!["jobId"]);

        // MIN(година, 30 с): ретрай, що мав статися за 30 с, не відкладається на годину.
        var after = await RowAsync(behind);
        Assert.Equal("Queued", after!.State);
        Assert.InRange(after.AvailableAt!.Value, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(5));
    }

    [Fact]
    public async Task Отруйна_задача_на_межі_переклеймів_claim_не_бере_а_Expire_закриває_Failed_з_ключем()
    {
        var poison = await EnqueueAsync();
        var lastChance = await EnqueueAsync();
        await using var host = NewHost();
        Assert.NotNull(await host.ClaimAsync());
        Assert.NotNull(await host.ClaimAsync());

        await ExecAsync($"UPDATE itg.JobProgress SET ReclaimCount = {JobQueueLimits.MaxReclaims} WHERE JobId = @id;", poison);
        await ExecAsync($"UPDATE itg.JobProgress SET ReclaimCount = {JobQueueLimits.MaxReclaims - 1} WHERE JobId = @id;", lastChance);
        await ExpireLeaseAsync(poison);
        await ExpireLeaseAsync(lastChance);

        // Межа: MaxReclaims - 1 ще переклеймлюється, MaxReclaims — уже ні.
        var reclaimed = await host.ClaimAsync();
        Assert.Equal((lastChance, JobQueueLimits.MaxReclaims), (reclaimed!.Claim.JobId, reclaimed.ReclaimCount));
        Assert.Null(await host.ClaimAsync());

        Assert.Equal(1, await host.Queue.ExpireAsync(JobQueueLimits.MaxReclaims, CancellationToken.None));

        var row = await RowAsync(poison);
        Assert.Equal("Failed", row!.State);
        Assert.True(JobProgressMessageCodec.TryDecode(row.Message, out var envelope));
        Assert.Equal("jobs.leaseLostTooOften", envelope.Key);
        Assert.Equal("Running", (await RowAsync(lastChance))!.State);
    }

    [Fact]
    public async Task Expire_закриває_прострочену_з_запитом_скасування_як_Cancelled_а_живу_не_чіпає()
    {
        var cancelled = await EnqueueAsync();
        var alive = await EnqueueAsync();
        await using var host = NewHost();
        await host.ClaimAsync();
        await host.ClaimAsync();

        await host.Queue.RequestCancelAsync(cancelled, CancellationToken.None);
        await host.Queue.RequestCancelAsync(alive, CancellationToken.None);
        await ExpireLeaseAsync(cancelled);

        // Прострочену з запитом скасування claim не переклеймлює.
        Assert.Null(await host.ClaimAsync());
        Assert.Equal(1, await host.Queue.ExpireAsync(JobQueueLimits.MaxReclaims, CancellationToken.None));
        Assert.Equal("Cancelled", (await RowAsync(cancelled))!.State);
        Assert.Equal("Running", (await RowAsync(alive))!.State);
    }

    [Fact]
    public async Task Restart_провалу_починає_нову_серію_спроб_і_переклеймів()
    {
        var jobId = await EnqueueAsync();
        await using var host = NewHost();
        var claim = (await host.ClaimAsync())!.Claim;
        await ExecAsync("UPDATE itg.JobProgress SET ReclaimCount = 2 WHERE JobId = @id;", jobId);
        Assert.True(await host.Queue.FailAsync(claim, "причина", "ECR-TEST-0001", CancellationToken.None));
        Assert.Equal(("Failed", "причина", "ECR-TEST-0001"), ((await RowAsync(jobId))!.State, (await RowAsync(jobId))!.Error, (await RowAsync(jobId))!.ErrorCode));

        Assert.True(await host.Queue.RestartAsync(jobId, CancellationToken.None));
        Assert.False(await host.Queue.RestartAsync(jobId, CancellationToken.None));

        var again = await host.ClaimAsync();
        Assert.Equal((jobId, 1, 0), (again!.Claim.JobId, again.Attempt, again.ReclaimCount));
    }

    [Fact]
    public async Task Невідомий_лейн_і_завеликий_payload_відкидаються_до_запису()
    {
        await using var host = NewHost();

        await Assert.ThrowsAsync<ArgumentException>(() => host.Queue.EnqueueAsync(
            Request() with { Lane = "recalc " }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Queue.ClaimAsync(
            ["Recalc"], "h", JobQueueLimits.DefaultLease, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => host.Queue.EnqueueAsync(
            Request(payload: new string('x', JobQueueLimits.MaxPayloadLength + 1)), CancellationToken.None));

        // Рівно на межі — приймається.
        var atLimit = await host.Queue.EnqueueAsync(
            Request(payload: new string('x', JobQueueLimits.MaxPayloadLength)), CancellationToken.None);
        Assert.Equal(JobQueueLimits.MaxPayloadLength, (await RowAsync(atLimit.JobId))!.Payload!.Length);
    }

    [Fact]
    public async Task Fence_поза_транзакцією_відмова_у_транзакції_тримає_лок_до_коміту()
    {
        await using var owner = NewHost();
        await using var other = NewHost();
        await EnqueueAsync();
        var claim = (await owner.ClaimAsync())!.Claim;

        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.Queue.FenceAsync(claim, CancellationToken.None));

        // Оренда вже прострочена, але токен ще наш: fence проходить і тримає X-лок.
        await ExpireLeaseAsync(claim.JobId);

        await using (var tx = await owner.Db.Database.BeginTransactionAsync())
        {
            Assert.True(await owner.Queue.FenceAsync(claim, CancellationToken.None));

            // Поки транзакція власника відкрита, переклейм цю задачу не бачить
            // (READPAST) — і не блокується на ній.
            Assert.Null(await other.ClaimAsync("host/other").WaitAsync(TimeSpan.FromSeconds(10)));

            await tx.CommitAsync();
        }

        var reclaimed = await other.ClaimAsync("host/other");
        Assert.Equal((claim.JobId, true), (reclaimed?.Claim.JobId, reclaimed?.Reclaimed ?? false));
        Assert.False(await owner.Queue.CompleteAsync(claim, CancellationToken.None));
    }

    [Fact]
    public async Task Сесія_з_QUOTED_IDENTIFIER_OFF_не_ламає_черги_бо_кожен_пакет_вмикає_опції()
    {
        // Правка В: SqlClient дає ON типово — перевіряємо це прямо.
        await using (var fresh = Sql.CreateContext())
        {
            await fresh.Database.OpenConnectionAsync();
            await using var probe = fresh.Database.GetDbConnection().CreateCommand();
            probe.CommandText = "SELECT CONVERT(int, SESSIONPROPERTY('QUOTED_IDENTIFIER')) * 10 + CONVERT(int, SESSIONPROPERTY('ANSI_NULLS'));";
            Assert.Equal(11, (int)(await probe.ExecuteScalarAsync())!);
        }

        // А сесію, яку хтось перемкнув в OFF, черга вмикає сама. Без пулу —
        // інакше OFF перейшов би в наступний тест.
        var builder = new SqlConnectionStringBuilder(Sql.ConnectionString) { Pooling = false };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using (var off = connection.CreateCommand())
        {
            off.CommandText = "SET QUOTED_IDENTIFIER OFF; SET ANSI_NULLS OFF;";
            await off.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(connection).Options;
        await using var db = new EcrDbContext(options);
        var queue = new DbJobQueue(db, new SystemClock());

        var jobId = (await queue.EnqueueAsync(Request(Target()), CancellationToken.None)).JobId;
        var claim = await queue.ClaimAsync(DefaultLane, "h", JobQueueLimits.DefaultLease, CancellationToken.None);
        Assert.Equal(jobId, claim?.Claim.JobId);
        Assert.True(await queue.CompleteAsync(claim!.Claim, CancellationToken.None));
    }
}
