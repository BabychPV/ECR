using System.Diagnostics;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Calculations.Tests.Scripting;

/// <summary>
/// ✎ SGF (DESIGN-sg-flaring §5.2a, E3–E7; REVIEW D2/D3): рушій скриптів читає довідник на дату РЯДКА з
/// <c>registry as of</c> — у місячному документі і в річному з колонками місяців.
/// </summary>
/// <remarks>
/// Довідник <c>GAS</c> (поле <c>MOL</c>) має дві версії; межа версій — усередині періоду документа. Мутаційні докази
/// (правило 401) — у звіті лінії.
/// </remarks>
public sealed class RegistryAsOfEngineTests(ITestOutputHelper output)
{
    private const int GasId = 41;
    private const int CaseId = 42;
    private const int MolField = 1;
    private const int CaseField = 2;
    private const int KeyField = 3;

    private const string BadOrMissing = "#REF";

    private const string AsOfScript = """
        rows from T1;
        registry as of row.D;
        output X : t = REGFIELD(REGFIND("GAS", "H2S"), "MOL");
        """;

    private const string PlainScript = """
        rows from T1;
        output X : t = REGFIELD(REGFIND("GAS", "H2S"), "MOL");
        """;

    private const string YearlyScript = """
        rows from T1;
        registry as of row.D;
        output X : t = row.V[asof.Month] * REGFIELD(REGFIND("GAS", "H2S"), "MOL");
        visible var M : t = asof.Month;
        """;

