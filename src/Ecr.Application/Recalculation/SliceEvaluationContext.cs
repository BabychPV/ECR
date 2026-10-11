using Ecr.Application.Ports;
using Ecr.Application.Sources;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Services;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;

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
public sealed class SliceEvaluationContext : IBudgetedEvaluationContext
{
    /// <inheritdoc />
    /// <remarks>
    /// ⛔ Без цієї властивості бюджет обчислювача був би сліпий саме там, де
    /// дорого: <see cref="EvaluatePredicate"/> нижче обчислює умову НАД КОЖНИМ
    /// рядком таблиці власним викликом обчислювача, і той заводив би собі
    /// свіжий бюджет на кожен рядок. Предикат, який нічого не відібрав, при
    /// цьому повертає порожню групу — нуль значень, — тобто зовнішній бюджет не
    /// побачив би ані сканування, ані його ціни.
    /// </remarks>
    public EvaluationBudget? Budget { get; set; }

    private readonly ReferenceResolver _resolver;
    private readonly RangeExpander _expander = new();
    private readonly Dictionary<int, TableDef> _tables;
    private readonly IReadOnlyDictionary<CellKey, ExpressionValue> _values;
    private readonly IReadOnlyDictionary<string, ExpressionValue> _headers;
    private readonly UnitCatalogSnapshot _units;
    private readonly IReadOnlyDictionary<int, IReadOnlyDictionary<string, long>> _rowsByTable;
    private readonly IReadOnlyDictionary<long, IReadOnlyDictionary<string, ExpressionValue>> _registryFields;
    private static readonly UnitConverter Converter = new();

    /// <summary>Порожній знімок довідника — виклик без жодного REGFIELD у цілях.</summary>
    private static readonly IReadOnlyDictionary<long, IReadOnlyDictionary<string, ExpressionValue>>
        EmptyRegistryFields = new Dictionary<long, IReadOnlyDictionary<string, ExpressionValue>>();

    /// <summary>
    /// Обчислювач умови предиката (<see cref="EvaluatePredicate"/>) — окремий
    /// від <c>IFormulaEngine</c>, яким рахує формулу викликач
    /// (<c>RecalculationService</c>): умова предиката НЕ формула, а фрагмент
    /// AST, уже розібраний разом із нею, і другий прохід через парсер сюди
    /// нічого не додав би.
    /// </summary>
    private static readonly Evaluator PredicateEvaluator = new(new FunctionRegistry());

    /// <summary>Створює контекст над завантаженими значеннями.</summary>
    /// <param name="snapshot">Структура версії шаблону.</param>
    /// <param name="values">Значення комірок: період, таблиця, рядок, колонка → значення.</param>
    /// <param name="headers">Поля шапки документа.</param>
    /// <param name="period">Календарний контекст періоду.</param>
    /// <param name="units">
    /// Знімок довідника одиниць — для <see cref="Convert"/>. Передається
    /// готовим, а не читається звідси: <see cref="IUnitCatalog.GetAsync"/>
    /// асинхронний, а цей контекст — синхронний діалект виразів.
    /// </param>
    /// <param name="rowsByTable">
    /// Таблиця → (ключ рядка → ідентифікатор рядка) у поточному екземплярі
    /// документа за цей період. Джерело рядків для предиката
    /// (<see cref="EvaluatePredicate"/>): список рядків таблиці НЕ виводиться
    /// зі <paramref name="values"/>, бо рядок без жодної заповненої комірки
    /// туди не потрапив би взагалі, і предикат мовчки не побачив би його.
    /// </param>
    /// <param name="registryFields">
    /// Знімок полів довідника для <c>REGFIELD</c>: id запису → (код поля →
    /// значення). Той самий принцип, що й у <paramref name="units"/> —
    /// контекст синхронний, а довідник читається з бази заздалегідь
    /// (<c>RecalculationService.RunAsync</c>), звужений до полів, які
    /// справді потрібні цілям цього прогону (Registry-залежності з
    /// <c>cfg.FormulaDependency</c>). <c>null</c> — прогін без жодної цілі,
    /// що читає REGFIELD; рівнозначно порожньому знімку.
    /// </param>
    /// <param name="formulaDependencies">
    /// Граф залежностей версії, збережений при публікації (<c>cfg.FormulaDependency</c>): за ним
    /// діапазони <c>[A:B]</c> розкриваються так, як при публікації, а не за поточним <c>Ordinal</c>
    /// (ФВ-0.2). <c>null</c> — чернетка чи граф не збережено: діапазон за поточним порядком.
    /// </param>
    public SliceEvaluationContext(
        TemplateVersionSnapshot snapshot,
        IReadOnlyDictionary<CellKey, ExpressionValue> values,
        IReadOnlyDictionary<string, ExpressionValue> headers,
        PeriodContext period,
        UnitCatalogSnapshot units,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, long>> rowsByTable,
        IReadOnlyDictionary<long, IReadOnlyDictionary<string, ExpressionValue>>? registryFields = null,
        IReadOnlyList<FormulaDependency>? formulaDependencies = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _formulaDependencies = formulaDependencies;

        _resolver = new ReferenceResolver(snapshot);
        _tables = snapshot.Sheets.SelectMany(s => s.Tables).ToDictionary(t => t.Id);
        _values = values ?? throw new ArgumentNullException(nameof(values));
        _headers = headers ?? throw new ArgumentNullException(nameof(headers));
        _units = units ?? throw new ArgumentNullException(nameof(units));
        _rowsByTable = rowsByTable ?? throw new ArgumentNullException(nameof(rowsByTable));
        _registryFields = registryFields ?? EmptyRegistryFields;
        Period = period;
    }

