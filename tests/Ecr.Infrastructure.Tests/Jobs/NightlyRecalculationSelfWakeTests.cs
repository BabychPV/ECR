// tests/Ecr.Infrastructure.Tests/Jobs/NightlyRecalculationSelfWakeTests.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ PSS-15 (огляд M4, D-562): СПРАВЖНІЙ нічний перерахунок документа (фаза формул на справжньому SQL — записи комірок
/// <c>Recalculation</c>, дотик рядків, журнал аркуша B79 <c>SheetRecalc</c>, перемикання актуальності) не робить документ
/// «зміненим» для наступної ночі: друга ніч пропускає його.
/// </summary>
/// <remarks>
/// Документ: <c>OUT = [IN] * 2</c>, <c>IN = 5</c>, <c>OUT</c> застарілий (−1), аркуш ПОДАНИЙ — перша ніч змінює число поданого
/// аркуша, тож пише й <c>aud.CellChange</c> (<c>Recalculation</c>), і запис журналу <c>SheetRecalc</c>
/// (<c>RecalculatedAfterSubmission</c>). Годинник — 2070 (пізніше за будь-які глобальні рядки інших тестів і слоти
/// <see cref="NightlyRecalculationTests"/>): усе, що пише сам прогін, датоване ПІСЛЯ його старту.
/// <para>
/// МУТАЦІЯ: прибрати <c>SheetRecalc</c> з <c>NightlyInputChangeQuery.IgnoredStructureEntityTypes</c> чи прибрати фільтр
/// походження <c>Recalculation</c> — друга ніч будить документ, червоний.
/// </para>
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "PSS-15")]
public sealed class NightlyRecalculationSelfWakeTests(SqlServerFixture sql)
{
    private const int FormulaId = 941_500;

    private static readonly DateTime Now = new(2070, 6, 15, 0, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Справжній_нічний_прогін_не_будить_документ_наступної_ночі()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 202601, columnCount: 3, rowCount: 1);
        await ExecuteAsync(
            "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES " +
            $"(202601, {doc.RowIds[0]}, {doc.ColumnDefIds[1]}, {doc.TableDefId}, 5, 0, 0), " +
            $"(202601, {doc.RowIds[0]}, {doc.ColumnDefIds[2]}, {doc.TableDefId}, -1, 1, 0);");

        await using (var db = builder.CreateContext())
        {
            var state = new ApprovalState(doc.DocumentId, doc.SheetDefId, 202601);
            state.Submit(userId: 5, Now.AddDays(-1));
            db.ApprovalStates.Add(state);
            await db.SaveChangesAsync();
        }

        // Ніч 1: прогону ще немає — документ ставиться; дитина рахує справжньою фазою формул.
        var first = await NightlyAsync(builder, doc, Now);
        var child = Assert.Single(first.Children);
        await using (var db = builder.CreateContext())
        {
            await Job(db, doc, Now).ExecuteAsync(child, new Progress(), CancellationToken.None);
        }

        Assert.Equal(10m, await OutputAsync(doc));
        Assert.True(await CountAsync(
            $"SELECT COUNT(*) FROM aud.CellChange WHERE DocumentId = {doc.DocumentId} AND Origin = N'Recalculation'") > 0);
        Assert.True(await CountAsync(
            $"SELECT COUNT(*) FROM aud.StructureChange WHERE EntityType = N'{RecalculationSkips.AuditEntityType}' " +
            $"AND CorrelationId = N'{RecalculationSkips.AuditCorrelation(doc.DocumentId, doc.SheetDefId, 202601)}'") > 0);

