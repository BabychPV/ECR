using Ecr.Domain.Enums;

namespace Ecr.Application.Calculations.Scripting.Migration;

/// <summary>Формула версії (старий вираз у діалекті методологій).</summary>
/// <param name="Code">Код формули.</param>
/// <param name="Expression">Текст виразу.</param>
/// <param name="EvaluationOrder">Порядок обчислення (зростання).</param>
/// <param name="Scope">Область: рядок або речовина.</param>
/// <param name="IsVisible">Проміжний результат видимий.</param>
/// <param name="ResultType">Тип результату.</param>
/// <param name="UnitCode">Код одиниці результату; <c>null</c> — немає.</param>
/// <param name="ArgumentsCsv">
/// Збережений список аргументів (<c>calc.MethodologyFormula.ArgumentsCsv</c>); <c>null</c> — немає. Рантайм старого рушія
/// бере з нього ЄДИНЕ: маркери необов'язковості <c>Name?</c> (див. <c>OptionalArguments</c>), тому поле входить у відбиток.
/// </param>
public sealed record SourceFormula(
    string Code,
    string Expression,
    int EvaluationOrder,
    MethodologyFormulaScope Scope = MethodologyFormulaScope.Substance,
    bool IsVisible = false,
    FormulaResultType ResultType = FormulaResultType.Number,
    string? UnitCode = null,
    string? ArgumentsCsv = null);

/// <summary>Вихід версії.</summary>
/// <param name="Code">Код (збігається з кодом формули).</param>
/// <param name="UnitCode">Код одиниці.</param>
/// <param name="IsPerSubstance">Вихід по речовинах.</param>
/// <param name="Ordinal">Порядок.</param>
public sealed record SourceOutput(string Code, string UnitCode, bool IsPerSubstance = true, int Ordinal = 0);

/// <summary>Константа версії: одне значення одного вікна чинності.</summary>
/// <param name="Code">Код (те, що після <c>CST.</c>).</param>
/// <param name="Kind">Вид: число, текст чи мітка категорії.</param>
/// <param name="Number">Число (для виду <see cref="ConstantKind.Numeric"/>); <c>null</c> — нерозібране.</param>
/// <param name="Text">Текст (текстові й нерозібрані числа).</param>
/// <param name="UnitCode">Код одиниці; <c>null</c> — немає.</param>
/// <param name="Category">Категорія; <c>null</c> — спільна.</param>
/// <param name="SubstanceCode">Код речовини; <c>null</c> — спільна.</param>
/// <param name="ValidFrom">Перший чинний день; <c>null</c> — від початку.</param>
/// <param name="ValidTo">Перший НЕчинний день; <c>null</c> — без обмеження.</param>
public sealed record SourceConstant(
    string Code,
    ConstantKind Kind,
    decimal? Number,
    string? Text = null,
    string? UnitCode = null,
    string? Category = null,
    string? SubstanceCode = null,
    DateOnly? ValidFrom = null,
    DateOnly? ValidTo = null)
{
    /// <summary>Числова константа.</summary>
    public static SourceConstant OfNumber(
        string code, decimal value, string? category = null, string? substanceCode = null,
        DateOnly? validFrom = null, DateOnly? validTo = null, string? unitCode = null)
        => new(code, ConstantKind.Numeric, value, null, unitCode, category, substanceCode, validFrom, validTo);

    /// <summary>Текстова константа.</summary>
    public static SourceConstant OfText(
        string code, string text, string? category = null, string? substanceCode = null,
        DateOnly? validFrom = null, DateOnly? validTo = null)
        => new(code, ConstantKind.Text, null, text, null, category, substanceCode, validFrom, validTo);
}

/// <summary>Імпорт бібліотеки.</summary>
/// <param name="LibraryCode">Код методології-бібліотеки.</param>
/// <param name="Members">Імена членів, які бібліотека відкриває (формули/<c>var</c>).</param>
/// <param name="ConstantCategories">
/// Категорії констант версій бібліотек, ДОСЯЖНИХ з формул версії-викликача через цей імпорт (замикання старого рушія,
/// <see cref="ImportClosureSource"/>); порожньо — бібліотека недосяжна; <c>null</c> — не читалося.
/// </param>
/// <param name="OptionalArguments">
/// Імена, позначені <c>Name?</c> у <c>ArgumentsCsv</c> ПОТРІБНИХ формул бібліотек (досяжних з формул версії-викликача):
/// рантайм об'єднує такі маркери з маркерами викликача (<c>OptionalArguments.Of</c>); <c>null</c> — не читалося.
/// </param>
public sealed record SourceImport(
    string LibraryCode,
    IReadOnlyList<string> Members,
    IReadOnlyList<string>? ConstantCategories = null,
    IReadOnlyList<string>? OptionalArguments = null);

/// <summary>Значення в правилі відбору рядків: текст або запис довідника.</summary>
public sealed record SourceMatchValue
{
    private SourceMatchValue() { }

