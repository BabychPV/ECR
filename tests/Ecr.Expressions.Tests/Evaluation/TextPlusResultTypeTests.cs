using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Evaluation;

/// <summary>
/// Груба оцінка типу <c>ParsedExpression.ResultType</c> (<c>Parser.InferShape</c>) для <c>+</c> на текстах.
/// Правило категорії (L-2) відхиляє вираз, чий <c>ResultType</c> — Number/Boolean/Date; тож
/// <c>!A + '_' + !B</c> і захищені варіанти 5.1 мусять давати Text, інакше правильне правило не публікується.
/// </summary>
/// <remarks>
/// Рішення: Add дає Text, коли одна сторона Text, а друга Text або Null-форма (поле <c>!X</c>, <c>if(...)</c>,
/// <c>NULL</c> — тип невідомий до виконання). Усе, що не торкається Text, не змінено:
/// <c>1 + 2</c>, <c>!A + 1</c>, <c>!A + !B</c> (обидві сторони Null-форми — тип невідомий, лишається Number,
/// як було), <c>1 + 'a'</c> (Text+Number — #VALUE; груба оцінка лишається Number).
/// </remarks>
public sealed class TextPlusResultTypeTests
{
    private const ExpressionDialect Dialect = ExpressionDialect.Methodology;

    private static ExpressionValueType ResultTypeOf(string expression)
    {
        var parsed = Expr.Parse(expression, Dialect);
        Assert.True(parsed.IsSuccess, expression);
        return parsed.Expression!.ResultType;
    }

    [Theory]
    [InlineData("!A + '_' + !B")]
    [InlineData("'x_' + !B")]
    [InlineData("!A + 'x'")]
    [InlineData("'a' + 'b'")]
    [InlineData("'x_' + if(!B = NULL, '', !B)")]
    [InlineData("if(!A = NULL, '', !A) + '_' + !B")]
    [InlineData("if(!A = NULL, '', !A) + '_' + if(!B = NULL, '', !B)")]
    [InlineData("if(!A = NULL or !A = '', 'NA', !A) + '_' + if(!B = NULL or !B = '', 'NA', !B)")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Плюс_з_текстом_і_Null_формою_дає_Text(string expression)
    {
        Assert.Equal(ExpressionValueType.Text, ResultTypeOf(expression));
    }

    [Theory]
    [InlineData("1 + 2", ExpressionValueType.Number)]
    [InlineData("!A + 1", ExpressionValueType.Number)]
    [InlineData("1 + !A", ExpressionValueType.Number)]
    [InlineData("!A + !B", ExpressionValueType.Number)]
    [InlineData("NULL + 1", ExpressionValueType.Number)]
    [InlineData("'a' + 1", ExpressionValueType.Number)]
    [InlineData("1 + 'a'", ExpressionValueType.Number)]
    [InlineData("!A - !B", ExpressionValueType.Number)]
    [InlineData("'a' - 'b'", ExpressionValueType.Number)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Не_текстові_плюси_не_змінено(string expression, ExpressionValueType expected)
    {
        Assert.Equal(expected, ResultTypeOf(expression));
    }

    [Theory]
    [InlineData("!A + '_' + !B")]
    [InlineData("if(!A = NULL, '', !A) + '_' + if(!B = NULL, '', !B)")]
    [InlineData("'x_' + if(!B = NULL, '', !B)")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Статичний_TypeChecker_дає_Text_без_діагностик(string expression)
    {
        var parsed = Expr.Parse(expression, Dialect);
        Assert.True(parsed.IsSuccess);

        var diagnostics = new List<ExpressionDiagnostic>();
        var type = new TypeChecker().Check(parsed.Expression!.Root, new TestBindingContext(), diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(ExpressionValueType.Text, type);
    }
}
