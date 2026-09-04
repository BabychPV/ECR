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
    /// <remarks>
    /// ⚠ Справжній <see cref="TopologicalSorter"/>, а не порядок вхідного
    /// списку. Раніше тут була заглушка, і тест «порядок обчислюється при
    /// публікації» проходив би навіть тоді, коли публікація не сортує нічого:
    /// вона перевіряла б заглушку.
    /// </remarks>
    public OrderingResult BuildEvaluationOrder(IReadOnlyList<FormulaNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var graph = new DependencyGraph();
        foreach (var node in nodes)
        {
            // Вузол без ребер теж має потрапити в граф — інакше формула, від
            // якої ніхто не залежить, зникла б із порядку. Ребро із себе на
            // себе тут дало б цикл, якого немає.
            graph.AddNode(node.FormulaDefId);

            foreach (var dependency in node.DependsOnFormulaDefIds)
            {
                graph.AddEdge(node.FormulaDefId, dependency);
            }
        }

        return new TopologicalSorter().Sort(graph);
    }
}
