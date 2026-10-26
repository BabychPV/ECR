// tests/Ecr.Infrastructure.Tests/Jobs/SubmissionSnapshotRetentionTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Workflow;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// AN-134: прибирання старих зрізів подання <c>calc.SubmissionSnapshot</c>. Зрізи погодження й
/// <c>RetainSnapshots</c> найновіших не видаляються ніколи; без <c>DeleteEnabled</c> прогін лише лічить.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНІ ДОКАЗИ (див. коміт): (1) прибрати з <c>NOT EXISTS (… Pinned …)</c> у <c>CandidatesCte</c> —
/// червоніє <see cref="Зрізи_погодження_не_видаляються_навіть_коли_вони_старші_за_межу"/> (t2 і t4 видалені);
/// (2) замінити <c>r.Rn &gt; @retain</c> на <c>r.Rn &gt; 0</c> — червоніє
/// <see cref="Лишає_останні_N_зрізів_і_не_чіпає_інші_трійки"/>.
/// <para>
/// ⚠ База спільна на колекцію, а прибирання глобальне: кожен тест працює на СВОЇХ документах і порівнює
/// лічильники з базовим значенням, а не з абсолютним.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class SubmissionSnapshotRetentionTests(SqlServerFixture sql)
{
    private static readonly DateTime T0 = new(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);

    private static SnapshotRetentionOptions Delete(int retain = 5, int batch = 50, int maxBatches = 200)
        => new(Enabled: true, DeleteEnabled: true, retain, batch, TimeSpan.Zero, maxBatches);

    private static SnapshotRetentionOptions DryRun(int retain = 5)
        => new(Enabled: true, DeleteEnabled: false, retain, 50, TimeSpan.Zero, 200);

    private static SubmissionSnapshotRetentionJob Job(EcrDbContext db, IConfiguration? configuration = null)
        => new(db, new TestClock(T0.AddDays(30)), configuration ?? new ConfigurationBuilder().Build(),
            NullLogger<SubmissionSnapshotRetentionJob>.Instance);

    private static SubmissionSnapshot Snapshot(TestDocument d, int minute, string? payload = null, int? sheetDefId = null)
        => new(
            d.DocumentId, sheetDefId ?? d.SheetDefId, d.PeriodKey.Value, d.TemplateVersionId,
            "[]", numericMode: null, calendarMode: null,
            payload ?? $"[{minute}]", new byte[32], T0.AddMinutes(minute), 1);

    private async Task<List<long>> IdsAsync(TestDocumentBuilder chain, TestDocument d, int? sheetDefId = null)
    {
        await using var db = chain.CreateContext();
        var sheet = sheetDefId ?? d.SheetDefId;
        return await db.SubmissionSnapshots.AsNoTracking()
            .Where(s => s.DocumentId == d.DocumentId && s.SheetDefId == sheet && s.PeriodKey == d.PeriodKey.Value)
            .OrderBy(s => s.SubmittedAt)
            .Select(s => s.Id)
            .ToListAsync(CancellationToken.None);
    }

    /// <summary>Мінути зрізів (з payload <c>[m]</c>) трійки, що лишилися, за зростанням часу.</summary>
    private static async Task<List<int>> MinutesAsync(TestDocumentBuilder chain, TestDocument d)
    {
        await using var db = chain.CreateContext();
        var payloads = await db.SubmissionSnapshots.AsNoTracking()
            .Where(s => s.DocumentId == d.DocumentId && s.PeriodKey == d.PeriodKey.Value)
            .OrderBy(s => s.SubmittedAt)
            .Select(s => s.PayloadJson)
            .ToListAsync(CancellationToken.None);
        return [.. payloads.Select(p => int.Parse(p.Trim('[', ']'), System.Globalization.CultureInfo.InvariantCulture))];
    }

    private static async Task AddAsync(TestDocumentBuilder chain, params SubmissionSnapshot[] snapshots)
    {
        await using var db = chain.CreateContext();
        db.SubmissionSnapshots.AddRange(snapshots);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static async Task<SnapshotRetentionOutcome> RunAsync(
        TestDocumentBuilder chain, SnapshotRetentionOptions options)
    {
        await using var db = chain.CreateContext();
        return await Job(db).RunAsync(options, null, CancellationToken.None);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Лишає_останні_N_зрізів_і_не_чіпає_інші_трійки()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await chain.BuildAsync();
        var other = await chain.BuildAsync();

        // 12 зрізів документа (хвилини 0..11) + 5 зрізів іншого документа: рівно на межі, не чіпаються.
        await AddAsync(chain, [.. Enumerable.Range(0, 12).Select(m => Snapshot(doc, m))]);
        await AddAsync(chain, [.. Enumerable.Range(0, 5).Select(m => Snapshot(other, m))]);

        var outcome = await RunAsync(chain, Delete(retain: 5));

        Assert.False(outcome.DryRun);
        Assert.True(outcome.Deleted >= 7);
        Assert.Equal([7, 8, 9, 10, 11], await MinutesAsync(chain, doc));
        Assert.Equal([0, 1, 2, 3, 4], await MinutesAsync(chain, other));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зрізи_погодження_не_видаляються_навіть_коли_вони_старші_за_межу()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await chain.BuildAsync();

        await AddAsync(chain, [.. Enumerable.Range(0, 12).Select(m => Snapshot(doc, m))]);

        await using (var db = chain.CreateContext())
        {
            // Повне погодження о хв. 2 (Approve) і проміжний крок маршруту о хв. 4 (ApproveStep): зрізи 2 і 4 — докази.
            // Подання о хв. 6 зрізу не береже: це не підпис.
            db.ApprovalEvents.Add(Event(doc, ApprovalAction.Submit, 6));
            db.ApprovalEvents.Add(Event(doc, ApprovalAction.ApproveStep, 4));
            db.ApprovalEvents.Add(Event(doc, ApprovalAction.Approve, 2));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        var outcome = await RunAsync(chain, Delete(retain: 5));

        Assert.True(outcome.Deleted >= 5);
        Assert.Equal([2, 4, 7, 8, 9, 10, 11], await MinutesAsync(chain, doc));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зріз_погодження_з_ApprovalState_без_журналу_теж_береться_під_захист()
    {
        // Погодження до появи журналу (BE-11): є лише ApprovedAt на стані.
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await chain.BuildAsync();

        await AddAsync(chain, [.. Enumerable.Range(0, 10).Select(m => Snapshot(doc, m))]);

        await using (var db = chain.CreateContext())
        {
            var state = new ApprovalState(doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value);
            state.Submit(1, T0.AddMinutes(1));
            state.Approve(2, T0.AddMinutes(3));
            db.ApprovalStates.Add(state);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await RunAsync(chain, Delete(retain: 2));

        Assert.Equal([3, 8, 9], await MinutesAsync(chain, doc));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task DryRun_нічого_не_видаляє_і_лічить_кандидатів_байти_та_збережені_зрізи_погодження()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var baseline = await RunAsync(chain, DryRun());
        var doc = await chain.BuildAsync();

        // 8 зрізів по 100 символів (200 байт у nvarchar): кандидати за межі 5 — хв. 0,1,2; хв. 1 — зріз погодження.
        var payload = new string('x', 100);
        await AddAsync(chain, [.. Enumerable.Range(0, 8).Select(m => Snapshot(doc, m, payload))]);
        await using (var db = chain.CreateContext())
        {
            db.ApprovalEvents.Add(Event(doc, ApprovalAction.Approve, 1));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        var outcome = await RunAsync(chain, DryRun());

        Assert.True(outcome.DryRun);
        Assert.Equal(0, outcome.Deleted);
        Assert.Equal(2, outcome.Candidates - baseline.Candidates);
        Assert.Equal(400, outcome.Bytes - baseline.Bytes);
        Assert.Equal(1, outcome.PinnedKept - baseline.PinnedKept);
        Assert.Equal(8, (await IdsAsync(chain, doc)).Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Типова_конфігурація_не_видаляє_а_лише_лічить_і_пише_підсумок_у_журнал_обслуговування()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await chain.BuildAsync();
        await AddAsync(chain, [.. Enumerable.Range(0, 9).Select(m => Snapshot(doc, m))]);

        await using (var db = chain.CreateContext())
        {
            // Порожня конфігурація = типові значення: Enabled=true, DeleteEnabled=false.
            await Job(db).ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);
        }

        Assert.Equal(9, (await IdsAsync(chain, doc)).Count);

        await using var verify = chain.CreateContext();
        var run = await verify.MaintenanceRuns.AsNoTracking()
            .Where(r => r.JobCode == SubmissionSnapshotRetentionJob.Code)
            .OrderByDescending(r => r.Id)
            .FirstAsync(CancellationToken.None);
        Assert.Equal("Succeeded", run.Status);
        Assert.Contains("\"mode\":\"dryRun\"", run.DetailsJson, StringComparison.Ordinal);
        Assert.Contains("\"deleted\":0", run.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task DeleteEnabled_у_конфігурації_вмикає_видалення_через_ExecuteAsync()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await chain.BuildAsync();
        await AddAsync(chain, [.. Enumerable.Range(0, 9).Select(m => Snapshot(doc, m))]);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SnapshotRetentionOptions.DeleteEnabledKey] = "true",
                [SnapshotRetentionOptions.RetainSnapshotsKey] = "3",
            })
            .Build();

        await using (var db = chain.CreateContext())
        {
            await Job(db, configuration).ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);
        }

        Assert.Equal([6, 7, 8], await MinutesAsync(chain, doc));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Видаляє_пакетами_поважає_стелю_пакетів_і_ідемпотентне()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);

        // База спільна: залишки сусідніх тестів (>5 зрізів на трійку) зіпсували б точні лічильники нижче.
        await RunAsync(chain, Delete(retain: 5, batch: 500, maxBatches: 1000));
        var doc = await chain.BuildAsync();

        // 1000 малих зрізів однієї трійки.
        await AddAsync(chain, [.. Enumerable.Range(0, 1000).Select(m => Snapshot(doc, m))]);

        // Стеля 3 пакети по 100: видалено рівно 300, найстарші.
        var first = await RunAsync(chain, Delete(retain: 5, batch: 100, maxBatches: 3));
        Assert.Equal(3, first.Batches);
        Assert.Equal(300, first.Deleted);
        Assert.Equal(700, (await IdsAsync(chain, doc)).Count);

        // Решта: 695 = 6 повних пакетів + пакет 95; пакет менший за межу зупиняє цикл.
        var second = await RunAsync(chain, Delete(retain: 5, batch: 100, maxBatches: 200));
        Assert.Equal(695, second.Deleted);
        Assert.Equal(7, second.Batches);
        Assert.Equal([995, 996, 997, 998, 999], await MinutesAsync(chain, doc));

        // Повторний прохід нічого не робить.
        var third = await RunAsync(chain, Delete(retain: 5, batch: 100, maxBatches: 200));
        Assert.Equal(0, third.Deleted);
        Assert.Equal(1, third.Batches);
        Assert.Equal(5, (await IdsAsync(chain, doc)).Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Мінімум_два_зрізи_валідація_у_читанні_і_у_виконанні()
    {
        foreach (var raw in new[] { "1", "0", "-3" })
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [SnapshotRetentionOptions.RetainSnapshotsKey] = raw })
                .Build();
            Assert.Equal(SnapshotRetentionOptions.MinRetainSnapshots, SnapshotRetentionOptions.Read(configuration).RetainSnapshots);
        }

        Assert.Equal(5, SnapshotRetentionOptions.Read(new ConfigurationBuilder().Build()).RetainSnapshots);
        Assert.False(SnapshotRetentionOptions.Read(new ConfigurationBuilder().Build()).DeleteEnabled);

        // Прямий виклик з недійсним значенням (повз Read) теж відмовляє, а не видаляє.
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await chain.BuildAsync();
        await AddAsync(chain, [.. Enumerable.Range(0, 4).Select(m => Snapshot(doc, m))]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => RunAsync(chain, Delete(retain: 1)));
        Assert.Equal(4, (await IdsAsync(chain, doc)).Count);

        // Мінімум 2 діє: лишаються два останні.
        await RunAsync(chain, Delete(retain: 2));
        Assert.Equal([2, 3], await MinutesAsync(chain, doc));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Подання_під_час_прибирання_не_губить_нових_зрізів_і_не_ламає_читання()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await chain.BuildAsync();

        // Затверджений зріз на початку: має пережити весь прогін.
        await AddAsync(chain, [.. Enumerable.Range(0, 20).Select(m => Snapshot(doc, m))]);
        await using (var db = chain.CreateContext())
        {
            db.ApprovalEvents.Add(Event(doc, ApprovalAction.Approve, 3));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        const int Submits = 60;
        using var stop = new CancellationTokenSource();

        // «Подання»: нові зрізи тієї ж трійки з наростаючим часом, кожен у власному контексті.
        var submitter = Task.Run(async () =>
        {
            for (var m = 100; m < 100 + Submits; m++)
            {
                await AddAsync(chain, Snapshot(doc, m));
                await Task.Delay(3);
            }
        });

        // «Прибирання» крутиться, доки подання йде, і ще раз після нього.
        var versionsOk = true;
        var pruner = Task.Run(async () =>
        {
            while (!submitter.IsCompleted)
            {
                await RunAsync(chain, Delete(retain: 5, batch: 4));

                // Читання версій (як DocumentVersionStore.ListAsync) не падає і завжди бачить хоч один зріз.
                await using var read = chain.CreateContext();
                var listed = await new DocumentVersionStore(read)
                    .ListAsync(doc.DocumentId, doc.PeriodKey, 100, CancellationToken.None);
                versionsOk &= listed.Count > 0;
            }
        });

        await Task.WhenAll(submitter, pruner);
        Assert.True(versionsOk);

        await RunAsync(chain, Delete(retain: 5, batch: 50));

        var minutes = await MinutesAsync(chain, doc);
        var last = 100 + Submits - 1;
        // Лишилися: затверджений (3) і п'ять НАЙНОВІШИХ; жоден новий зріз не втрачено серед них.
        Assert.Equal([3, last - 4, last - 3, last - 2, last - 1, last], minutes);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зрізи_погодження_кожного_циклу_Approve_Reopen_Submit_Approve_захищені()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await chain.BuildAsync();

        var snapshots = new List<SubmissionSnapshot>();
        await using (var db = chain.CreateContext())
        {
            for (var cycle = 0; cycle < 4; cycle++)
            {
                var b = cycle * 100;
                snapshots.Add(Snapshot(doc, b));       // подання
                snapshots.Add(Snapshot(doc, b + 1));   // зріз після правки погоджувача = зріз погодження
                db.ApprovalEvents.Add(Event(doc, ApprovalAction.Submit, b));
                db.ApprovalEvents.Add(Event(doc, ApprovalAction.Approve, b + 1));
                db.ApprovalEvents.Add(Event(doc, ApprovalAction.Reopen, b + 2));
            }

            // Чернеткові подання після останнього циклу.
            for (var m = 400; m < 404; m++)
            {
                snapshots.Add(Snapshot(doc, m));
            }

            db.SubmissionSnapshots.AddRange(snapshots);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await RunAsync(chain, Delete(retain: 2));

        // Зріз погодження КОЖНОГО циклу (1, 101, 201, 301) + 2 найновіші (402, 403); подання (0, 100, …) не береже ніхто.
        Assert.Equal([1, 101, 201, 301, 402, 403], await MinutesAsync(chain, doc));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Режими_у_DetailsJson_disabled_dryRun_delete_не_плутаються()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await chain.BuildAsync();
        await AddAsync(chain, [.. Enumerable.Range(0, 8).Select(m => Snapshot(doc, m))]);

        async Task<string> DetailsAsync(Dictionary<string, string?> values)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            await using (var db = chain.CreateContext())
            {
                await Job(db, configuration).ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);
            }

            await using var verify = chain.CreateContext();
            return (await verify.MaintenanceRuns.AsNoTracking()
                .Where(r => r.JobCode == SubmissionSnapshotRetentionJob.Code)
                .OrderByDescending(r => r.Id)
                .FirstAsync(CancellationToken.None)).DetailsJson!;
        }

        // Enabled=false (навіть при DeleteEnabled=true): нічого не робить, режим disabled.
        var disabled = await DetailsAsync(new()
        {
            [SnapshotRetentionOptions.EnabledKey] = "false",
            [SnapshotRetentionOptions.DeleteEnabledKey] = "true",
        });
        Assert.Contains("\"mode\":\"disabled\"", disabled, StringComparison.Ordinal);
        Assert.Equal(8, (await IdsAsync(chain, doc)).Count);

        var dry = await DetailsAsync(new() { [SnapshotRetentionOptions.RetainSnapshotsKey] = "3" });
        Assert.Contains("\"mode\":\"dryRun\"", dry, StringComparison.Ordinal);
        Assert.Contains("\"recoveryModel\":\"", dry, StringComparison.Ordinal);
        Assert.Equal(8, (await IdsAsync(chain, doc)).Count);

        var oldest = (await IdsAsync(chain, doc))[0];
        var del = await DetailsAsync(new()
        {
            [SnapshotRetentionOptions.DeleteEnabledKey] = "true",
            [SnapshotRetentionOptions.RetainSnapshotsKey] = "3",
        });
        Assert.Contains("\"mode\":\"delete\"", del, StringComparison.Ordinal);
        // Перелік для відновлення: Id і трійка видаленого зрізу.
        Assert.Contains($"\"id\":{oldest},\"documentId\":{doc.DocumentId},\"sheetDefId\":{doc.SheetDefId},\"periodKey\":{doc.PeriodKey.Value}", del, StringComparison.Ordinal);
        Assert.Equal([5, 6, 7], await MinutesAsync(chain, doc));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перелік_видалених_обмежений_двома_сотнями()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await chain.BuildAsync();
        await AddAsync(chain, [.. Enumerable.Range(0, 260).Select(m => Snapshot(doc, m))]);

        var outcome = await RunAsync(chain, Delete(retain: 5, batch: 100));

        Assert.True(outcome.Deleted >= 255);
        Assert.Equal(SubmissionSnapshotRetentionJob.MaxListedSnapshots, outcome.DeletedSnapshots.Count);
    }

    private static ApprovalEvent Event(TestDocument d, ApprovalAction action, int minute)
        => new(
            d.DocumentId, d.SheetDefId, d.PeriodKey.Value,
            Ecr.Domain.Enums.DocumentStatus.Submitted, Ecr.Domain.Enums.DocumentStatus.Approved,
            action, byUserId: 2, T0.AddMinutes(minute));
}
