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
        Assert.Contains($"\"{w.World.Source.DocumentId}:{March}\"", recorded!, StringComparison.Ordinal);
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

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
