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
    /// <summary>Порожній знімок довідника — конструктор без параметра.</summary>
    private static readonly IReadOnlyDictionary<long, IReadOnlyDictionary<string, ExpressionValue>>
        EmptyRegistryFields = new Dictionary<long, IReadOnlyDictionary<string, ExpressionValue>>();

    private readonly IReadOnlyDictionary<long, IReadOnlyDictionary<string, ExpressionValue>> _registryFields;

    /// <param name="registryFields">
    /// Знімок полів довідника для <c>REGFIELD</c> у правилах (id запису → код
    /// поля → значення); <c>null</c> — правило без REGFIELD, рівнозначно
    /// порожньому знімку. Той самий принцип, що в
    /// <c>SliceEvaluationContext</c>: контекст правила синхронний, а довідник
    /// читається заздалегідь.
    /// </param>
    protected ValidationEvaluationContext(
        IReadOnlyDictionary<long, IReadOnlyDictionary<string, ExpressionValue>>? registryFields = null)
        => _registryFields = registryFields ?? EmptyRegistryFields;

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
    /// <remarks>
    /// ⛔ Реальні дані з <see cref="_registryFields"/>, не заглушка: правило
    /// рівня рядка/таблиці/документа так само здатне прочитати поле запису,
    /// на який показує Lookup-колонка, як і формула шаблону — той самий
    /// сенс, що вже описаний у класовому коментарі («правило бачить лише
    /// дані документа», а Lookup-посилання — це дані документа).
    /// </remarks>
    public ExpressionValue GetRegistryField(long registryEntryId, string fieldCode)
        => _registryFields.TryGetValue(registryEntryId, out var byField)
           && byField.TryGetValue(fieldCode, out var value)
            ? value
            : ExpressionValue.Error(ExpressionErrors.BadReference);

    /// <inheritdoc />
    public ExpressionValue Convert(ExpressionValue value, string fromUnitCode, string toUnitCode)
        => ExpressionValue.Error(ExpressionErrors.BadUnit);
}
