// tests/Ecr.Calculations.Tests/ScriptCalculatedOwnerPeriodSqlTests.cs
using System.Globalization;
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.Infrastructure.Persistence;
using Ecr.Scripting;
using Ecr.TestKit;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// MS (рецензія 2, п.1): колонка <c>Calculated</c> місяця з ЧУЖИМ власником (версія YearWithMonthColumns, Q-17/D-474) —
/// скрипт-читач (<c>row.X[m]</c>, §13.9) бере число з періоду-власника, як і сітка (<see cref="CalculatedCellOverlay"/>
/// з <see cref="CalculatedOwnerRouting"/>), а не число поточного періоду.
/// </summary>
/// <remarks>
/// Склад — справжні <see cref="MonthColumnRouter"/>, <see cref="RowStore"/>, <see cref="MonthRoutedCellStore"/> і
/// <see cref="ScriptCalculationModule"/>; числа методології-джерела — подвійник сховища результатів, спільний для сітки й
/// читача. Періоди 2099xx. Мутаційні докази: у <c>ScriptRuntime.LoadCalculatedAsync</c> зробити <c>owners = null</c> —
/// червоніє <see cref="Читач_бере_число_періоду_власника_як_сітка"/>; не читати <c>NoteForeignOwnerRead</c> — червоніє
/// <see cref="Читання_чужого_періоду_позначає_прогін"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class ScriptCalculatedOwnerPeriodSqlTests(SqlServerFixture sql)
{
    private const int Year = 2099;
    private const int SourceVersionId = 480;
    private const int SourceMethodologyId = 48;
    private const int ReaderVersionId = 490;
    private const int ReaderMethodologyId = 49;
    private const int OneUnit = 7;
    private const int LastPeriod = 4;
    private static readonly DateTime Now = new(2026, 11, 17, 8, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(1, 1, 11)]
    [InlineData(1, 2, 22)]
    [InlineData(1, 3, 33)]
    [InlineData(2, 1, 11)]
    [InlineData(2, 3, 33)]
    [InlineData(3, 3, 33)]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Читач_бере_число_періоду_власника_як_сітка(int viewPeriod, int month, int expected)
    {
        var world = await ArrangeAsync();
        var facts = Facts(world);

        // Сітка: комірка колонки місяця в екземплярі періоду перегляду.
        var grid = await GridValueAsync(world, facts, viewPeriod, month);

        // Скрипт-читач у тому ж періоді читає row.X[month].
        var read = await ReadAsync(world, facts, viewPeriod, month);

        Assert.Equal(expected, grid);
        Assert.Equal(grid, read.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Читання_чужого_періоду_позначає_прогін()
    {
        var world = await ArrangeAsync();
        var facts = Facts(world);

        var foreign = await ReadAsync(world, facts, viewPeriod: 1, month: 3);

        Assert.Equal([Period(2), Period(3)], foreign.Run.ForeignOwnerPeriods);

        // Контроль: без прив'язок колонок до чужих місяців (лише місяць періоду перегляду) позначки немає.
        var own = await ReadAsync(world, facts, viewPeriod: 1, month: 1, boundMonths: 1);
        Assert.Equal(11m, own.Value);
        Assert.Empty(own.Run.ForeignOwnerPeriods);
    }

    // ── сітка й читач ────────────────────────────────────────────────────

    private async Task<decimal?> GridValueAsync(World world, List<YearResult> facts, int view, int month)
    {
        var methodologies = Methodologies(world);
        var results = Results(world, facts);

        await using var db = world.Builder.CreateContext();
        var rows = new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now));
        var rowIds = await rows.GetRowIdsAsync(world.Instances[view], Key(view), CancellationToken.None);
        var table = Table(world);
        var reference = new TableInstanceRef(
            world.Instances[view], world.Doc.DocumentId, world.Doc.TableDefId, world.Doc.TemplateVersionId, Period(view));
        var calculated = world.Doc.MonthColumnDefIds.ToHashSet();

        // Як зріз таблиці (GetTableSliceHandler): колонки з чужим власником і їхні періоди-власники — спільним правилом.
        var owners = await CalculatedOwnerRouting.OwnerPeriodsAsync(
            Router(db), world.Doc.DocumentId, reference, table, calculated, CancellationToken.None);

        var overlaid = await new CalculatedCellOverlay(methodologies, results).ApplyAsync(
            world.Doc.DocumentId,
            Period(view),
            [new OverlayTable(world.Instances[view], world.Doc.TableDefId, rowIds, [], calculated, owners)],
            CancellationToken.None);

        return overlaid[world.Instances[view]]
            .SingleOrDefault(c => c.Address.TableRowId == world.Rows[view] && c.Address.ColumnDefId == world.MonthColumn(month))
            ?.Value.ValueNumeric;
    }

    private async Task<(decimal? Value, ScriptRunContext Run)> ReadAsync(
        World world, List<YearResult> facts, int viewPeriod, int month, int boundMonths = 3)
    {
        const string Script = "rows from TA;\noutput Z: one = row.X[MONTH];";
        var text = Script.Replace("MONTH", month.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        await using var db = world.Builder.CreateContext();
        var router = Router(db);
        var cells = new MonthRoutedCellStore(new NormalizedCellStore(db), router, world.Metadata, db);
        var rows = new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now));

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(world.Doc.DocumentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(Year, 1, 1), new DateOnly(Year, 12, 31)));

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase) { ["one"] = new(OneUnit, "one", 1, 1m) },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        var store = Methodologies(world, boundMonths);
        store.GetScriptAsync(ReaderVersionId, Arg.Any<CancellationToken>())
            .Returns(new MethodologyScript(text, null, MethodologyEngine.Script, false, NumericMode.Strict));
        var output = new MethodologyOutput(ReaderVersionId, EcrCode.Create("Z"), OneUnit, 1);
        output.SetPerSubstance(false);
        store.GetOutputsAsync(ReaderVersionId, Arg.Any<CancellationToken>()).Returns([output]);
        store.GetSubstancesAsync(ReaderVersionId, Arg.Any<CancellationToken>()).Returns([]);
        store.ResolveImportsAsync(ReaderVersionId, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns([]);

        var run = new ScriptRunContext();
        var module = new ScriptCalculationModule(
            new Evaluator(new FunctionRegistry()),
            store,
            new ScriptProgramCache(),
            new CalendarContext(),
            units,
            periods,
            Substitute.For<ICalculationBindingStore>(),
            rows,
            cells,
            world.Metadata,
            new CalculationInputBuilder(cells, world.Metadata, rows),
            Results(world, facts),
            run,
            monthRouter: router);

        var descriptor = new MethodologyDescriptor(
            MethodologyId: ReaderMethodologyId,
            MethodologyVersionId: ReaderVersionId,
            Code: "READER",
            VersionNumber: "1.0.0.0",
            Level: CalculationLevel.Configuration,
            NumericMode: NumericMode.Strict,
            CalendarMode: CalendarMode.Actual,
            TraceLevel: TraceLevel.ErrorsOnly,
            Engine: MethodologyEngine.Script);

        var outcome = await module.ExecuteAsync(
            new CalculationInput(
                descriptor, world.Doc.DocumentId, world.Instances[viewPeriod], Key(viewPeriod), "F_Sum", []),
            CancellationToken.None);

        return (outcome.Values.SingleOrDefault(v => v.OutputCode == "Z")?.Value, run);
    }

    // ── подвійники ───────────────────────────────────────────────────────

    /// <summary>
    /// Числа методології-джерела: у кожному періоді m — її місяць m у екземплярі періоду m (прогін пише лише свій місяць,
    /// B80). Плюс «сміття» в екземплярі періоду 1 — число місяця 3 з прогону, який цього місяця не рахував.
    /// </summary>
    private static List<YearResult> Facts(World world)
    {
        var facts = new List<YearResult>();
        for (var m = 1; m <= 3; m++)
        {
            facts.Add(new YearResult(
                Period(m), SourceVersionId, world.Instances[m], "F_Sum", Month(m), null, OneUnit, 11m * m));
        }

        facts.Add(new YearResult(Period(1), SourceVersionId, world.Instances[1], "F_Sum", Month(3), null, OneUnit, -1m));
        return facts;
    }

    private static ICalculationResultStore Results(World world, List<YearResult> facts)
    {
        var results = Substitute.For<ICalculationResultStore, ICurrentResultFilterReader, ICalculationYearReader>();
        ((ICurrentResultFilterReader)results)
            .ReadCurrentMatchingAsync(world.Doc.DocumentId, Arg.Any<int>(), Arg.Any<CurrentResultFilter>(), Arg.Any<CancellationToken>())
            .Returns(call => facts
                .Where(f => f.PeriodKey == call.ArgAt<int>(1)
                            && (f.TableInstanceId is null || call.ArgAt<CurrentResultFilter>(2).TableInstanceIds.Contains(f.TableInstanceId.Value)))
                .Select(f => new CalculationResultRow(
                    f.MethodologyVersionId, f.SourceRowKey, f.OutputCode, f.Value, f.UnitId, f.SubstanceEntryId, f.TableInstanceId))
                .ToList());
        ((ICalculationYearReader)results)
            .ReadTableInstancesAsync(world.Doc.DocumentId, Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(call => world.Instances
                .Where(i => call.ArgAt<IReadOnlyList<int>>(1).Contains(Period(i.Key)))
                .Select(i => new TableInstanceRef(i.Value, world.Doc.DocumentId, world.Doc.TableDefId, world.Doc.TemplateVersionId, Period(i.Key)))
                .ToList());
        ((ICalculationYearReader)results)
            .ReadYearAsync(Arg.Any<CalculationYearReadRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.ArgAt<CalculationYearReadRequest>(0);
                return CalculationYearFacts.Empty with
                {
                    Results = [.. facts.Where(f => request.PeriodKeys.Contains(f.PeriodKey)
                                                   && (f.TableInstanceId is null || request.TableInstanceIds.Contains(f.TableInstanceId.Value)))],
                };
            });
        return results;
    }

    private static IMethodologyStore Methodologies(World world, int boundMonths = 3)
    {
        var store = Substitute.For<IMethodologyStore>();
        store.GetColumnResultBindingsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns([.. Enumerable.Range(1, boundMonths).Select(m => new ColumnResultBinding(
                world.Doc.TableDefId, world.MonthColumn(m), SourceMethodologyId, Month(m), "{}", [SourceVersionId]))]);
        return store;
    }

    // ── світ ─────────────────────────────────────────────────────────────

    private async Task<World> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            periodKey: Period(1), columnCount: 0, rowCount: 0, layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12);

        var instances = new Dictionary<int, long> { [1] = doc.TableInstanceId };
        var rows = new Dictionary<int, long> { [1] = await InsertRowAsync(builder, 1, doc.TableInstanceId) };
        for (var p = 2; p <= LastPeriod; p++)
        {
            var other = await builder.AddPeriodInstanceAsync(doc, Key(p));
            instances[p] = other.TableInstanceId;
            rows[p] = other.RowIds[0];   // рядки якоря копіюються в інші періоди
        }

        return new World(builder, doc, instances, rows, Metadata(doc));
    }

    private async Task<long> InsertRowAsync(TestDocumentBuilder builder, int period, long instanceId)
    {
        var id = await new BulkCellLoader(sql.ConnectionString, 1000)
            .ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None);
        await using var db = builder.CreateContext();
        db.TableRows.Add(new TableRow(Key(period), id, instanceId, RowKey.Create("F_Sum"), 1, Now));
        await db.SaveChangesAsync();
        return id;
    }

    private static MonthColumnRouter Router(EcrDbContext db)
    {
        var registry = Substitute.For<ITemplateLayoutRegistry>();
        registry.HasAny.Returns(true);
        registry.IsYearVersion(Arg.Any<int>()).Returns(true);
        registry.IsYearSheet(Arg.Any<int>()).Returns(true);
        return new MonthColumnRouter(db, Substitute.For<IConfiguration>(), registry);
    }

    private static TableDef Table(World world)
        => world.Metadata.GetAsync(world.Doc.TemplateVersionId, CancellationToken.None).GetAwaiter().GetResult()
            .Sheets.SelectMany(s => s.Tables).Single();

    /// <summary>Таблиця TA: місячні колонки <c>X_M01..X_M12</c> типу <c>Calculated</c> (їх заповнюють результати методологій).</summary>
    private static IMetadataCache Metadata(TestDocument doc)
    {
        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create($"SH{doc.SheetDefId}"), Text("Sheet"), 1);
        SetId(sheet, doc.SheetDefId);
        var table = new TableDef(doc.SheetDefId, EcrCode.Create("TA"), Text("TA"), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Dynamic);
        SetId(table, doc.TableDefId);
        for (var m = 1; m <= 12; m++)
        {
            var code = "X_M" + m.ToString("00", CultureInfo.InvariantCulture);
            var column = new ColumnDef(doc.TableDefId, EcrCode.Create(code), Text(code), m, CellDataType.Calculated);
            column.SetMonth(true, (byte)m);
            SetId(column, doc.MonthColumnDefIds[m - 1]);
            table.AddColumn(column);
        }

        sheet.AddTable(table);
        var snapshot = new TemplateVersionSnapshot(
            doc.TemplateVersionId, 0, [sheet], table.Columns.ToDictionary(c => c.Id), new Dictionary<(int, string), RowDef>());

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);
        return metadata;
    }

    private static int Period(int month) => (Year * 100) + month;

    private static PeriodKey Key(int month) => new(Period(month));

    private static string Month(int month) => "M" + month.ToString("00", CultureInfo.InvariantCulture);

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId(Entity<int> entity, int id)
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);

    private sealed record World(
        TestDocumentBuilder Builder,
        TestDocument Doc,
        IReadOnlyDictionary<int, long> Instances,
        IReadOnlyDictionary<int, long> Rows,
        IMetadataCache Metadata)
    {
        public int MonthColumn(int month) => Doc.MonthColumnDefIds[month - 1];
    }
}
