using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Що означає «золотий набір зійшовся» (<c>ФВ-13.7</c>, <c>ФВ-9.12</c>).
/// </summary>
/// <remarks>
/// ⛔ До винесення в <see cref="GoldenSet"/> це порівняння жило приватним
/// методом усередині публікації, і побачити його результат можна було лише
/// спробувавши опублікувати. Прогін «без запису» (<c>ФВ-13.5</c>) проганяв ті
/// самі тести і **жодного разу не звіряв їх з очікуваннями**: числа показував,
/// а «зійшлося чи ні» — ні. Саме на це питання дивляться перед публікацією, і
/// саме воно лишалося без відповіді до самої відмови.
/// </remarks>
public sealed class GoldenSetTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-13.7")]
    public void Збіг_у_межах_допуску_зелений()
    {
        var verdict = GoldenSet.Judge(TestCase(expected: 12.5m, tolerance: 0.1m), Output(12.55m));

        Assert.True(verdict.IsGreen);
        Assert.Empty(verdict.Mismatches);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-13.7")]
    public void Вихід_за_допуск_червоний_і_названий_числами()
    {
        // ⚠ Розбіжність несе ОБИДВА числа і допуск. «Тест не пройшов» без них
        // означає, що методолог відкриває вираз і гадає, у який бік помилка.
        var verdict = GoldenSet.Judge(TestCase(expected: 12.5m, tolerance: 0.1m), Output(13m));

        Assert.False(verdict.IsGreen);

        var mismatch = Assert.Single(verdict.Mismatches);
        Assert.Equal(13m, mismatch.Actual);
        Assert.Equal(12.5m, mismatch.Expected);
        Assert.Equal(0.1m, mismatch.Tolerance);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-13.7")]
    public void Відсутній_вихід_це_розбіжність_а_не_відсутність_предмета()
    {
        // ⛔ Методологія, яка перестала рахувати оголошений вихід, мовчки
        // проходила б набір, якби відсутність нічого не означала — і зникле
        // число помітили б у звіті.
        var verdict = GoldenSet.Judge(
            TestCase(expected: 12.5m, tolerance: 0m),
            new CalculationOutput(1, null, [], []));

        Assert.False(verdict.IsGreen);
        Assert.Null(Assert.Single(verdict.Mismatches).Actual);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.12")]
    public void Порожній_набір_не_зелений()
    {
        // ⛔ «Тестів немає, отже все гаразд» — саме та підміна, через яку
        // публікація без перевірки виглядає як публікація з перевіркою.
        Assert.False(GoldenSet.IsGreen([]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.12")]
    public void Один_червоний_робить_червоним_увесь_набір()
    {
        var verdicts = new[]
        {
            GoldenSet.Judge(TestCase(expected: 1m, tolerance: 0m), Output(1m)),
            GoldenSet.Judge(TestCase(expected: 1m, tolerance: 0m), Output(2m)),
        };

        Assert.False(GoldenSet.IsGreen(verdicts));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-13.7")]
    public void Нульовий_допуск_вимагає_точного_збігу()
    {
        // ⚠ Нуль — це «точно», а не «приблизно нуль». Найменше відхилення при
        // нульовому допуску мусить бути червоним: інакше допуск нічого не
        // означає, і його ніхто не задаватиме свідомо.
        Assert.False(
            GoldenSet.Judge(TestCase(expected: 1m, tolerance: 0m), Output(1.0000000001m)).IsGreen);
    }

    private static MethodologyTestCase TestCase(decimal expected, decimal tolerance)
        => new(
            "TC-1",
            new CalculationInput(
                new MethodologyDescriptor(1, 1, "M", "1.0", CalculationLevel.Configuration,
                    NumericMode.Strict, CalendarMode.Actual, TraceLevel.Off),
                DocumentId: 1,
                TableInstanceId: 1,
                PeriodKey: new PeriodKey(202601),
                SourceRowKey: null,
                Arguments: []),
            new Dictionary<string, decimal> { ["gsec"] = expected },
            tolerance);

    private static CalculationOutput Output(decimal value)
        => new(1, null, [new CalculationOutputValue(1, null, "gsec", value, 1)], []);
}
