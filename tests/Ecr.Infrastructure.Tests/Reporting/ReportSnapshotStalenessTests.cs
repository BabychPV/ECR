// tests/Ecr.Infrastructure.Tests/Reporting/ReportSnapshotStalenessTests.cs
using System.Globalization;
using Microsoft.Data.SqlClient;
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// Застарілість зрізів після перерахунку (ФВ-10.5) — реальним завершенням
/// прогону (<see cref="RunCalculationHandler.CompleteAsync"/>) проти SQL Server.
/// </summary>
/// <remarks>
/// ⛔ До цього <c>InvalidateReportSnapshotsAsync</c> був порожньою заглушкою:
/// зріз, побудований до перерахунку, показувався як чинний, хоча його числа вже
/// не збігалися з системою, і сліду в журналі не лишалося.
/// <para>
/// Мутаційний доказ: у <c>ReportSnapshotStaleness.StaleSnapshotIds</c> прибрати
/// <c>r.FinishedAt &gt; s.BuiltAt</c> — червоніє
/// <see cref="Зріз_побудований_після_перерахунку_не_застарілий"/>; прибрати
/// умову <c>ErrorMessage == null</c> — червоніє
/// <see cref="Прогін_що_не_став_актуальним_зрізу_не_старить"/>; прибрати
/// <c>!staleWithoutThisRun.Contains</c> — червоніє
/// <see cref="Уже_застарілий_зріз_не_журналюється_вдруге"/>; прибрати фільтр
/// періоду в журналі — червоніє перший тест (зріз іншого періоду в журналі).
/// </para>
/// <para>
/// ⚠ <c>exceptRunId</c> мутацією не ловиться, і це чесно: у нинішньому порядку
/// <c>FinishedAt</c> свого прогону ще не збережено, коли журнал питає базу. Він —
/// страховка на випадок, якщо <c>SaveChanges</c> колись стане раніше: тоді без
/// нього журнал мовчки спорожнів би, а перший тест це й покаже.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class ReportSnapshotStalenessTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перерахунок_робить_зріз_застарілим_і_пише_журнал_не_чіпаючи_поданого()
    {
        var arrange = await ArrangeAsync();

        // Три зрізи, побудовані ДО перерахунку: чернетка свого періоду, поданий
        // зріз усього року (з сумою — іммутабельний) і зріз ІНШОГО періоду.
        var draft = await SnapshotAsync(arrange, arrange.Period, Now.AddHours(-2));
        var submitted = await SnapshotAsync(arrange, periodKey: null, Now.AddHours(-2), submit: true);
        var otherPeriod = await SnapshotAsync(arrange, arrange.Period + 1, Now.AddHours(-2));
        var before = await RowAsync(arrange, submitted);

        var run = await RunAsync(arrange, arrange.Period);
        var journal = await CompleteAsync(arrange, run, Now);

        var list = await ListAsync(arrange);
        Assert.True(list[draft].IsStale, "Чернетка свого періоду мала застаріти.");
        Assert.True(list[submitted].IsStale, "Зріз усього року старить прогін будь-якого періоду.");
        Assert.False(list[otherPeriod].IsStale, "Прогін одного періоду не старить зріз іншого.");

        // Журнал — по записові на зріз, що застарів саме зараз, з прогоном.
        Assert.Equal([draft, submitted], journal.Select(r => (long)r.EntityId).Order().ToArray());
        Assert.All(journal, r =>
        {
            Assert.Equal(RunCalculationHandler.SnapshotAuditEntityType, r.EntityType);
            Assert.Equal(RunCalculationHandler.SnapshotInvalidatedOperation, r.Operation);
            Assert.Equal(arrange.TemplateVersionId, r.TemplateVersionId);
            Assert.Equal($"calc-run:{run}", r.CorrelationId);
        });

        // ⛔ Стан, а не видалення і не перезапис: поданий зріз той самий.
        var after = await RowAsync(arrange, submitted);
        Assert.Equal(SnapshotStatus.Submitted, after.Status);
        Assert.Equal(before.ContentHash, after.ContentHash);
        Assert.Equal(before.RowCount, after.RowCount);
        Assert.True(after.IsCurrent);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зріз_побудований_після_перерахунку_не_застарілий()
    {
        var arrange = await ArrangeAsync();

        var run = await RunAsync(arrange, arrange.Period);
        var journal = await CompleteAsync(arrange, run, Now);
        var fresh = await SnapshotAsync(arrange, arrange.Period, Now.AddMinutes(5));

        Assert.Empty(journal);
        Assert.False((await ListAsync(arrange))[fresh].IsStale);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Уже_застарілий_зріз_не_журналюється_вдруге()
    {
        var arrange = await ArrangeAsync();
        var snapshot = await SnapshotAsync(arrange, arrange.Period, Now.AddHours(-2));

        var first = await CompleteAsync(arrange, await RunAsync(arrange, arrange.Period), Now);
        var second = await CompleteAsync(arrange, await RunAsync(arrange, arrange.Period), Now.AddHours(1));

        Assert.Single(first);
        Assert.Empty(second);
        Assert.True((await ListAsync(arrange))[snapshot].IsStale);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прогін_що_не_став_актуальним_зрізу_не_старить()
    {
        var arrange = await ArrangeAsync();

        // Два прогони одного документа: новіший завершується першим, потім
        // зріз, і лише тоді старіший — він стає `Superseded` одразу, без
        // актуальності, і його числа звіт не бачить ніколи.
        var older = await RunAsync(arrange, arrange.Period, arrange.DocumentId);
        var newer = await RunAsync(arrange, arrange.Period, arrange.DocumentId);
        await CompleteAsync(arrange, newer, Now);
        var snapshot = await SnapshotAsync(arrange, arrange.Period, Now.AddMinutes(5));

        var journal = await CompleteAsync(arrange, older, Now.AddMinutes(10));

        await using (var db = arrange.Builder.CreateContext())
        {
            Assert.Equal(
                CalculationRun.SupersededStatus,
                (await db.CalculationRuns.AsNoTracking().SingleAsync(r => r.Id == older)).Status);
        }

        Assert.Empty(journal);
        Assert.False((await ListAsync(arrange))[snapshot].IsStale);
    }

    /// <summary>
    /// Прогін ІНШОГО проєкту зрізу не старить. Мутаційний доказ (2026-10-01): прибрати
    /// <c>r.ProjectId == s.ProjectId</c> у <c>StaleSnapshotIds</c> — тест червоніє, інші тести
    /// класу лишаються зеленими (у них один проєкт).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прогін_іншого_проєкту_зрізу_не_старить()
    {
        var mine = await ArrangeAsync();
        var foreign = await ArrangeAsync();
        Assert.NotEqual(mine.ProjectId, foreign.ProjectId);

        var snapshot = await SnapshotAsync(mine, mine.Period, Now.AddHours(-2));
        var yearSnapshot = await SnapshotAsync(mine, periodKey: null, Now.AddHours(-2));

        var journal = await CompleteAsync(foreign, await RunAsync(foreign, foreign.Period), Now);

        Assert.DoesNotContain(journal, r => (long)r.EntityId == snapshot || (long)r.EntityId == yearSnapshot);
        var list = await ListAsync(mine);
        Assert.False(list[snapshot].IsStale, "Прогін чужого проєкту не старить зріз періоду.");
        Assert.False(list[yearSnapshot].IsStale, "Прогін чужого проєкту не старить зріз усього року.");
    }

    /// <summary>
    /// Вимір 2026-10-01 (docs/build/perf/jobs-stale-health-2026-10-01.md): перевірка заст. зрізів
    /// при завершенні прогону не читає ЧУЖІ (давні) прогони проєкту — без
    /// <c>IX_CalculationRun_Project_FinishedAt</c> EXISTS скановував усі прогони проєкту.
    /// Мутація: прибрати індекс з CalculationsConfiguration + міграції — тест червоніє.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перевірка_зрізів_не_читає_давніх_прогонів_проєкту()
    {
        var arrange = await ArrangeAsync();
        await SnapshotAsync(arrange, arrange.Period, Now.AddHours(-2));
        var run = await RunAsync(arrange, arrange.Period, arrange.DocumentId);
        await CompleteAsync(arrange, run, Now);

        async Task<long> ReadsAsync()
        {
            await using var connection = new SqlConnection(sql.ConnectionString);
            await connection.OpenAsync();

            async Task<long> SessionReadsAsync()
            {
                await using var q = connection.CreateCommand();
                q.CommandText = "SELECT logical_reads FROM sys.dm_exec_sessions WHERE session_id = @@SPID";
                return Convert.ToInt64(await q.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            }

            await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(connection).Options);
            var before = await SessionReadsAsync();
            var invalidated = await new CalculationResultStore(db, new TestClock(Now))
                .InvalidateReportSnapshotsAsync(run, CancellationToken.None);
            Assert.NotNull(invalidated);
            return await SessionReadsAsync() - before;
        }

        var readsBefore = await ReadsAsync();

        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 120;
            command.CommandText = """
                INSERT calc.CalculationRun (ProjectId, PeriodKey, Status, StartedAt, FinishedAt, ErrorMessage, DocumentId)
                SELECT TOP (@n) @p, @k, N'Superseded', '2020-01-01', '2020-01-02', NULL, NULL
                FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b
                """;
            command.Parameters.AddWithValue("@n", 8_000);
            command.Parameters.AddWithValue("@p", arrange.ProjectId);
            command.Parameters.AddWithValue("@k", arrange.Period);
            await command.ExecuteNonQueryAsync();
        }

        await ReadsAsync(); // розігрів: оновлення статистики після масової вставки
        var readsAfter = await ReadsAsync();

        Assert.True(
            readsAfter <= readsBefore + 8,
            $"Читань при перевірці зрізів {readsBefore} -> {readsAfter} після 8000 давніх прогонів проєкту.");
    }

    private sealed record Arrange(
        TestDocumentBuilder Builder, int ProjectId, int Period, long DocumentId, int TemplateVersionId, int ReportVersionId);

    private async Task<Arrange> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];

        await using var db = builder.CreateContext();
        var def = new ReportDef(
            EcrCode.Create($"STL{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Staleness test" }),
            isRegulatory: true);
        db.ReportDefs.Add(def);
        await db.SaveChangesAsync();

        var version = new ReportVersion(def.Id, "1.0", "[]", "{}", Now);
        version.Publish();
        db.ReportVersions.Add(version);
        await db.SaveChangesAsync();

        return new Arrange(
            builder, document.ProjectId, document.PeriodKey.Value, document.DocumentId,
            document.TemplateVersionId, version.Id);
    }

    /// <summary>Поточний завершений зріз; <paramref name="submit"/> — поданий і з сумою.</summary>
    private static async Task<long> SnapshotAsync(Arrange arrange, int? periodKey, DateTime builtAt, bool submit = false)
    {
        await using var db = arrange.Builder.CreateContext();
        var snapshot = new ReportSnapshot(
            arrange.ReportVersionId, arrange.ProjectId, periodKey, SnapshotStatus.Draft, builtAt, null);
        snapshot.Complete(rowCount: 3, contentHash: [1, 2, 3], calculationRunId: null, parametersJson: null);
        snapshot.MakeCurrent();

        if (submit)
        {
            snapshot.MarkSubmitted(userId: 7);
        }

        db.ReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync();
        return snapshot.Id;
    }

    private static async Task<ReportSnapshot> RowAsync(Arrange arrange, long snapshotId)
    {
        await using var db = arrange.Builder.CreateContext();
        return await db.ReportSnapshots.AsNoTracking().SingleAsync(s => s.Id == snapshotId);
    }

    private static async Task<long> RunAsync(Arrange arrange, int periodKey, long? documentId = null)
    {
        await using var db = arrange.Builder.CreateContext();
        var run = new CalculationRun(arrange.ProjectId, periodKey, 7, Now.AddHours(-3), documentId);
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    /// <summary>Завершує прогін окремим контекстом; повертає записи журналу застарілості.</summary>
    private static async Task<List<StructureChangeRecord>> CompleteAsync(Arrange arrange, long runId, DateTime at)
    {
        await using var db = arrange.Builder.CreateContext();
        var clock = new TestClock(at);
        var audit = Substitute.For<IAuditWriter>();
        var journal = new List<StructureChangeRecord>();
        audit.WriteStructureChangeAsync(Arg.Do<StructureChangeRecord>(journal.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var handler = new RunCalculationHandler(
            Substitute.For<IPeriodStore>(),
            Substitute.For<IWorkflowStore>(),
            new CalculationResultStore(db, clock),
            Substitute.For<IBackgroundJobScheduler>(),
            new UnitOfWork(db),
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
            Substitute.For<ICurrentUser>(),
            clock,
            Substitute.For<IRecalculationApprovalStore>(),
            audit);

        await handler.CompleteAsync(runId, new ModuleProfile(), CancellationToken.None);
        return journal;
    }

    private static async Task<Dictionary<long, ReportSnapshotSummary>> ListAsync(Arrange arrange)
    {
        await using var db = arrange.Builder.CreateContext();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var builder = new ReportSnapshotBuilder(db, new TestClock(Now), memory);

        var list = await builder.ListAsync(arrange.ProjectId, periodKey: null, visibleProjectIds: null, CancellationToken.None);
        return list.ToDictionary(s => s.Id);
    }
}
