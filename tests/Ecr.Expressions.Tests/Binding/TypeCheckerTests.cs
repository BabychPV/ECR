// tests/Ecr.Expressions.Tests/Binding/TypeCheckerTests.cs
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Binding;

/// <summary>
/// Типізація при публікації. Мета — щоб несумісність типів була **помилкою
/// публікації**, а не дивним числом у звіті через місяць.
/// </summary>
public sealed class TypeCheckerTests
{
    private static readonly TypeChecker Checker = new();

    private static (ExpressionValueType Type, List<ExpressionDiagnostic> Diagnostics) Check(
        string expression, TestBindingContext? context = null)
    {
        var parsed = Expr.Parse(expression);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        var diagnostics = new List<ExpressionDiagnostic>();
        var type = Checker.Check(parsed.Expression!.Root, context ?? new TestBindingContext(), diagnostics);
        return (type, diagnostics);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Число_плюс_текст_відхиляється()
    {
        // ⚠ Excel тут вгадав би і дав «майже правильне» число. Мовчазне
        // приведення — рівно те, від чого система відходить (02b §5).
        var (_, diagnostics) = Check("1 + 'a'");

        Assert.Contains(diagnostics, d => d.Code == "ECR-TMPL-4222");

        // Конкатенація того самого — дозволена: там приведення оголошене.
        Assert.Empty(Check("1 & 'a'").Diagnostics);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Різниця_дат_дає_число()
    {
        var context = new TestBindingContext();
        context.ColumnTypes["Start"] = ExpressionValueType.Date;
        context.ColumnTypes["End"] = ExpressionValueType.Date;

        var (type, diagnostics) = Check("[End] - [Start]", context);

        Assert.Equal(ExpressionValueType.Number, type);
        Assert.Empty(diagnostics);

        // …а додавання дат сенсу не має і відхиляється.
        Assert.NotEmpty(Check("[End] + [Start]", context).Diagnostics);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Boolean_в_арифметиці_відхиляється()
    {
        var context = new TestBindingContext();
        context.ColumnTypes["IsActive"] = ExpressionValueType.Boolean;

        var (_, diagnostics) = Check("[IsActive] + 1", context);

        // TRUE = 1 — конвенція Excel, і саме вона робить «суму галочок»
        // непомітною помилкою.
        Assert.Contains(diagnostics, d => d.Code == "ECR-TMPL-4222");
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Порівняння_числа_з_текстом_дає_помилку_публікації()
    {
        var context = new TestBindingContext();
        context.ColumnTypes["Note"] = ExpressionValueType.Text;

        var (type, diagnostics) = Check("[Note] > 1", context);

        Assert.Equal(ExpressionValueType.Boolean, type);
        Assert.Contains(diagnostics, d => d.Code == "ECR-TMPL-4222");
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Несумісні_одиниці_без_CONVERT_дають_ECR_TMPL_4223()
    {
        // Тонни (1) і кілограми (2) однієї розмірності — але неявних
        // конверсій не буває (D-74): або CONVERT, або відмова публікації.
        var context = new TestBindingContext();
        context.ColumnUnits["Tons"] = 1;
        context.ColumnUnits["Kilos"] = 2;
        context.Dimensions[1] = 1;
        context.Dimensions[2] = 1;

        var parsed = Expr.Parse("[Tons] + [Kilos]");
        var diagnostics = new List<ExpressionDiagnostic>();
        new UnitChecker().Check(parsed.Expression!.Root, context, diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "ECR-TMPL-4223");

        // Та сама одиниця з обох боків — жодних зауважень.
        var same = new List<ExpressionDiagnostic>();
        new UnitChecker().Check(
            Expr.Parse("[Tons] + [Tons]").Expression!.Root, context, same);
        Assert.Empty(same);
    }
}
