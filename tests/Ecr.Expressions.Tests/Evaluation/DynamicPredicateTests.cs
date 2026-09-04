// tests/Ecr.Expressions.Tests/Evaluation/DynamicPredicateTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Evaluation;

/// <summary>
/// Предикатні діапазони для динамічних таблиць. На відміну від звичайних
/// діапазонів, які матеріалізуються при публікації (ФВ-2.9), предикат
/// **обчислюється в рантаймі** — рядків на момент публікації ще немає.
/// </summary>
public sealed class DynamicPredicateTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Предикат_обчислюється_в_рантаймі()
    {
        var context = new TestEvaluationContext { CurrentSheet = "Waste", CurrentTable = "Items" };
        Row(context, "r1", "W-01", 100m);
        Row(context, "r2", "W-02", 500m);
        Row(context, "r3", "W-01", 25m);

        var expression = "SUM([Items].[WHERE [WasteType] = 'W-01'].[Amount])";
        Assert.Equal(125m, Expr.Number(expression, context));

        // ⚠ Рядок, доданий ПІСЛЯ публікації, одразу потрапляє в суму: саме
        // тому предикат і не можна матеріалізувати наперед.
        Row(context, "r4", "W-01", 75m);
        Assert.Equal(200m, Expr.Number(expression, context));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Предикат_на_порожній_таблиці_дає_порожню_множину()
    {
        var context = new TestEvaluationContext { CurrentSheet = "Waste", CurrentTable = "Items" };
        context.Rows["Waste|Items"] = [];

        // Порожня множина — не помилка: SUM над нею дає 0, AVERAGE — null.
        // Документ без жодного рядка відходів — нормальний документ.
        Assert.Equal(0m, Expr.Number("SUM([Items].[WHERE [WasteType] = 'W-01'].[Amount])", context));
        Assert.True(Expr.Eval("AVERAGE([Items].[WHERE [WasteType] = 'W-01'].[Amount])", context).IsNull);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Агрегат_у_предикаті_відхиляється_при_публікації()
    {
        // Дозволені лише посилання на колонки того самого рядка, RowKind,
        // літерали та оператори. Виклик функції всередині предиката робить
        // його другою мовою всередині мови (02b §4.2).
        var diagnostics = Validate("SUM([Items].[WHERE SUM([Amount]) > 1].[Amount])");

        Assert.Contains(diagnostics, d => d.Code == "ECR-TMPL-0422");
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Крос_періодне_посилання_у_предикаті_відхиляється()
    {
        var diagnostics = Validate("SUM([Items].[WHERE [Period:-1].[Items].[r1].[Amount] > 1].[Amount])");

        Assert.Contains(diagnostics, d => d.Code == "ECR-TMPL-0422");
    }

    /// <summary>Перевіряє предикати виразу правилами публікації.</summary>
    private static List<ExpressionDiagnostic> Validate(string expression)
    {
        var parsed = Expr.Parse(expression);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        var diagnostics = new List<ExpressionDiagnostic>();
        PredicateValidator.Validate(parsed.Expression!.Root, diagnostics);
        return diagnostics;
    }

    private static void Row(TestEvaluationContext context, string rowKey, string wasteType, decimal amount)
    {
        context.SetCell("Waste", "Items", rowKey, "WasteType", ExpressionValue.Text(wasteType));
        context.SetCell("Waste", "Items", rowKey, "Amount", ExpressionValue.Number(amount));
    }
}
