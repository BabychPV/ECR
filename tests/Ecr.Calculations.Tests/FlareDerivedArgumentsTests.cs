// tests/Ecr.Calculations.Tests/FlareDerivedArgumentsTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// AN-5: похідні аргументи події факела підставляє збирач входу (<c>CalculationInputBuilder</c>), а колонки
/// <c>Total</c>/<c>Duration</c>/<c>FlareUnitMode</c> у таблиці не потрібні.
/// </summary>
/// <remarks>
/// ⛔ Правила — дослівно з CLR: HP → 1, LP → 2, інше → 0 (<c>R_Air_HSE30X_Cont_General.cs:116–129</c>);
/// безперервна подія — <c>Duration = 86400</c> (:140); переривчаста — <c>Duration</c> з атрибута події, а
/// <c>FlareUnitMode</c> з перших двох знаків <c>FlareUnit</c> (<c>R_Air_HSE30X_Int_FG_Calculate.cs:25–49</c>).
/// </remarks>
public sealed class FlareDerivedArgumentsTests
{
    private const int VersionId = 72;
    private const int KilogramUnit = 1;
    private const long DocumentId = 801;
    private const long TableInstance = 502;
    private const int TemplateVersion = 5;
    private const int Period = 202601;
    private const long RowId = 9001;

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("HP", 1)]
    [InlineData("LP", 2)]
    [InlineData("Other", 0)]
    public async Task Режим_факела_з_Pr_Type_подається_без_колонки_FlareUnitMode(string pressureType, int expected)
    {
        var result = await Run(
            "@FlareUnitMode",
            ("Pr_Type", Text(pressureType)),
            ("PurgePilot_Type", Text("Purge")),
            ("FlareName", Text("F1")),
            ("Value", Number(8640m)));

        Assert.Equal((decimal)expected, result);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Безперервна_подія_Duration_доба_а_Total_з_Value()
    {
        var result = await Run(
            "@Total / @Duration",
            ("Pr_Type", Text("HP")),
            ("PurgePilot_Type", Text("Pilot")),
            ("FlareName", Text("D Island - HP Flare")),
            ("Value", Number(864000m)));

        // 864000 / 86400 = 10.
        Assert.Equal(10m, result);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Переривчаста_подія_Duration_з_атрибута_а_режим_з_FlareUnit()
    {
        // Duration — колонка рядка (атрибут події), похідне 86400 її НЕ затирає.
        var duration = await Run(
            "@Total / @Duration",
            ("FlareUnit", Text("LP Flare")),
            ("Value", Number(500m)),
            ("Duration", Number(100m)));

        Assert.Equal(5m, duration);

        var mode = await Run(
            "@FlareUnitMode",
            ("FlareUnit", Text("LP Flare")),
            ("Value", Number(500m)),
            ("Duration", Number(100m)));

        Assert.Equal(2m, mode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Колонка_з_кодом_похідного_аргумента_має_пріоритет()
    {
        var result = await Run(
            "@Total",
            ("Pr_Type", Text("HP")),
            ("Value", Number(1m)),
            ("Total", Number(77m)));

        Assert.Equal(77m, result);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Рядок_без_події_лишається_без_похідних_аргументів()
    {
        var inputs = await Build(
            ("Jan", Number(1m)),
            ("Feb", Number(2m)));

        var input = Assert.Single(inputs);
        Assert.Equal(["Feb", "Jan"], input.Arguments.Select(a => a.ArgumentCode).Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(input.Arguments, a => a.ArgumentCode is "Duration" or "Total" or "FlareUnitMode");
    }

    private static CellValueData Text(string value) => new() { ValueString = value };

    private static CellValueData Number(decimal value) => new() { ValueNumeric = value };

    private static async Task<IReadOnlyList<CalculationInput>> Build(params (string Code, CellValueData Value)[] cells)
    {
        var columns = new Dictionary<int, ColumnDef>();
        var records = new List<CellRecord>();
        var id = 100;
        foreach (var (code, value) in cells)
        {
            columns[id] = new ColumnDef(
                tableDefId: 3,
                EcrCode.Create(code),
                new LocalizedText(new Dictionary<string, string> { ["en"] = code }),
                ordinal: 0,
                value.ValueString is null ? CellDataType.Decimal : CellDataType.String);
            records.Add(new CellRecord(new CellAddress(new PeriodKey(Period), RowId, id), TableDefId: 3, value));
            id++;
        }

        var rows = Substitute.For<IRowStore>();
        rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
            .Returns(new TableInstanceRef(TableInstance, DocumentId, 3, TemplateVersion, Period));
        rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, long> { ["R1"] = RowId });

        var cellStore = Substitute.For<ICellStore>();
        cellStore.ReadSliceAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(records);

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(TemplateVersion, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(
                TemplateVersion,
                0,
                [],
                columns,
                new Dictionary<(int TableDefId, string RowKey), RowDef>()));

        var builder = new CalculationInputBuilder(cellStore, metadata, rows);

        return await builder.BuildAsync(
            TableInstance, ["R1"], new PeriodKey(Period), Descriptor(), CancellationToken.None);
    }

    /// <summary>Збирач → модуль: число єдиного виходу формули.</summary>
    private static async Task<decimal?> Run(string expression, params (string Code, CellValueData Value)[] cells)
    {
        var input = Assert.Single(await Build(cells));

        var formula = new MethodologyFormula(VersionId, EcrCode.Create("Out"), expression);
        formula.SetEvaluationOrder(1);

        var store = Substitute.For<IMethodologyStore>();
        store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns([formula]);
        store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>())
             .Returns([new MethodologyOutput(VersionId, EcrCode.Create("Out"), KilogramUnit)]);
        store.GetSubstancesAsync(VersionId, Arg.Any<CancellationToken>()).Returns([]);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, Period, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

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
            MethodologyId: 8,
            MethodologyVersionId: VersionId,
            Code: "FLARE",
            VersionNumber: "1.0.0.0",
            Level: CalculationLevel.Configuration,
            NumericMode: NumericMode.Legacy,
            CalendarMode: CalendarMode.Actual,
            TraceLevel: TraceLevel.Off);
}
