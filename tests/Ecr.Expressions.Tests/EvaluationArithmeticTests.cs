using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// Дві арифметики: `Legacy` — <c>double</c>, `Strict` — <c>decimal</c>
/// (директива №05 §5, `B13` §8 п.2).
/// </summary>
/// <remarks>
/// ⛔ До цього розділення обидва режими рахували в <c>decimal</c>, а різниця
/// між ними була в **моменті** округлення — і цей момент я вигадав сам.
/// Наслідок був би найтихішим із можливих: `Legacy` рахував би **краще** за
/// чинну систему, звірка розійшлася б у шостому знаку на кожній формулі, і
/// пояснити це не зміг би ніхто.
///
/// ⚠ Кожне очікуване число нижче звірене з прогоном справжнього NCalc 1.3.8
/// (`tests/Ecr.Legacy.Probe`, `docs/legacy-ncalc-1.3.8.md`). Це не «правильні»
/// числа — це **чинні**.
/// </remarks>
public sealed class EvaluationArithmeticTests
{
    private static readonly LegacyDoubleArithmetic Legacy = new();
    private static readonly StrictDecimalArithmetic Strict = new();

    private static ExpressionValue Num(double v) => ExpressionValue.LegacyNumber(v);

    private static ExpressionValue Dec(decimal v) => ExpressionValue.Number(v);

