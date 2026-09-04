using Ecr.Domain.Entities.Configuration;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Parsing;

namespace Ecr.Expressions.Binding;

/// <summary>
/// Витягує залежності виразу для збереження в <c>cfg.FormulaDependency</c>.
/// Зворотний індекс по цій таблиці — основа інкрементного перерахунку.
/// </summary>
public sealed class DependencyExtractor(ReferenceResolver resolver, RangeExpander expander)
{
    /// <summary>Вид залежності: комірка (<c>cfg.FormulaDependency.DependsOnKind</c> = 0).</summary>
    public const byte KindCell = 0;

    /// <summary>Поле шапки документа.</summary>
    public const byte KindHeader = 1;

    /// <summary>Крос-періодне посилання.</summary>
    public const byte KindCrossPeriod = 3;

    /// <summary>Обходить AST і збирає всі залежності.</summary>
    /// <param name="root">Корінь виразу.</param>
    /// <param name="currentTableDefId">Таблиця, в якій живе формула.</param>
    /// <param name="currentRowKey">Рядок формули; <c>null</c> для формул рівня колонки.</param>
    /// <param name="tables">Таблиці для розкриття діапазонів: <c>TableDefId</c> → таблиця.</param>
    /// <param name="diagnostics">Куди складати зауваження публікації.</param>
    /// <param name="currentColumnDefId">Колонка для плейсхолдера <c>{Month}</c>.</param>
    public IReadOnlyList<ExtractedDependency> Extract(
        AstNode root,
        int currentTableDefId,
        string? currentRowKey,
        IReadOnlyDictionary<int, TableDef>? tables = null,
        List<ExpressionDiagnostic>? diagnostics = null,
        int? currentColumnDefId = null)
    {
        ArgumentNullException.ThrowIfNull(root);

        var found = new List<ExtractedDependency>();
        Visit(root, found, currentTableDefId, currentRowKey, tables, diagnostics, currentColumnDefId);
        return found;
    }

    private void Visit(
        AstNode node,
        List<ExtractedDependency> found,
        int currentTableDefId,
        string? currentRowKey,
        IReadOnlyDictionary<int, TableDef>? tables,
        List<ExpressionDiagnostic>? diagnostics,
        int? currentColumnDefId)
    {
        switch (node)
        {
            case CellReferenceNode reference:
                Add(reference, found, currentTableDefId, currentRowKey, tables, diagnostics, currentColumnDefId);
                return;

            case SymbolReferenceNode { Kind: SymbolKind.Header } header:
                found.Add(new ExtractedDependency(
                    KindHeader, null, header.Name, null, null, null, found.Count));
                return;

            case SymbolReferenceNode { Kind: SymbolKind.Formula } formula:
                // Залежність між формулами потрібна саме для топологічного
                // порядку: без неї !Base порахувалася б після того, хто її
                // читає, і результат був би «майже правильним».
                found.Add(new ExtractedDependency(
                    KindCell, null, formula.Name, null, null, null, found.Count));
                return;

            // Календарний контекст не є залежністю: він не змінюється від
            // правки комірок, тож інкрементний перерахунок його не стосується.
            case PeriodPropertyNode:
            case LiteralNode:
            case SymbolReferenceNode:
                return;

            case UnaryNode unary:
                Visit(unary.Operand, found, currentTableDefId, currentRowKey, tables, diagnostics, currentColumnDefId);
                return;

            case BinaryNode binary:
                Visit(binary.Left, found, currentTableDefId, currentRowKey, tables, diagnostics, currentColumnDefId);
                Visit(binary.Right, found, currentTableDefId, currentRowKey, tables, diagnostics, currentColumnDefId);
                return;

            case ConditionalNode conditional:
                Visit(conditional.Condition, found, currentTableDefId, currentRowKey, tables, diagnostics, currentColumnDefId);
                Visit(conditional.WhenTrue, found, currentTableDefId, currentRowKey, tables, diagnostics, currentColumnDefId);
                Visit(conditional.WhenFalse, found, currentTableDefId, currentRowKey, tables, diagnostics, currentColumnDefId);
                return;

            case FunctionNode function:
                foreach (var argument in function.Arguments)
                {
                    Visit(argument, found, currentTableDefId, currentRowKey, tables, diagnostics, currentColumnDefId);
                }

                return;

            default:
                return;
        }
    }

    private void Add(
        CellReferenceNode reference,
        List<ExtractedDependency> found,
        int currentTableDefId,
        string? currentRowKey,
        IReadOnlyDictionary<int, TableDef>? tables,
        List<ExpressionDiagnostic>? diagnostics,
        int? currentColumnDefId)
    {
        var resolved = resolver.Resolve(
            reference, currentTableDefId, currentRowKey, diagnostics, currentColumnDefId);

        if (resolved is null)
        {
            return;
        }

        var kind = reference.PeriodOffset == 0 ? KindCell : KindCrossPeriod;
        var offset = reference.PeriodOffset == 0 ? (short?)null : (short)reference.PeriodOffset;

        if (reference.Row is RowSelector.Range range)
        {
            // ⚠ Діапазон РОЗКРИВАЄТЬСЯ тут, при публікації, і далі не існує.
            // Саме це робить зміну Ordinal після публікації безпечною:
            // формула вже посилається на конкретні рядки.
            if (tables is null || !tables.TryGetValue(resolved.TableDefId, out var table))
            {
                diagnostics?.Add(new ExpressionDiagnostic(
                    ExpressionErrors.Unresolved,
                    "Діапазон неможливо розкрити: таблиця недоступна.",
                    reference.Position, 1));
                return;
            }

            foreach (var rowKey in expander.Expand(table, range.FromRowKey, range.ToRowKey, diagnostics, reference.Position))
            {
                found.Add(new ExtractedDependency(
                    kind, resolved.TableDefId, rowKey, resolved.ColumnDefId, null, offset, found.Count));
            }

            return;
        }

        found.Add(new ExtractedDependency(
            kind, resolved.TableDefId, resolved.RowKey, resolved.ColumnDefId,
            resolved.FilterJson, offset, found.Count));
    }
}

/// <summary>Витягнута залежність.</summary>
/// <param name="DependsOnKind">0 Cell, 1 Header, 2 Registry, 3 CrossPeriod, 4 CrossProject.</param>
/// <param name="TableDefId">Таблиця; <c>null</c> для шапки.</param>
/// <param name="RowKey">Конкретний рядок; <c>null</c> для предиката.</param>
/// <param name="ColumnDefId">Колонка.</param>
/// <param name="FilterJson">Предикат для <c>RowMode = Dynamic</c>.</param>
/// <param name="PeriodOffset">Зсув періоду; <c>null</c> для поточного.</param>
/// <param name="SortOrder">Порядковий номер у списку залежностей формули.</param>
public sealed record ExtractedDependency(
    byte DependsOnKind, int? TableDefId, string? RowKey, int? ColumnDefId,
    string? FilterJson, short? PeriodOffset, int SortOrder);