    /// <summary>Збережений при публікації граф залежностей версії (<c>cfg.FormulaDependency</c>).</summary>
    private readonly IReadOnlyList<FormulaDependency>? _formulaDependencies;

    private Dictionary<int, Dictionary<int, FormulaDependency>>? _edgesByFormula;
    private readonly Dictionary<int, IReadOnlyDictionary<int, IReadOnlyList<string>>> _publishedRanges = [];
    private FormulaDef? _formula;
    private AstNode? _formulaRoot;

    /// <summary>
    /// Повідомляє контексту, яку формулу обчислюють: діапазони її посилань розкриваються ключами,
    /// зафіксованими при публікації (ФВ-0.2), а не поточним <c>Ordinal</c>.
    /// </summary>
    /// <param name="formula">Формула; <c>null</c> — формула невідома, діапазон за поточним порядком.</param>
    /// <param name="root">Корінь розібраного виразу цієї формули.</param>
    public void UseFormula(FormulaDef? formula, AstNode? root)
    {
        _formula = formula;
        _formulaRoot = root;
    }

    /// <summary>
    /// Ключі діапазону за позицією посилання, як їх розкрила публікація; <c>null</c> — збереженого
    /// пробігу немає (чернетка, граф не збережено), діапазон береться за поточним порядком.
    /// </summary>
    private IReadOnlyList<string>? PublishedRange(CellReferenceNode reference)
    {
        if (_formula is null || _formulaRoot is null || _formulaDependencies is null)
        {
            return null;
        }

        if (!_publishedRanges.TryGetValue(_formula.Id, out var byPosition))
        {
            byPosition = BuildPublishedRanges(_formula, _formulaRoot);
            _publishedRanges[_formula.Id] = byPosition;
        }

        return byPosition.TryGetValue(reference.Position, out var keys) ? keys : null;
    }

