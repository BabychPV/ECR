// tests/Ecr.Infrastructure.Tests/Jobs/SourceImpactPerfTests.cs
using System.Diagnostics;
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ D-581 (огляд M1): ціна оцінки впливу на ~200 документах-споживачах HSE400 (один шаблон, 12 відкритих місяців 2099, ребро
/// «місяць», формула <c>[Period:-1]</c> — план кожного споживача має пізніших читачів). Вимір: звернення до SQL і час для GET
/// (з планом каскаду) і для перевірки в транзакції Approve/Reopen (лише факти політики, без плану). Стелі — храповик.
/// </summary>
/// <remarks>
/// Числа — у D-581 і у виводі тесту. МУТАЦІЯ (локально): шлях транзакції з планом (5-аргументний <c>AssessAsync</c> → план) —
/// червоний рядок «перевірка».
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Perf", "SourceImpact200")]
public sealed class SourceImpactPerfTests(SqlServerFixture sql, ITestOutputHelper output) : DbJobQueueTestsBase(sql)
{
    private const int Consumers = 200;

    /// <summary>Стеля звернень перевірки в транзакції (без плану): сталі запити + по одному на набір версій.</summary>
    private const int CheckCeiling = 60;

    /// <summary>Стеля звернень GET (з планом каскаду кожного документа-споживача).</summary>
    private const int GetCeiling = 2_000;

    private static readonly DateTime Now = new(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Оцінка_впливу_на_200_споживачів_GET_і_перевірка_в_транзакції()
    {
        var world = new SourceDependencyWorld(Sql);
        var (registryId, code) = await world.RegistryAsync();
        var source = await world.DocumentAsync([.. Enumerable.Range(1, 12)]);
        var first = await world.DocumentAsync([.. Enumerable.Range(1, 12)], code);
        var bindingId = await world.BindingAsync(source.TemplateId, registryId, DocumentSourceState.ApprovedOnly);
        await ArrangeAsync(source, first, bindingId);

        var (getTrips, getMs, get) = await MeasureAsync(source, withPlan: true);
        var (checkTrips, checkMs, check) = await MeasureAsync(source, withPlan: false);

        output.WriteLine(
            $"D-581 M1: consumers={Consumers}; GET (plan) SQL round trips={getTrips}, {getMs} ms, listed={get.Consumers.Count}, " +
            $"recalculated={get.Consumers.Count(c => c.WillRecalculate)}; check (policy only) SQL round trips={checkTrips}, {checkMs} ms, " +
            $"listed={check.Consumers.Count}; hash equal={get.Hash == check.Hash}");

        Assert.Equal(get.Hash, check.Hash);
        Assert.Equal(Consumers * 10, get.Consumers.Count(c => c.WillRecalculate)); // березень + 9 пізніших читачів [Period:-1]
        Assert.True(checkTrips <= CheckCeiling, $"check round trips {checkTrips} > {CheckCeiling}");
        Assert.True(getTrips <= GetCeiling, $"GET round trips {getTrips} > {GetCeiling}");
    }

    private async Task<(int Trips, long Ms, SourceImpact Impact)> MeasureAsync(TestDocument source, bool withPlan)
    {
        await using var db = Sql.CreateContext();
        await db.Database.OpenConnectionAsync();
        var connection = (Microsoft.Data.SqlClient.SqlConnection)db.Database.GetDbConnection();
        var graph = SourceDependencyWorld.Graph(db);
        await graph.GetAsync(CancellationToken.None); // знімок графа — кеш процесу, у вимір не входить
        var jobs = new DbBackgroundJobScheduler(
            new DbJobQueue(db, new SystemClock()), new QuartzJobScheduler(null, new JobProgressStore(db), new SystemClock()), new JobQueueSignal());
        var assessor = new SourceImpactAssessor(
            db, graph,
            new RecalculationJob(db, new NoopRunner(), RunHandler(), FormulaService(), new TestClock(Now)),
            new CalculationTrigger(jobs, new PeriodStore(db), new WorkflowStore(db), new DocumentStore(db), new DocumentSheetCopyStore(db)));
        connection.StatisticsEnabled = true;
        connection.ResetStatistics();
        var watch = Stopwatch.StartNew();
        var period = SourceDependencyWorld.Period(3);
        var impact = withPlan
            ? await assessor.AssessAsync(source.DocumentId, source.SheetDefId, period, SourceApprovalChange.Approved, null, CancellationToken.None)
            : await assessor.AssessAsync(source.DocumentId, source.SheetDefId, period, SourceApprovalChange.Approved, CancellationToken.None);
        watch.Stop();
        return ((int)(long)connection.RetrieveStatistics()["ServerRoundtrips"]!, watch.ElapsedMilliseconds, impact);
    }

    /// <summary>Таблиця складу на аркуші джерела; [Period:-1] у шаблоні споживача; ще 199 документів того самого шаблону.</summary>
    private async Task ArrangeAsync(TestDocument source, TestDocument first, int bindingId)
    {
        await using var db = Sql.CreateContext();
        await db.DocumentSourceBindings.Where(b => b.Id == bindingId)
            .ExecuteUpdateAsync(b => b.SetProperty(x => x.Policy, DocumentSourcePolicy.Warn));
        var stream = new TableDef(
            source.SheetDefId, EcrCode.Create("FG_Internal"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "FG_Internal" }), 50,
            TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        db.TableDefs.Add(stream);
        await db.SaveChangesAsync();
        var loader = new BulkCellLoader(Sql.ConnectionString, 1000);
        db.TableInstances.Add(new TableInstance(
            new PeriodKey(SourceDependencyWorld.Period(3)), await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None),
            source.DocumentId, stream.Id, Now));

        await db.ColumnDefs.Where(c => c.Id == first.ColumnDefIds[1])
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.DataType, CellDataType.Formula));
        var formula = new FormulaDef(first.TableDefId, FormulaScope.Column, "1", ExpressionDialect.Template);
        formula.AssignColumn(first.ColumnDefIds[2]);
        db.FormulaDefs.Add(formula);
        await db.SaveChangesAsync();
        db.FormulaDependencies.Add(FormulaDependency.ForFormula(formula.Id, 0, first.TableDefId, "R1", first.ColumnDefIds[1], null, -1, 0));
        await db.SaveChangesAsync();

        var policyId = await db.PeriodPolicies.Select(p => p.Id).FirstAsync();
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        for (var i = 1; i < Consumers; i++)
        {
            var project = new Project(
                EcrCode.Create($"SIPC{tag}{i}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "c" }),
                new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), first.TemplateVersionId, PeriodKind.Monthly, policyId,
                "Asia/Atyrau");
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            var document = new Document(project.Id, $"SIPD{tag}{i}", 1, Now);
            db.Documents.Add(document);
            for (var m = 1; m <= 12; m++)
            {
                var period = new Period(
                    project.Id, new PeriodKey(SourceDependencyWorld.Period(m)), (byte)m,
                    new DateOnly(2099, m, 1), new DateOnly(2099, m, DateTime.DaysInMonth(2099, m)));
                period.AdvanceTo(PeriodState.Open, Now);
                db.Periods.Add(period);
            }

            await db.SaveChangesAsync();
            var firstId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 12, CancellationToken.None);
            for (var m = 1; m <= 12; m++)
            {
                db.TableInstances.Add(new TableInstance(
                    new PeriodKey(SourceDependencyWorld.Period(m)), firstId + m - 1, document.Id, first.TableDefId, Now));
            }

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
    }

    private static RecalculationService FormulaService()
    {
        var rows = Substitute.For<IRowStore>();
        rows.GetTableInstancesAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableInstanceRef>>([]));
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);
        var header = Substitute.For<IDocumentHeaderStore>();
        header.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());
        return new(
            Substitute.For<ICellStore>(), rows, Substitute.For<IPeriodStore>(), Substitute.For<IMetadataCache>(),
            Substitute.For<ITemplateVersionStore>(), Substitute.For<IFormulaEngine>(), units, Substitute.For<IRegistryStore>(),
            header, Substitute.For<IAuditWriter>(), new TestClock(Now), Substitute.For<IUnitOfWork>(), Substitute.For<ISheetEditGate>());
    }

    private static RunCalculationHandler RunHandler()
        => new(
            Substitute.For<IPeriodStore>(), Substitute.For<IWorkflowStore>(), Substitute.For<ICalculationResultStore>(),
            Substitute.For<IBackgroundJobScheduler>(), Substitute.For<IUnitOfWork>(),
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(), Substitute.For<ICurrentUser>(), new TestClock(Now));

    private sealed class NoopRunner : ICalculationRunner
    {
        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
            => Task.FromResult(new ModuleProfile());
    }
}
