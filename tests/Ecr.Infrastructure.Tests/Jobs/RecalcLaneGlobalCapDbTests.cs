// tests/Ecr.Infrastructure.Tests/Jobs/RecalcLaneGlobalCapDbTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ PERF-05/10 (аудит PERF1000 21.11, рішення людини §6 п. 3; <c>D-564</c> (в)): одна стеля на всі важкі перерахунки в черзі в
/// базі (ручні, авто, каскад, діти розкладу), останнє місце — людині; «черга людей» у проході ручних; місце в черзі й
/// орієнтовний старт.
/// </summary>
/// <remarks>
/// МУТАЦІЇ (локально, не комітяться): (1) <c>HeavyCapFilter</c> → порожній — червоний <c>Стеля_тримається…</c>; (2)
/// <c>heavySystem = heavyCap</c> (без резерву людині) — червоний <c>Останнє_місце…</c>; (3) <c>fair: false</c> — червоний
/// <c>Людина_з_одним_натисканням…</c>; (4) у <c>DbJobQueuePosition.PositionSql</c> рахувати ручних попереду системної без
/// <c>@manualAhead</c> — червоний <c>Місце_в_черзі…</c>.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "PERF-05")]
public sealed class RecalcLaneGlobalCapDbTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private const string RecalcCode = "Ecr.Test.HeavyRecalcJob";

    private const string Empty = "{}";

    /// <summary>
    /// ✎ CI 22.11 (lane/integ-b7): <c>AvailableAt</c> — <c>datetime2(3)</c> від <c>SYSUTCDATETIME()</c>, тож дві постановки в одну
    /// мілісекунду рівні, і порядок між ними вирішує <c>JobId</c> (GUID) — детерміновано, але не в порядку постановки. Тести класу,
    /// що перевіряють порядок, ставлять задачам явний строго зростаючий час (крок 10 мс, хвилину тому — пізніше за масові
    /// задачі «вікна», −1000…−400 с); рівний час перевіряє окремий тест.
    /// </summary>
    private readonly DateTime _queuedFrom = DateTime.UtcNow.AddMinutes(-1);

    private int _enqueued;

    /// <summary>Стеля 3, десять одночасних claim-ів на окремих з'єднаннях: виконується не більше трьох — і ніколи більше.</summary>
    [Fact]
    public async Task Стеля_тримається_під_десятьма_одночасними_claim()
    {
        for (var i = 0; i < 12; i++)
        {
            await EnqueueRecalcAsync(user: 100 + i);
        }

        var claimed = await ClaimConcurrentlyAsync(cap: 3, claimers: 10);

        Assert.Equal(3, claimed.Count);
        Assert.Equal(3, await RunningAsync());

        // Ще одна хвиля — місць немає, доки хтось не завершиться.
        Assert.Empty(await ClaimConcurrentlyAsync(cap: 3, claimers: 10));

        await using (var host = NewHost(3))
        {
            Assert.True(await host.Queue.CompleteAsync(claimed[0].Claim, CancellationToken.None));
        }

        Assert.Single(await ClaimConcurrentlyAsync(cap: 3, claimers: 10));
        Assert.Equal(3, await RunningAsync());
    }

    /// <summary>
    /// Системна робота (авто, каскад, діти розкладу, кроки ланцюжків) бере не більше «стеля − 1»; останнє місце — людині, яка
    /// натиснула «Перерахувати», навіть коли системних задач у черзі багато.
    /// </summary>
    [Fact]
    public async Task Останнє_місце_стелі_лишається_людині()
    {
        for (var i = 0; i < 6; i++)
        {
            await EnqueueRecalcAsync(user: null);
        }

        Assert.Equal(2, (await ClaimConcurrentlyAsync(cap: 3, claimers: 10)).Count);

        var manual = await EnqueueRecalcAsync(user: 7);
        var taken = await ClaimConcurrentlyAsync(cap: 3, claimers: 5);
        Assert.Equal([manual], taken.Select(t => t.Claim.JobId));

        // Стелю вичерпано і для людини.
        await EnqueueRecalcAsync(user: 8);
        Assert.Empty(await ClaimConcurrentlyAsync(cap: 3, claimers: 5));
    }

    /// <summary>Людина з одним натисканням не стоїть за десятьма натисканнями іншої людини (PERF-10).</summary>
    [Fact]
    public async Task Людина_з_одним_натисканням_не_остання_за_десятьма_чужими()
    {
        var a = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            a.Add(await EnqueueRecalcAsync(user: 1));
        }

        var b = await EnqueueRecalcAsync(user: 2);

        var order = new List<string>();
        await using var host = NewHost(cap: 50);
        for (var i = 0; i < 11; i++)
        {
            order.Add((await host.Queue.ClaimAsync([JobLanes.Recalc], "test/fair", JobQueueLimits.DefaultLease, CancellationToken.None))!.Claim.JobId);
        }

        // A1 (найраніша, ранг 0), далі B (ранг 0) — раніше за A2 (ранг 1: одна задача A вже виконується).
        Assert.Equal([a[0], b], order.Take(2));
        Assert.Equal(a.Skip(1), order.Skip(2));
    }

    /// <summary>
    /// ✎ CI 22.11: усі задачі поставлено в ту саму мілісекунду (рівний <c>AvailableAt</c>). Гарантія та сама — людина з одним
    /// натисканням серед перших двох, а не одинадцята; рівність вирішує унікальний <c>JobId</c> однаково в claim, у виборі
    /// «черги людей» і в місці в черзі: порядок claim = порядок місць, задачі A — за <c>JobId</c>.
    /// </summary>
    [Fact]
    public async Task Рівний_час_постановки_порядок_за_JobId_людина_серед_перших_двох()
    {
        var a = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            a.Add(await EnqueueRecalcAsync(user: 1));
        }

        var b = await EnqueueRecalcAsync(user: 2);
        await using (var db = Sql.CreateContext())
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE itg.JobProgress SET AvailableAt = {_queuedFrom} WHERE Lane = {JobLanes.Recalc} AND [State] = 'Queued'");
        }

        var positions = new Dictionary<string, int>();
        await using (var db = Sql.CreateContext())
        {
            var reader = new DbJobQueuePosition(db, new SystemClock(), new QuartzJobTypeLimiter(2, recalcMaxConcurrent: 50));
            foreach (var id in a.Append(b))
            {
                positions[id] = (await reader.GetAsync(id, CancellationToken.None))!.Position;
            }
        }

        var order = new List<string>();
        await using var host = NewHost(cap: 50);
        for (var i = 0; i < 11; i++)
        {
            order.Add((await host.Queue.ClaimAsync([JobLanes.Recalc], "test/tie", JobQueueLimits.DefaultLease, CancellationToken.None))!.Claim.JobId);
        }

        var byJobId = a.Order(StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { byJobId[0], b }.Order(StringComparer.Ordinal), order.Take(2).Order(StringComparer.Ordinal));
        Assert.Equal(byJobId.Skip(1), order.Skip(2));
        Assert.Equal(Enumerable.Range(1, 11), positions.Values.Order());
        Assert.Equal(positions.OrderBy(p => p.Value).Select(p => p.Key), order);
    }

    /// <summary>
    /// ✎ Повторний огляд L-b: людина з тисячею натискань не витісняє інших із вікна кандидатів (до 5 найраніших на людину), і
    /// кандидат, чий документ зайнятий, не скидає вибір на FIFO — береться наступний за рангом.
    /// </summary>
    /// <remarks>
    /// МУТАЦІЯ: вікно без розбиття на людей (<c>UserRow</c>) — червоний рядок «вікно»; один кандидат замість кількох — червоний
    /// рядок «документ зайнятий».
    /// </remarks>
    [Theory]
    [InlineData("window")]
    [InlineData("busyDocument")]
    public async Task Черга_людей_не_скидається_на_FIFO(string scenario)
    {
        await using var host = CapHost(50);
        if (scenario == "window")
        {
            await using (var db = Sql.CreateContext())
            {
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO itg.JobProgress (JobId, JobCode, [State], [Percent], StartedAt, UpdatedAt, HeartbeatAt, CreatedAt,
                                                 CreatedByUserId, Lane, Payload, AvailableAt, ReclaimCount)
                    SELECT TOP (600) CONCAT(N'Ecr.Test.HeavyRecalcJob-a-', n.i), N'Ecr.Test.HeavyRecalcJob', 'Queued', 0,
                           SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(),
                           1, 'recalc', N'{{}}', DATEADD(second, -1000 + n.i, SYSUTCDATETIME()), 0
                      FROM (SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects) AS n
                    """);
            }

            var b = await EnqueueRecalcAsync(user: 2);
            var first = await host.Queue.ClaimAsync([JobLanes.Recalc], "test/w1", JobQueueLimits.DefaultLease, CancellationToken.None);
            var second = await host.Queue.ClaimAsync([JobLanes.Recalc], "test/w2", JobQueueLimits.DefaultLease, CancellationToken.None);
            Assert.Contains(b, new[] { first!.Claim.JobId, second!.Claim.JobId });
            return;
        }

        // A1 — документ 9001, який уже рахується; A2 — раніше за B1, але ранг A2 = 1 (A1 попереду), B1 — 0.
        var a1 = await EnqueueRecalcAsync(user: 1, document: 9001);
        var a2 = await EnqueueRecalcAsync(user: 1, document: 9002);
        var b1 = await EnqueueRecalcAsync(user: 2, document: 9003);
        await using (var db = Sql.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO itg.JobProgress (JobId, JobCode, [State], [Percent], StartedAt, UpdatedAt, HeartbeatAt, CreatedAt,
                                             DocumentId, Lane, Payload, AvailableAt, ReclaimCount, LeaseUntil)
                VALUES (N'Ecr.Test.HeavyRecalcJob-busy', N'Ecr.Test.HeavyRecalcJob', 'Running', 0, SYSUTCDATETIME(), SYSUTCDATETIME(),
                        SYSUTCDATETIME(), SYSUTCDATETIME(), 9001, 'recalc', N'{{}}', SYSUTCDATETIME(), 0, DATEADD(hour, 1, SYSUTCDATETIME()))
                """);
        }

        var claimed = await host.Queue.ClaimAsync([JobLanes.Recalc], "test/busy", JobQueueLimits.DefaultLease, CancellationToken.None);
        Assert.Equal(b1, claimed!.Claim.JobId);
        Assert.NotEqual(a2, claimed.Claim.JobId);
        Assert.NotEqual(a1, claimed.Claim.JobId);
    }

    /// <summary>
    /// Місце в черзі — порядок claim (ручні — «черга людей», далі системні FIFO) — і орієнтовний старт за медіаною кроку.
    /// </summary>
    /// <remarks>
    /// ✎ 22.11 (b8): одноразовий червоний «Expected 3, Actual 4» на <c>a2</c> у повному прогоні Jobs — на гілках
    /// <c>lane/perf-cascade</c>/<c>-fix</c> (0f365316a, 4320d6723), відгалужених ДО da4f249d7, де задачі ставились без явного
    /// <c>AvailableAt</c>: a2 і a3 в одну мілісекунду (<c>datetime2(3)</c>), нічию вирішує <c>JobId</c> (GUID) — за a3 &lt; a2
    /// ранг a3 = 1, a2 = 2, і a2 — четвертий. Порядок продукту повний і правильний; явний зростаючий час
    /// <see cref="EnqueueRecalcAsync"/> (da4f249d7) нічию прибрав. Мутація «a3 у мілісекунду a2» — червоний з тим самим
    /// повідомленням (6/8; зелені — коли GUID a3 &gt; a2).
    /// </remarks>
    [Fact]
    public async Task Місце_в_черзі_й_орієнтовний_старт()
    {
        // Медіана кроку за добу: 60 с (три завершені кроки 30/60/90 с; розклад проєкту без документа не рахується).
        await SucceededAsync(TimeSpan.FromSeconds(30), documentId: 1);
        await SucceededAsync(TimeSpan.FromSeconds(60), documentId: 1);
        await SucceededAsync(TimeSpan.FromSeconds(90), documentId: 1);
        await SucceededAsync(TimeSpan.FromHours(5), documentId: 0);
        DbJobQueuePosition.ResetMedianCache();

        var system1 = await EnqueueRecalcAsync(user: null);
        var a1 = await EnqueueRecalcAsync(user: 1);
        var a2 = await EnqueueRecalcAsync(user: 1);
        var a3 = await EnqueueRecalcAsync(user: 1);
        var b1 = await EnqueueRecalcAsync(user: 2);
        var system2 = await EnqueueRecalcAsync(user: null);

        await using var db = Sql.CreateContext();
        var reader = new DbJobQueuePosition(db, new SystemClock(), new QuartzJobTypeLimiter(2, recalcMaxConcurrent: 2));
        var positions = new Dictionary<string, JobQueuePosition?>();
        foreach (var id in new[] { system1, a1, a2, a3, b1, system2 })
        {
            positions[id] = await reader.GetAsync(id, CancellationToken.None);
        }

        Assert.Equal(1, positions[a1]!.Position);
        Assert.Equal(2, positions[b1]!.Position);
        Assert.Equal(3, positions[a2]!.Position);
        Assert.Equal(4, positions[a3]!.Position);
        Assert.Equal(5, positions[system1]!.Position);
        Assert.Equal(6, positions[system2]!.Position);

        // Стеля 2, нічого не виконується: ручні — 2 місця (перші два одразу, 3-4 — після одного кроку 60 с); системна задача —
        // «стеля − 1» = 1 місце (огляд L3), кроків попереду 5 — п'ять хвиль по 60 с.
        var now = DateTime.UtcNow;
        Assert.InRange((positions[b1]!.EstimatedStartAt!.Value - now).TotalSeconds, -5, 5);
        Assert.InRange((positions[a3]!.EstimatedStartAt!.Value - now).TotalSeconds, 55, 65);
        Assert.InRange((positions[system2]!.EstimatedStartAt!.Value - now).TotalSeconds, 295, 305);

        // Задача, що виконується, місця не має (кеш відповіді — 5 с; скинуто).
        await using var host = NewHost(cap: 2);
        var running = await host.Queue.ClaimAsync([JobLanes.Recalc], "test/pos", JobQueueLimits.DefaultLease, CancellationToken.None);
        DbJobQueuePosition.ResetMedianCache();
        Assert.Null(await reader.GetAsync(running!.Claim.JobId, CancellationToken.None));

        // Огляд L3: ланцюжок попереду рахується рештою своїх кроків. a1 виконується; попереду system1 — a2 (ланцюжок 4 кроки, 1
        // зроблено: 3), a3 і b1 (по 1) = 5 кроків; місце системної — одне (стеля − 1), одне виконується: 6 хвиль по 60 с.
        await ExecAsync(
            "UPDATE itg.JobProgress SET Payload = N'{\"chainTotal\":4,\"chainDone\":[202601]}' WHERE JobId = @id;", a2);
        DbJobQueuePosition.ResetMedianCache();
        var chainAware = await reader.GetAsync(system1, CancellationToken.None);
        Assert.Equal(4, chainAware!.Position);
        Assert.InRange((chainAware.EstimatedStartAt!.Value - now).TotalSeconds, 355, 425);
    }

    /// <summary>
    /// ✎ Огляд M2: «черга людей» не тримає claim — вибір окремим запитом без блокувань, claim — названого рядка. Тисяча ручних
    /// задач п'ятдесяти людей у черзі: кожен claim — далеко в межах тайм-ауту applock стелі (5 с), лейн не зупиняється.
    /// </summary>
    [Fact]
    public async Task Claim_з_тисячею_ручних_у_черзі_далеко_в_межах_applock()
    {
        await using (var db = Sql.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO itg.JobProgress (JobId, JobCode, [State], [Percent], StartedAt, UpdatedAt, HeartbeatAt, CreatedAt,
                                             CreatedByUserId, Lane, Payload, AvailableAt, ReclaimCount)
                SELECT TOP (1000) CONCAT(N'Ecr.Test.HeavyRecalcJob-bulk-', n.i), N'Ecr.Test.HeavyRecalcJob', 'Queued', 0,
                       SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(),
                       1000 + n.i % 50, 'recalc', N'{{"triggeredByUserId":1}}', DATEADD(millisecond, -n.i, SYSUTCDATETIME()), 0
                  FROM (SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects) AS n
                """);
        }

        // Перший claim — компіляція планів нових текстів (не вимір черги); далі — п'ять вимірів.
        await using (var warm = CapHost(50))
        {
            Assert.NotNull(await warm.Queue.ClaimAsync([JobLanes.Recalc], "test/warm", JobQueueLimits.DefaultLease, CancellationToken.None));
        }

        var slowest = TimeSpan.Zero;
        for (var i = 0; i < 5; i++)
        {
            await using var host = CapHost(50);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.NotNull(await host.Queue.ClaimAsync([JobLanes.Recalc], $"test/bulk{i}", JobQueueLimits.DefaultLease, CancellationToken.None));
            slowest = watch.Elapsed > slowest ? watch.Elapsed : slowest;
        }

        Assert.True(slowest < TimeSpan.FromSeconds(1.5), $"claim {slowest.TotalMilliseconds:F0} мс");
    }

    [Theory]
    [InlineData(1, 0, 2, 0)]
    [InlineData(2, 0, 2, 0)]
    [InlineData(3, 0, 2, 1)]
    [InlineData(1, 2, 2, 1)]
    [InlineData(5, 1, 2, 2)]
    public void Хвилі_очікування(int position, int running, int slots, int waves)
    {
        var now = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(now.AddSeconds(waves * 10), DbJobQueuePosition.EstimateStart(position, running, slots, 10_000, now));
        Assert.Null(DbJobQueuePosition.EstimateStart(position, running, slots, null, now));
    }

    private Host NewHost(int cap) => CapHost(cap);

    private Host CapHost(int cap)
    {
        var db = Sql.CreateContext();
        return new Host(db, new DbJobQueue(db, new SystemClock(), new QuartzJobTypeLimiter(2, recalcMaxConcurrent: cap)));
    }

    private async Task<List<ClaimedJob>> ClaimConcurrentlyAsync(int cap, int claimers)
    {
        var tasks = Enumerable.Range(0, claimers).Select(async i =>
        {
            await using var host = CapHost(cap);
            return await host.Queue.ClaimAsync([JobLanes.Recalc], $"test/cap{i}", JobQueueLimits.DefaultLease, CancellationToken.None);
        });
        return [.. (await Task.WhenAll(tasks)).OfType<ClaimedJob>()];
    }

    private async Task<string> EnqueueRecalcAsync(int? user, long? document = null)
    {
        await using var host = CapHost(100);
        var id = (await host.Queue.EnqueueAsync(
                new JobEnqueueRequest(RecalcCode, JobLanes.Recalc, "{}", CreatedByUserId: user, DocumentId: document),
                CancellationToken.None))
            .JobId;

        // Порядок постановки = порядок AvailableAt (див. _queuedFrom): без рівного часу в одну мілісекунду.
        var at = _queuedFrom.AddMilliseconds(10 * ++_enqueued);
        await host.Db.Database.ExecuteSqlAsync($"UPDATE itg.JobProgress SET AvailableAt = {at} WHERE JobId = {id}");
        return id;
    }

    private async Task<int> RunningAsync()
    {
        await using var db = Sql.CreateContext();
        return await db.JobProgresses.CountAsync(p => p.Lane == JobLanes.Recalc && p.State == "Running");
    }

    private async Task SucceededAsync(TimeSpan took, long documentId)
    {
        await using var db = Sql.CreateContext();
        var id = $"{RecalcCode}-{Guid.NewGuid():N}";
        var started = DateTime.UtcNow.AddMinutes(-30);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO itg.JobProgress (JobId, JobCode, [State], [Percent], StartedAt, UpdatedAt, HeartbeatAt, CreatedAt,
                                         DocumentId, Lane, Payload, AvailableAt, ReclaimCount)
            VALUES ({id}, {RecalcCode}, 'Succeeded', 100, {started}, {started + took}, {started + took}, {started},
                    {documentId}, {JobLanes.Recalc}, {Empty}, {started}, 0)
            """);
    }
}
