using Ecr.Domain.Entities.Configuration;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Evaluation;

namespace Ecr.Application.Recalculation;

/// <summary>
/// Контекст обчислення формули шаблону над зрізом документа.
/// </summary>
/// <remarks>
/// ⛔ Формули шаблону не обчислювалися ніде: <c>RecalculateAsync</c> був
/// заглушкою, а контексту, здатного прочитати комірку з документа, не
/// існувало взагалі (<c>A7-63</c>).
///
/// ⚠ Посилання резолвиться ТИМ САМИМ резолвером і розкривається ТИМ САМИМ
/// розкривачем діапазонів, що й при публікації. Друга реалізація читання
/// посилань розійшлася б із тією, за якою будувався граф залежностей, — і
/// формула читала б не ті комірки, від яких її оголосили залежною.
///
/// ⛔ Аргументи, константи й конверсія одиниць тут недоступні — і не «поки
/// що»: це діалект ШАБЛОНУ (<c>ФВ-9.1</c>), а не методології. Формула
/// шаблону, яка дістає константу методології, перестає бути формулою шаблону.
/// </remarks>
public sealed class SliceEvaluationContext : IEvaluationContext
{
    private readonly ReferenceResolver _resolver;
    private readonly RangeExpander _expander = new();
    private readonly Dictionary<int, TableDef> _tables;
    private readonly IReadOnlyDictionary<CellKey, ExpressionValue> _values;
    private readonly IReadOnlyDictionary<string, ExpressionValue> _headers;

    /// <summary>Створює контекст над завантаженими значеннями.</summary>
    /// <param name="snapshot">Структура версії шаблону.</param>
    /// <param name="values">Значення комірок: період, таблиця, рядок, колонка → значення.</param>
    /// <param name="headers">Поля шапки документа.</param>
    /// <param name="period">Календарний контекст періоду.</param>
    public SliceEvaluationContext(
        TemplateVersionSnapshot snapshot,
        IReadOnlyDictionary<CellKey, ExpressionValue> values,
        IReadOnlyDictionary<string, ExpressionValue> headers,
        PeriodContext period)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _resolver = new ReferenceResolver(snapshot);
        _tables = snapshot.Sheets.SelectMany(s => s.Tables).ToDictionary(t => t.Id);
        _values = values ?? throw new ArgumentNullException(nameof(values));
        _headers = headers ?? throw new ArgumentNullException(nameof(headers));
        Period = period;
    }

    /// <inheritdoc />
    public PeriodContext Period { get; }

    /// <summary>Таблиця формули, яку обчислюємо зараз.</summary>
    public int CurrentTableDefId { get; set; }

    /// <summary>Рядок формули; <c>null</c> для формул рівня колонки.</summary>
    public string? CurrentRowKey { get; set; }

    /// <summary>Колонка, яку підставляє плейсхолдер <c>{Month}</c>.</summary>
    public int? CurrentColumnDefId { get; set; }

    /// <inheritdoc />
    public IReadOnlyList<ExpressionValue> Read(CellReferenceNode reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var resolved = _resolver.Resolve(
            reference, CurrentTableDefId, CurrentRowKey, diagnostics: null, CurrentColumnDefId);

        // ⛔ Нерезолвлене в рантаймі посилання — це `#REF`, а не порожнє
        // значення. Порожнє мовчки перетворило б суму на суму без доданка, і
        // число було б «майже правильним» (`02b` §6.1).
        if (resolved is null)
        {
            return [ExpressionValue.Error(ExpressionErrors.BadReference)];
        }

        // ⚠ Діапазон розкривається ТУТ так само, як при публікації: у графі
        // залежностей він уже розкритий у конкретні рядки, і читати треба ті
        // самі.
        if (reference.Row is RowSelector.Range range
            && _tables.TryGetValue(resolved.TableDefId, out var table))
        {
            return
            [
                .. _expander
                    .Expand(table, range.FromRowKey, range.ToRowKey)
                    .Select(rowKey => Value(
                        resolved.TableDefId, rowKey, resolved.ColumnDefId, reference.PeriodOffset)),
            ];
        }

        return [Value(resolved.TableDefId, resolved.RowKey, resolved.ColumnDefId, reference.PeriodOffset)];
    }

    /// <inheritdoc />
    public ExpressionValue GetCell(int tableDefId, string rowKey, int columnDefId, int periodOffset)
        => Value(tableDefId, rowKey, columnDefId, periodOffset);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Предикат динамічного діапазону обчислюється не тут: він потребує
    /// значень ІНШИХ колонок кожного рядка, а не лише тієї, яку читають.
    /// Формули з таким посиланням у каскад не потрапляють — див.
    /// <see cref="RecalculationService"/>, — тож мовчазної підміни порожнім
    /// набором тут не стається.
    /// </remarks>
    public IReadOnlyList<ExpressionValue> GetCellsByPredicate(
        int tableDefId, string filterJson, int columnDefId)
        => [ExpressionValue.Error(ExpressionErrors.BadReference)];

    /// <inheritdoc />
    public ExpressionValue GetArgument(string name) => ExpressionValue.Error(ExpressionErrors.BadReference);

    /// <inheritdoc />
    public ExpressionValue GetConstant(string name) => ExpressionValue.Error(ExpressionErrors.BadReference);

    /// <inheritdoc />
    public ExpressionValue GetFormulaResult(string name) => ExpressionValue.Error(ExpressionErrors.BadReference);

    /// <inheritdoc />
    public ExpressionValue GetHeader(string name)
        => _headers.TryGetValue(name, out var value) ? value : ExpressionValue.Null;

    /// <inheritdoc />
    public ExpressionValue Convert(ExpressionValue value, string fromUnitCode, string toUnitCode)
        => ExpressionValue.Error(ExpressionErrors.BadUnit);

    /// <summary>Значення комірки; відсутня комірка — це <c>null</c>, а не помилка (02b §6.3).</summary>
    private ExpressionValue Value(int tableDefId, string? rowKey, int columnDefId, int periodOffset)
    {
        if (rowKey is null)
        {
            return ExpressionValue.Null;
        }

        var key = new CellKey(periodOffset, tableDefId, rowKey, columnDefId);

        // ⚠ Комірки немає — `null`, а не помилка: незаповнена комірка це
        // нормальний стан (`ФВ-3.8`), і агрегат над нею має визначений
        // результат. Помилка тут перетворила б порожню таблицю на `#REF`.
        return _values.TryGetValue(key, out var value) ? value : ExpressionValue.Null;
    }
}

/// <summary>Адреса комірки в межах обчислення.</summary>
/// <param name="PeriodOffset">Зсув періоду: <c>0</c> — поточний.</param>
/// <param name="TableDefId">Таблиця.</param>
/// <param name="RowKey">Ключ рядка.</param>
/// <param name="ColumnDefId">Колонка.</param>
/// <remarks>
/// ⚠ Ключем є <c>RowKey</c>, а не <c>TableRowId</c>: вираз оперує ключами
/// шаблону, і переклад в ідентифікатори рядків документа робиться на межі
/// запису, а не всередині обчислення.
/// </remarks>
public readonly record struct CellKey(int PeriodOffset, int TableDefId, string RowKey, int ColumnDefId);
