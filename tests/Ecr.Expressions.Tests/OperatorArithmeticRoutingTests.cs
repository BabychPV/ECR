using Ecr.Domain.Enums;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// Оператори рахує <see cref="IEvaluationArithmetic"/>, а не сам обчислювач
/// (<c>I.7</c>, <c>H-24d-1</c>).
/// </summary>
/// <remarks>
/// ⛔ Тут була третя копія того самого класу дефекту.
/// <c>LegacyDoubleArithmetic.Binary</c> написаний на кроці <c>I.4</c>,
/// покритий тестами і **не потрапляв на шлях операторів узагалі**: метод
/// <c>Evaluator.Arithmetic</c> був <c>static</c> і рахував у <c>decimal</c>
/// незалежно від режиму. Арифметику кликали лише функції (<c>I.14</c>).
///
/// ⚠ Наслідок тихий, як завжди: у <c>Legacy</c> вираз <c>1/0</c> давав
/// <c>#DIV/0</c>, тоді як чинний рушій дає <c>+∞</c> — і саме це <c>+∞</c> він
/// далі маскує в нуль (<c>Utilities.cs:56-71</c>). Режим, який існує заради
/// відтворення чисел, на найпростішому діленні відтворював інше.
/// </remarks>
public sealed class OperatorArithmeticRoutingTests
{
    private static ExpressionValue Eval(string expression, IEvaluationArithmetic arithmetic)
    {
        var parsed = new Parser().Parse(expression, ExpressionDialect.Methodology);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        return new Evaluator(new FunctionRegistry(), arithmetic)
            .Evaluate(parsed.Expression!.Root, new TestEvaluationContext(), ExpressionDialect.Methodology);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.9")]
    public void Ділення_на_нуль_у_Legacy_дає_нескінченність_а_не_помилку()
    {
        // ⛔ Головне твердження. Виміряно на справжньому NCalc 1.3.8:
        // `1/0 → ∞`, а не `DivideByZeroException`. Той `catch` у чинному коді
        // для ділення на нуль не спрацьовує ніколи.
        var value = Eval("1/0", new LegacyDoubleArithmetic());

        Assert.False(value.IsError);
        Assert.True(double.IsPositiveInfinity(value.AsDouble()!.Value));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.9")]
    public void Ділення_на_нуль_у_Strict_лишається_помилкою()
    {
        // ⚠ Другий бік: у `decimal` нескінченності не існує, і мовчазний нуль
        // тут був би невідрізненний від справжнього. Режими різняться, і
        // різниця має бути видима саме тут.
        var value = Eval("1/0", new StrictDecimalArithmetic());

        Assert.True(value.IsError);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.9")]
    public void Нуль_поділити_на_нуль_у_Legacy_дає_NaN()
    {
        // ⚠ Друга причина маскування, і вона інша за природою: `∞` — це
        // «завелике», `NaN` — «невизначене». У звіті вони мають стояти
        // окремими рядками, бо й лікуються по-різному.
        var value = Eval("0/0", new LegacyDoubleArithmetic());

        Assert.False(value.IsError);
        Assert.True(double.IsNaN(value.AsDouble()!.Value));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.9")]
    public void Звичайне_ділення_у_Legacy_не_цілочисельне()
    {
        // ⛔ Замір, який скасував пастку 1 директиви №05: `365/31` дає
        // 11.774…, а не 11. Тест стоїть тут, а не лише в стенді, бо саме
        // цей шлях — оператор через арифметику — і рахує число.
        var value = Eval("365/31", new LegacyDoubleArithmetic());

        Assert.Equal(11.774193548387096d, value.AsDouble()!.Value, 12);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.9")]
    public void Порівняння_з_Legacy_нескінченністю_впорядковується_а_не_дає_VALUE()
    {
        // ⛔ Аудит 2026-09-16, §2.1. `Compare` працював через `AsNumber()`, який
        // звужує до `decimal`, а `±∞`/`NaN` у `decimal` не подаються — тож
        // повертав `null`, і будь-яке `<`, `<=`, `>`, `>=` над Legacy-
        // нескінченністю тихо ставало `#VALUE`. Арифметичні оператори при цьому
        // нескінченність зберігали коректно: дві половини одного режиму
        // розходилися, і саме на порівнянні, яким методологія відсікає викиди
        // за порогом.
        var legacy = new LegacyDoubleArithmetic();

        // `1/0` — це `+∞` як ЗНАЧЕННЯ (див. тест вище), і воно більше за будь-яке
        // скінченне число.
        Assert.True((bool)Eval("1/0 > 1000000", legacy).Value!);
        Assert.False((bool)Eval("1/0 < 1000000", legacy).Value!);
        Assert.True((bool)Eval("1/0 >= 1/0", legacy).Value!);

        // Від'ємна нескінченність — з іншого краю.
        Assert.True((bool)Eval("(0 - 1)/0 < 0", legacy).Value!);

        // ⚠ А NaN не впорядковується ні з чим — і це саме помилка значення, не
        // «менше»: `NaN < 1` і `NaN >= 1` в IEEE 754 обидва false, тож віддати
        // Boolean означало б збрехати в один із двох боків.
        Assert.True(Eval("0/0 > 1", legacy).IsError);
        Assert.True(Eval("0/0 <= 1", legacy).IsError);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Показник_степеня_обмежений_а_не_крутить_мільйон_множень()
    {
        // ⛔ Аудит 2026-09-16, §2.4. Оператор `^` мав звичайний `for`-цикл O(n)
        // БЕЗ жодної межі — на відміну від `DecimalMath.Pow`, яка обмежує
        // швидкий цілочисельний шлях `|exponent| <= 1000`. `1.0001 ^ 5000000`
        // не переповнює decimal (основа ≈1), тож не падало, а МОЛОТИЛО —
        // всередині нічного масового перерахунку («мільйони викликів») одна
        // помилково введена формула підвішувала спільний прогін.
        // ⚠ Через `Expr.Eval` (діалект ШАБЛОНУ): у діалекті методологій `^` —
        // це побітовий XOR, а не степінь, і парсер це прямо відхиляє.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tooBig = Expr.Eval("1.0001 ^ 5000000");
        sw.Stop();

        Assert.True(tooBig.IsError);
        Assert.Equal(ExpressionErrors.BadValue, tooBig.ErrorCode);

        // Межа доведена не лише кодом помилки, а й тим, що відповідь приходить
        // одразу: мутація, що прибирає перевірку, не вкладеться в цей бюджет.
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Обчислення зайняло {sw.ElapsedMilliseconds} мс.");

        // Межа саме на межі: 1000 ще рахується, 1001 — вже ні.
        Assert.False(Expr.Eval("1.0001 ^ 1000").IsError);
        Assert.True(Expr.Eval("1.0001 ^ 1001").IsError);

        // ⚠ Звичайні показники методології лишаються точними — бінарне
        // піднесення не «оптимізація замість правильного числа».
        Assert.Equal(1024m, Expr.Eval("2 ^ 10").AsNumber());
        Assert.Equal(0.001m, Expr.Eval("10 ^ (0 - 3)").AsNumber());
        Assert.Equal(1m, Expr.Eval("7 ^ 0").AsNumber());
    }
}
