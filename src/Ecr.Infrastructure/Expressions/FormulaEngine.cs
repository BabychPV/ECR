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
    Evaluator evaluator,
    TopologicalSorter sorter,
    ITemplateStructure structure) : IFormulaEngine
{
    /// <inheritdoc />
    public ParseResult Parse(string expression, ExpressionDialect dialect)
        => parser.Parse(expression, dialect);

    /// <inheritdoc />
    public IReadOnlyList<FormulaDependencyRef> ExtractDependencies(
        ParsedExpression expression, DependencyContext context)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(context);

        // ⚠ Знімок береться СИНХРОННО з кешу, а не очікуванням асинхронного
        // виклику: сигнатура порту заморожена контрактом, і `GetAwaiter().
        // GetResult()` тут виїдав би пул потоків саме на піку останнього дня
        // періоду. Контракт ITemplateStructure сильніший — знімок має бути вже
        // завантажений, і на шляху публікації це так і є.
        var snapshot = structure.Get(context.TemplateVersionId);

        var tables = snapshot.Sheets
            .SelectMany(s => s.Tables)
            .ToDictionary(t => t.Id);

        var extractor = new DependencyExtractor(new ReferenceResolver(snapshot), new RangeExpander());

        return extractor
            .Extract(expression.Root, context.CurrentTableDefId, context.CurrentRowKey, tables)
            .Select(d => new FormulaDependencyRef(
                d.DependsOnKind, d.TableDefId, d.RowKey, d.ColumnDefId,
                d.FilterJson, d.PeriodOffset, d.SortOrder))
            .ToList();
    }

    /// <inheritdoc />
    public EvaluationResult Evaluate(ParsedExpression expression, IEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(expression);

        // Помилка обчислення — це ЗНАЧЕННЯ всередині ExpressionValue
        // (#DIV/0, #REF, #VALUE), а не запис у Diagnostics: одна зіпсована
        // комірка не валить перерахунок таблиці (02b §6.4).
        return new EvaluationResult(evaluator.Evaluate(expression.Root, context), []);
    }

    /// <inheritdoc />
    public OrderingResult BuildEvaluationOrder(IReadOnlyList<FormulaNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var graph = new DependencyGraph();
        foreach (var node in nodes)
        {
            // ⚠ Самопосилання НЕ відсіюється: формула, що залежить від себе, —
            // це цикл, і саме таким його має побачити публікація (ФВ-9.4).
            foreach (var dependency in node.DependsOnFormulaDefIds)
            {
                graph.AddEdge(node.FormulaDefId, dependency);
            }
        }

        var result = sorter.Sort(graph);
        if (!result.IsSuccess)
        {
            return result;
        }

        // Формула без жодної залежності не має ребер і тому не потрапляє в
        // граф. Її треба дописати в порядок: інакше вона просто не рахувалася б.
        var order = result.Order.ToList();
        var known = order.ToHashSet();
        foreach (var node in nodes.Where(n => known.Add(n.FormulaDefId)))
        {
            order.Add(node.FormulaDefId);
        }

        return new OrderingResult(true, order, null);
    }
}
