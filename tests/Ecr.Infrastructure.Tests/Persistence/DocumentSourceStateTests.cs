// tests/Ecr.Infrastructure.Tests/Persistence/DocumentSourceStateTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Tests.Jobs;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ P-1123-20 (рішення людини «Зараз, у цьому оновленні»): «джерело змінено після затвердження» (F5-R) і за змінами
/// джерела БЕЗ <c>aud.CellChange</c> — видалення рядка людиною (м'яке) і синхронізацією подій (тверде), новий актуальний
/// прогін джерела з іншими <c>F_&lt;X&gt;</c>. Прогін споживача записує стан джерел, який бачив
/// (<c>CalculationRun.SourceStateJson</c>); позначка — коли стан зараз інший. Порожній перерахунок джерела (ті самі числа)
/// позначки не дає.
/// </summary>
/// <remarks>
/// МУТАЦІЇ (локально, scratchpad\m11): (1) <c>SourceStateChangedAsync</c> → <c>false</c> — червоні три види змін;
/// (2) відбиток без кількості й суми рядків — червоні «видалення»; (3) хеш за Id прогону замість значень — червоний «порожній
/// перерахунок»; (4) прогін не пише стан — червоний «прогін записує».
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Decision", "P-1123-20")]
public sealed class DocumentSourceStateTests(SqlServerFixture sql)
{
    private const int March = SourceIssueWorld.March;
    private static readonly DateTime Approved = new(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("user-row-delete", true)]
    [InlineData("event-row-delete", true)]
    [InlineData("source-run-new-results", true)]
    [InlineData("source-run-same-results", false)]
    [InlineData("none", false)]
    public async Task Зміна_джерела_без_журналу_комірок_позначає_затверджений_період(string change, bool expected)
    {
        var w = await ArrangeAsync();
        Assert.Empty(await ListAsync(w));

        await using (var db = sql.CreateContext())
        {
            switch (change)
            {
                case "user-row-delete":
                    var row = await db.TableRows.SingleAsync(r => r.Id == w.RowIds[0]);
                    row.SoftDelete(Approved.AddHours(1));
                    await db.SaveChangesAsync();
                    break;
                case "event-row-delete":
                    await db.Database.ExecuteSqlAsync($"DELETE FROM doc.TableRow WHERE Id = {w.RowIds[1]} AND PeriodKey = {March}");
                    break;
                case "source-run-new-results":
                    await SourceRunAsync(w, 2.5m);
                    break;
                case "source-run-same-results":
                    await SourceRunAsync(w, 1m);
                    break;
            }
        }

        var issues = await ListAsync(w);
        Assert.Equal(expected, issues.Count == 1);
        if (expected)
        {
            Assert.Equal(ISourceIssueReader.SourceChangedAfterApproval, issues[0].Kind);
        }
    }

    /// <summary>Прогін споживача (задача перерахунку документа) записує стан джерел, який бачив на старті.</summary>
    [Fact]
    public async Task Прогін_споживача_записує_стан_джерел()
    {
        var w = await ArrangeAsync(recordRun: false);
        await using (var db = sql.CreateContext())
        {
            await NightlyJobParts.Job(db, new NoopRunner(), Substitute.For<IBackgroundJobScheduler>(), Approved.AddDays(1))
                .ExecuteAsync(
                    new RecalculationRequest(w.World.Land.ProjectId, w.World.Land.DocumentId, March, TriggeredByUserId: null),
                    Substitute.For<IJobProgress>(),
                    CancellationToken.None);
        }

        await using var check = sql.CreateContext();
        var recorded = await check.CalculationRuns.AsNoTracking()
            .Where(r => r.DocumentId == w.World.Land.DocumentId && r.PeriodKey == March)
            .Select(r => r.SourceStateJson)
            .SingleAsync();
        Assert.Equal(await StatesAsync(check, w), recorded);
        Assert.Contains($"\"{w.World.BindingId}:{w.World.Source.DocumentId}:{March}\"", recorded!, StringComparison.Ordinal);
    }

