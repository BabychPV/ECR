using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Graph;
using Ecr.Expressions.Parsing;

namespace Ecr.Infrastructure.Expressions;

/// <summary>
/// Реалізація <see cref="IFormulaEngine"/> — фасад над лексером, парсером,
/// резолвером, перевірками і обчислювачем.
/// </summary>
/// <remarks>
/// Живе в <c>Ecr.Infrastructure</c>, а не в <c>Ecr.Expressions</c> (рішення
/// за <c>Q-013</c>, варіант A). Причина: порт <see cref="IFormulaEngine"/>
/// оголошений у <c>Ecr.Application.Ports</c> і сам оперує типами виразів, тому
/// <c>Ecr.Application</c> залежить від <c>Ecr.Expressions</c> (tz/03 §3.3).
/// Реалізація в <c>Ecr.Expressions</c> замкнула б цикл; <c>Ecr.Infrastructure</c> —
/// єдиний проєкт, який за <c>05-skeleton.md</c> §4 знає обидва.
/// </remarks>
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
