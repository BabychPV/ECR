// tests/Ecr.Infrastructure.Tests/Jobs/YearLayoutTailSqlTests.cs
using System.Globalization;
using Ecr.Application.Calculations;
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
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// LAND-YEAR, хвіст після B24 (пачка PE-7): B58 — fan-out до закритого якоря; B59 — «увесь рік» кнопки річного
/// документа звужений до свого року; B60 — стан аркуша відкритого якоря для не-місячних цілей і «пізня» правка прогону
/// закритого якоря.
/// </summary>
/// <remarks>
/// <para>
/// Світ — той самий, що в <see cref="YearLayoutCalculatedRoutingSqlTests"/> (таблиця «місяці в колонках»: <c>IN</c> —
/// не-місячний введений, <c>M1..M12</c> — введені місячні, <c>CM1..CM12</c> = <c>[Mm] * [IN]</c>, <c>TOT</c> = <c>[CM9]</c>
/// у якорі), але рік — <c>2099</c> (тестові періоди далеко від реальних) і розкладка версії — <c>YearWithMonthColumns</c>.
/// </para>
/// <para>
/// ⛔ Червоне без фіксу (локально, відкотом СВОГО фіксу, не в коміті): B58 — у <c>CrossPeriodFanOut.FindReadersAsync</c>
/// знову пропускати будь-який <c>Closed</c> — підсумок закритого якоря лишається 90; B59 — прибрати межі
/// <c>FromKey/ToKey</c> у <c>RecalculationJob</c> — перераховується й 2098; B60 — без <c>openAnchor</c> в
/// <c>EnterSheetsAsync</c> поданий січень блокує підсумок; без <c>YearStatesAsync</c> — <c>IsLateEdit = 0</c>.
/// Контролі зелені в обох станах.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed partial class YearLayoutTailSqlTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Мить задачі; періоди 2099 у майбутньому, тож ефективний стан не просувається датами.</summary>
    private static readonly DateTime JobNow = Now;

    private const int Year = 2099;
    private const int Anchor = 1;
    private const int PriorYearKey = 209812;
    private const int TotalFormulaId = 930_100;
    private const int MonthFormulaBase = 930_000;
    private const decimal Input = 10m;

    // ── B59: «увесь рік» звужений до свого року ──────────────────────────

    /// <summary>
    /// B59: задача «увесь рік» з <c>Year = 2099</c> рахує місяці 2099 (CM4 = 4 × IN) і НЕ чіпає екземпляр іншого року
    /// документа (209812: застаріле значення лишається).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    public async Task B59_річна_задача_рахує_місяці_свого_року_і_не_чіпає_інший_рік()
    {
        var world = await ArrangeAsync(priorYear: true);
        try
        {
            await SeedInputsAsync(world);
            await SetCellAsync(world, 4, world.CalcMonth[4], -4m, calculated: true);

            // Інший рік документа (209812 — сам собі якір): входи є, тож перерахунок дав би CM12 = 12 × 10, а не −12.
            var prior = world.PriorYear!.RowIds[0];
            await SetCellAtAsync(PriorYearKey, prior, world.Doc.TableDefId, world.Doc.ColumnDefIds[1], Input, calculated: false);
            await SetCellAtAsync(PriorYearKey, prior, world.Doc.TableDefId, world.Doc.MonthColumnDefIds[11], 12m, calculated: false);
            await SetCellAtAsync(PriorYearKey, prior, world.Doc.TableDefId, world.CalcMonth[12], -12m, calculated: true);

            await RunJobAsync(world, periodKey: null, year: Year, States(closed: []));

            Assert.Equal(4 * Input, await PhysicalAsync(world, 4, world.CalcMonth[4]));
            Assert.Equal(-12m, await PhysicalAtAsync(PriorYearKey, prior, world.CalcMonth[12]));
        }
        finally
        {
            await CleanupAsync(world);
        }
    }

    // ── сценарії ─────────────────────────────────────────────────────────

    private async Task RunJobAsync(World world, int? periodKey, int? year, Dictionary<int, PeriodState> states)
    {
        await using var db = world.Builder.CreateContext();
        var router = new MonthColumnRouter(db, Flag(false), YearRegistry(world));
        var store = new MonthRoutedCellStore(new NormalizedCellStore(db), router, world.Metadata, db);
        var job = new RecalculationJob(
            db, new NoBindingsRunner(), RunHandler(), Formulas(db, world, store, router, Periods(world, states)),
            new TestClock(JobNow));

        await job.ExecuteAsync(
            new RecalculationRequest(world.Doc.ProjectId, world.Doc.DocumentId, periodKey, TriggeredByUserId: null, Year: year),
            new CapturingJobProgress(),
            CancellationToken.None);
    }

    // ── допоміжне ────────────────────────────────────────────────────────

    private static int Period(int month) => (Year * 100) + month;

    private static PeriodKey Key(int month) => new(Period(month));

    /// <summary>Стани місяців 2099 для порту періодів: названі — Closed, решта Open.</summary>
    private static Dictionary<int, PeriodState> States(IReadOnlyCollection<int> closed)
        => Enumerable.Range(1, 12).ToDictionary(Period, m => closed.Contains(m) ? PeriodState.Closed : PeriodState.Open);

    private static IConfiguration Flag(bool routing)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Collection:MonthColumnRouting"] = routing ? "true" : "false" })
            .Build();

    private static ITemplateLayoutRegistry YearRegistry(World world)
    {
        var registry = Substitute.For<ITemplateLayoutRegistry>();
        registry.HasAny.Returns(true);
        registry.IsYearVersion(world.Doc.TemplateVersionId).Returns(true);
        registry.IsYearSheet(world.Doc.SheetDefId).Returns(true);
        return registry;
    }

    private static RunCalculationHandler RunHandler()
        => new(
            Substitute.For<IPeriodStore>(),
            Substitute.For<IWorkflowStore>(),
            Substitute.For<ICalculationResultStore>(),
            Substitute.For<IBackgroundJobScheduler>(),
            Substitute.For<IUnitOfWork>(),
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
            Substitute.For<Ecr.Application.Common.ICurrentUser>(),
            new TestClock(JobNow),
            Substitute.For<IAuditWriter>());

    /// <summary>Оркестратор без прив'язок: тест про фазу формул і скоуп періодів.</summary>
    private sealed class NoBindingsRunner : ICalculationRunner
    {
        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
            => Task.FromResult(new ModuleProfile());
    }

    private async Task<World> ArrangeAsync(bool priorYear = false)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            periodKey: Period(Anchor), columnCount: 2, rowCount: 1,
            layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12);

        var calcMonth = new Dictionary<int, int>();
        int total;
        await using (var setup = builder.CreateContext())
        {
            var columns = new List<ColumnDef>();
            for (var m = 1; m <= 12; m++)
            {
                var column = new ColumnDef(
                    doc.TableDefId, EcrCode.Create($"CM{m}_{doc.TableDefId}"), Text($"CM{m}"), 100 + m, CellDataType.Formula);
                column.SetMonth(true, (byte)m);
                columns.Add(column);
            }

            var totalColumn = new ColumnDef(
                doc.TableDefId, EcrCode.Create($"TOT_{doc.TableDefId}"), Text("TOT"), 200, CellDataType.Formula);
            columns.Add(totalColumn);
            setup.ColumnDefs.AddRange(columns);
            await setup.SaveChangesAsync();

            for (var m = 1; m <= 12; m++)
            {
                calcMonth[m] = columns[m - 1].Id;
            }

            total = totalColumn.Id;
        }

        var others = new Dictionary<int, TestPeriodInstance>();
        for (var m = 2; m <= 12; m++)
        {
            others[m] = await builder.AddPeriodInstanceAsync(doc, Key(m));
        }

        var prior = priorYear ? await builder.AddPeriodInstanceAsync(doc, new PeriodKey(PriorYearKey)) : null;

        await using (var setup = builder.CreateContext())
        {
            foreach (var period in await setup.Periods.Where(p => p.ProjectId == doc.ProjectId).ToListAsync())
            {
                period.AdvanceTo(PeriodState.Open, DateTime.UtcNow);
            }

            await setup.SaveChangesAsync();
        }

        await ExecuteAsync(
            $"UPDATE cfg.TemplateVersion SET Layout = {(int)TemplateLayout.YearWithMonthColumns} WHERE Id = {doc.TemplateVersionId};");

        var world = new World(builder, doc, others, prior, calcMonth, total, null!);
        return world with { Metadata = Metadata(world) };
    }

    /// <summary>Введені значення у власниках: IN = 10 у якорі, Mm = m у періоді m.</summary>
    private async Task SeedInputsAsync(World world)
    {
        await SetCellAsync(world, Anchor, world.Doc.ColumnDefIds[1], Input);
        for (var m = 1; m <= 12; m++)
        {
            await SetCellAsync(world, m, world.Doc.MonthColumnDefIds[m - 1], m);
        }
    }

    private long RowOf(World world, int month)
        => month == Anchor ? world.Doc.RowIds[0] : world.Others[month].RowIds[0];

    private Task SetCellAsync(World world, int month, int columnDefId, decimal value, bool calculated = false)
        => SetCellAtAsync(Period(month), RowOf(world, month), world.Doc.TableDefId, columnDefId, value, calculated);

    private async Task SetCellAtAsync(int period, long rowId, int tableDefId, int columnDefId, decimal value, bool calculated)
        => await ExecuteAsync(
            $"DELETE FROM doc.CellValue WHERE PeriodKey = {period} AND TableRowId = {rowId} AND ColumnDefId = {columnDefId}; " +
            "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES " +
            $"({period}, {rowId}, {columnDefId}, {tableDefId}, " +
            $"{value.ToString(CultureInfo.InvariantCulture)}, {(calculated ? 1 : 0)}, 0);");

    private static RecalculationService Formulas(
        EcrDbContext db, World world, ICellStore store, IMonthColumnRouter router, IPeriodStore periods)
    {
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var clock = new TestClock(Now);

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        return new RecalculationService(
            store, new RowStore(db, bulk, clock), periods, world.Metadata,
            Versions(world), new RealFormulaEngine(), units, Substitute.For<IRegistryStore>(), headers,
            new AuditWriter(db), clock, new UnitOfWork(db), new SheetEditGate(db), monthRouter: router);
    }

    /// <summary>Порт періодів: ефективні стани — зі словника (за замовчуванням Open), межі — січень.</summary>
    private static IPeriodStore Periods(World world, Dictionary<int, PeriodState> states)
    {
        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(world.Doc.DocumentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(Year, 1, 1), new DateOnly(Year, 1, 31)));
        periods.FindPeriodStateAsync(world.Doc.DocumentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(c => Task.FromResult<PeriodState?>(states.GetValueOrDefault(c.ArgAt<int>(1), PeriodState.Open)));
        return periods;
    }

    private static ITemplateVersionStore Versions(World world)
    {
        var dependencies = new List<FormulaDependency>();
        var order = 0;
        for (var m = 1; m <= 12; m++)
        {
            var formulaId = MonthFormulaBase + m;
            dependencies.Add(FormulaDependency.ForFormula(
                formulaId, dependsOnKind: 0, world.Doc.TableDefId, rowKey: null, world.Doc.MonthColumnDefIds[m - 1],
                filterJson: null, periodOffset: null, sortOrder: order++));
            dependencies.Add(FormulaDependency.ForFormula(
                formulaId, dependsOnKind: 0, world.Doc.TableDefId, rowKey: null, world.Doc.ColumnDefIds[1],
                filterJson: null, periodOffset: null, sortOrder: order++));
        }

        dependencies.Add(FormulaDependency.ForFormula(
            TotalFormulaId, dependsOnKind: 0, world.Doc.TableDefId, rowKey: null, world.CalcMonth[9],
            filterJson: null, periodOffset: null, sortOrder: order));

        var versions = Substitute.For<ITemplateVersionStore>();
        versions.ListFormulaDependenciesAsync(world.Doc.TemplateVersionId, Arg.Any<CancellationToken>())
            .Returns(dependencies);
        return versions;
    }

    private static IMetadataCache Metadata(World world)
    {
        var doc = world.Doc;
        var text = ColumnDefFor(doc, doc.ColumnDefIds[0], "C1", 1, CellDataType.String);
        var input = ColumnDefFor(doc, doc.ColumnDefIds[1], "IN", 2, CellDataType.Decimal);
        var total = ColumnDefFor(doc, world.Total, "TOT", 200, CellDataType.Formula);

        var columns = new List<ColumnDef> { text, input };
        for (var m = 1; m <= 12; m++)
        {
            var month = ColumnDefFor(doc, doc.MonthColumnDefIds[m - 1], $"M{m}", 2 + m, CellDataType.Decimal);
            month.SetMonth(true, (byte)m);
            columns.Add(month);
        }

        for (var m = 1; m <= 12; m++)
        {
            var calc = ColumnDefFor(doc, world.CalcMonth[m], $"CM{m}", 100 + m, CellDataType.Formula);
            calc.SetMonth(true, (byte)m);
            columns.Add(calc);
        }

        columns.Add(total);

        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create($"SH{doc.SheetDefId}"), Text("Sheet"), 1);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(sheet, doc.SheetDefId);
        var table = new TableDef(doc.SheetDefId, EcrCode.Create($"TB{doc.TableDefId}"), Text("Table"), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Dynamic);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, doc.TableDefId);
        foreach (var column in columns)
        {
            table.AddColumn(column);
        }

        for (var m = 1; m <= 12; m++)
        {
            AddFormula(table, MonthFormulaBase + m, world.CalcMonth[m], $"[M{m}] * [IN]");
        }

        AddFormula(table, TotalFormulaId, world.Total, "[CM9]");
        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: doc.TemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: columns.ToDictionary(c => c.Id), RowsByKey: new Dictionary<(int, string), RowDef>());

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);
        return metadata;
    }

    private static void AddFormula(TableDef table, int id, int columnDefId, string expression)
    {
        var formula = new FormulaDef(table.Id, FormulaScope.Column, expression, ExpressionDialect.Template);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(formula, id);
        formula.AssignColumn(columnDefId);
        table.AddFormula(formula);
    }

    private static ColumnDef ColumnDefFor(TestDocument doc, int id, string code, int ordinal, CellDataType type)
    {
        var column = new ColumnDef(doc.TableDefId, EcrCode.Create(code), Text(code), ordinal, type);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(column, id);
        return column;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private Task<decimal?> PhysicalAsync(World world, int month, int columnDefId)
        => PhysicalAtAsync(Period(month), RowOf(world, month), columnDefId);

    private async Task<decimal?> PhysicalAtAsync(int period, long rowId, int columnDefId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT ValueNumeric FROM doc.CellValue WHERE PeriodKey = {period} " +
            $"AND TableRowId = {rowId} AND ColumnDefId = {columnDefId}";
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : (decimal)result;
    }

    /// <summary>
    /// Прибирання власного стану тесту (retro 375): спільна тестова база не має отримати від цього тесту ні
    /// актуальних чи «Running» прогонів (обробник завершення тут підмінений, тож прогони задачі лишались би
    /// «Running»; інакше «останній прогін» інших тестів — наш), ні змінених станів, ні станів процесу.
    /// Лише рядки ЦЬОГО проєкту й документа. <c>aud.CellChange</c> незмінний (тригер) — його рядки лишаються на
    /// власному <c>DocumentId</c> і періодах 2099.
    /// </summary>
    private async Task CleanupAsync(World world)
    {
        await ExecuteAsync(
            $"UPDATE calc.CalculationRun SET Status = N'Superseded' " +
            $"WHERE ProjectId = {world.Doc.ProjectId} AND Status <> N'{Ecr.Domain.Entities.Calculations.CalculationRun.SupersededStatus}';");
        await ExecuteAsync($"DELETE FROM wf.ApprovalState WHERE DocumentId = {world.Doc.DocumentId};");
        await ExecuteAsync(
            $"UPDATE doc.Period SET State = {(int)PeriodState.Open} " +
            $"WHERE ProjectId = {world.Doc.ProjectId} AND State <> {(int)PeriodState.Open};");
    }

    private async Task ExecuteAsync(string statement)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync();
    }

    private sealed record World(
        TestDocumentBuilder Builder,
        TestDocument Doc,
        IReadOnlyDictionary<int, TestPeriodInstance> Others,
        TestPeriodInstance? PriorYear,
        Dictionary<int, int> CalcMonth,
        int Total,
        IMetadataCache Metadata);
}
