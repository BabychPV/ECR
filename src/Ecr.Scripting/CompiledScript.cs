using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;

namespace Ecr.Scripting;

/// <summary>Вихід скрипта.</summary>
/// <param name="Code">Код виходу.</param>
/// <param name="UnitCode">Код одиниці.</param>
/// <param name="IsPerSubstance">Вихід по речовині (<c>per S</c>).</param>
/// <param name="Ordinal">Порядок у тексті з 1.</param>
public sealed record OutputInfo(string Code, string UnitCode, bool IsPerSubstance, int Ordinal);

/// <summary>Проміжна величина <c>visible var</c>.</summary>
/// <param name="Code">Ім'я.</param>
/// <param name="UnitCode">Код одиниці.</param>
public sealed record VisibleInfo(string Code, string UnitCode);

/// <summary>Поле рядка, яке читає скрипт.</summary>
/// <param name="Name">Ім'я (як у першому зверненні).</param>
/// <param name="IsOptional">Поле необов'язкове, якщо жодне звернення не обов'язкове.</param>
/// <param name="IsMonthly">Є звернення <c>row.X[m]</c> (місячна колонка, §13.2).</param>
public sealed record RowFieldInfo(string Name, bool IsOptional, bool IsMonthly = false)
{
    /// <summary>Місце першого звернення.</summary>
    public ScriptSpan Span { get; init; }
}

/// <summary>Посилання <c>entry("РЕЄСТР","КОД")</c>.</summary>
/// <param name="RegistryCode">Код довідника.</param>
/// <param name="EntryCode">Код запису.</param>
public sealed record RegistryEntryRef(string RegistryCode, string EntryCode)
{
    /// <summary>Місце згадки.</summary>
    public ScriptSpan Span { get; init; }
}

/// <summary>Згадана одиниця виміру з позицією.</summary>
/// <param name="Code">Код одиниці.</param>
/// <param name="Line">Рядок з 1.</param>
/// <param name="Column">Колонка з 1.</param>
/// <param name="Length">Довжина.</param>
public sealed record UnitUse(string Code, int Line, int Column, int Length);

/// <summary>Вид члена верхнього рівня.</summary>
public enum MemberKind : byte
{
    /// <summary><c>const</c>.</summary>
    Const,

    /// <summary><c>function</c>.</summary>
    Function,

    /// <summary><c>var</c>.</summary>
    Var,
}

/// <summary>Член верхнього рівня (для імпортерів і підказок).</summary>
/// <param name="Name">Ім'я.</param>
/// <param name="Kind">Вид.</param>
/// <param name="Line">Рядок оголошення.</param>
/// <param name="UnitCode">Одиниця, якщо оголошена.</param>
/// <param name="ParameterCount">Кількість параметрів функції.</param>
/// <param name="IsMap">Константа — мапа.</param>
public sealed record MemberInfo(string Name, MemberKind Kind, int Line, string? UnitCode = null, int ParameterCount = 0, bool IsMap = false);

/// <summary>Вид агрегату (§13.1).</summary>
public enum AggregateKind : byte
{
    /// <summary>Сума.</summary>
    Sum,

    /// <summary>Мінімум.</summary>
    Min,

    /// <summary>Максимум.</summary>
    Max,

    /// <summary>Середнє.</summary>
    Avg,

    /// <summary>Перше значення.</summary>
    First,

    /// <summary>Кількість рядків.</summary>
    Count,

    /// <summary>Чи є хоч один рядок.</summary>
    Any,
}

/// <summary>Опис агрегату; у виразі лишається <c>$agg(індекс)</c>.</summary>
/// <param name="Kind">Вид.</param>
/// <param name="TableAlias">Псевдонім таблиці.</param>
/// <param name="Value">Вираз-значення (для <c>Count/Any</c> — <c>null</c>).</param>
/// <param name="Where">Умова; <c>null</c> — без умови.</param>
public sealed record AggregateInfo(AggregateKind Kind, string TableAlias, AstNode? Value, AstNode? Where);

