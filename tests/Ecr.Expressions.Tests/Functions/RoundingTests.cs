using System.Globalization;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Functions;

/// <summary>
/// Режим округлення. **Банківське округлення дало б інші числа у звіті**,
/// тому скрізь <c>MidpointRounding.AwayFromZero</c> (02b §7, 02c E12–E13).
/// </summary>
public sealed class RoundingTests
{
    [Theory]
    [InlineData("2.5", 0, "3")]
    [InlineData("3.5", 0, "4")]     // банківське дало б 4 — збіг, тому потрібен наступний випадок
    [InlineData("0.5", 0, "1")]     // банківське дало б 0 — тут різниця видна
    [InlineData("-2.5", 0, "-3")]
    [InlineData("1.2345", 2, "1.23")]
    [InlineData("1.2350", 2, "1.24")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.16a")]
    public void ROUND_округлює_від_нуля_а_не_до_парного(string value, int digits, string expected)
    {
        var actual = Expr.Number(
            string.Create(CultureInfo.InvariantCulture, $"ROUND({value}, {digits})"));

        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), actual);
    }

    [Theory]
    [InlineData("ROUND(1.2345, 2.7)")]
    [InlineData("ROUND(1.2345, 0.5)")]
    [InlineData("ROUND(1.2345, -0.5)")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void ROUND_із_дробовою_кількістю_знаків_це_помилка_а_не_тихе_відкидання(string expression)
    {
        // ⛔ Аудит 2026-09-16, §2.5. `MethodologyFunctions.Round` відхиляв це від
        // початку з прямим поясненням: «Round(x, 2.7) написали не для того, щоб
        // отримати два знаки». `TemplateFunctions.Round` робив ПРОТИЛЕЖНЕ —
        // `(int)decimal.Truncate(places)` тихо відкидав дробову частину. Одна
        // мова правил, що округлює по-різному залежно від діалекту, дає
        // розбіжність, видиму лише як інше число у звіті.
        var value = Expr.Eval(expression);

        Assert.True(value.IsError, $"Очікувалась помилка, отримано {value.Value}.");
        Assert.Equal("#VALUE", value.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Ціла_кількість_знаків_у_дробовому_записі_лишається_дозволеною()
    {
        // ⚠ Зворотний бік: `2.0` — це рівно два знаки, а не «дробова кількість».
        // Заборонити й це означало б зламати вирази, де кількість знаків
        // приходить з обчислення в decimal.
        Assert.Equal(1.23m, Expr.Number("ROUND(1.2345, 2.0)"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Обчислення_ведеться_в_decimal_і_не_втрачає_точності()
    {
        // 0.1 + 0.2 у double дає 0.30000000000000004. У decimal — рівно 0.3.
        // Це не педантизм: звірка з еталоном на 108 млн рядків неможлива,
        // якщо результат залежить від порядку додавання (D-30).
        var sum = Expr.Number("0.1 + 0.2");

        Assert.Equal(0.3m, sum);
        Assert.Equal("0.3", sum.ToString(CultureInfo.InvariantCulture));
        Assert.NotEqual(0.1 + 0.2, (double)sum);

        // Той самий доданок у різному порядку дає той самий результат.
        Assert.Equal(Expr.Number("SUM(0.1, 0.2, 0.3)"), Expr.Number("SUM(0.3, 0.2, 0.1)"));
    }
}
