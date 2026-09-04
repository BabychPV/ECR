using Ecr.Application.Ports;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;

namespace Ecr.Calculations;

/// <summary>
/// Контекст обчислення для діалекту методологій.
/// </summary>
/// <remarks>
/// ⛔ Посилань на комірки документа тут немає **за побудовою**: методологія їх
/// не читає (02b §3.4). Дані приходять аргументами (<c>@Arg</c>), які зібрав
/// <see cref="CalculationInputBuilder"/> — саме це робить методологію
/// переносною між проєктами і придатною до симуляції на будь-яких числах.
/// <para>
/// ⚠ Конверсія одиниць бере коефіцієнти з переданого довідника, а не з бази:
/// модуль рахує в пам'яті воркера, і похід у сховище на кожен <c>CONVERT</c>
/// зруйнував би бюджет 10 хвилин на річний перерахунок.
/// </para>
/// </remarks>
internal sealed class MethodologyEvaluationContext(
    PeriodContext period,
    IReadOnlyDictionary<string, ExpressionValue> arguments,
    IReadOnlyDictionary<string, ExpressionValue> constants,
    UnitTable units) : IEvaluationContext
{
    private readonly Dictionary<string, ExpressionValue> _formulaResults =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public PeriodContext Period { get; } = period;

    /// <summary>Записує результат формули, доступний далі як <c>!Code</c>.</summary>
    /// <param name="code">Код формули.</param>
    /// <param name="value">Обчислене значення.</param>
    public void SetFormulaResult(string code, ExpressionValue value) => _formulaResults[code] = value;

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ Методологія не читає комірки: якби читала, вона знала б структуру
    /// конкретного шаблону і перестала б переноситися між проєктами.
    /// </remarks>
    public IReadOnlyList<ExpressionValue> Read(CellReferenceNode reference)
        => [ExpressionValue.Error(ExpressionErrors.BadReference)];

    /// <inheritdoc />
    public ExpressionValue GetCell(int tableDefId, string rowKey, int columnDefId, int periodOffset)
        => ExpressionValue.Error(ExpressionErrors.BadReference);

    /// <inheritdoc />
    public IReadOnlyList<ExpressionValue> GetCellsByPredicate(
        int tableDefId, string filterJson, int columnDefId) => [];

    /// <inheritdoc />
    public ExpressionValue GetArgument(string name)
        => arguments.TryGetValue(name, out var value) ? value : ExpressionValue.Null;

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Відсутня константа — це <c>#REF</c>, а не <c>null</c>. Константа, якої
    /// немає, означає, що методологію налаштували не до кінця; мовчазний
    /// <c>null</c> перетворив би це на нульовий викид у звіті.
    /// </remarks>
    public ExpressionValue GetConstant(string name)
        => constants.TryGetValue(name, out var value)
            ? value
            : ExpressionValue.Error(ExpressionErrors.BadReference);

    /// <inheritdoc />
    public ExpressionValue GetFormulaResult(string name)
        => _formulaResults.TryGetValue(name, out var value)
            ? value
            : ExpressionValue.Error(ExpressionErrors.BadReference);

    /// <inheritdoc />
    /// <remarks>Шапки документа методологія теж не бачить — з тієї самої причини.</remarks>
    public ExpressionValue GetHeader(string name)
        => ExpressionValue.Error(ExpressionErrors.BadReference);

    /// <inheritdoc />
    public ExpressionValue Convert(ExpressionValue value, string fromUnitCode, string toUnitCode)
        => units.Convert(value, fromUnitCode, toUnitCode);
}

/// <summary>
/// Довідник одиниць у пам'яті воркера — те, чим модуль виконує <c>CONVERT</c>.
/// </summary>
/// <remarks>
/// Завантажується раз на прогін. Похід у базу на кожну конверсію дав би
/// мільйони запитів на річний перерахунок і сам собою вибрав би весь бюджет.
/// </remarks>
public sealed class UnitTable
{
    private readonly Dictionary<string, Entry> _units = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (decimal Factor, decimal Offset)> _explicit =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Одиниця довідника.</summary>
    /// <param name="Dimension">Розмірність; конверсія можлива лише в її межах.</param>
    /// <param name="FactorToBase">Множник переходу до базової одиниці.</param>
    /// <param name="OffsetToBase">Зсув; ненульовий лише в температури.</param>
    private readonly record struct Entry(byte Dimension, decimal FactorToBase, decimal OffsetToBase);

