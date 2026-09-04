using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;

namespace Ecr.Application.Validation;

/// <summary>
/// Основа контексту для виразів ПРАВИЛ.
/// </summary>
/// <remarks>
/// ⚠ Правило валідації бачить лише дані документа. Аргументи методології,
/// константи, посилання на інші формули і конверсія одиниць тут недоступні —
/// не «поки не реалізовано», а за побудовою: правило, яке лізе в методологію,
/// перестає бути перевіркою даних і стає ще одним обчисленням, результат якого
/// теж треба перевіряти.
///
/// Календарний контекст дозволений: перевірки на кшталт «витрата за добу не
/// перевищує ліміт» без нього неможливі.
/// </remarks>
public abstract class ValidationEvaluationContext : IEvaluationContext
{
    /// <inheritdoc />
    public PeriodContext Period { get; init; } = new(
        DateOnly.FromDateTime(DateTime.UnixEpoch),
        DateOnly.FromDateTime(DateTime.UnixEpoch),
        CalendarMode.Actual, 1970, 1);

    /// <inheritdoc />
    public abstract IReadOnlyList<ExpressionValue> Read(CellReferenceNode reference);

    /// <inheritdoc />
    public ExpressionValue GetCell(int tableDefId, string rowKey, int columnDefId, int periodOffset)
        => ExpressionValue.Null;

    /// <inheritdoc />
    public IReadOnlyList<ExpressionValue> GetCellsByPredicate(int tableDefId, string filterJson, int columnDefId)
        => [];

    /// <inheritdoc />
    public ExpressionValue GetArgument(string name) => ExpressionValue.Error(ExpressionErrors.BadReference);

    /// <inheritdoc />
    public ExpressionValue GetConstant(string name) => ExpressionValue.Error(ExpressionErrors.BadReference);

    /// <inheritdoc />
    public ExpressionValue GetFormulaResult(string name) => ExpressionValue.Error(ExpressionErrors.BadReference);

    /// <inheritdoc />
    public ExpressionValue GetHeader(string name) => ExpressionValue.Null;

    /// <inheritdoc />
    public ExpressionValue Convert(ExpressionValue value, string fromUnitCode, string toUnitCode)
        => ExpressionValue.Error(ExpressionErrors.BadUnit);
}
