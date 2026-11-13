// tests/Ecr.Calculations.Tests/MonthColumnArgumentsModuleTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// HSE400: модуль рівня 3, у якого набір аргументів — колонка місяця, а результат лягає в рядок <c>F_…</c>.
/// </summary>
/// <remarks>
/// Синтетична методологія <c>X400</c> на двох таблицях (<c>TA</c>, <c>TB</c>) і трьох місяцях:
/// <code>
/// Common_K       = CST.Factor * 2                                  (проміжна, рядка немає)
/// X400_TA_Sum      = @GCV.X400_TA_Methane + @GCV.X400_TA_Nitrogen
/// X400_TA_Nitrogen = @GCV.X400_TA_Nitrogen - 1                     (колізія: вхід і вихід Nitrogen)
/// X400_TA_Ratio    = Round(!X400_TA_Sum / @GCV.X400_TA_Methane, 2)
/// X400_TA_Double   = !X400_TA_Ratio * 2
/// X400_TB_Scaled   = Round(@GCV.X400_TB_Flow * !Common_K + !X400_TA_Sum, 1)
/// </code>
/// ⛔ Мутаційні докази (локально, не в коміті):
/// читати рядки <c>F_…</c> як входи — <see cref="Порожній_місяць_пропускається_без_результатів_і_кроків"/>
/// червоний; шукати рядок результату за <c>X</c> без префікса <c>F_</c> —
/// <see cref="Колізія_вхід_і_вихід_з_одним_кодом"/> червоний; прибрати <c>tainted</c> з <c>broken</c> —
/// <see cref="Помилка_формули_лишає_комірку_порожньою_і_не_блокує_решту"/> червоний; підставити код формули замість
/// коду колонки місяця — <see cref="Кожен_місяць_рахується_зі_своєї_колонки"/> червоний; вимкнути правило нульової
/// молекулярної ваги (<c>ZeroMolecularWeightTables</c> завжди порожній) —
/// <see cref="Нульова_молекулярна_вага_обнуляє_всю_таблицю_місяця_як_стара_система"/> червоний (11.11: 2 з 9 червоні).
/// </remarks>
public sealed class MonthColumnArgumentsModuleTests
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

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Нульова_молекулярна_вага_обнуляє_всю_таблицю_місяця_як_стара_система()
    {
        // TC, січень: Gas = 0 → MolecularWeight = 0. Стара система (CalculationBaseAdv.cs:292–301) пише 0 у всі
        // результати частини: і в ділення на нуль (Inverse), і в рядок із константою (Shifted = 5 + 0).
        var module = NewModule();
        var cells = await RunAll(module);

        Assert.Equal(0m, cells[(InstanceC, "F_MolecularWeight", "M01")]);
        Assert.Equal(0m, cells[(InstanceC, "F_Inverse", "M01")]);
        Assert.Equal(0m, cells[(InstanceC, "F_Shifted", "M01")]);

        foreach (var rowKey in RowsC.Keys.Where(MonthColumnArgumentsModule.IsCalculatedRow))
        {
            var output = await module.ExecuteAsync(Input(InstanceC, rowKey), CancellationToken.None);
            Assert.DoesNotContain(output.Trace, s => s.StepCode == "M01");
            Assert.Equal(OneUnit, Assert.Single(output.Values, v => v.OutputCode == "M01").UnitId);
        }

        // Лютий: Gas = 2 → звичайний розрахунок.
        Assert.Equal(4m, cells[(InstanceC, "F_MolecularWeight", "M02")]);
        Assert.Equal(0.25m, cells[(InstanceC, "F_Inverse", "M02")]);
        Assert.Equal(7m, cells[(InstanceC, "F_Shifted", "M02")]);

        // Правило не виходить за межі таблиці: у TA лютневе ділення на нуль лишається порожнім.
        Assert.False(cells.ContainsKey((InstanceA, "F_Ratio", "M02")));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Кожен_місяць_рахується_зі_своєї_колонки()
    {
        var cells = await RunAll(NewModule());

        Assert.Equal(14m, cells[(InstanceA, "F_Sum", "M01")]);
        Assert.Equal(3m, cells[(InstanceA, "F_Nitrogen", "M01")]);
        Assert.Equal(1.4m, cells[(InstanceA, "F_Ratio", "M01")]);
        Assert.Equal(2.8m, cells[(InstanceA, "F_Double", "M01")]);

        // Таблиця TB читає проміжну формулу з константою і формулу сусідньої таблиці: 3 × (1.5 × 2) + 14.
        Assert.Equal(23m, cells[(InstanceB, "F_Scaled", "M01")]);

        Assert.Equal(2.5m, cells[(InstanceA, "F_Sum", "M02")]);
        Assert.Equal(1.5m, cells[(InstanceA, "F_Nitrogen", "M02")]);

        // Round(1.26 × 3 + 2.5, 1) = Round(6.28, 1).
        Assert.Equal(6.3m, cells[(InstanceB, "F_Scaled", "M02")]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Порожній_місяць_пропускається_без_результатів_і_кроків()
    {
        // У березні заповнена лише комірка рядка F_Nitrogen — це не вхід, місяць порожній.
        var module = NewModule();
        var cells = await RunAll(module);

        Assert.DoesNotContain(cells.Keys, k => k.Column == "M03");

        foreach (var (instance, rowKey) in AllRows())
        {
            var output = await module.ExecuteAsync(Input(instance, rowKey), CancellationToken.None);
            Assert.DoesNotContain(output.Trace, s => s.StepCode == "M03");
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Помилка_формули_лишає_комірку_порожньою_і_не_блокує_решту()
    {
        // Лютий: Methane = 0 → ділення на нуль у Ratio; Double залежить від Ratio.
        var module = NewModule();
        var cells = await RunAll(module);

        Assert.False(cells.ContainsKey((InstanceA, "F_Ratio", "M02")));
        Assert.False(cells.ContainsKey((InstanceA, "F_Double", "M02")));

        foreach (var rowKey in (string[])["F_Ratio", "F_Double"])
        {
            var output = await module.ExecuteAsync(Input(InstanceA, rowKey), CancellationToken.None);
            var step = Assert.Single(output.Trace, s => s.StepCode == "M02");
            Assert.True(step.TraceJson is not null || step.Masked != MaskedZeroReason.None);
            Assert.Null(output.Values.FirstOrDefault(v => v.OutputCode == "M02"));
        }

        // Решта формул того ж місяця пораховані.
        Assert.Equal(2.5m, cells[(InstanceA, "F_Sum", "M02")]);
        Assert.Equal(6.3m, cells[(InstanceB, "F_Scaled", "M02")]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Колізія_вхід_і_вихід_з_одним_кодом()
    {
        // Рядок F_Nitrogen уже має значення 999 у всіх місяцях: формула читає ВХІД Nitrogen (4), а не його.
        var module = NewModule();
        var cells = await RunAll(module);

        Assert.Equal(3m, cells[(InstanceA, "F_Nitrogen", "M01")]);

        // У рядок-вхід нічого не пишеться.
        foreach (var rowKey in (string[])["Nitrogen", "Methane"])
        {
            var output = await module.ExecuteAsync(Input(InstanceA, rowKey), CancellationToken.None);
            Assert.Empty(output.Values);
            Assert.Empty(output.Trace);
        }

        Assert.All(cells.Keys, k => Assert.StartsWith("F_", k.Row, StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Повторний_прогін_дає_той_самий_результат()
    {
        var module = NewModule();
        var first = await RunAll(module);
        var sameRun = await RunAll(module);
        var rerun = await RunAll(NewModule());

        Assert.Equal(14, first.Count);
        Assert.Equal(first.OrderBy(k => k.Key), sameRun.OrderBy(k => k.Key));
        Assert.Equal(first.OrderBy(k => k.Key), rerun.OrderBy(k => k.Key));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Рівень_трасування_Off_не_віддає_кроків_і_не_пише_замаскований_нуль()
    {
        var module = NewModule();
        var output = await module.ExecuteAsync(Input(InstanceA, "F_Ratio", TraceLevel.Off), CancellationToken.None);

        Assert.Empty(output.Trace);
        Assert.Equal(["M01"], output.Values.Select(v => v.OutputCode).ToArray());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Таблиця_не_місяці_в_колонках_відмова_без_тихого_нуля()
    {
        var error = await Assert.ThrowsAsync<DomainException>(
            () => NewModule().ExecuteAsync(Input(InstancePlain, "R1"), CancellationToken.None));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Модуль_бере_лише_рівень_3_а_одиничним_модулем_лишається_Generic()
    {
        var module = NewModule();

        Assert.True(module.CanHandle(Descriptor(CalculationLevel.Module)));
        Assert.False(module.CanHandle(Descriptor(CalculationLevel.Configuration)));

        // Публікація й симуляція беруть одиничний ICalculationModule — останню реєстрацію.
        var registered = new ServiceCollection().AddEcrCalculations()
            .Where(d => d.ServiceType == typeof(ICalculationModule))
            .Select(d => d.ImplementationType)
            .ToArray();

        Assert.Contains(typeof(MonthColumnArgumentsModule), registered);
        Assert.Equal(typeof(GenericCalculationModule), registered[^1]);
    }

    private static IEnumerable<(long Instance, string RowKey)> AllRows()
        => RowsA.Keys.Select(k => (InstanceA, k))
            .Concat(RowsB.Keys.Select(k => (InstanceB, k)))
            .Concat(RowsC.Keys.Select(k => (InstanceC, k)));

    /// <summary>Як оркестратор: виклик на кожен рядок обох таблиць; результат — комірки (таблиця, рядок, колонка).</summary>
    private static async Task<Dictionary<(long Instance, string Row, string Column), decimal>> RunAll(
        MonthColumnArgumentsModule module)
    {
        var cells = new Dictionary<(long Instance, string Row, string Column), decimal>();
        foreach (var (instance, rowKey) in AllRows())
        {
            var output = await module.ExecuteAsync(Input(instance, rowKey), CancellationToken.None);
            Assert.Equal(instance, output.TableInstanceId);
            Assert.Equal(rowKey, output.SourceRowKey);
            foreach (var value in output.Values)
            {
                cells.Add((instance, rowKey, value.OutputCode), value.Value);
            }
        }

        return cells;
    }

    private static CalculationInput Input(long instance, string rowKey, TraceLevel trace = TraceLevel.ErrorsOnly)
        => new(Descriptor(CalculationLevel.Module, trace), DocumentId, instance, new PeriodKey(Period), rowKey, []);

    private static MethodologyDescriptor Descriptor(CalculationLevel level, TraceLevel trace = TraceLevel.ErrorsOnly)
        => new(
            MethodologyId: 40,
            MethodologyVersionId: VersionId,
            Code: "X400",
            VersionNumber: "1.0.0.0",
            Level: level,
            NumericMode: NumericMode.Legacy,
            CalendarMode: CalendarMode.Actual,
            TraceLevel: trace);

    private static MonthColumnArgumentsModule NewModule()
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

        var rows = Substitute.For<IRowStore>();
        rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(
        [
            new TableInstanceRef(InstanceA, DocumentId, TableA, TemplateVersion, Period),
            new TableInstanceRef(InstanceB, DocumentId, TableB, TemplateVersion, Period),
            new TableInstanceRef(InstancePlain, DocumentId, TablePlain, TemplateVersion, Period),
            new TableInstanceRef(InstanceC, DocumentId, TableC, TemplateVersion, Period),
        ]);
        rows.GetRowIdsAsync(InstanceC, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(RowsC);
        rows.GetRowIdsAsync(InstanceA, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(RowsA);
        rows.GetRowIdsAsync(InstanceB, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(RowsB);
        rows.GetRowIdsAsync(InstancePlain, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, long> { ["R1"] = 21 });

        // Колонки: 100/200 — M01, 101/201 — M02, 102/202 — M03.
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

        return new MonthColumnArgumentsModule(
            new RealFormulaEngine(),
            Methodologies(),
            new ConstantResolver(Substitute.For<IConstantStore>()),
            new CalendarContext(),
            units,
            periods,
            Substitute.For<ICalculationBindingStore>(),
            cellStore,
            metadata,
            rows);
    }

    private static IMethodologyStore Methodologies()
    {
        (string Code, string Expression)[] source =
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

        var formulas = new List<MethodologyFormula>();
        foreach (var (code, expression) in source)
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
