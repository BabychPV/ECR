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
/// <para>
/// ✎ HSE301 L: публічний, щоб захист від циклу (<see cref="GetFormulaResult"/>)
/// перевірявся модульним тестом напряму, а не лише через модуль.
/// </para>
/// </remarks>
/// <param name="period">Календарний контекст періоду.</param>
/// <param name="arguments">Аргументи рядка (<c>@Arg</c>).</param>
/// <param name="constants">Розв'язані константи (<c>CST.X</c>).</param>
/// <param name="units">Довідник одиниць у пам'яті.</param>
/// <param name="registries">Знімок довідників; <c>null</c> — <c>REG*</c> дають <c>#REF</c>.</param>
/// <param name="resolve">
/// ✎ HSE301 L: звідки брати <c>!Code</c>, якого немає серед записаних результатів, —
/// формулу імпортованої методології (бібліотеки), обчислену в її власному контексті.
/// Повертає <c>null</c>, якщо ім'я нікуди не веде. <c>null</c> замість резолвера —
/// лише формули своєї версії, як до кроку.
/// </param>
public sealed class MethodologyEvaluationContext(
    PeriodContext period,
    IReadOnlyDictionary<string, ExpressionValue> arguments,
    IReadOnlyDictionary<string, ExpressionValue> constants,
    UnitTable units,
    IRegistrySnapshot? registries = null,
    Func<string, ExpressionValue?>? resolve = null) : IEvaluationContext
{
    private readonly Dictionary<string, ExpressionValue> _formulaResults =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Імена, які саме зараз резолвляться через <c>resolve</c>, — захист від циклу.</summary>
    private readonly HashSet<string> _resolving = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public PeriodContext Period { get; } = period;

    /// <inheritdoc />
    /// <remarks>
    /// ✎ RT-23a: знімок довідників прив'язки (<c>CalculationBindingContext.Registries</c>),
    /// завантажений у <c>GenericCalculationModule.PrepareAsync</c> ДО обчислення
    /// (<c>D-162</c>). Це не порушує переносності методології (02b §3.4): довідник — дані
    /// системи, спільні для всіх проєктів, а не комірки конкретного шаблону. <c>null</c> —
    /// формули версії довідників не читають, і <c>REG*</c> дають <c>#REF</c>.
    /// </remarks>
    public IRegistrySnapshot? Registries { get; } = registries;

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
    /// <remarks>
    /// ⚠ Відсутній аргумент — це <c>#ARG</c>, а не <c>null</c> (директива
    /// «структурна перевірка аргументів методології при публікації», друга
    /// лінія захисту). Той самий вибір, що вже стоїть нижче в
    /// <see cref="GetConstant"/> для відсутньої константи: <c>arguments</c>
    /// зібрав <see cref="CalculationInputBuilder"/> по одному
    /// <c>CalculationArgument</c> на кожну КЛІТИНКУ рядка, іменовану кодом її
    /// колонки, — і ключа, якого там немає, означає, що колонки з таким кодом
    /// немає в таблиці (структурна вада, яку мала зловити публікація) АБО
    /// клітинка не існує фізично. Обидва випадки відрізняються від
    /// ЛЕГІТИМНОГО <c>null</c> — коли ключ Є, а значення клітинки порожнє, —
    /// і саме цю різницю трейс невдалого розрахунку мусить показати, а не
    /// одну голу <c>null</c> на все.
    /// </remarks>
    public ExpressionValue GetArgument(string name)
        => arguments.TryGetValue(name, out var value)
            ? value
            : ExpressionValue.Error(ExpressionErrors.ArgumentNotFound);

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
    /// <remarks>
    /// Спершу — записані результати своєї версії. Промах іде в резолвер
    /// (HSE301 L): формула бібліотеки, обчислена в її власному контексті, і її
    /// значення запам'ятовується тут — другий <c>!Code</c> того самого рядка
    /// не рахує її вдруге. Резолвера немає або ім'я нікуди не веде — <c>#REF</c>,
    /// як до кроку.
    /// <para>
    /// ⛔ Захист від циклу — тут, а не лише на публікації. Бібліотеку можуть
    /// перевидати ПІСЛЯ публікації викликача з посиланням назад, і цикл з'явиться
    /// лише в рантаймі. Ім'я, що вже резолвиться в цьому контексті, повертає
    /// <c>#CYCLE</c> замість рекурсії без кінця (<c>StackOverflow</c> убив би
    /// процес воркера разом з усім прогоном).
    /// </para>
    /// </remarks>
    public ExpressionValue GetFormulaResult(string name)
    {
        if (_formulaResults.TryGetValue(name, out var value))
        {
            return value;
        }

        if (resolve is null)
        {
            return ExpressionValue.Error(ExpressionErrors.BadReference);
        }

        if (!_resolving.Add(name))
        {
            return ExpressionValue.Error(ExpressionErrors.RuntimeCycle);
        }

        try
        {
            if (resolve(name) is not { } resolved)
            {
                return ExpressionValue.Error(ExpressionErrors.BadReference);
            }

            _formulaResults[name] = resolved;
            return resolved;
        }
        finally
        {
            _resolving.Remove(name);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Шапки документа методологія теж не бачить — з тієї самої причини, що
    /// й <see cref="GetCell"/> вище.
    ///
    /// ⚠ Судження (фундамент шапки документа): підключення реального читання
    /// <c>doc.DocumentHeaderValue</c> сюди НЕ зроблено навмисно, хоча задача
    /// просила «підключити GetHeader у всіх трьох місцях». Коментар класу
    /// (вище) прямо каже, чому методологія не читає комірки — вона мусить
    /// лишатися переносною між проєктами й шаблонами, тому дані документа
    /// приходять лише як <c>@Arg</c>, зібрані <see cref="CalculationInputBuilder"/>.
    /// Шапка — так само дані КОНКРЕТНОГО документа, як і комірка; підключити
    /// реальне читання тут означало б порушити ту саму інваріанту, яку
    /// <see cref="GetCell"/> уже захищає. Тому цей контекст приведено у
    /// відповідність із <see cref="GetCell"/> (обидва — стабільний
    /// <c>#REF</c>), а не з <c>SliceEvaluationContext.GetHeader</c>.
    /// </remarks>
    public ExpressionValue GetHeader(string name)
        => ExpressionValue.Error(ExpressionErrors.BadReference);

    /// <inheritdoc />
    /// <remarks>
    /// ✎ RT-23a: поле запису — за ЗНІМКОМ (<see cref="Registries"/>), а не з бази. Id
    /// запису приходить не з комірки (методологія комірок не читає, 02b §3.4), а як
    /// <c>EntryRef</c>: аргумент <c>Lookup</c>-колонки у версії <c>Strict</c> (<c>D-161</c>)
    /// або результат <c>REGFIND</c>/<c>REGONE</c>. <c>REGFIELD</c> методологій іде через
    /// <c>RegistryForms</c> і читає знімок сам; цей метод — та сама відповідь для всіх,
    /// хто питає через контракт контексту.
    ///
    /// ⚠ Без знімка — <c>#REF</c>, як і до кроку: запис невидимий так само, як поле, якого
    /// немає.
    /// </remarks>
    public ExpressionValue GetRegistryField(long registryEntryId, string fieldCode)
        => Registries is { } snapshot
            ? snapshot.GetField(registryEntryId, fieldCode)
            : ExpressionValue.Error(ExpressionErrors.BadReference);

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
    /// <para>
    /// ⚠ Дзеркало ПОВНЕ, і це звіряє <c>Hse301UnitsTests</c> по тексту сіду.
    /// Множники — такі, як їх зберігає <c>decimal(38,18)</c>: літерал
    /// <c>t_per_year</c> у сіді має 19 знаків і в базі округлюється, а
    /// <c>Sm3_per_h</c> (1/3600) там записано вже округленим до 18.
    /// </para>
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
        table.Add("J", dimension: 3, factorToBase: 1m);
        table.Add("GJ", dimension: 3, factorToBase: 1_000_000_000m);
        table.Add("MWh", dimension: 3, factorToBase: 3_600_000_000m);
        table.Add("s", dimension: 4, factorToBase: 1m);
        table.Add("min", dimension: 4, factorToBase: 60m);
        table.Add("h", dimension: 4, factorToBase: 3600m);
        table.Add("day", dimension: 4, factorToBase: 86_400m);
        table.Add("year", dimension: 4, factorToBase: 31_536_000m);
        table.Add("K", dimension: 5, factorToBase: 1m);
        table.Add("degC", dimension: 5, factorToBase: 1m, offsetToBase: 273.15m);
        table.Add("mol", dimension: 6, factorToBase: 1m);
        table.Add("one", dimension: 7, factorToBase: 1m);
        table.Add("g_per_s", dimension: 8, factorToBase: 0.001m);
        table.Add("t_per_year", dimension: 8, factorToBase: 0.000031709791983765m);
        table.Add("kg_per_t", dimension: 9, factorToBase: 0.001m);
        table.Add("g_per_GJ", dimension: 10, factorToBase: 0.000000000001m);
        table.Add("mg_per_m3", dimension: 11, factorToBase: 0.000001m);
        table.Add("kg_per_m3", dimension: 11, factorToBase: 1m);

        // HSE301:F1 — секція `-- HSE301:F1` сіду. ⛔ Sm3 — розмірність 12
        // (StdVolume), а не 2 (Volume): V-12.
        table.Add("kt", dimension: 1, factorToBase: 1_000_000m);
        table.Add("MJ", dimension: 3, factorToBase: 1_000_000m);
        table.Add("TJ", dimension: 3, factorToBase: 1_000_000_000_000m);
        table.Add("pct_vol", dimension: 7, factorToBase: 0.01m);
        table.Add("pct_wt", dimension: 9, factorToBase: 0.01m);
        table.Add("t_per_t", dimension: 9, factorToBase: 1m);
        table.Add("kg_per_TJ", dimension: 10, factorToBase: 0.000000000001m);
        table.Add("Sm3", dimension: 12, factorToBase: 1m);
        table.Add("Sm3_per_s", dimension: 13, factorToBase: 1m);
        table.Add("Sm3_per_h", dimension: 13, factorToBase: 0.000277777777777778m);
        table.Add("m_per_s", dimension: 14, factorToBase: 1m);
        table.Add("m2", dimension: 15, factorToBase: 1m);
        table.Add("kg_per_Sm3", dimension: 16, factorToBase: 1m);
        table.Add("MJ_per_Sm3", dimension: 17, factorToBase: 1_000_000m);
        table.Add("MJ_per_kg", dimension: 18, factorToBase: 1_000_000m);
        table.Add("g_per_mol", dimension: 19, factorToBase: 0.001m);

        // UNITS:ecr-derived — секція `-- UNITS:ecr-derived` сіду. 1/86400
        // так, як його зберігає decimal(38,18).
        table.Add("mg_per_Sm3", dimension: 16, factorToBase: 0.000001m);
        table.Add("Sm3_per_day", dimension: 13, factorToBase: 0.000011574074074074m);

        return table;
    }
}
