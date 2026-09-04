using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Binding;

/// <summary>
/// Витягує залежності виразу для збереження в <c>cfg.FormulaDependency</c>.
/// Зворотний індекс по цій таблиці — основа інкрементного перерахунку.
/// </summary>
public sealed class DependencyExtractor(ReferenceResolver resolver, RangeExpander expander)
{
    /// <summary>Обходить AST і збирає всі залежності.</summary>
    public IReadOnlyList<ExtractedDependency> Extract(AstNode root, int currentTableDefId, string? currentRowKey)
        => throw new NotImplementedException(
            "TODO: обійти дерево; для CellReferenceNode: Single → одна залежність, " +
            "Range → розкрити через expander і створити залежність на КОЖЕН рядок із SortOrder, " +
            "Predicate → одна залежність із FilterJson і RowKey = null; " +
            "для SymbolReferenceNode(Formula) → залежність між формулами (для топологічного порядку); " +
            "для PeriodPropertyNode → залежності немає, це календарний контекст.");
}

/// <summary>Витягнута залежність.</summary>
public sealed record ExtractedDependency(
    byte DependsOnKind, int? TableDefId, string? RowKey, int? ColumnDefId,
    string? FilterJson, short? PeriodOffset, int SortOrder);
