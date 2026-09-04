using Ecr.Domain.ValueObjects;

namespace Ecr.Expressions.Evaluation;

/// <summary>Джерело даних для обчислення.</summary>
public interface IEvaluationContext
{
    /// <summary>Значення комірки; відсутня комірка → <c>DefaultValue</c> або <c>null</c> (02b §6.3).</summary>
    public ExpressionValue GetCell(int tableDefId, string rowKey, int columnDefId, int periodOffset);

    /// <summary>Значення рядків за предикатом — для динамічних діапазонів.</summary>
    public IReadOnlyList<ExpressionValue> GetCellsByPredicate(int tableDefId, string filterJson, int columnDefId);

    /// <summary>Аргумент методології (<c>@Name</c>).</summary>
    public ExpressionValue GetArgument(string name);

    /// <summary>Константа методології (<c>CST.Name</c>), резолвлена за категорією і датою.</summary>
    public ExpressionValue GetConstant(string name);

    /// <summary>Результат іншої формули цієї версії (<c>!Name</c>).</summary>
    public ExpressionValue GetFormulaResult(string name);

    /// <summary>Поле шапки документа (<c>HDR.Name</c>).</summary>
    public ExpressionValue GetHeader(string name);

    /// <summary>Календарний контекст. Значення залежать від <c>CalendarMode</c> (D-78).</summary>
    public PeriodContext Period { get; }

    /// <summary>Конверсія одиниць для функції <c>CONVERT</c>.</summary>
    public ExpressionValue Convert(ExpressionValue value, string fromUnitCode, string toUnitCode);
}
