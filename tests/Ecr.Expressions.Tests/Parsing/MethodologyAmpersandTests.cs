using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Parsing;

/// <summary>
/// Аудит L7-04: одинарний <c>&amp;</c> у діалекті методологій і конкатенація
/// <c>Legacy</c>-чисел.
/// </summary>
/// <remarks>
/// ⛔ Замір NCalc 1.3.8 (`tests/Ecr.Legacy.Probe`, 2026-10-03): <c>&amp;</c> —
/// ПОБІТОВЕ AND цілих: <c>6 &amp; 3</c> = 2, <c>1 &amp; 3</c> = 1, а
/// <c>'a' &amp; 'b'</c> кидає FormatException. У нас <c>&amp;</c> був
/// конкатенацією — імпортована формула дала б текст замість числа мовчки. Тому,
/// як і <c>^</c> (XOR), він відхиляється в діалекті методологій.
/// </remarks>
public sealed class MethodologyAmpersandTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("6 & 3")]
    [InlineData("(@A + @B) & ' t'")]
    public void Одинарний_амперсанд_у_методології_відхиляється(string expression)
    {
        var result = Expr.Parse(expression, ExpressionDialect.Methodology);

        Assert.False(result.IsSuccess);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.MessageKey == "expr.ampersandNotConcat");
        Assert.Equal(ExpressionErrors.Syntax, diagnostic.Code);
        Assert.Contains("bitwise AND", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Подвійний_амперсанд_у_методології_лишається_логічним_AND()
        => Assert.True(Expr.Parse("TRUE && FALSE", ExpressionDialect.Methodology).IsSuccess);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Амперсанд_у_шаблонах_лишається_конкатенацією()
    {
        Assert.True(DialectSyntax.Of(ExpressionDialect.Template).AmpersandIsConcat);
        Assert.False(DialectSyntax.Of(ExpressionDialect.Methodology).AmpersandIsConcat);
        Assert.Equal("ab", Expr.Eval("'a' & 'b'").Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.9")]
    public void Конкатенація_результату_Legacy_арифметики_не_кидає()
    {
        // ⚠ Парсер тепер не пропускає `&` у методологію, але обчислювач тримає
        // контракт сам: Legacy-число — boxed double, і `(decimal)` розпаковка
        // кидала InvalidCastException.
        var sum = new BinaryNode(
            BinaryOperator.Add,
            new SymbolReferenceNode(SymbolKind.Argument, "A"),
            new SymbolReferenceNode(SymbolKind.Argument, "B"));
        var concat = new BinaryNode(BinaryOperator.Concat, sum, new LiteralNode("x", ExpressionValueType.Text));
        var context = new TestEvaluationContext();
        context.Arguments["A"] = ExpressionValue.LegacyNumber(1);
        context.Arguments["B"] = ExpressionValue.LegacyNumber(2.5);

        var value = new Evaluator(new FunctionRegistry(), new LegacyDoubleArithmetic())
            .Evaluate(concat, context, ExpressionDialect.Methodology);

        Assert.Equal("3.5x", value.Value);
    }
}