    /// <summary>
    /// ✎ Огляд b11 №5 (HIGH, MEDIUM b): невідомий стан ніколи не гасить позначки — прогін до міграції (<c>NULL</c>, рішення
    /// людини P-1123-21: «змінено»), явне «невідомо» (збій знімка, без графа), документного прогону немає, а чинний прогін
    /// усього проєкту (<c>DocumentId = NULL</c>) новіший за документний.
    /// </summary>
    /// <remarks>Мутації: <c>LegacyStateMeansChanged</c> → <c>false</c> — червоний «legacy-null»; «невідомо» як відомий — червоні решта.</remarks>
    [Theory]
    [InlineData("legacy-null")]
    [InlineData("unknown")]
    [InlineData("no-document-run")]
    [InlineData("project-run-newer")]
    public async Task Невідомий_стан_прогону_споживача_позначає(string kind)
    {
        var w = await ArrangeAsync();
        Assert.Empty(await ListAsync(w));

        await using (var db = sql.CreateContext())
        {
            var land = w.World.Land;
            switch (kind)
            {
                case "legacy-null":
                    await db.Database.ExecuteSqlAsync($"UPDATE calc.CalculationRun SET SourceStateJson = NULL WHERE DocumentId = {land.DocumentId} AND Status = N'Current'");
                    break;
                case "unknown":
                    await db.Database.ExecuteSqlAsync($"UPDATE calc.CalculationRun SET SourceStateJson = {DocumentSourceIssues.UnknownState} WHERE DocumentId = {land.DocumentId} AND Status = N'Current'");
                    break;
                case "no-document-run":
                case "project-run-newer":
                    if (kind == "no-document-run")
                    {
                        await db.Database.ExecuteSqlAsync($"UPDATE calc.CalculationRun SET Status = N'Superseded' WHERE DocumentId = {land.DocumentId} AND Status = N'Current'");
                    }

                    var project = new Ecr.Domain.Entities.Calculations.CalculationRun(land.ProjectId, March, 5, Approved.AddHours(-1));
                    db.CalculationRuns.Add(project);
                    await db.SaveChangesAsync();
                    project.Complete(Ecr.Domain.Entities.Calculations.CalculationRun.CurrentStatus, Approved.AddHours(-1), null, null);
                    await db.SaveChangesAsync();
                    break;
            }
        }

        var issue = Assert.Single(await ListAsync(w));
        Assert.Equal(ISourceIssueReader.SourceChangedAfterApproval, issue.Kind);
    }

    /// <summary>
    /// ✎ Огляд b11 №5 (LOW 1): джерело, яке читав чинний прогін споживача (ключ у стані), видалено — обраного документа за рік
    /// більше немає: позначка (числа змінилися), а не «джерела нема — проблем нема». Контроль — джерела не було й у стані.
    /// </summary>
    /// <remarks>
    /// ✎ Огляд b11 №5 (залишок): і стан прогону до міграції (P-1123-21) чи невідомий — позначка, хоч джерела зараз немає (той
    /// прогін міг читати видалене джерело). Мутації: <c>VanishedSourceAsync</c> → <c>null</c> — червоний «known»; невідомий /
    /// до міграції → <c>null</c> — червоні «legacy», «unknown».
    /// </remarks>
    [Theory]
    [InlineData("known", true)]
    [InlineData("none-recorded", false)]
    [InlineData("legacy", true)]
    [InlineData("unknown", true)]
    public async Task Видалене_джерело_яке_читав_прогін_позначає(string recorded, bool expected)
    {
        var w = await ArrangeAsync();
        var hadSource = recorded == "known";
        await using (var db = sql.CreateContext())
        {
            var land = w.World.Land.DocumentId;
            switch (recorded)
            {
                case "none-recorded":
                    await db.Database.ExecuteSqlAsync($"UPDATE calc.CalculationRun SET SourceStateJson = N'{{}}' WHERE DocumentId = {land} AND Status = N'Current'");
                    break;
                case "legacy":
                    await db.Database.ExecuteSqlAsync($"UPDATE calc.CalculationRun SET SourceStateJson = NULL WHERE DocumentId = {land} AND Status = N'Current'");
                    break;
                case "unknown":
                    await db.Database.ExecuteSqlAsync($"UPDATE calc.CalculationRun SET SourceStateJson = {DocumentSourceIssues.UnknownState} WHERE DocumentId = {land} AND Status = N'Current'");
                    break;
            }

            await db.Database.ExecuteSqlAsync($"DELETE FROM doc.TableRow WHERE TableInstanceId = {w.InstanceId} AND PeriodKey = {March}");
            await db.Database.ExecuteSqlAsync($"DELETE FROM doc.TableInstance WHERE Id = {w.InstanceId} AND PeriodKey = {March}");
        }

        var issues = await ListAsync(w);
        Assert.Equal(expected, issues.Count == 1);
        if (hadSource)
        {
            Assert.Equal(w.World.Source.DocumentId, issues[0].SourceDocumentId);
        }
    }

