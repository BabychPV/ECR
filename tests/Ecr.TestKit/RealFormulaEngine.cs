using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.Expressions.Graph;
using Ecr.Expressions.Parsing;

namespace Ecr.TestKit;

/// <summary>
/// <see cref="IFormulaEngine"/> без інфраструктури: справжні лексер, парсер і
/// обчислювач.
/// </summary>
/// <remarks>
/// ⚠ Підміняти розбір і обчислення заглушкою означало б перевіряти заглушку.
/// Ці частини — чистий код <c>Ecr.Expressions</c> без бази, HTTP і часу, тому
/// в тестах вони беруться справжніми.
///
/// Заглушені лише дві операції, які потребують знімка метаданих із кешу:
/// витягування залежностей і топологічний порядок. Їхню власну поведінку
/// перевіряють <c>RangeExpansionTests</c> і <c>TopologicalSorterTests</c>.
/// </remarks>
public sealed class RealFormulaEngine : IFormulaEngine
{
    private readonly Parser _parser = new();
    private readonly Evaluator _evaluator = new(new FunctionRegistry());

    /// <inheritdoc />
    public ParseResult Parse(string expression, ExpressionDialect dialect)
        => _parser.Parse(expression, dialect);

    /// <inheritdoc />
    public EvaluationResult Evaluate(ParsedExpression expression, IEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return new EvaluationResult(_evaluator.Evaluate(expression.Root, context), []);
    }

    /// <inheritdoc />
    public IReadOnlyList<FormulaDependencyRef> ExtractDependencies(
        ParsedExpression expression, DependencyContext context) => [];

    /// <inheritdoc />
    public OrderingResult BuildEvaluationOrder(IReadOnlyList<FormulaNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        return new OrderingResult(true, nodes.Select(n => n.FormulaDefId).ToList(), null);
    }
}
