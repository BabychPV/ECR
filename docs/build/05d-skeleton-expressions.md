# 05d — Скелет: `Ecr.Expressions`

> Частина [`05-skeleton.md`](05-skeleton.md).
> Граматика і семантика — [`02b-expressions.md`](02b-expressions.md).
>
> **Головне непорозуміння, якого треба уникнути:** NCalc — це **обчислювач
> арифметики**, а не парсер нашої мови (`D-20`). Посилання виду
> `[Water_07].[Main].[7001001].[Jan]`, діапазони, `@Arg`, `CST.`, `!Formula`
> і `[Period].Seconds` NCalc не розуміє й не має розуміти. Наш парсер розбирає
> вираз, резолвить посилання у значення, і лише потім арифметику можна віддати
> NCalc — або порахувати самому. Очікування «взяли NCalc — рушій готовий»
> коштує 2–3 тижні недооцінки.

---

### `src/Ecr.Expressions/Ast/AstNode.cs`
MODULE: expressions | STAGE: 2
CONTRACT: 02b-expressions.md#ast
SCOPE: модель синтаксичного дерева.

**`COPY FROM`** [`02b-expressions.md#ast`](02b-expressions.md#ast) — дослівно,
разом із `ParseResult.cs` (перенести `ParseResult`, `ParsedExpression`,
`ExpressionDiagnostic` у `src/Ecr.Expressions/Parsing/ParseResult.cs`).

---

### `src/Ecr.Expressions/Lexing/Token.cs`
MODULE: expressions | STAGE: 2

```csharp
namespace Ecr.Expressions.Lexing;

/// <summary>Тип лексеми.</summary>
public enum TokenType : byte
{
    Number, String, Boolean, Null,
    Identifier,
    LBracket, RBracket, LParen, RParen,
    Dot, Comma, Colon, Question,
    At, Bang, Ampersand,
    Plus, Minus, Star, Slash, Percent, Caret,
    Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual,
    And, Or, Not,
    EndOfInput
}

/// <summary>
/// Лексема з позицією. Позиція потрібна для діагностики: користувач має
/// побачити, **де саме** помилка, а не «вираз некоректний».
/// </summary>
/// <param name="Type">Тип.</param>
/// <param name="Text">Вихідний текст лексеми.</param>
/// <param name="Position">Зсув від початку виразу.</param>
/// <param name="Length">Довжина.</param>
public readonly record struct Token(TokenType Type, string Text, int Position, int Length);
```

---

### `src/Ecr.Expressions/Lexing/Lexer.cs`
MODULE: expressions | STAGE: 2
CONTRACT: 02b-expressions.md#grammar
SCOPE: розбиття тексту на лексеми.
NOT IN SCOPE: пріоритети операторів — це парсер.

```csharp
namespace Ecr.Expressions.Lexing;

/// <summary>Лексичний аналізатор виразів.</summary>
public sealed class Lexer
{
    /// <summary>Розбиває вираз на лексеми.</summary>
    /// <param name="expression">Текст виразу.</param>
    /// <returns>Послідовність лексем, остання — <see cref="TokenType.EndOfInput"/>.</returns>
    /// <exception cref="LexicalException">Недопустимий символ або незакритий рядок.</exception>
    public IReadOnlyList<Token> Tokenize(string expression)
        => throw new NotImplementedException(
            "TODO: розбити на лексеми за 02b §1. Особливості:\n" +
            "— ідентифікатори всередині [...] НЕ екрануються, тому лексер має розрізняти\n" +
            "  контекст: усередині дужок допустимі цифри на початку (RowKey '7001001'),\n" +
            "  зовні — ні (EcrCode);\n" +
            "— рядкові літерали в одинарних лапках, подвоєння '' для екранування;\n" +
            "— числа — інваріантний формат, роздільник тільки '.', без розділювачів тисяч;\n" +
            "— '=' і '==' — синоніми порівняння; присвоєння в мові немає;\n" +
            "— ключові слова AND/OR/NOT/TRUE/FALSE/NULL/WHERE — регістронезалежні;\n" +
            "— коментарів у мові немає (пояснення живуть в описі формули).");
}

/// <summary>Помилка лексичного аналізу.</summary>
public sealed class LexicalException(string message, int position) : Exception(message)
{
    /// <summary>Позиція проблемного символу.</summary>
    public int Position { get; } = position;
}
```

---

### `src/Ecr.Expressions/Parsing/Parser.cs`
MODULE: expressions | STAGE: 2
CONTRACT: 02b-expressions.md#grammar
SCOPE: рекурсивний спуск за EBNF; побудова AST.
NOT IN SCOPE: резолвінг посилань, перевірка типів — окремі етапи.

```csharp
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Lexing;

namespace Ecr.Expressions.Parsing;

/// <summary>
/// Парсер рекурсивного спуску. Один парсер на обидва діалекти: різниця між
/// <c>Template</c> і <c>Methodology</c> — лише в наборі дозволених посилань і
/// функцій, а не в синтаксисі (D-19).
/// </summary>
public sealed class Parser
{
    /// <summary>Розбирає вираз.</summary>
    /// <param name="expression">Текст.</param>
    /// <param name="dialect">Діалект — визначає, які посилання дозволені.</param>
    /// <returns>
    /// Результат із AST або з діагностиками. Помилка синтаксису — **результат**,
    /// а не виняток: конфігуратор має показати проблему, а не впасти.
    /// </returns>
    public ParseResult Parse(string expression, ExpressionDialect dialect)
        => throw new NotImplementedException(
            "TODO: рекурсивний спуск за EBNF (02b §1) із пріоритетами (02b §2):\n" +
            "ternary → or → and → not → comparison → concat → additive → multiplicative →\n" +
            "power (ПРАВОАСОЦІАТИВНИЙ) → unary → primary.\n" +
            "Посилання (02b §3): cell_ref у трьох скорочених формах, arg_ref '@', const_ref 'CST.',\n" +
            "formula_ref '!', header_ref 'HDR.', period_ref '[Period:±N]'.\n" +
            "Для dialect = Template заборонити @Arg/CST./!Formula → діагностика;\n" +
            "для Methodology заборонити cell_ref → діагностика (методологія працює з\n" +
            "підготовленими аргументами, а не лізе в документ сама).\n" +
            "Збирати ВСІ діагностики, не зупинятися на першій.");
}
```

---

### `src/Ecr.Expressions/Binding/ReferenceResolver.cs`
MODULE: expressions | STAGE: 2

```csharp
using Ecr.Domain.Entities.Configuration;
using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Binding;

/// <summary>
/// Резолвить посилання в конкретні <c>TableDefId</c>, <c>ColumnDefId</c>,
/// <c>RowKey</c>. Виконується **при публікації**: нерезолвлене посилання має
/// зупинити публікацію, а не зіпсувати число в проді.
/// </summary>
public sealed class ReferenceResolver(TemplateVersionSnapshot snapshot)
{
    /// <summary>Резолвить одне посилання.</summary>
    /// <param name="node">Вузол посилання.</param>
    /// <param name="currentTableDefId">Таблиця, в якій живе формула — для скорочених форм.</param>
    /// <param name="currentRowKey">Рядок формули; <c>null</c> для формул рівня колонки.</param>
    /// <returns>Резолвлене посилання або діагностика <c>ECR-TMPL-4222</c>.</returns>
    public ResolvedReference Resolve(CellReferenceNode node, int currentTableDefId, string? currentRowKey)
        => throw new NotImplementedException(
            "TODO: доповнити скорочені форми з контексту (02b §3.1); знайти SheetDef/TableDef/ColumnDef " +
            "за кодами в snapshot; для RowMode = Fixed перевірити наявність RowKey у cfg.RowDef; " +
            "для Dynamic заборонити конкретний RowKey (ECR-TMPL-4222) — посилатися можна лише предикатом; " +
            "'{Month}' підставити поточну місячну колонку; " +
            "[Period:±N] зберегти як PeriodOffset — вихід за межі проєкту в рантаймі дає null, НЕ помилку.");
}

/// <summary>Резолвлене посилання.</summary>
public sealed record ResolvedReference(
    int TableDefId, string? RowKey, int ColumnDefId, int PeriodOffset, string? FilterJson);
```

---

### `src/Ecr.Expressions/Binding/RangeExpander.cs`
MODULE: expressions | STAGE: 2

```csharp
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Expressions.Binding;

/// <summary>
/// Розкриває діапазони рядків у явний список <c>RowKey</c> **на момент
/// `Publish`**. У рантаймі діапазонів не існує (B03 §4).
/// </summary>
/// <remarks>
/// Саме це робить зміну <c>Ordinal</c> після публікації безпечною: формула вже
/// посилається на конкретні рядки. Якби діапазон обчислювався в рантаймі за
/// <c>Ordinal</c>, презентаційна правка мовчки змінювала б числа — найгірший
/// клас помилок, бо даних не зіпсовано, а результат інший.
/// </remarks>
public sealed class RangeExpander
{
    /// <summary>Розкриває діапазон у список ключів.</summary>
    /// <param name="table">Таблиця, в якій живуть рядки.</param>
    /// <param name="fromRowKey">Початок діапазону.</param>
    /// <param name="toRowKey">Кінець діапазону.</param>
    /// <returns>Ключі рядків у порядку <c>Ordinal</c> на момент виклику.</returns>
    public IReadOnlyList<string> Expand(TableDef table, string fromRowKey, string toRowKey)
        => throw new NotImplementedException(
            "TODO: знайти обидва рядки в table.Rows; впорядкувати рядки за Ordinal; " +
            "повернути всі RowKey між ними включно, ігноруючи IsDeleted. " +
            "Якщо межа не знайдена або from стоїть після to — діагностика ECR-TMPL-4222.");
}
```

---

### `src/Ecr.Expressions/Binding/DependencyExtractor.cs`
MODULE: expressions | STAGE: 2

```csharp
using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Binding;

/// <summary>
/// Витягує залежності виразу для збереження в <c>cfg.FormulaDependency</c>.
/// Зворотний індекс по цій таблиці — основа інкрементного перерахунку.
/// </summary>
public sealed class DependencyExtractor(ReferenceResolver resolver, RangeExpander expander)
{
    /// <summary>Обходить AST і збирає всі залежності.</summary>
    public IReadOnlyList<ExtractedDependency> Extract(AstNode root, int currentTableDefId, string? currentRowKey)
        => throw new NotImplementedException(
            "TODO: обійти дерево; для CellReferenceNode: Single → одна залежність, " +
            "Range → розкрити через expander і створити залежність на КОЖЕН рядок із SortOrder, " +
            "Predicate → одна залежність із FilterJson і RowKey = null; " +
            "для SymbolReferenceNode(Formula) → залежність між формулами (для топологічного порядку); " +
            "для PeriodPropertyNode → залежності немає, це календарний контекст.");
}

/// <summary>Витягнута залежність.</summary>
public sealed record ExtractedDependency(
    byte DependsOnKind, int? TableDefId, string? RowKey, int? ColumnDefId,
    string? FilterJson, short? PeriodOffset, int SortOrder);
```

---

### `src/Ecr.Expressions/Binding/TypeChecker.cs`
MODULE: expressions | STAGE: 2

```csharp
using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Binding;

/// <summary>
/// Перевіряє типи **при публікації**. Приведення не відбувається мовчки:
/// Excel вгадує тип і саме тому дає «майже правильні» числа; тут
/// неоднозначність — помилка, поки її ще дешево виправити (02b §5).
/// </summary>
public sealed class TypeChecker
{
    /// <summary>Виводить тип виразу і збирає діагностики.</summary>
    public ExpressionValueType Check(AstNode node, ITypeContext context, List<Parsing.ExpressionDiagnostic> diagnostics)
        => throw new NotImplementedException(
            "TODO: рекурсивно вивести тип за правилами 02b §5. Заборонити: Number + Text, " +
            "Boolean в арифметиці, порівняння різних типів, Lookup-комірку в арифметиці. " +
            "Дозволити: Text & будь-що (друге приводиться до тексту), Date − Date → Number, " +
            "Date + Number → Date. Кожне порушення — ECR-TMPL-4222 із позицією.");
}

/// <summary>Джерело типів для посилань.</summary>
public interface ITypeContext
{
    /// <summary>Тип значення колонки.</summary>
    public ExpressionValueType GetColumnType(int tableDefId, int columnDefId);

    /// <summary>Тип аргументу методології.</summary>
    public ExpressionValueType GetArgumentType(string name);
}
```

---

### `src/Ecr.Expressions/Binding/UnitChecker.cs`
MODULE: expressions | STAGE: 4

```csharp
using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Binding;

/// <summary>
/// Перевіряє сумісність одиниць. **Головна цінність механізму одиниць** —
/// саме ця перевірка: помилка ловиться до продуктиву, а не на звірці через
/// місяць (ФВ-16.7).
/// </summary>
public sealed class UnitChecker
{
    /// <summary>Виводить одиницю результату і перевіряє сумісність операндів.</summary>
    /// <returns>Ідентифікатор одиниці результату або <c>null</c>, якщо вираз безрозмірний.</returns>
    public int? Check(AstNode node, IUnitContext context, List<Parsing.ExpressionDiagnostic> diagnostics)
        => throw new NotImplementedException(
            "TODO: правила виведення:\n" +
            "— Add/Subtract: одиниці мають ЗБІГАТИСЯ; інакше ECR-TMPL-4223 (неявної конверсії немає, D-74);\n" +
            "— Multiply/Divide: одиниця результату — похідна; шукати відповідну в uom.Unit за\n" +
            "  NumeratorUnitId/DenominatorUnitId; не знайдено → ECR-TMPL-4223;\n" +
            "— CONVERT(x, from, to): результат — 'to'; перевірити, що from і to однієї розмірності;\n" +
            "— SUM/AVERAGE/MIN/MAX: усі елементи однієї одиниці;\n" +
            "— порівняння: одиниці мають збігатися;\n" +
            "— агрегація колонки з DataType = Unit без CONVERT → ECR-TMPL-4223 (ФВ-16.8).\n" +
            "Наприкінці звірити з оголошеною одиницею колонки або виходу методології.");
}

/// <summary>Джерело одиниць.</summary>
public interface IUnitContext
{
    /// <summary>Одиниця колонки; <c>null</c> — безрозмірна.</summary>
    public int? GetColumnUnit(int tableDefId, int columnDefId);

    /// <summary>Одиниця константи методології.</summary>
    public int? GetConstantUnit(string code);

    /// <summary>Розмірність одиниці.</summary>
    public byte GetDimension(int unitId);

    /// <summary>Шукає похідну одиницю за чисельником і знаменником.</summary>
    public int? FindDerived(int numeratorUnitId, int denominatorUnitId);
}
```

---

### `src/Ecr.Expressions/Graph/DependencyGraph.cs` і `TopologicalSorter.cs`
MODULE: expressions | STAGE: 2

```csharp
namespace Ecr.Expressions.Graph;

/// <summary>Граф залежностей формул.</summary>
public sealed class DependencyGraph
{
    private readonly Dictionary<int, HashSet<int>> _edges = [];

    /// <summary>Додає ребро «<paramref name="dependent"/> залежить від <paramref name="dependency"/>».</summary>
    public void AddEdge(int dependent, int dependency)
    {
        if (!_edges.TryGetValue(dependent, out var set))
        {
            set = [];
            _edges[dependent] = set;
        }
        set.Add(dependency);
    }

    /// <summary>Усі вузли графа.</summary>
    public IReadOnlyCollection<int> Nodes => _edges.Keys;

    /// <summary>Залежності вузла.</summary>
    public IReadOnlyCollection<int> DependenciesOf(int node)
        => _edges.TryGetValue(node, out var set) ? set : Array.Empty<int>();
}
```

```csharp
namespace Ecr.Expressions.Graph;

/// <summary>
/// Топологічне сортування. Цикл повертається як **помилка публікації**
/// (<c>ECR-TMPL-4221</c>), а не як тихо неправильне число в проді (ФВ-9.4).
/// </summary>
public sealed class TopologicalSorter
{
    /// <summary>Сортує вузли.</summary>
    /// <returns>Порядок обчислення або перелік вузлів, що утворюють цикл.</returns>
    public OrderingResult Sort(DependencyGraph graph)
        => throw new NotImplementedException(
            "TODO: алгоритм Кана. При виявленні циклу — ПОВЕРНУТИ шлях циклу, а не просто " +
            "прапорець: користувач має побачити, які саме формули замкнулися.");
}

/// <summary>Результат сортування.</summary>
/// <param name="IsSuccess">Чи вдалося впорядкувати.</param>
/// <param name="Order">Вузли в порядку обчислення.</param>
/// <param name="CyclePath">Шлях циклу, якщо він є.</param>
public sealed record OrderingResult(bool IsSuccess, IReadOnlyList<int> Order, IReadOnlyList<int>? CyclePath);
```

---

### `src/Ecr.Expressions/Evaluation/ExpressionValue.cs`
MODULE: expressions | STAGE: 2
CONTRACT: 02b-expressions.md#null

```csharp
using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Evaluation;

/// <summary>
/// Значення виразу. Помилки — **значення**, а не винятки: одна зіпсована
/// комірка не має валити перерахунок усієї таблиці (02b §6.4).
/// </summary>
public readonly record struct ExpressionValue
{
    private ExpressionValue(ExpressionValueType type, object? value, string? errorCode)
    {
        Type = type;
        Value = value;
        ErrorCode = errorCode;
    }

    public ExpressionValueType Type { get; }
    public object? Value { get; }

    /// <summary>Код помилки (<c>#DIV/0</c>, <c>#REF</c>, <c>#VALUE</c>, <c>#UNIT</c>, <c>#CYCLE</c>).</summary>
    public string? ErrorCode { get; }

    public bool IsNull => Type == ExpressionValueType.Null;
    public bool IsError => Type == ExpressionValueType.Error;

    /// <summary>Порожнє значення.</summary>
    public static ExpressionValue Null { get; } = new(ExpressionValueType.Null, null, null);

    public static ExpressionValue Number(decimal v) => new(ExpressionValueType.Number, v, null);
    public static ExpressionValue Text(string v) => new(ExpressionValueType.Text, v, null);
    public static ExpressionValue Boolean(bool v) => new(ExpressionValueType.Boolean, v, null);
    public static ExpressionValue Date(DateTime v) => new(ExpressionValueType.Date, v, null);

    /// <summary>Помилка обчислення.</summary>
    public static ExpressionValue Error(string code) => new(ExpressionValueType.Error, null, code);

    /// <summary>Значення як <see cref="decimal"/>; <c>null</c>, якщо це не число.</summary>
    public decimal? AsNumber() => Type == ExpressionValueType.Number ? (decimal)Value! : null;
}
```

---

### `src/Ecr.Expressions/Evaluation/Evaluator.cs`
MODULE: expressions | STAGE: 2

```csharp
using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Evaluation;

/// <summary>
/// Обчислює AST. Уся арифметика — в <see cref="decimal"/>: порядок додавання
/// <c>float</c> змінює результат, і звірка з еталоном стає неможливою (D-30).
/// </summary>
public sealed class Evaluator(Functions.FunctionRegistry functions)
{
    /// <summary>Обчислює вираз у контексті.</summary>
    public ExpressionValue Evaluate(AstNode node, IEvaluationContext context)
        => throw new NotImplementedException(
            "TODO: рекурсивне обчислення з семантикою 02b §6 — це найтонше місце рушія:\n" +
            "— null в АГРЕГАТАХ ПОГЛИНАЄТЬСЯ: SUM ігнорує null, порожня множина → 0;\n" +
            "  AVERAGE не рахує null ані в сумі, ані в дільнику, порожня → null;\n" +
            "— null у БІНАРНИХ ОПЕРАТОРАХ ПОШИРЮЄТЬСЯ: null + 1 = null, null * 0 = null (НЕ 0!);\n" +
            "— виняток: конкатенація трактує null як порожній рядок;\n" +
            "— null = null → TRUE; null > 1 → null;\n" +
            "— ділення на нуль або на null → #DIV/0 як ЗНАЧЕННЯ, не виняток;\n" +
            "— помилка поширюється через операції; перехоплює лише IFERROR;\n" +
            "— IFERROR не перехоплює null (це не помилка).\n" +
            "Плутати два правила щодо null не можна: саме тут народжуються розбіжності " +
            "зі старою системою.");
}
```

---

### `src/Ecr.Expressions/Evaluation/IEvaluationContext.cs`
MODULE: expressions | STAGE: 2

```csharp
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
```

---

### `src/Ecr.Expressions/PeriodContext.cs`
MODULE: expressions | STAGE: 4

```csharp
using Ecr.Domain.Enums;

namespace Ecr.Expressions;

/// <summary>
/// Календарний контекст періоду.
/// </summary>
/// <remarks>
/// Значення залежать від <see cref="CalendarMode"/> і це **не косметика**:
/// перерахунок у <c>г/с</c> ділить на <see cref="Seconds"/>, тому різниця між
/// 365 і 366 днями змінює **всі** числа звіту — і виглядає як помилка формули,
/// а не як різниця конвенції (D-78).
/// </remarks>
public sealed class PeriodContext
{
    /// <summary>Створює контекст.</summary>
    /// <param name="start">Перший день періоду.</param>
    /// <param name="end">Останній день періоду.</param>
    /// <param name="mode">Календарна конвенція версії методології.</param>
    /// <param name="year">Рік.</param>
    /// <param name="sequence">Порядковий номер періоду в році.</param>
    public PeriodContext(DateOnly start, DateOnly end, CalendarMode mode, int year, byte sequence)
    {
        Start = start;
        End = end;
        Mode = mode;
        Year = year;
        Sequence = sequence;
    }

    public DateOnly Start { get; }
    public DateOnly End { get; }
    public CalendarMode Mode { get; }
    public int Year { get; }
    public byte Sequence { get; }

    /// <summary>Днів у періоді згідно з <see cref="Mode"/>.</summary>
    public int Days => throw new NotImplementedException(
        "TODO: Actual → фактична кількість днів (End - Start + 1); " +
        "Fixed365 → місяць як фактичний, але рік завжди 365; " +
        "Fixed360 → місяць 30, рік 360. Перевіряється тестом на обидва режими (02c §7).");

    /// <summary>Годин у періоді.</summary>
    public int Hours => Days * 24;

    /// <summary>Секунд у періоді — дільник при перерахунку в <c>г/с</c>.</summary>
    public long Seconds => (long)Days * 86400;
}
```

---

### `src/Ecr.Expressions/Functions/FunctionRegistry.cs`
MODULE: expressions | STAGE: 2

```csharp
using Ecr.Domain.Enums;
using Ecr.Expressions.Evaluation;

namespace Ecr.Expressions.Functions;

/// <summary>
/// Каталог функцій. Набір закритий: 11 для <c>Template</c>, 24 для
/// <c>Methodology</c> (02b §7–8). Розширення — зміна контракту, тобто
/// <c>questions.md</c> і зупинка.
/// </summary>
public sealed class FunctionRegistry
{
    /// <summary>Чи дозволена функція в діалекті.</summary>
    public bool IsAllowed(string name, ExpressionDialect dialect)
        => throw new NotImplementedException(
            "TODO: Template → рівно 11 функцій зі списку 02b §7; " +
            "Methodology → ті самі 11 плюс 13 із §8. Порівняння імені — регістронезалежне.");

    /// <summary>Сигнатура функції для перевірки при публікації.</summary>
    public FunctionSignature? GetSignature(string name)
        => throw new NotImplementedException("TODO: повернути сигнатуру або null, якщо функції немає.");

    /// <summary>Викликає функцію.</summary>
    public ExpressionValue Invoke(string name, IReadOnlyList<ExpressionValue> args, IEvaluationContext context)
        => throw new NotImplementedException("TODO: диспетчеризація за іменем у TemplateFunctions/MethodologyFunctions.");
}

/// <summary>Сигнатура функції.</summary>
/// <param name="Name">Ім'я.</param>
/// <param name="MinArgs">Мінімум аргументів.</param>
/// <param name="MaxArgs">Максимум; <c>null</c> — необмежено (агрегати).</param>
/// <param name="AcceptsRange">Чи приймає діапазон замість скалярів.</param>
/// <param name="ResultType">Тип результату.</param>
public sealed record FunctionSignature(
    string Name, int MinArgs, int? MaxArgs, bool AcceptsRange, ExpressionValueType ResultType);
```

---

### `src/Ecr.Expressions/Functions/TemplateFunctions.cs`
MODULE: expressions | STAGE: 2
CONTRACT: 02b-expressions.md#functions-template

```csharp
using Ecr.Expressions.Evaluation;

namespace Ecr.Expressions.Functions;

/// <summary>
/// Одинадцять функцій діалекту <c>Template</c> — рівно стільки, скільки
/// використовує чинний шаблон.
/// </summary>
/// <remarks>
/// <c>VLOOKUP</c> відсутній навмисно: усі 429 його входжень у чинному шаблоні —
/// звернення до довідників, які тут замінені посиланням на реєстр. Це не
/// спрощення, а усунення цілого класу помилок «діапазон з'їхав».
/// </remarks>
public static class TemplateFunctions
{
    /// <summary>Сума; <c>null</c> ігноруються; порожня множина → <c>0</c>.</summary>
    public static ExpressionValue Sum(IReadOnlyList<ExpressionValue> args)
        => throw new NotImplementedException("TODO: підсумувати не-null числа в decimal; порожня множина → 0.");

    /// <summary>Середнє не-<c>null</c>; порожня множина → <c>null</c> (а не <c>0</c>).</summary>
    public static ExpressionValue Average(IReadOnlyList<ExpressionValue> args)
        => throw new NotImplementedException("TODO: null не входять ані в суму, ані в дільник.");

    public static ExpressionValue Min(IReadOnlyList<ExpressionValue> args)
        => throw new NotImplementedException("TODO: мінімум не-null; порожня → null.");

    public static ExpressionValue Max(IReadOnlyList<ExpressionValue> args)
        => throw new NotImplementedException("TODO: максимум не-null; порожня → null.");

    public static ExpressionValue Count(IReadOnlyList<ExpressionValue> args)
        => throw new NotImplementedException(
            "TODO: підрахувати елементи, де Type != Null і не Error; помилки НЕ рахувати як значення.");

    /// <summary>
    /// Округлення. **`MidpointRounding.AwayFromZero`** — саме так рахує чинна
    /// система; банківське округлення дало б інші числа у звіті.
    /// </summary>
    public static ExpressionValue Round(ExpressionValue value, ExpressionValue digits)
        => throw new NotImplementedException(
            "TODO: Math.Round(decimal, int, MidpointRounding.AwayFromZero). " +
            "ROUND(2.5, 0) = 3, ROUND(-2.5, 0) = -3 (02c E12, E13).");

    public static ExpressionValue Abs(ExpressionValue value)
        => throw new NotImplementedException(
            "TODO: Math.Abs для decimal; null → null (поширення), помилка → та сама помилка.");

    /// <summary>Добуток не-<c>null</c>; порожня множина → <c>1</c>.</summary>
    public static ExpressionValue Product(IReadOnlyList<ExpressionValue> args)
        => throw new NotImplementedException("TODO: порожня множина → 1, не 0.");

    public static ExpressionValue If(ExpressionValue condition, ExpressionValue then, ExpressionValue otherwise)
        => throw new NotImplementedException(
            "TODO: condition має бути Boolean — інакше це помилка ПУБЛІКАЦІЇ, не рантайму; " +
            "тут лише вибір гілки. null-умова → null.");

    /// <summary>Перехоплює **лише** помилки, не <c>null</c>.</summary>
    public static ExpressionValue IfError(ExpressionValue value, ExpressionValue fallback)
        => throw new NotImplementedException("TODO: value.IsError → fallback; null → null (02c E8).");

    public static ExpressionValue SumIf(IReadOnlyList<ExpressionValue> range,
                                        IReadOnlyList<ExpressionValue> conditions,
                                        IReadOnlyList<ExpressionValue>? sumRange)
        => throw new NotImplementedException("TODO: підсумувати елементи, де умова TRUE.");
}
```

---

### `src/Ecr.Expressions/Functions/ConvertFunction.cs`
MODULE: expressions | STAGE: 4

```csharp
using Ecr.Expressions.Evaluation;

namespace Ecr.Expressions.Functions;

/// <summary>
/// <c>CONVERT(value, fromUnit, toUnit)</c> — **єдиний** спосіб змінити одиницю
/// у виразі. Рушій ніколи не конвертує неявно (D-74).
/// </summary>
public static class ConvertFunction
{
    /// <summary>Виконує конверсію через контекст.</summary>
    public static ExpressionValue Invoke(IReadOnlyList<ExpressionValue> args, IEvaluationContext context)
        => throw new NotImplementedException(
            "TODO: перевірити 3 аргументи; 2-й і 3-й — текстові коди одиниць; " +
            "делегувати context.Convert. Різні розмірності → ExpressionValue.Error(\"#UNIT\"). " +
            "Контекстні коефіцієнти (щільність) сюди НЕ передаються: перехід м³ → кг робиться " +
            "як CONVERT(@Volume * CST.Density, 'kg', 't') (ФВ-16.5).");
}
```

---

### `src/Ecr.Expressions/FormulaEngine.cs`
MODULE: expressions | STAGE: 2
CONTRACT: 02-contracts.md#ports

```csharp
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Graph;
using Ecr.Expressions.Parsing;

namespace Ecr.Expressions;

/// <summary>
/// Реалізація <see cref="IFormulaEngine"/> — фасад над лексером, парсером,
/// резолвером, перевірками і обчислювачем.
/// </summary>
public sealed class FormulaEngine(
    Parser parser,
    DependencyExtractor dependencyExtractor,
    Evaluator evaluator,
    TopologicalSorter sorter) : IFormulaEngine
{
    /// <inheritdoc />
    public ParseResult Parse(string expression, ExpressionDialect dialect)
        => throw new NotImplementedException(
            "TODO: делегувати parser.Parse; при помилці не кидати виняток, а повернути ParseResult " +
            "із діагностиками — конфігуратор має показати проблему користувачеві, а не впасти.");

    /// <inheritdoc />
    public IReadOnlyList<FormulaDependencyRef> ExtractDependencies(
        ParsedExpression expression, DependencyContext context)
        => throw new NotImplementedException("TODO: делегувати dependencyExtractor і спроєктувати в контрактний тип.");

    /// <inheritdoc />
    public EvaluationResult Evaluate(ParsedExpression expression, IEvaluationContext context)
        => throw new NotImplementedException("TODO: делегувати evaluator; загорнути результат і діагностики.");

    /// <inheritdoc />
    public OrderingResult BuildEvaluationOrder(IReadOnlyList<FormulaNode> nodes)
        => throw new NotImplementedException(
            "TODO: побудувати DependencyGraph із nodes і делегувати sorter. " +
            "Цикл → OrderingResult із CyclePath, а не виняток.");
}
```

> **Типи `FormulaDependencyRef`, `DependencyContext`, `FormulaNode`,
> `EvaluationResult`** оголошені в `src/Ecr.Application/Ports/IFormulaEngine.cs`
> поруч із інтерфейсом — вони частина контракту порту, а не деталь реалізації.