    private Dictionary<int, IReadOnlyList<string>> BuildPublishedRanges(FormulaDef formula, AstNode root)
    {
        _edgesByFormula ??= _formulaDependencies!
            .Where(d => d.SourceKind == 0 && d.FormulaDefId is not null)
            .GroupBy(d => d.FormulaDefId!.Value)
            .ToDictionary(g => g.Key, g => g.ToDictionary(d => d.SortOrder));

        if (!_edgesByFormula.TryGetValue(formula.Id, out var edges)
            || !_tables.TryGetValue(formula.TableDefId, out var table))
        {
            return new Dictionary<int, IReadOnlyList<string>>();
        }

        // ⚠ Той самий обхід, що й при публікації (`PublishChecks.Dependencies`): ті самі таблиця,
        // рядок і колонка формули, тому `SortOrder` збігається з позицією у збереженому графі.
        var expander = new PublishedRangeExpander(edges);
        new DependencyExtractor(_resolver, expander).Extract(
            root, table.Id, FormulaOutputs.RowKeyOf(table, formula), _tables,
            diagnostics: null, formula.ColumnDefId);

        return expander.ByPosition;
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

        // ⛔ ФВ-0.2: діапазон розкрито при публікації в конкретні ключі (`cfg.FormulaDependency`), і
        // читати треба ТІ САМІ рядки. Розкриття за поточним `RowDef.Ordinal` давало інше число після
        // презентаційної зміни порядку (60 → 1039). Поточний порядок — лише запасний шлях, коли
        // збереженого пробігу немає (чернетка, граф не збережено).
        if (reference.Row is RowSelector.Range range
            && _tables.TryGetValue(resolved.TableDefId, out var table))
        {
            return
            [
                .. (PublishedRange(reference) ?? _expander.Expand(table, range.FromRowKey, range.ToRowKey))
                    .Select(rowKey => Value(
                        resolved.TableDefId, rowKey, resolved.ColumnDefId, reference.PeriodOffset)),
            ];
        }

        // ⚠ Предикат обчислюється ТУТ, а не в GetCellsByPredicate нижче: умова
        // вже розібрана в AST разом з рештою формули (`RowSelector.Predicate.
        // Condition`), і другий прохід через `FilterJson` (текстовий запис для
        // `cfg.FormulaDependency`, не виконуваний вираз) означав би розібрати
        // те саме двічі на кожен рядок кожної правки комірки.
        if (reference.Row is RowSelector.Predicate predicate)
        {
            return EvaluatePredicate(
                resolved.TableDefId, predicate.Condition, resolved.ColumnDefId, reference.PeriodOffset);
        }

        return [Value(resolved.TableDefId, resolved.RowKey, resolved.ColumnDefId, reference.PeriodOffset)];
    }

    /// <inheritdoc />
    public ExpressionValue GetCell(int tableDefId, string rowKey, int columnDefId, int periodOffset)
        => Value(tableDefId, rowKey, columnDefId, periodOffset);

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ Ніколи не викликається. Обчислювач (<c>Evaluator.EvaluateGroup</c>)
    /// диспетчеризує КОЖЕН <c>CellReferenceNode</c>, зокрема й предикатний, у
    /// <see cref="Read"/> — так само в усіх реалізаціях <c>IEvaluationContext</c>
    /// цього дерева (див. <c>TestEvaluationContext</c>). Метод лишається в
    /// інтерфейсі як контракт на майбутнє (директива №11, T12); прибрати його
    /// звідси — заміна одного дизайнерського рішення на інше, а не частина
    /// цієї правки.
    /// </remarks>
    public IReadOnlyList<ExpressionValue> GetCellsByPredicate(
        int tableDefId, string filterJson, int columnDefId)
        => [ExpressionValue.Error(ExpressionErrors.BadReference)];

