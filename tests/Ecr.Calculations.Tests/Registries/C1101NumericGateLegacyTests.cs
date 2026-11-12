// tests/Ecr.Calculations.Tests/Registries/C1101NumericGateLegacyTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests.Registries;

/// <summary>
/// C11_01 live (12.11, doc 96, T9a_30, рядок A37 @202609, run 10096): ворота гілок AN-165
/// <c>CONTAINS(@Land_NAE, '66xx')</c> / <c>CONTAINS(@Land_TypeFuelOrStream, …)</c> у <c>Legacy</c>-версії, що читає
/// довідники (REGFIELD/REGFIND), — через СПРАВЖНІЙ <see cref="GenericCalculationModule"/> і його <c>ToValue</c>.
/// </summary>
/// <remarks>
/// ⛔ Що було. Комірки рядка — текст <c>"6362"</c> і <c>"452"</c> (колонки без довідника, <c>ValueRegistryEntryId</c>
/// NULL). <c>ToValue</c> у <c>Legacy</c> подає числовий текст числом (C1-05), <c>CONTAINS</c> на числі давав
/// <c>#VALUE</c>, і кожна формула рядка падала: 0 результатів і 64 помилкові кроки замість 8 результатів.
/// Золотий <c>PARITY_1</c> цього не бачив: його <c>expectedJson</c> — <c>{}</c>, тобто <c>GoldenSet.Judge</c>
/// не звіряв жодного виходу, і #VALUE на всіх кроках лишався «зеленим».
///
/// Мутаційний доказ: повернути в <c>MethodologyFunctions.Contains</c> <c>args[n].Value is not string</c> →
/// <c>#VALUE</c> — червоніють усі випадки з числовим NAE/потоком нижче.
/// </remarks>
public sealed class C1101NumericGateLegacyTests
{
    private const int MethodologyId = 11;
    private const int VersionId = 80;
    private const int StreamRegistryId = 78;
    private const long StreamEntryId = 9001;
    private const long DocumentId = 96;

    // Ворота V4/V5 (AN-165): NAE 6646..6657 — гілка type2, решта — type1_*; потік обирає type1_1/1_2/1_3.
    private static readonly string Nae66 = "(" + string.Join(
        " or ", Enumerable.Range(6646, 12).Select(n => $"CONTAINS(@Land_NAE, '{n}')")) + ")";

    private const string GasLpg =
        "((CONTAINS(@Land_TypeFuelOrStream, 'gas') or CONTAINS(@Land_TypeFuelOrStream, 'Gas') "
        + "or CONTAINS(@Land_TypeFuelOrStream, 'LPG')) and not (CONTAINS(@Land_TypeFuelOrStream, 'sludge')))";

    private const string Diesel452 =
        "(CONTAINS(@Land_TypeFuelOrStream, '452') or CONTAINS(@Land_TypeFuelOrStream, 'Diesel'))";

    private static (string Code, string Expression)[] Gates() =>
    [
        ("T11", $"if(not {Nae66} and {GasLpg}, REGFIELD(@Stream, 'MW') * 1, NULL)"),
        ("T12", $"if(not {Nae66} and {Diesel452}, REGFIELD(@Stream, 'MW') * 2, NULL)"),
        ("T13", $"if(not {Nae66} and not {GasLpg} and not {Diesel452}, REGFIELD(@Stream, 'MW') * 3, NULL)"),
        ("T2", $"if({Nae66}, REGFIELD(@Stream, 'MW') * 4, NULL)"),
    ];

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("6362", "452", "T12", 32)]                  // live A37: числовий NAE + числовий потік → дизель/452
    [InlineData("6362", "LPG (HMB) - СУГ (МТБ)", "T11", 16)]  // числовий NAE + текстовий потік → газ/СУГ
    [InlineData("6362", "1-204 sludge", "T13", 48)]         // числовий NAE, решта потоків
    [InlineData("6646", "452", "T2", 64)]                   // числовий NAE групи 66xx → type2 (бере верх над потоком)
    [InlineData("6657", "LPG (HMB) - СУГ (МТБ)", "T2", 64)]
    [InlineData("0782(009)", "452", "T12", 32)]             // текстовий NAE — як і до виправлення
    public async Task Legacy_ворота_C11_01_з_числовими_входами_обирають_одну_гілку(
        string nae, string stream, string expectedBranch, int expected)
    {
        var output = await Run(nae, stream);

        var value = Assert.Single(output.Values);
        Assert.Equal(expectedBranch, value.OutputCode);
        Assert.Equal((decimal)expected, value.Value);
        Assert.DoesNotContain(output.Trace, s => s.TraceJson == ExpressionErrors.BadValue);
    }

    private static async Task<CalculationOutput> Run(string nae, string stream)
    {
        var descriptor = new MethodologyDescriptor(
            MethodologyId, VersionId, Code: "ECW_C11_01", VersionNumber: "5.0.0.0",
            Level: CalculationLevel.Configuration, NumericMode: NumericMode.Legacy,
            CalendarMode: CalendarMode.Actual, TraceLevel: TraceLevel.ErrorsOnly);

        var formulas = new List<MethodologyFormula>();
        var outputs = new List<MethodologyOutput>();
        var order = 1;
        foreach (var (code, expression) in Gates())
        {
            var formula = new MethodologyFormula(VersionId, EcrCode.Create(code), expression);
            formula.SetEvaluationOrder(order++);
            formulas.Add(formula);
            outputs.Add(new MethodologyOutput(VersionId, EcrCode.Create(code), 1));
        }

        var store = Substitute.For<IMethodologyStore>();
        store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns(formulas);
        store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>()).Returns(outputs);
        store.GetSubstancesAsync(VersionId, Arg.Any<CancellationToken>()).Returns([]);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(Arg.Any<long>(), 202609, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)));

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase) { ["kg"] = new(1, "kg", 1, 1m) },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        // Версія читає довідники (REGFIELD) — як C11_01 V5: binding.Registries є, entryRef у ToValue увімкнено.
        var bindings = Substitute.For<ICalculationBindingStore>();
        bindings.ListLookupRegistryIdsAsync(MethodologyId, Arg.Any<CancellationToken>()).Returns([StreamRegistryId]);
        var registries = Substitute.For<IRegistryStore>();
        registries.FindDefinitionsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns([]);
        var snapshot = new InMemoryRegistrySnapshot().AddRegistry("STREAM", ["MW"]);
        snapshot.AddEntry("STREAM", StreamEntryId, "S1", new() { ["MW"] = ExpressionValue.Number(16m) }, ordinal: 1);
        var loader = Substitute.For<IRegistrySnapshotLoader>();
        loader.LoadAsync(default!, default, default, default).ReturnsForAnyArgs(snapshot);

        var module = new GenericCalculationModule(
            new RealFormulaEngine(), store, new ConstantResolver(Substitute.For<IConstantStore>()),
            new CalendarContext(), units, periods, bindings, registries, loader);

        // Комірки live: текстові колонки без довідника (ValueNumeric і EntryId — NULL), значення — як у doc 96.
        var input = new CalculationInput(
            descriptor, DocumentId, TableInstanceId: 500, PeriodKey: new PeriodKey(202609), SourceRowKey: "LTD-A37",
            Arguments:
            [
                new CalculationArgument("Land_NAE", null, nae, null),
                new CalculationArgument("Land_TypeFuelOrStream", null, stream, null),
                new CalculationArgument("Stream", 7m, null, null, StreamEntryId),
            ]);

        return await module.ExecuteAsync(input, CancellationToken.None);
    }
}
