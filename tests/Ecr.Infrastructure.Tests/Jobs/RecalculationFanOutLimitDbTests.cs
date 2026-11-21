// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationFanOutLimitDbTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ PSS-02 (аудит 20.11, <c>D-561</c>): у режимі черги в базі дітей розкладу перерахунку проєкту
/// (<c>FanOutParentJobId</c>) одночасно виконується не більше <c>Jobs:Recalculation:FanOutMaxConcurrency</c> — на весь пул
/// процесів разом; ручний перерахунок, автоперерахунок і залежні межі не бачать і обганяють дітей, що чекають.
/// </summary>
/// <remarks>
/// Справжня конкуренція: десять «процесів» (окремі контексти й з'єднання) роблять claim одночасно, бар'єр — лише
/// <see cref="Task.WhenAll(System.Collections.Generic.IEnumerable{Task})"/>, без затримок.
/// <para>
/// МУТАЦІЇ (локально): (1) без <c>FanOutCapFilter</c> у <c>ClaimQueuedSqlFor</c> — червоні всі три (дітей Running 10);
/// (2) без applock у <c>FanOutCapPrologue</c> (лічильник без серіалізації) — <c>Десять_процесів…</c> червоний у
/// частині прогонів (5–6 дітей), тому доказ строгості — 20 прогонів поспіль.
/// </para>
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "PSS-02")]
public sealed class RecalculationFanOutLimitDbTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private const int Cap = 4;

    private const string Parent = "IRecalculationJob-project-parent-under-test";

    [Fact]
    public async Task Десять_процесів_одночасно_беруть_не_більше_межі_дітей_а_ручний_проходить_повз()
    {
        for (var i = 0; i < 10; i++)
        {
            await EnqueueRecalcAsync(documentId: 9_100_000 + i, parent: Parent);
        }

        var manual = await EnqueueRecalcAsync(documentId: 9_199_999, parent: null);

        var hosts = Enumerable.Range(0, 10).Select(_ => CappedHost()).ToList();
        try
        {
            var claimed = (await Task.WhenAll(hosts.Select((h, i) => ClaimAsync(h, $"host/{i}"))))
                .Where(c => c is not null)
                .Select(c => c!.Claim.JobId)
                .ToList();

            // Строгість: одночасний claim ніколи не перевищує межу (навіть якщо applock когось не пустив за 5 с).
            Assert.True(await RunningChildrenAsync() <= Cap);

            // ✎ Огляд L9: на навантаженій машині applock може не дочекатися — тоді той claim дітей не бере (так задумано).
            // Добираємо послідовно, доки беруть, — і тоді межа рівно заповнена.
            await using (var filler = CappedHost())
            {
                while (await ClaimAsync(filler, "host/fill") is { } more)
                {
                    claimed.Add(more.Claim.JobId);
                }
            }

            Assert.Equal(Cap, await RunningChildrenAsync());

            // Ручний (без батька) узятий, хоча стояв у черзі ПІСЛЯ всіх десяти дітей.
            Assert.Contains(manual, claimed);
            Assert.Equal(Cap + 1, claimed.Count);
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
    public async Task Місце_дитини_звільнилось_наступна_дитина_береться_за_FIFO()
    {
        var children = new List<string>();
        for (var i = 0; i < Cap + 2; i++)
        {
            children.Add(await EnqueueRecalcAsync(documentId: 9_200_000 + i, parent: Parent));
        }

        await AssertStrictlyIncreasingAvailableAtAsync(children);

        await using var host = CappedHost();
        var running = new List<ClaimedJob>();
        for (var i = 0; i < Cap; i++)
        {
            running.Add((await ClaimAsync(host, "host/a"))!);
        }

        Assert.Equal(children.Take(Cap), running.Select(r => r.Claim.JobId));
        Assert.Null(await ClaimAsync(host, "host/a"));

        // Автоперерахунок (не дитина), поставлений пізніше за дітей, — береться попри межу.
        var auto = await EnqueueRecalcAsync(documentId: 9_299_999, parent: null);
        Assert.Equal(auto, (await ClaimAsync(host, "host/a"))?.Claim.JobId);

        Assert.True(await host.Queue.CompleteAsync(running[0].Claim, CancellationToken.None));
        Assert.Equal(children[Cap], (await ClaimAsync(host, "host/a"))?.Claim.JobId);
        Assert.Null(await ClaimAsync(host, "host/a"));
    }

    [Fact]
    public async Task Без_межі_у_збірці_черга_як_доти_а_межа_рахує_лише_свій_лейн()
    {
        for (var i = 0; i < Cap + 1; i++)
        {
            await EnqueueRecalcAsync(documentId: 9_300_000 + i, parent: Parent);
        }

        // Ручна збірка без межі (тести, утиліти) — береться все.
        await using (var unlimited = NewHost())
        {
            for (var i = 0; i < Cap + 1; i++)
            {
                Assert.NotNull(await ClaimAsync(unlimited, "host/u"));
            }
        }

        // Межа вже вичерпана (5 дітей Running) — нова дитина з межею не береться, а задача лейна default з батьком
        // (не перерахунок) межі не бачить.
        await EnqueueRecalcAsync(documentId: 9_399_999, parent: Parent);
        await using var capped = CappedHost();
        Assert.Null(await ClaimAsync(capped, "host/c"));

        await using (var enqueue = NewHost())
        {
            await enqueue.Queue.EnqueueAsync(
                new JobEnqueueRequest(Code, JobLanes.Default, $"{{\"fanOutParentJobId\":\"{Parent}\"}}"), CancellationToken.None);
        }

        Assert.NotNull(await capped.Queue.ClaimAsync([JobLanes.Default], "host/c", JobQueueLimits.DefaultLease, CancellationToken.None));
    }

    /// <summary>
    /// ✎ Огляд M4 (рішення координатора за §6 п. 2): обганяють лише ручні й автоперерахунок. Системні залежні
    /// (<c>…-dependents</c>), поки діє розклад, — під тією самою межею й займають у ній місце; без розкладу — як доти.
    /// </summary>
    /// <remarks>МУТАЦІЯ: <c>FanOutCapFilter</c> без умови цілі каскаду — залежні беруться повз межу, червоний.</remarks>
    [Fact]
    public async Task Залежні_під_межею_поки_діє_розклад_а_ручні_й_авто_обганяють()
    {
        await EnqueueRecalcAsync(documentId: 9_500_000, parent: Parent);
        await EnqueueRecalcAsync(documentId: 9_500_001, parent: Parent);
        await using var host = CappedHost(cap: 1);
        var child = (await ClaimAsync(host, "host/a"))!;

        var dependents = await EnqueueRecalcAsync(9_500_010, null, target: Cascade(9_500_010, "-dependents"));
        var auto = await EnqueueRecalcAsync(9_500_011, null, target: Cascade(9_500_011, "-auto"));
        var manual = await EnqueueRecalcAsync(9_500_012, null, user: 7);

        var taken = new List<string>();
        while (await ClaimAsync(host, "host/a") is { } next)
        {
            taken.Add(next.Claim.JobId);
        }

        // Ручний — першим (прохід «спершу ручні»), авто — повз межу; залежні й друга дитина чекають межі.
        Assert.Equal([manual, auto], taken);
        Assert.Equal("Queued", (await RowAsync(dependents))!.State);

        // Межу звільнено — далі FIFO серед підмежових: друга дитина (раніша), потім залежні.
        Assert.True(await host.Queue.CompleteAsync(child.Claim, CancellationToken.None));
        var secondChild = (await ClaimAsync(host, "host/a"))!;
        Assert.NotEqual(dependents, secondChild.Claim.JobId);
        Assert.Null(await ClaimAsync(host, "host/a"));

        // Розклад закінчився (дітей немає) — залежні як доти, без межі.
        Assert.True(await host.Queue.CompleteAsync(secondChild.Claim, CancellationToken.None));
        Assert.Equal(dependents, (await ClaimAsync(host, "host/a"))?.Claim.JobId);
    }

    /// <summary>
    /// ✎ Огляд H2: річний розклад іде вглиб — крок ланцюжка лишає місце документа в FIFO, тож наступний claim бере той
    /// самий документ, а не перший місяць наступного; ручний перерахунок (будь-якого документа й місяця) — попереду всіх.
    /// </summary>
    /// <remarks>
    /// МУТАЦІЇ: <c>ContinueAsync</c> з <c>AvailableAt = зараз</c> — другий claim бере документ B, червоний; без проходу
    /// «спершу ручні» — ручний за дітьми, червоний.
    /// </remarks>
    [Fact]
    public async Task Рік_іде_вглиб_по_документу_а_ручний_пізнішого_місяця_попереду_дітей()
    {
        var a = await EnqueueRecalcAsync(9_600_001, Parent, target: Cascade(9_600_001, "-year"));
        var b = await EnqueueRecalcAsync(9_600_002, Parent, target: Cascade(9_600_002, "-year"));
        var c = await EnqueueRecalcAsync(9_600_003, Parent, target: Cascade(9_600_003, "-year"));
        await using var host = CappedHost(cap: 1);

        for (var month = 1; month <= 3; month++)
        {
            var step = (await ClaimAsync(host, "host/w"))!;
            Assert.Equal(a, step.Claim.JobId);
            Assert.True(await host.Queue.ContinueAsync(
                step.Claim, $"{{\"documentId\":9600001,\"fanOutParentJobId\":\"{Parent}\",\"chainAfter\":20260{month}}}",
                TimeSpan.Zero, 202600 + month, CancellationToken.None));
        }

        // Людина просить березень документа C, поки A посеред року, а B і C ще не починались.
        var manual = await EnqueueRecalcAsync(9_600_003, null, target: Cascade(9_600_003, "-p202603"), user: 7, period: 202603);
        Assert.Equal(manual, (await ClaimAsync(host, "host/w"))?.Claim.JobId);

        // Далі — знову A (вглиб), а не B.
        var next = (await ClaimAsync(host, "host/w"))!;
        Assert.Equal(a, next.Claim.JobId);
        Assert.Equal("Queued", (await RowAsync(b))!.State);
        Assert.Equal("Queued", (await RowAsync(c))!.State);
    }

    private static string Cascade(long documentId, string suffix)
        => $"{nameof(Ecr.Application.Ports.IRecalculationJob)}~doc{documentId}{suffix}";

    private Host CappedHost(int cap = Cap)
    {
        var db = Sql.CreateContext();
        return new Host(db, new DbJobQueue(db, new SystemClock(), new QuartzJobTypeLimiter(2, recalcFanOutMaxConcurrency: cap)));
    }

    private static Task<ClaimedJob?> ClaimAsync(Host host, string owner)
        => host.Queue.ClaimAsync([JobLanes.Recalc], owner, JobQueueLimits.DefaultLease, CancellationToken.None);

    private async Task<string> EnqueueRecalcAsync(
        long documentId, string? parent, string? target = null, int? user = null, int? period = null)
    {
        await using var host = NewHost();
        var periodJson = period is { } p ? $",\"periodKey\":{p}" : string.Empty;
        var payload = parent is null
            ? $"{{\"documentId\":{documentId}{periodJson}}}"
            : $"{{\"documentId\":{documentId},\"fanOutParentJobId\":\"{parent}\"{periodJson}}}";
        var jobId = (await host.Queue.EnqueueAsync(
                new JobEnqueueRequest(Code, JobLanes.Recalc, payload, target, user, DocumentId: documentId),
                CancellationToken.None))
            .JobId;
        await AwaitNextQueueMillisecondAsync(jobId);
        return jobId;
    }

    /// <summary>
    /// Чекає, доки годинник СУБД на основі claim (<c>CAST(SYSUTCDATETIME() AS datetime2(3))</c>) перейде
    /// <c>AvailableAt</c> щойно поставленої задачі, — наступна постановка тесту отримує строго пізніший момент.
    /// </summary>
    /// <remarks>
    /// ⛔ FIFO черги — <c>ORDER BY AvailableAt, JobId</c>, а <c>AvailableAt</c> — <c>datetime2(3)</c>: дві постановки в одну
    /// мілісекунду СУБД рівні за моментом, і між ними порядок — за випадковим <c>JobId</c> (GUID), не за приходом. На
    /// швидкому CI (Linux, SQL Server у контейнері) постановки підряд потрапляли в одну мілісекунду, і
    /// <c>Місце_дитини_звільнилось…</c> брав замість <c>children[Cap]</c> дитину з тієї самої мілісекунди з меншим GUID
    /// (локально: штучна рівність моментів <c>children[Cap]</c> і <c>children[Cap + 1]</c> — червоний 99 зі 200). Для продукту це не
    /// дефект: діти розкладу ставляться за <c>Document.Id</c> без залежностей між собою, і порядок у межах мілісекунди
    /// нічого не означає; детермінований (той самий <c>JobId</c>) — тож і «рік углиб» тримається.
    /// </remarks>
    private async Task AwaitNextQueueMillisecondAsync(string jobId)
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @at datetime2(3) = (SELECT AvailableAt FROM itg.JobProgress WHERE JobId = @id);
            WHILE CAST(SYSUTCDATETIME() AS datetime2(3)) <= @at
                WAITFOR DELAY '00:00:00.001';
            """;
        command.Parameters.AddWithValue("@id", jobId);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Моменти доступності задач у порядку списку — строго зростають (передумова FIFO-тверджень).</summary>
    private async Task AssertStrictlyIncreasingAvailableAtAsync(List<string> jobIds)
    {
        await using var db = Sql.CreateContext();
        var at = await db.JobProgresses.AsNoTracking()
            .Where(p => jobIds.Contains(p.JobId))
            .ToDictionaryAsync(p => p.JobId, p => p.AvailableAt);
        for (var i = 1; i < jobIds.Count; i++)
        {
            Assert.True(at[jobIds[i - 1]] < at[jobIds[i]], $"AvailableAt {jobIds[i - 1]} не раніший за {jobIds[i]}");
        }
    }

    private async Task<int> RunningChildrenAsync()
    {
        await using var db = Sql.CreateContext();
        return await db.JobProgresses.AsNoTracking()
            .CountAsync(p => p.Lane == JobLanes.Recalc && p.State == "Running" && p.FanOutParentJobId != null);
    }
}
