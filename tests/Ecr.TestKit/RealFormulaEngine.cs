using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
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
/// ⛔ Витягування залежностей БІЛЬШЕ НЕ ЗАГЛУШЕНЕ. Заглушка стояла тут, доки
/// метод сам діставав знімок структури з кешу; тепер знімок приходить
/// параметром (<c>H-3</c>), і повертати порожній перелік означало б, що тест
/// «редактор каже те саме, що публікація» звіряє дві порожнечі.
/// </remarks>
public sealed class RealFormulaEngine : IFormulaEngine
{
    private readonly Parser _parser = new();

    /// <summary>
    /// Справжній фасад — саме він обходить AST.
    /// </summary>
    /// <remarks>
    /// ⚠ Не власна копія обходу: друга проєкція залежностей у тестовому коді
    /// розійшлася б із бойовою, і тест підтверджував би сам себе.
    /// </remarks>
    private readonly Ecr.Infrastructure.Expressions.FormulaEngine _engine =
        new(new Parser(), new Evaluator(new FunctionRegistry()), new TopologicalSorter());

    /// <inheritdoc />
    public ParseResult Parse(string expression, ExpressionDialect dialect)
        => _parser.Parse(expression, dialect);

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ Обчислення теж іде через БОЙОВИЙ фасад, а не через власний
    /// <c>Evaluator</c>. Тут стояла своя копія одного рядка — і вона
    /// приховувала цілу зміну: коли `FormulaEngine` почав передавати
    /// обчислювачу діалект розібраного виразу (`I.14`), підміна цього рядка на
    /// «завжди діалект шаблонів» не робила червоним ЖОДНОГО тесту в жодному
    /// проєкті. Копія в один рядок — теж друга правда.
    /// </remarks>
    public EvaluationResult Evaluate(
        ParsedExpression expression,
        IEvaluationContext context,
        Ecr.Domain.Enums.NumericMode mode = Ecr.Domain.Enums.NumericMode.Strict)
        => _engine.Evaluate(expression, context, mode);

    /// <inheritdoc />
    public DependencyExtraction ExtractDependencies(
        ParsedExpression expression, TemplateVersionSnapshot? snapshot, DependencyContext context)
        => _engine.ExtractDependencies(expression, snapshot, context);

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
