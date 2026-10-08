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
/// L2-4: <c>REGFIELD(@Stream, 'NAME')</c> лише в правилі категорії константи (формули версії
/// довідників не читають) — довідник Lookup-колонки потрапляє в знімок прив'язки.
/// </summary>
/// <remarks>
/// ⛔ Що було. <c>RegistryCodes</c> розбирала лише <c>MethodologyFormula.Expression</c>; правило
/// категорії (<c>calc.CategoryRule</c>) в перелік не потрапляло, знімок був <c>null</c>, і
/// <c>REGFIELD</c> у правилі давав <c>#REF</c> → <c>categoryRuleFailed</c> на кожному рядку.
///
/// Мутаційний доказ: прибрати правило з <c>RegistryCodes</c> — червоний
/// <see cref="REGFIELD_лише_в_правилі_категорії_читає_поле_довідника"/>.
/// </remarks>
public sealed class CategoryRuleRegistryTests
{
    private const int MethodologyId = 6;
    private const int VersionId = 64;
    private const int StreamRegistryId = 77;
    private const long StreamEntryId = 9001;
    private const long DocumentId = 702;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task REGFIELD_лише_в_правилі_категорії_читає_поле_довідника()
    {
        var bindings = Substitute.For<ICalculationBindingStore>();
        bindings.ListLookupRegistryIdsAsync(MethodologyId, Arg.Any<CancellationToken>()).Returns([StreamRegistryId]);
        var loader = Loader(fuel: "Gas");

        var output = await Module(bindings, loader, "REGFIELD(@Stream, 'NAME')").ExecuteAsync(Input(), CancellationToken.None);

        // Gas = 5, Diesel = 3: ключ категорії прийшов із довідника, а не #REF.
        Assert.Equal(10m, Assert.Single(output.Values).Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task REGFIND_літерал_лише_в_правилі_категорії_потрапляє_в_знімок()
    {
        var bindings = Substitute.For<ICalculationBindingStore>();
        var registries = Substitute.For<IRegistryStore>();
        registries.FindDefinitionsAsync(
                      Arg.Is<IReadOnlyCollection<string>>(codes => codes.Contains("STREAM", StringComparer.OrdinalIgnoreCase)),
                      Arg.Any<CancellationToken>())
                  .Returns([]);
        var loader = Loader(fuel: "Gas");

        await Module(bindings, loader, "REGFIELD(REGFIND('STREAM', @Stream), 'NAME')", registries)
            .PrepareAsync(Descriptor(), DocumentId, new PeriodKey(202601), CancellationToken.None);

        await registries.Received(1).FindDefinitionsAsync(
            Arg.Is<IReadOnlyCollection<string>>(codes => codes.Contains("STREAM", StringComparer.OrdinalIgnoreCase)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Правило_без_функцій_довідників_знімок_не_завантажує()
    {
        var bindings = Substitute.For<ICalculationBindingStore>();
        var loader = Substitute.For<IRegistrySnapshotLoader>();

        await Module(bindings, loader, "@Stream").PrepareAsync(
            Descriptor(), DocumentId, new PeriodKey(202601), CancellationToken.None);

        await bindings.DidNotReceiveWithAnyArgs().ListLookupRegistryIdsAsync(default, default);
        await loader.DidNotReceiveWithAnyArgs().LoadAsync(default!, default, default, default);
    }

    private static IRegistrySnapshotLoader Loader(string fuel)
    {
        var snapshot = new InMemoryRegistrySnapshot().AddRegistry("STREAM", ["NAME"]);
        snapshot.AddEntry("STREAM", StreamEntryId, "S1", new() { ["NAME"] = ExpressionValue.Text(fuel) }, ordinal: 1);

        var loader = Substitute.For<IRegistrySnapshotLoader>();
        loader.LoadAsync(
                Arg.Is<IReadOnlyCollection<int>>(ids => ids.Contains(StreamRegistryId)),
                Arg.Any<DateOnly>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
              .Returns(snapshot);
        return loader;
    }

    private static MethodologyDescriptor Descriptor()
        => new(
            MethodologyId,
            VersionId,
            Code: "CATREG",
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

    private static MethodologyConstant Number(string code, decimal value, string category)
    {
        var constant = new MethodologyConstant(VersionId, EcrCode.Create(code), value, unitId: 23);
        constant.SetScope(category, substanceEntryId: null);
        return constant;
    }

    private static GenericCalculationModule Module(
        ICalculationBindingStore bindings,
        IRegistrySnapshotLoader loader,
        string rule,
        IRegistryStore? registryStore = null)
    {
        // Формула НЕ читає довідників: лише константу категорії, яку вибирає правило.
        var formula = new MethodologyFormula(VersionId, EcrCode.Create("Mass"), "CST.EF * 2");
        formula.SetScope(MethodologyFormulaScope.Substance);
        formula.SetEvaluationOrder(1);

        var store = Substitute.For<IMethodologyStore>();
        store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns([formula]);
        store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>())
             .Returns([new MethodologyOutput(VersionId, EcrCode.Create("Mass"), 1)]);
        store.GetSubstancesAsync(VersionId, Arg.Any<CancellationToken>())
             .Returns([new MethodologySubstance(VersionId, 9101)]);
        store.GetConstantsAsync(VersionId, Arg.Any<CancellationToken>())
             .Returns([Number("EF", 3m, "Diesel"), Number("EF", 5m, "Gas")]);
        store.GetCategoryRuleAsync(VersionId, Arg.Any<CancellationToken>()).Returns(rule);

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
