using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
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
    TopologicalSorter sorter) : IFormulaEngine
{
    /// <summary>Обчислювач для режиму.</summary>
    /// <remarks>
    /// ⚠ Два екземпляри на весь застосунок, а не новий на кожен вираз:
    /// нічний перерахунок — це мільйони викликів, і алокація на кожен була б
    /// платою за ніщо. Обидва без стану, тож спільні безпечно.
    /// </remarks>
    /// <param name="mode">Режим версії.</param>
    private Evaluator EvaluatorFor(Ecr.Domain.Enums.NumericMode mode)
        => mode == Ecr.Domain.Enums.NumericMode.Legacy ? _legacy : evaluator;

    /// <summary>Обчислювач із арифметикою чинної системи.</summary>
    private readonly Evaluator _legacy =
        evaluator.WithArithmetic(
            EvaluationArithmetics.For(Ecr.Domain.Enums.NumericMode.Legacy));

    /// <summary>
    /// Знімок «структури немає» для виклику без версії шаблону.
    /// </summary>
    /// <remarks>
    /// ⚠ Порожній знімок, а не <c>null</c> усередині: <c>ReferenceResolver</c>
    /// вимагає знімок, і без нього посилання на комірку впало б винятком
    /// замість того, щоб стати зауваженням. У діалекті методологій таких
    /// посилань немає за побудовою, тож сюди резолвер не заглядає взагалі.
    /// </remarks>
    private static readonly TemplateVersionSnapshot Structureless = new(
        TemplateVersionId: 0,
        PresentationRevision: 0,
        Sheets: [],
        ColumnsById: new Dictionary<int, ColumnDef>(),
        RowsByKey: new Dictionary<(int TableDefId, string RowKey), RowDef>());

    /// <inheritdoc />
    public ParseResult Parse(string expression, ExpressionDialect dialect)
        => parser.Parse(expression, dialect);

    /// <inheritdoc />
    public DependencyExtraction ExtractDependencies(
        ParsedExpression expression, TemplateVersionSnapshot? snapshot, DependencyContext context)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(context);

        var structure = snapshot ?? Structureless;

        var tables = structure.Sheets
            .SelectMany(s => s.Tables)
            .ToDictionary(t => t.Id);

        // ⛔ Зауваження резолвінгу збираються тим самим обходом і повертаються
        // разом: без них перевірки 2, 5 і 12 з `02b` §12 просто зникли б —
        // нерезолвлене посилання проходило б публікацію і ставало б `#REF` у
        // звіті через місяць.
        var diagnostics = new List<ExpressionDiagnostic>();
        var extractor = new DependencyExtractor(new ReferenceResolver(structure), new RangeExpander());

        var found = extractor.Extract(
            expression.Root,
            context.CurrentTableDefId,
            context.CurrentRowKey,
            tables,
            diagnostics,
            context.CurrentColumnDefId);

        return new DependencyExtraction(
            [.. found.Select(d => new FormulaDependencyRef(
                d.DependsOnKind, d.TableDefId, d.RowKey, d.ColumnDefId,
                d.FilterJson, d.PeriodOffset, d.SortOrder))],
            diagnostics);
    }

    /// <inheritdoc />
    public EvaluationResult Evaluate(
        ParsedExpression expression,
        IEvaluationContext context,
        Ecr.Domain.Enums.NumericMode mode = Ecr.Domain.Enums.NumericMode.Strict)
    {
        ArgumentNullException.ThrowIfNull(expression);

        // Помилка обчислення — це ЗНАЧЕННЯ всередині ExpressionValue
        // (#DIV/0, #REF, #VALUE), а не запис у Diagnostics: одна зіпсована
        // комірка не валить перерахунок таблиці (02b §6.4).
        //
        // ⛔ Діалект береться з РОЗБОРУ, а не з параметра виклику: приймати й
        // рахувати мусить одна мова. Доки обчислювач діалекту не знав,
        // `POWER(2;3)` у методології рахувався вигаданим набором `02b` §8,
        // хоча чинний рушій такого імені не знає (`Q-082`).
        // ⛔ Арифметика береться за РЕЖИМОМ версії (`I.7`). Доти
        // обчислювач був один і завжди `Strict`, тож `Legacy` рахував у
        // `decimal` — і режим, який існує заради відтворення чисел,
        // відтворював інші.

        return new EvaluationResult(
            EvaluatorFor(mode).Evaluate(expression.Root, context, expression.Dialect), []);
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