    /// <summary>
    /// ✎ Огляд b11 №5 (MEDIUM a, c): звичайний перерахунок затвердженого періоду (оригінал затверджено, копія-чернетка
    /// перераховується) не переписує затверджених чисел — лишає стан ПОПЕРЕДНЬОГО прогону, і видалення рядка джерела між
    /// прогонами позначається. Прогін «з затвердженими» (IncludeApproved) перераховує й затверджене — пише ВЛАСНИЙ стан
    /// (оркестратор викликано за період, без перенесення), і позначки немає.
    /// </summary>
    /// <remarks>Мутації: неповний період бере новий стан — червоний «regular»; IncludeApproved неповний — червоний «include-approved».</remarks>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Перерахунок_затвердженого_лишає_стан_попереднього_а_з_затвердженими_пише_свій(bool includeApproved, bool expected)
    {
        var w = await ArrangeAsync(recordRun: false);
        var runner = new RecordingRunner();
        await JobAsync(w, runner, includeApproved: false, Approved.AddDays(-1));
        var first = await RecordedAsync(w);

        await SourceIssueWorld.AddStateAsync(sql, w.World.Land.DocumentId, w.World.Land.SheetDefId, March, Approved.AddHours(-1), Approved);
        await using (var db = sql.CreateContext())
        {
            // Копія аркуша Land — чернетка з власним екземпляром: звичайний прогін періоду не відмовляє (F13 p218).
            if (!includeApproved)
            {
                await AddDraftCopyAsync(db, w);
            }

            var row = await db.TableRows.SingleAsync(r => r.Id == w.RowIds[0]);
            row.SoftDelete(Approved.AddHours(1));
            await db.SaveChangesAsync();
        }

        runner.Periods.Clear();
        await JobAsync(w, runner, includeApproved, Approved.AddDays(1));

        Assert.Contains(March, runner.Periods);
        var second = await RecordedAsync(w);
        if (includeApproved)
        {
            await using var check = sql.CreateContext();
            Assert.Equal(await StatesAsync(check, w), second);
            Assert.NotEqual(first, second);
            Assert.Null(await check.CalculationRuns.AsNoTracking()
                .Where(r => r.DocumentId == w.World.Land.DocumentId && r.PeriodKey == March && r.Status == Ecr.Domain.Entities.Calculations.CalculationRun.CurrentStatus)
                .Select(r => r.InputsAsOfUtc).SingleAsync());
        }
        else
        {
            Assert.Equal(first, second);
        }

        Assert.Equal(expected, (await ListAsync(w)).Count == 1);
    }

