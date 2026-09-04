// tests/Ecr.Expressions.Tests/Functions/AggregateFunctionTests.cs
using Ecr.Expressions;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Functions;

/// <summary>
/// Одинадцять функцій діалекту шаблонів на **порожній множині** і з `null`.
/// </summary>
/// <remarks>
/// В агрегатах `null` **поглинається**, у бінарних операціях —
/// **поширюється** (`02b-expressions.md` §6). Це різні правила навмисно:
/// сума трьох місяців, де
/// один порожній, має дорівнювати сумі двох; а `A + B` з невідомим `B`
/// невідоме, і мовчазний нуль тут був би неправильним числом.
/// </remarks>
public sealed class AggregateFunctionTests
{
    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("SUM")]
    [InlineData("AVG")]
    [InlineData("MIN")]
    [InlineData("MAX")]
    [InlineData("COUNT")]
    public void Агрегат_на_порожній_множині_має_визначений_результат(string function)
    {
        // ⚠ «AVG» тут — назва АГРЕГАТУ, а не функції мови: у діалекті вона
        // зветься AVERAGE, і набір закритий рівно на одинадцяти іменах
        // (02b §7). Псевдонім не заводиться, щоб «рівно одинадцять» лишалося
        // перевіряним твердженням.
        var value = function switch
        {
            "SUM" => TemplateFunctions.Sum([]),
            "AVG" => TemplateFunctions.Average([]),
            "MIN" => TemplateFunctions.Min([]),
            "MAX" => TemplateFunctions.Max([]),
            _ => TemplateFunctions.Count([]),
        };

        // Визначений — це «не помилка і не виняток». Конкретне значення різне:
        // сума й лічильник дають нуль, решта — null.
        Assert.False(value.IsError);

        var expected = function is "SUM" or "COUNT" ? (decimal?)0m : null;
        Assert.Equal(expected, value.AsNumber());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Null_поглинається_в_агрегатах()
    {
        Assert.Equal(3m, Expr.Number("SUM(1, NULL, 2)"));
        Assert.Equal(1.5m, Expr.Number("AVERAGE(1, NULL, 2)"));
        Assert.Equal(1m, Expr.Number("MIN(1, NULL, 2)"));
        Assert.Equal(2m, Expr.Number("MAX(1, NULL, 2)"));
        Assert.Equal(2m, Expr.Number("COUNT(1, NULL, 2)"));
        Assert.Equal(2m, Expr.Number("PRODUCT(1, NULL, 2)"));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Null_поширюється_в_бінарних_операціях()
    {
        // Той самий null, інше правило — і саме тут народжуються розбіжності
        // зі старою системою.
        Assert.True(Expr.Eval("NULL + 1").IsNull);
        Assert.True(Expr.Eval("NULL - 1").IsNull);
        Assert.True(Expr.Eval("NULL * 2").IsNull);
        Assert.True(Expr.Eval("NULL ^ 2").IsNull);

        Assert.Equal(1m, Expr.Number("SUM(NULL, 1)"));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Ділення_на_нуль_дає_діагностику_а_не_виняток()
    {
        var value = Expr.Eval("SUM(1, 2) / 0");

        Assert.True(value.IsError);
        Assert.Equal(ExpressionErrors.DivideByZero, value.ErrorCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void AVG_ігнорує_null_а_не_рахує_його_нулем()
    {
        // 30/2 = 15, а не 30/3 = 10. Друге — рівно те «майже правильне» число,
        // яке помічають аж на річній звірці.
        Assert.Equal(15m, Expr.Number("AVERAGE(10, NULL, 20)"));

        var direct = TemplateFunctions.Average(
            [ExpressionValue.Number(10m), ExpressionValue.Null, ExpressionValue.Number(20m)]);
        Assert.Equal(15m, direct.AsNumber());
    }
}
