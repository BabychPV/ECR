// tests/Ecr.Infrastructure.Tests/Jobs/YearLayoutCalculatedRoutingSqlTests.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
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
/// LAND-YEAR L1 (D-474 варіант A): обчислювані колонки таблиці «місяці в колонках» версії з розкладкою
/// <c>YearWithMonthColumns</c> мають ВЛАСНИКА — місячна <c>C_Mm</c> належить періоду <c>m</c>, не-місячна — якорю
/// року. Перерахунок періоду <c>m</c> пише лише <c>C_Mm</c> у свій екземпляр, а річний перегляд (якір) бачить
/// <c>C_M01..C_M12</c> з дванадцяти власників. Версія <c>PerMonthCopies</c> під спадковим прапором — як до L1.
/// </summary>
/// <remarks>
/// <para>
/// Світ: документ 202601 (якір = перегляд року) з таблицею «місяці в колонках»: <c>IN</c> — не-місячний введений
/// (власник — якір), <c>M1..M12</c> — введені місячні, <c>CM1..CM12</c> — місячні формули <c>[Mm] * [IN]</c>,
/// <c>TOT</c> — не-місячна формула <c>[CM9]</c> (підсумок у якорі). Екземпляри всіх 12 місяців, усі Open.
/// </para>
/// <para>
/// ⛔ Мутації (локально, не комітити; червоне до фіксу): (1) <c>MonthColumnRouter.OwnerOf</c> — повернути
/// <c>column.IsComputed → viewPeriod</c> без <c>routesComputed</c> — червоніють читання і перерахунок; (2) прибрати
/// <c>OverlayComputedAsync</c> у <c>MonthRoutedCellStore</c> — червоніє читання року; (3) прибрати пропуск
/// <c>ownedElsewhere</c> у <c>RecalculationService.Evaluate</c> і фільтр цілей — червоніє «лише C_Mm у m»;
/// (4) прибрати гілку <c>computedRouted</c> у <c>CrossPeriodFanOut.RoutedInputsAsync</c> — червоніє підсумок якоря.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class YearLayoutCalculatedRoutingSqlTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
    private const int Year = 2026;
    private const int Anchor = 1;
    private const int TotalFormulaId = 930_100;
    private const int MonthFormulaBase = 930_000;
    private const decimal Input = 10m;

    // ── читання: річний перегляд бачить C_Mm власників ───────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    public async Task Рік_перегляд_якоря_показує_CM1_CM12_з_дванадцяти_власників_а_не_свої_копії()
    {
        var world = await ArrangeAsync();
        for (var m = 1; m <= 12; m++)
        {
            await SetCellAsync(world, m, world.CalcMonth[m], 100m + m, calculated: true);   // власник CMm
            if (m != Anchor)
            {
                await SetCellAsync(world, Anchor, world.CalcMonth[m], -1m, calculated: true); // стара копія в якорі
            }
        }

        await using var db = world.Builder.CreateContext();
        var router = new MonthColumnRouter(db, Flag(false), YearRegistry(world));
        var store = new MonthRoutedCellStore(new NormalizedCellStore(db), router, world.Metadata, db);
        var slice = await store.ReadSliceAsync(world.Doc.TableInstanceId, Key(Anchor), CancellationToken.None);

        for (var m = 1; m <= 12; m++)
        {
            Assert.Equal(100m + m, ValueOf(slice, world.Doc.RowIds[0], world.CalcMonth[m]));
        }

        // Одна комірка на адресу: стара копія затінена, а не продубльована.
        Assert.Equal(12, slice.Count(c => world.CalcMonth.ContainsValue(c.Address.ColumnDefId)));
        Assert.All(
            slice.Where(c => world.CalcMonth.ContainsValue(c.Address.ColumnDefId)),
            c => Assert.True(c.Value.IsCalculated));
        Assert.Equal(1, store.OwnerComputedQueryCount);
    }

    // ── перерахунок: період m пише лише C_Mm у свій екземпляр ────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    public async Task Перерахунок_періоду_m_пише_CMm_лише_в_екземпляр_m_і_рік_бачить_усі_дванадцять()
    {
        var world = await ArrangeAsync();
        await SeedInputsAsync(world);

        await using (var db = world.Builder.CreateContext())
        {
            var router = new MonthColumnRouter(db, Flag(false), YearRegistry(world));
            var store = new MonthRoutedCellStore(new NormalizedCellStore(db), router, world.Metadata, db);
            var service = Formulas(db, world, store, router);

            // Якір — останнім: його TOT читає CM9, яку пише прогін періоду 9 (порядок «читач після власника»
            // у задачі забезпечує fan-out — окремий тест нижче).
            for (var m = 12; m >= 1; m--)
            {
                await service.RecalculateAllAsync(world.Doc.DocumentId, Key(m), CancellationToken.None);
            }
        }

        // Фізично: кожна CMm — рівно одна комірка, і лише в екземплярі періоду m.
        for (var m = 1; m <= 12; m++)
        {
            Assert.Equal([Period(m)], await PeriodsHoldingAsync(world, world.CalcMonth[m]));
        }

        // TOT (не-місячна) — лише в якорі.
        Assert.Equal([Period(Anchor)], await PeriodsHoldingAsync(world, world.Total));

        // Річний перегляд: CMm = m × IN з власника m; TOT = CM9 = 90.
        await using var read = world.Builder.CreateContext();
        var readRouter = new MonthColumnRouter(read, Flag(false), YearRegistry(world));
        var readStore = new MonthRoutedCellStore(new NormalizedCellStore(read), readRouter, world.Metadata, read);
        var slice = await readStore.ReadSliceAsync(world.Doc.TableInstanceId, Key(Anchor), CancellationToken.None);
        for (var m = 1; m <= 12; m++)
        {
            Assert.Equal(m * Input, ValueOf(slice, world.Doc.RowIds[0], world.CalcMonth[m]));
        }

        Assert.Equal(9 * Input, ValueOf(slice, world.Doc.RowIds[0], world.Total));
    }

    // ── fan-out: змінена CM9 у періоді 9 перераховує підсумок якоря ──────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    public async Task Правка_IN_у_якорі_CM9_перераховує_власник_9_а_підсумок_якоря_бачить_нове_значення()
    {
        var world = await ArrangeAsync();
        await SeedInputsAsync(world);

        // Стан «до правки»: CM9 = 9 × 10 у власнику, TOT = 90 у якорі.
        await SetCellAsync(world, 9, world.CalcMonth[9], 9 * Input, calculated: true);
        await SetCellAsync(world, Anchor, world.Total, 9 * Input, calculated: true);

        // Правка IN у якорі: 10 → 20.
        await SetCellAsync(world, Anchor, world.Doc.ColumnDefIds[1], 20m);

        await using var db = world.Builder.CreateContext();
        var router = new MonthColumnRouter(db, Flag(false), YearRegistry(world));
        var store = new MonthRoutedCellStore(new NormalizedCellStore(db), router, world.Metadata, db);
        var periods = Periods(world);
        var fanOut = new CrossPeriodFanOut(
            new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now)),
            Versions(world), periods, Substitute.For<IBackgroundJobScheduler>(), db, router, world.Metadata);
        var job = new FormulaRecalculationJob(Formulas(db, world, store, router), db, fanOut, periods);

        await job.ExecuteAsync(
            new FormulaRecalculationRequest(
                world.Doc.TableInstanceId, Period(Anchor),
                [new DirtyCell(world.Doc.RowIds[0], world.Doc.ColumnDefIds[1])]),
            new CapturingJobProgress(),
            CancellationToken.None);

        Assert.Equal(9 * 20m, await PhysicalAsync(world, 9, world.CalcMonth[9]));
        Assert.Equal(9 * 20m, await PhysicalAsync(world, Anchor, world.Total));
        Assert.Equal([Period(9)], await PeriodsHoldingAsync(world, world.CalcMonth[9]));
    }

    // ── контроль: PerMonthCopies під спадковим прапором — як до L1 ───────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    public async Task Контроль_версія_PerMonthCopies_під_прапором_читає_й_пише_обчислене_в_екземплярі_перегляду()
    {
        var world = await ArrangeAsync();
        await SeedInputsAsync(world);
        await SetCellAsync(world, 9, world.CalcMonth[9], 999m, calculated: true);
        await SetCellAsync(world, Anchor, world.CalcMonth[9], -1m, calculated: true);

        await using (var db = world.Builder.CreateContext())
        {
            // Реєстр знає річну версію, але НЕ цю (документ — PerMonthCopies); прапор маршрутизує введене.
            var router = new MonthColumnRouter(db, Flag(true), OtherYearRegistry());
            var store = new MonthRoutedCellStore(new NormalizedCellStore(db), router, world.Metadata, db);

            Assert.False(((IMonthColumnRouter)router).RoutesComputedVersion(world.Doc.TemplateVersionId));

            var slice = await store.ReadSliceAsync(world.Doc.TableInstanceId, Key(Anchor), CancellationToken.None);
            Assert.Equal(-1m, ValueOf(slice, world.Doc.RowIds[0], world.CalcMonth[9])); // своя копія, як до L1
            Assert.Equal(0, store.OwnerComputedQueryCount);                              // жодного нового запиту
            Assert.Equal(1, store.OwnerQueryCount);                                      // запит введених — як був

            var service = Formulas(db, world, store, router);
            await service.RecalculateAllAsync(world.Doc.DocumentId, Key(Anchor), CancellationToken.None);
            await service.RecalculateAllAsync(world.Doc.DocumentId, Key(9), CancellationToken.None);
            Assert.Equal(0, store.OwnerComputedQueryCount);
        }

        // Кожен прогін пише ВСІ CMm у свій екземпляр (старе зберігання «по періодах»), значення введених — з власників.
        Assert.Equal([Period(Anchor), Period(9)], await PeriodsHoldingAsync(world, world.CalcMonth[9]));
        Assert.Equal(90m, await PhysicalAsync(world, Anchor, world.CalcMonth[9]));
        Assert.Equal(90m, await PhysicalAsync(world, 9, world.CalcMonth[9]));
        Assert.Equal([Period(Anchor), Period(9)], await PeriodsHoldingAsync(world, world.CalcMonth[4]));
    }

    // ── допоміжне ────────────────────────────────────────────────────────

    private static int Period(int month) => (Year * 100) + month;

    private static PeriodKey Key(int month) => new(Period(month));

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

    private static ITemplateLayoutRegistry OtherYearRegistry()
    {
        var registry = Substitute.For<ITemplateLayoutRegistry>();
        registry.HasAny.Returns(true);
        registry.IsYearVersion(Arg.Any<int>()).Returns(false);
        registry.IsYearSheet(Arg.Any<int>()).Returns(false);
        return registry;
    }

    private async Task<World> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            periodKey: Period(Anchor), columnCount: 2, rowCount: 1,
            layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12);

        // Обчислювані колонки в БАЗІ (запит власників обчислених бере тип і місяць з cfg.ColumnDef).
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

        await using (var setup = builder.CreateContext())
        {
            foreach (var period in await setup.Periods.Where(p => p.ProjectId == doc.ProjectId).ToListAsync())
            {
                period.AdvanceTo(PeriodState.Open, DateTime.UtcNow);
            }

            await setup.SaveChangesAsync();
        }

        var world = new World(builder, doc, others, calcMonth, total, null!);
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

    private async Task SetCellAsync(World world, int month, int columnDefId, decimal value, bool calculated = false)
    {
        var period = Period(month);
        var rowId = RowOf(world, month);
        await ExecuteAsync(
            $"DELETE FROM doc.CellValue WHERE PeriodKey = {period} AND TableRowId = {rowId} AND ColumnDefId = {columnDefId}; " +
            "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES " +
            $"({period}, {rowId}, {columnDefId}, {world.Doc.TableDefId}, " +
            $"{value.ToString(CultureInfo.InvariantCulture)}, {(calculated ? 1 : 0)}, 0);");
    }

    private static RecalculationService Formulas(
        EcrDbContext db, World world, ICellStore store, IMonthColumnRouter router)
    {
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var clock = new TestClock(Now);

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        return new RecalculationService(
            store, new RowStore(db, bulk, clock), Periods(world), world.Metadata,
            Versions(world), new RealFormulaEngine(), units, Substitute.For<IRegistryStore>(), headers,
            new AuditWriter(db), clock, new UnitOfWork(db), new SheetEditGate(db), monthRouter: router);
    }

    private static IPeriodStore Periods(World world)
    {
        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(world.Doc.DocumentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(Year, 1, 1), new DateOnly(Year, 1, 31)));
        periods.FindPeriodStateAsync(world.Doc.DocumentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((PeriodState?)PeriodState.Open);
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

    private static decimal? ValueOf(IReadOnlyList<CellRecord> slice, long rowId, int columnDefId)
        => slice.SingleOrDefault(c => c.Address.TableRowId == rowId && c.Address.ColumnDefId == columnDefId)?.Value.ValueNumeric;

    private async Task<List<int>> PeriodsHoldingAsync(World world, int columnDefId)
    {
        var result = new List<int>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT cv.PeriodKey FROM doc.CellValue cv JOIN doc.TableRow r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId " +
            $"WHERE cv.ColumnDefId = {columnDefId} AND cv.ValueNumeric IS NOT NULL " +
            $"AND r.TableInstanceId IN (SELECT Id FROM doc.TableInstance WHERE DocumentId = {world.Doc.DocumentId}) " +
            "ORDER BY cv.PeriodKey";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetInt32(0));
        }

        return result;
    }

    private async Task<decimal?> PhysicalAsync(World world, int month, int columnDefId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT ValueNumeric FROM doc.CellValue WHERE PeriodKey = {Period(month)} " +
            $"AND TableRowId = {RowOf(world, month)} AND ColumnDefId = {columnDefId}";
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : (decimal)result;
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
        Dictionary<int, int> CalcMonth,
        int Total,
        IMetadataCache Metadata);
}
