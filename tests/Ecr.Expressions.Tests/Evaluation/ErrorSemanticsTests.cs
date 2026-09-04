using Ecr.Expressions;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Evaluation;

/// <summary>
/// Помилки — **значення**, а не винятки: одна зіпсована комірка не має валити
/// перерахунок усієї таблиці (02b §6.4).
/// </summary>
public sealed class ErrorSemanticsTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Ділення_на_нуль_дає_помилку_а_не_виняток()
    {
        // Саме «а не виняток»: виняток тут зупинив би перерахунок усієї
        // таблиці через одну комірку.
        var value = Expr.Eval("1 / 0");

        Assert.True(value.IsError);
        Assert.Equal(ExpressionErrors.DivideByZero, value.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Ділення_на_null_дає_помилку_ділення_на_нуль()
    {
        // ⚠ Виняток із правила поширення null: ділення на невідоме — це не
        // «невідомий результат», а неможлива операція (02b §6.4).
        var value = Expr.Eval("1 / NULL");

        Assert.True(value.IsError);
        Assert.Equal(ExpressionErrors.DivideByZero, value.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Помилка_поширюється_через_арифметику()
    {
        Assert.Equal(ExpressionErrors.DivideByZero, Expr.Eval("1 / 0 + 1").ErrorCode);
        Assert.Equal(ExpressionErrors.DivideByZero, Expr.Eval("100 * (1 / 0)").ErrorCode);

        // Через агрегат — теж: зіпсована комірка не має тихо випадати з суми,
        // бо тоді підсумок виглядав би правдоподібним.
        Assert.Equal(ExpressionErrors.DivideByZero, Expr.Eval("SUM(1, 1 / 0, 2)").ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void IFERROR_перехоплює_помилку()
    {
        Assert.Equal(0m, Expr.Number("IFERROR(1 / 0, 0)"));
        Assert.Equal(5m, Expr.Number("IFERROR(5, 0)"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void IFERROR_НЕ_перехоплює_null_бо_це_не_помилка()
    {
        // «Не заповнено» — не помилка. Підміна порожнечі запасним значенням
        // тихо дописала б у звіт число, якого ніхто не вводив (02c E08).
        var value = Expr.Eval("IFERROR(NULL, 99)");

        Assert.True(value.IsNull);
        Assert.NotEqual(99m, value.AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Комірка_з_помилкою_зберігається_видимою_а_не_як_порожня()
    {
        var value = Expr.Eval("1 / 0");
        var stored = CellValueMapping.ToCellValue(value);

        // IsEmpty = 0, ValueString = '#DIV/0', IsCalculated = 1 — щоб помилку
        // було ВИДНО у звіті, а не «просто порожньо» (02b §6.4).
        Assert.False(stored.IsEmpty);
        Assert.Equal("#DIV/0", stored.ValueString);
        Assert.True(stored.IsCalculated);
        Assert.True(stored.IsWellFormed());
    }
}
