using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Evaluation;

/// <summary>
/// Хостові функції (<c>$name</c>) діалекту методологій: гачок
/// <see cref="IHostFunctionContext"/> в <c>Evaluator.MethodologyCall</c>.
/// </summary>
public sealed class HostFunctionTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Хостова_функція_викликається_з_обчисленими_аргументами()
    {
        var context = new HostContext();
        context.Inner.Arguments["X"] = ExpressionValue.Number(4m);

        // $sum(@X * 2, 3) → аргументи обчислено ДО виклику хоста: 8 і 3.
        var node = new FunctionNode("$sum", [
            new BinaryNode(
                BinaryOperator.Multiply,
                new SymbolReferenceNode(SymbolKind.Argument, "X"),
                new LiteralNode(2m, ExpressionValueType.Number)),
            new LiteralNode(3m, ExpressionValueType.Number),
        ]);

        var value = new Evaluator(new FunctionRegistry())
            .Evaluate(node, context, ExpressionDialect.Methodology);

        Assert.Equal(11m, value.AsNumber());
        Assert.Equal("$sum", context.LastName);
        Assert.Equal([8m, 3m], context.LastArgs.Select(a => a.AsNumber()!.Value));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Без_хостового_контексту_функція_з_доларом_дає_VALUE()
    {
        var node = new FunctionNode("$sum", [new LiteralNode(1m, ExpressionValueType.Number)]);

        var value = new Evaluator(new FunctionRegistry())
            .Evaluate(node, new TestEvaluationContext(), ExpressionDialect.Methodology);

        Assert.True(value.IsError);
        Assert.Equal(ExpressionErrors.BadValue, value.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Невідома_хосту_функція_дає_VALUE_а_не_виняток()
    {
        var context = new HostContext { Known = false };
        var node = new FunctionNode("$nope", []);

        var value = new Evaluator(new FunctionRegistry())
            .Evaluate(node, context, ExpressionDialect.Methodology);

        Assert.True(value.IsError);
        Assert.Equal(ExpressionErrors.BadValue, value.ErrorCode);
    }

    /// <summary>Контекст із єдиною хостовою функцією <c>$sum</c>.</summary>
    private sealed class HostContext : IHostFunctionContext
    {
        public TestEvaluationContext Inner { get; } = new();

        public bool Known { get; init; } = true;

        public string? LastName { get; private set; }

        public IReadOnlyList<ExpressionValue> LastArgs { get; private set; } = [];

        public PeriodContext Period => Inner.Period;

        public bool TryInvokeHost(
            string name, IReadOnlyList<ExpressionValue> args, EvaluationBudget budget, out ExpressionValue result)
        {
            LastName = name;
            LastArgs = args;
            if (!Known)
            {
                result = ExpressionValue.Null;
                return false;
            }

            result = ExpressionValue.Number(args.Sum(a => a.AsNumber() ?? 0m));
            return true;
        }

        public ExpressionValue GetArgument(string name) => Inner.GetArgument(name);

        public IReadOnlyList<ExpressionValue> Read(CellReferenceNode reference) => Inner.Read(reference);

        public ExpressionValue GetCell(int tableDefId, string rowKey, int columnDefId, int periodOffset)
            => Inner.GetCell(tableDefId, rowKey, columnDefId, periodOffset);

        public IReadOnlyList<ExpressionValue> GetCellsByPredicate(int tableDefId, string filterJson, int columnDefId)
            => Inner.GetCellsByPredicate(tableDefId, filterJson, columnDefId);

        public ExpressionValue GetConstant(string name) => Inner.GetConstant(name);

        public ExpressionValue GetFormulaResult(string name) => Inner.GetFormulaResult(name);

        public ExpressionValue GetHeader(string name) => Inner.GetHeader(name);

        public ExpressionValue Convert(ExpressionValue value, string fromUnitCode, string toUnitCode)
            => Inner.Convert(value, fromUnitCode, toUnitCode);
    }
}
