# Методології як скрипт: план реалізації (MS)

Стан: 18.11.2026. Автор плану — Claude (Fable), виконавці — сабагенти на Sonnet, перевірка — координатор.
Гілка роботи: `lane/integ-ms-script` (worktree `F:\ecr-wt\ms`). Правила репозиторію — `CLAUDE.md`, `docs/build/HANDOVER-coordinator.md`.

## 0. Мета і рішення людини

**Мета.** Замість ~14 типів об'єктів (формули, константи, категорії, правило категорії, правила вибору рядків, фільтр активності, виходи, речовини, імпорти…) версія методології описується **одним текстом** — скриптом у C#-подібному стилі. Користувач — еколог: просто, але гнучко.

**Рішення людини (17–18.11.2026):**

1. Власна C#-подібна мова, **не** справжній C# (безпека, збіг чисел зі старою системою, перевірки при публікації). Мова має бути гнучкою.
2. Автор — еколог: баланс простоти й можливостей.
3. Константи — у тексті скрипта, **без вікон дат усередині версії**: нове значення з нової дати — нова версія методології (нове авторство). Історичні вікна наявних версій конвертер розгортає в умову за `period.EndKey` (розд. 8.3).
4. Усі наявні методології (з бази прода) мають переноситися в нову модель інструментом + Claude, з доказом збігу чисел.

**Рішення плану (автор плану; людина може змінити):**

- R1. Скрипт описує **логіку розрахунку**: звідки рядки, входи, константи, проміжні величини, речовини, виходи, відмови. **Тести** (`calc.TestCase`), **обов'язкові колонки** (`calc.MethodologyRequiredInput`) і **прив'язки виходів до колонок** (`cfg.CalculationBinding`) лишаються даними поза текстом, але з простим UI і автоматикою (розд. 6, 7).
- R2. Технічні режими версії (`NumericMode`, `CalendarMode`, `TraceLevel`) лишаються властивостями версії, не текстом.
- R3. Старий рушій не чіпаємо: версія має поле `Engine` (`Formulas` | `Script`). Нові версії — `Script`; старі працюють як були, доки їх не перенесено (розд. 8). Жодного «великого вибуху».
- R4. Числа рахує **той самий** обчислювач (`Ecr.Expressions.Evaluation.Evaluator` + `IEvaluationArithmetic`): скрипт компілюється в наявні вузли AST діалекту методологій. Нової арифметики немає.

## 1. Як зараз (карта коду)

| Що | Де |
|---|---|
| Сутності версії | `src/Ecr.Domain/Entities/Calculations/*` (`MethodologyVersion` — агрегат; `MethodologyFormula`, `MethodologyConstant`, `MethodologyCategory`, `MethodologyCategoryRule`, `MethodologyRule`, `MethodologySubstance`, `MethodologyOutput`, `MethodologyRequiredInput`, `MethodologyImport`, `MethodologyTestCaseEntity`) |
| Прив'язка вихід → колонка | `src/Ecr.Domain/Entities/Configuration/CalculationBinding.cs` (на методологію, не на версію; копіюється при клоні версії шаблону — `TemplateVersionStore.cs:~701`) |
| Оркестратор | `src/Ecr.Calculations/CalculationOrchestrator.cs` (`RunAsync` :96, `ExecuteAsync` :~383) |
| Модуль формул | `src/Ecr.Calculations/GenericCalculationModule.cs` (`PrepareAsync` :78, `ExecuteAsync` :378, `MissingArgumentsOf` :683, `Emit` :771, `Evaluate` :897, `Describe` :987, `LibraryRun` :1083, правило категорії :1261–1400, `ResolveConstants` :1401, `ToValue` :1592) |
| Вибір рядків | `src/Ecr.Calculations/MethodologyResolver.cs` (`MatchRowsDetailedAsync` :209), `MethodologyRuleMatcher.cs` (текстова рівність; `Text(CellValueData)`) |
| Входи рядка | `src/Ecr.Calculations/CalculationInputBuilder.cs` (аргумент = код колонки; місячні псевдоніми `cfg.MonthInputAlias`; `FlareEventArguments.Derive`) |
| Контракт модуля | `src/Ecr.Application/Ports/ICalculationModule.cs` (`CalculationBindingContext`, `MethodologyDescriptor`, `CalculationInput/Output`) |
| Мова виразів | `src/Ecr.Expressions/**` (`Ast/AstNode.cs`, `Evaluation/Evaluator.cs`, `Functions/DialectCatalog.cs`, `Functions/MethodologyFunctions.cs`, `Evaluation/RegistryForms.cs`) |
| Публікація | `src/Ecr.Application/Calculations/PublishMethodologyHandler.cs`, `MethodologyPublishChecks.cs`, `GoldenSet.cs` |
| Авторинг | `src/Ecr.Application/Calculations/MethodologyAuthoringHandlers.cs`, `MethodologyDraftHandlers.cs`; клон — `src/Ecr.Infrastructure/Persistence/MethodologyDraftStore.cs` |
| API | `src/Ecr.Api/Controllers/MethodologiesController.cs` (32 маршрути), `MethodologyVersionsController.cs` |
| Клієнт | `src/Ecr.Web/src/pages/admin/MethodologiesPage.tsx`, `MethodologyVersionsPage.tsx`, `src/Ecr.Web/src/features/methodologies/**`, редактор Monaco — `src/Ecr.Web/src/features/expressions/**` |
| DI розрахунків | `src/Ecr.Calculations/DependencyInjection.cs` (остання реєстрація `ICalculationModule` — Generic: її беруть публікація й симуляція) |
| Модуль HSE400 | `src/Ecr.Calculations/MonthColumnArgumentsModule.cs` (рівень `Module`, місяці в колонках) |

Числова семантика, яку не можна змінити: режим `Legacy` — подвійна точність NCalc 1.3.8, `NaN/±∞` після **кожного кроку** маскуються в 0 з записом у трейс (`GenericCalculationModule.Evaluate` :940–950, `MaskedZero`), округлення виходу — `NumericPolicy.RoundOutput` зі шкалою колонки (`Emit`).

## 2. Цільова архітектура

```
текст скрипта (calc.MethodologyVersion.ScriptText)
      │  Ecr.Scripting: лексер → парсер → зв'язування → CompiledScript
      │  (вирази опущено в AstNode діалекту Methodology)
      ├─ збереження/публікація (Ecr.Application): перевірки оточення + МАТЕРІАЛІЗАЦІЯ
      │     calc.MethodologyOutput, calc.MethodologySubstance, calc.MethodologyImport,
      │     cfg.CalculationBinding (лише додавання відсутніх), cfg.RegistryUse
      └─ прогін (Ecr.Calculations):
            ScriptRowSelector  — які рядки (rows from … where … active when …)
            CalculationInputBuilder — входи рядка (БЕЗ змін)
            ScriptCalculationModule — виконує CompiledScript на рядок через Evaluator
            CalculationOutputWriter — запис у calc.* (БЕЗ змін)
```