        // Ніч 2: нічого, крім власних записів прогону, — пропуск.
        var second = await NightlyAsync(builder, doc, Now.AddDays(1));
        Assert.Empty(second.Children);
        Assert.Equal(1, second.Details.GetProperty("skippedUnchanged").GetInt32());
    }

    private static async Task<(IReadOnlyList<RecalculationRequest> Children, JsonElement Details)> NightlyAsync(
        TestDocumentBuilder builder, TestDocument doc, DateTime at)
    {
        var scheduler = Substitute.For<IBackgroundJobScheduler>();
        var calls = new List<RecalculationRequest>();
        scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(ci =>
            {
                calls.Add(Assert.IsType<RecalculationRequest>(ci.ArgAt<object?>(1)));
                return Task.FromResult("x");
            });

        await using (var db = builder.CreateContext())
        {
            await NightlyJobParts.Job(db, Substitute.For<ICalculationRunner>(), scheduler, at)
                .ExecuteAsync(
                    new RecalculationRequest(doc.ProjectId, 0, null, null, Year: 2026, Nightly: true, NightlySkipUnchanged: true),
                    new Progress(),
                    CancellationToken.None);
        }

        await using var verify = builder.CreateContext();
        var json = await verify.MaintenanceRuns.AsNoTracking()
            .Where(r => r.JobCode == RecalculationJob.NightlyMaintenanceCode && r.StartedAt == at
                        && r.DetailsJson!.Contains($"\"projectId\":{doc.ProjectId},"))
            .Select(r => r.DetailsJson!)
            .SingleAsync();
        using var parsed = JsonDocument.Parse(json);
        return (calls, parsed.RootElement.Clone());
    }

    /// <summary>Документна задача зі справжньою фазою формул, справжнім завершенням прогону і журналом аркушів.</summary>
    private static RecalculationJob Job(EcrDbContext db, TestDocument doc, DateTime at)
    {
        var clock = new TestClock(at);
        var runs = new RunCalculationHandler(
            Substitute.For<IPeriodStore>(),
            Substitute.For<IWorkflowStore>(),
            new CalculationResultStore(db, clock),
            Substitute.For<IBackgroundJobScheduler>(),
            new UnitOfWork(db),
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
            Substitute.For<ICurrentUser>(),
            clock);
        return new RecalculationJob(
            db, new NoBindingsRunner(), runs, Formulas(db, doc, clock), clock, audit: new AuditWriter(db));
    }

    private static RecalculationService Formulas(EcrDbContext db, TestDocument doc, TestClock clock)
    {
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(doc.DocumentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));
        periods.FindPeriodStateAsync(doc.DocumentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<PeriodState?>(PeriodState.Open));

        var versions = Substitute.For<ITemplateVersionStore>();
        versions.ListFormulaDependenciesAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(
        [
            FormulaDependency.ForFormula(
                FormulaId, dependsOnKind: 0, doc.TableDefId, rowKey: null, doc.ColumnDefIds[1],
                filterJson: null, periodOffset: null, sortOrder: 0),
        ]);

        return new RecalculationService(
            new NormalizedCellStore(db), new RowStore(db, bulk, clock), periods, Metadata(doc), versions,
            new RealFormulaEngine(), units, Substitute.For<IRegistryStore>(), headers, new AuditWriter(db), clock,
            new UnitOfWork(db), new SheetEditGate(db));
    }

    private static IMetadataCache Metadata(TestDocument doc)
    {
        var text = Column(doc, 1, "TXT", CellDataType.String);
        var input = Column(doc, 2, "IN", CellDataType.Decimal);
        var output = Column(doc, 3, "OUT", CellDataType.Decimal);

        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create($"SH{doc.SheetDefId}"), Text("Sheet"), 1);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(sheet, doc.SheetDefId);
        var table = new TableDef(doc.SheetDefId, EcrCode.Create($"TB{doc.TableDefId}"), Text("Table"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, doc.TableDefId);
        table.AddColumn(text);
        table.AddColumn(input);
        table.AddColumn(output);

        var formula = new FormulaDef(table.Id, FormulaScope.Column, "[IN] * 2", ExpressionDialect.Template);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(formula, FormulaId);
        formula.AssignColumn(output.Id);
        table.AddFormula(formula);
        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: doc.TemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef> { [text.Id] = text, [input.Id] = input, [output.Id] = output },
            RowsByKey: new Dictionary<(int, string), RowDef>());

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);
        return metadata;
    }

    private static ColumnDef Column(TestDocument doc, int ordinal, string code, CellDataType type)
    {
        var column = new ColumnDef(doc.TableDefId, EcrCode.Create(code), Text(code), ordinal, type);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(column, doc.ColumnDefIds[ordinal - 1]);
        return column;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private async Task<decimal?> OutputAsync(TestDocument doc)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT ValueNumeric FROM doc.CellValue WHERE PeriodKey = 202601 AND TableRowId = {doc.RowIds[0]} " +
            $"AND ColumnDefId = {doc.ColumnDefIds[2]}";
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : (decimal)result;
    }

    private async Task<int> CountAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAsync(string statement)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Оркестратор без прив'язок: тест про фазу формул і журнали.</summary>
    private sealed class NoBindingsRunner : ICalculationRunner
    {
        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
            => Task.FromResult(new ModuleProfile());
    }

    private sealed class Progress : IJobProgress, IJobIdentity
    {
        public string JobId { get; } = $"IRecalculationJob-selfwake-{Guid.NewGuid():N}";

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
