using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Functions;
using Ecr.Expressions.Parsing;

namespace Ecr.Application.Documents.FormulaOverride;

/// <summary>Тип результату формули для ручного значення замість неї (D-461): число, текст чи невідомо.</summary>
public enum FormulaResultKind
{
    /// <summary>Статично не видно (напр. посилання на іншу формулу) — вирішує наявне значення комірки.</summary>
    Unknown = 0,

    /// <summary>Число (а також булеве чи дата — у колонці формули вони не текст).</summary>
    Number = 1,

    /// <summary>Текст (напр. <c>REGFIELD</c> текстового поля довідника).</summary>
    Text = 2,
}

/// <summary>
/// ✎ P3 06.11 (Аудит-2): тип результату формули з її ВИРАЗУ, а не з наявного значення комірки. Порожня комірка
/// числової формули типу не казала, і текст ручним значенням приймався. Тип береться з розбору
/// (<see cref="ParsedExpression.ResultType"/>: арифметика, <c>SUM</c> — число); де розбір каже «невідомо»
/// (<c>IF</c>, <c>IFERROR</c>, <c>REGFIELD</c>, посилання на колонку) — малий обхід дерева: гілки, тип поля довідника,
/// тип колонки.
/// </summary>
public static class FormulaResultKinds
{
    private static readonly FunctionRegistry Functions = new();

    /// <summary>Тип результату формули.</summary>
    /// <param name="formula">Формула.</param>
    /// <param name="table">Таблиця формули (колонки для посилань і <c>REGFIELD</c>).</param>
    /// <param name="registries">Сховище довідників — тип поля <c>REGFIELD</c>; <c>null</c> — поле невідоме.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<FormulaResultKind> OfAsync(
        FormulaDef formula, TableDef table, IRegistryStore? registries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(formula);
        ArgumentNullException.ThrowIfNull(table);

        var parsed = new Parser().Parse(formula.Expression, formula.Dialect);
        if (!parsed.IsSuccess || parsed.Expression is not { } expression)
        {
            return FormulaResultKind.Unknown;
        }

        var shape = Of(expression.ResultType);
        if (shape != Kind.Neutral)
        {
            return shape == Kind.Text ? FormulaResultKind.Text : FormulaResultKind.Number;
        }

        return await InferAsync(expression.Root, table, registries, ct).ConfigureAwait(false) switch
        {
            Kind.Text => FormulaResultKind.Text,
            Kind.Number => FormulaResultKind.Number,
            _ => FormulaResultKind.Unknown,
        };
    }

    /// <summary>Внутрішній тип вузла: <see cref="Neutral"/> — літерал NULL / форма без власного типу (не голосує).</summary>
    private enum Kind
    {
        Neutral = 0,
        Unknown = 1,
        Number = 2,
        Text = 3,
    }

    private static Kind Of(ExpressionValueType type) => type switch
    {
        ExpressionValueType.Text => Kind.Text,
        ExpressionValueType.Number or ExpressionValueType.Boolean or ExpressionValueType.Date => Kind.Number,
        _ => Kind.Neutral,
    };

    /// <summary>Гілки: будь-яка текстова — текст; будь-яка невідома — невідомо; NULL не голосує.</summary>
    private static Kind Combine(Kind a, Kind b)
        => a == Kind.Text || b == Kind.Text ? Kind.Text
            : a == Kind.Unknown || b == Kind.Unknown ? Kind.Unknown
            : a == Kind.Number || b == Kind.Number ? Kind.Number
            : Kind.Neutral;

    private static async Task<Kind> InferAsync(AstNode node, TableDef table, IRegistryStore? registries, CancellationToken ct)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();

        switch (node)
        {
            case LiteralNode literal:
                return Of(literal.Type);

            case CellReferenceNode reference:
                return ColumnKind(ColumnOf(table, reference));

            case UnaryNode { Operator: UnaryOperator.Not }:
                return Kind.Number;

            case UnaryNode unary:
                return await InferAsync(unary.Operand, table, registries, ct).ConfigureAwait(false);

            case ConditionalNode conditional:
                return Combine(
                    await InferAsync(conditional.WhenTrue, table, registries, ct).ConfigureAwait(false),
                    await InferAsync(conditional.WhenFalse, table, registries, ct).ConfigureAwait(false));

            case BinaryNode { Operator: BinaryOperator.Concat }:
                return Kind.Text;

            case BinaryNode { Operator: BinaryOperator.Add } add:
            {
                var left = await InferAsync(add.Left, table, registries, ct).ConfigureAwait(false);
                var right = await InferAsync(add.Right, table, registries, ct).ConfigureAwait(false);
                return left == Kind.Text || right == Kind.Text ? Kind.Text
                    : left == Kind.Number && right == Kind.Number ? Kind.Number
                    : Kind.Unknown;
            }

            case BinaryNode:
                return Kind.Number;

            case FunctionNode { Name: var name, Arguments: [_, var whenTrue, var whenFalse] }
                when string.Equals(name, "IF", StringComparison.OrdinalIgnoreCase):
                return Combine(
                    await InferAsync(whenTrue, table, registries, ct).ConfigureAwait(false),
                    await InferAsync(whenFalse, table, registries, ct).ConfigureAwait(false));

            case FunctionNode { Name: var name, Arguments: [var value, var fallback] }
                when string.Equals(name, "IFERROR", StringComparison.OrdinalIgnoreCase):
                return Combine(
                    await InferAsync(value, table, registries, ct).ConfigureAwait(false),
                    await InferAsync(fallback, table, registries, ct).ConfigureAwait(false));

            case FunctionNode { Name: var name, Arguments: [CellReferenceNode source, LiteralNode { Value: string field }] }
                when string.Equals(name, "REGFIELD", StringComparison.OrdinalIgnoreCase):
                return await RegistryFieldKindAsync(ColumnOf(table, source), field, registries, ct).ConfigureAwait(false);

            case FunctionNode function:
                return Functions.GetSignature(function.Name)?.ResultType is { } type && Of(type) is var kind
                       && kind != Kind.Neutral
                    ? kind
                    : Kind.Unknown;

            default:
                return Kind.Unknown;
        }
    }

    /// <summary>Колонка своєї таблиці за кодом; посилання на інший аркуш/таблицю — невідомо.</summary>
    private static ColumnDef? ColumnOf(TableDef table, CellReferenceNode reference)
        => reference.SheetCode is null && reference.TableCode is null
            ? table.Columns.FirstOrDefault(c => !c.IsDeleted
                && string.Equals(c.Code, reference.ColumnSelector, StringComparison.OrdinalIgnoreCase))
            : null;

    private static Kind ColumnKind(ColumnDef? column) => column?.DataType switch
    {
        null or CellDataType.Formula or CellDataType.Calculated => Kind.Unknown,
        CellDataType.String => Kind.Text,
        _ => Kind.Number,
    };

    private static async Task<Kind> RegistryFieldKindAsync(
        ColumnDef? source, string field, IRegistryStore? registries, CancellationToken ct)
    {
        if (registries is null || source?.LookupRegistryDefId is not { } registryDefId)
        {
            return Kind.Unknown;
        }

        var definition = await registries.FindDefinitionByIdAsync(registryDefId, ct).ConfigureAwait(false);
        return definition?.Fields.FirstOrDefault(f => string.Equals(f.Code, field, StringComparison.OrdinalIgnoreCase))
            ?.DataType switch
        {
            null => Kind.Unknown,
            CellDataType.String or CellDataType.Unit => Kind.Text,
            _ => Kind.Number,
        };
    }
}