/// <summary>
/// Константа: скаляр або мапа (довільної вкладеності), з необов'язковим кодом одиниці.
/// </summary>
public sealed class ScriptConst
{
    private ScriptConst(ExpressionValue? scalar, IReadOnlyDictionary<string, ScriptConst>? map, string? unitCode)
    {
        Scalar = scalar;
        Map = map;
        UnitCode = unitCode;
    }

    /// <summary>Скалярне значення; <c>null</c>, якщо це мапа.</summary>
    public ExpressionValue? Scalar { get; }

    /// <summary>Мапа «ключ → значення» у порядку оголошення; <c>null</c>, якщо це скаляр.</summary>
    public IReadOnlyDictionary<string, ScriptConst>? Map { get; }

    /// <summary>Код одиниці.</summary>
    public string? UnitCode { get; }

    /// <summary>Чи це мапа.</summary>
    public bool IsMap => Map is not null;

    /// <summary>Створює скаляр.</summary>
    /// <param name="value">Значення.</param>
    /// <param name="unitCode">Одиниця.</param>
    public static ScriptConst FromScalar(ExpressionValue value, string? unitCode = null) => new(value, null, unitCode);

    /// <summary>Створює мапу.</summary>
    /// <param name="map">Елементи.</param>
    /// <param name="unitCode">Одиниця.</param>
    public static ScriptConst FromMap(IReadOnlyDictionary<string, ScriptConst> map, string? unitCode = null)
        => new(null, map, unitCode);
}

/// <summary>Власна функція скрипта.</summary>
/// <param name="Name">Ім'я.</param>
/// <param name="Parameters">Параметри.</param>
/// <param name="Body">Тіло, опущене в AST.</param>
public sealed record ScriptFunction(string Name, IReadOnlyList<string> Parameters, AstNode Body);

/// <summary>Блок операторів.</summary>
/// <param name="Statements">Оператори.</param>
public sealed record ScriptBlock(IReadOnlyList<ScriptStatement> Statements);

/// <summary>Виконуваний оператор.</summary>
public abstract record ScriptStatement;

/// <summary><c>var</c>/<c>visible var</c>.</summary>
public sealed record VarStatement(string Name, string? UnitCode, bool IsVisible, AstNode Expr, string SourceText, int Line) : ScriptStatement;

/// <summary><c>output</c>.</summary>
public sealed record OutputStatement(string Code, string UnitCode, string? SubstanceVar, AstNode Expr, string SourceText, int Line) : ScriptStatement;

/// <summary><c>reject when</c>.</summary>
public sealed record RejectStatement(AstNode Cond, string SourceText) : ScriptStatement;

/// <summary><c>skip when</c>.</summary>
public sealed record SkipStatement(AstNode Cond) : ScriptStatement;

/// <summary><c>if/else</c>.</summary>
/// <param name="Cond">Умова.</param>
/// <param name="Then">Гілка «так».</param>
/// <param name="Else">Гілка «ні»; <c>null</c> — немає.</param>
/// <param name="OutputCodes">Коди виходів, оголошених у будь-якій гілці (включно з вкладеними блоками).</param>
public sealed record IfStatement(AstNode Cond, ScriptBlock Then, ScriptBlock? Else, IReadOnlyList<string> OutputCodes) : ScriptStatement;

/// <summary><c>foreach substance</c>.</summary>
public sealed record ForeachSubstanceStatement(string VarName, IReadOnlyList<string> Codes, ScriptBlock Body) : ScriptStatement;

/// <summary>
/// Результат компіляції скрипта: метадані й виконувана форма (§4.3, §13).
/// </summary>
public sealed class CompiledScript
{
    /// <summary>Скрипт — бібліотека.</summary>
    public bool IsLibrary { get; internal init; }

    /// <summary>Коди бібліотек.</summary>
    public IReadOnlyList<string> Imports { get; internal init; } = [];

    /// <summary>Імена <c>param</c> бібліотеки (змінні імпортера з тими самими іменами).</summary>
    public IReadOnlyList<string> Params { get; internal init; } = [];

