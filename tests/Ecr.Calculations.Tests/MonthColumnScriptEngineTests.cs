// tests/Ecr.Calculations.Tests/MonthColumnScriptEngineTests.cs
using Ecr.Application.Calculations.Scripting.Migration;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.MethodologyScriptMigration.Conversion;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// MS Етап 7: ті самі сценарії, що в <see cref="MonthColumnArgumentsModuleTests"/> (те саме тіло, ті самі очікування),
/// але на ОБОХ рушіях: формули версії <c>X400</c> або їхній переклад конвертером у скрипт рівня Module.
/// Наприкінці — пряма рівність виходів обох рушіїв на кожному рядку.
/// </summary>
/// <remarks>
/// ⛔ Паритет доводиться виходом (<see cref="CalculationOutput"/>): значення, одиниці, коди колонок місяців, кроки-причини
/// (порожній місяць, замаскований нуль, нульова молекулярна вага). Текст виразу в кроці різниться (старий діалект проти
/// скрипта) і тому не порівнюється.
/// </remarks>
public sealed class MonthColumnScriptEngineTests
{
    private const int VersionId = 400;
    private const int OneUnit = 7;
    private const long DocumentId = 901;
    private const int TemplateVersion = 9;
    private const int Period = 202600;
    private const long InstanceA = 601;
    private const long InstanceB = 602;
    private const long InstancePlain = 603;
    private const long InstanceC = 604;
    private const int TableA = 31;
    private const int TableB = 32;
    private const int TablePlain = 33;
    private const int TableC = 34;

    private static readonly Dictionary<string, long> RowsA = new()
    {
        ["Methane"] = 1, ["Nitrogen"] = 2, ["F_Sum"] = 3, ["F_Nitrogen"] = 4, ["F_Ratio"] = 5, ["F_Double"] = 6,
    };

    private static readonly Dictionary<string, long> RowsB = new() { ["Flow"] = 11, ["F_Scaled"] = 12 };

    private static readonly Dictionary<string, long> RowsC = new()
    {
        ["Gas"] = 21, ["F_MolecularWeight"] = 22, ["F_Inverse"] = 23, ["F_Shifted"] = 24,
    };

    private static readonly (string Code, string Expression)[] Source =
    [
        ("Common_K", "CST.Factor * 2"),
        ("X400_TA_Sum", "@GCV.X400_TA_Methane + @GCV.X400_TA_Nitrogen"),
        ("X400_TA_Nitrogen", "@GCV.X400_TA_Nitrogen - 1"),
        ("X400_TA_Ratio", "Round(!X400_TA_Sum / @GCV.X400_TA_Methane, 2)"),
        ("X400_TA_Double", "!X400_TA_Ratio * 2"),
        ("X400_TB_Scaled", "Round(@GCV.X400_TB_Flow * !Common_K + !X400_TA_Sum, 1)"),
        ("X400_TC_MolecularWeight", "@GCV.X400_TC_Gas * 2"),
        ("X400_TC_Inverse", "1 / !X400_TC_MolecularWeight"),
        ("X400_TC_Shifted", "5 + @GCV.X400_TC_Gas"),
    ];

    public static TheoryData<MethodologyEngine> Engines => new()
    {
        MethodologyEngine.Formulas,
        MethodologyEngine.Script,
    };

