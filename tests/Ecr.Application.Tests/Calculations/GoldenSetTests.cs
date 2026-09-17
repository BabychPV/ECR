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

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-13.7")]
    public void Розбіжність_ДРУГОЇ_речовини_не_проходить_повз()
    {
        // ⛔ Звірка брала `FirstOrDefault(v => v.OutputCode == code)` зі
        // списку, який іде ПО ОДНОМУ РЯДКУ НА (речовина × вихід). Отже вона
        // міряла лише ПЕРШУ речовину: друга й далі могли бути якими завгодно,
        // і набір лишався зеленим. Для системи, найтвердіша вимога якої —
        // «числа мусять збігатися», сторож, що дивиться на одну речовину з N,
        // гірший за відсутній: він займає місце перевірки, якої тепер ніхто
        // не напише.
        var verdict = GoldenSet.Judge(
            TestCase(expected: 12.5m, tolerance: 0.1m),
            new CalculationOutput(1, null,
            [
                new CalculationOutputValue(1, 901, "gsec", 12.5m, 1),
                new CalculationOutputValue(1, 902, "gsec", 99m, 1),
            ],
            []));

        Assert.False(verdict.IsGreen);

        // ⚠ Розбіжність називає САМЕ ТУ речовину: лічба («розбіжностей — 1»)
        // не каже, у якої з них зламане число.
        var mismatch = Assert.Single(verdict.Mismatches);
        Assert.Equal(902, mismatch.SubstanceEntryId);
        Assert.Equal("gsec", mismatch.OutputCode);
        Assert.Equal(99m, mismatch.Actual);
        Assert.Equal(12.5m, mismatch.Expected);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-13.7")]
    public void Очікування_з_речовиною_міряє_саме_її()
    {
        // ⚠ Речовини одного виходу майже ніколи не мають однакового числа,
        // тож набір мусить уміти оголосити очікування ПОРЕЧОВИННО:
        // `код@ідентифікатор`. Без цього єдиний спосіб покрити N речовин —
        // вимагати від усіх одного числа, а це неправда.
        var testCase = TestCase(
            new Dictionary<string, decimal> { ["gsec@901"] = 12.5m, ["gsec@902"] = 99m },
            tolerance: 0m);

        var output = new CalculationOutput(1, null,
        [
            new CalculationOutputValue(1, 901, "gsec", 12.5m, 1),
            new CalculationOutputValue(1, 902, "gsec", 99m, 1),
        ],
        []);

        Assert.True(GoldenSet.Judge(testCase, output).IsGreen);

        // І та сама звірка червона, щойно розійшлася друга речовина.
        var broken = new CalculationOutput(1, null,
        [
            new CalculationOutputValue(1, 901, "gsec", 12.5m, 1),
            new CalculationOutputValue(1, 902, "gsec", 98m, 1),
        ],
        []);

        var mismatch = Assert.Single(GoldenSet.Judge(testCase, broken).Mismatches);
        Assert.Equal(902, mismatch.SubstanceEntryId);
        Assert.Equal(98m, mismatch.Actual);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-13.7")]
    public void Оголошена_речовина_якої_рушій_не_видав_це_розбіжність()
    {
        // ⛔ Речовина, оголошена набором і зникла з результату, — це
        // розбіжність, а не «нема з чим порівняти»: методологія, що перестала
        // рахувати оголошену речовину, інакше мовчки пройшла б набір.
        var verdict = GoldenSet.Judge(
            TestCase(new Dictionary<string, decimal> { ["gsec@902"] = 99m }, tolerance: 0m),
            new CalculationOutput(1, null,
                [new CalculationOutputValue(1, 901, "gsec", 12.5m, 1)],
                []));

        Assert.False(verdict.IsGreen);

        var mismatch = Assert.Single(verdict.Mismatches);
        Assert.Equal(902, mismatch.SubstanceEntryId);
        Assert.Null(mismatch.Actual);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-13.7")]
    public void Опис_розбіжності_несе_речовину_вихід_і_обидва_числа()
    {
        // ⚠ Саме цей рядок бачить той, хто публікує. «Розбіжностей — 3» без
        // переліку не дає зробити нічого: незрозуміло ні де, ні наскільки.
        var mismatch = new TestCaseMismatch("gsec", 902, 99m, 12.5m, 0.1m);
        var described = mismatch.Describe();

        Assert.Contains("gsec", described, StringComparison.Ordinal);
        Assert.Contains("902", described, StringComparison.Ordinal);
        Assert.Contains("99", described, StringComparison.Ordinal);
        Assert.Contains("12.5", described, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-13.7")]
    public void Перелік_розбіжностей_називає_випадок_набору()
    {
        var verdicts = new[]
        {
            GoldenSet.Judge(TestCase(expected: 1m, tolerance: 0m), Output(1m)),
            GoldenSet.Judge(TestCase(expected: 1m, tolerance: 0m), Output(2m)),
        };

        // Зелений вердикт у перелік не потрапляє — інакше «перелік
        // розбіжностей» містив би те, що зійшлося.
        var divergence = Assert.Single(GoldenSet.Divergences(verdicts));

        Assert.Contains("TC-1", divergence, StringComparison.Ordinal);
        Assert.Contains("gsec", divergence, StringComparison.Ordinal);
    }

    private static MethodologyTestCase TestCase(decimal expected, decimal tolerance)
        => TestCase(new Dictionary<string, decimal> { ["gsec"] = expected }, tolerance);

    private static MethodologyTestCase TestCase(
        IReadOnlyDictionary<string, decimal> expected, decimal tolerance)
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
            expected,
            tolerance);

    private static CalculationOutput Output(decimal value)
        => new(1, null, [new CalculationOutputValue(1, null, "gsec", value, 1)], []);
}
