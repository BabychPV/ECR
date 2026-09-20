using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Parsing;

namespace Ecr.Expressions.Evaluation;

/// <summary>
/// Контекст обчислення діалекту <c>Report</c>: ОДИН рядок зрізу і параметри звіту.
/// </summary>
/// <remarks>
/// ⛔ Усе, що веде за межі рядка, дає <c>#REF</c>: даних документа, констант і
/// шапки тут немає за побудовою (<c>ФВ-10.3</c>), а не «поки що».
/// </remarks>
public sealed class ReportRowContext : IEvaluationContext
{
    private readonly Dictionary<string, ExpressionValue> _columns;
    private readonly Dictionary<string, ExpressionValue> _parameters;

    /// <summary>Створює контекст рядка.</summary>
    /// <param name="columns">
    /// Код колонки → значення: <c>decimal</c> (і цілі), <c>string</c>, <c>DateOnly</c>,
    /// <c>DateTime</c>, <c>bool</c> або <c>null</c>.
    /// </param>
    /// <param name="parameters">Ім'я параметра звіту → значення тих самих типів.</param>
    public ReportRowContext(
        IEnumerable<KeyValuePair<string, object?>> columns,
        IEnumerable<KeyValuePair<string, object?>> parameters)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(parameters);

        // Ті самі правила імен, що й у `ReportExpressionScope`.
        _columns = columns.ToDictionary(c => c.Key, c => ToValue(c.Value), StringComparer.Ordinal);
        _parameters = parameters.ToDictionary(p => p.Key, p => ToValue(p.Value), StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public PeriodContext Period
        => throw new InvalidOperationException("Діалект звітів не має календарного контексту.");

    /// <inheritdoc />
    public IReadOnlyList<ExpressionValue> Read(CellReferenceNode reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        return ReportExpressionChecker.IsRowColumn(reference)
               && _columns.TryGetValue(reference.ColumnSelector, out var value)
            ? [value]
            : [ExpressionValue.Error(ExpressionErrors.BadReference)];
    }

    /// <inheritdoc />
    public ExpressionValue GetArgument(string name)
        => _parameters.TryGetValue(name, out var value)
            ? value
            : ExpressionValue.Error(ExpressionErrors.ArgumentNotFound);

    /// <inheritdoc />
    public ExpressionValue GetCell(int tableDefId, string rowKey, int columnDefId, int periodOffset) => Outside;

    /// <inheritdoc />
    public IReadOnlyList<ExpressionValue> GetCellsByPredicate(int tableDefId, string filterJson, int columnDefId)
        => [Outside];

    /// <inheritdoc />
    public ExpressionValue GetConstant(string name) => Outside;

    /// <inheritdoc />
    public ExpressionValue GetFormulaResult(string name) => Outside;

    /// <inheritdoc />
    public ExpressionValue GetHeader(string name) => Outside;

    /// <inheritdoc />
    public ExpressionValue Convert(ExpressionValue value, string fromUnitCode, string toUnitCode) => Outside;

    private static ExpressionValue Outside => ExpressionValue.Error(ExpressionErrors.BadReference);

    private static ExpressionValue ToValue(object? value)
        => value switch
        {
            null => ExpressionValue.Null,
            decimal number => ExpressionValue.Number(number),
            int or long or short or byte => ExpressionValue.Number(System.Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture)),
            string text => ExpressionValue.Text(text),
            bool flag => ExpressionValue.Boolean(flag),
            DateOnly date => ExpressionValue.Date(date.ToDateTime(TimeOnly.MinValue)),
            DateTime moment => ExpressionValue.Date(moment),

            // ⚠ `double` сюди не приймається навмисно: арифметика діалекту — decimal.
            _ => ExpressionValue.Error(ExpressionErrors.BadValue),
        };
}

/// <summary>Єдина точка обчислення виразів діалекту <c>Report</c>.</summary>
public static class ReportEvaluation
{
    /// <summary>
    /// Арифметика діалекту — <c>Strict</c> (наскрізний <c>decimal</c>, округлення від нуля).
    /// </summary>
    /// <remarks>
    /// ⛔ Вибір ЯВНИЙ, а не успадкований від версії методології: <c>Legacy</c>
    /// існує, щоб відтворити числа чинного рушія, а правила звіту — нова
    /// функціональність, і відтворювати їм нічого (<c>02b</c> §5).
    /// </remarks>
    public const NumericMode Arithmetic = NumericMode.Strict;

    private static readonly Evaluator Evaluator =
        new(new Functions.FunctionRegistry(), EvaluationArithmetics.For(Arithmetic));

    /// <summary>Обчислює вираз над рядком — тим самим обчислювачем і під тим самим бюджетом.</summary>
    /// <param name="expression">Розібраний вираз діалекту <c>Report</c>.</param>
    /// <param name="row">Рядок зрізу і параметри звіту.</param>
    public static ExpressionValue Evaluate(ParsedExpression expression, ReportRowContext row)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(row);

        if (expression.Dialect != ExpressionDialect.Report)
        {
            throw new ArgumentException("Очікувався вираз діалекту Report.", nameof(expression));
        }

        return Evaluator.Evaluate(expression.Root, row, ExpressionDialect.Report);
    }
}