Читачі результатів (`CalculatedCellOverlay`, `rpt.*`-в'юхи, панель результатів, ворота подання) **не змінюються**: вони читають `calc.CalculationResult` × `cfg.CalculationBinding`, а ці таблиці мають ту саму форму.

Новий проєкт `src/Ecr.Scripting` (посилається на `Ecr.Domain`, `Ecr.Expressions`); на нього посилаються `Ecr.Application`, `Ecr.Calculations`, `Ecr.Infrastructure`. Тести — `tests/Ecr.Scripting.Tests`.

## 3. Мова «ECR Script» v1 — специфікація

### 3.1 Приклад (ECW_C05_02, двигуни на бензині)

```csharp
// Які рядки рахуємо
rows from T3_00
    where row.Land_TypeFuel == entry("LAND_FUEL", "Gasoline")
       && row.Land_TypeEquipment in (entry("LAND_EQUIPMENT", "Compressor"), entry("LAND_EQUIPMENT", "Generator"))
    active when row.Land_Status == entry("LAND_GENERIC_EQUIPSTATUS", "Running");

// Коефіцієнти
const k1 = 5;
const k2 = 0.25;
const EF_gsec = {
    Summer: { NOx: 1.2, s330: 0.3, s337: 7.5, s2704: 0.1 },
    Winter: { NOx: 1.4, s330: 0.3, s337: 9.3, s2704: 0.1 },
};

// Розрахунок
var hours  = row.Land_RunningHoursMonthly;
var fuel   = row.Land_Measure_FuelSubstanceConsumptionOrPumpedVolume;
var season = period.Month >= 4 && period.Month <= 9 ? "Summer" : "Winter";
var idle   = hours == 0 && fuel == 0;
var nox_gs = k1 * k2 * EF_gsec[season].NOx / 3600;

output ECW_EC_gsec_301 : g_per_s = idle ? 0 : nox_gs * 0.8;
output ECW_EC_gsec_304 : g_per_s = idle ? 0 : nox_gs * 0.13;
output ECW_EC_tons_337 : t       = hours * k1 * k2 * EF_gsec[season].s337 / 1000000;
```

### 3.2 Лексика

- Кодування UTF-8; ідентифікатор `[A-Za-z_][A-Za-z0-9_]*`. Імена оголошень чутливі до регістру; два імені, що різняться лише регістром, — помилка `script.duplicateName`.
- Коментарі: `// …` до кінця рядка, `/* … */` (без вкладення).
- Число: `123`, `1.5`, `1e6`, `2.5E-3` → `decimal` (`decimal.Parse`, `NumberStyles.Float`, Invariant); поза межами `decimal` — `script.invalidNumber`. Унарний мінус — оператор.
- Рядок: `"…"` або `'…'`; екранування `\"`, `\'`, `\\`, `\n`. Незакритий — `script.unterminatedString`.
- Ключові слова (зарезервовані, не можуть бути іменами): `import rows from where active when const var visible function output per if else foreach substance in reject skip true false null row period header key entry`.
- Оператори: `+ - * / %`, `== != < <= > >=`, `&& || !`, `? :`, `( ) [ ] { } , ; : .`, `=` (лише в оголошеннях).
- Заборонено з порадою: `^` (`script.caretNotPower`: «степінь — Pow(a, b)»), одинарне `=` у виразі (`script.assignInExpression`: «порівняння — ==»), `&`, `|`.

### 3.3 Граматика (EBNF)

```
program     := importDecl* item*
importDecl  := "import" Ident ";"
item        := rowsClause | constDecl | funcDecl | statement
rowsClause  := "rows" "from" tableRef ("," tableRef)* ("where" matchExpr)? ("active" "when" matchExpr)? ";"
tableRef    := Ident ("." Ident)?                       // [КодШаблону.]КодТаблиці
matchExpr   := matchOr
matchOr     := matchAnd ("||" matchAnd)*
matchAnd    := matchNot ("&&" matchNot)*
matchNot    := "!" matchNot | "(" matchExpr ")" | matchAtom
matchAtom   := "row" "." Ident ("==" | "!=") matchValue
             | "row" "." Ident "in" "(" matchValue ("," matchValue)* ")"
matchValue  := String | "entry" "(" String "," String ")" | "null"

constDecl   := "const" Ident (":" Ident)? "=" constValue ";"       // : одиниця
constValue  := Number | "-" Number | String | "true" | "false" | mapLit
mapLit      := "{" (mapEntry ("," mapEntry)* ","?)? "}"
mapEntry    := (Ident | String | Number) ":" constValue
funcDecl    := "function" Ident "(" (Ident ("," Ident)*)? ")" "=" expr ";"

statement   := varDecl | outputDecl | rejectStmt | skipStmt | ifStmt | foreachStmt
varDecl     := "visible"? "var" Ident (":" Ident)? "=" expr ";"   // visible вимагає одиницю
outputDecl  := "output" Ident ("per" Ident)? ":" Ident "=" expr ";"
rejectStmt  := "reject" "when" expr ";"
skipStmt    := "skip" "when" expr ";"                              // лише в foreach substance
ifStmt      := "if" "(" expr ")" block ("else" (ifStmt | block))?
block       := "{" statement* "}"
foreachStmt := "foreach" "substance" Ident "in" "(" String ("," String)* ")" block

expr        := ternary
ternary     := or ("?" expr ":" expr)?
or          := and ("||" and)*
and         := equality ("&&" equality)*
equality    := relational (("==" | "!=") relational)*
relational  := additive (("<" | "<=" | ">" | ">=") additive | "in" "(" expr ("," expr)* ")")?
additive    := multiplicative (("+" | "-") multiplicative)*
multiplicative := unary (("*" | "/" | "%") unary)*
unary       := ("-" | "+" | "!") unary | postfix
postfix     := primary ( "[" expr ("," expr)* "]" | "." Ident )*
primary     := Number | String | "true" | "false" | "null" | "(" expr ")"
             | "row" "." Ident "?"?                    // поле рядка; «?» упритул — необов'язкове
             | "period" "." Ident
             | "header" "." Ident
             | "entry" "(" String "," String ")"
             | "key" "(" "row" "." Ident "?"? ")"
             | Ident "(" (expr ("," expr)*)? ")"        // функція (вбудована або своя)
             | Ident "." Ident "(" … ")"               // функція бібліотеки
             | Ident                                    // const / var / output / параметр / змінна foreach
```

`postfix` `.Ident` після імені константи-мапи — те саме, що `["Ident"]`; після імені імпорту — член бібліотеки; після змінної `foreach substance S` дозволено лише `S.code`.
Тернарний `?` після `row.X` пишеться з пробілом (`row.X ? a : b`); `row.X?` упритул — маркер необов'язкового поля.

### 3.4 Імена й області видимості

- Глобальна область: `const`, `function`, `var`, `output` верхнього рівня. Імена унікальні в межах усіх видів.
- `const` і `function` видимі всюди (підняті). `var`/`output` видимі **нижче** свого оголошення (інакше `script.forwardReference`).
- Блок (`if`, `else`, `foreach`) має власну область; ім'я в блоці не може збігатися з видимим зовнішнім (`script.duplicateName`). Змінна блоку поза блоком невидима.
- Присвоєння вдруге немає взагалі: кожна величина оголошується один раз.
- `output X` після оголошення читається як змінна `X`.
- Функція: параметри — локальні імена; тіло бачить `const`, інші функції, `row`, `period`, `header`, члени імпортів; **не** бачить `var`/`output`. Рекурсія (пряма й через інші функції) — `script.recursiveFunction`.
- Коди виходів унікальні без урахування регістру.

### 3.5 Значення й обчислення виразів

Значення — `ExpressionValue` (Null, Number, Text, Boolean, Date, Error). Помилки — значення (`#VALUE`, `#ARG`, `#REF`, `#DIV/0`, `#N/A`, `#NULL`), поширюються за правилами наявного обчислювача.

Кожен вираз **опускається** у вузли `Ecr.Expressions.Ast` і обчислюється `Evaluator.Evaluate(node, context, ExpressionDialect.Methodology, budget)`:

| Скрипт | Вузол AST |
|---|---|
| число/рядок/true/false/null | `LiteralNode` (число — `decimal`) |
| `a + b`, `- * / %` | `BinaryNode(Add/Subtract/Multiply/Divide/Modulo)` |
| `== != < <= > >=` | `BinaryNode(Equal/NotEqual/Less/LessOrEqual/Greater/GreaterOrEqual)` |
| `&&`, `\|\|` | `BinaryNode(And/Or)` |
| `!x`, `-x`, `+x` | `UnaryNode(Not/Negate/Plus)` |
| `c ? a : b` | `FunctionNode("if", [c, a, b])` — семантика `if` діалекту методологій |
| `x in (a, b)` | `FunctionNode("in", [x, a, b])` |
| вбудована `F(args)` | `FunctionNode("F", args)` (ім'я як у `DialectCatalog`) |
| `row.X` / `row.X?` | `SymbolReferenceNode(Argument, "X") { IsOptional }` |
| `key(row.X)` | `FunctionNode("$key", [LiteralNode("X")])` |
| `const`-скаляр, `var`, `output`, параметр, змінна foreach | `SymbolReferenceNode(Formula, ім'я)` |
| `M[k1][k2]`, `M[k1, k2]`, `M.name` | `FunctionNode("$lookup", [LiteralNode(ім'я мапи), k1, k2])` |
| своя `f(args)` | `FunctionNode("$call:f", args)` |
| `Lib.Name` | `SymbolReferenceNode(Formula, "Lib.Name")` |
| `Lib.f(args)` | `FunctionNode("$call:Lib.f", args)` |
| `period.Days\|Hours\|Seconds\|Start\|End\|Year` | `PeriodPropertyNode(властивість, 0)` |
| `period.Month` | `PeriodPropertyNode("Sequence", 0)` |
| `period.EndKey` | `FunctionNode("$periodEndKey", [])` → число `yyyymmdd` дати кінця періоду |
| `header.Name` | `SymbolReferenceNode(Header, "Name")` |
| `entry("REG","CODE")` | `FunctionNode("$entry", [LiteralNode("REG"), LiteralNode("CODE")])` → Number(id запису) |
| `S.code` | `SymbolReferenceNode(Formula, "S.code")` → Text(код речовини) |

Функції з іменем на `$` — «хостові»: їх обчислює не `MethodologyFunctions`, а контекст скрипта (розд. 4.2, зміна в `Evaluator`).

**Вбудовані функції** — рівно каталог `DialectCatalog` діалекту методологій (ті самі імена, чутливі до регістру, та сама кількість аргументів): `Abs Acos Asin Atan Ceiling Cos Exp Floor IEEERemainder Log Log10 Max Min Pow Round Sign Sin Sqrt Tan Truncate in if`, у `Strict` ще `Ln ifs CONVERT SUBSTANCE`, і довідникові `REGFIND REGONE REGFIELD REGSUM REGAVG REGMIN REGMAX REGCOUNT CURRENT_SUBSTANCE SUBSTANCE_APPLICABLE CONTAINS`. Дозволеність у режимі — `DialectCatalog.IsAllowedIn(name, numericMode)`; порушення — `script.functionNotAllowedInMode`. Невідома — `script.unknownFunction` (з порадою `DialectCatalog.Advice/CaseCorrection`), хибна кількість аргументів — `script.argumentCount`.

**Поле рядка.** `row.X` — значення аргументу `X` входу рядка (`CalculationInput.Arguments`, пошук без урахування регістру; сюди ж входять місячні псевдоніми й похідні факельні аргументи — їх дає `CalculationInputBuilder`). Перетворення в значення — як `GenericCalculationModule.ToValue(argument, entryRef, legacyNumericText)`:
`entryRef = NumericMode == Strict || скрипт потребує знімка довідників` (те саме правило, що `binding.Registries is not null` у старому модулі); `legacyNumericText = NumericMode == Legacy`.
Поля немає або воно порожнє: `row.X` → `#ARG` (як зараз), `row.X?` → `null`.
`key(row.X)` — «ключове» читання: `ToValue(argument, entryRef: true, legacyNumericText: false)` (так старий рушій читає аргументи правила категорії).

**Мапа.** `M[k]`: ключ порівнюється як текст (число → інваріантний текст без хвостових нулів; Text — як є; регістр важливий). Немає ключа, ключ `null`/помилка, або результат — не скаляр (вкладена мапа без подальшого індексу) → `#N/A`. Якщо всі ключі індексу — літерали, відсутність ключа — помилка компіляції `script.unknownMapKey`.

### 3.6 Вибір рядків

`rows from T1, T2 where W active when A;` — таких речень може бути кілька (набори таблиць не перетинаються; інакше `script.duplicateTable`). Методологія виду `Library` не має `rows` і `output` (`script.rowsInLibrary`, `script.outputInLibrary`); решта — мусить мати хоч одне `rows` (`script.missingRows`).

Для екземпляра таблиці, код якої названо в реченні:

- «Текст збігу» колонки рядка — `MethodologyRuleMatcher.Text(cell.Value)` (ValueString ?? ValueNumeric ?? ValueRegistryEntryId ?? ValueBool), ключ — **код колонки** (не Id): зріз читається так само, як у `MethodologyResolver.MatchRowsDetailedAsync` (з дефолтами колонок), але мапа рядка будується за `ColumnDef.Code` зі знімка метаданих версії шаблону екземпляра.
- `row.X == v` — порядкова рівність тексту збігу з `v`; `entry("REG","CODE")` → текст Id запису; `null` — «тексту немає». Колонки в таблиці немає → тексту немає. `!=` — заперечення `==`. `in` — будь-який збіг.
- Спершу `active when`: рядки, де воно **хибне**, — «неактивні» (`RowMatchOutcome.ExcludedRowKeys`), у розрахунок не йдуть. Речення без `active when` нікого не виключає.
- Потім `where`: істинне — рядок рахується (`Matches`, код правила — код таблиці). Без `where` — усі активні рядки.
- `AllRowKeys` — усі рядки екземпляра в порядку, як у старому методі.

Екземпляр таблиці, коду якої в скрипті немає: `RowMatchOutcome([], null)` (як «версія без правил»).

### 3.7 Виконання на рядок

Вхід: `CalculationBindingContext` + `CalculationInput`. Оператори верхнього рівня виконуються **згори вниз**:

1. `var N = e;` — обчислити; якщо режим `Legacy` і результат `NaN/±∞` — замінити на 0 і записати `trace.Masked` (точно як `GenericCalculationModule.Evaluate`); записати крок трейсу; зберегти значення. `visible var` після цього емітує проміжний результат (`CalculationResultKind.Intermediate`, лише якщо число).
2. `output N [per S] : u = e;` — як `var`, потім емісія виходу тим самим кодом, що `GenericCalculationModule.Emit` (маскування, шкала колонки `binding.OutputScales`, `NumericPolicy.RoundOutput`, `trace.OutputFailed` для нечисла). `per S` — із `SubstanceEntryId` поточної речовини; дозволено лише всередині `foreach substance S` (`script.outputOutsideSubstanceLoop`), а `output` без `per` усередині `foreach substance` — помилка `script.outputNeedsPer`.
3. `reject when c;` — якщо `c` не `Boolean false` (тобто `true`, `null`, помилка, не булеве) — рядок **відхилено**: кинути той самий `DomainException`, що й правило категорії (`ECR-CALC-0422`, `messageKey = err.ECR-CALC-0422.categoryRuleFailed`, `expression` — текст умови, `reason` — `"reject"` або код помилки) — оркестратор ловить його через `GenericCalculationModule.IsCategoryRuleRowFailure` і пише проблему `CategoryRuleRejected`.
4. `if (c) A else B` — `Boolean true` → `A`; `Boolean false` → `B` (якщо є); інакше жодна гілка не виконується, а **кожен** `output`, статично оголошений в обох гілках, отримує `trace.OutputFailed(код, помилка)` (`#VALUE` для не-булевого, код помилки — для помилки, `#NULL` — для null).
5. `foreach substance S in ("0301", "0304") { … }` — для кожного коду по порядку: Id запису речовини (розв'язаний у `PrepareAsync`); `trace.EnterSubstance(id)`; нова область; `CurrentSubstanceCode = код`; виконати блок. Вкладений `foreach substance` — `script.nestedSubstanceLoop`. `skip when c;` (лише на верхньому рівні цього блоку): `Boolean true` → `trace.Step("SubstanceNotApplicable", код, null)` і перехід до наступної речовини.
6. Після останнього оператора `trace.EnterSubstance(null)`.

Підсумок рядка — `CalculationOutput` тієї ж форми, що в старого модуля:
`MissingArguments` — якщо жодного значення не емітовано і скрипт має хоч один `output`: перелік **обов'язкових** полів (`row.X` без `?`, включно з полями в задіяних членах бібліотек), яких у вході немає або вони порожні (логіка — як `MissingArgumentsOf` :683); інакше `null`.
`StepError` — `trace.FirstFailure` + `trace.FailedStepCount`, як у старому модулі.

Бюджет: один `EvaluationBudget(Evaluator.MaxEvaluationSteps)` на **оператор**; глибина викликів своїх функцій ≤ 32 (`#VALUE` при перевищенні).

### 3.8 Бібліотеки

`import Common;` — код методології виду `Library` з рушієм `Script`. Версія бібліотеки — чинна на дату кінця періоду (наявний `IMethodologyStore.ResolveImportsAsync`; рядки `calc.MethodologyImport` матеріалізуються зі скрипта). Члени бібліотеки: `const`, `function`, `var` верхнього рівня. `Common.X` обчислюється **ліниво** в контексті рядка, що імпортує (його `row`, `period`, поточна речовина), з константами бібліотеки; результат запам'ятовується в межах поточної області (рядок або ітерація речовини). Кожен `var` бібліотеки при обчисленні проходить те саме маскування `Legacy` і пише крок трейсу з `source = (код бібліотеки, Id її версії)`. Бібліотека може імпортувати бібліотеку; цикл — `script.importCycle`; глибина ≤ 8.

### 3.9 Діагностики компіляції

Кожна: `code`, `severity` (`error`), `line`, `column`, `length`, `message` (англ.), `messageKey = "script.<code>"`, `params`. Перелік кодів: `unexpectedToken`, `expected`, `unterminatedString`, `unterminatedComment`, `invalidNumber`, `caretNotPower`, `assignInExpression`, `duplicateName`, `unknownName`, `forwardReference`, `unknownFunction`, `argumentCount`, `functionNotAllowedInMode`, `recursiveFunction`, `outputOutsideSubstanceLoop`, `outputNeedsPer`, `nestedSubstanceLoop`, `skipOutsideSubstanceLoop`, `rowsInLibrary`, `outputInLibrary`, `missingRows`, `duplicateTable`, `mapNotScalar`, `indexOnNonMap`, `unknownMapKey`, `duplicateMapKey`, `visibleNeedsUnit`, `unknownImport`, `unknownImportMember`, `importCycle`, `tooLarge` (текст > 200 000 символів), `tooDeep` (вкладеність виразу > 64).

Перевірки оточення (потребують БД; розд. 5.2) дають діагностики з кодами `unknownUnit`, `unknownSubstance`, `unknownTable`, `ambiguousTable`, `unknownColumn`, `unknownRegistryEntry`, `noBoundOutput`, `libraryNotScript`.

## 4. Рушій

### 4.1 Модель даних (EF-міграція `MS01_MethodologyScript`)

`calc.MethodologyVersion` + три колонки:

| Колонка | Тип | Зміст |
|---|---|---|
| `Engine` | `tinyint NOT NULL DEFAULT 0` | `MethodologyEngine`: `Formulas = 0`, `Script = 1` |
| `ScriptText` | `nvarchar(max) NULL` | текст скрипта; може бути заповнений і при `Engine = Formulas` («тіньовий» скрипт під час перенесення) |
| `ScriptHash` | `varbinary(32) NULL` | SHA-256 від UTF-8 тексту (без BOM, переводи рядків нормалізовано до `\n`) |

Домен (`MethodologyVersion`): властивості `Engine`, `ScriptText`, `ScriptHash`; методи
`SetScript(string text, byte[] hash)` (лише чернетка **або** `Engine == Formulas` — тіньовий запис; порожній текст заборонено),
`SwitchEngine(MethodologyEngine engine)` (на `Script` — лише якщо `ScriptText` не порожній);
конструктор отримує необов'язковий `MethodologyEngine engine = Formulas`; `CloneAsDraft` копіює всі три поля.
`MethodologyDescriptor` + останній позиційний параметр `MethodologyEngine Engine = MethodologyEngine.Formulas` (заповнює `MethodologyResolver` :126).
`CalculationBindingContext` + `Ecr.Scripting.CompiledScript? Script = null` і `IReadOnlyDictionary<string, long>? SubstanceIds = null` (код речовини → Id запису).

Зміни схеми — лише додавання; upgrade-перевірка обов'язкова (база з `origin/main` → `-Upgrade` ×2 → smoke).

### 4.2 Гачок у `Evaluator` (єдина зміна в `Ecr.Expressions`)

Новий інтерфейс `src/Ecr.Expressions/Evaluation/IHostFunctionContext.cs`:

```csharp
public interface IHostFunctionContext : IEvaluationContext
{
    /// Обчислює хостову функцію (ім'я починається з '$'); false — функції немає.
    bool TryInvokeHost(string name, IReadOnlyList<ExpressionValue> args, EvaluationBudget budget, out ExpressionValue result);
}
```

У `Evaluator.MethodologyCall` (:894), **після** гілок `if`/`ifs` і після обчислення аргументів, перед `MethodologyFunctions.Invoke`:
якщо `node.Name` починається з `$` → `context is IHostFunctionContext host && host.TryInvokeHost(node.Name, args, budget, out var v) ? v : ExpressionValue.Error(ExpressionErrors.BadValue)`.
Парсер наявних діалектів імен на `$` не породжує (лексер не знає символу) — поведінка наявних виразів не змінюється. Тест: наявні тести `Ecr.Expressions.Tests` зелені + два нові (хостова функція викликається; без хоста — `#VALUE`).

### 4.3 `Ecr.Scripting` — публічний API

```csharp
namespace Ecr.Scripting;

public sealed record ScriptDiagnostic(string Code, int Line, int Column, int Length, string Message,
    IReadOnlyDictionary<string, string> Params);      // MessageKey => "script." + Code

public sealed record ScriptCompileOptions(
    NumericMode NumericMode,
    bool IsLibrary,
    Func<string, CompiledScript?>? ResolveImport = null);   // код методології → скомпільована бібліотека

public static class ScriptCompiler
{
    public const int MaxTextLength = 200_000;
    public static ScriptCompilation Compile(string text, ScriptCompileOptions options);
    public static byte[] Hash(string text);
}

public sealed record ScriptCompilation(CompiledScript? Script, IReadOnlyList<ScriptDiagnostic> Diagnostics)
{
    public bool IsSuccess => Script is not null && Diagnostics.Count == 0;
}

public sealed class CompiledScript
{
    public IReadOnlyList<string> Imports { get; }                       // коди бібліотек
    public IReadOnlyList<RowsClause> Rows { get; }                      // таблиці + where + active
    public IReadOnlyList<OutputInfo> Outputs { get; }                   // Code, UnitCode, IsPerSubstance, Ordinal (порядок у тексті з 1)
    public IReadOnlyList<VisibleInfo> Visibles { get; }                 // Code, UnitCode
    public IReadOnlyList<string> Substances { get; }                    // коди речовин усіх foreach, у порядку появи, без дублів
    public IReadOnlyList<RowFieldInfo> RowFields { get; }               // Name, IsOptional (поле обов'язкове, якщо хоч одне звернення без «?»)
    public IReadOnlyList<string> MatchColumns { get; }                  // колонки з where/active when
    public IReadOnlyList<RegistryEntryRef> RegistryEntries { get; }     // (RegistryCode, EntryCode) з entry(...)
    public IReadOnlyList<string> RegistryCodes { get; }                 // довідники з REG*-функцій (перший аргумент-літерал) і entry()
    public bool NeedsRegistries { get; }                                // є entry() / REG* / SUBSTANCE_APPLICABLE / CURRENT_SUBSTANCE
    public IReadOnlyList<UnitUse> Units { get; }                        // усі згадані коди одиниць із позиціями
    public IReadOnlyList<MemberInfo> Members { get; }                   // const/function/var верхнього рівня (для імпортерів і підказок)
    // Виконувана форма:
    public IReadOnlyList<ScriptStatement> Statements { get; }
    public IReadOnlyDictionary<string, ScriptConst> Constants { get; }
    public IReadOnlyDictionary<string, ScriptFunction> Functions { get; }
}
```

Вузли операторів (`ScriptStatement`): `VarStatement(Name, UnitCode?, IsVisible, AstNode Expr, string SourceText, int Line)`, `OutputStatement(Code, UnitCode, SubstanceVar?, AstNode Expr, SourceText, Line)`, `RejectStatement(AstNode Cond, SourceText)`, `SkipStatement(AstNode Cond)`, `IfStatement(AstNode Cond, Block Then, Block? Else, IReadOnlyList<string> OutputCodes)`, `ForeachSubstanceStatement(VarName, IReadOnlyList<string> Codes, Block Body)`.
`ScriptConst` — скаляр (`ExpressionValue`) або мапа (`IReadOnlyDictionary<string, ScriptConst>`), з необов'язковим кодом одиниці. `ScriptFunction(Name, Parameters, AstNode Body)`.
`RowsClause(IReadOnlyList<TableRef> Tables, MatchNode? Where, MatchNode? Active)`; `MatchNode` — `MatchAnd/MatchOr/MatchNot/MatchEquals(Column, MatchValue, Negated)/MatchIn(Column, Values)`; `MatchValue` — `Text(string)` | `Entry(RegistryCode, EntryCode)` | `Null`.
`MatchEvaluator.Evaluate(MatchNode, Func<string, string?> columnText, Func<string, string, string?> entryIdText)` — чиста функція в `Ecr.Scripting`.

`ScriptFormatter.FormatConst(ScriptConst)` — канонічний друк значення константи (для редактора таблиць і конвертера): мапа — по рядку на ключ, вкладена мапа з рівними наборами ключів — в один рядок із вирівнюванням.

### 4.4 `ScriptCalculationModule` (`src/Ecr.Calculations/ScriptCalculationModule.cs`)

- `Code = "script"`, `Level = CalculationLevel.Configuration`, `CanHandle(d) => d.Level == Configuration && d.Engine == MethodologyEngine.Script`. `GenericCalculationModule.CanHandle` додатково вимагає `Engine == Formulas`.
- **Спільний код.** Статичні частини старого модуля виносяться без зміни поведінки у `src/Ecr.Calculations/CalculationSteps.cs` (internal static): `EmitValue(...)` (тіло `Emit`), `FinishStep(...)` (частина `Evaluate` після обчислення: помилка → `trace.Failed`; маскування `Legacy`; `trace.Step`), `ToValue(...)`, `PeriodAsync(...)`. Старий модуль кличе їх же (рефакторинг; усі наявні тести `Ecr.Calculations.Tests` мусять лишитися зеленими без правок очікувань).
- `PrepareAsync(methodology, documentId, periodKey, registries, ct)`:
  1. текст і хеш версії (`IMethodologyStore.GetScriptAsync`); компіляція через `ScriptProgramCache` (singleton, ключ `(versionId, hex(hash))`, місткість 256; при переповненні очищається);
  2. імпорти: `ResolveImportsAsync(versionId, onDate)` → для кожної бібліотеки її скрипт (`GetScriptAsync(libraryVersionId)`), рекурсивно; бібліотека без версії або без скрипта → `DomainException ECR-CALC-0422`, `messageKey err.ECR-CALC-0422.scriptLibraryUnavailable`;
  3. `Outputs` — `GetOutputsAsync` (матеріалізовані), `OutputScales` — `ICalculationBindingStore.ListOutputScalesAsync` (як старий модуль), `Period` — як старий, `Substances` — `GetSubstancesAsync`, `SubstanceIds` — обернена мапа `GetSubstanceCodesAsync`;
  4. `Registries` — якщо `script.NeedsRegistries` (або бібліотеки): той самий шлях, що `GenericCalculationModule.RegistriesAsync` (:236) з кодами `script.RegistryCodes`; Id записів `entry()` розв'язуються зі знімка (код запису → Id); нерозв'язаний запис → значення `#REF` у виразі і «немає тексту» у збігу;
  5. повертає `CalculationBindingContext` з `Formulas = []`, порожнім словником `Constants`, `Script = compiled`.
- `ExecuteAsync(binding, input, ct)` — розд. 3.7. Контекст обчислення — `ScriptEvaluationContext : IHostFunctionContext, IBudgetedEvaluationContext`:
  `GetArgument(name)` → значення поля (необов'язковість — за `CompiledScript.RowFields`), `GetFormulaResult(name)` → параметр функції → змінна області → скалярна константа → `S.code` → член бібліотеки (`Lib.Name`); `GetConstant` → `#REF`; `GetHeader` — як `MethodologyEvaluationContext.GetHeader` (:192); `Registries`, `CurrentSubstanceCode`, `Period`, `Convert` — як у старому контексті (`UnitTable`).
  `TryInvokeHost`: `$lookup`, `$call:<f>`, `$key`, `$entry`, `$periodEndKey`.
- Трейс: `TraceRecorder` так само, як старий модуль; деталі кроку (`TraceDetail`) — перелік імен, які читає вираз, з їх значеннями й одиницями (форма JSON та сама, щоб наявний перегляд трейсу працював). Крок = `var`/`output`; код кроку = ім'я.
- `ExecuteDetailedAsync(binding, input, ct)` повертає `(CalculationOutput Output, IReadOnlyList<ScriptValue> Values)`, де `ScriptValue(Name, SubstanceCode?, Kind: Var|Output|Visible, ValueType, Text, ErrorCode?)` — для живого перегляду.
- DI (`DependencyInjection.cs`): `ScriptCalculationModule` реєструється **перед** `GenericCalculationModule` (остання реєстрація лишається Generic); `ScriptProgramCache` — singleton; `ScriptRowSelector` — scoped.

### 4.5 Вибір рядків і оркестратор

`src/Ecr.Calculations/ScriptRowSelector.cs`: `Task<RowMatchOutcome> MatchAsync(CompiledScript script, long tableInstanceId, Func<string,string,string?> entryIdText, ct)` — розд. 3.6. Читання зрізу, дефолтів і Id рядків — тим самим кодом, що `MethodologyResolver.MatchRowsDetailedAsync` (спільна частина виноситься в метод `ReadTableAsync`, який повертає `instance`, `rowIds`, `slice`); `Read = new TableSliceRead(...)` заповнюється так само, щоб `CalculationInputBuilder` не читав зріз удруге.

`CalculationOrchestrator.ExecuteAsync` (:~409): якщо `binding.Descriptor.Engine == Script` — **спершу** `module.PrepareAsync`, потім `ScriptRowSelector.MatchAsync` (потрібні скрипт і Id записів із підготовки); інакше — як зараз. Решта методу спільна.

### 4.6 Публікація, симуляція, тести

`PublishMethodologyHandler` і `SimulateMethodologyHandler` отримують `IEnumerable<ICalculationModule>` і беруть модуль за `CanHandle(descriptor)` (замість одиничного). Для `Engine == Script` публікація **не** виконує формульні кроки (`CheckRulesAsync`, `ApplyEvaluationOrderAsync`, перевірки формул/констант/правила категорії), а виконує `ScriptPublishChecks` (розд. 5.2) і матеріалізацію; спільне лишається: дата чинності, заборона заднього числа для `Strict`, еталонний набір (через модуль скрипта), різниця результатів на тестах, аудит, `PublishVersion`.
Тести (`calc.TestCase`) — без змін формату: вхід — аргументи рядка, очікування — `вихід` або `вихід@IdРечовини`.

## 5. Авторинг

### 5.1 Матеріалізація (`ScriptMaterializer`, `src/Ecr.Application/Calculations/Scripting/`)

Викликається при успішній компіляції **чернетки** з рушієм `Script` (збереження) і при публікації. В одній транзакції зі збереженням тексту:

| Таблиця | Правило |
|---|---|
| `calc.MethodologyOutput` | повна заміна: рядок на кожен `output` (Code, UnitId за кодом одиниці, Ordinal, IsPerSubstance) |
| `calc.MethodologySubstance` | повна заміна: рядок на кожен код із `CompiledScript.Substances` (Id запису довідника речовин, Ordinal = порядок) |
| `calc.MethodologyImport` | повна заміна за `CompiledScript.Imports` |
| `cfg.CalculationBinding` | **лише додавання** (при публікації): для кожної «живої» таблиці з `rows from` і кожного виходу `O` — колонки з `DataType = Calculated`, де `Code == O` (без регістру) або (`IsMonthColumn` і `Code == O + "_M" + MM`); якщо прив'язки (колонка, методологія, вихід) немає — створити активну з `MatchJson = "{}"`. Наявні прив'язки не змінюються й не видаляються |
| `cfg.RegistryUse` | при публікації — як зараз (`ReplaceRegistryUsesAsync`) з `CompiledScript.RegistryCodes` |

«Жива» таблиця — таблиця з цим кодом в останній опублікованій версії кожного шаблону (`FindLiveTablesByCodeAsync`, розд. 5.3). Старі дочірні записи (формули, константи, правила…) версії з рушієм `Script` не читаються і не змінюються.

Заборона ручного редагування: `PUT/DELETE` формул, констант, категорій, правила категорії, правил, речовин, виходів для версії з `Engine == Script` → `409 ECR-CALC-0409`, `messageKey err.ECR-CALC-0409.scriptManaged`. Тести, обов'язкові колонки, режими, прив'язки — дозволені.

### 5.2 Перевірки оточення (`ScriptEnvironmentChecker`) і публікації (`ScriptPublishChecks`)

`ScriptEnvironmentChecker.CheckAsync(compiled, methodology, ct)` → діагностики (позиція — місце згадки в тексті):

1. `unknownUnit` — код одиниці відсутній у `IUnitCatalog`.
2. `unknownSubstance` — код речовини відсутній у довіднику речовин (те саме джерело, що `GetSubstanceCodesAsync`).
3. `unknownTable` / `ambiguousTable` — таблиці з `rows from` немає в жодній живій версії шаблону / є в кількох шаблонах і не вказано `Шаблон.Таблиця`.
4. `unknownColumn` — колонки з `where`/`active when` або обов'язкового `row.X` немає в живій таблиці (для `row.X` дозволені також місячні псевдоніми таблиці й похідні імена з `DerivedArgumentNames`; логіка — як перевірка `ECR-CALC-0438` у `MethodologyPublishChecks`).
5. `unknownRegistryEntry` — `entry(REG, CODE)` не знаходиться.
6. `libraryNotScript` — імпортована методологія не має чинної версії з рушієм `Script`.

`ScriptPublishChecks` (блокують публікацію, `ECR-CALC-0422`, список `PublishProblem`): помилки компіляції; усі діагностики оточення; `noBoundOutput` — не-бібліотека, у якої після автоприв'язки жоден вихід не має активної прив'язки («методологія нічого не запише»); порожній еталонний набір — як зараз.

### 5.3 Сховище (`IMethodologyStore`, реалізація в Infrastructure)

```csharp
Task<MethodologyScript?> GetScriptAsync(int methodologyVersionId, CancellationToken ct);   // (Text, Hash, Engine, IsLibrary, NumericMode)
Task ReplaceScriptOutputsAsync(int versionId, IReadOnlyList<ScriptOutputRow> rows, CancellationToken ct);
Task ReplaceScriptSubstancesAsync(int versionId, IReadOnlyList<long> substanceEntryIds, CancellationToken ct);
Task ReplaceScriptImportsAsync(int versionId, IReadOnlyList<int> importedMethodologyIds, CancellationToken ct);
Task<IReadOnlyDictionary<string, long>> FindSubstanceEntryIdsAsync(IReadOnlyCollection<string> codes, CancellationToken ct);
Task<IReadOnlyList<LiveTable>> FindLiveTablesByCodeAsync(string tableCode, string? templateCode, CancellationToken ct);
// LiveTable(TemplateId, TemplateCode, TemplateVersionId, TableDefId, TableCode, Columns: [Id, Code, DataType, IsMonthColumn, MonthNumber, UnitId], MonthAliases: [string])
```

### 5.4 API (контролер `MethodologyScriptController`, префікс `api/v1/methodologies/{id}/versions/{vid}`)

| Метод | Право | Тіло → відповідь |
|---|---|---|
| `GET …/script` | `Calculation.View` | → `MethodologyScriptDto { engine, text, hash (hex), isDraft, diagnostics[], outline }` |
| `PUT …/script` | `Calculation.EditFormula` | `{ text, expectedHash? }` → той самий DTO. Лише чернетка з `Engine = Script` (інакше `409 scriptNotEditable`); `expectedHash` не збігається → `409 ECR-CALC-0409.scriptChanged`. Текст зберігається **завжди**; матеріалізація — лише коли діагностик немає |
| `POST …/script/validate` | `Calculation.View` | `{ text }` → `{ diagnostics[], outline }` (нічого не зберігає) |
| `POST …/script/preview` | `Calculation.View` + доступ до документа | `{ text?, documentId, periodKey, tableInstanceId?, maxRows (≤ 200, типово 50) }` → `ScriptPreviewDto` |
| `POST api/v1/methodologies/{id}/versions` | як зараз | додається поле `engine: "Formulas" \| "Script"` (типово `Script` для нової порожньої; при копіюванні — як у джерела) |

Нова порожня версія з рушієм `Script` отримує стартовий текст-заготовку (закоментований приклад: `rows from`, `const`, `var`, `output`) — константа `ScriptStarter.Text` в `Ecr.Application`. Порівняння версій (`CompareMethodologyVersionsHandler`, `MethodologyDiffFields`) отримує поле `scriptText` (сторож `MethodologyDiffFieldCatalogTests`: рядок сіду + гілка в `DiffFieldLabel.tsx`).

`outline`: `{ tables[], inputs[{name, optional}], outputs[{code, unit, perSubstance}], substances[], imports[], constants[{name, kind: scalar|map, line}] }`.
`ScriptPreviewDto`: `{ tables: [{ tableInstanceId, tableCode, totalRows, selectedRows, inactiveRows, rows: [{ rowKey, rowNumber, state: "selected"|"inactive"|"notSelected"|"rejected", issue?: "missingInputs"|"stepError"|"rejected", missing?: string[], values: [{ name, substance?, kind, text, error? }] }] }], diagnostics[] }`. Перегляд нічого не пише; `text` порожній → збережений текст версії. Рядки `notSelected`/`inactive` повертаються без `values`.

Діагностика в DTO: `{ code, messageKey, params, line, column, length, message }`.

Після зміни контролерів: `contracts/openapi.snapshot.json` (тест оновлення знімка в `Ecr.Api.Tests` — знайти за назвою `OpenApiSnapshot`) і `npm run api:types` — в одному коміті.

### 5.5 Рядки інтерфейсу

Усі нові ключі (`script.*`, `methodologyScript.*`, `err.ECR-CALC-0409.script*`, `err.ECR-CALC-0422.script*`) — у новому файлі `src/Ecr.Infrastructure/Persistence/Sql/seed/features/MS-methodology-script.sql` трьома мовами (`en`, `ru`, `kz`) за зразком наявного малого файлу (напр. `B54-no-expectations.sql`). Сторожі `SeedTranslationTests`, `EndpointCoverageTests`, `MessageKeyRatchetTests`, `ErrorTitleCatalogTests` мають лишитися зеленими.

## 6. Клієнт

Нова сторінка `src/Ecr.Web/src/pages/admin/MethodologyScriptPage.tsx`, маршрут `/admin/methodologies/:id/versions/:vid/script` (реєстрація — за зразком `adminMethodologyVersions` у `src/app/router.tsx` і файлі маршрутів). Функціональний код — `src/Ecr.Web/src/features/methodologyScript/`:

| Файл | Зміст |
|---|---|
| `api.ts` | типізовані виклики 5.4 |
| `scriptLanguage.ts` | мова Monaco `ecr-script`: токенізатор (ключові слова 3.2, числа, рядки, коментарі), дужки, автозакриття |
| `scriptCompletion.ts` | підказки: ключові слова; після `row.` — колонки живих таблиць (`outline.tables` + наявний API структури версії шаблону); імена `const/var/function` з `outline`; функції з `expressionMetadata` (діалект Methodology); заготовки: `rows from`, `const {…}`, `output`, `foreach substance`, `if/else` |
| `scriptMarkers.ts` | діагностики → маркери Monaco (рядок/колонка/довжина) |
| `ScriptEditor.tsx` | Monaco (динамічний імпорт через наявний `features/expressions/monaco.ts`), перевірка з затримкою 500 мс через `validate`, `aria-label` обов'язковий |
| `ScriptPreviewPanel.tsx` | вибір документа й періоду, кнопка «Порахувати», таблиця рядків: стан, проблема, значення (колонки — імена `output`, розгортання рядка — усі `var`) |
| `ScriptOutlinePanel.tsx` | «Що читає / що пише»: входи, виходи з позначкою «немає колонки» (дані — наявний `MethodologyCoveragePanel`) |
| `ScriptTestsPanel.tsx` | тести структуровано: таблиця «поле → значення» і «вихід → очікування, допуск»; серіалізація у наявний `inputJson`/`expectedJson`; кнопка «Створити тест із рядка перегляду» |
| `ConstTableDialog.tsx` | (етап 6) редагування константи-мапи сіткою, вставка з Excel |

Розкладка сторінки: зліва редактор (на всю висоту), справа вкладки «Перегляд», «Входи й виходи», «Тести», «Обов'язкові колонки» (наявна панель), «Проблеми». Угорі: назва й версія, статус, «Зберегти» (Ctrl+S), «Опублікувати» (наявний діалог публікації), «Порівняти з версією…» (Monaco diff двох текстів).
`MethodologyVersionsPage`: для версії з `engine = Script` кнопка «Відкрити» веде на сторінку скрипта, а секції старого конфігуратора не показуються; діалог «Нова версія» має вибір рушія (типово «Скрипт»).
Тести клієнта: чисті модулі (`scriptLanguage`, `scriptCompletion`, `scriptMarkers`, серіалізація тестів) — vitest; e2e `src/Ecr.Web/e2e/methodologyScript.spec.ts`: створити методологію й версію-скрипт через API → відкрити сторінку → ввести текст → побачити діагностику → виправити → зберегти → перегляд на документі показує число → опублікувати → перерахунок документа дає значення в колонці.

## 7. Що бачить еколог (приймальний сценарій)

1. «Нова методологія» → код, назва. 2. «Нова версія» → відкривається редактор із заготовкою. 3. Пише `rows from …`, константи, `output …`; підказки пропонують колонки; помилки підкреслено з поясненням. 4. Справа обирає документ і місяць → бачить відібрані рядки й числа. 5. «Створити тест із рядка» → тест готовий. 6. «Опублікувати» → причина, дата. Жодного JSON, жодних Id.

## 8. Перенесення наявних методологій (інструмент + Claude)

### 8.1 Інструмент `tools/Ecr.MethodologyScriptMigration` (консоль, .NET)

Працює з **копією** бази прода (рядок з'єднання параметром; ніколи не з live). Піднімає DI Infrastructure + Calculations. Команди:

| Команда | Дія |
|---|---|
| `convert --out <тека> [--methodology <код>]` | для кожної версії рівня `Configuration` з `Engine = Formulas` генерує `<Код>/<Версія>.ecrs` і `convert-report.json` (що не вдалося й чому) |
| `shadow --in <тека>` | записує тексти як тіньові (`SetScript`) у копію бази |
| `parity [--methodology <код>] [--document <id>] --out <звіт>` | порівнює старий модуль і модуль скрипта на всіх документах і періодах копії (розд. 8.4); код виходу ≠ 0 при розбіжностях |
| `pack --in <тека> --out package.json` | пакет: для кожної версії — код методології, номер версії, дата чинності, текст, **відбиток джерела** (SHA-256 канонічного подання старих сутностей версії) |

Артефакти з коефіцієнтами прода пишуться **поза репозиторієм** (`.sync-local\methodology-migration\`).

### 8.2 Правила конвертера (детерміновані)

| Старе | Скрипт |
|---|---|
| `Methodology.Kind = Library` | скрипт без `rows`/`output`; формули → `var`, константи → `const` |
| `MethodologyImport` | `import <Код>;`; `!Name`, якого немає серед власних формул, → `<Бібліотека>.Name` |
| Прив'язки (`cfg.CalculationBinding`) → коди таблиць | `rows from <таблиці>`; самі прив'язки не змінюються |
| `MethodologyRule` (активні, за пріоритетом) | `where (правило1) \|\| (правило2) …`; кожне правило — `&&` рівностей; ключ `ColumnDefId` → код колонки; значення: колонка-Lookup і значення — Id запису → `entry("REG","CODE")`, інакше рядковий літерал. Порожнє правило `{}` → без `where` |
| `Methodology.RowActivityFilter` | `active when row.<статус-колонка> == entry(REG, ENTRY)` (усі статус-колонки таблиці через `&&`); таблиця без статус-колонок — окреме речення `rows` без `active when` |
| `MethodologyFormula` (у `EvaluationOrder`) | `var Code = <вираз>;` або `output Code : unit = <вираз>;`, якщо код є у `MethodologyOutput`; `IsVisible` і не вихід → `visible var Code : unit = …`. `Scope = Row` — на верхньому рівні до `foreach`; `Scope = Substance` — у `foreach substance` (якщо версія має речовини) |
| Вираз | `@X`→`row.X`, `@X?`→`row.X?`, `!F`→`F`, `CST.C`→вираз константи (8.3), `and/or/not`→`&&/\|\|/!`, `=`→`==`, `<>`→`!=`, `if(c,a,b)` лишається функцією, `[Period].P`→`period.P` (`Sequence`→`Month`), `HDR.N`→`header.N`; функції — ті самі імена |
| `CategoryRule` | `var Category = <вираз, де @X → key(row.X)>;` + `reject when Category == null \|\| Category == "" [\|\| !(Category in (<відомі категорії>))];`; якщо правило містить `SUBSTANCE_APPLICABLE` — у `foreach substance`: `var SubstanceCategory = …; skip when SubstanceCategory == null \|\| SubstanceCategory == "";` |
| `MethodologySubstance` | `foreach substance S in ("код1", …) { … }`; виходи з `IsPerSubstance` → `output Code per S …` |
| `MethodologyConstant` | розд. 8.3 |
| `MethodologyTestCase`, `MethodologyRequiredInput`, режими | не змінюються |

Чого конвертер не вміє — пише у звіт і **не** генерує файл (Claude розбирає вручну або розширює конвертер). Версії рівня `Module` (HSE400) — етап 7.

### 8.3 Константи

Для кожної константи `C`, яку читає версія, конвертер **обчислює справжнім `ConstantResolver.Resolve`** значення для кожної комбінації: категорія ∈ (категорії версії ∪ «без категорії»), речовина ∈ (речовини версії ∪ «без речовини»), дата ∈ межі вікон чинності констант у межах життя версії. Далі спрощує:

- однакове для всіх комбінацій → `const C = 5;`
- залежить лише від категорії → `const C = { Summer: 1.2, Winter: 1.4 };`, у виразі `C[Category]`
- від категорії й речовини → `const C = { Summer: { "0301": …, "0304": … }, … };`, у виразі `C[Category][S.code]`
- лише від речовини → `C[S.code]`
- залежить від дати → `var C = period.EndKey >= 20260101 ? C_from_2026 : C_from_2024;` (значення — окремі константи)
- для комбінації резолвер дає «немає» / «неоднозначно» → ключ у мапу не кладеться (читання дасть `#N/A`)
- текстові константи → рядкові `const`.

Одиниця константи → `const C : unit = …`.

### 8.4 Паритет (доказ збігу)

`ParityComparer` (`src/Ecr.Application/Calculations/Scripting/` + виконання в `Ecr.Calculations`): для версії з тіньовим скриптом, документа, періоду й екземпляра таблиці:

1. Рядки: старий `MatchRowsDetailedAsync` проти `ScriptRowSelector` — рівні множини відібраних, виключених і всіх ключів.
2. Для кожного відібраного рядка — однаковий `CalculationInput` в обидва модулі; порівнюється: множина `(OutputCode, SubstanceEntryId, Kind)`; `Value` — **точна** рівність `decimal`; `UnitId`; наявність `MissingArguments` і їх множина; наявність `StepError`; відхилення рядка.
3. Розбіжність — запис `(методологія, версія, документ, період, таблиця, рядок, що саме, старе, нове)`.

Поріг переходу — **0 розбіжностей** на всіх документах і періодах копії, де версія чинна. Послаблювати порівняння заборонено; розбіжність виправляється в конвертері, рушії або тексті.

### 8.5 Перемикання на проді (за окремим словом людини, у вікні)

API (право `Calculation.Publish`, аудит, обов'язкова причина):
`POST api/v1/methodologies/script-migration/apply?dryRun=` — тіло — пакет; для кожної версії звіряє відбиток джерела з базою (не збігся — відмова для цієї версії), записує тіньовий текст;
`POST api/v1/methodologies/script-migration/switch` — `{ versionIds[], engine, reason }` — перемикає рушій групи версій однією транзакцією (на `Script`: текст компілюється без помилок, матеріалізація дає **ті самі** виходи/речовини/імпорти, що вже є, — інакше відмова; бібліотека й усі її імпортери — в одній групі);
`POST …/versions/{vid}/script/parity` — `{ documentId, periodKey }` → розбіжності (вибіркова перевірка на живих даних, нічого не пише).
Відкат — `switch` назад на `Formulas` (старі сутності не видаляються до етапу 9).

### 8.6 Роль Claude (порядок сесії перенесення)

1. Людина дає копію бази прода (відновлену як окрему локальну базу). 2. `-Upgrade` до поточної схеми. 3. `convert` → читання `convert-report.json`; кожен невдалий випадок: розширити конвертер (PR) або написати текст вручну. 4. `shadow`, `parity` → розбір кожної розбіжності до нуля. 5. Перегляд текстів на читабельність (згортання повторів у мапи, осмислені імена) — **кожна** правка тексту знову проходить `parity`. 6. `pack`. 7. У вікні: `apply?dryRun=true` → `apply` → вибірковий `parity` на 3–5 документах → `switch` → перерахунок одного документа і звірка результатів «до/після» → звіт людині.

## 9. Етапи й задачі

Порядок суворий. Кожна задача — окремий коміт(и) у `lane/integ-ms-script`; після кожної: `dotnet build Ecr.sln` зелений, тести зачеплених проєктів і `tests/Ecr.Architecture.Tests` зелені (сторож, що почервонів, виправляється за його ж повідомленням, а не вимикається).

### Етап 1 — мова (`Ecr.Scripting`), без БД

**T1.1 Проєкт і лексер.** Створити `src/Ecr.Scripting/Ecr.Scripting.csproj` (за зразком `Ecr.Expressions.csproj` + посилання на `Ecr.Expressions`), `tests/Ecr.Scripting.Tests` (за зразком `tests/Ecr.Expressions.Tests`), додати обидва в `Ecr.sln`, у правила шарів (`tests/Ecr.Architecture.Tests/LayerRulesTests.cs`) і в переліки тестових проєктів CI, якщо їх перелічено явно (знайти за назвою `Ecr.Expressions.Tests` у `tools/` і `.github/workflows/`). `Lexing/ScriptLexer.cs`: лексеми 3.2 з рядком/колонкою/довжиною; помилки — діагностики, не винятки.
Тести: кожен вид лексеми; коментарі; числа `1e6`, `.5` (помилка), `1.` (помилка); рядки з екрануванням; `^`, `&`, `|` → діагностики.

**T1.2 Парсер.** `Parsing/ScriptParser.cs` — рекурсивний спуск за 3.3 у синтаксичне дерево (`Syntax/*.cs`); відновлення після помилки — до наступної `;` або `}`. Обмеження `tooLarge`, `tooDeep`.
Тести: приклад 3.1 розбирається; кожна конструкція граматики; `row.X?` проти `row.X ? a : b`; типові помилки дають очікуваний код і позицію.

**T1.3 Зв'язування й опускання.** `Binding/ScriptBinder.cs`: області 3.4, усі діагностики 3.9 (крім оточення), опускання виразів у `AstNode` за таблицею 3.5, метадані `CompiledScript` (4.3). `ScriptCompiler.Compile`, `ScriptCompiler.Hash`, `MatchEvaluator`, `ScriptFormatter.FormatConst`.
Тести: по тесту на кожен код діагностики; опущений AST для 10 виразів (через `AstPrinter`); метадані прикладу 3.1; `MatchEvaluator` — таблиця істинності; імпорт бібліотеки (члени видимі, цикл — помилка).

### Етап 2 — рушій

**T2.1 Гачок `Evaluator`** (4.2) + тести.
**T2.2 Модель і міграція** (4.1): домен, конфігурація EF (`CalculationsConfiguration.cs`), міграція `MS01_MethodologyScript` (`dotnet ef migrations add` за `docs/build/09-commands.md`), `MethodologyDescriptor.Engine`, `MethodologyResolver`, клон (`MethodologyDraftStore`, сторож `MethodologyCloneCompletenessTests`), методи сховища 5.3. Тести домену: `SetScript`/`SwitchEngine`/`CloneAsDraft`; тест сховища на SQL-фікстурі.
**T2.3 Спільні кроки** — винесення `CalculationSteps` зі старого модуля без зміни поведінки (4.4). DoD: `tests/Ecr.Calculations.Tests` зелені без правок очікувань.
**T2.4 `ScriptCalculationModule` + `ScriptEvaluationContext` + `ScriptProgramCache`** (3.7, 3.8, 4.4). Тести (`tests/Ecr.Calculations.Tests/Scripting/`), із фальшивими сховищами за зразком наявних тестів `GenericCalculationModule`:
 арифметика `Legacy`/`Strict` дає ті самі числа, що еквівалентні формули старого модуля (5 пар «версія з формулами ↔ скрипт», порівняння `CalculationOutput.Values`); маскування `1/0` у `Legacy`; мапа — знайдено / `#N/A`; своя функція; `if/else` (три стани умови); `reject when`; `foreach substance` з `per S`, `skip when`, `CURRENT_SUBSTANCE()`; `row.X` відсутній → `MissingArguments`; `row.X?` → null; помилка у виході → `StepError`; бібліотека.
**T2.5 `ScriptRowSelector` + оркестратор + DI + вибір модуля в публікації/симуляції** (4.5, 4.6). Тести: вибір рядків; інтеграційний тест на SQL-фікстурі: документ із таблицею, версія-скрипт, прогін оркестратора → рядки в `calc.CalculationResult`.

### Етап 3 — авторинг (API)

**T3.1** `ScriptMaterializer`, `ScriptEnvironmentChecker`, `ScriptPublishChecks`, сховище (5.1–5.3) + тести.
**T3.2** Обробники й контролер 5.4, поле `engine` у створенні версії, відмова `scriptManaged`, публікація версії-скрипта; рядки 5.5; знімок OpenAPI і `schema.d.ts`. Тести `Ecr.Api.Tests`: повний шлях «методологія → версія-скрипт → PUT з помилкою → PUT правильного → тест → публікація → перерахунок → значення»; права; `expectedHash`; `preview` не пише в `calc.*`.

### Етап 4 — клієнт

**T4.1** Мова Monaco, редактор, сторінка, маршрут, збереження/діагностики + vitest.
**T4.2** Панелі «Перегляд», «Входи й виходи», «Проблеми»; інтеграція у сторінку версій і діалог нової версії + vitest.
**T4.3** Панель тестів, «тест із рядка перегляду», порівняння версій + e2e `methodologyScript.spec.ts`.

### Етап 5 — перенесення

**T5.1** `ParityComparer` + ендпоінти 8.5 + тести.
**T5.2** Інструмент 8.1 і конвертер 8.2–8.3. Приймання: (а) методології з `docs/delivery/reference-data/land-template/af-land-package.json`, налаштовані як у `tools/land/Build-LandTemplate.ps1`, конвертуються повністю; (б) на тестовому документі Land `parity` дає 0 розбіжностей; (в) очікування наявних `LandGoldenFormulaTests` виконуються на модулі скрипта з конвертованим текстом.

### Етап 6 — зручності мови й редактора

T6.1 редактор константи-мапи сіткою (вставка з Excel). T6.2 `foreach X in (301, 304) { … name_{X} … }` — розгортання під час компіляції. T6.3 читання інших рядків/таблиць документа (`Max(T10_03.Col where …)`) і місячні колонки `row.X[month]` — прибирання колонок-містків. T6.4 перевірка одиниць (перенесення `MethodologyUnitChecks`). T6.5 посібник користувача й розділ у `TESTER-GUIDE.md`.

### Етап 7 — HSE400 (рівень `Module`)

`MonthColumnArgumentsModule` обирає внутрішній модуль за `Engine`; для скрипта: `binding.Formulas` — тимчасові описи кроків зі скрипта, залежності кроків — із `CompiledScript` (нове поле `StepDependencies` у `CalculationBindingContext` замість регулярного виразу), аргументи — `row.GCV_<простір>_<таблиця>_<рядок>`; модуль скрипта емітує виходи за `binding.Outputs`, які передає зовнішній модуль. Паритет — на документі HSE400.

### Етап 8 — перенесення прода (розд. 8.6) — лише за словом людини.

### Етап 9 — прибирання (після повного перенесення, за словом людини)

Видалити старі екрани, ендпоінти формул/констант/правил/категорій, `GenericCalculationModule`, сутності й таблиці (EF-міграція з видаленням), імпорт пакета AF.

## 10. Що не змінюється (заборони для виконавців)

- `calc.CalculationResult`, `calc.CalculationRun`, `calc.CalculationIssue`, `rpt.*`, `CalculatedCellOverlay`, `CalculationInputBuilder`, `CalculationOutputWriter`, `RecalculationJob` — без змін.
- Поведінка версій з `Engine = Formulas` — без жодних змін (усі наявні тести зелені без правок очікувань).
- Арифметика й функції — лише через `Evaluator`; власних реалізацій операторів немає.
- Ніколи: force push, `--no-verify`, `git add -A`, вимкнення/послаблення тестів, тестові періоди не `2099xx`.

## 11. Ризики

| Ризик | Захист |
|---|---|
| Числа скрипта відрізняються від старих | той самий `Evaluator`; спільні `CalculationSteps`; паритет з точною рівністю перед перемиканням |
| Скрипт «нічого не рахує» мовчки | `missingRows`, `noBoundOutput`, `unknownColumn` блокують публікацію; живий перегляд показує відібрані рядки |
| Еколог не впорається з кодом | заготовки, підказки колонок, помилки з порадами, живий перегляд, тест із рядка, редактор таблиць |
| Змішаний стан (частина версій на скрипті) | рушій — властивість версії; бібліотека й імпортери перемикаються разом |
| Конфлікт EF-міграцій з іншими сесіями | одна міграція `MS01`; перед злиттям — перегенерація знімка поверх `origin/main` |

## 12. Самоперевірка плану (18.11.2026)

Перевірено за кодом і враховано:

1. **Збіг чисел.** Літерали й константи скрипта — `decimal`, як константи й літерали старого рушія; у `Legacy` обидва шляхи переходять у `double` тим самим `AsDouble()`. Аргументи рядка йдуть тим самим `CalculationInputBuilder` і `ToValue`. Арифметика, порівняння, `if`, `in`, функції — ті самі вузли й той самий `Evaluator`. Єдине джерело можливих розбіжностей — порядок кроків і маскування; його закриває спільний `CalculationSteps` і паритет.
2. **Гачок `$`.** `Evaluator.Function` спершу питає `RegistryForms.Handles` (імена на `$` йому невідомі), далі `MethodologyCall` — гачок стоїть там. Наявні парсери символ `$` не пропускають.
3. **Прив'язки.** Вони належать методології й копіюються разом із версією шаблону — тому скрипт їх не описує, а публікація лише додає відсутні за угодою «код колонки = код виходу».
4. **Паралельність виконавців.** Етап 1 (`Ecr.Scripting`) не перетинається з T2.1–T2.3 — їх можна вести двома виконавцями в окремих worktree і злити. T2.4+ — послідовно.
5. **Чого v1 свідомо не дає** (етап 6): читання інших рядків/таблиць і місячних колонок напряму (поки працюють колонки-містки шаблону й місячні псевдоніми), перевірка одиниць у виразах, редактор таблиць коефіцієнтів, `foreach` для імен. Це не блокує перенесення: наявні методології цих можливостей не мають.
6. **Ризик обсягу.** Найбільші задачі — T2.4 (модуль) і T5.2 (конвертер). Обидві мають вимірюване приймання (пари «формули ↔ скрипт», паритет 0 розбіжностей).

## 13. Повний обсяг v1 (рішення людини 18.11: «роби зараз повноцінний варіант без боргів»)

Усе, що в розд. 9 було «етапом 6», входить у першу версію. Цей розділ **доповнює** розд. 3–6 і має ту саму силу. Пункт 5 розд. 12 скасовано.

### 13.1 Інші таблиці й рядки того самого документа

**Оголошення джерела.** На верхньому рівні, поруч із `rows`:

```
tableDecl := "table" Ident "=" tableRef ";"          // псевдонім = [КодШаблону.]КодТаблиці
```

`table` — ключове слово. Псевдонім — ім'я глобальної області. Таблиця може бути й тією самою, з якої беруться рядки (`table Self = T3_00;`).

**Звернення.** `Alias.Column` і `Alias.Column[m]` (місячна колонка, 13.2) дозволені **лише всередині агрегату** над цим псевдонімом (інакше `script.tableColumnOutsideAggregate`).

**Агрегати** (імена чутливі до регістру; це не функції каталогу, а конструкції мови):

```
aggregate := ("Sum" | "Min" | "Max" | "Avg" | "First") "(" expr ("where" expr)? ")"
           | ("Count" | "Any") "(" Ident ("where" expr)? ")"          // Ident — псевдонім таблиці
```

У `Sum/Min/Max/Avg/First` вираз-значення мусить звертатися рівно до **одного** псевдоніма таблиці (`script.aggregateNeedsTable`, якщо жодного; `script.aggregateManyTables`, якщо кілька). Усередині значення й умови: `Alias.Col` — комірка рядка-кандидата; `row.X` — поточний рядок; доступні `const`, `var`, функції, `period`. Вкладений агрегат над **іншим** псевдонімом дозволений; над тим самим — `script.nestedAggregateSameTable`. Агрегат не можна вживати у `where`/`active when` речення `rows` і в тілі `function`.

**Які рядки.** Усі рядки екземплярів таблиці з цим кодом у **тому самому документі й періоді прогону** (ті самі, що дає `IRowStore.GetTableInstancesAsync(documentId, periodKey)`): якщо поточний рядок належить копії аркуша (`SheetCopyId`), беруться екземпляри з тим самим `SheetCopyId`; якщо таких немає — екземпляри оригіналу. Значення комірки кандидата — аргумент його рядка, побудований тим самим `CalculationInputBuilder` (отже, з місячними псевдонімами й колонками-формулами), перетворений тим самим `ToValue`. Колонки `Calculated` (результати методологій) рядок не містить — методологія читає **входи**, а не результати інших методологій; спільний розрахунок — через `import`.

**Семантика.** Кандидати перебираються в порядку рядків таблиці. Умова: `Boolean true` — рядок бере участь; `false` або `null` — ні; помилка — результат агрегату ця помилка. Значення `null` пропускається; помилка значення — результат агрегату ця помилка.
`Sum` порожнього — `0`; `Min/Max/Avg/First` порожнього — `null`; `Count` — кількість рядків, що пройшли умову; `Any` — `Boolean`. Додавання й порівняння — через `IEvaluationArithmetic` режиму версії (`Binary(Add)`, `CompareNumbers`), ділення в `Avg` — `Binary(Divide)` на кількість; нечислове значення в `Sum/Avg` — `#VALUE`; `Min/Max` для тексту — порядкове порівняння.

**Опускання.** Опис агрегату кладеться в `CompiledScript.Aggregates[i] = (Kind, TableAlias, AstNode? Value, AstNode? Where)`, у виразі лишається `FunctionNode("$agg", [LiteralNode(i)])`; `Alias.Col` → `FunctionNode("$cell", [LiteralNode(alias), LiteralNode(col)])`; `Alias.Col[m]` → `FunctionNode("$cellMonth", [LiteralNode(alias), LiteralNode(col), m])`. Хост під час `$agg` ставить «поточний рядок псевдоніма» і обчислює `Value`/`Where` тим самим `Evaluator` зі спільним бюджетом.

**Метадані.** `CompiledScript.Tables` (псевдонім → `TableRef`), `CompiledScript.ReadTables` (коди таблиць усіх `table`), `CompiledScript.TableColumns` (псевдонім → колонки, що читаються).

**Завантаження.** `ScriptCalculationModule.PrepareAsync` для кожного псевдоніма читає екземпляри й **усі** їх рядки один раз на прив'язку (зріз + `CalculationInputBuilder.BuildAsync` для всіх ключів рядків), групує за `SheetCopyId`. Сумарна кількість комірок, прочитаних для всіх псевдонімів, не більша за `CalculationLimits.MaxInputCellsPerBinding`, інакше `ECR-CALC-4222` (той самий код, що й для входу).

**Перерахунок і застарілість** (щоб правка в чужій таблиці не лишала старі числа):
- нова таблиця `calc.MethodologyReadTable (Id, MethodologyVersionId, TableCode nvarchar(128))`, унікальність `(MethodologyVersionId, TableCode)`; матеріалізується зі `CompiledScript.ReadTables` разом з виходами (розд. 5.1); входить у клон версії;
- `RecalculationJob.BindingsAsync`: до `touchedByPeriod` додаються методології, чиї опубліковані версії читають код таблиці будь-якого **активного** екземпляра періоду (зайвий перерахунок безпечний, пропущений — ні);
- позначка «результати застаріли»: знайти, де правка комірки робить результати методології застарілими (`StaleResultsMapCache`, `GetCalculationFreshnessAsync`, ворота подання `SubmitSheetHandler` `staleMethodologyResults`), і додати те саме для методологій, що **читають** змінену таблицю. Тест: правка в таблиці-джерелі робить результат методології-читача застарілим і перерахунок аркуша-джерела його оновлює;
- перевірка оточення: `unknownTable`/`ambiguousTable`/`unknownColumn` діють і для `table` та `Alias.Col`.

Приклад (значення GTU з рядка потрібного місяця іншої таблиці):

```csharp
table Gtu = T10_03;
var no2 = Max(Gtu.Land_Measure_GTU1_301 where Gtu.Land_GTU_MonthNo == period.Month);
```

### 13.2 Місячні колонки

`row.X[m]` і `Alias.X[m]`, де `m` — вираз (1…12): значення колонки місяця `m` для входу `X`. Розв'язання колонки, у такому порядку: (1) псевдонім `cfg.MonthInputAlias` таблиці з `Alias == X` і `MonthNumber == m`; (2) колонка таблиці з `IsMonthColumn`, `MonthNumber == m` і кодом `X + "_M" + mm` (дві цифри). Не знайдено або `m` не ціле 1…12 → `#ARG`. `row.X?[m]` — необов'язкове (порожнє → `null`).
Опускання: `FunctionNode("$month", [LiteralNode("X"), m, LiteralNode(optional)])`. Карту «(вхід, місяць) → код колонки» для таблиці екземпляра модуль бере зі знімка метаданих версії шаблону (`IMetadataCache`, `MonthInputAliasesFor`) і кешує на прив'язку.
`CompiledScript.RowFields` отримує ознаку `IsMonthly`; перевірка оточення `unknownColumn` для місячного входу вимагає, щоб хоч для одного місяця колонка розв'язувалась.

Так колонки-містки шаблону («вибір X_M01..X_M12 за місяцем») стають непотрібними: `row.Land_RunningHours[period.Month]`.

### 13.3 `foreach` для повторів (розгортання під час компіляції)

```
foreachStmt := … | "foreach" Ident "in" "(" literal ("," literal)* ")" block
```

Літерал — число або рядок. Блок **розгортається** на рівні лексем до розбору: для кожного значення копія лексем блоку, де (а) в ідентифікаторі фрагмент `{X}` замінюється текстом значення (результат мусить бути ідентифікатором, інакше `script.foreachNameNotIdentifier`), (б) окрема лексема-ідентифікатор `X` замінюється літералом. Лексер приймає `{Ident}` усередині ідентифікатора. Розгорнуті оператори стають операторами **зовнішньої** області (без нової області), тому оголошені в них `var`/`output` видимі далі, а імена мусять бути унікальні. Позиції діагностик вказують на вихідний текст. Дозволено на верхньому рівні, у блоках і всередині `foreach substance`; вкладення — до 3 рівнів; сумарно не більше 2000 розгорнутих операторів (`script.foreachTooLarge`).

```csharp
foreach S in (301, 304, 330) {
    output ECW_EC_gsec_{S} : g_per_s = idle ? 0 : base * EF["s{S}"];
}
```
(у рядковому літералі `{X}` теж підставляється.)

### 13.4 Перевірка одиниць

При збереженні й публікації кожен вираз `var`/`output` перевіряється тим самим `Ecr.Expressions.Binding.UnitChecker`, що й формули (`MethodologyUnitChecks`): одиниця `row.X` — одиниця колонки живої таблиці; `const` і `var` — оголошена (`: unit`) або виведена; `output` — оголошена. Умови й код відмови — ті самі, що в наявній публікації (`ECR-TMPL-4223`); у редакторі — діагностика `script.unitMismatch` з позицією оператора. Виконавець спершу читає `MethodologyUnitChecks.cs` і `UnitChecker.cs` і відтворює рівно наявну строгість (не суворіше й не м'якше); тести — дзеркало наявних тестів перевірки одиниць методологій.

### 13.5 Редактор таблиць коефіцієнтів

`ConstTableDialog.tsx`: курсор на `const`-мапі → дія «Редагувати як таблицю»: сітка (рядки — ключі першого рівня, колонки — ключі другого), додавання/видалення рядків і колонок, вставка з Excel (TSV), перевірка чисел; «Застосувати» замінює літерал у тексті канонічним форматом (`ScriptFormatter` — клієнтська копія правил форматування з тестом на збіг із серверним через `POST …/script/format-const`, або формат робить сервер цим ендпоінтом). Однорівнева мапа — сітка з одним стовпцем значень.

### 13.6 Документація

`docs/user/methodology-script.md` (посібник еколога: приклади від простого до агрегатів і бібліотек, усі конструкції, типові помилки), розділ у `docs/build/TESTER-GUIDE.md` (сценарії перевірки), оновлення `docs/tz/05-engines.md` §5.2 (рівень 2 — власна мова скриптів) і рішення `D-NEW` у `docs/tz/10-decisions.md` + `docs/CHECKSUMS.txt`.

### 13.7 Додаткові коди діагностик

`tableColumnOutsideAggregate`, `aggregateNeedsTable`, `aggregateManyTables`, `nestedAggregateSameTable`, `aggregateNotAllowedHere`, `unknownTableAlias`, `foreachNameNotIdentifier`, `foreachTooLarge`, `unitMismatch`.

### 13.8 Зміни в задачах

- T1.x: + `table`, агрегати, `row.X[m]`, `foreach` для повторів, нові діагностики й метадані (13.1–13.3, 13.7).
- T2.2: + таблиця `calc.MethodologyReadTable` у тій самій міграції `MS01`, клон, сховище (`ReplaceScriptReadTablesAsync`, `GetMethodologyIdsReadingTablesAsync(tableCodes)`).
- T2.4: + `$agg`, `$cell`, `$cellMonth`, `$month`, завантаження таблиць у `PrepareAsync`, ліміт комірок. Тести: кожен агрегат (порожній, з `null`, з помилкою, з умовою), копія аркуша, місячна колонка за псевдонімом і за кодом.
- T2.5: + `RecalculationJob.BindingsAsync` і застарілість (13.1) з інтеграційним тестом.
- T3.1: + матеріалізація `ReadTable`, перевірка одиниць (13.4), ендпоінт `format-const`.
- T4.x: + підказки після `Alias.`, заготовки агрегатів, `ConstTableDialog` (13.5).
- Етап 5 (перенесення) і етап 7 (HSE400) — після цього, без змін.
- T6.5 (документація) — обов'язкова частина v1.

### 13.9 Читання результатів інших методологій (рішення людини 18.11: «як додаткова можливість»)

**Синтаксис — без нових конструкцій.** Результат іншої методології читається як звичайна колонка: `row.Code` (поточний рядок) або `Alias.Code` в агрегаті (13.1), де `Code` — колонка шаблону з `DataType = Calculated`, прив'язана (`cfg.CalculationBinding`) до виходу **іншої** методології. Місячна форма `row.Code[m]` працює так само (13.2). У компіляторі змін немає; усе — у рушії й перевірках.

**Значення.** Те саме, що користувач бачить у сітці: правило `CalculatedCellOverlay` (вихід прив'язки, рядок за `RowKey`, предикат прив'язки, сума по речовинах, одиниця виходу), але з результатів **поточного прогону**, а не попереднього. Спільне ядро зіставлення виноситься з `CalculatedCellOverlay` у чисту функцію, якою користуються і накладення сітки, і рушій (без зміни поведінки накладення; його тести зелені без правок). Результату для рядка немає → `row.Code` дає `#ARG`, `row.Code?` → `null`.

**Порядок розрахунку.** Оркестратор (`CalculationOrchestrator.RunAsync`) будує залежності вузлів `CalculationNode(versionId, dependencies)` (зараз порожні): для кожної версії-скрипта прогону — методології, до яких прив'язані `Calculated`-колонки, що їх читає скрипт (власна таблиця й таблиці псевдонімів), крім самої себе; залежність — версія цієї методології в тому самому прогоні (якщо вона в прогоні є). `CalculationPlan.Build` уже розкладає вузли на послідовні пачки. Залежна версія рахується в пізнішій пачці й у `PrepareAsync` читає результати прогону (`ICalculationResultStore` — новий метод читання за `CalculationRunId`, екземплярами таблиць і кодами виходів). Ідентифікатор прогону модуль отримує через scoped-об'єкт `ScriptRunContext { long? CalculationRunId }`, який оркестратор заповнює перед `PrepareAsync`; поза прогоном (перегляд, тест, симуляція) читаються результати **актуального** прогону документа й періоду.
Якщо методологія-джерело в цьому прогоні не рахується (її аркуш затверджено й результати перенесено, або прогін частковий) — читаються її актуальні/перенесені результати.

**Заборони й перевірки.**
- Читання власного виходу (колонка прив'язана до цієї ж методології) — діагностика оточення `script.readsOwnOutput`.
- Цикл між методологіями (A читає B, B читає A, напряму чи через ланцюг) — `script.resultCycle`, блокує публікацію; граф будується з прив'язок і з матеріалізованих залежностей опублікованих версій. Під час прогону цикл (з'явився через пізнішу прив'язку) — `DomainException ECR-CALC-0422`, `messageKey err.ECR-CALC-0422.scriptResultCycle`, прогін документа не виконується частково.
- Колонка `Calculated` без активної прив'язки — `script.unknownColumn` (читати нема чого).

**Матеріалізація й перерахунок.** При публікації залежності «методологія → методології-джерела» записуються наявним `IMethodologyStore.ReplaceDependenciesAsync` (`calc.MethodologyDependency`). `RecalculationJob.BindingsAsync` замикає `touchedByPeriod` транзитивно: якщо перераховується методологія-джерело, перераховуються й усі, хто читає її результати (за `calc.MethodologyDependency`). Застарілість: результат методології-читача застаріває разом із результатом джерела (той самий механізм, що в 13.1).

**Метадані компілятора** не змінюються: рушій сам визначає, які з `RowFields` і `TableColumns` є `Calculated`-колонками, за знімком метаданих версії шаблону.

**Задачі.** T2.4: злиття результатів у вхід рядка й у рядки псевдонімів; `ScriptRunContext`. T2.5: залежності вузлів, читання результатів прогону, замикання в `BindingsAsync`, інтеграційний тест «B читає результат A в тому самому прогоні; правка входу A оновлює B». T3.1: діагностики `readsOwnOutput`, `resultCycle`, матеріалізація залежностей. Паритет старих методологій це не зачіпає (вони результатів не читають).

Приклад:

```csharp
rows from T5_00;
// сумарний викид NOx з таблиці джерел, порахований методологією ECW_C05_02
table Sources = T3_00;
var noxTotal = Sum(Sources.ECW_EC_tons_NOx where Sources.Land_Unit == row.Land_Unit);
output Unit_NOx_Share : one = noxTotal == 0 ? 0 : row.ECW_EC_tons_NOx / noxTotal;
```
