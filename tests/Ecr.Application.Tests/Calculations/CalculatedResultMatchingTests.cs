// tests/Ecr.Application.Tests/Calculations/CalculatedResultMatchingTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// MS (рецензія F13, A): чисте ядро зіставлення «результат методології → значення колонки Calculated»
/// (<see cref="CalculatedResultMatching"/>) — те саме правило, яким сітка показує число, і яким скрипт читає колонку.
/// </summary>
public sealed class CalculatedResultMatchingTests
{
    private const long Own = 10;
    private const long Foreign = 11;

    private static CalculationResultRow Row(decimal value, long? instance, long? substance = null, int unit = 1)
        => new(5, "R1", "X", value, unit, substance, instance);

    private static CalculatedMatch Resolve(string match, long? own, params CalculationResultRow[] rows)
        => CalculatedResultMatching.Resolve(match, rows, own, () => new Dictionary<string, string?> { ["7"] = "gas" });

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Результат_свого_екземпляра_перекриває_результат_без_екземпляра_а_чужий_ігнорується()
    {
        var own = Resolve("{}", Own, Row(9m, null), Row(5m, Own), Row(7m, Foreign));
        var legacy = Resolve("{}", Own, Row(9m, null), Row(7m, Foreign));
        var onlyForeign = Resolve("{}", Own, Row(7m, Foreign));
        var noOwnInstance = Resolve("{}", null, Row(9m, null), Row(5m, Own));
        var onlyBound = Resolve("{}", null, Row(5m, Own));

        Assert.Equal(new CalculatedMatch(CalculatedMatchKind.Value, 5m, 1), own);
        Assert.Equal(new CalculatedMatch(CalculatedMatchKind.Value, 9m, 1), legacy);
        Assert.Equal(CalculatedMatchKind.NoValue, onlyForeign.Kind);
        Assert.Equal(new CalculatedMatch(CalculatedMatchKind.Value, 9m, 1), noOwnInstance);
        Assert.Equal(CalculatedMatchKind.NoValue, onlyBound.Kind);
        Assert.Equal(CalculatedMatchKind.NoValue, Resolve("{}", Own).Kind);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Речовини_одного_виходу_сумуються_а_дублікат_тієї_самої_речовини_не_подвоюється()
    {
        var sum = Resolve("{}", Own, Row(2m, Own, substance: 1), Row(3m, Own, substance: 2));
        var duplicate = Resolve("{}", Own, Row(2m, Own, substance: 1), Row(2m, Own, substance: 1));

        Assert.Equal(new CalculatedMatch(CalculatedMatchKind.Value, 5m, 1), sum);
        Assert.Equal(new CalculatedMatch(CalculatedMatchKind.Value, 2m, 1), duplicate);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Розбіжність_значення_або_одиниці_однієї_речовини_неоднозначна_а_не_сума()
    {
        Assert.Equal(CalculatedMatchKind.Ambiguous, Resolve("{}", Own, Row(2m, Own, 1), Row(3m, Own, 1)).Kind);
        Assert.Equal(CalculatedMatchKind.Ambiguous, Resolve("{}", Own, Row(2m, Own, 1, unit: 1), Row(2m, Own, 1, unit: 2)).Kind);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Предикат_прив_язки_звужує_рядок_а_універсальний_не_читає_тексту_комірок()
    {
        var hit = Resolve("""{"7":"gas"}""", Own, Row(5m, Own));
        var miss = Resolve("""{"7":"oil"}""", Own, Row(5m, Own));
        var lazy = CalculatedResultMatching.Resolve(
            " { } ", [Row(5m, Own)], Own, () => throw new InvalidOperationException("текст комірок для {} не потрібен"));

        Assert.Equal(CalculatedMatchKind.Value, hit.Kind);
        Assert.Equal(CalculatedMatchKind.NoValue, miss.Kind);
        Assert.Equal(CalculatedMatchKind.Value, lazy.Kind);
        Assert.True(CalculatedResultMatching.IsCatchAll("{ }"));
        Assert.False(CalculatedResultMatching.IsCatchAll("""{"7":"gas"}"""));
    }
}
