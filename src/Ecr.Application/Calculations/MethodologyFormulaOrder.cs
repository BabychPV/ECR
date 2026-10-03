using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;

namespace Ecr.Application.Calculations;

/// <summary>
/// Порядок обчислення формул версії, яку ще не опублікували (аудит L7-02).
/// </summary>
/// <remarks>
/// ⛔ Що було. <c>EvaluationOrder</c> ставить лише публікація, ПІСЛЯ золотого
/// прогону; у чернетки він нульовий, і модуль рахував її в порядку <c>Id</c> —
/// тобто в порядку створення. Формула, що читає пізніше створену
/// (<c>tons = !M * …</c>, а <c>M</c> додали потім), отримувала <c>#REF</c>,
/// вихід не писався, і публікація правильної методології відхилялась
/// «золотий набір розійшовся»; симуляція показувала те саме.
/// <para>
/// ⚠ Ребра — тим самим правилом, що й у публікації: залежності рушія
/// (<see cref="IFormulaEngine.ExtractDependencies"/>) і
/// <see cref="MethodologyReferenceResolver"/>, лише локальні. Сортування —
/// тим самим <see cref="IFormulaEngine.BuildEvaluationOrder"/>, тож
/// порядок чернетки дорівнює тому, який публікація запише в
/// <c>EvaluationOrder</c>: золотий тест перевіряє той порядок, що піде в
/// продуктив.
/// </para>
/// <para>
/// ⚠ Цикл, невірний вираз чи незбережена формула — порядок <c>Id</c>, як
/// раніше: публікація відхилить таку версію сама, з назвою причини.
/// </para>
/// </remarks>
public static class MethodologyFormulaOrder
{
    /// <summary>Чи версія ще без порядку обчислення (чернетка).</summary>
    public static bool IsUnordered(IReadOnlyList<MethodologyFormula> formulas)
    {
        ArgumentNullException.ThrowIfNull(formulas);
        return formulas.Count > 1 && formulas.Any(f => f.EvaluationOrder == 0);
    }

    /// <summary>Формули в топологічному порядку; за неможливості — за <c>Id</c>.</summary>
    public static IReadOnlyList<MethodologyFormula> Topological(
        IReadOnlyList<MethodologyFormula> formulas, IFormulaEngine formulaEngine)
    {
        ArgumentNullException.ThrowIfNull(formulas);
        ArgumentNullException.ThrowIfNull(formulaEngine);

        var byId = formulas.OrderBy(f => f.Id).ToList();
        if (formulas.Any(f => !f.IsPersisted) || formulas.Select(f => f.Id).Distinct().Count() != formulas.Count)
        {
            return byId;
        }

        var byCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var formula in formulas)
        {
            byCode.TryAdd(formula.Code, formula.Id);
        }

        var nodes = new List<FormulaNode>(formulas.Count);
        foreach (var formula in formulas)
        {
            nodes.Add(new FormulaNode(
                formula.Id, TableDefId: 0, FormulaScope.Column, ColumnDefId: null, RowDefId: null,
                Edges(formula, byCode, formulaEngine)));
        }

        var ordering = formulaEngine.BuildEvaluationOrder(nodes);
        if (!ordering.IsSuccess || ordering.Order.Count != formulas.Count)
        {
            return byId;
        }

        var index = formulas.ToDictionary(f => f.Id);
        return [.. ordering.Order.Select(id => index[id])];
    }

    private static List<int> Edges(
        MethodologyFormula formula, Dictionary<string, int> byCode, IFormulaEngine formulaEngine)
    {
        var parsed = formulaEngine.Parse(formula.Expression, ExpressionDialect.Methodology);
        if (parsed.Expression is null)
        {
            return [];
        }

        var extraction = formulaEngine.ExtractDependencies(
            parsed.Expression,
            snapshot: null,
            new DependencyContext(CurrentTableDefId: 0, CurrentRowKey: null, CurrentColumnDefId: null));

        var edges = new List<int>();
        foreach (var code in extraction.Dependencies.Select(d => d.FormulaCode))
        {
            if (code is not null
                && MethodologyReferenceResolver.Resolve(code, byCode, []) is
                    { Outcome: MethodologyReferenceOutcome.Local, FormulaId: { } id })
            {
                edges.Add(id);
            }
        }

        return edges;
    }
}
