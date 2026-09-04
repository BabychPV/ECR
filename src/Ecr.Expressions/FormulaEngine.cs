using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Graph;
using Ecr.Expressions.Parsing;

namespace Ecr.Expressions;

/// <summary>
/// Реалізація <see cref="IFormulaEngine"/> — фасад над лексером, парсером,
/// резолвером, перевірками і обчислювачем.
/// </summary>
public sealed class FormulaEngine(
    Parser parser,
    DependencyExtractor dependencyExtractor,
    Evaluator evaluator,
    TopologicalSorter sorter) : IFormulaEngine
{
    /// <inheritdoc />
    public ParseResult Parse(string expression, ExpressionDialect dialect)
        => throw new NotImplementedException(
            "TODO: делегувати parser.Parse; при помилці не кидати виняток, а повернути ParseResult " +
            "із діагностиками — конфігуратор має показати проблему користувачеві, а не впасти.");

    /// <inheritdoc />
    public IReadOnlyList<FormulaDependencyRef> ExtractDependencies(
        ParsedExpression expression, DependencyContext context)
        => throw new NotImplementedException("TODO: делегувати dependencyExtractor і спроєктувати в контрактний тип.");

    /// <inheritdoc />
    public EvaluationResult Evaluate(ParsedExpression expression, IEvaluationContext context)
        => throw new NotImplementedException("TODO: делегувати evaluator; загорнути результат і діагностики.");

    /// <inheritdoc />
    public OrderingResult BuildEvaluationOrder(IReadOnlyList<FormulaNode> nodes)
        => throw new NotImplementedException(
            "TODO: побудувати DependencyGraph із nodes і делегувати sorter. " +
            "Цикл → OrderingResult із CyclePath, а не виняток.");
}
