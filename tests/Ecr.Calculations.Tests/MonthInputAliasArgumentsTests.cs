// tests/Ecr.Calculations.Tests/MonthInputAliasArgumentsTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// ✎ LAND-YEAR L2a: аргумент методології, що після перебудови Land під рік став псевдонімом місячного входу
/// (<c>cfg.MonthInputAlias</c>), у прогоні місяця m читає колонку місяця m (<c>CalculationInputBuilder</c>).
/// </summary>
/// <remarks>
/// Золотий випадок плану B5 §2.3: <c>@Land_GasConsumption</c> у прогоні 209910 — значення <c>Land_GasConsumption_M10</c>,
/// у прогоні 209909 — <c>_M09</c>; та сама формула методології (<c>GenericCalculationModule</c>) дає число свого місяця.
/// </remarks>
public sealed class MonthInputAliasArgumentsTests
{
    private const int VersionId = 73;
    private const int KilogramUnit = 1;
    private const long DocumentId = 802;
    private const long TableInstance = 503;
    private const int TemplateVersion = 6;
    private const int TableDefId = 4;
    private const long RowId = 9002;
    private const int SepColumn = 109;
    private const int OctColumn = 110;
    private const int RateColumn = 120;

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "D-474")]
    [InlineData(209909, 9.5)]
    [InlineData(209910, 10.25)]
    public async Task Псевдонім_місячного_входу_читає_колонку_місяця_прогону(int period, double expected)
    {
        var input = Assert.Single(await Build(period));

        var gas = Assert.Single(input.Arguments, a => a.ArgumentCode == "Land_GasConsumption");
        Assert.Equal((decimal)expected, gas.Value);
        Assert.Equal(KilogramUnit, gas.UnitId);

        // Коди самих колонок лишаються аргументами, як і досі.
        Assert.Contains(input.Arguments, a => a.ArgumentCode == "Land_GasConsumption_M09");
        Assert.Contains(input.Arguments, a => a.ArgumentCode == "Land_GasConsumption_M10");

        Assert.Equal((decimal)expected * 2m, await RunAsync("@Land_GasConsumption * @Rate", input, period));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "D-474")]
    public async Task Місяць_без_псевдоніма_аргументу_не_має()
    {
        var input = Assert.Single(await Build(209911));

        Assert.DoesNotContain(input.Arguments, a => a.ArgumentCode == "Land_GasConsumption");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "D-474")]
    public async Task Псевдонім_не_перекриває_код_живої_колонки()
    {
        // Хибна конфігурація (публікація її відхиляє): псевдонім Rate на жовтень. Перемагає комірка колонки Rate.
        var input = Assert.Single(await Build(209910, extraAlias: new MonthInputAlias(TableDefId, "Rate", 10, OctColumn)));

        Assert.Equal(2m, Assert.Single(input.Arguments, a => a.ArgumentCode == "Rate").Value);
    }

    private static async Task<IReadOnlyList<CalculationInput>> Build(int period, MonthInputAlias? extraAlias = null)
    {
        var columns = new Dictionary<int, ColumnDef>
        {
            [SepColumn] = Column("Land_GasConsumption_M09", 9),
            [OctColumn] = Column("Land_GasConsumption_M10", 10),
            [RateColumn] = Column("Rate", null),
        };
        var key = new PeriodKey(period);
        IReadOnlyList<CellRecord> records =
        [
            new(new CellAddress(key, RowId, SepColumn), TableDefId, new CellValueData { ValueNumeric = 9.5m }),
            new(new CellAddress(key, RowId, OctColumn), TableDefId, new CellValueData { ValueNumeric = 10.25m }),
            new(new CellAddress(key, RowId, RateColumn), TableDefId, new CellValueData { ValueNumeric = 2m }),
        ];

        var rows = Substitute.For<IRowStore>();
        rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
            .Returns(new TableInstanceRef(TableInstance, DocumentId, TableDefId, TemplateVersion, period));
        rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, long> { ["R1"] = RowId });

        var cellStore = Substitute.For<ICellStore>();
        cellStore.ReadSliceAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(records);

        var aliases = new Dictionary<(int TableDefId, int MonthNumber), IReadOnlyList<MonthInputAlias>>
        {
            [(TableDefId, 9)] = [new MonthInputAlias(TableDefId, "Land_GasConsumption", 9, SepColumn)],
            [(TableDefId, 10)] = extraAlias is null
                ? [new MonthInputAlias(TableDefId, "Land_GasConsumption", 10, OctColumn)]
                : [new MonthInputAlias(TableDefId, "Land_GasConsumption", 10, OctColumn), extraAlias],
        };

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(TemplateVersion, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(
                TemplateVersion,
                0,
                [],
                columns,
                new Dictionary<(int TableDefId, string RowKey), RowDef>())
            {
                MonthInputAliases = aliases,
            });

        return await new CalculationInputBuilder(cellStore, metadata, rows)
            .BuildAsync(TableInstance, ["R1"], key, Descriptor(), CancellationToken.None);
    }

    private static ColumnDef Column(string code, byte? month)
    {
        var column = new ColumnDef(
            TableDefId,
            EcrCode.Create(code),
            new LocalizedText(new Dictionary<string, string> { ["en"] = code }),
            ordinal: 0,
            CellDataType.Decimal);
        column.SetUnit(KilogramUnit);
        if (month is { } m)
        {
            column.SetMonth(true, m);
        }

        return column;
    }

    /// <summary>Вхід → справжній модуль: число єдиного виходу формули методології.</summary>
    private static async Task<decimal?> RunAsync(string expression, CalculationInput input, int period)
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create("Out"), expression);
        formula.SetEvaluationOrder(1);

        var store = Substitute.For<IMethodologyStore>();
        store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns([formula]);
        store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>())
             .Returns([new MethodologyOutput(VersionId, EcrCode.Create("Out"), KilogramUnit)]);
        store.GetSubstancesAsync(VersionId, Arg.Any<CancellationToken>()).Returns([]);

        var periods = Substitute.For<IPeriodStore>();
        var month = new DateOnly(period / 100, period % 100, 1);
        periods.FindPeriodBoundsAsync(DocumentId, period, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(month, month.AddMonths(1).AddDays(-1)));

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
            {
                ["kg"] = new(KilogramUnit, "kg", 1, 1m),
            },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        var module = new GenericCalculationModule(
            new RealFormulaEngine(),
            store,
            new ConstantResolver(Substitute.For<IConstantStore>()),
            new CalendarContext(),
            units,
            periods,
            Substitute.For<ICalculationBindingStore>());

        var output = await module.ExecuteAsync(input, CancellationToken.None);

        return Assert.Single(output.Values).Value;
    }

    private static MethodologyDescriptor Descriptor()
        => new(
            MethodologyId: 9,
            MethodologyVersionId: VersionId,
            Code: "LANDGAS",
            VersionNumber: "1.0.0.0",
            Level: CalculationLevel.Configuration,
            NumericMode: NumericMode.Legacy,
            CalendarMode: CalendarMode.Actual,
            TraceLevel: TraceLevel.Off);
}