    // ───── AC-1: рядки по різні боки межі версій ─────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-1")]
    public async Task Місячний_документ__рядки_по_різні_боки_межі_бачать_різні_версії()
    {
        var (stand, _) = Monthly(AsOfScript);

        Assert.Equal(1m, ScriptStand.Value((await stand.RunAsync("R1")).Output, "X"));
        Assert.Equal(2m, ScriptStand.Value((await stand.RunAsync("R2")).Output, "X"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-1")]
    public async Task Без_речення__як_досі_знімок_на_кінець_періоду()
    {
        var (stand, loader) = Monthly(PlainScript);

        Assert.Equal(2m, ScriptStand.Value((await stand.RunAsync("R1")).Output, "X"));
        Assert.Equal(2m, ScriptStand.Value((await stand.RunAsync("R2")).Output, "X"));
        Assert.Equal(0, loader.SourceLoads);
        Assert.Equal([new DateOnly(2099, 1, 31)], loader.LoadDates.Distinct());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-1")]
    public async Task Річний_документ_з_колонками_місяців__версія_й_місяць_з_дати_рядка()
    {
        var (stand, _) = Yearly(YearlyScript);

        var (february, februaryValues) = await stand.RunAsync("R1");
        var (april, _) = await stand.RunAsync("R2");

        Assert.Equal(10m * 1m, ScriptStand.Value(february, "X"));
        Assert.Equal(30m * 2m, ScriptStand.Value(april, "X"));
        Assert.Contains(februaryValues, v => v.Name == "M" && v.Text == "2");
        Assert.Contains(february.Trace, s => s.StepCode == "RegistryAsOf" && s.Expression!.Contains("2099-02-10", StringComparison.Ordinal));
    }

    /// <summary>
    /// F1-2: без <c>registry as of</c> Date-колонка в рядку скрипта — порожня, як до лінії (паритет із живими версіями);
    /// дату значенням бачить лише скрипт із реченням.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-F1-2")]
    public async Task Без_речення_Date_колонка_як_до_лінії__з_реченням_дата()
    {
        var (plain, _) = Monthly("rows from T1;\nvar d = row.D;\noutput X : t = REGFIELD(REGFIND(\"GAS\", \"H2S\"), \"MOL\");\n");
        var (withClause, _) = Monthly("rows from T1;\nregistry as of row.D;\nvar d = row.D;\noutput X : t = REGFIELD(REGFIND(\"GAS\", \"H2S\"), \"MOL\");\n");

        var (plainOutput, plainValues) = await plain.RunAsync("R1");
        var (_, clauseValues) = await withClause.RunAsync("R1");

        Assert.Equal("Null", plainValues.Single(v => v.Name == "d").ValueType);
        Assert.Equal(2m, ScriptStand.Value(plainOutput, "X"));
        Assert.Equal("Date", clauseValues.Single(v => v.Name == "d").ValueType);
    }

    // ───── AC-2: порожня дата або дата поза періодом → відхилено з причиною ─────

    [Theory]
    [InlineData("R3", "registryAsOfEmpty")]
    [InlineData("R4", "registryAsOfOutsidePeriod")]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-2")]
    public async Task Місячний__дата_порожня_чи_поза_періодом__рядок_відхилено(string row, string reason)
    {
        var (stand, _) = Monthly(AsOfScript);

        var error = await Assert.ThrowsAsync<DomainException>(() => stand.RunAsync(row));

        AssertRejected(error, reason);
    }

    [Theory]
    [InlineData("R3", "registryAsOfEmpty")]
    [InlineData("R4", "registryAsOfOutsidePeriod")]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-2")]
    public async Task Річний__дата_порожня_чи_поза_періодом__рядок_відхилено(string row, string reason)
    {
        var (stand, _) = Yearly(YearlyScript);

        var error = await Assert.ThrowsAsync<DomainException>(() => stand.RunAsync(row));

        AssertRejected(error, reason);
    }

    // ───── AC-4 (D2): живий перегляд = розрахунок по обидва боки межі ─────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-4")]
    public async Task Перегляд_дорівнює_розрахунку_по_обидва_боки_межі__місячний()
        => await PreviewEqualsRunAsync(Monthly(AsOfScript).Stand, AsOfScript, ["R1", "R2"]);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-4")]
    public async Task Перегляд_дорівнює_розрахунку_по_обидва_боки_межі__річний()
        => await PreviewEqualsRunAsync(Yearly(YearlyScript).Stand, YearlyScript, ["R1", "R2"]);

    // ───── AC-5 (D3): межа лише в транзитивній цілі Lookup ─────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-5")]
    public async Task Версія_змінюється_лише_в_цілі_Lookup_якої_скрипт_не_називає()
    {
        var source = TransitiveSource();
        var stand = new ScriptStand(AsOfScript) { RegistryLoader = new CountingLoader(source) };
        stand.RegistryDefinitions.Add(source.Registries.First(r => r.Id == GasId));
        stand.AddTable("T1").Column("D", CellDataType.Date)
            .Row("R1", ("D", new DateTime(2099, 1, 10)))
            .Row("R2", ("D", new DateTime(2099, 1, 20)));

        Assert.Equal(5m, ScriptStand.Value((await stand.RunAsync("R1")).Output, "X"));
        Assert.Equal(7m, ScriptStand.Value((await stand.RunAsync("R2")).Output, "X"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-5")]
    public void Інтервали_чинності_містять_межі_транзитивних_довідників()
    {
        var intervals = RegistryValidityIntervals.Of(TransitiveSource());

        Assert.Equal([new DateOnly(2099, 1, 15)], intervals.Boundaries);
        Assert.NotEqual(intervals.KeyOf(new DateOnly(2099, 1, 14)), intervals.KeyOf(new DateOnly(2099, 1, 15)));
        Assert.Equal(intervals.KeyOf(new DateOnly(2099, 1, 15)), intervals.KeyOf(new DateOnly(2099, 1, 31)));
    }

    // ───── SGF-flaring AC-5 (огляд b13, F1-3): дата через псевдонім місяця і через GCV-модуль ─────

    /// <summary>
    /// Псевдонім місячного входу (LAND-YEAR L2a) несе дату Date-колонки місяця: рядки по різні боки межі бачать різні версії,
    /// а не «registry as of date is empty» на кожному рядку.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-AC-5")]
    public async Task Псевдонім_місяця_несе_дату_події_в_registry_as_of()
    {
        var loader = new CountingLoader(GasSource(new DateOnly(2099, 1, 15)));
        var stand = new ScriptStand("""
            rows from T1;
            registry as of row.EVENT_DATE;
            output X : t = REGFIELD(REGFIND("GAS", "H2S"), "MOL");
            """) { RegistryLoader = loader };
        stand.RegistryDefinitions.Add(loader.Gas);
        stand.AddTable("T1").Column("D_M01", CellDataType.Date, month: 1).Column("D_M02", CellDataType.Date, month: 2)
            .Row("R1", ("D_M01", new DateTime(2099, 1, 10)), ("D_M02", new DateTime(2099, 2, 20)))
            .Row("R2", ("D_M01", new DateTime(2099, 1, 20)), ("D_M02", new DateTime(2099, 2, 5)));
        stand.WithMonthAlias("T1", "EVENT_DATE", 1, "D_M01").WithMonthAlias("T1", "EVENT_DATE", 2, "D_M02");

        Assert.Equal(1m, ScriptStand.Value((await stand.RunAsync("R1")).Output, "X"));
        Assert.Equal(2m, ScriptStand.Value((await stand.RunAsync("R2")).Output, "X"));
    }

    /// <summary>
    /// GCV-модуль (<see cref="MonthColumnArgumentsModule"/>, таблиці «місяці в колонках»): дата рядка-входу місяця доходить до
    /// <c>registry as of row.GCV_…</c>; місяць лише з датою (без вводу) лишається порожнім — як до лінії.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-AC-5")]
    public async Task GCV_модуль_несе_дату_місяця_в_registry_as_of()
    {
        var module = GcvModule(GcvScript);

        var output = await module.ExecuteAsync(GcvInput("F_Mol"), CancellationToken.None);

        Assert.Equal(10m * 1m, output.Values.Single(v => v.OutputCode == "M01").Value);
        Assert.Equal(10m * 2m, output.Values.Single(v => v.OutputCode == "M02").Value);
        Assert.DoesNotContain(output.Values, v => v.OutputCode == "M03");
        Assert.DoesNotContain(output.Trace, s => s.StepCode == "M03");
    }

    /// <summary>Без речення Date-рядок у GCV-модулі не вхід (F1-2): той самий скрипт без дати рахує як до лінії.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-AC-5")]
    public async Task GCV_модуль_без_речення__дата_не_вхід_і_місяць_лише_з_датою_порожній()
    {
        var module = GcvModule(GcvScript.Replace("registry as of row.GCV_X400_TD_EventDate;\n", string.Empty, StringComparison.Ordinal));

        var output = await module.ExecuteAsync(GcvInput("F_Mol"), CancellationToken.None);

        // Знімок на кінець періоду (2099-12-31) — друга версія для обох місяців.
        Assert.Equal(20m, output.Values.Single(v => v.OutputCode == "M01").Value);
        Assert.Equal(20m, output.Values.Single(v => v.OutputCode == "M02").Value);
        Assert.DoesNotContain(output.Values, v => v.OutputCode == "M03");
    }

    private const string GcvScript =
        "rows from TD;\nregistry as of row.GCV_X400_TD_EventDate;\n"
        + "output X400_TD_Mol : one = row.GCV_X400_TD_Flow * REGFIELD(REGFIND(\"GAS\", \"H2S\"), \"MOL\");\n";

    private const long GcvInstance = 701;
    private const int GcvTable = 71;
    private const int GcvVersion = 7100;
    private const int GcvTemplateVersion = 19;
    private const int GcvPeriod = 209900;

    private static CalculationInput GcvInput(string rowKey)
        => new(GcvDescriptor(), ScriptStand.DocumentId, GcvInstance, new PeriodKey(GcvPeriod), rowKey, []);

    private static MethodologyDescriptor GcvDescriptor()
        => new(71, GcvVersion, "X400", "1.0.0.0", CalculationLevel.Module, NumericMode.Legacy, CalendarMode.Actual,
            Ecr.Domain.Enums.TraceLevel.ErrorsOnly, MethodologyEngine.Script);

    /// <summary>Таблиця TD «місяці в колонках» (M01..M03): рядки EventDate (дата), Flow (ввід), F_Mol (результат).</summary>
    private static MonthColumnArgumentsModule GcvModule(string script)
    {
        var loader = new CountingLoader(GasSource(new DateOnly(2099, 2, 1)));
        var table = new TableDef(1, EcrCode.Create("TD"), Text("TD"), GcvTable, TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, GcvTable);
        for (byte month = 1; month <= 3; month++)
        {
            var column = new ColumnDef(GcvTable, EcrCode.Create($"M0{month}"), Text($"M0{month}"), month, CellDataType.Decimal);
            column.SetMonth(true, month);
            typeof(Entity<int>).GetProperty("Id")!.SetValue(column, 7100 + month);
            table.AddColumn(column);
        }

        var sheet = new SheetDef(GcvTemplateVersion, EcrCode.Create("S1"), Text("S1"), 1);
        sheet.AddTable(table);
        var metadata = NSubstitute.Substitute.For<IMetadataCache>();
        metadata.GetAsync(GcvTemplateVersion, Arg.Any<CancellationToken>()).Returns(new TemplateVersionSnapshot(
            GcvTemplateVersion, 0, [sheet], table.Columns.ToDictionary(c => c.Id), new Dictionary<(int TableDefId, string RowKey), RowDef>()));

        var instance = new TableInstanceRef(GcvInstance, ScriptStand.DocumentId, GcvTable, GcvTemplateVersion, GcvPeriod);
        var rows = NSubstitute.Substitute.For<IRowStore>();
        rows.GetTableInstancesAsync(ScriptStand.DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns([instance]);
        rows.ResolveTableInstanceAsync(GcvInstance, Arg.Any<CancellationToken>()).Returns(instance);
        rows.GetRowIdsAsync(GcvInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, long> { ["EventDate"] = 1, ["Flow"] = 2, ["F_Mol"] = 3 });

        var period = new PeriodKey(GcvPeriod);
        var cells = NSubstitute.Substitute.For<ICellStore>();
        cells.ReadSliceAsync(GcvInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(
        [
            new CellRecord(new CellAddress(period, 1, 7101), GcvTable, new CellValueData { ValueDate = new DateTime(2099, 1, 10) }),
            new CellRecord(new CellAddress(period, 1, 7102), GcvTable, new CellValueData { ValueDate = new DateTime(2099, 3, 10) }),
            new CellRecord(new CellAddress(period, 1, 7103), GcvTable, new CellValueData { ValueDate = new DateTime(2099, 3, 20) }),
            new CellRecord(new CellAddress(period, 2, 7101), GcvTable, new CellValueData { ValueNumeric = 10m }),
            new CellRecord(new CellAddress(period, 2, 7102), GcvTable, new CellValueData { ValueNumeric = 10m }),
        ]);

        var periods = NSubstitute.Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(ScriptStand.DocumentId, GcvPeriod, Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(2099, 1, 1), new DateOnly(2099, 12, 31)));
        var units = NSubstitute.Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase) { ["one"] = new(7, "one", 1, 1m) },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        var store = NSubstitute.Substitute.For<IMethodologyStore>();
        store.GetScriptAsync(GcvVersion, Arg.Any<CancellationToken>())
            .Returns(new MethodologyScript(script, null, MethodologyEngine.Script, false, NumericMode.Legacy));
        store.GetFormulasAsync(GcvVersion, Arg.Any<CancellationToken>()).Returns([]);
        store.GetOutputsAsync(GcvVersion, Arg.Any<CancellationToken>()).Returns([]);
        store.GetSubstancesAsync(GcvVersion, Arg.Any<CancellationToken>()).Returns([]);
        store.GetConstantsAsync(GcvVersion, Arg.Any<CancellationToken>()).Returns([]);
        store.ResolveImportsAsync(GcvVersion, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns([]);

        var registries = NSubstitute.Substitute.For<IRegistryStore>();
        registries.FindDefinitionsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RegistryDef>)[loader.Gas]);
        var bindings = NSubstitute.Substitute.For<ICalculationBindingStore>();
        bindings.ListLookupRegistryIdsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((IReadOnlyList<int>)[]);

        var script1 = new ScriptCalculationModule(
            new Evaluator(new Ecr.Expressions.Functions.FunctionRegistry()), store, new ScriptProgramCache(), new CalendarContext(), units,
            periods, bindings, rows, cells, metadata, new CalculationInputBuilder(cells, metadata, rows),
            NSubstitute.Substitute.For<ICalculationResultStore>(), registryStore: registries, registryLoader: loader);

        return new MonthColumnArgumentsModule(
            new RealFormulaEngine(), store, new ConstantResolver(NSubstitute.Substitute.For<IConstantStore>()), new CalendarContext(),
            units, periods, bindings, cells, metadata, rows, registryStore: registries, registryLoader: loader, scriptModule: script1);
    }

    // ───── AC-8: один завантаж даних на прогін, один знімок на інтервал ─────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-8")]
    public async Task Двісті_прив_язок_і_тисячі_рядків__одне_читання_БД_і_знімок_на_інтервал()
    {
        const int Documents = 200;
        const int RowsPerDocument = 15;

        var (stand, loader) = Yearly(AsOfScript, rows: 0);
        stand.Main.Column("D", CellDataType.Date);
        var start = new DateOnly(2099, 1, 1);
        for (var i = 0; i < RowsPerDocument; i++)
        {
            stand.Main.Row($"E{i}", ("D", start.AddDays(i * 24).ToDateTime(TimeOnly.MinValue)));
        }

        stand.RegistryCache = new RegistrySnapshotCache(registryAsOfUtc: null);
        var inputs = new List<CalculationInput>();
        foreach (var (key, _) in stand.Main.Rows)
        {
            inputs.Add(await stand.InputAsync(key));
        }

        var watch = Stopwatch.StartNew();
        var rows = 0;
        decimal total = 0;
        for (var d = 0; d < Documents; d++)
        {
            var binding = await stand.Module.PrepareAsync(
                stand.Methodology, ScriptStand.DocumentId, new PeriodKey(ScriptStand.PeriodValue), stand.RegistryCache, CancellationToken.None);
            foreach (var input in inputs)
            {
                total += ScriptStand.Value(await stand.Module.ExecuteAsync(binding, input, CancellationToken.None), "X") ?? 0m;
                rows++;
            }
        }

        watch.Stop();
        output.WriteLine($"AC-8: {Documents} bindings × {RowsPerDocument} rows = {rows} rows in {watch.ElapsedMilliseconds} ms; "
                         + $"LoadSourceAsync = {loader.SourceLoads}, builds = {loader.Dated(stand.RegistryCache)?.BuildCount}");

        Assert.Equal(Documents * RowsPerDocument, rows);
        Assert.Equal(1, loader.SourceLoads);
        Assert.Empty(loader.LoadDates);
        Assert.Equal(1, stand.RegistryCache.SourceCount);

        // Межа версій одна (2099-03-01) → два інтервали; знімок на кінець періоду — той самий другий інтервал.
        Assert.Equal(2, loader.Dated(stand.RegistryCache)!.BuildCount);
        Assert.True(total > 0);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-AC-8")]
    public void Знімок_будується_раз_на_інтервал_а_не_на_дату()
    {
        var dated = new DatedRegistrySnapshots(GasSource(new DateOnly(2099, 3, 1)));

        for (var day = new DateOnly(2099, 1, 1); day <= new DateOnly(2099, 12, 31); day = day.AddDays(1))
        {
            _ = dated.Get(day);
        }

        Assert.Equal(2, dated.BuildCount);
        Assert.Equal(1m, dated.Get(new DateOnly(2099, 2, 28)).GetField(5001, "MOL").AsNumber());
        Assert.Equal(BadOrMissing, dated.Get(new DateOnly(2099, 3, 1)).GetField(5001, "MOL").ErrorCode);
    }

    // ───── допоміжне ─────

    private static void AssertRejected(DomainException error, string reason)
    {
        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-0422.categoryRuleFailed", error.Details!["messageKey"]);
        Assert.Equal(reason, error.Details["reason"]);
        Assert.Contains("row.D", (string)error.Details["expression"]!, StringComparison.Ordinal);
    }

    private static async Task PreviewEqualsRunAsync(ScriptStand stand, string text, string[] keys)
    {
        var preview = await stand.PreviewRunner().RunAsync(
            new ScriptPreviewRequest(ScriptStand.VersionId, text, ScriptStand.DocumentId, ScriptStand.PeriodValue, null, 50),
            CancellationToken.None);
        var table = Assert.Single(preview.Tables);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var (_, values) = await stand.RunAsync(key);
            var run = values.Single(v => v.Name == "X").Text;
            var row = table.Rows.Single(r => r.RowKey == key);
            Assert.Equal(run, row.Values.Single(v => v.Name == "X").Text);
            seen.Add(run!);
        }

        // Рядки по різні боки межі — різні числа, і перегляд їх показує саме так.
        Assert.Equal(keys.Length, seen.Count);

        // Відхилений рядок (порожня дата) — «rejected» і в перегляді.
        Assert.Equal("rejected", table.Rows.Single(r => r.RowKey == "R3").State);
    }

    private static (ScriptStand Stand, CountingLoader Loader) Monthly(string script)
    {
        var loader = new CountingLoader(GasSource(new DateOnly(2099, 1, 15)));
        var stand = new ScriptStand(script) { RegistryLoader = loader };
        stand.RegistryDefinitions.Add(loader.Gas);
        stand.AddTable("T1").Column("D", CellDataType.Date)
            .Row("R1", ("D", new DateTime(2099, 1, 10, 8, 30, 0)))
            .Row("R2", ("D", new DateTime(2099, 1, 20)))
            .Row("R3")
            .Row("R4", ("D", new DateTime(2099, 2, 1)));
        return (stand, loader);
    }

    private static (ScriptStand Stand, CountingLoader Loader) Yearly(string script, int rows = 4)
    {
        var loader = new CountingLoader(GasSource(new DateOnly(2099, 3, 1)));
        var stand = new ScriptStand(script)
        {
            RegistryLoader = loader,
            Bounds = (new DateOnly(2099, 1, 1), new DateOnly(2099, 12, 31)),
        };
        stand.RegistryDefinitions.Add(loader.Gas);
        var table = stand.AddTable("T1");
        if (rows == 0)
        {
            return (stand, loader);
        }

        table.Column("D", CellDataType.Date);
        for (byte m = 1; m <= 12; m++)
        {
            table.Column($"V_M{m:D2}", month: m);
        }

        table
            .Row("R1", ("D", new DateTime(2099, 2, 10)), ("V_M02", 10m), ("V_M04", 99m))
            .Row("R2", ("D", new DateTime(2099, 4, 5)), ("V_M02", 99m), ("V_M04", 30m))
            .Row("R3", ("V_M02", 1m))
            .Row("R4", ("D", new DateTime(2100, 1, 1)), ("V_M01", 1m));
        return (stand, loader);
    }

    /// <summary>GAS: V1 до межі (MOL 1), V2 з межі (MOL 2).</summary>
    private static RegistrySnapshotSource GasSource(DateOnly boundary)
    {
        var gas = Definition(GasId, "GAS", temporal: true);
        gas.AddField(Field(GasId, MolField, "MOL", CellDataType.Decimal));
        var key = KeyOf(gas);

        // Дві версії одного ключа «H2S» з вікнами, що не перетинаються.
        var v1 = Entry(5001, GasId, "V1");
        v1.SetValidity(null, boundary);
        var v2 = Entry(5002, GasId, "V2");
        v2.SetValidity(boundary, null);

        return new RegistrySnapshotSource(
            [gas], [key], [v1, v2],
            [Value(5001, MolField, 1m), Key(5001), Value(5002, MolField, 2m), Key(5002)]);
    }

    /// <summary>
    /// GAS (нетемпоральний) — частини композиції кейсу CASE; CASE (скрипт його не називає, завантажувач додав сам) має межу
    /// версій 2099-01-15: до неї видно K1 (і його рядок MOL 5), з неї — K2 (MOL 7).
    /// </summary>
    private static RegistrySnapshotSource TransitiveSource()
    {
        var gasCase = Definition(CaseId, "CASE", temporal: true);
        var gas = Definition(GasId, "GAS", temporal: false);
        gas.AddField(Field(GasId, MolField, "MOL", CellDataType.Decimal));
        var link = Field(GasId, CaseField, "CASE", CellDataType.Lookup);
        link.PointTo(CaseId);
        link.ComposeInto(ParentDeletePolicy.Cascade);
        gas.AddField(link);
        var key = KeyOf(gas);

        var k1 = Entry(6001, CaseId, "K1");
        k1.SetValidity(null, new DateOnly(2099, 1, 15));
        var k2 = Entry(6002, CaseId, "K2");
        k2.SetValidity(new DateOnly(2099, 1, 15), null);
        var r1 = Entry(6101, GasId, "R1");
        var r2 = Entry(6102, GasId, "R2");

        return new RegistrySnapshotSource(
            [gas, gasCase], [key], [k1, k2, r1, r2],
            [
                Value(6101, MolField, 5m), Key(6101), new RegistrySnapshotValue(6101, CaseField, null, null, null, null, 6001, null, null),
                Value(6102, MolField, 7m), Key(6102), new RegistrySnapshotValue(6102, CaseField, null, null, null, null, 6002, null, null),
            ]);
    }

    /// <summary>Первинний ключ GAS — текстове поле <c>K</c> (усі записи тесту мають ключ «H2S»).</summary>
    private static RegistryKeyDef KeyOf(RegistryDef gas)
    {
        var field = Field(GasId, KeyField, "K", CellDataType.String);
        field.Update(Text("K"), KeyField, isRequired: true);
        gas.AddField(field);
        return new RegistryKeyDef(GasId, EcrCode.Create("PK"), Text("PK"), [field], isPrimary: true, ignoreCase: false, 1, DateTime.UnixEpoch);
    }

    private static RegistrySnapshotValue Key(long entry) => new(entry, KeyField, null, "H2S", null, null, null, null, null);

    private static RegistrySnapshotValue Value(long entry, int field, decimal number)
        => new(entry, field, number, null, null, null, null, null, null);

    private static RegistryDef Definition(int id, string code, bool temporal)
    {
        var definition = new RegistryDef(EcrCode.Create(code), Text(code), temporal);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(definition, id);
        return definition;
    }

    private static RegistryFieldDef Field(int registryId, int id, string code, CellDataType type)
    {
        var field = new RegistryFieldDef(registryId, EcrCode.Create(code), Text(code), type, ordinal: id);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(field, id);
        return field;
    }

    private static RegistryEntry Entry(long id, int registryId, string code)
    {
        var entry = new RegistryEntry(registryId, EcrCode.Create(code), Text(code));
        typeof(Entity<long>).GetProperty("Id")!.SetValue(entry, id);
        return entry;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Завантажувач, що лічить читання «БД».</summary>
    private sealed class CountingLoader(RegistrySnapshotSource source) : IRegistrySnapshotLoader
    {
        public RegistryDef Gas => source.Registries.First(r => r.Id == GasId);

        public int SourceLoads { get; private set; }

        public List<DateOnly> LoadDates { get; } = [];

        public DatedRegistrySnapshots? Dated(RegistrySnapshotCache cache)
            => cache.GetOrLoadSourceAsync([GasId], this, CancellationToken.None).Result;

        public Task<IRegistrySnapshot> LoadAsync(
            IReadOnlyCollection<int> registryDefIds, DateOnly businessDate, DateTime? registryAsOfUtc, CancellationToken ct)
        {
            LoadDates.Add(businessDate);
            return Task.FromResult(source.Build(businessDate));
        }

        public Task<IRegistrySnapshotSource> LoadSourceAsync(
            IReadOnlyCollection<int> registryDefIds, DateTime? registryAsOfUtc, CancellationToken ct)
        {
            SourceLoads++;
            return Task.FromResult<IRegistrySnapshotSource>(source);
        }
    }
}