    /// <summary>Рядки предикатного посилання: обчислює умову для кожного рядка таблиці.</summary>
    /// <remarks>
    /// ⚠ Директива №11 T12 (`#26`). До цієї правки предикатні формули
    /// виключалися з каскаду ЯВНО (`RecalculationService`) саме тому, що
    /// обчислити їх не було чим: єдина тодішня реалізація повертала `#REF`
    /// безумовно. Порожня множина тут — не помилка (02b §6.1): документ, у
    /// якому жоден рядок динамічної таблиці не задовольняє умову, — нормальний
    /// документ, а не збій.
    ///
    /// ⚠ `CurrentTableDefId`/`CurrentRowKey` контексту тимчасово підміняються
    /// на кожен перевірюваний рядок і повертаються в `finally`: умова читає
    /// колонки ЦЬОГО рядка через звичайний `[Current]`-шлях (`RowSelector.
    /// Current` у <see cref="Read"/>), а не через окремий механізм, — саме
    /// так само, як формула, що дійшла до цього рядка в обчисленні.
    /// </remarks>
    private List<ExpressionValue> EvaluatePredicate(
        int tableDefId, AstNode condition, int columnDefId, int periodOffset)
    {
        if (!_rowsByTable.TryGetValue(tableDefId, out var rows))
        {
            return [];
        }

        var matched = new List<ExpressionValue>();
        var savedTable = CurrentTableDefId;
        var savedRow = CurrentRowKey;
        try
        {
            CurrentTableDefId = tableDefId;

            // ⚠ Порядок обходу — детермінований (Ordinal), а не порядок
            // словника: два прогони на тих самих даних мають дати той самий
            // список, інакше звірка з еталоном (ФВ-9.5) стає неможливою.
            foreach (var rowKey in rows.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                CurrentRowKey = rowKey;
                var verdict = PredicateEvaluator.Evaluate(
                    condition, this, Domain.Enums.ExpressionDialect.Template);
                if (verdict.Type == ExpressionValueType.Boolean && (bool)verdict.Value!)
                {
                    matched.Add(Value(tableDefId, rowKey, columnDefId, periodOffset));
                }
            }
        }
        finally
        {
            CurrentTableDefId = savedTable;
            CurrentRowKey = savedRow;
        }

        return matched;
    }

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
    /// <remarks>
    /// ⛔ Реальні дані, не заглушка: значення бере знімок
    /// <see cref="_registryFields"/>, який <c>RecalculationService.RunAsync</c>
    /// наповнює з <c>IRegistryStore</c> ДО обчислення — контекст сам у базу не
    /// ходить (синхронний діалект виразів, той самий принцип, що в
    /// <see cref="Convert"/> і одиницях).
    ///
    /// ⚠ Відсутній запис ЧИ відсутнє поле — одна й та сама відповідь, <c>#REF</c>
    /// (02b §6.4): автору формули байдуже, яка з двох причин, — обидві
    /// означають «звідси значення взяти нема звідки», і дія одна — полагодити
    /// довідник або посилання. ПОРОЖНЄ поле існуючого запису (RT-24, R-11) —
    /// <c>null</c>: завантажувач кладе в знімок <c>ExpressionValue.Null</c>.
    /// </remarks>
    public ExpressionValue GetRegistryField(long registryEntryId, string fieldCode)
        => _registryFields.TryGetValue(registryEntryId, out var byField)
           && byField.TryGetValue(fieldCode, out var value)
            ? value
            : ExpressionValue.Error(ExpressionErrors.BadReference);

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ До цієї правки метод відмовляв БЕЗУМОВНО — незалежно від того, чи
    /// існує сама конверсія в довіднику. Функція `CONVERT` каталогу
    /// існувала і в бою не працювала жодного разу (директива №09 §6.5, `S-22`).
    ///
    /// ⚠ Однакові коди — тотожність без звернення до довідника: формула,
    /// написана `CONVERT(x, "t", "t")`, не має ставати помилкою через
    /// відсутність одиниці в знімку.
    ///
    /// ⚠ Explicit-конверсія (`uom.Conversion`, `LegacyPinned`) тут НЕ
    /// застосовується: це діалект ШАБЛОНУ, а не методології, і мапінг
    /// «джерело → ціль» із власним коефіцієнтом належить методології
    /// (`ФВ-16.3`). Тут — лише маршрут через базову одиницю розмірності.
    /// </remarks>
    public ExpressionValue Convert(ExpressionValue value, string fromUnitCode, string toUnitCode)
    {
        if (string.Equals(fromUnitCode, toUnitCode, StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        if (!_units.Units.TryGetValue(fromUnitCode, out var from)
            || !_units.Units.TryGetValue(toUnitCode, out var to))
        {
            // ⚠ Одиниці немає в довіднику — це помилка ВВЕДЕННЯ (одрук у коді
            // формули), а не привід удавати конверсію.
            return ExpressionValue.Error(ExpressionErrors.BadUnit);
        }

        var numeric = value.Value switch
        {
            decimal d => d,
            double d => (decimal)d,
            int i => i,
            _ => (decimal?)null,
        };

        if (numeric is null)
        {
            return ExpressionValue.Error(ExpressionErrors.BadUnit);
        }

        try
        {
            // ⛔ Z2-05: та сама арифметика, що на межі інтеграції й у `POST /units/convert`
            // (`BoundaryUnitConversion.Convert` над доменним `UnitConverter`): швидкість у швидкість - через
            // чисельник і знаменник, а не через заокруглений `FactorToBase` (`1 Sm3/s` → `3599.99…712`).
            var converted = BoundaryUnitConversion.Convert(numeric.Value, from.Id, to.Id, _units, Converter);

            return ExpressionValue.Number(converted);
        }
        catch (Exception ex) when (ex is DomainException or Errors.EcrException)
        {
            // ⛔ Різні розмірності — те саме `#UNIT`, яке відмова видавала й
            // раніше. Різниця в тому, що ТЕПЕР до цього коду доходять лише
            // справжні розбіжності розмірностей, а не будь-який виклик CONVERT.
            return ExpressionValue.Error(ExpressionErrors.BadUnit);
        }
    }

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
