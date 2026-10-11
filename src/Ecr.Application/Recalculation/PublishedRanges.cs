using Ecr.Domain.Entities.Configuration;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;

namespace Ecr.Application.Recalculation;

/// <summary>
/// Ключі діапазонів <c>[A:B]</c> формули так, як їх розкрила ПУБЛІКАЦІЯ (<c>cfg.FormulaDependency</c>),
/// а не поточний <c>RowDef.Ordinal</c> (ФВ-0.2, D-17).
/// </summary>
/// <remarks>
/// ⛔ Одне місце для всіх, хто мусить читати діапазон так само, як це зробив перерахунок: рантайм
/// (<see cref="SliceEvaluationContext"/>) і Excel-експорт формул. Два незалежні розкриття за
/// поточним порядком давали б різні числа в системі й у книзі — саме той клас розбіжності, який
/// ФВ-0.2 забороняє.
/// <para>
/// ⚠ Видобувач залежностей розкриває діапазон у тому самому порядку обходу, що й при публікації
/// (<c>PublishChecks.Dependencies</c>): ті самі таблиця, рядок і колонка формули, тому
/// <c>SortOrder</c> першого ребра посилання збігається з позицією у збереженому графі.
/// </para>
/// </remarks>
public static class PublishedRanges
{
    /// <summary>Ребра ФОРМУЛ графа версії: формула → (<c>SortOrder</c> → ребро).</summary>
    /// <param name="graph">Граф залежностей версії (<c>ITemplateVersionStore.ListFormulaDependenciesAsync</c>).</param>
    public static Dictionary<int, Dictionary<int, FormulaDependency>> EdgesByFormula(
        IEnumerable<FormulaDependency> graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        return graph
            .Where(d => d.SourceKind == 0 && d.FormulaDefId is not null)
            .GroupBy(d => d.FormulaDefId!.Value)
            .ToDictionary(g => g.Key, g => g.ToDictionary(d => d.SortOrder));
    }

    /// <summary>Ключі діапазонів формули за позицією посилання в тексті виразу.</summary>
    /// <param name="formula">Формула.</param>
    /// <param name="root">Корінь розібраного виразу цієї формули.</param>
    /// <param name="table">Таблиця формули.</param>
    /// <param name="tables">Усі живі таблиці версії за <c>Id</c>.</param>
    /// <param name="resolver">Резолвер посилань знімка версії.</param>
    /// <param name="edges">Ребра цієї формули в графі версії за <c>SortOrder</c>.</param>
    /// <returns>
    /// Позиція посилання → ключі рядків; діапазону, для якого немає збереженого пробігу, у словнику
    /// немає (чернетка, граф не збережено).
    /// </returns>
    public static IReadOnlyDictionary<int, IReadOnlyList<string>> Resolve(
        FormulaDef formula,
        AstNode root,
        TableDef table,
        IReadOnlyDictionary<int, TableDef> tables,
        ReferenceResolver resolver,
        IReadOnlyDictionary<int, FormulaDependency> edges)
    {
        ArgumentNullException.ThrowIfNull(formula);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(edges);

        var expander = new PublishedRangeExpander(edges);
        new DependencyExtractor(resolver, expander).Extract(
            root, table.Id, FormulaOutputs.RowKeyOf(table, formula), tables,
            diagnostics: null, formula.ColumnDefId);

        return expander.ByPosition;
    }
}
