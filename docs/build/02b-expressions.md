# 02b — Мова виразів. Граматика і семантика

> Частина контракту. Парсер реалізується **точно за цією граматикою**.
> В `docs/reference/backend/B03-expressions.md` §3 наведено ескіз — цей файл його
> замінює й уточнює (`R-B1`).
>
> **Один парсер, два діалекти.** Різниця між `Template` і `Methodology` —
> тільки в наборі дозволених **посилань** і **функцій**. Синтаксис, пріоритети
> операторів, приведення типів і семантика `null` — спільні.

## Зміст

| Якір | Розділ |
|---|---|
| [`#grammar`](#grammar) | EBNF |
| [`#precedence`](#precedence) | Пріоритети операторів |
| [`#references`](#references) | Посилання: синтаксис і резолвінг |
| [`#ranges`](#ranges) | Діапазони рядків |
| [`#types`](#types) | Типи і приведення |
| [`#null`](#null) | Семантика `null` і помилок |
| [`#functions-template`](#functions-template) | Функції діалекту `Template` |
| [`#functions-methodology`](#functions-methodology) | Функції діалекту `Methodology` |
| [`#convert`](#convert) | `CONVERT` — єдиний спосіб змінити одиницю |
| [`#period`](#period) | Календарний контекст |
| [`#ast`](#ast) | Модель AST |
| [`#publish-checks`](#publish-checks) | Перевірки при публікації |

---

<a id="grammar"></a>
## 1. EBNF

```ebnf
expression      = ternary ;

ternary         = or_expr [ "?" expression ":" expression ] ;

or_expr         = and_expr { ( "||" | "OR" ) and_expr } ;
and_expr        = not_expr { ( "&&" | "AND" ) not_expr } ;
not_expr        = [ "!" | "NOT" ] comparison ;

comparison      = concat [ ( "=" | "==" | "<>" | "!=" | "<" | "<=" | ">" | ">=" ) concat ] ;

concat          = additive { "&" additive } ;              (* конкатенація рядків *)

additive        = multiplicative { ( "+" | "-" ) multiplicative } ;
multiplicative  = power { ( "*" | "/" | "%" ) power } ;
power           = unary [ "^" power ] ;                     (* правоасоціативний *)
unary           = [ "-" | "+" ] primary ;

primary         = literal
                | reference
                | function_call
                | "(" expression ")" ;

literal         = number | string | boolean | "NULL" ;
number          = digit { digit } [ "." digit { digit } ] ;
string          = "'" { character } "'" ;                   (* подвоєння '' для екранування *)
boolean         = "TRUE" | "FALSE" ;

function_call   = identifier "(" [ argument { "," argument } ] ")" ;
argument        = expression | range_ref ;

identifier      = letter { letter | digit | "_" } ;

(* ---------- посилання ---------- *)

reference       = cell_ref | arg_ref | const_ref | formula_ref | header_ref | period_ref ;

cell_ref        = "[" code "]" "." "[" code "]" "." "[" row_selector "]" "." "[" column_selector "]"
                | "[" code "]" "." "[" row_selector "]" "." "[" column_selector "]"   (* та сама таблиця *)
                | "[" column_selector "]" ;                                            (* той самий рядок *)

row_selector    = row_key | range_ref | predicate ;
row_key         = char { char } ;                           (* 7001001, GUID *)
range_ref       = row_key ":" row_key ;
predicate       = "WHERE" ws condition ;                    (* для RowMode = Dynamic *)

column_selector = code | "{Month}" | "{Period}" ;           (* підстановка поточної колонки *)

arg_ref         = "@" identifier ;                          (* Methodology: поле рядка джерела *)
const_ref       = "CST" "." identifier ;                    (* Methodology: константа *)
formula_ref     = "!" identifier ;                          (* Methodology: інша формула цієї версії *)
header_ref      = "HDR" "." identifier ;                    (* поле шапки документа *)
period_ref      = "[" "Period" [ ":" [ "+" | "-" ] digit { digit } ] "]" ;

code            = letter { letter | digit | "_" } ;         (* EcrCode, R-B6 *)
```

**Лексер.** Ідентифікатори всередині `[...]` не екрануються — саме тому
`EcrCode` не може містити `]`, `.`, пробіл і починатися з цифри (`R-B6`).
`RowKey` дозволяє цифри і дефіси, бо стоїть в окремій позиції граматики.

**Коментарі** в виразах не підтримуються. Пояснення живуть у полі опису
формули, а не в тексті виразу.

---

<a id="precedence"></a>
## 2. Пріоритети операторів

Від найвищого до найнижчого. Асоціативність зліва направо, крім `^`.

| # | Оператори | Асоціативність | Примітка |
|---|---|---|---|
| 1 | `(...)`, виклик функції | — | |
| 2 | унарні `-`, `+`, `!`, `NOT` | права | |
| 3 | `^` | **права** | `2^3^2 = 2^(3^2) = 512` |
| 4 | `*`, `/`, `%` | ліва | |
| 5 | `+`, `-` | ліва | |
| 6 | `&` | ліва | конкатенація |
| 7 | `=`, `==`, `<>`, `!=`, `<`, `<=`, `>`, `>=` | ліва | |
| 8 | `&&`, `AND` | ліва | |
| 9 | `\|\|`, `OR` | ліва | |
| 10 | `? :` | права | |

> `=` і `==` — синоніми **порівняння**. Присвоєння в мові немає взагалі, тому
> двозначності, як у C-подібних мовах, не виникає.

---

<a id="references"></a>
## 3. Посилання

### 3.1 Форми посилання на комірку

| Форма | Значення |
|---|---|
| `[Water_07].[Main].[7001001].[Jan]` | аркуш → таблиця → рядок → колонка |
| `[Main].[7001001].[Jan]` | таблиця в **тому самому** аркуші |
| `[7001001].[Jan]` | рядок у **тій самій** таблиці |
| `[Jan]` | колонка в **тому самому** рядку (для формул рівня рядка) |
| `[Water_07].[Main].[7001001].[{Month}]` | колонка = поточна місячна колонка |

### 3.2 Крос-періодні посилання

```
[Period:-1].[Main].[7001001].[Total]     -- попередній період
[Period:+1].[Main].[7001001].[Total]     -- наступний (для планових таблиць)
[Period].[Main].[7001001].[Total]        -- поточний, явно
```

Зсув застосовується до **порядкового номера періоду в межах проєкту**. Вихід
за межі проєкту → `null`, **не помилка**: січень не має попереднього місяця,
і це нормальна ситуація, а не збій.

### 3.3 Резолвінг

1. Код аркуша/таблиці/колонки шукається в **опублікованій версії шаблону**
   документа. Не знайдено → `ECR-TMPL-4222` **при публікації**, не в рантаймі.
2. `RowKey` для `RowMode = Fixed` має існувати в `cfg.RowDef`.
3. Для `RowMode = Dynamic` конкретний `RowKey` у формулі **заборонений** —
   тільки предикат (§4.2): динамічні рядки створює користувач, і посилання на
   конкретний з них не має сенсу.
4. `HDR.<field>` — поле документа з `IsBusinessKey` або `IsScopeField`.

### 3.4 Діалект `Methodology`

| Форма | Значення |
|---|---|
| `@FuelConsumption` | поле рядка джерела |
| `CST.EF_CO2` | константа методології, вже резолвлена за категорією і датою |
| `!BaseEmission` | результат іншої формули **цієї самої** версії методології |
| `HDR.Train` | поле шапки |
| `[Period].Days` | календарний контекст (§10) |

Посилання на комірки документів (`[Sheet].[Table]…`) у діалекті `Methodology`
**заборонені**: методологія працює з підготовленими аргументами, а не лізе в
документ сама. Це межа, яка робить методологію переносною між шаблонами.

---

<a id="ranges"></a>
## 4. Діапазони рядків

### 4.1 Фіксовані таблиці

```
SUM([Main].[7001001:7001005].[Jan])
```

**Діапазон розкривається в явний список `RowKey` на момент `Publish`**
і зберігається в `cfg.FormulaDependency`. **У рантаймі діапазонів не існує.**

Порядок розкриття — за `Ordinal` **на момент публікації**. Наслідки, які треба
розуміти:

1. Зміна `Ordinal` після публікації (презентаційна операція) **не змінює**
   результат формули. Це саме те, що обіцяно: презентаційна зміна не може
   вплинути на дані.
2. Додати рядок «усередину» діапазону — **структурна** зміна, тобто нова версія
   шаблону. Це логічно: «сюди тепер входить ще один рядок» змінює зміст звіту,
   а не оформлення.
3. Розкритий діапазон показується в UI («формула охоплює рядки 7001001…7001005»),
   що знімає цілий клас питань «а чому не порахувалося».

### 4.2 Динамічні таблиці

Статичного списку не існує, тому діапазон записується **предикатом**:

```
SUM([Main].[WHERE RowKind = 'Item'].[Amount])
SUM([Main].[WHERE RowKind = 'Item' AND [Category] = 'Fuel'].[Amount])
```

Предикат зберігається в `cfg.FormulaDependency.FilterJson` і обчислюється в
рантаймі над фактичними рядками екземпляра таблиці.

Дозволені в предикаті: посилання на колонки того самого рядка, `RowKind`,
літерали, оператори порівняння і логіки. **Заборонені**: виклики функцій,
крос-періодні посилання, вкладені предикати — інакше предикат стає другою
мовою всередині мови.

---

<a id="types"></a>
## 5. Типи і приведення

Типи виразу: `Number` (`decimal`), `Text` (`string`), `Boolean`, `Date`, `Null`.

**`float`/`double` в обчисленнях не використовуються ніде** (`D-30`): усе
`decimal`, бо порядок додавання `float` змінює результат, і звірка з еталоном
стає неможливою.

### Правила приведення

| Операція | Правило |
|---|---|
| `Number` + `Number` | `Number` |
| `Text` `&` будь-що | `Text` (друге приводиться до тексту інваріантно) |
| `Number` + `Text` | **помилка публікації** `ECR-TMPL-4222`, не спроба вгадати |
| `Boolean` в арифметиці | **помилка публікації** |
| `Date` − `Date` | `Number` (днів) |
| `Date` + `Number` | `Date` (додати днів) |
| порівняння різних типів | **помилка публікації** |
| `Lookup`-комірка в арифметиці | помилка; для числа використовуйте поле реєстру |

> **Приведення не відбувається мовчки.** Excel вгадує тип і саме тому дає
> «майже правильні» числа; тут неоднозначність — помилка публікації, коли її
> ще дешево виправити.

**Формат чисел у літералах** — інваріантний: десятковий роздільник тільки `.`,
роздільників тисяч немає.

---

<a id="null"></a>
## 6. Семантика `null` і помилок

Це найтонше місце мови. Два різні правила, і плутати їх не можна.

### 6.1 `null` в агрегатах — **поглинається**

```
SUM(...)      -- null-елементи ігноруються; SUM порожньої множини = 0
AVERAGE(...)  -- null не входять ані в суму, ані в дільник; порожня множина → null
COUNT(...)    -- рахує лише не-null
MIN / MAX     -- ігнорують null; порожня множина → null
```

Це **сумісність з Excel** і з чинними числами: інакше одна незаповнена комірка
обнуляла б увесь звіт.

### 6.2 `null` у бінарних операторах — **поширюється**

```
null + 1     → null
null * 0     → null        (не 0!)
null & 'x'   → 'x'         (виняток: конкатенація трактує null як порожній рядок)
null = null  → TRUE
null = 1     → FALSE
null > 1     → null        (порівняння з null дає null, крім рівності)
```

### 6.3 Порожня комірка vs явна порожнеча

| Стан комірки | Значення у виразі |
|---|---|
| рядка `CellValue` немає | `ColumnDef.DefaultValue`, або `null`, якщо його немає |
| `IsEmpty = 1` | завжди `null`, `DefaultValue` **не застосовується** |
| значення є | значення |

Різниця істотна: «не заповнювали» може мати дефолт, «свідомо лишили порожнім» —
ні.

### 6.4 Помилки обчислення

Помилки — **значення**, а не винятки: одна зіпсована комірка не має валити
перерахунок усієї таблиці.

| Помилка | Коли |
|---|---|
| `#DIV/0` | ділення на нуль або на `null` |
| `#REF` | посилання не резолвиться в рантаймі (рядок видалено) |
| `#VALUE` | несумісні типи, які не вдалося відсіяти при публікації |
| `#UNIT` | несумісні одиниці в рантаймі |
| `#CYCLE` | цикл, виявлений у рантаймі (аварійний випадок) |

Помилка **поширюється** через операції: `#DIV/0 + 1 = #DIV/0`.
Перехопити її можна лише `IFERROR`.

Комірка з помилкою зберігається як `IsEmpty = 0`, `ValueString = '#DIV/0'`,
`IsCalculated = 1` — щоб її було видно у звіті, а не «просто порожньо».

---

<a id="functions-template"></a>
## 7. Функції діалекту `Template`

**Рівно одинадцять** — стільки використовує чинний шаблон
(`docs/01-as-is-overview.md`). Розширення набору — зміна контракту, тобто
`questions.md` і зупинка.

| Функція | Сигнатура | Семантика |
|---|---|---|
| `SUM` | `SUM(range \| number, …)` | сума; `null` ігноруються; порожня множина → `0` |
| `AVERAGE` | `AVERAGE(range \| number, …)` | середнє не-`null`; порожня множина → `null` |
| `MIN` | `MIN(range \| number, …)` | мінімум не-`null`; порожня → `null` |
| `MAX` | `MAX(range \| number, …)` | максимум не-`null`; порожня → `null` |
| `COUNT` | `COUNT(range \| any, …)` | кількість не-`null` |
| `ROUND` | `ROUND(number, digits)` | **банківське округлення заборонене**: `MidpointRounding.AwayFromZero` — саме так рахує чинна система |
| `ABS` | `ABS(number)` | модуль |
| `PRODUCT` | `PRODUCT(range \| number, …)` | добуток не-`null`; порожня → `1` |
| `IF` | `IF(condition, then, else)` | `condition` має бути `Boolean`, інакше помилка публікації |
| `IFERROR` | `IFERROR(value, fallback)` | перехоплює **лише** помилки §6.4, не `null` |
| `SUMIF` | `SUMIF(range, condition, sum_range?)` | сума за умовою; `condition` — вираз над рядком |

> **`VLOOKUP` відсутній навмисно.** Усі 429 його входжень у чинному шаблоні —
> звернення до довідників, які тут замінені посиланням на реєстр
> (`ColumnDef.LookupRegistryDefId` + `ValueRegistryEntryId`). Функція пошуку по
> діапазону більше не потрібна, і її відсутність — це не спрощення, а усунення
> цілого класу помилок «діапазон з'їхав».

---

<a id="functions-methodology"></a>
## 8. Функції діалекту `Methodology`

24 функції. Включає всі 11 із §7 (з тією самою семантикою) плюс 13 нижче.

| Функція | Сигнатура | Семантика |
|---|---|---|
| `CONVERT` | `CONVERT(number, fromUnit, toUnit)` | §9 — **єдиний** спосіб змінити одиницю |
| `POWER` | `POWER(number, exponent)` | те саме, що `^` |
| `SQRT` | `SQRT(number)` | від'ємний аргумент → `#VALUE` |
| `EXP` | `EXP(number)` | |
| `LN` | `LN(number)` | аргумент ≤ 0 → `#VALUE` |
| `LOG10` | `LOG10(number)` | |
| `CEILING` | `CEILING(number, significance?)` | |
| `FLOOR` | `FLOOR(number, significance?)` | |
| `TRUNC` | `TRUNC(number, digits?)` | відкидання, не округлення |
| `MOD` | `MOD(number, divisor)` | знак результату — як у діленого |
| `COALESCE` | `COALESCE(a, b, …)` | перше не-`null` |
| `SWITCH` | `SWITCH(value, case1, result1, …, default?)` | без збігу і без `default` → `null` |
| `SUBSTANCE` | `SUBSTANCE(code)` | значення для конкретної речовини в контексті методології |

**Заборонені в обох діалектах:** будь-які функції поточного часу (`NOW`,
`TODAY`), випадковості, введення-виведення, звернення до БД. Час береться
**лише** з календарного контексту §10 — інакше результат перерахунку залежить
від дня, коли його запустили.

---

<a id="convert"></a>
## 9. `CONVERT` і одиниці

```
CONVERT(@FuelMass, 't', 'kg')          → множення на 1000
CONVERT(!Emission, 'kg', 'g_per_s')    → ⛔ ПОМИЛКА: різні розмірності
```

**Маршрут конверсії** (`ФВ-16.3`):

```
1. fromUnit = toUnit                          → значення без змін
2. Є рядок uom.Conversion (from, to)          → value * Factor + Offset
3. Однакова DimensionId                       → через базову одиницю:
      base   = value * From.FactorToBase + From.OffsetToBase
      result = (base - To.OffsetToBase) / To.FactorToBase
4. Різні DimensionId                          → ⛔ #UNIT / ECR-UOM-0422
```

**Неявних конверсій не буває** (`D-74`). Якщо колонка оголошена в `t`, а
константа в `kg_per_t`, і результат оголошений у `t` — рушій **не** підганяє
множники сам. Або вираз містить `CONVERT`, або публікація відхиляється
(`ECR-TMPL-4223`).

**Контекстні коефіцієнти не є конверсіями** (`ФВ-16.5`). Перехід «м³ → кг»
робиться множенням на щільність із `calc.MethodologyConstant`:

```
CONVERT(@Volume * CST.Density, 'kg', 't')
```

а не `CONVERT(@Volume, 'm3', 'kg')` — останнє неможливе за побудовою і
відхиляється `CHECK`-обмеженням у БД.

---

<a id="period"></a>
## 10. Календарний контекст

```
[Period].Days      -- днів у періоді
[Period].Hours     -- годин
[Period].Seconds   -- секунд
[Period].Start     -- перша дата (Date)
[Period].End       -- остання дата (Date)
[Period].Year      -- рік
[Period].Sequence  -- порядковий номер у році
```

**Значення залежать від `MethodologyVersion.CalendarMode`** (`D-78`):

| `CalendarMode` | `Days` для вересня 2026 | `Days` для року |
|---|---|---|
| `Actual` | 30 (фактичний календар) | 365 або 366 |
| `Fixed365` | 30 | **365 завжди** |
| `Fixed360` | **30 завжди** | **360** |

> Це не косметика. Перерахунок у `г/с` ділить на `[Period].Seconds`; різниця
> між 365 і 366 днями змінює **всі** числа звіту, і виглядає це як помилка
> формули, а не як різниця конвенції. Тому режим видимий у конфігураторі й
> обов'язковий у diff при публікації.

У діалекті `Template` `CalendarMode` завжди `Actual`: формули шаблону не
перераховують потужності.

---

<a id="ast"></a>
## 11. Модель AST

```csharp
// src/Ecr.Expressions/Ast/AstNode.cs
namespace Ecr.Expressions.Ast;

/// <summary>Вузол синтаксичного дерева виразу.</summary>
public abstract record AstNode
{
    /// <summary>Позиція в тексті виразу — для повідомлень про помилки.</summary>
    public int Position { get; init; }
}

/// <summary>Літерал: число, текст, булеве значення або NULL.</summary>
public sealed record LiteralNode(object? Value, ExpressionValueType Type) : AstNode;

/// <summary>Бінарна операція.</summary>
public sealed record BinaryNode(BinaryOperator Operator, AstNode Left, AstNode Right) : AstNode;

/// <summary>Унарна операція.</summary>
public sealed record UnaryNode(UnaryOperator Operator, AstNode Operand) : AstNode;

/// <summary>Тернарний оператор <c>? :</c>.</summary>
public sealed record ConditionalNode(AstNode Condition, AstNode WhenTrue, AstNode WhenFalse) : AstNode;

/// <summary>Виклик функції.</summary>
public sealed record FunctionNode(string Name, IReadOnlyList<AstNode> Arguments) : AstNode;

/// <summary>Посилання на комірку або діапазон.</summary>
public sealed record CellReferenceNode(
    string? SheetCode,
    string? TableCode,
    RowSelector Row,
    string ColumnSelector,
    int PeriodOffset) : AstNode;

/// <summary>Посилання діалекту Methodology: <c>@Arg</c>, <c>CST.X</c>, <c>!Formula</c>, <c>HDR.Y</c>.</summary>
public sealed record SymbolReferenceNode(SymbolKind Kind, string Name) : AstNode;

/// <summary>Календарний контекст: <c>[Period].Days</c> тощо.</summary>
public sealed record PeriodPropertyNode(string Property, int PeriodOffset) : AstNode;

/// <summary>Селектор рядків: конкретний ключ, діапазон або предикат.</summary>
public abstract record RowSelector
{
    /// <summary>Конкретний рядок.</summary>
    public sealed record Single(string RowKey) : RowSelector;

    /// <summary>Діапазон; розкривається в список RowKey при Publish.</summary>
    public sealed record Range(string FromRowKey, string ToRowKey) : RowSelector;

    /// <summary>Предикат для RowMode = Dynamic; обчислюється в рантаймі.</summary>
    public sealed record Predicate(AstNode Condition) : RowSelector;

    /// <summary>Той самий рядок, що й у формули (для Scope = Row).</summary>
    public sealed record Current : RowSelector;
}

public enum ExpressionValueType : byte { Null = 0, Number = 1, Text = 2, Boolean = 3, Date = 4, Error = 5 }
public enum SymbolKind : byte { Argument = 0, Constant = 1, Formula = 2, Header = 3 }
public enum UnaryOperator : byte { Negate = 0, Plus = 1, Not = 2 }

public enum BinaryOperator : byte
{
    Add = 0, Subtract = 1, Multiply = 2, Divide = 3, Modulo = 4, Power = 5,
    Concat = 6,
    Equal = 7, NotEqual = 8, Less = 9, LessOrEqual = 10, Greater = 11, GreaterOrEqual = 12,
    And = 13, Or = 14
}
```

```csharp
// src/Ecr.Expressions/ParseResult.cs
namespace Ecr.Expressions;

using Ecr.Expressions.Ast;

/// <summary>
/// Результат розбору. Помилка синтаксису — це <b>результат</b>, а не виняток:
/// конфігуратор має показати проблему користувачеві, а не впасти.
/// </summary>
public sealed record ParseResult(
    bool IsSuccess,
    ParsedExpression? Expression,
    IReadOnlyList<ExpressionDiagnostic> Diagnostics);

/// <summary>Розібраний вираз, готовий до обчислення.</summary>
public sealed record ParsedExpression(
    string SourceText,
    Ecr.Domain.Enums.ExpressionDialect Dialect,
    AstNode Root,
    ExpressionValueType ResultType);

/// <summary>Діагностика розбору або перевірки.</summary>
/// <param name="Code">Код із каталогу помилок (<c>ECR-TMPL-*</c>).</param>
/// <param name="Message">Локалізоване повідомлення.</param>
/// <param name="Position">Позиція в тексті виразу.</param>
/// <param name="Length">Довжина проблемного фрагмента.</param>
public sealed record ExpressionDiagnostic(string Code, string Message, int Position, int Length);
```

---

<a id="publish-checks"></a>
## 12. Перевірки при публікації

Виконуються **всі** до першої публікації версії. Публікація або проходить
цілком, або відхиляється з переліком проблем.

| # | Перевірка | Код помилки |
|---|---|---|
| 1 | Кожен вираз синтаксично коректний | `ECR-TMPL-0422` |
| 2 | Кожне посилання резолвиться | `ECR-TMPL-4222` |
| 3 | Типи сумісні в кожній операції | `ECR-TMPL-4222` |
| 4 | Граф залежностей ациклічний | `ECR-TMPL-4221` |
| 5 | Діапазони розкриті в списки `RowKey` і збережені | `ECR-TMPL-0422` |
| 6 | Обчислено `EvaluationOrder` для кожної формули | — |
| 7 | Функція існує в дозволеному наборі діалекту | `ECR-TMPL-0422` |
| 8 | Кількість і типи аргументів функції відповідають сигнатурі | `ECR-TMPL-0422` |
| 9 | **Одиниці сумісні або є явний `CONVERT`** | `ECR-TMPL-4223` |
| 10 | Результат формули сумісний за розмірністю з `OutputUnitId` | `ECR-TMPL-4223` |
| 11 | Предикат динамічного діапазону не містить заборонених конструкцій | `ECR-TMPL-0422` |
| 12 | Конкретний `RowKey` не використовується для `RowMode = Dynamic` | `ECR-TMPL-4222` |

**Чому саме при публікації, а не в рантаймі.** Помилка типу або одиниці,
виявлена під час нічного перерахунку, — це неправильні числа в звіті, які
хтось помітить через місяць на звірці. Виявлена при публікації — це червоний
екран конфігуратора, який виправляють за хвилину.
