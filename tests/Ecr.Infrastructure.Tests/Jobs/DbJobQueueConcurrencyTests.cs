// tests/Ecr.Infrastructure.Tests/Jobs/DbJobQueueConcurrencyTests.cs
using System.Collections.Concurrent;
using System.Xml.Linq;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Гонки черги між хостами (<c>D14-01</c>, правки А і В «Аудиту»): claim з
/// READPAST, коалесценція постановок, «1 Running + 1 Queued позаду» на ціль.
/// </summary>
/// <remarks>Мутаційні докази — в описі коміту.</remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class DbJobQueueConcurrencyTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private static readonly TimeSpan NotBlocked = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Незакомічений_claim_хоста_A_не_блокує_B_і_B_бере_іншу_задачу()
    {
        await EnqueueAsync();
        await EnqueueAsync();
        await using var a = NewHost();
        await using var b = NewHost();

        await using var tx = await a.Db.Database.BeginTransactionAsync();
        var taken = await a.ClaimAsync("host/a");

        Task<ClaimedJob?>? other = null;
        try
        {
            other = b.ClaimAsync("host/b");
            var second = await other.WaitAsync(NotBlocked);

            Assert.NotNull(second);
            Assert.NotEqual(taken!.Claim.JobId, second.Claim.JobId);
        }
        finally
        {
            await tx.RollbackAsync();
            if (other is not null)
            {
                await other.ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    [Fact]
    [Trait("Requirement", "ФВ-12.3")]
    public async Task Два_хости_по_чотири_воркери_беруть_кожну_з_50_задач_рівно_раз()
    {
        for (var i = 0; i < 50; i++)
        {
            await EnqueueAsync();
        }

        var claims = new ConcurrentBag<JobClaimToken>();
        using var start = new SemaphoreSlim(0);

        var workers = Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            await using var host = NewHost();
            await start.WaitAsync();
            // Стеля — щоб зламаний claim, що бере те саме по колу, не завис тест.
            while (claims.Count < 200 && await host.ClaimAsync($"host/{w % 2}/{w}") is { } job)
            {
                claims.Add(job.Claim);
            }
        })).ToArray();

        start.Release(8);
        await Task.WhenAll(workers);

        Assert.Equal(50, claims.Count);
        Assert.Equal(50, claims.Select(c => c.JobId).Distinct(StringComparer.Ordinal).Count());

        // Кожна видана оренда — та, що в базі: подвійний claim лишив би один токен «мертвим».
        await using var db = Sql.CreateContext();
        var tokens = await db.JobProgresses.AsNoTracking().Where(p => p.Lane != null)
            .ToDictionaryAsync(p => p.JobId, p => p.ClaimToken);
        Assert.All(claims, c => Assert.Equal(c.Token, tokens[c.JobId]));
    }

    [Fact]
    public async Task Пятдесят_паралельних_постановок_на_одну_ціль_дають_одну_Queued()
    {
        var hosts = Enumerable.Range(0, 50).Select(_ => NewHost()).ToArray();
        try
        {
            // З'єднання відкриті заздалегідь: інакше відкриття пулу розтягує
            // старт і постановки йдуть майже послідовно — гонки немає. Раундів
            // кілька: вікно гонки вузьке, один раунд ловить її не щоразу.
            await Task.WhenAll(hosts.Select(h => h.Db.Database.OpenConnectionAsync()));

            for (var round = 0; round < 10; round++)
            {
                var target = Target();
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var enqueues = hosts.Select(async h =>
                {
                    await start.Task;
                    return await h.Queue.EnqueueAsync(Request(target), CancellationToken.None);
                }).ToArray();

                start.SetResult();
                var results = await Task.WhenAll(enqueues);

                Assert.Single(results.Select(r => r.JobId).Distinct(StringComparer.Ordinal));
                Assert.Single(results, r => r.Outcome == JobEnqueueOutcome.Created);

                await using var db = Sql.CreateContext();
                Assert.Equal(1, await db.JobProgresses.CountAsync(p => p.TargetKey == target && p.State == "Queued"));
            }
        }
        finally
        {
            foreach (var host in hosts)
            {
                await host.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Running_і_20_постановок_дають_одну_Queued_позаду_яку_claim_не_бере_доки_Running()
    {
        var target = Target();
        var runningId = await EnqueueAsync(target);
        await using var host = NewHost();
        var running = await host.ClaimAsync();

        var behind = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => EnqueueAsync(target))));
        var behindId = Assert.Single(behind.Distinct(StringComparer.Ordinal));
        Assert.NotEqual(runningId, behindId);

        // Сторонню задачу ставимо ПІСЛЯ: у порядку AvailableAt вона позаду Queued на ціль.
        var unrelated = await EnqueueAsync();

        Assert.Equal(unrelated, (await host.ClaimAsync())?.Claim.JobId);
        Assert.Null(await host.ClaimAsync());

        Assert.True(await host.Queue.CompleteAsync(running!.Claim, CancellationToken.None));
        Assert.Equal(behindId, (await host.ClaimAsync())?.Claim.JobId);
    }

    [Fact]
    public async Task Незакомічене_завершення_Running_не_блокує_claim_і_Queued_позаду_не_стає_другою_Running()
    {
        // Правка А: NOT EXISTS іде БЕЗ READPAST. Running, що в цю мить виходить
        // зі стану під чужою транзакцією, читається останньою закоміченою
        // версією (ще Running) — Queued позаду не береться, а claim не чекає.
        var target = Target();
        await EnqueueAsync(target);
        await using var a = NewHost();
        await using var b = NewHost();
        var running = (await a.ClaimAsync("host/a"))!.Claim;
        var behind = await EnqueueAsync(target);
        var unrelated = await EnqueueAsync();

        await using (var tx = await a.Db.Database.BeginTransactionAsync())
        {
            Assert.True(await a.Queue.CompleteAsync(running, CancellationToken.None));

            Task<ClaimedJob?>? pending = null;
            try
            {
                pending = b.ClaimAsync("host/b");
                Assert.Equal(unrelated, (await pending.WaitAsync(NotBlocked))?.Claim.JobId);
            }
            finally
            {
                await tx.RollbackAsync();
                if (pending is not null)
                {
                    await pending.ContinueWith(_ => { }, TaskScheduler.Default);
                }
            }
        }

        // Відкат: Running лишилась — Queued позаду й далі не береться.
        Assert.Null(await b.ClaimAsync("host/b"));

        await using var db = Sql.CreateContext();
        Assert.Equal(1, await db.JobProgresses.CountAsync(p => p.TargetKey == target && p.State == "Running"));
        Assert.Equal("Queued", (await RowAsync(behind))!.State);
    }

    [Theory]
    [InlineData("/* ecr:jobqueue-claim */", "Index Seek [IX_JobProgress_Claim]")]
    [InlineData("/* ecr:jobqueue-reclaim */", "Index Seek [IX_JobProgress_Claim]")]
    [InlineData("/* ecr:jobqueue-enqueue */", "Index Seek [UX_JobProgress_Target_Queued]")]
    public async Task Запити_черги_з_параметрами_шукають_по_фільтрованому_індексу(string marker, string expected)
    {
        // Правка В: фільтровані індекси й параметри (@lane, @target) — без
        // OPTION(RECOMPILE). План беремо з кешу за маркером у тексті запиту: той
        // самий, яким щойно виконались постановка і claim (переклейм пробується
        // першим, тож виконуються обидва).
        await EnqueueAsync(Target());
        await using var host = NewHost();
        Assert.NotNull(await host.ClaimAsync());

        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1) CONVERT(nvarchar(max), qp.query_plan)
            FROM sys.dm_exec_query_stats AS qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS st
            CROSS APPLY sys.dm_exec_query_plan(qs.plan_handle) AS qp
            WHERE CHARINDEX(@marker, st.text) > 0 AND st.text NOT LIKE N'%dm_exec_query_stats%'
              AND qp.dbid = DB_ID()
            ORDER BY qs.last_execution_time DESC;
            """;
        command.Parameters.AddWithValue("@marker", marker);
        var raw = await command.ExecuteScalarAsync() as string;
        Assert.False(raw is null, $"плану з маркером {marker} немає в кеші: запит не кешується");

        XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        var access = XDocument.Parse(raw).Descendants(ns + "RelOp")
            .SelectMany(op => op.Elements(ns + "IndexScan")
                .Where(s => (string?)s.Attribute("Lookup") is not ("1" or "true"))
                .SelectMany(s => s.Elements(ns + "Object"))
                .Where(o => (string?)o.Attribute("Alias") == "[q]")
                .Select(o => $"{(string?)op.Attribute("PhysicalOp")} {(string?)o.Attribute("Index")}"))
            .ToList();

        Assert.Equal(new List<string> { expected }, access);
    }
}