    /// <summary>Речення <c>rows</c>.</summary>
    public IReadOnlyList<RowsClause> Rows { get; internal init; } = [];

    /// <summary>Виходи.</summary>
    public IReadOnlyList<OutputInfo> Outputs { get; internal init; } = [];

    /// <summary>Проміжні величини <c>visible var</c>.</summary>
    public IReadOnlyList<VisibleInfo> Visibles { get; internal init; } = [];

    /// <summary>Коди речовин усіх <c>foreach substance</c>, без дублів.</summary>
    public IReadOnlyList<string> Substances { get; internal init; } = [];

    /// <summary>Поля рядка, які читає скрипт.</summary>
    public IReadOnlyList<RowFieldInfo> RowFields { get; internal init; } = [];

    /// <summary>Колонки з <c>where</c>/<c>active when</c>.</summary>
    public IReadOnlyList<string> MatchColumns { get; internal init; } = [];

    /// <summary>Записи довідників з <c>entry(...)</c>.</summary>
    public IReadOnlyList<RegistryEntryRef> RegistryEntries { get; internal init; } = [];

    /// <summary>Довідники з <c>REG*</c> і <c>entry()</c>.</summary>
    public IReadOnlyList<string> RegistryCodes { get; internal init; } = [];

    /// <summary>Потрібен знімок довідників.</summary>
    public bool NeedsRegistries { get; internal init; }

    /// <summary>
    /// ✎ DESIGN-sg-flaring §5.2a: вираз <c>registry as of &lt;вираз&gt;;</c>, що обчислюється на КОЖЕН рядок; усі читання
    /// довідників рядка бачать знімок на цю дату. <c>null</c> — речення немає (знімок на кінець періоду, як досі).
    /// </summary>
    /// <remarks>⚠ <see cref="RegistryCodes"/> від речення не змінюється: воно не додає й не прибирає довідників.</remarks>
    public AstNode? RegistryAsOf { get; internal init; }

    /// <summary>Текст виразу <see cref="RegistryAsOf"/> — для причини відхилення рядка й трейсу.</summary>
    public string? RegistryAsOfText { get; internal init; }

    /// <summary>Усі згадані одиниці.</summary>
    public IReadOnlyList<UnitUse> Units { get; internal init; } = [];

    /// <summary>Члени верхнього рівня.</summary>
    public IReadOnlyList<MemberInfo> Members { get; internal init; } = [];

    /// <summary>Оператори верхнього рівня.</summary>
    public IReadOnlyList<ScriptStatement> Statements { get; internal init; } = [];

    /// <summary>
    /// Для кожного <c>var</c>/<c>output</c> верхнього рівня — імена <c>var</c>/<c>output</c> верхнього рівня, які його
    /// вираз читає безпосередньо (через власні функції теж); порожньо, якщо нічого. Ключі — у порядку оголошення.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> StepDependencies { get; internal init; }
        = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Константи.</summary>
    public IReadOnlyDictionary<string, ScriptConst> Constants { get; internal init; } = new Dictionary<string, ScriptConst>();

    /// <summary>Власні функції.</summary>
    public IReadOnlyDictionary<string, ScriptFunction> Functions { get; internal init; } = new Dictionary<string, ScriptFunction>();

    /// <summary>Псевдоніми таблиць з <c>table</c> (§13.1).</summary>
    public IReadOnlyDictionary<string, TableRef> Tables { get; internal init; } = new Dictionary<string, TableRef>();

    /// <summary>Коди таблиць усіх <c>table</c> (для перерахунку й застарілості).</summary>
    public IReadOnlyList<string> ReadTables { get; internal init; } = [];

    /// <summary>Псевдонім → колонки, які читаються з таблиці.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> TableColumns { get; internal init; }
        = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Описи агрегатів; <c>$agg(i)</c> посилається на <c>Aggregates[i]</c>.</summary>
    public IReadOnlyList<AggregateInfo> Aggregates { get; internal init; } = [];
}
