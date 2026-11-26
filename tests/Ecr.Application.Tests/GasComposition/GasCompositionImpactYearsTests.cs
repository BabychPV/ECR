// tests/Ecr.Application.Tests/GasComposition/GasCompositionImpactYearsTests.cs
using Ecr.Application.GasComposition;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.GasComposition;

/// <summary>
/// ✎ SGF-flaring AC-3 (DESIGN-sg-flaring §5.8 «double enqueue»): правка версії складу газу (кейс + склад) ставить ОДНУ задачу
/// перерахунку на рік з обома довідниками, а не дві (тоді документ, що читає обидва, перераховувався двічі); рік без
/// залежних не ставиться; зачеплені дати звужують роки.
/// </summary>
public sealed class GasCompositionImpactYearsTests
{
    private const int Case = 81;
    private const int Composition = 82;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-AC-3")]
    public void Кейс_і_склад_з_залежними_в_тому_самому_році__одна_постановка_з_обома_довідниками()
    {
        var dependents = Dependents((Case, [2026]), (Composition, [2026]));

        var years = GasCompositionService.ImpactYears(dependents, affected: null, [Case, Composition]);

        var (year, registries) = Assert.Single(years);
        Assert.Equal(2026, year);
        Assert.Equal([Case, Composition], registries);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-AC-3")]
    public void Довідник_без_залежних_у_році__не_в_постановці__рік_без_залежних__не_ставиться()
    {
        var dependents = Dependents((Case, [2025, 2026]), (Composition, [2026]));

        var years = GasCompositionService.ImpactYears(dependents, affected: null, [Case, Composition]);

        Assert.Equal([(2025, [Case]), (2026, [Case, Composition])], years.Select(y => (y.Year, y.Registries.ToArray())).ToArray());
        Assert.Empty(GasCompositionService.ImpactYears(Dependents(), affected: null, [Case, Composition]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-AC-3")]
    public void Версія_з_березня_2026__лише_роки_від_2026()
    {
        var dependents = Dependents((Case, [2025, 2026, 2027]), (Composition, [2025, 2026, 2027]));

        var years = GasCompositionService.ImpactYears(
            dependents, new GasDateRange(new DateOnly(2026, 3, 1), new DateOnly(2027, 1, 1)), [Case, Composition]);

        Assert.Equal([2026], years.Select(y => y.Year));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-AC-3")]
    public void Ціль_черги__один_довідник_як_досі__кілька_власний_ключ_незалежний_від_порядку()
    {
        Assert.Equal(RegistryImpactRecalculationTarget.ForYear(Case, 2026), RegistryImpactRecalculationTarget.ForYear([Case], 2026));
        Assert.Equal(
            RegistryImpactRecalculationTarget.ForYear([Composition, Case], 2026),
            RegistryImpactRecalculationTarget.ForYear([Case, Composition, Case], 2026));
        Assert.NotEqual(RegistryImpactRecalculationTarget.ForYear(Case, 2026), RegistryImpactRecalculationTarget.ForYear([Case, Composition], 2026));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "SGF-FLARING-AC-3")]
    public void Завдання_черги_несе_всі_довідники_і_переживає_JSON()
    {
        var request = new RegistryImpactRecalculationRequest(Case, [], "gas", 2026, AlsoRegistryDefIds: [Composition, Case]);

        var parsed = RegistryImpactRecalculationRequest.Parse(System.Text.Json.JsonSerializer.Serialize(request, WebJson));

        Assert.Equal([Case, Composition], parsed.AllRegistryDefIds);
        Assert.Equal([Case], new RegistryImpactRecalculationRequest(Case, [], "old payload", 2026).AllRegistryDefIds);
    }

    private static readonly System.Text.Json.JsonSerializerOptions WebJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static Dictionary<int, IReadOnlyList<int>> Dependents(params (int Registry, int[] Years)[] items)
        => items.ToDictionary(i => i.Registry, i => (IReadOnlyList<int>)i.Years);
}
