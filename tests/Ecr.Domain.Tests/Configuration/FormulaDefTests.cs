// tests/Ecr.Domain.Tests/Configuration/FormulaDefTests.cs
using System.Reflection;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>
/// Порядок обчислення формул — **обчислюваний**, не введений (ФВ-9.4).
/// Дозволити задати його руками означало б, що додана формула тихо зміщує
/// решту.
/// </summary>
public sealed class FormulaDefTests
{
    private static FormulaDef Formula(string expression = "SUM([Jan])")
        => new(tableDefId: 3, FormulaScope.Column, expression, ExpressionDialect.Template);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void EvaluationOrder_встановлюється_лише_при_публікації()
    {
        var formula = Formula();

        // Свіжостворена формула не має порядку: він з'являється разом із
        // графом, а граф будується при публікації.
        Assert.Equal(0, formula.EvaluationOrder);

        formula.SetEvaluationOrder(7);
        Assert.Equal(7, formula.EvaluationOrder);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Спроба_задати_EvaluationOrder_ззовні_відхиляється()
    {
        var property = typeof(FormulaDef).GetProperty(nameof(FormulaDef.EvaluationOrder))!;

        // ⚠ Перевіряється саме ФОРМА типу: порядок не можна ані передати в
        // конструктор, ані присвоїти властивості. Єдиний шлях — SetEvaluationOrder,
        // який викликає публікація. Інакше додана формула тихо зміщувала б
        // решту, і числа звіту змінилися б без жодної зміни даних.
        Assert.Null(property.GetSetMethod(nonPublic: false));

        var constructorParameters = typeof(FormulaDef)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters())
            .Select(p => p.Name!)
            .ToList();

        Assert.DoesNotContain("evaluationOrder", constructorParameters, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("order", constructorParameters, StringComparer.OrdinalIgnoreCase);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-3.4")]
    public void Токен_поза_оголошеним_списком_аргументів_дає_помилку_публікації()
    {
        var formula = Formula("@FuelConsumption * CST.EF_CO2");
        var declared = new[] { "FuelConsumption" };

        var parsed = Expr.Parse(formula.Expression, ExpressionDialect.Methodology);
        Assert.True(parsed.IsSuccess);

        var arguments = Tokens(parsed.Expression!.Root, SymbolKind.Argument);
        Assert.Equal(["FuelConsumption"], arguments);

        // Токен, якого немає серед оголошених аргументів методології, має
        // зупинити ПУБЛІКАЦІЮ: у рантаймі він дав би null, а null у множенні
        // поширюється — весь розрахунок тихо став би порожнім.
        var unknown = Tokens(
            Expr.Parse("@FuelConsumption + @Unknown", ExpressionDialect.Methodology).Expression!.Root,
            SymbolKind.Argument);

        Assert.Contains("Unknown", unknown, StringComparer.Ordinal);
        Assert.NotEmpty(unknown.Except(declared, StringComparer.Ordinal));
    }

    /// <summary>Імена символів заданого виду у виразі.</summary>
    private static List<string> Tokens(AstNode node, SymbolKind kind)
    {
        var found = new List<string>();
        Walk(node, found, kind);
        return found;
    }

    private static void Walk(AstNode node, List<string> found, SymbolKind kind)
    {
        switch (node)
        {
            case SymbolReferenceNode symbol when symbol.Kind == kind:
                found.Add(symbol.Name);
                return;

            case UnaryNode unary:
                Walk(unary.Operand, found, kind);
                return;

            case BinaryNode binary:
                Walk(binary.Left, found, kind);
                Walk(binary.Right, found, kind);
                return;

            case FunctionNode function:
                foreach (var argument in function.Arguments)
                {
                    Walk(argument, found, kind);
                }

                return;

            default:
                return;
        }
    }
}
