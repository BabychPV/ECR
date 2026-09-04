using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Evaluation;

/// <summary>Джерело даних для обчислення.</summary>
/// <remarks>
/// ⚠ Обчислювач читає посилання ЛИШЕ через <see cref="Read"/>, якому віддає
/// вузол AST цілком. Причина: у дереві посилання записане КОДАМИ
/// (<c>[Water_07].[Main].[7001001].[Jan]</c>), а сховище адресується
/// ідентифікаторами, і резолвінг кодів потребує знімка метаданих, якого в
/// обчислювача немає й не має бути. Решта членів — нижчий рівень, на якому
/// реалізація <see cref="Read"/> і будується.
/// </remarks>
public interface IEvaluationContext
{
    /// <summary>
    /// Значення посилання: одна комірка, діапазон або предикат.
    /// </summary>
    /// <remarks>
    /// Порожній список — це не помилка, а порожня множина: агрегат над нею
    /// має визначений результат (02b §6.1). Нерезолвлене в рантаймі посилання
    /// повертається як єдиний елемент <c>#REF</c>, а вихід за межі проєкту за
    /// <c>PeriodOffset</c> — як <c>null</c>: січень не має попереднього
    /// місяця, і це нормальна ситуація, а не збій (02b §3.2).
    /// </remarks>
    /// <param name="reference">Вузол посилання з дерева виразу.</param>
    public IReadOnlyList<ExpressionValue> Read(CellReferenceNode reference);

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