    /// <summary>Додає одиницю.</summary>
    /// <param name="code">Код одиниці.</param>
    /// <param name="dimension">Розмірність.</param>
    /// <param name="factorToBase">Множник до базової.</param>
    /// <param name="offsetToBase">Зсув до базової.</param>
    public void Add(string code, byte dimension, decimal factorToBase, decimal offsetToBase = 0m)
        => _units[code] = new Entry(dimension, factorToBase, offsetToBase);

    /// <summary>Додає явну конверсію <c>uom.Conversion</c>.</summary>
    /// <param name="from">Вихідна одиниця.</param>
    /// <param name="to">Цільова одиниця.</param>
    /// <param name="factor">Коефіцієнт.</param>
    /// <param name="offset">Зсув.</param>
    /// <remarks>
    /// ⚠ Явна конверсія має пріоритет над маршрутом через базу — і це не
    /// оптимізація. <c>LegacyPinned</c> існує саме щоб відтворити число чинної
    /// системи, пораховане за іншим коефіцієнтом.
    /// </remarks>
    public void AddConversion(string from, string to, decimal factor, decimal offset = 0m)
        => _explicit[$"{from}|{to}"] = (factor, offset);

    /// <summary>Виконує конверсію значення.</summary>
    /// <param name="value">Значення у вихідній одиниці.</param>
    /// <param name="fromUnitCode">Код вихідної одиниці.</param>
    /// <param name="toUnitCode">Код цільової одиниці.</param>
    public ExpressionValue Convert(ExpressionValue value, string fromUnitCode, string toUnitCode)
    {
        if (value.IsError || value.IsNull)
        {
            return value;
        }

        if (string.Equals(fromUnitCode, toUnitCode, StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        if (value.AsNumber() is not { } number)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        if (_explicit.TryGetValue($"{fromUnitCode}|{toUnitCode}", out var pinned))
        {
            return ExpressionValue.Number((number * pinned.Factor) + pinned.Offset);
        }

        if (!_units.TryGetValue(fromUnitCode, out var from) || !_units.TryGetValue(toUnitCode, out var to))
        {
            return ExpressionValue.Error(ExpressionErrors.BadUnit);
        }

        // ⛔ Різні розмірності — відмова. Саме тут щільність не стає конверсією:
        // коефіцієнт залежить від речовини й умов і живе в константах
        // методології (ФВ-16.3, ФВ-16.5).
        if (from.Dimension != to.Dimension || to.FactorToBase == 0m)
        {
            return ExpressionValue.Error(ExpressionErrors.BadUnit);
        }

        var inBase = (number * from.FactorToBase) + from.OffsetToBase;
        return ExpressionValue.Number((inBase - to.OffsetToBase) / to.FactorToBase);
    }

    /// <summary>Довідник із базовими одиницями `09-seed.sql`.</summary>
    /// <remarks>
    /// Використовується там, де прогін іде без бази — у симуляції й тестах.
    /// Коефіцієнти збігаються з seed навмисно: інакше «те саме» обчислення
    /// давало б різні числа залежно від того, звідки взяли довідник.
    /// </remarks>
    public static UnitTable Seed()
    {
        var table = new UnitTable();

        table.Add("kg", dimension: 1, factorToBase: 1m);
        table.Add("t", dimension: 1, factorToBase: 1000m);
        table.Add("g", dimension: 1, factorToBase: 0.001m);
        table.Add("mg", dimension: 1, factorToBase: 0.000001m);
        table.Add("m3", dimension: 2, factorToBase: 1m);
        table.Add("l", dimension: 2, factorToBase: 0.001m);
        table.Add("s", dimension: 4, factorToBase: 1m);
        table.Add("K", dimension: 5, factorToBase: 1m);
        table.Add("degC", dimension: 5, factorToBase: 1m, offsetToBase: 273.15m);

        return table;
    }
}
