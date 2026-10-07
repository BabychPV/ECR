using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Parsing;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Evaluation;

/// <summary>
/// Оператор <c>+</c> на двох ТЕКСТАХ конкатенує (AF-формула
/// <c>ECW_Location = !ECW_RepairStatus + '_' + !ECW_Category</c>).
/// </summary>
/// <remarks>
/// Рішення: лише текст + текст. Текст + число (і навпаки) лишається <c>#VALUE</c>, як було;
/// Null поширюється, як у решти арифметики (<c>NULL + 'a'</c> = Null) — у шаблонному діалекті на відміну від &amp;, який трактує Null порожнім рядком (у Methodology &amp; — побітове І). Число + число та Дата + число не змінено.
/// </remarks>
public sealed class TextPlusConcatenationTests
{
    private const ExpressionDialect Dialect = ExpressionDialect.Methodology;

    private static TestEvaluationContext Fields(string? a, string? b, string? c = null)
    {
        var context = new TestEvaluationContext();
        context.FormulaResults["A"] = a is null ? ExpressionValue.Null : ExpressionValue.Text(a);
        context.FormulaResults["B"] = b is null ? ExpressionValue.Null : ExpressionValue.Text(b);
        context.FormulaResults["C"] = c is null ? ExpressionValue.Null : ExpressionValue.Text(c);
        return context;
    }

    // ——— Нова поведінка ———

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Текст_плюс_текст_конкатенує()
    {
        var value = Expr.Eval("'a' + 'b'", dialect: Dialect);

        Assert.Equal(ExpressionValueType.Text, value.Type);
        Assert.Equal("ab", value.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Поля_через_роздільник_конкатенують()
    {
        var value = Expr.Eval("!A + '_' + !B", Fields("Repair", "Cat1"), Dialect);

        Assert.Equal(ExpressionValueType.Text, value.Type);
        Assert.Equal("Repair_Cat1", value.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Ланцюг_із_трьох_текстів_конкатенує()
    {
        var value = Expr.Eval("!A + '_' + !B + '_' + !C", Fields("x", "y", "z"), Dialect);

        Assert.Equal("x_y_z", value.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Статичний_тип_текст_плюс_текст_це_текст_без_діагностик()
    {
        var parsed = Expr.Parse("'a' + 'b'", Dialect);
        Assert.True(parsed.IsSuccess);
        Assert.Equal(ExpressionValueType.Text, parsed.Expression!.ResultType);

        var diagnostics = new List<ExpressionDiagnostic>();
        var type = new TypeChecker().Check(parsed.Expression.Root, new TestBindingContext(), diagnostics);
        Assert.Empty(diagnostics);
        Assert.Equal(ExpressionValueType.Text, type);
    }

    // ——— Контрольні: поведінка не змінюється ———

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Число_плюс_число_лишається_додаванням()
    {
        Assert.Equal(5m, Expr.Eval("2 + 3", dialect: Dialect).AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Дата_плюс_число_лишається_датою()
    {
        var context = new TestEvaluationContext();
        context.FormulaResults["D"] = ExpressionValue.Date(new DateTime(2026, 1, 1));
        var value = Expr.Eval("!D + 2", context, Dialect);

        Assert.Equal(ExpressionValueType.Date, value.Type);
        Assert.Equal(new DateTime(2026, 1, 3), value.Value);
    }

    [Theory]
    [InlineData("'a' + 1")]
    [InlineData("1 + 'a'")]
    [InlineData("TRUE + 'a'")]
    public void Текст_плюс_не_текст_лишається_VALUE(string expression)
    {
        var value = Expr.Eval(expression, dialect: Dialect);

        Assert.True(value.IsError);
        Assert.Equal(ExpressionErrors.BadValue, value.ErrorCode);
    }

    [Theory]
    [InlineData("NULL + 'a'")]
    [InlineData("'a' + NULL")]
    [InlineData("NULL + 1")]
    public void Null_поширюється(string expression)
    {
        Assert.True(Expr.Eval(expression, dialect: Dialect).IsNull);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Порожнє_поле_у_конкатенації_плюсом_дає_Null()
    {
        Assert.True(Expr.Eval("!A + '_' + !B", Fields("x", null), Dialect).IsNull);
    }
}
