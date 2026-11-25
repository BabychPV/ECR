# DESIGN: правило категорії константи (CategoryRule, L-2)

> Файл створено заново: попереднього `DESIGN-category-rule.md` у `H:\ECR\.sync-local` не було.
> Тут лише розділ про порожні значення; решту дизайну (lane/analiz/l2-category-rule-2) додає власник.

## Порожні значення у виразі правила (NULL+текст)

Стан на `744eadae` (lane/analiz/expr-text-plus): `'a' + 'b'` = `'ab'`, але `NULL + 'a'` = Null
(як `NULL + 1`). Для CategoryRule Null/порожнє = `categoryRuleFailed` (ECR-CALC-0422, відмова рядка) — це
дефолт, і він лишається. Нижче — як у Land-демо зробити обробку порожнього явною.

### 1. Що є в діалекті Methodology (перевірено за кодом)

Каталог `DialectCatalog` (ярус Core + Extension), регістр значущий:

| Засіб | Є? | Примітка |
|---|---|---|
| `if(cond, then, else)` | так (лише малими літерами) | рівно 3 аргументи, гілки ліниві; повертає й текст |
| літерал `NULL` | так (Lexer: AND/OR/NOT/TRUE/FALSE/NULL, регістронезалежно) | |
| `= NULL`, `<> NULL` | так | `null = null` TRUE, `null = 'x'` FALSE (02b §6.2); єдині порівняння, що не поглинають Null |
| `''` (порожній рядок) | так | окреме значення, не Null; перевіряти окремо: `!X = ''` |
| `and` / `or` / `not` | так | операнди мають бути булевими; `!X = NULL` завжди булеве, тож безпечно |
| `ifs(...)` | Extension | не для Legacy-версій (ECR-CALC-0433) |
| `COALESCE`, `IFNULL`, `ISNULL`, `ISBLANK`, `IFERROR` | **НІ** | невідомі функції; `Advice("COALESCE")` прямо каже «nothing: null does not occur at run time» |
| `&` (конкатенація) | **НІ** | у Methodology `&` побітове, парсер відхиляє (DialectSyntax.AmpersandIsConcat=false) |
| `+` на двох текстах | так (744eadae) | `NULL + 'a'` = Null; `'a' + 1` = `#VALUE` |

Чи виникає Null у рантаймі Methodology? Документація (docs-text-plus, 02b §6.2a) і `Advice` стверджують,
що ні: відсутній `@Arg` — `ARGUMENT_MISSING`, нечислова константа — `CONSTANT_NOT_NUMERIC`, обидва ловляться при
публікації. Але це про числові формули. Для правила категорії, що читає `!Row`-формули й `@`-аргументи
з документа, порожня комірка/незаповнена колонка таки віддає `ExpressionValue.Null` (тест
`TextPlusConcatenationTests` будує саме `FormulaResults[...] = Null`), тож захист доречний і працює
(доведено тестами нижче). Висновок: «Null не буває» не слід вважати гарантією для CategoryRule.

### 2. Готовий вираз для формули 5.1

`ECW_Location = !ECW_RepairStatus + '_' + !ECW_Category`

Варіант A (порожнє -> порожній сегмент, ключ `_Cat1`, `Repair_`):

```
if(!ECW_RepairStatus = NULL, '', !ECW_RepairStatus) + '_' + if(!ECW_Category = NULL, '', !ECW_Category)
```

Варіант B (і Null, і `''` -> `NA`, ключ `NA_Cat1`, `Repair_NA`, `NA_NA`):

```
if(!ECW_RepairStatus = NULL or !ECW_RepairStatus = '', 'NA', !ECW_RepairStatus) + '_' + if(!ECW_Category = NULL or !ECW_Category = '', 'NA', !ECW_Category)
```

Якщо порожнє має відмовляти рядок (дефолт) — лишити простий `!ECW_RepairStatus + '_' + !ECW_Category`: Null -> Null -> `categoryRuleFailed`.

Доказ (парситься, статично типізується як Text, обчислюється на Null/''/значеннях):
`tests/Ecr.Expressions.Tests/Evaluation/TextPlusNullHandlingTests.cs`, коміт `aeb2a7ca`
у `lane/analiz/docs-category-null-handling` (база — `lane/analiz/expr-text-plus` 744eadae;
`dotnet test --filter TextPlus` 27/27). Там само перевірено, що `COALESCE`, `IFNULL`, `ISBLANK`, `&` — не розбираються.

### ⛔ Знайдена проблема: `categoryRuleNotText` відхилить і простий, і захищений вираз

`MethodologyCategoryRuleChecks` бракує правило, якщо `result.Expression.ResultType` ∈ {Number, Boolean, Date}.
`ResultType` дає груба `Parser.InferShape`, а не `TypeChecker`. Виміряно: для `!A + '_' + !B`, обох варіантів 5.1,
`'x_' + if(!A = NULL, '', !A)` — **Number** (бо `!X` і `if(...)` мають форму Null, а `Add` дає Text лише коли
ОБИДВІ сторони вже Text). `TypeChecker` при цьому дає Text без діагностик. Наслідок: правило категорії з `+` над
посиланнями буде відхилене при публікації як «повертає не текст», хоч обчислюється правильно.

Рішення (не реалізовано, поза межами цього завдання):
- A. `Parser.InferShape`: `Add` -> Text, якщо одна сторона Text, а друга Text або Null (невідомий тип); Text+не-Text і так помилка.
  Мінімально, 3 рядки в `Parser.cs` + тест.
- B. Для `MethodologyCategoryRuleChecks` брати тип із `TypeChecker`, а не з `ResultType` (точніше, але тягне binding-контекст).
- Обхід без коду: обернути вираз у `if(TRUE, <вираз>, '')` — форма Null, перевірка не спрацює (потворно, для демо ок).

### 3. Якщо засобу замало — мінімальне розширення (без реалізації)

Засіб є (`if` + `= NULL`), але багатослівний: на кожен операнд — повтор виразу. Якщо Land-демо має багато таких правил:

- A. Функція `IFNULL(x, fallback)` (Extension, 2 аргументи, повертає `fallback`, якщо `x` Null; ярус Extension, тож
  не для Legacy-версій; одна гілка в `MethodologyFunctions.Invoke` ДО `Propagate`, як зараз `in`/`CONVERT`;
  запис у `ExtensionSet`; сигнатура з типом результату «тип першого аргументу»; рядок у `Replacements` для `COALESCE` -> `IFNULL`).
  5.1 тоді: `IFNULL(!ECW_RepairStatus, '') + '_' + IFNULL(!ECW_Category, '')`. Плюс: коротко, читається; мінус: нова функція в каталозі
  (ключі помилок, 02b §8, тест-сторож каталогу).
- B. Функція `ISBLANK(x)` (булева; Null або `''` -> TRUE), без fallback: `if(ISBLANK(!X), 'NA', !X)`. Плюс: ловить `''` теж;
  мінус: усе одно повтор операнда.
- Без розширення (рекомендовано для Land-демо): варіанти A/B розділу 2 + виправлення `InferShape` (A у блоці проблеми) —
  без нових функцій у діалекті.
