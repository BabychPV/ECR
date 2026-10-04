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
/// Аудит L7-06: <c>REGFIELD(@Stream, 'MW')</c> — довідник запису з Lookup-колонки
/// потрапляє в знімок прив'язки.
/// </summary>
/// <remarks>
/// ⛔ Що було. Перелік довідників знімка збирався лише з літеральних кодів
/// <c>REGFIND</c>/<c>REGONE</c>/агрегатів. <c>REGFIELD(@Stream, …)</c> коду не
/// має — у версії без інших функцій довідників знімок був <c>null</c>, і кожен
/// рядок давав <c>#REF</c>: вихід мовчки не писався.
/// </remarks>
public sealed class LookupArgumentRegistryTests
{
    private const int MethodologyId = 6;
    private const int VersionId = 63;
    private const int StreamRegistryId = 77;
    private const long StreamEntryId = 9001;
    private const long DocumentId = 702;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task REGFIELD_над_Lookup_аргументом_читає_поле()
    {
        var bindings = Substitute.For<ICalculationBindingStore>();
        bindings.ListLookupRegistryIdsAsync(MethodologyId, Arg.Any<CancellationToken>()).Returns([StreamRegistryId]);

        var snapshot = new InMemoryRegistrySnapshot().AddRegistry("STREAM", ["MW"]);
        snapshot.AddEntry("STREAM", StreamEntryId, "S1", new() { ["MW"] = ExpressionValue.Number(16.04m) }, ordinal: 1);

        var loader = Substitute.For<IRegistrySnapshotLoader>();
        loader.LoadAsync(
                Arg.Is<IReadOnlyCollection<int>>(ids => ids.Contains(StreamRegistryId)),
                Arg.Any<DateOnly>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
              .Returns(snapshot);

        var output = await Module(bindings, loader).ExecuteAsync(Input(), CancellationToken.None);

        Assert.Equal(32.08m, Assert.Single(output.Values).Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Без_REGFIELD_підстановки_прив_язки_не_читаються()
    {
        var bindings = Substitute.For<ICalculationBindingStore>();
        var loader = Substitute.For<IRegistrySnapshotLoader>();

        await Module(bindings, loader, "@Volume * 2").PrepareAsync(
            Descriptor(), DocumentId, new PeriodKey(202601), CancellationToken.None);

        await bindings.DidNotReceiveWithAnyArgs().ListLookupRegistryIdsAsync(default, default);
        await loader.DidNotReceiveWithAnyArgs().LoadAsync(default!, default, default, default);
    }

    /// <summary>
    /// Рев'ю AN-38, P3-7: у прогоні перелік довідників прив'язки розв'язується раз, а не на
    /// кожен документ (300 документів — 300 однакових пар запитів).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Перелік_довідників_розв_язується_раз_на_прогін_а_не_на_документ()
    {
        var bindings = Substitute.For<ICalculationBindingStore>();
        bindings.ListLookupRegistryIdsAsync(MethodologyId, Arg.Any<CancellationToken>()).Returns([StreamRegistryId]);
        var registries = Substitute.For<IRegistryStore>();
        registries.FindDefinitionsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
                  .Returns([]);
        var loader = Substitute.For<IRegistrySnapshotLoader>();
        loader.LoadAsync(default!, default, default, default).ReturnsForAnyArgs(new InMemoryRegistrySnapshot());

        var module = Module(bindings, loader, "REGFIELD(@Stream, 'MW') + REGFIELD(REGFIND('COMPONENT', @Stream), 'MW')", registries);
        var run = new RegistrySnapshotCache(registryAsOfUtc: null);

        foreach (var document in new long[] { DocumentId, DocumentId + 1, DocumentId + 2 })
        {
            await module.PrepareAsync(Descriptor(), document, new PeriodKey(202601), run, CancellationToken.None);
        }

        await bindings.Received(1).ListLookupRegistryIdsAsync(MethodologyId, Arg.Any<CancellationToken>());
        await registries.ReceivedWithAnyArgs(1).FindDefinitionsAsync(default!, default);
    }

    private static MethodologyDescriptor Descriptor()
        => new(
            MethodologyId,
            VersionId,
            Code: "LOOKUP",
            VersionNumber: "1.0.0.0",
            Level: CalculationLevel.Configuration,
            NumericMode: NumericMode.Strict,
            CalendarMode: CalendarMode.Actual,
            TraceLevel: TraceLevel.Off);

    private static CalculationInput Input()
        => new(
            Descriptor(),
            DocumentId,
            TableInstanceId: 500,
            PeriodKey: new PeriodKey(202601),
            SourceRowKey: "R1",
            Arguments: [new CalculationArgument("Stream", null, null, null, StreamEntryId)]);

    private static GenericCalculationModule Module(
        ICalculationBindingStore bindings,
        IRegistrySnapshotLoader loader,
        string expression = "REGFIELD(@Stream, 'MW') * 2",
        IRegistryStore? registryStore = null)
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create("Mass"), expression);
        formula.SetEvaluationOrder(1);

        var store = Substitute.For<IMethodologyStore>();
        store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns([formula]);
        store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>())
             .Returns([new MethodologyOutput(VersionId, EcrCode.Create("Mass"), 1)]);
        store.GetSubstancesAsync(VersionId, Arg.Any<CancellationToken>()).Returns([]);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(Arg.Any<long>(), 202601, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase) { ["kg"] = new(1, "kg", 1, 1m) },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        var registries = registryStore ?? Substitute.For<IRegistryStore>();
        if (registryStore is null)
        {
            registries.FindDefinitionsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
                      .Returns([]);
        }

        return new GenericCalculationModule(
            new RealFormulaEngine(),
            store,
            new ConstantResolver(Substitute.For<IConstantStore>()),
            new CalendarContext(),
            units,
            periods,
            bindings,
            registries,
            loader);
    }
}
