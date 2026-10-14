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
/// D-426 (REG* у <c>Legacy</c>: <c>EntryRef</c> лише там, де версія читає довідники) і
/// D-427 (маска NaN/±∞ → 0 на КОЖНІЙ формулі <c>Legacy</c>, Q-088).
/// </summary>
public sealed partial class LegacyRegistryAndMaskingTests
{
    private const int MethodologyId = 7;
    private const int VersionId = 71;
    private const int StreamRegistryId = 78;
    private const long StreamEntryId = 9001;
    private const long DocumentId = 703;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Legacy_REGFIELD_над_Lookup_аргументом_читає_поле_через_EntryRef()
    {
        var output = await Run(NumericMode.Legacy, [("Mass", "REGFIELD(@Stream, 'MW') * 2")], MwSnapshot(16m));

        Assert.Equal(32m, Assert.Single(output.Values).Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Legacy_без_REG_не_бачить_EntryRef_голден()
    {
        // Версія без функцій довідників лишається побітно як до D-426: аргумент — число з комірки, не id запису.
        var legacy = await Run(NumericMode.Legacy, [("Mass", "@Stream")], null);
        var strict = await Run(NumericMode.Strict, [("Mass", "@Stream")], null);

        Assert.Equal(7m, Assert.Single(legacy.Values).Value);
        Assert.Equal(StreamEntryId, Assert.Single(strict.Values).Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Legacy_ділення_на_нуль_у_довіднику_дає_0_з_причиною_а_Strict_нічого()
    {
        // Приклад у дусі ECW_C12_03/C11_10: потік ділиться на значення з довідника, там 0.
        var formulas = new[] { ("Mass", "10 / REGFIELD(@Stream, 'MW')") };

        var legacy = await Run(NumericMode.Legacy, formulas, MwSnapshot(0m));
        var strict = await Run(NumericMode.Strict, formulas, MwSnapshot(0m));

        Assert.Equal(0m, Assert.Single(legacy.Values).Value);
        Assert.Contains(legacy.Trace, s => s.Masked == MaskedZeroReason.Infinity);
        Assert.Empty(strict.Values);
        Assert.Contains(strict.Trace, s => s.Masked == MaskedZeroReason.Infinity || s.TraceJson == "#DIV/0");
    }

    private static InMemoryRegistrySnapshot MwSnapshot(decimal mw)
    {
        var snapshot = new InMemoryRegistrySnapshot().AddRegistry("STREAM", ["MW"]);
        snapshot.AddEntry("STREAM", StreamEntryId, "S1", new() { ["MW"] = ExpressionValue.Number(mw) }, ordinal: 1);
        return snapshot;
    }

    private static async Task<CalculationOutput> Run(
        NumericMode mode,
        (string Code, string Expression)[] formulas,
        InMemoryRegistrySnapshot? snapshot,
        string outputCode = "Mass")
    {
        var descriptor = new MethodologyDescriptor(
            MethodologyId, VersionId, Code: "LEGREG", VersionNumber: "1.0.0.0",
            Level: CalculationLevel.Configuration, NumericMode: mode,
            CalendarMode: CalendarMode.Actual, TraceLevel: TraceLevel.ErrorsOnly);

        var list = new List<MethodologyFormula>();
        var order = 1;
        foreach (var (code, expression) in formulas)
        {
            var formula = new MethodologyFormula(VersionId, EcrCode.Create(code), expression);
            formula.SetEvaluationOrder(order++);
            list.Add(formula);
        }

        var store = Substitute.For<IMethodologyStore>();
        store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns(list);
        store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>())
             .Returns([new MethodologyOutput(VersionId, EcrCode.Create(outputCode), 1)]);
        store.GetSubstancesAsync(VersionId, Arg.Any<CancellationToken>()).Returns([]);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(Arg.Any<long>(), 202601, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase) { ["kg"] = new(1, "kg", 1, 1m) },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        var bindings = Substitute.For<ICalculationBindingStore>();
        bindings.ListLookupRegistryIdsAsync(MethodologyId, Arg.Any<CancellationToken>()).Returns([StreamRegistryId]);
        var registries = Substitute.For<IRegistryStore>();
        registries.FindDefinitionsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>()).Returns([]);
        var loader = Substitute.For<IRegistrySnapshotLoader>();
        loader.LoadAsync(default!, default, default, default)
              .ReturnsForAnyArgs(snapshot ?? new InMemoryRegistrySnapshot());

        var module = new GenericCalculationModule(
            new RealFormulaEngine(), store, new ConstantResolver(Substitute.For<IConstantStore>()),
            new CalendarContext(), units, periods, bindings, registries, loader);

        var input = new CalculationInput(
            descriptor, DocumentId, TableInstanceId: 500, PeriodKey: new PeriodKey(202601), SourceRowKey: "R1",
            Arguments: [new CalculationArgument("Stream", 7m, null, null, StreamEntryId)]);

        return await module.ExecuteAsync(input, CancellationToken.None);
    }
}