    // ─────────────────────────────────────────────────────────────────────────
    // Округлення — головна відмінність, і вона протилежна
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(2.5, 2.0)]
    [InlineData(0.5, 0.0)]
    [InlineData(1.5, 2.0)]
    [InlineData(3.5, 4.0)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.16d")]
    public void Legacy_округлює_банківськи(double value, double expected)
    {
        // ⛔ Не «поки не виміряно», а факт: чинна збірка створює
        // `new Expression(key)` без `EvaluateOptions` (`Utilities.cs:42`),
        // отже `RoundAwayFromZero` не виставлений.
        Assert.Equal(expected, Legacy.Round(Num(value), 0).AsDouble());
    }

    [Theory]
    [InlineData(2.5, 3)]
    [InlineData(0.5, 1)]
    [InlineData(1.5, 2)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.16a")]
    public void Strict_округлює_від_нуля(decimal value, decimal expected)
    {
        // Діалект шаблонів — спадок Excel, а Excel округлює половину від нуля.
        Assert.Equal(expected, Strict.Round(Dec(value), 0).AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Два_режими_дають_РІЗНІ_числа_на_тому_самому_вході()
    {
        // ⚠ Це і є сенс розділення. Якщо колись обидва дадуть однаково —
        // або хтось звів їх в одну реалізацію, або зламав правило округлення.
        Assert.NotEqual(
            Legacy.Round(Num(2.5), 0).AsDouble(),
            (double?)Strict.Round(Dec(2.5m), 0).AsNumber());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Ділення: у Legacy немає ані цілочисельного, ані #DIV/0
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(365, 31, 11.774193548387096)]
    [InlineData(12, 163, 0.0736196319018405)]
    [InlineData(3747, 10000, 0.3747)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Legacy_ділить_дробово_навіть_цілі(double a, double b, double expected)
    {
        // ⛔ Директива №05 §7 вимагала відтворити `365/31 = 11`. Замір показав,
        // що такої поведінки в NCalc 1.3.8 немає **ні з констант, ні з
        // літералів**: пастка 1 скасована цілком.
        var result = Legacy.Binary(Num(a), Num(b), BinaryOperator.Divide).AsDouble();

        Assert.Equal(expected, result!.Value, 12);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Legacy_ділення_на_нуль_дає_нескінченність_а_не_помилку()
    {
        // ⛔ Виміряно: `1/0 → ∞`. Повернути тут `#DIV/0` означало б дати ІНШИЙ
        // результат, ніж еталон: чинна система маскує саме нескінченність, і
        // маскує її **в нуль**. Ознаку `MaskedZero` ставить той, хто зберігає.
        var result = Legacy.Binary(Num(1), Num(0), BinaryOperator.Divide);

        Assert.True(double.IsPositiveInfinity(result.AsDouble()!.Value));
        Assert.False(result.IsError);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Strict_ділення_на_нуль_дає_помилку_значенням()
    {
        // У `decimal` нескінченності не існує, а мовчазний нуль був би
        // невідрізненний від справжнього (`02b` §6.4).
        var result = Strict.Binary(Dec(1m), Dec(0m), BinaryOperator.Divide);

        Assert.True(result.IsError);
        Assert.Equal(ExpressionErrors.DivideByZero, result.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Legacy_повертає_NaN_значенням()
    {
        // `Sqrt(-1)` і `0/0` у чинній системі — значення, а не винятки.
        Assert.True(double.IsNaN(Legacy.Unary(Num(-1), "Sqrt").AsDouble()!.Value));
        Assert.True(double.IsNaN(Legacy.Binary(Num(0), Num(0), BinaryOperator.Divide).AsDouble()!.Value));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Межа збереження
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void NaN_і_нескінченність_не_приводяться_до_decimal()
    {
        // ⛔ `AsNumber()` — межа збереження. Мовчки перетворити тут `∞` на нуль
        // означало б втратити причину; тому повертається `null`, і маскування
        // з записом причини робить викликач.
        Assert.Null(Num(double.PositiveInfinity).AsNumber());
        Assert.Null(Num(double.NaN).AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Legacy_значення_позначене_подвійною_точністю()
    {
        // За цією ознакою трейс і збереження розрізняють режими, не питаючи
        // про версію методології.
        Assert.True(Num(1.5).IsDouble);
        Assert.False(Dec(1.5m).IsDouble);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Функції: регістр і арність — як у чинному рушії
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Abs", -1.5, 1.5)]
    [InlineData("Ceiling", 1.2, 2.0)]
    [InlineData("Floor", 1.8, 1.0)]
    [InlineData("Truncate", 1.9, 1.0)]
    [InlineData("Sign", -5.0, -1.0)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Legacy_одномісні_функції(string name, double input, double expected)
        => Assert.Equal(expected, Legacy.Unary(Num(input), name).AsDouble());

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Legacy_не_знає_імен_у_чужому_регістрі()
    {
        // `EvaluateOptions.IgnoreCase` у чинній збірці не виставлений, тож
        // `abs` там — невідома функція. Прийняти її в нас означало б прийняти
        // формулу, якої чинна система не рахувала.
        Assert.True(Legacy.Unary(Num(-1), "abs").IsError);
        Assert.True(Legacy.Binary(Num(2), Num(3), "POW").IsError);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void IEEERemainder_це_не_відсоток()
    {
        // ⚠ Знак і правило інші: `IEEERemainder(5,3) = -1`, тоді як `5 % 3 = 2`.
        // Сплутати їх — це інше число там, де ділене від'ємне.
        Assert.Equal(-1d, Legacy.Binary(Num(5), Num(3), "IEEERemainder").AsDouble());
        Assert.Equal(2d, Legacy.Binary(Num(5), Num(3), BinaryOperator.Modulo).AsDouble());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Legacy_не_має_натурального_логарифма()
    {
        // ⛔ У NCalc 1.3.8 `Ln` не оголошений, а `Log` строго двоаргументний
        // (виміряно). Отже формула на `Ln` ніколи не була чинною — і в
        // `Legacy` їй нічого відтворювати.
        Assert.True(Legacy.Unary(Num(1), "Ln").IsError);
        Assert.Equal(3d, Legacy.Binary(Num(8), Num(2), "Log").AsDouble());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Strict_має_натуральний_логарифм()
    {
        // Він у ярусі `Extension`: чинний рушій його не вміє, тому доступний
        // лише там, де відтворювати нічого.
        Assert.False(Strict.Unary(Dec(1m), "Ln").IsError);
    }
}
