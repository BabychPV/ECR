namespace Ecr.Scripting;

/// <summary>Позиція фрагмента в тексті скрипта: зсув, довжина, рядок і колонка (з 1).</summary>
/// <param name="Start">Зсув від початку тексту.</param>
/// <param name="Length">Довжина в символах.</param>
/// <param name="Line">Номер рядка з 1.</param>
/// <param name="Column">Номер колонки з 1.</param>
public readonly record struct ScriptSpan(int Start, int Length, int Line, int Column);

/// <summary>
/// Діагностика компіляції скрипта. Помилки скрипта — значення, а не винятки.
/// </summary>
/// <param name="Code">Код без префікса (<see cref="ScriptDiagnosticCodes"/>).</param>
/// <param name="Line">Рядок з 1.</param>
/// <param name="Column">Колонка з 1.</param>
/// <param name="Length">Довжина підкресленого фрагмента.</param>
/// <param name="Message">Англійський текст (запасний; інтерфейс бере переклад за <see cref="MessageKey"/>).</param>
/// <param name="Params">Підстановки для перекладу.</param>
public sealed record ScriptDiagnostic(
    string Code,
    int Line,
    int Column,
    int Length,
    string Message,
    IReadOnlyDictionary<string, string> Params)
{
    /// <summary>Ключ каталогу перекладів: <c>script.&lt;code&gt;</c>.</summary>
    public string MessageKey => "script." + Code;

    /// <summary>Найбільша кількість діагностик однієї компіляції: далі лексер, парсер і binder мовчать (текст-«сміття» не мусить давати відповідь на мегабайти).</summary>
    public const int MaxPerCompilation = 200;

    /// <summary>Найбільша довжина підстановки діагностики, символів (довші ідентифікатори й рядки скорочуються з «…»).</summary>
    public const int MaxTextChars = 200;

    /// <summary>
    /// Робить рядок придатним для JSON і інтерфейсу: непарні сурогати замінюються на U+FFFD
    /// (серіалізатор JSON відмовляється кодувати їх і валить відповідь), довгий текст скорочується.
    /// </summary>
    /// <param name="value">Рядок.</param>
    /// <param name="max">Найбільша довжина.</param>
    public static string Sanitize(string value, int max)
    {
        var text = value;
        if (text.Length > max)
        {
            var cut = max;
            if (char.IsHighSurrogate(text[cut - 1]))
            {
                cut--;
            }

            text = string.Concat(text.AsSpan(0, cut), "…");
        }

        System.Text.StringBuilder? builder = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var valid = !char.IsSurrogate(c)
                        || (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                        || (char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(text[i - 1]));
            if (!valid)
            {
                builder ??= new System.Text.StringBuilder(text, 0, i, text.Length);
                builder.Append('�');
            }
            else
            {
                builder?.Append(c);
            }
        }

        return builder?.ToString() ?? text;
    }

    /// <summary>Створює діагностику з позиції й набору підстановок.</summary>
    /// <param name="code">Код.</param>
    /// <param name="span">Позиція.</param>
    /// <param name="message">Англійський текст.</param>
    /// <param name="pairs">Пари «ім'я, значення» підстановок.</param>
    public static ScriptDiagnostic At(string code, ScriptSpan span, string message, params (string Key, string Value)[] pairs)
        => new(code, span.Line, span.Column, span.Length, Sanitize(message, MaxTextChars * 3),
            pairs.ToDictionary(p => p.Key, p => Sanitize(p.Value, MaxTextChars), StringComparer.Ordinal));
}

/// <summary>Коди діагностик (METHODOLOGY-SCRIPT-PLAN §3.9, §5.2, §13.7).</summary>
public static class ScriptDiagnosticCodes
{
    public const string UnexpectedToken = "unexpectedToken";
    public const string Expected = "expected";
    public const string UnterminatedString = "unterminatedString";
    public const string UnterminatedComment = "unterminatedComment";
    public const string InvalidNumber = "invalidNumber";
    public const string CaretNotPower = "caretNotPower";
    public const string AssignInExpression = "assignInExpression";
    public const string DuplicateName = "duplicateName";
    public const string UnknownName = "unknownName";
    public const string ForwardReference = "forwardReference";
    public const string UnknownFunction = "unknownFunction";
    public const string ArgumentCount = "argumentCount";
    public const string FunctionNotAllowedInMode = "functionNotAllowedInMode";
    public const string RecursiveFunction = "recursiveFunction";
    public const string OutputOutsideSubstanceLoop = "outputOutsideSubstanceLoop";
    public const string OutputNeedsPer = "outputNeedsPer";
    public const string NestedSubstanceLoop = "nestedSubstanceLoop";
    public const string SkipOutsideSubstanceLoop = "skipOutsideSubstanceLoop";
    public const string RowsInLibrary = "rowsInLibrary";
    public const string OutputInLibrary = "outputInLibrary";
    public const string MissingRows = "missingRows";
    public const string DuplicateTable = "duplicateTable";
    public const string MapNotScalar = "mapNotScalar";
    public const string IndexOnNonMap = "indexOnNonMap";
    public const string UnknownMapKey = "unknownMapKey";
    public const string DuplicateMapKey = "duplicateMapKey";
    public const string VisibleNeedsUnit = "visibleNeedsUnit";
    public const string UnknownImport = "unknownImport";
    public const string UnknownImportMember = "unknownImportMember";

    /// <summary>Лише константа: цикл бібліотек виявляє викликач (він бачить граф версій).</summary>
    public const string ImportCycle = "importCycle";
    public const string TooLarge = "tooLarge";

    /// <summary><c>param</c> поза бібліотекою.</summary>
    public const string ParamOutsideLibrary = "paramOutsideLibrary";
    public const string TooDeep = "tooDeep";

    // §13.7
    public const string TableColumnOutsideAggregate = "tableColumnOutsideAggregate";
    public const string AggregateNeedsTable = "aggregateNeedsTable";
    public const string AggregateManyTables = "aggregateManyTables";
    public const string NestedAggregateSameTable = "nestedAggregateSameTable";
    public const string AggregateNotAllowedHere = "aggregateNotAllowedHere";
    public const string UnknownTableAlias = "unknownTableAlias";
    public const string ForeachNameNotIdentifier = "foreachNameNotIdentifier";
    public const string ForeachTooLarge = "foreachTooLarge";

    /// <summary>Лише константа: перевірку одиниць виконує окремий крок (T3.1).</summary>
    public const string UnitMismatch = "unitMismatch";

    // Перевірки оточення (потребують БД; створюються поза цим проєктом).
    public const string UnknownUnit = "unknownUnit";
    public const string UnknownSubstance = "unknownSubstance";
    public const string UnknownTable = "unknownTable";
    public const string AmbiguousTable = "ambiguousTable";
    public const string UnknownColumn = "unknownColumn";
    public const string UnknownRegistryEntry = "unknownRegistryEntry";
    public const string NoBoundOutput = "noBoundOutput";
    public const string LibraryNotScript = "libraryNotScript";
}
