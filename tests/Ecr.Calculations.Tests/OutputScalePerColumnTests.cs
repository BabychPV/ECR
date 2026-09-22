// tests/Ecr.Calculations.Tests/OutputScalePerColumnTests.cs
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
/// Скільки знаків несе результат методології — конфігурація колонки-приймача,
/// а не константа рушія (пряме рішення людини 2026-09-20: «має бути 16 знаків
/// у звіті, це конфігурація комірки»).
/// </summary>
/// <remarks>
/// ⛔ Що було: <c>NumericPolicy.OutputScale => 6</c> — одне число на всю
/// систему. Наслідок був найтихішим із можливих: колонка, оголошена з іншим
/// масштабом, отримувала рівно ті самі шість знаків, а сьомий і далі зникали
/// без жодної помилки — побачити це можна було тільки звіркою з чинною
/// системою, конвеєр якої несе шістнадцять (<c>decimal(25,16)</c>).
///
/// ⚠ Масштаб тут береться з живого <see cref="ColumnDef"/>, а не пишеться
/// числом у словник: перевіряти треба саме те, що знаки задає конфігурація
/// колонки.
/// </remarks>
public sealed class OutputScalePerColumnTests
{
    private const int MethodologyId = 77;
    private const int VersionId = 78;
    private const long DocumentId = 700;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Колонка_зі_Scale_2_обрізає_результат_до_двох_знаків()
    {
        var column = Column(scale: 2);

        var value = await RunAsync(column.Scale);

        // 1 / 3 = 0.3333… у колонці на два знаки — 0.33.
        Assert.Equal(0.33m, value);

        // ⚠ Контроль до мутації «ігнорувати ColumnDef.Scale»: без нього тест
        // пройшов би й на рушії, який усім віддає замовчування.
        Assert.NotEqual(DefaultScaleValue, value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Колонка_без_масштабу_лишає_всі_шістнадцять_знаків()
    {
        // Колонка створена і НЕ оголошує масштабу — рівно те, як виглядає
        // більшість обчислюваних колонок шаблону.
        var column = Column(scale: null);
        Assert.Null(column.Scale);

        var value = await RunAsync(column.Scale);

        // ⛔ Шістнадцять знаків літералом, а не через константу рушія: число —
        // вимога, а не деталь реалізації.
        Assert.Equal(0.3333333333333333m, value);

        // ⛔ І саме те число, яке віддавала константа 6. Воно тут як опис
        // дефекту, а не як очікування: сьомий знак і далі зникали мовчки.
        Assert.NotEqual(0.333333m, value);
    }

    /// <summary>Значення з шістнадцятьма знаками — те, що дає замовчування.</summary>
    private const decimal DefaultScaleValue = 0.3333333333333333m;

    /// <summary>
    /// Обчислювана колонка-приймач із заданим масштабом.
    /// </summary>
    /// <param name="scale"><c>null</c> — колонка масштабу не оголошує.</param>
    private static ColumnDef Column(byte? scale)
    {
        var column = new ColumnDef(
            tableDefId: 3,
            EcrCode.Create("Total"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Total" }),
            ordinal: 0,
            CellDataType.Calculated);

        column.SetNumericFormat(scale is null ? null : (byte)28, scale);
        return column;
    }

    /// <summary>
    /// Виконує методологію з єдиною формулою <c>@Jan / 3</c> на <c>@Jan = 1</c>.
    /// </summary>
    /// <param name="scale">Масштаб колонки-приймача, як його бачить прив'язка.</param>
    /// <remarks>
    /// ⚠ <c>Strict</c> навмисно: наскрізний <c>decimal</c> дає 1/3 з повними
    /// 28 знаками, тож усе, що видно на виході, — робота саме округлення, а не
    /// залишок подвійної точності.
    /// </remarks>
    private static async Task<decimal> RunAsync(byte? scale)
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create("Total"), "@Jan / 3");
        formula.SetEvaluationOrder(1);

        var store = Substitute.For<IMethodologyStore>();
        store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns([formula]);
        store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>())
             .Returns([new MethodologyOutput(VersionId, EcrCode.Create("Total"), unitId: 1)]);
        store.GetSubstancesAsync(VersionId, Arg.Any<CancellationToken>()).Returns([]);

        var bindings = Substitute.For<ICalculationBindingStore>();
        bindings.ListOutputScalesAsync(MethodologyId, Arg.Any<CancellationToken>())
                .Returns(new Dictionary<string, byte?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Total"] = scale,
                });

        var module = new GenericCalculationModule(
            new RealFormulaEngine(),
            store,
            new ConstantResolver(Substitute.For<IConstantStore>()),
            new CalendarContext(),
            Units(),
            Periods(),
            bindings);

        var descriptor = new MethodologyDescriptor(
            MethodologyId,
            MethodologyVersionId: VersionId,
            Code: "SCALE_TEST",
            VersionNumber: "1.0.0.0",
            Level: CalculationLevel.Configuration,
            NumericMode: NumericMode.Strict,
            CalendarMode: CalendarMode.Actual,
            TraceLevel: TraceLevel.Off);

        var input = new CalculationInput(
            descriptor,
            DocumentId,
            TableInstanceId: 500,
            PeriodKey: new PeriodKey(202601),
            SourceRowKey: "7001001",
            Arguments: [new CalculationArgument("Jan", 1m, null, null)]);

        var output = await module.ExecuteAsync(input, CancellationToken.None);

        return Assert.Single(output.Values).Value;
    }

    private static IPeriodStore Periods()
    {
        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, 202601, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        return periods;
    }

    private static IUnitCatalog Units()
    {
        var catalog = Substitute.For<IUnitCatalog>();
        catalog.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
            {
                ["kg"] = new(1, "kg", 1, 1m),
            },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        return catalog;
    }
}