    /// <summary>Рядковий літерал; <c>null</c>, якщо це запис довідника.</summary>
    public string? Text { get; private init; }

    /// <summary>Код довідника.</summary>
    public string? RegistryCode { get; private init; }

    /// <summary>Код запису.</summary>
    public string? EntryCode { get; private init; }

    /// <summary>Чи це запис довідника.</summary>
    public bool IsEntry => RegistryCode is not null;

    /// <summary>Текстове значення.</summary>
    public static SourceMatchValue OfText(string text) => new() { Text = text };

    /// <summary>Запис довідника.</summary>
    public static SourceMatchValue OfEntry(string registryCode, string entryCode)
        => new() { RegistryCode = registryCode, EntryCode = entryCode };
}

/// <summary>Одна рівність правила: колонка = значення.</summary>
public sealed record SourceMatchPair(string ColumnCode, SourceMatchValue Value);

/// <summary>Правило відбору рядків: усі рівності (<c>&amp;&amp;</c>). Порожнє — «усі рядки».</summary>
public sealed record SourceRule(IReadOnlyList<SourceMatchPair> Pairs);

/// <summary>Статус-колонка активності рядка: активний, коли її значення = запис довідника.</summary>
public sealed record SourceActiveColumn(string ColumnCode, string RegistryCode, string EntryCode);

/// <summary>Речення <c>rows from</c>: таблиці, правила (за пріоритетом, активні), статус-колонки.</summary>
/// <param name="TableCodes">Коди таблиць (<c>Таблиця</c> або <c>Шаблон.Таблиця</c>).</param>
/// <param name="Rules">Правила за пріоритетом; між ними — «або».</param>
/// <param name="ActiveColumns">Статус-колонки; порожньо — активність не фільтрується.</param>
public sealed record SourceRowsClause(
    IReadOnlyList<string> TableCodes,
    IReadOnlyList<SourceRule> Rules,
    IReadOnlyList<SourceActiveColumn> ActiveColumns);

/// <summary>Усе, що потрібно перетворювачеві про одну версію методології (без сутностей EF).</summary>
public sealed record VersionSource
{
    /// <summary>Код методології.</summary>
    public required string MethodologyCode { get; init; }

    /// <summary>Мітка версії для заголовка (наприклад «V1»).</summary>
    public string VersionLabel { get; init; } = "V1";

    /// <summary>Методологія виду <c>Library</c>.</summary>
    public bool IsLibrary { get; init; }

    /// <summary>
    /// Версія рівня <c>Module</c> (HSE400, «місяці в колонках»): усі формули стають <c>output</c>, аргументи
    /// <c>@GCV.x_y_z</c> — <c>row.GCV_x_y_z</c>, речення <c>rows</c> немає.
    /// </summary>
    public bool IsModuleLevel { get; init; }

    /// <summary>Числовий режим.</summary>
    public NumericMode NumericMode { get; init; } = NumericMode.Legacy;

    /// <summary>Перший день життя версії.</summary>
    public DateOnly EffectiveFrom { get; init; } = new(2024, 1, 1);

    /// <summary>Перший день наступної версії (виключна межа життя); <c>null</c> — чинна й досі.</summary>
    public DateOnly? NextEffectiveFrom { get; init; }

    /// <summary>Формули.</summary>
    public IReadOnlyList<SourceFormula> Formulas { get; init; } = [];

    /// <summary>Виходи.</summary>
    public IReadOnlyList<SourceOutput> Outputs { get; init; } = [];

    /// <summary>Константи (усі вікна чинності).</summary>
    public IReadOnlyList<SourceConstant> Constants { get; init; } = [];

    /// <summary>Категорії версії (<c>calc.MethodologyCategory</c>).</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>Правило категорії (вираз, де <c>@X</c> — аргументи рядка); <c>null</c> — немає.</summary>
    public string? CategoryRule { get; init; }

    /// <summary>Коди речовин у порядку.</summary>
    public IReadOnlyList<string> Substances { get; init; } = [];

    /// <summary>Імпорти бібліотек.</summary>
    public IReadOnlyList<SourceImport> Imports { get; init; } = [];

    /// <summary>Речення відбору рядків.</summary>
    public IReadOnlyList<SourceRowsClause> RowsClauses { get; init; } = [];

    /// <summary>Усі активні правила відбору рядків за пріоритетом (без розкладу по реченнях): для відбитка.</summary>
    public IReadOnlyList<SourceRule> Rules { get; init; } = [];

    /// <summary>Фільтр активності рядків методології (<c>РЕЄСТР:ЗАПИС</c>); <c>null</c> — немає. Для відбитка.</summary>
    public string? RowActivityFilter { get; init; }

    /// <summary>Календарний режим версії. Для відбитка.</summary>
    public CalendarMode CalendarMode { get; init; } = CalendarMode.Actual;
}
