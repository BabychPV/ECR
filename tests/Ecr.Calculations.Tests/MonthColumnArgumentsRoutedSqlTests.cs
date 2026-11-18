// tests/Ecr.Calculations.Tests/MonthColumnArgumentsRoutedSqlTests.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Calculations.Scripting.Migration;
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
using Ecr.MethodologyScriptMigration.Conversion;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Calculations.Tests;

/// <summary>
/// B80 N0: модуль «місяці в колонках» (HSE400) на МІСЯЧНОМУ проєкті з версією річної розкладки — зберігання
/// маршрутизоване (<see cref="MonthRoutedCellStore"/>, Q-17/D-474): місячна колонка <c>m</c> належить періоду <c>m</c>.
/// </summary>
/// <remarks>
/// <para>
/// Склад — той самий, що збирає контейнер (<c>Ecr.Infrastructure/DependencyInjection.cs</c>): <see cref="MonthColumnRouter"/>
/// з реєстром річних версій і без прапора <c>Collection:MonthColumnRouting</c>, <see cref="MonthRoutedCellStore"/> над
/// <see cref="NormalizedCellStore"/>, справжній <see cref="RowStore"/>; база — власна <c>EcrTest_*</c>, періоди 2099xx.
/// Модуль кличеться так, як кличе оркестратор: раз на рядок кожного екземпляра періоду, окремий scope на період.
/// </para>
/// <para>
/// Вимога (B80, рішення): прогін періоду <c>m</c> рахує й пише ЛИШЕ місяць <c>m</c>; річний перегляд (якір) показує
/// M01..M12 з результатів періодів-власників; річний проєкт — без змін.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class MonthColumnArgumentsRoutedSqlTests(SqlServerFixture sql, ITestOutputHelper output)
{
    private const int Year = 2099;
    private const int VersionId = 480;
    private const int OneUnit = 7;
    private const int LastPeriod = 4;   // 209904: екземпляр є, вводу немає.
    private static readonly DateTime Now = new(2026, 11, 17, 8, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Inputs = ["Methane", "Nitrogen"];

    private static readonly (string Code, string Expression)[] Formulas =
    [
        ("X400_TA_Sum", "@GCV.X400_TA_Methane + @GCV.X400_TA_Nitrogen"),
        ("X400_TA_Ratio", "Round(!X400_TA_Sum / @GCV.X400_TA_Methane, 2)"),
        ("X400_TA_Double", "!X400_TA_Sum * 2"),
    ];

    // ── прогін періоду m: лише місяць m ──────────────────────────────────

    public static TheoryData<MethodologyEngine> Engines => new() { MethodologyEngine.Formulas, MethodologyEngine.Script };

    [Theory]
    [MemberData(nameof(Engines))]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B80")]
    public async Task Місячний_проєкт_прогін_періоду_m_пише_лише_місяць_m_без_дублів_і_як_річний(MethodologyEngine engine)
    {
        var monthly = await ArrangeAsync("TA", routed: true, [.. Inputs, "F_Sum", "F_Ratio"]);
        for (var m = 1; m <= 3; m++)
        {
            await SetInputsAsync(monthly, period: m, month: m, Synthetic(m));
        }

        await AssertRoutedAsync(monthly);

        var methodologies = Methodologies(Formulas, [], engine);
        var runs = new Dictionary<int, List<Result>>();
        for (var p = 1; p <= LastPeriod; p++)
        {
            runs[p] = await RunPeriodAsync(monthly, p, methodologies, engine);
        }

        for (var p = 1; p <= 3; p++)
        {
            Assert.Equal(new[] { Month(p) }, runs[p].Select(r => r.Output).Distinct().Order(StringComparer.Ordinal));
            Assert.Equal(11m * p, runs[p].Single(r => r.Row == "F_Sum").Value);
            Assert.Equal(1.1m, runs[p].Single(r => r.Row == "F_Ratio").Value);
            Assert.All(runs[p], r => Assert.Equal(monthly.Instances[p], r.Instance));
        }

        Assert.Empty(runs[LastPeriod]);

        // Дублі, які побачив би завантажувач: одна пара (рядок, місяць) — один результат на весь документ.
        var all = runs.Values.SelectMany(r => r).ToList();
        Assert.Empty(all.GroupBy(r => (r.Row, r.Output)).Where(g => g.Count() > 1).Select(g => g.Key));

        // Ті самі числа, що дає річний документ з тим самим вводом в одному екземплярі.
        var yearly = await ArrangeAsync("TA", routed: false, [.. Inputs, "F_Sum", "F_Ratio"]);
        for (var m = 1; m <= 3; m++)
        {
            await SetInputsAsync(yearly, period: 1, month: m, Synthetic(m));
        }

        var year = await RunPeriodAsync(yearly, 1, methodologies, engine);
        Assert.Equal(Map(year), Map(all));
    }

    // ── FAIL-3: рядки є в одному періоді й відсутні в іншому ─────────────

    [Theory]
    [MemberData(nameof(Engines))]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B80")]
    public async Task Місячний_проєкт_період_рахує_власні_рядки_навіть_коли_в_якорі_їх_немає(MethodologyEngine engine)
    {
        // Якір має лише F_Sum; F_Ratio — у періодах 2–4, F_Double — лише в 3.
        var world = await ArrangeAsync(
            "TA", routed: true, [.. Inputs, "F_Sum"],
            extra: new Dictionary<int, string[]> { [2] = ["F_Ratio"], [3] = ["F_Ratio", "F_Double"], [4] = ["F_Ratio"] });
        for (var m = 1; m <= 3; m++)
        {
            await SetInputsAsync(world, period: m, month: m, Synthetic(m));
        }

        var methodologies = Methodologies(Formulas, [], engine);
        var runs = new Dictionary<int, string>();
        for (var p = 1; p <= LastPeriod; p++)
        {
            runs[p] = string.Join(", ", Map(await RunPeriodAsync(world, p, methodologies, engine)));
        }

        Assert.Equal("F_Sum|M01|11", runs[1]);
        Assert.Equal("F_Ratio|M02|1.1, F_Sum|M02|22", runs[2]);
        Assert.Equal("F_Double|M03|66, F_Ratio|M03|1.1, F_Sum|M03|33", runs[3]);
        Assert.Equal(string.Empty, runs[LastPeriod]);
    }

    // ── річний проєкт: без змін ──────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Engines))]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B80")]
    public async Task Річний_проєкт_один_прогін_рахує_всі_місяці_як_доти(MethodologyEngine engine)
    {
        var yearly = await ArrangeAsync("TA", routed: false, [.. Inputs, "F_Sum", "F_Ratio"]);
        for (var m = 1; m <= 3; m++)
        {
            await SetInputsAsync(yearly, period: 1, month: m, Synthetic(m));
        }

        await using (var db = yearly.Builder.CreateContext())
        {
            Assert.Null(await Router(db).GetDocumentAsync(yearly.Doc.DocumentId, CancellationToken.None));
        }

        var run = await RunPeriodAsync(yearly, 1, Methodologies(Formulas, [], engine), engine);

        Assert.Equal(
            "F_Ratio|M01|1.1, F_Ratio|M02|1.1, F_Ratio|M03|1.1, F_Sum|M01|11, F_Sum|M02|22, F_Sum|M03|33",
            string.Join(", ", Map(run)));
    }

    // ── перегляд: якір показує M01..M03 з періодів-власників ─────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B80")]
    public async Task Перегляд_якоря_показує_F_рядки_кожного_місяця_з_періоду_власника()
    {
        var world = await ArrangeAsync("TA", routed: true, [.. Inputs, "F_Sum"]);

        // Шар чисел після виправлення: місяць m — у прогоні періоду m, екземпляр періоду m. Плюс застаріле M02 у якорі
        // (прогін «усі місяці» до виправлення) — його перегляд не показує.
        var facts = new List<YearResult>();
        for (var m = 1; m <= 3; m++)
        {
            facts.Add(new YearResult(Period(m), VersionId, world.Instances[m], "F_Sum", Month(m), null, OneUnit, 11m * m));
        }

        facts.Add(new YearResult(Period(1), VersionId, world.Instances[1], "F_Sum", Month(2), null, OneUnit, -1m));

        foreach (var view in (int[])[1, 2])
        {
            var cells = await OverlayAsync(world, view, facts);
            var row = world.Rows[view]["F_Sum"];
            for (var m = 1; m <= 3; m++)
            {
                Assert.Equal(11m * m, Value(cells, row, world.MonthColumn(m)));
            }

            Assert.Null(Value(cells, row, world.MonthColumn(4)));
            Assert.Equal(cells.Count, cells.Select(c => c.Address).Distinct().Count());
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B80")]
    public async Task Перегляд_річного_проєкту_читає_всі_місяці_з_власного_екземпляра_як_доти()
    {
        var world = await ArrangeAsync("TA", routed: false, [.. Inputs, "F_Sum"]);

        // Річний документ: один екземпляр, усі місяці в ньому. Ті самі ключі в чужому екземплярі (99) не беруться.
        var facts = new List<YearResult>();
        for (var m = 1; m <= 3; m++)
        {
            facts.Add(new YearResult(Period(1), VersionId, world.Instances[1], "F_Sum", Month(m), null, OneUnit, 11m * m));
            facts.Add(new YearResult(Period(1), VersionId, 99, "F_Sum", Month(m), null, OneUnit, -1m));
        }

        var cells = await OverlayAsync(world, 1, facts);
        var row = world.Rows[1]["F_Sum"];
        Assert.Equal(
            new decimal?[] { 11m, 22m, 33m, null },
            Enumerable.Range(1, 4).Select(m => Value(cells, row, world.MonthColumn(m))).ToArray());
    }

    // ── золотий набір HSE400: річний і місячний ──────────────────────────

    [Theory]
    [MemberData(nameof(Engines))]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B80")]
    public async Task Золоті_тести_HSE400_проходять_на_річному_і_на_місячному_документі(MethodologyEngine engine)
    {
        var model = Hse400Model.Load();
        var table = model.Tables[model.GoldenTable];
        var methodologies = Methodologies(model.Formulas, model.Constants, engine);

        var monthly = await ArrangeAsync(model.GoldenTable, routed: true, table);
        var yearly = await ArrangeAsync(model.GoldenTable, routed: false, table);
        for (var m = 1; m <= 3; m++)
        {
            await SetInputsAsync(monthly, period: m, month: m, model.GoldenInputs);
            await SetInputsAsync(yearly, period: 1, month: m, model.GoldenInputs);
        }

        var runs = new Dictionary<int, List<Result>>();
        for (var p = 1; p <= LastPeriod; p++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            runs[p] = await RunPeriodAsync(monthly, p, methodologies, engine);
            output.WriteLine($"perf monthly period {Period(p)}: {watch.ElapsedMilliseconds} ms, results {runs[p].Count}");
        }

        var yearWatch = System.Diagnostics.Stopwatch.StartNew();
        var year = await RunPeriodAsync(yearly, 1, methodologies, engine);
        output.WriteLine($"perf yearly period {Period(1)}: {yearWatch.ElapsedMilliseconds} ms, results {year.Count}");

        foreach (var test in model.Tests)
        {
            for (var m = 1; m <= 3; m++)
            {
                var monthValue = runs[m].Single(r => r.Row == test.Row && r.Output == Month(m)).Value;
                var yearValue = year.Single(r => r.Row == test.Row && r.Output == Month(m)).Value;
                Assert.InRange(monthValue, test.Expected - test.Tolerance, test.Expected + test.Tolerance);
                Assert.InRange(yearValue, test.Expected - test.Tolerance, test.Expected + test.Tolerance);
            }
        }

        for (var p = 1; p <= 3; p++)
        {
            Assert.Equal(new[] { Month(p) }, runs[p].Select(r => r.Output).Distinct());
        }

        Assert.Empty(runs[LastPeriod]);
        Assert.Equal(Map(year), Map(runs.Values.SelectMany(r => r)));
    }

    // ── прогін і перегляд ────────────────────────────────────────────────

    /// <summary>Як оркестратор: окремий scope на період, виклик модуля на кожен рядок кожного екземпляра періоду.</summary>
    private async Task<List<Result>> RunPeriodAsync(
        World world, int period, IMethodologyStore methodologies, MethodologyEngine engine = MethodologyEngine.Formulas)
    {
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

        var bindingStore = Substitute.For<ICalculationBindingStore>();
        var script = new ScriptCalculationModule(
            new Evaluator(new FunctionRegistry()),
            methodologies,
            new ScriptProgramCache(),
            new CalendarContext(),
            units,
            periods,
            bindingStore,
            rows,
            cells,
            world.Metadata,
            new CalculationInputBuilder(cells, world.Metadata, rows),
            Substitute.For<ICalculationResultStore>());
        var module = new MonthColumnArgumentsModule(
            new RealFormulaEngine(),
            methodologies,
            new ConstantResolver(Substitute.For<IConstantStore>()),
            new CalendarContext(),
            units,
            periods,
            bindingStore,
            cells,
            world.Metadata,
            rows,
            monthRouter: router,
            scriptModule: script);

        var key = Key(period);
        var results = new List<Result>();
        foreach (var instance in await rows.GetTableInstancesAsync(world.Doc.DocumentId, key, CancellationToken.None))
        {
            foreach (var rowKey in (await rows.GetRowIdsAsync(instance.TableInstanceId, key, CancellationToken.None)).Keys)
            {
                var outcome = await module.ExecuteAsync(
                    new CalculationInput(Descriptor(engine), world.Doc.DocumentId, instance.TableInstanceId, key, rowKey, []),
                    CancellationToken.None);
                results.AddRange(outcome.Values.Select(v => new Result(instance.TableInstanceId, rowKey, v.OutputCode, v.Value)));
            }
        }

        return results;
    }

    /// <summary>Накладення чисел на зріз екземпляра періоду <paramref name="view"/> — так, як його робить зріз таблиці.</summary>
    private async Task<IReadOnlyList<CellRecord>> OverlayAsync(World world, int view, List<YearResult> facts)
    {
        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetColumnResultBindingsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns([new ColumnResultBinding(world.Doc.TableDefId, world.MonthColumn(1), 48, "M01", "{}", [VersionId])]);

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

        await using var db = world.Builder.CreateContext();
        var rows = new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now));
        var rowIds = await rows.GetRowIdsAsync(world.Instances[view], Key(view), CancellationToken.None);
        var table = (await world.Metadata.GetAsync(world.Doc.TemplateVersionId, CancellationToken.None))
            .Sheets.SelectMany(s => s.Tables).Single();
        var monthColumns = MonthRowResultOverlay.MonthColumns(table);

        // Як зріз таблиці (GetTableSliceHandler): роутер того самого scope і опис таблиці.
        return await new MonthRowResultOverlay(methodologies, results, Router(db)).ApplyAsync(
            world.Doc.DocumentId, Period(view), world.Instances[view], table, rowIds, [], monthColumns, CancellationToken.None);
    }

    // ── світ ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Документ з однією таблицею «місяці в колонках» (M01..M12) і екземплярами 209901..209904 (місячний проєкт) або
    /// одним екземпляром 209901 (річний проєкт). Рядки якоря копіюються в інші періоди; <paramref name="extra"/> — рядки,
    /// яких у якорі немає.
    /// </summary>
    private async Task<World> ArrangeAsync(
        string tableCode, bool routed, IReadOnlyList<string> rowKeys, Dictionary<int, string[]>? extra = null)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            periodKey: Period(1), columnCount: 0, rowCount: 0, layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12);

        var instances = new Dictionary<int, long> { [1] = doc.TableInstanceId };
        var rows = new Dictionary<int, Dictionary<string, long>> { [1] = await InsertRowsAsync(builder, 1, doc.TableInstanceId, rowKeys) };

        if (routed)
        {
            for (var p = 2; p <= LastPeriod; p++)
            {
                var other = await builder.AddPeriodInstanceAsync(doc, Key(p));
                instances[p] = other.TableInstanceId;
                rows[p] = other.RowKeys.Zip(other.RowIds).ToDictionary(r => r.First, r => r.Second, StringComparer.Ordinal);
                if (extra?.TryGetValue(p, out var more) == true)
                {
                    foreach (var (rowKey, rowId) in await InsertRowsAsync(builder, p, other.TableInstanceId, more, rows[p].Count))
                    {
                        rows[p][rowKey] = rowId;
                    }
                }
            }
        }
        else
        {
            await ExecuteAsync($"UPDATE doc.Project SET PeriodKind = {(int)PeriodKind.Yearly} WHERE Id = {doc.ProjectId};");
        }

        return new World(builder, doc, instances, rows, Metadata(doc, tableCode));
    }

    private async Task<Dictionary<string, long>> InsertRowsAsync(
        TestDocumentBuilder builder, int period, long instanceId, IReadOnlyList<string> keys, int ordinalOffset = 0)
    {
        var first = await new BulkCellLoader(sql.ConnectionString, 1000).ReserveIdsAsync("doc.TableRowSeq", keys.Count, CancellationToken.None);
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var db = builder.CreateContext();
        for (var i = 0; i < keys.Count; i++)
        {
            db.TableRows.Add(new TableRow(Key(period), first + i, instanceId, RowKey.Create(keys[i]), ordinalOffset + i + 1, Now));
            ids[keys[i]] = first + i;
        }

        await db.SaveChangesAsync();
        return ids;
    }

    /// <summary>Ввід місяця <paramref name="month"/> у рядки екземпляра періоду <paramref name="period"/>.</summary>
    private async Task SetInputsAsync(World world, int period, int month, IReadOnlyDictionary<string, decimal> values)
    {
        var statements = values.Select(v =>
            "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES " +
            $"({Period(period)}, {world.Rows[period][v.Key]}, {world.MonthColumn(month)}, {world.Doc.TableDefId}, " +
            $"{v.Value.ToString(CultureInfo.InvariantCulture)}, 0, 0);");
        await ExecuteAsync(string.Join(Environment.NewLine, statements));
    }

    /// <summary>Передумова маршрутизації: документ маршрутизований, ввід лежить у власниках, зріз періоду бачить чужі місяці.</summary>
    private async Task AssertRoutedAsync(World world)
    {
        await using var db = world.Builder.CreateContext();
        var router = Router(db);
        Assert.NotNull(await router.GetDocumentAsync(world.Doc.DocumentId, CancellationToken.None));

        var physical = new List<string>();
        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT DISTINCT cv.PeriodKey, c.MonthNumber FROM doc.CellValue cv JOIN cfg.ColumnDef c ON c.Id = cv.ColumnDefId " +
                "JOIN doc.TableRow r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId " +
                "JOIN doc.TableInstance ti ON ti.PeriodKey = r.PeriodKey AND ti.Id = r.TableInstanceId " +
                $"WHERE ti.DocumentId = {world.Doc.DocumentId} AND c.IsMonthColumn = 1 AND cv.IsEmpty = 0 ORDER BY 1, 2";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                physical.Add($"{reader.GetInt32(0)}:{Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture)}");
            }
        }

        Assert.Equal([$"{Period(1)}:1", $"{Period(2)}:2", $"{Period(3)}:3"], physical);

        var slice = await new MonthRoutedCellStore(new NormalizedCellStore(db), router, world.Metadata, db)
            .ReadSliceAsync(world.Instances[2], Key(2), CancellationToken.None);
        Assert.Contains(slice, c => c.Address.ColumnDefId == world.MonthColumn(1) && !c.Value.IsCalculated);
    }

    private static MonthColumnRouter Router(EcrDbContext db)
    {
        var registry = Substitute.For<ITemplateLayoutRegistry>();
        registry.HasAny.Returns(true);
        registry.IsYearVersion(Arg.Any<int>()).Returns(true);
        registry.IsYearSheet(Arg.Any<int>()).Returns(true);
        return new MonthColumnRouter(db, Substitute.For<IConfiguration>(), registry);
    }

    private static IMetadataCache Metadata(TestDocument doc, string tableCode)
    {
        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create($"SH{doc.SheetDefId}"), Text("Sheet"), 1);
        SetId(sheet, doc.SheetDefId);
        var table = new TableDef(doc.SheetDefId, EcrCode.Create(tableCode), Text(tableCode), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Dynamic);
        SetId(table, doc.TableDefId);
        for (var m = 1; m <= 12; m++)
        {
            var column = new ColumnDef(doc.TableDefId, EcrCode.Create(Month(m)), Text(Month(m)), m, CellDataType.Decimal);
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

    /// <summary>Скрипт, який дає конвертер на тих самих формулах і константах (шлях перенесення на проді).</summary>
    private static string ConvertedScript(
        IReadOnlyList<(string Code, string Expression)> formulas, IReadOnlyList<(string Code, decimal Value)> constants)
    {
        var result = ScriptConverter.Convert(new VersionSource
        {
            MethodologyCode = "X400",
            IsModuleLevel = true,
            NumericMode = NumericMode.Legacy,
            Formulas = [.. formulas.Select((f, i) => new SourceFormula(f.Code, f.Expression, i + 1))],
            Constants = [.. constants.Select(c => SourceConstant.OfNumber(c.Code, c.Value, unitCode: "one"))],
        });
        Assert.True(result.Text is not null, string.Join("; ", result.Issues.Select(i => $"{i.Severity} {i.Code}: {i.Message}")));
        return result.Text!;
    }

    private static IMethodologyStore Methodologies(
        IEnumerable<(string Code, string Expression)> formulas,
        IEnumerable<(string Code, decimal Value)> constants,
        MethodologyEngine engine = MethodologyEngine.Formulas)
    {
        var formulaList = formulas.ToList();
        var constantList = constants.ToList();
        var list = new List<MethodologyFormula>();
        foreach (var (code, expression) in formulaList)
        {
            var formula = new MethodologyFormula(VersionId, EcrCode.Create(code), expression);
            SetId(formula, list.Count + 1);   // порядок — топологічний, як у чернетки (EvaluationOrder = 0)
            list.Add(formula);
        }

        var store = Substitute.For<IMethodologyStore>();
        store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns(list);
        store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>()).Returns([]);
        store.GetSubstancesAsync(VersionId, Arg.Any<CancellationToken>()).Returns([]);
        store.GetConstantsAsync(VersionId, Arg.Any<CancellationToken>())
            .Returns([.. constantList.Select(c => new MethodologyConstant(VersionId, EcrCode.Create(c.Code), c.Value, OneUnit))]);
        if (engine == MethodologyEngine.Script)
        {
            store.GetScriptAsync(VersionId, Arg.Any<CancellationToken>())
                .Returns(new MethodologyScript(ConvertedScript(
                    [.. MethodologyFormulaOrder.Topological(list, new RealFormulaEngine()).Select(f => (f.Code, f.Expression))],
                    constantList), null, MethodologyEngine.Script, false, NumericMode.Legacy));
            store.ResolveImportsAsync(VersionId, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns([]);
        }

        return store;
    }

    private static MethodologyDescriptor Descriptor(MethodologyEngine engine)
        => new(
            MethodologyId: 48,
            MethodologyVersionId: VersionId,
            Code: "X400",
            VersionNumber: "1.0.0.0",
            Level: CalculationLevel.Module,
            NumericMode: NumericMode.Legacy,
            CalendarMode: CalendarMode.Actual,
            TraceLevel: TraceLevel.ErrorsOnly,
            Engine: engine);

    private static Dictionary<string, decimal> Synthetic(int month)
        => new(StringComparer.Ordinal) { ["Methane"] = 10m * month, ["Nitrogen"] = month };

    private static List<string> Map(IEnumerable<Result> results)
        => [.. results
            .Select(r => $"{r.Row}|{r.Output}|{r.Value.ToString(CultureInfo.InvariantCulture)}")
            .Order(StringComparer.Ordinal)];

    private static decimal? Value(IReadOnlyList<CellRecord> cells, long rowId, int columnDefId)
        => cells.SingleOrDefault(c => c.Address.TableRowId == rowId && c.Address.ColumnDefId == columnDefId)?.Value.ValueNumeric;

    private async Task ExecuteAsync(string statement)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync();
    }

    private static int Period(int month) => (Year * 100) + month;

    private static PeriodKey Key(int month) => new(Period(month));

    private static string Month(int month) => "M" + month.ToString("00", CultureInfo.InvariantCulture);

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId(Entity<int> entity, int id)
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);

    private sealed record Result(long Instance, string Row, string Output, decimal Value);

    private sealed record World(
        TestDocumentBuilder Builder,
        TestDocument Doc,
        IReadOnlyDictionary<int, long> Instances,
        IReadOnlyDictionary<int, Dictionary<string, long>> Rows,
        IMetadataCache Metadata)
    {
        public int MonthColumn(int month) => Doc.MonthColumnDefIds[month - 1];
    }

    /// <summary>
    /// Модель HSE400 (<c>tools/hse400/hse400-model.json</c>): формули, константи, золоті тести та рядки таблиці золотих
    /// тестів — той самий файл, з якого будівник стенда публікує методологію і її золотий набір.
    /// </summary>
    private sealed record Hse400Model(
        IReadOnlyList<(string Code, string Expression)> Formulas,
        IReadOnlyList<(string Code, decimal Value)> Constants,
        IReadOnlyList<GoldenTest> Tests,
        IReadOnlyDictionary<string, string[]> Tables,
        string GoldenTable,
        IReadOnlyDictionary<string, decimal> GoldenInputs)
    {
        private const string File = "tools/hse400/hse400-model.json";

        public static Hse400Model Load()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !System.IO.File.Exists(Path.Combine(dir.FullName, File)))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            using var json = JsonDocument.Parse(System.IO.File.ReadAllText(Path.Combine(dir!.FullName, File)));
            var root = json.RootElement;
            var methodology = root.GetProperty("methodology");

            var tests = methodology.GetProperty("tests").EnumerateArray()
                .Select(t => new GoldenTest(
                    t.GetProperty("table").GetString()!,
                    t.GetProperty("row").GetString()!,
                    t.GetProperty("month").GetString()!,
                    t.GetProperty("inputs").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDecimal(), StringComparer.Ordinal),
                    t.GetProperty("expected").GetDecimal(),
                    t.GetProperty("tolerance").GetDecimal()))
                .ToList();
            Assert.NotEmpty(tests);

            // Усі золоті тести моделі — одна таблиця, місяць M01, той самий ввід (будівник пише його в одну комірку).
            var goldenTable = Assert.Single(tests.Select(t => t.Table).Distinct());
            Assert.All(tests, t => Assert.Equal("M01", t.Month));
            var inputs = tests[0].Inputs;
            Assert.All(tests, t => Assert.Equal(inputs.OrderBy(i => i.Key), t.Inputs.OrderBy(i => i.Key)));

            return new Hse400Model(
                [.. methodology.GetProperty("formulas").EnumerateArray()
                    .Select(f => (f.GetProperty("code").GetString()!, f.GetProperty("expression").GetString()!))],
                [.. methodology.GetProperty("constants").EnumerateArray()
                    .Select(c => (c.GetProperty("code").GetString()!, c.GetProperty("value").GetDecimal()))],
                tests,
                root.GetProperty("tables").EnumerateArray().ToDictionary(
                    t => t.GetProperty("code").GetString()!,
                    t => t.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("key").GetString()!).ToArray(),
                    StringComparer.Ordinal),
                goldenTable,
                inputs);
        }
    }

    private sealed record GoldenTest(
        string Table, string Row, string Month, IReadOnlyDictionary<string, decimal> Inputs, decimal Expected, decimal Tolerance);
}
