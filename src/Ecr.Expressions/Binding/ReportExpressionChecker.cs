using Ecr.Expressions.Ast;
using Ecr.Expressions.Functions;
using Ecr.Expressions.Parsing;

namespace Ecr.Expressions.Binding;

/// <summary>
/// Те, на що може послатися правило звіту: колонки рядка зрізу і параметри звіту.
/// </summary>
/// <remarks>
/// ⚠ Коди колонок порівнюються ТОЧНО (так їх звіряє й опис звіту —
/// <c>ReportSourceColumns</c>), імена параметрів — без регістру, як усі
/// <c>@Name</c> мови (<c>02b</c> §3.4).
/// </remarks>
public sealed class ReportExpressionScope
{
    private readonly Dictionary<string, ExpressionValueType> _columns;
    private readonly Dictionary<string, ExpressionValueType> _parameters;

    /// <summary>Створює оточення.</summary>
    /// <param name="columns">Код колонки → тип.</param>
    /// <param name="parameters">Ім'я параметра → тип.</param>
    public ReportExpressionScope(
        IEnumerable<KeyValuePair<string, ExpressionValueType>> columns,
        IEnumerable<KeyValuePair<string, ExpressionValueType>> parameters)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(parameters);

        // Індексатором, а не конструктором словника: повтор у запиті — не виняток.
        _columns = new(StringComparer.Ordinal);
        foreach (var (code, type) in columns)
        {
            _columns[code] = type;
        }

        _parameters = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, type) in parameters)
        {
            _parameters[name] = type;
        }
    }

    /// <summary>Тип колонки; <c>null</c> — такої колонки рядок не має.</summary>
    public ExpressionValueType? ColumnType(string code) => _columns.TryGetValue(code, out var t) ? t : null;

    /// <summary>Тип параметра; <c>null</c> — такого параметра звіт не має.</summary>
    public ExpressionValueType? ParameterType(string name) => _parameters.TryGetValue(name, out var t) ? t : null;
}

/// <summary>
/// Прив'язка і типи виразу діалекту <c>Report</c> (<c>02b</c> §8a).
/// </summary>
/// <remarks>
/// ⛔ Заборонені форми посилань відхиляє вже розбір; тут вони відхиляються ВДРУГЕ
/// (<see cref="IsRowColumn"/>), бо дерево може прийти й не з нашого парсера.
/// </remarks>
public static class ReportExpressionChecker
{
    /// <summary>Перевіряє посилання й типи; повертає тип результату.</summary>
    /// <param name="expression">Розібраний вираз діалекту <c>Report</c>.</param>
    /// <param name="scope">Колонки і параметри, на які можна послатися.</param>
    /// <param name="expected">
    /// Очікуваний тип результату (<c>Boolean</c> для умови <c>when</c>); <c>null</c> — довільний.
    /// </param>
    /// <param name="diagnostics">Куди складати зауваження.</param>
    public static ExpressionValueType Check(
        ParsedExpression expression,
        ReportExpressionScope scope,
        ExpressionValueType? expected,
        List<ExpressionDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(diagnostics);

        Bind(expression.Root, scope, diagnostics);

        var actual = new TypeChecker(ReportFunctions.Find)
            .Check(expression.Root, new TypeContext(scope), diagnostics);

        // Null сумісний з усім (§6.2): `NULL` як значення комірки — законний результат.
        if (expected is { } required && actual != required && actual != ExpressionValueType.Null)
        {
            Report(diagnostics, expression.Root,
                "expr.report.resultType", DiagnosticParams.Of(("expected", required.ToString()), ("actual", actual.ToString())),
                $"A result of type {required} was expected, but the expression gives {actual}.");
        }

        return actual;
    }

    /// <summary>Чи це посилання на колонку СВОГО рядка — єдина форма <c>[…]</c> діалекту.</summary>
    public static bool IsRowColumn(CellReferenceNode reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        return reference is { SheetCode: null, TableCode: null, Row: RowSelector.Current, PeriodOffset: 0 };
    }

    private static void Bind(AstNode node, ReportExpressionScope scope, List<ExpressionDiagnostic> diagnostics)
    {
        switch (node)
        {
            case CellReferenceNode reference when !IsRowColumn(reference):
                Report(diagnostics, node,
                    "expr.report.ownRowColumnsOnly", null,
                    "A report rule sees only the columns of its own row: [Code].");
                break;

            case CellReferenceNode reference when scope.ColumnType(reference.ColumnSelector) is null:
                Report(diagnostics, node,
                    "expr.report.columnMissing", DiagnosticParams.Of(("column", reference.ColumnSelector)),
                    $"The report row has no column \"{reference.ColumnSelector}\".");
                break;

            case SymbolReferenceNode { Kind: SymbolKind.Argument } symbol when scope.ParameterType(symbol.Name) is null:
                Report(diagnostics, node,
                    "expr.report.parameterMissing", DiagnosticParams.Of(("name", symbol.Name)),
                    $"The report has no parameter \"@{symbol.Name}\".");
                break;

            case SymbolReferenceNode { Kind: not SymbolKind.Argument } or PeriodPropertyNode:
                Report(diagnostics, node,
                    "expr.report.scope", null,
                    "A report rule sees only the columns of its own row and the report parameters.");
                break;

            case UnaryNode unary:
                Bind(unary.Operand, scope, diagnostics);
                break;

            case BinaryNode binary:
                Bind(binary.Left, scope, diagnostics);
                Bind(binary.Right, scope, diagnostics);
                break;

            case ConditionalNode conditional:
                Bind(conditional.Condition, scope, diagnostics);
                Bind(conditional.WhenTrue, scope, diagnostics);
                Bind(conditional.WhenFalse, scope, diagnostics);
                break;

            case FunctionNode function:
                foreach (var argument in function.Arguments)
                {
                    Bind(argument, scope, diagnostics);
                }

                break;
        }
    }

    private static void Report(
        List<ExpressionDiagnostic> diagnostics,
        AstNode node,
        string messageKey,
        IReadOnlyDictionary<string, string>? messageParams,
        string message)
        => diagnostics.Add(new ExpressionDiagnostic(
            ExpressionErrors.Unresolved, message, node.Position, 1, messageKey, messageParams));

    private sealed class TypeContext(ReportExpressionScope scope) : ITypeContext
    {
        // Невідоме ім'я вже названо прив'язкою; `Null` не додає до нього другої відмови.
        public ExpressionValueType GetReferenceType(CellReferenceNode reference)
            => scope.ColumnType(reference.ColumnSelector) ?? ExpressionValueType.Null;

        public ExpressionValueType GetColumnType(int tableDefId, int columnDefId) => ExpressionValueType.Null;

        public ExpressionValueType GetArgumentType(string name)
            => scope.ParameterType(name) ?? ExpressionValueType.Null;
    }
}