    /// <summary>
    /// ✎ Огляд b11 №5 (HIGH): знімок стану не вдався (тут — без графа залежностей) — прогін пише явне «невідомо», а не NULL,
    /// і позначка стоїть.
    /// </summary>
    /// <remarks>Мутація: без графа — <c>"{}"</c> замість «невідомо» — червоний.</remarks>
    [Fact]
    public async Task Збій_знімка_стану_пише_невідомо_і_позначає()
    {
        var w = await ArrangeAsync(recordRun: false);
        await SourceIssueWorld.AddStateAsync(sql, w.World.Land.DocumentId, w.World.Land.SheetDefId, March, Approved.AddHours(-1), Approved);
        await using (var db = sql.CreateContext())
        {
            await AddDraftCopyAsync(db, w);
            await NightlyJobParts.Job(db, new RecordingRunner(), Substitute.For<IBackgroundJobScheduler>(), Approved.AddDays(1), withGraph: false)
                .ExecuteAsync(
                    new RecalculationRequest(w.World.Land.ProjectId, w.World.Land.DocumentId, March, TriggeredByUserId: null, IncludeApproved: true),
                    Substitute.For<IJobProgress>(),
                    CancellationToken.None);
        }

        Assert.Equal(DocumentSourceIssues.UnknownState, await RecordedAsync(w));
        Assert.Single(await ListAsync(w));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private async Task JobAsync(StateWorld w, ICalculationRunner runner, bool includeApproved, DateTime at)
    {
        await using var db = sql.CreateContext();
        await NightlyJobParts.Job(db, runner, Substitute.For<IBackgroundJobScheduler>(), at)
            .ExecuteAsync(
                new RecalculationRequest(
                    w.World.Land.ProjectId, w.World.Land.DocumentId, March, TriggeredByUserId: null, IncludeApproved: includeApproved),
                Substitute.For<IJobProgress>(),
                CancellationToken.None);
    }

    private async Task<string?> RecordedAsync(StateWorld w)
    {
        await using var db = sql.CreateContext();
        return await db.CalculationRuns.AsNoTracking()
            .Where(r => r.DocumentId == w.World.Land.DocumentId && r.PeriodKey == March
                        && r.Status == Ecr.Domain.Entities.Calculations.CalculationRun.CurrentStatus)
            .Select(r => r.SourceStateJson)
            .SingleAsync();
    }

    private async Task AddDraftCopyAsync(EcrDbContext db, StateWorld w)
    {
        var land = w.World.Land;
        if (!await db.DocumentSheets.AnyAsync(s => s.DocumentId == land.DocumentId && s.CopyOfId == null))
        {
            db.DocumentSheets.Add(new DocumentSheet(land.DocumentId, land.SheetDefId));
            await db.SaveChangesAsync();
        }

        var copy = (await new DocumentSheetCopyStore(db).AddCopyAsync(land.DocumentId, land.SheetDefId, default)).SheetCopyId;
        var instanceId = await new BulkCellLoader(sql.ConnectionString, 1000).ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        if (!await db.TableInstances.AnyAsync(i => i.DocumentId == land.DocumentId && i.SheetCopyId == copy))
        {
            db.TableInstances.Add(new TableInstance(new PeriodKey(March), instanceId, land.DocumentId, land.TableDefId, Approved.AddDays(-2), copy));
            await db.SaveChangesAsync();
        }
    }

    /// <summary>Оркестратор-свідок: періоди, за якими справді рахувалися методології.</summary>
    private sealed class RecordingRunner : ICalculationRunner
    {
        public List<int> Periods { get; } = [];

        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
        {
            lock (Periods)
            {
                Periods.Add(periodKey.Value);
            }

            return Task.FromResult(new ModuleProfile());
        }
    }

    private sealed record StateWorld(SourceIssueWorld.World World, long InstanceId, long[] RowIds);

    private async Task<StateWorld> ArrangeAsync(bool recordRun = true)
    {
        var world = await SourceIssueWorld.ArrangeAsync(sql, DocumentSourceState.Any);
        await using var db = sql.CreateContext();
        // Екземпляр потоку FG_Internal — створений світом ПІСЛЯ екземпляра будівника (найбільший Id документа-джерела).
        var instance = await db.TableInstances.AsNoTracking()
            .Where(i => i.DocumentId == world.Source.DocumentId)
            .OrderByDescending(i => i.Id)
            .Select(i => i.Id)
            .FirstAsync();
        var first = await new BulkCellLoader(sql.ConnectionString, 1000).ReserveIdsAsync("doc.TableRowSeq", 2, CancellationToken.None);
        db.TableRows.Add(new TableRow(new PeriodKey(March), first, instance, RowKey.Create("CH4"), 1, Approved.AddDays(-2)));
        db.TableRows.Add(new TableRow(new PeriodKey(March), first + 1, instance, RowKey.Create("F_CH4"), 2, Approved.AddDays(-2)));
        await db.SaveChangesAsync();

        var w = new StateWorld(world, instance, [first, first + 1]);
        await SourceRunAsync(w, 1m);
        if (recordRun)
        {
            await SourceIssueWorld.AddStateAsync(sql, world.Land.DocumentId, world.Land.SheetDefId, March, Approved.AddHours(-1), Approved);

            // Чинний прогін споживача, що бачив поточний стан джерела.
            var run = new Ecr.Domain.Entities.Calculations.CalculationRun(world.Land.ProjectId, March, 5, Approved.AddHours(-2), world.Land.DocumentId);
            db.CalculationRuns.Add(run);
            await db.SaveChangesAsync();
            run.Complete(Ecr.Domain.Entities.Calculations.CalculationRun.CurrentStatus, Approved.AddHours(-2), null, null);
            await db.SaveChangesAsync();
            var states = await StatesAsync(db, w);
            await db.Database.ExecuteSqlAsync($"UPDATE calc.CalculationRun SET SourceStateJson = {states} WHERE Id = {run.Id}");
        }

        return w;
    }

    /// <summary>Новий актуальний прогін джерела за березень з одним F_CH4 (попередній — витіснено).</summary>
    private async Task SourceRunAsync(StateWorld w, decimal value)
    {
        await using var db = sql.CreateContext();
        await db.Database.ExecuteSqlAsync(
            $"UPDATE calc.CalculationRun SET Status = N'Superseded' WHERE DocumentId = {w.World.Source.DocumentId} AND PeriodKey = {March} AND Status = N'Current'");
        var run = new Ecr.Domain.Entities.Calculations.CalculationRun(w.World.Source.ProjectId, March, 5, Approved, w.World.Source.DocumentId);
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync();
        run.Complete(Ecr.Domain.Entities.Calculations.CalculationRun.CurrentStatus, Approved, null, null);
        await db.SaveChangesAsync();

        var versionId = await db.MethodologyVersions.AsNoTracking().OrderByDescending(v => v.Id).Select(v => (int?)v.Id).FirstOrDefaultAsync();
        if (versionId is null)
        {
            var methodology = new Ecr.Domain.Entities.Calculations.Methodology(
                EcrCode.Create($"SS_{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync();
            var version = new Ecr.Domain.Entities.Calculations.MethodologyVersion(
                methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Approved);
            db.MethodologyVersions.Add(version);
            await db.SaveChangesAsync();
            versionId = version.Id;
        }
        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO calc.CalculationResult
                (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId, TableInstanceId)
            VALUES (NEXT VALUE FOR calc.CalculationResultSeq, {run.Id}, {versionId}, {March}, {w.World.Source.DocumentId},
                    N'F_CH4', N'M3', {value}, {unitId}, {w.InstanceId})
            """);
    }

    private static Task<string> StatesAsync(EcrDbContext db, StateWorld w)
        => new DocumentSourceIssues(db, readOverride: null, DependencyGraphTestKit.Provider(db))
            .SourceStatesJsonAsync(w.World.Land.DocumentId, [March / 100], CancellationToken.None);

    private async Task<IReadOnlyList<SourceIssueRow>> ListAsync(StateWorld w)
    {
        await using var db = sql.CreateContext();
        return await new DocumentSourceMonthGate(db, readOverride: null, DependencyGraphTestKit.Provider(db))
            .ListSourceIssuesAsync(w.World.Land.DocumentId, [March], CancellationToken.None);
    }

    /// <summary>Фейк оркестратора: нічого не пише.</summary>
    private sealed class NoopRunner : ICalculationRunner
    {
        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
            => Task.FromResult(new ModuleProfile());
    }
}
