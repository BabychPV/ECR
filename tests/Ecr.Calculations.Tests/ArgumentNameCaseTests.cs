// tests/Ecr.Calculations.Tests/ArgumentNameCaseTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Регістр імені параметра — на боці, де його справді шукають (крок <c>I.6</c>,
/// директива ПК-1 №05 §2a).
/// </summary>
/// <remarks>
/// ⛔ Асиметрія перенесена з чинної системи дослівно: імена ПАРАМЕТРІВ
/// шукаються <c>StringComparer.OrdinalIgnoreCase</c> (`Utilities.cs:186`), а
/// імена ФУНКЦІЙ — з урахуванням регістру (`EvaluateOptions.IgnoreCase` не
/// виставлений, `Utilities.cs:42`). Другу половину тримає
/// <c>DialectCatalogTests.Регістр_значущий</c>; тут — перша, і саме на
/// БОЙОВОМУ шляху: словник аргументів збирає
/// <c>GenericCalculationModule</c>, а не тестовий контекст, тож перевірка над
/// <c>TestEvaluationContext</c> підтверджувала б лише сама себе.
/// </remarks>
public sealed class ArgumentNameCaseTests
{
    private const int VersionId = 61;
    private const int KilogramUnit = 1;
    private const long DocumentId = 700;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Ім_я_параметра_знаходиться_НЕЗАЛЕЖНО_від_регістру()
    {
        // ⛔ Що ламається при регресії: словник аргументів стає
        // `StringComparer.Ordinal`, і формула `@jan + @FEB`, написана в чинній
        // системі й нею пораховану, у нас дає `null` замість числа — тобто
        // порожній вихід замість викиду. Помилки при цьому не буде: відсутній
        // аргумент повертається як `null`, і рядок просто не з'явиться у звіті.
        var module = new GenericCalculationModule(
            new RealFormulaEngine(),
            Methodologies(),
            new ConstantResolver(Substitute.For<IConstantStore>()),
            new CalendarContext(),
            Units(),
            Periods());

        var descriptor = new MethodologyDescriptor(
            MethodologyId: 6,
            MethodologyVersionId: VersionId,
            Code: "FLARE",
            VersionNumber: "1.0.0.0",
            Level: CalculationLevel.Configuration,
            NumericMode: NumericMode.Legacy,
            CalendarMode: CalendarMode.Actual,
            TraceLevel: TraceLevel.Off);

        // Аргументи названі `Jan`/`Feb`, а формула посилається на `@jan` і
        // `@FEB` — рівно та розбіжність написань, яка в корпусі трапляється в
        // межах однієї методології.
        var input = new CalculationInput(
            descriptor,
            DocumentId,
            TableInstanceId: 500,
            PeriodKey: new PeriodKey(202601),
            SourceRowKey: "7001001",
            Arguments:
            [
                new CalculationArgument("Jan", 2m, null, null),
                new CalculationArgument("Feb", 3m, null, null),
            ]);

        var output = await module.ExecuteAsync(input, CancellationToken.None);

        Assert.Equal(5m, Assert.Single(output.Values).Value);
    }

    private static IMethodologyStore Methodologies()
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create("Total"), "@jan + @FEB");
        formula.SetEvaluationOrder(1);

        var store = Substitute.For<IMethodologyStore>();
        store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns([formula]);
        store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>())
             .Returns([new MethodologyOutput(VersionId, EcrCode.Create("Total"), KilogramUnit)]);

        // Без речовин: прогін один, і константа за речовиною тут ні до чого —
        // перевіряється лише пошук аргументу.
        store.GetSubstancesAsync(VersionId, Arg.Any<CancellationToken>()).Returns([]);

        return store;
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
                ["kg"] = new(KilogramUnit, "kg", 1, 1m),
            },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        return catalog;
    }
}
