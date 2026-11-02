// tests/Ecr.Calculations.Tests/DerivedArgumentsEquivalenceTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Calculations.Tests.Library;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Похідні аргументи (2026-11-02): перерахунок ДО і ПІСЛЯ вирівнювання (<see cref="MethodologyDerivedArguments"/>)
/// дає ті самі числа й ті самі сліди — на необов'язкових аргументах, невжитих елементах списку, маркері «по версії»
/// і замиканні бібліотеки.
/// </summary>
/// <remarks>
/// ⛔ Мутація: у <c>Normalize</c> ставити маркер лише з власного списку формули — червоніє
/// <see cref="Версія_рахує_так_само_до_і_після_вирівнювання"/> на «Jan відсутній»: маркер `Jan?` стоїть у формулі
/// Other, яка Jan не вживає, і без переносу «по версії» Total дає <c>#ARG</c> замість <c>null</c>.
/// </remarks>
public sealed class DerivedArgumentsEquivalenceTests
{
    private const int VersionId = 73;
    private const long DocumentId = 801;

    /// <summary>Версія старого формату: маркери і зайві елементи — у списках.</summary>
    private static readonly (string Code, string Expression, string Csv)[] Legacy =
    [
        ("Total", "@Jan + @Feb", "Jan;Feb"),

        // Маркер `Jan?` у формулі, що Jan НЕ вживає (Land, ECW_C12_02 v1), плюс невжиті CST і `Ghost?`.
        ("Other", "@Feb * 2", "Feb;Jan?;CST.Unused;Ghost?"),
        ("Third", "@Feb * 3 + @Jan", "@Feb;Unused2"),
    ];

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Версія_рахує_так_само_до_і_після_вирівнювання(bool janPresent, bool febPresent)
    {
        var before = await RunAsync(Formulas(Legacy), janPresent, febPresent);

        var normalized = MethodologyDerivedArguments.Normalize(
            [.. Legacy.Select((f, i) => new MethodologyDerivedArguments.Input(i + 1, f.Code, f.Expression, f.Csv))]);
        var after = await RunAsync(
            Formulas([.. normalized.Formulas.Select(f => (f.Code, f.Expression, f.ArgumentsCsv))]), janPresent, febPresent);

        Assert.Equal(Fingerprint(before), Fingerprint(after));

        // Вираз справді змінився (маркер переїхав), тобто порівнюються дві різні версії тексту.
        Assert.Equal("@Jan? + @Feb", normalized.Formulas[0].Expression);
        Assert.Equal("@Feb * 3 + @Jan?", normalized.Formulas[2].Expression);
        Assert.Equal(["Ghost"], normalized.DroppedOptional);
    }

    [Theory]
    [InlineData("OpTime?")]
    [InlineData("OpTime")]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Замикання_бібліотеки_рахує_так_само_до_і_після_вирівнювання(string libraryDeclared)
    {
        var before = await RunLibraryAsync("@OpTime * CST.RHO", libraryDeclared);

        var normalized = MethodologyDerivedArguments.Normalize(
            [new MethodologyDerivedArguments.Input(1, "Common_M", "@OpTime * CST.RHO", libraryDeclared)]);
        var formula = Assert.Single(normalized.Formulas);
        var after = await RunLibraryAsync(formula.Expression, formula.ArgumentsCsv);

        Assert.Equal(Fingerprint(before), Fingerprint(after));
    }

    private static string[] Fingerprint(CalculationOutput output)
        => [
            .. output.Values
                .Select(v => $"V|{v.OutputCode}|{v.SubstanceEntryId}|{v.Value}|{v.Kind}")
                .Order(StringComparer.Ordinal),
            .. output.Trace
                .Select(s => $"T|{s.StepCode}|{s.TraceJson}")
                .Order(StringComparer.Ordinal),
        ];

    private static List<MethodologyFormula> Formulas(IEnumerable<(string Code, string Expression, string Csv)> items)
        => [.. items.Select((f, i) =>
        {
            var formula = new MethodologyFormula(VersionId, EcrCode.Create(f.Code), f.Expression);
            formula.SetEvaluationOrder(i + 1);
            formula.SetArguments(f.Csv);
            return formula;
        })];

    private static async Task<CalculationOutput> RunLibraryAsync(string expression, string csv)
    {
        var stand = new LibraryStand();
        var formulas = LibraryStand.CommonFormulas();
        formulas[0] = LibraryStand.Formula(
            LibraryStand.CommonVersionId, "Common_M", expression, 1, MethodologyFormulaScope.Row, visible: true);
        formulas[0].SetArguments(csv);
        stand.Imports(LibraryStand.CallerVersionId, LibraryStand.CommonContent(formulas));

        return await stand.RunAsync();
    }

    private static async Task<CalculationOutput> RunAsync(List<MethodologyFormula> formulas, bool janPresent, bool febPresent)
    {
        var store = Substitute.For<IMethodologyStore>();
        store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns(formulas);
        store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>())
             .Returns(
             [
                 new MethodologyOutput(VersionId, EcrCode.Create("Total"), unitId: 1),
                 new MethodologyOutput(VersionId, EcrCode.Create("Other"), unitId: 1),
                 new MethodologyOutput(VersionId, EcrCode.Create("Third"), unitId: 1),
             ]);
        store.GetSubstancesAsync(VersionId, Arg.Any<CancellationToken>()).Returns([]);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, 202601, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase) { ["kg"] = new(1, "kg", 1, 1m) },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        var module = new GenericCalculationModule(
            new RealFormulaEngine(),
            store,
            new ConstantResolver(Substitute.For<IConstantStore>()),
            new CalendarContext(),
            units,
            periods,
            Substitute.For<ICalculationBindingStore>());

        List<CalculationArgument> arguments = [];
        if (janPresent)
        {
            arguments.Add(new("Jan", 5m, null, null));
        }

        if (febPresent)
        {
            arguments.Add(new("Feb", 2m, null, null));
        }

        var input = new CalculationInput(
            new MethodologyDescriptor(
                9, VersionId, "EQ_TEST", "1.0.0.0", CalculationLevel.Configuration,
                NumericMode.Strict, CalendarMode.Actual, TraceLevel.Full),
            DocumentId,
            TableInstanceId: 500,
            PeriodKey: new PeriodKey(202601),
            SourceRowKey: "7001001",
            Arguments: arguments);

        return await module.ExecuteAsync(input, CancellationToken.None);
    }
}