    /// <summary>Скрипт, який дає конвертер на формулах <see cref="Source"/>.</summary>
    internal static string ConvertedScript()
    {
        var source = new VersionSource
        {
            MethodologyCode = "X400",
            IsModuleLevel = true,
            NumericMode = NumericMode.Legacy,
            Formulas = [.. Source.Select((f, i) => new SourceFormula(f.Code, f.Expression, i + 1))],
            Constants = [SourceConstant.OfNumber("Factor", 1.5m, unitCode: "one")],
        };
        var result = ScriptConverter.Convert(source);
        Assert.True(result.Text is not null, string.Join("; ", result.Issues.Select(i => $"{i.Severity} {i.Code}: {i.Message}")));
        return result.Text!;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Конвертер_дає_скрипт_без_rows_з_виходами_у_порядку_обчислення()
    {
        var text = ConvertedScript();

        Assert.DoesNotContain("rows from", text, StringComparison.Ordinal);
        Assert.Contains("output X400_TA_Ratio : one = Round(X400_TA_Sum / row.GCV_X400_TA_Methane, 2);", text, StringComparison.Ordinal);
        Assert.Contains("row.GCV_X400_TC_Gas", text, StringComparison.Ordinal);
        Assert.DoesNotContain("@", text, StringComparison.Ordinal);
        Assert.True(
            text.IndexOf("output Common_K", StringComparison.Ordinal) < text.IndexOf("output X400_TB_Scaled", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Нульова_молекулярна_вага_обнуляє_всю_таблицю_місяця_як_стара_система(MethodologyEngine engine)
    {
        var module = NewModule(engine);
        var cells = await RunAll(module, engine);

        Assert.Equal(0m, cells[(InstanceC, "F_MolecularWeight", "M01")]);
        Assert.Equal(0m, cells[(InstanceC, "F_Inverse", "M01")]);
        Assert.Equal(0m, cells[(InstanceC, "F_Shifted", "M01")]);

        foreach (var rowKey in RowsC.Keys.Where(MonthColumnArgumentsModule.IsCalculatedRow))
        {
            var output = await module.ExecuteAsync(Input(engine, InstanceC, rowKey), CancellationToken.None);
            Assert.DoesNotContain(output.Trace, s => s.StepCode == "M01");
            Assert.Equal(OneUnit, Assert.Single(output.Values, v => v.OutputCode == "M01").UnitId);
        }

        Assert.Equal(4m, cells[(InstanceC, "F_MolecularWeight", "M02")]);
        Assert.Equal(0.25m, cells[(InstanceC, "F_Inverse", "M02")]);
        Assert.Equal(7m, cells[(InstanceC, "F_Shifted", "M02")]);

        Assert.False(cells.ContainsKey((InstanceA, "F_Ratio", "M02")));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Кожен_місяць_рахується_зі_своєї_колонки(MethodologyEngine engine)
    {
        var cells = await RunAll(NewModule(engine), engine);

        Assert.Equal(14m, cells[(InstanceA, "F_Sum", "M01")]);
        Assert.Equal(3m, cells[(InstanceA, "F_Nitrogen", "M01")]);
        Assert.Equal(1.4m, cells[(InstanceA, "F_Ratio", "M01")]);
        Assert.Equal(2.8m, cells[(InstanceA, "F_Double", "M01")]);
        Assert.Equal(23m, cells[(InstanceB, "F_Scaled", "M01")]);

        Assert.Equal(2.5m, cells[(InstanceA, "F_Sum", "M02")]);
        Assert.Equal(1.5m, cells[(InstanceA, "F_Nitrogen", "M02")]);
        Assert.Equal(6.3m, cells[(InstanceB, "F_Scaled", "M02")]);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Порожній_місяць_пропускається_без_результатів_і_кроків(MethodologyEngine engine)
    {
        var module = NewModule(engine);
        var cells = await RunAll(module, engine);

        Assert.DoesNotContain(cells.Keys, k => k.Column == "M03");

        foreach (var (instance, rowKey) in AllRows())
        {
            var output = await module.ExecuteAsync(Input(engine, instance, rowKey), CancellationToken.None);
            Assert.DoesNotContain(output.Trace, s => s.StepCode == "M03");
        }
    }

    [Theory]
    [MemberData(nameof(Engines))]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Помилка_формули_лишає_комірку_порожньою_і_не_блокує_решту(MethodologyEngine engine)
    {
        var module = NewModule(engine);
        var cells = await RunAll(module, engine);

        Assert.False(cells.ContainsKey((InstanceA, "F_Ratio", "M02")));
        Assert.False(cells.ContainsKey((InstanceA, "F_Double", "M02")));

        foreach (var rowKey in (string[])["F_Ratio", "F_Double"])
        {
            var output = await module.ExecuteAsync(Input(engine, InstanceA, rowKey), CancellationToken.None);
            var step = Assert.Single(output.Trace, s => s.StepCode == "M02");
            Assert.True(step.TraceJson is not null || step.Masked != MaskedZeroReason.None);
            Assert.Null(output.Values.FirstOrDefault(v => v.OutputCode == "M02"));
        }

        Assert.Equal(2.5m, cells[(InstanceA, "F_Sum", "M02")]);
        Assert.Equal(6.3m, cells[(InstanceB, "F_Scaled", "M02")]);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Колізія_вхід_і_вихід_з_одним_кодом(MethodologyEngine engine)
    {
        var module = NewModule(engine);
        var cells = await RunAll(module, engine);

        Assert.Equal(3m, cells[(InstanceA, "F_Nitrogen", "M01")]);

        foreach (var rowKey in (string[])["Nitrogen", "Methane"])
        {
            var output = await module.ExecuteAsync(Input(engine, InstanceA, rowKey), CancellationToken.None);
            Assert.Empty(output.Values);
            Assert.Empty(output.Trace);
        }

        Assert.All(cells.Keys, k => Assert.StartsWith("F_", k.Row, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Повторний_прогін_дає_той_самий_результат(MethodologyEngine engine)
    {
        var module = NewModule(engine);
        var first = await RunAll(module, engine);
        var sameRun = await RunAll(module, engine);
        var rerun = await RunAll(NewModule(engine), engine);

        Assert.Equal(14, first.Count);
        Assert.Equal(first.OrderBy(k => k.Key), sameRun.OrderBy(k => k.Key));
        Assert.Equal(first.OrderBy(k => k.Key), rerun.OrderBy(k => k.Key));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Рівень_трасування_Off_не_віддає_кроків_і_не_пише_замаскований_нуль(MethodologyEngine engine)
    {
        var module = NewModule(engine);
        var output = await module.ExecuteAsync(Input(engine, InstanceA, "F_Ratio", TraceLevel.Off), CancellationToken.None);

        Assert.Empty(output.Trace);
        Assert.Equal(["M01"], output.Values.Select(v => v.OutputCode).ToArray());
    }

    [Theory]
    [MemberData(nameof(Engines))]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Таблиця_не_місяці_в_колонках_відмова_без_тихого_нуля(MethodologyEngine engine)
    {
        var error = await Assert.ThrowsAsync<DomainException>(
            () => NewModule(engine).ExecuteAsync(Input(engine, InstancePlain, "R1"), CancellationToken.None));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Модуль_бере_рівень_3_для_обох_рушіїв(MethodologyEngine engine)
    {
        var module = NewModule(engine);

        Assert.True(module.CanHandle(Descriptor(engine, CalculationLevel.Module)));
        Assert.False(module.CanHandle(Descriptor(engine, CalculationLevel.Configuration)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Обидва_рушії_дають_однакові_виходи_одиниці_і_кроки_на_кожному_рядку()
    {
        var formulas = NewModule(MethodologyEngine.Formulas);
        var script = NewModule(MethodologyEngine.Script);

        foreach (var (instance, rowKey) in AllRows())
        {
            var expected = await formulas.ExecuteAsync(Input(MethodologyEngine.Formulas, instance, rowKey), CancellationToken.None);
            var actual = await script.ExecuteAsync(Input(MethodologyEngine.Script, instance, rowKey), CancellationToken.None);

            Assert.Equal(
                expected.Values.Select(v => (v.OutputCode, v.Value, v.UnitId, v.Kind)).OrderBy(v => v.OutputCode),
                actual.Values.Select(v => (v.OutputCode, v.Value, v.UnitId, v.Kind)).OrderBy(v => v.OutputCode));

            // Кроки: ті самі місяці, та сама причина (помилка чи замаскований нуль); текст виразу не порівнюється.
            Assert.Equal(
                expected.Trace.Select(s => (s.StepCode, Failed: s.TraceJson is not null, s.Masked)).OrderBy(s => s.StepCode),
                actual.Trace.Select(s => (s.StepCode, Failed: s.TraceJson is not null, s.Masked)).OrderBy(s => s.StepCode));
            Assert.Equal(expected.TableInstanceId, actual.TableInstanceId);
            Assert.Equal(expected.SourceRowKey, actual.SourceRowKey);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Версія_скрипт_не_може_рахуватись_без_модуля_скрипта()
    {
        var module = NewModule(MethodologyEngine.Script, withScriptModule: false);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => module.ExecuteAsync(Input(MethodologyEngine.Script, InstanceA, "F_Sum"), CancellationToken.None));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
    }

    private static IEnumerable<(long Instance, string RowKey)> AllRows()
        => RowsA.Keys.Select(k => (InstanceA, k))
            .Concat(RowsB.Keys.Select(k => (InstanceB, k)))
            .Concat(RowsC.Keys.Select(k => (InstanceC, k)));

    private static async Task<Dictionary<(long Instance, string Row, string Column), decimal>> RunAll(
        MonthColumnArgumentsModule module, MethodologyEngine engine)
    {
        var cells = new Dictionary<(long Instance, string Row, string Column), decimal>();
        foreach (var (instance, rowKey) in AllRows())
        {
            var output = await module.ExecuteAsync(Input(engine, instance, rowKey), CancellationToken.None);
            Assert.Equal(instance, output.TableInstanceId);
            Assert.Equal(rowKey, output.SourceRowKey);
            foreach (var value in output.Values)
            {
                cells.Add((instance, rowKey, value.OutputCode), value.Value);
            }
        }

        return cells;
    }

    private static CalculationInput Input(
        MethodologyEngine engine, long instance, string rowKey, TraceLevel trace = TraceLevel.ErrorsOnly)
        => new(Descriptor(engine, CalculationLevel.Module, trace), DocumentId, instance, new PeriodKey(Period), rowKey, []);

    private static MethodologyDescriptor Descriptor(
        MethodologyEngine engine, CalculationLevel level, TraceLevel trace = TraceLevel.ErrorsOnly)
        => new(
            MethodologyId: 40,
            MethodologyVersionId: VersionId,
            Code: "X400",
            VersionNumber: "1.0.0.0",
            Level: level,
            NumericMode: NumericMode.Legacy,
            CalendarMode: CalendarMode.Actual,
            TraceLevel: trace,
            Engine: engine);

    private static MonthColumnArgumentsModule NewModule(MethodologyEngine engine, bool withScriptModule = true)
    {
        var tableA = Table(TableA, "TA", TableLayoutKind.MonthsInColumns, firstColumnId: 100);
        var tableB = Table(TableB, "TB", TableLayoutKind.MonthsInColumns, firstColumnId: 200);
        var plain = Table(TablePlain, "TP", TableLayoutKind.MonthsInColumns, firstColumnId: 300, months: false);
        var tableC = Table(TableC, "TC", TableLayoutKind.MonthsInColumns, firstColumnId: 400);

        var sheet = new SheetDef(TemplateVersion, EcrCode.Create("S1"), Name("S1"), 1);
        sheet.AddTable(tableA);
        sheet.AddTable(tableB);
        sheet.AddTable(plain);
        sheet.AddTable(tableC);

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(TemplateVersion, Arg.Any<CancellationToken>()).Returns(new TemplateVersionSnapshot(
            TemplateVersion,
            0,
            [sheet],
            sheet.Tables.SelectMany(t => t.Columns).ToDictionary(c => c.Id),
            new Dictionary<(int TableDefId, string RowKey), RowDef>()));

        var instances = new[]
        {
            new TableInstanceRef(InstanceA, DocumentId, TableA, TemplateVersion, Period),
            new TableInstanceRef(InstanceB, DocumentId, TableB, TemplateVersion, Period),
            new TableInstanceRef(InstancePlain, DocumentId, TablePlain, TemplateVersion, Period),
            new TableInstanceRef(InstanceC, DocumentId, TableC, TemplateVersion, Period),
        };
        var rows = Substitute.For<IRowStore>();
        rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(instances);
        foreach (var instance in instances)
        {
            rows.ResolveTableInstanceAsync(instance.TableInstanceId, Arg.Any<CancellationToken>()).Returns(instance);
        }

        rows.GetRowIdsAsync(InstanceC, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(RowsC);
        rows.GetRowIdsAsync(InstanceA, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(RowsA);
        rows.GetRowIdsAsync(InstanceB, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(RowsB);
        rows.GetRowIdsAsync(InstancePlain, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, long> { ["R1"] = 21 });

        var cellStore = Substitute.For<ICellStore>();
        cellStore.ReadSliceAsync(InstanceA, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(
        [
            Cell(TableA, RowsA["Methane"], 100, 10m),
            Cell(TableA, RowsA["Nitrogen"], 100, 4m),
            Cell(TableA, RowsA["F_Nitrogen"], 100, 999m),
            Cell(TableA, RowsA["Methane"], 101, 0m),
            Cell(TableA, RowsA["Nitrogen"], 101, 2.5m),
            Cell(TableA, RowsA["F_Nitrogen"], 101, 999m),
            Cell(TableA, RowsA["F_Nitrogen"], 102, 999m),
        ]);
        cellStore.ReadSliceAsync(InstanceB, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(
        [
            Cell(TableB, RowsB["Flow"], 200, 3m),
            Cell(TableB, RowsB["Flow"], 201, 1.26m),
        ]);
        cellStore.ReadSliceAsync(InstancePlain, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns([]);
        cellStore.ReadSliceAsync(InstanceC, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(
        [
            Cell(TableC, RowsC["Gas"], 400, 0m),
            Cell(TableC, RowsC["Gas"], 401, 2m),
        ]);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, Period, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
            {
                ["one"] = new(OneUnit, "one", 1, 1m),
            },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        var bindingStore = Substitute.For<ICalculationBindingStore>();
        var methodologies = Methodologies();
        ScriptCalculationModule? script = null;
        if (engine == MethodologyEngine.Script && withScriptModule)
        {
            script = new ScriptCalculationModule(
                new Evaluator(new FunctionRegistry()),
                methodologies,
                new ScriptProgramCache(),
                new CalendarContext(),
                units,
                periods,
                bindingStore,
                rows,
                cellStore,
                metadata,
                new CalculationInputBuilder(cellStore, metadata, rows),
                Substitute.For<ICalculationResultStore>());
        }

        return new MonthColumnArgumentsModule(
            new RealFormulaEngine(),
            methodologies,
            new ConstantResolver(Substitute.For<IConstantStore>()),
            new CalendarContext(),
            units,
            periods,
            bindingStore,
            cellStore,
            metadata,
            rows,
            scriptModule: script);
    }

    private static IMethodologyStore Methodologies()
    {
        var formulas = new List<MethodologyFormula>();
        foreach (var (code, expression) in Source)
        {
            var formula = new MethodologyFormula(VersionId, EcrCode.Create(code), expression);
            formula.SetEvaluationOrder(formulas.Count + 1);
            formulas.Add(formula);
        }

        var store = Substitute.For<IMethodologyStore>();
        store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns(formulas);
        store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>()).Returns([]);
        store.GetSubstancesAsync(VersionId, Arg.Any<CancellationToken>()).Returns([]);
        store.GetConstantsAsync(VersionId, Arg.Any<CancellationToken>())
             .Returns([new MethodologyConstant(VersionId, EcrCode.Create("Factor"), 1.5m, OneUnit)]);
        store.GetScriptAsync(VersionId, Arg.Any<CancellationToken>())
             .Returns(new MethodologyScript(ConvertedScript(), null, MethodologyEngine.Script, false, NumericMode.Legacy));
        store.ResolveImportsAsync(VersionId, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns([]);

        return store;
    }

    private static TableDef Table(int id, string code, TableLayoutKind layout, int firstColumnId, bool months = true)
    {
        var table = new TableDef(1, EcrCode.Create(code), Name(code), id, layout, TableRowMode.Fixed);
        SetId(table, id);

        for (byte month = 1; month <= 3; month++)
        {
            var column = new ColumnDef(id, EcrCode.Create($"M0{month}"), Name($"M0{month}"), month, CellDataType.Decimal);
            if (months)
            {
                column.SetMonth(true, month);
            }

            SetId(column, firstColumnId + month - 1);
            table.AddColumn(column);
        }

        return table;
    }

    private static CellRecord Cell(int tableDefId, long rowId, int columnId, decimal value)
        => new(new CellAddress(new PeriodKey(Period), rowId, columnId), tableDefId, new CellValueData { ValueNumeric = value });

    private static LocalizedText Name(string text) => new(new Dictionary<string, string> { ["en"] = text });

    private static void SetId(Entity<int> entity, int id)
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);
}
