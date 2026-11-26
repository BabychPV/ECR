# DESIGN: хвіст L-2 після регресії Land RC12 (L2-1, L2-3, L2-4, L2-5, L2-7) - 2026-10-08

Лише читання коду й документів. Нічого не змінено, нічого не запушено. Джерело дефектів: `.sync-local\reports\LAND-RC12-2026-10-08.md` §6.

**Основа коду.** Читав `F:\ecr-wt\combo` (3d4c1c22 = L-2 lane + expr-text-plus). Усі серверні файли, на які посилаюсь (`GenericCalculationModule`, `PublishMethodologyHandler`, `CalculationInputBuilder`, `MaskedZero`, `MethodologyCategoryRuleChecks`, `MethodologyDraftHandlers`, `MethodologiesController`, `Evaluator`, `StrictDecimalArithmetic`) побайтно однакові з RC12 (c4e345ca, `git diff --stat` по цих шляхах порожній); відрізняються лише `MethodologyPackagePlanner`, `ImportMethodologyPackageHandler`, `MethodologyPackageDtos` (кілька рядків) - номери рядків там орієнтовні. Номери рядків нижче - за combo.

Суміжні лінії, які НЕ в цьому документі, але перетинаються з ним: L2-2 (`lane/analiz/fix-publish-text-plus`, 51b3ae54, лише `MethodologyPublishChecks.cs`), L2-6 (`lane/analiz/fix-category-rule-row-rejection`, 2fb0a199: `CalculationPlan.cs`, `CalculationOrchestrator.cs`, `GenericCalculationModule.cs` +16, `RecalculationJob.cs`, `09-seed.sql`).

---

## L2-1 (P1): область формули Row не задається ні через API, ні через імпорт

### 1. Корінь - ПІДТВЕРДЖЕНО
- `MethodologyFormula.Scope` за замовчуванням `Substance` (`src/Ecr.Domain/Entities/Calculations/MethodologyFormula.cs:82`), сеттер `SetScope` - `:101`.
- Єдині виклики `SetScope(MethodologyFormulaScope)` у `src`: `MethodologyDraftStore.cs:334` (клон чернетки з джерела). `SetVisible` - так само лише клон (`:335`). Тобто ні Scope, ні IsVisible з API/імпорту виставити не можна.
- API: `SaveMethodologyFormulaRequest(Expression, ResultType, OutputUnitId, ArgumentsCsv)` - `Ecr.Api/Controllers/MethodologiesController.cs:744`; `SaveFormula` передає лише ці поля (`:185-196`); хендлер `SaveMethodologyFormulaHandler.HandleAsync` (`Ecr.Application/Calculations/MethodologyDraftHandlers.cs:311-371`) викликає `AddFormula`/`EditFormula` + `SetArguments`, `Scope` не чіпає. `MethodologyFormulaDto` (`Dto/MethodologyDraftDtos.cs:64-71`) Scope не повертає.
- Імпорт: `MethodologyPackageFormulaDto` (`Dto/MethodologyPackageDtos.cs:64-72`) без scope; `ImportFormulaContent(Code, Expression, ArgumentsCsv)` (`MethodologyPackagePlanner.cs:16`); `ImportMethodologyPackageHandler.cs:209-213` - `AddFormula` + `SetArguments`, без `SetScope`.
- Наслідок для правила: `MethodologyCategoryRuleChecks.cs:108-117` вимагає `Scope == Row` для кожної `!Formula` у правилі (інакше `categoryRuleBadFormula`), а рантайм (`GenericCalculationModule.cs:363-364, 390-415`) обчислює правило в `rowContext`, де є лише Row-формули. Підтверджує звіт: `!ECW_Location` на Substance-формулі = `#REF`.
- Додатково: Row-формула не має посилатись на Substance-формулу чи речовинні константи (`RejectScopeViolations`, `PublishMethodologyHandler.cs:589-690`, код `rowScopeReferencesSubstance`). Для 5.1 `ECW_Location = !ECW_RepairStatus + '_' + !ECW_Category` це означає: Row мають бути ВСІ ТРИ формули (транзитивне замикання). Що саме з цього AF-формули, а що аргументи/константи - з пакета не видно (див. питання).

### 2. Мінімальний фікс
Явне необов'язкове поле `scope` (`Substance`/`Row`) у трьох місцях; пропуск = «не змінювати» (на нове - `Substance`).
- `MethodologiesController.cs`: `SaveMethodologyFormulaRequest` + `MethodologyFormulaScope? Scope`; передати в хендлер (~4 рядки).
- `MethodologyDraftHandlers.cs`: параметр `scope`, `if (scope is { } s) formula.SetScope(s);` після `SetArguments`; у `MethodologyFormulaDto` + `Scope` і в обох місцях побудови DTO (`:253`, `:363`) (~10 рядків).
- Імпорт: `MethodologyPackageFormulaDto` + `string? Scope`; `ImportFormulaContent` + scope; ДОДАТИ scope у хеш вмісту (`MethodologyPackagePlanner.cs:68`, інакше два пакети, що різняться лише областю, склеяться як «однакові»); `ImportMethodologyPackageHandler.cs:212` - `SetScope`; планувальник - валідація значення (невідоме = blocker, як `categoryRule*`) (~25 рядків).
- Обсяг: ~40-50 рядків коду + ~120 рядків тестів + згенерований контракт. Два коміти: (а) код+тести, (б) `openapi.snapshot.json`/`schema.d.ts` останнім кроком після rebase.
- Опційно (НЕ в мінімумі, рішення продукту): автопозначка Row для замикання `!X` правила під час імпорту. Без неї імпорт AF-пакета не дає робочого `!ECW_Location` без ручного `scope` у пакеті.
- Той самий пропуск є для `IsVisible` (не виставляється ні API, ні імпортом); поза L2-1, лише відмічаю.

### 3. Безпека / контракт / міграція
- Міграція: НЕ потрібна (колонка `Scope` вже є: `CalculationsConfiguration.cs:193-194`, `DF_MF_Scope`).
- Контракт: ТАК (поле в запиті й відповіді + пакет `ecr-methodology-package v1`, поле необов'язкове -> зворотна сумісність збережена). `openapi.snapshot.json` + `schema.d.ts` - гарячі файли, lane із зеленим CI, регенерація останнім кроком; `EndpointCoverageTests` / сторож контракту.
- Безпека: права ті самі (`SaveMethodologyFormulaHandler.Permission`, `scope.RequireAsync`), нових ендпоінтів немає. Зміна області формули опублікованої версії неможлива (чернетка). Ризик логічний: перемикання на Row змінює семантику виходів (раз на рядок, `substance = null`) - захищає публікація (`rowScopeReferencesSubstance`, перевірка виходів `:639`).

### 4. Тест-план (червоне до фіксу)
- Application: збереження формули зі `scope: Row` -> DTO повертає `Row` (до фіксу немає поля/лишається Substance); редагування без `scope` не скидає Row.
- Application/планувальник: пакет з `"scope":"Row"` -> чернетка має Row; невідоме значення -> blocker; два пакети, що різняться лише scope, мають різний хеш.
- Api: PUT formula зі `scope` круговий (request -> GET).
- Calculations (end-to-end, як `CategoryRuleEngineTests`): 5.1-подібна версія - три Row-формули + правило `!ECW_Location`; публікація проходить, ключ `Running_Cat1`. До фіксу: `categoryRuleBadFormula`. Залежить від L2-2 і expr-text-plus (`+` двох текстів) - вони в combo, не в RC12.
- Негатив: Row-формула з `SUBSTANCE()` - лишається `rowScopeReferencesSubstance`.

### 5. Залежності
Незалежний від L2-3/4/5/7. Кінцевий тест на 5.1 потребує L2-2 + expr-text-plus. Конфліктує по файлах лише з контрактом (гарячі) і з `MethodologyPackagePlanner.cs` (його ж чіпає L-2 lane).

### 6. Відкриті питання
- [факт замовника/методолога] Які саме формули AF-пакетів 5.1/5.2/7.21 мають бути Row? Припущення: `ECW_Location`, `ECW_RepairStatus`, `ECW_Category` (їх вписали в правило в RC12). Чи читають вони речовинні константи - з пакета не перевірено.
- [рішення продукту] Автопозначка Row для замикання правила під час імпорту? Дефолт до відповіді: НІ, лише явне `scope` (без «магії»; пакетний експортер AF має виставляти поле).
- [невідомо] Чи існує в самому AF поняття «раз на рядок» для таких формул - у репозиторії не знайдено.

---

## L2-3 (P1): категорія за Lookup-колонкою - `@Lookup` порожній у Legacy

### 1. Корінь - ПІДТВЕРДЖЕНО; і є дві складові

Що кладе документ у Lookup-комірку:
- `CellValueData` (`Ecr.Domain/ValueObjects/CellValueData.cs:11-60`): рівно ОДНЕ поле `Value*` заповнене (`IsWellFormed`). У Lookup-комірки заповнене лише `ValueRegistryEntryId` (id запису довідника, число); `ValueString` і `ValueNumeric` = `null`.
- Клієнт/PATCH: `PatchCellsHandler.cs:838-841` віддає `ValueRegistryEntryId`; перевірки `:1754-1841` вимагають непорожній id, того ж довідника, не видалений/активний/чинний на дату. Excel-імпорт: `ImportDiffBuilder.cs:468-489` переводить КОД запису довідника (текст/число з книги) в id; нерозпізнаний код лишається текстом і потім відхиляється.

Як це доходить до виразу:
1. `CalculationInputBuilder.cs:71-84`: `new CalculationArgument(код колонки, ValueNumeric, ValueString, UnitId, entryRefs ? ValueRegistryEntryId : null)`; `entryRefs = NumericMode == Strict` (`:59`, рішення D-161, `docs/tz/10-decisions.md:428`). У Legacy: Value=null, ValueString=null, EntryId=null.
2. `GenericCalculationModule.ToValue` (`:1282-1289`): EntryId -> Number(id); інакше Value -> Number; інакше ValueString -> Text; інакше `Null`. Тобто Legacy `@Land_TypeFuel` = `Null` (ключ у словнику ЄСТЬ, тому не `#ARG`).
3. Правило: `EvaluateCategoryRule` (`:1062-1102`) рахує в `rowContext`, чиї аргументи - той самий словник (`:350-351, 392-400`). `Null = 'Diesel - Дизель'` -> FALSE (рівність не поглинає Null, 02b §6.2) -> спрацьовує остання гілка `''` -> `empty key` -> відмова (`categoryRuleFailed`). Звіт: «задача Failed ≈40 %» - це L2-6 (відмова рядка валить усю задачу; лінія 2fb0a199 це лагодить: рядки пропускаються).
4. Золотий тест у публікації подає `valueString` - тому правило «зеленіє», а реальний документ дає id. Це розрив між тестовим входом і реальним (`CalculationArgument` серіалізується в `inputJson` як є).

Чи справжнє твердження звіту: ТАК. Нюанс: «у Legacy EntryRef не передається» - свідоме рішення D-161 («Legacy мусить рахувати побітно як до змін»). Побічно: `ToValue` НЕ перевіряє режим - якщо тест подасть `entryId` для Legacy-версії, `ToValue` віддасть число (комент «недосяжно для Legacy» - хибний для тестового входу; це і є половина L2-5).

Що в Lookup-значенні «підпис чи код»: у вираз потрапляє лише id (Strict) або нічого (Legacy). Текст `'Diesel - Дизель'` доступний тільки через `REGFIELD(@arg, '<поле>')` у Strict. Обхід у RC12 з `REGFIELD(@Land_TypeFuel,'NAME')` дав 100 % ненульових рядків, тобто поле `NAME` запису довідника = `Diesel - Дизель` - ЦЕ ВИВЕДЕНО З УСПІХУ ОБХОДУ, у сіді/довіднику не перевіряв (не знайдено визначення поля `NAME`; воно користувацьке).

### 2. Мінімальний фікс (рекомендований: «EntryRef лише для правила»)
Зберігає D-161 для ФОРМУЛ (жодне число Legacy-формул не змінюється), але дає правилу id навіть у Legacy:
- `CalculationInputBuilder.cs:59,84`: завжди заповнювати `EntryId` (прибрати `entryRefs` як умову заповнення).
- `GenericCalculationModule`: словник аргументів формул будувати через `ToValue(argument, numeric.Mode)`: у `Legacy` ігнорувати `EntryId` (як тепер, бітово), у `Strict` - як тепер. Другий словник `ruleArguments` (EntryId завжди виграє) для обчислення правила: окремий `MethodologyEvaluationContext` із тими самими `!Row`-результатами й константами, але `ruleArguments` (потрібен спосіб віддати Row-результати в другий контекст: метод копіювання результатів або параметр-оверлей) (`:350-415`).
- `RejectExtensionFunctions` (`PublishMethodologyHandler.cs:294`) судить лише формули, тож `REGFIELD` у правилі Legacy-версії публікація НЕ відхиляє (є `ruleFormulas`, але вони йдуть лише в `CheckArgumentColumnsAsync`, `:327-329`) - це потрібно ЗАЛИШИТИ так і закріпити тестом (інакше Legacy-версія з правилом на REGFIELD заборониться ECR-CALC-0433).
- Обсяг: ~40-70 рядків коду + ~150 тестів; 2-3 коміти. Залежить від L2-4 (інакше `REGFIELD` у правилі = `#REF`).
- Побічні ефекти: `CalculationResultStore.WriteInputsAsync` (`:233-248`) для аргументів, прочитаних кроками, пише `Value ?? EntryId` - у Legacy-версії з REGFIELD-правилом вхід збереже id (узгоджено зі Strict). `ReadArguments` (`GenericCalculationModule.cs:523-536`) повертає аргументи, прочитані кроками.

Розглянуті й НЕ рекомендовані варіанти:
- B. Віддавати в Legacy текст запису як `ValueString` для всіх Lookup-аргументів: змінює вхід усіх Legacy-формул, що читають Lookup-аргумент, суперечить D-161; невідомо, яке поле запису є «підписом».
- C. Змінити в шаблоні LAND_REG колонки палива/матеріалу з Lookup на String (як в AF, де порівняння з текстом працюють): нуль коду, але втрата цілісності довідника - рішення замовника/шаблону.
- D. Лишити як є й писати нові `_L` Strict-методології (поточний обхід): вимагає майбутньої `effectiveFrom` (`strictBackdated`, `RejectBackdatedStrict`, `PublishMethodologyHandler.cs:125`), тож історичні періоди не рахуються; і породжує L2-5, L2-7. Не рішення.

### 3. Безпека / контракт / міграція
- Міграція: НЕ потрібна. Контракт: НЕ змінюється (внутрішній шлях розрахунку; `CalculationArgument.EntryId` вже є).
- Безпека: id запису приходить із комірки документа, яку користувач уже бачить; `REGFIELD` читає знімок довідника на дату періоду (RT-23a), що вже діє у Strict. Нового витоку немає; права на довідник розрахунок не обходить (виконується від імені системи, як і Strict-шлях). Перевірити, що знімок обмежений видимістю на дату (існуюча поведінка `IRegistrySnapshot`).
- Числовий ризик: гарантія «Legacy-формули побітно як до» тримається лише тестом на `ToValue(…, Legacy)` - обов'язковий мутаційний доказ (мутація: віддати EntryId у Legacy -> тест червоніє).

### 4. Тест-план (червоне до фіксу)
- `CalculationInputBuilder` (Calculations.Tests): Legacy + Lookup-комірка -> `argument.EntryId == id` (зараз `null`).
- Модульний: Legacy-версія, формула `@Lookup` -> `Null` (охоронний, зелений до і після); правило `REGFIELD(@Land_TypeFuel,'NAME')` у Legacy-версії -> ключ `Diesel` (до фіксу `empty key`/`#BadValue`).
- Strict без змін (`CategoryRuleEngineTests` уже на Strict).
- Публікація: золотий тест із `entryId` для Legacy-версії з REGFIELD-правилом проходить; для Legacy-версії з правилом `= 'текст'` і входом `entryId` - див. L2-5.
- Інтеграційний: реальний документ із Lookup-колонкою (`LAND_REG`-подібний), перерахунок -> результати > 0 і без падіння задачі (вимагає L2-6 у базі для відмови рядка).

### 5. Залежності
Після/разом із L2-4 (REGFIELD у правилі). Усуває потребу в Strict для Land, а отже знімає L2-7 і більшу частину L2-5. Файл `GenericCalculationModule.cs` спільний з L2-4 і L2-6 (2fb0a199) - узгодити порядок: L2-6 -> L2-4 -> L2-3 (див. нижче).

### 6. Відкриті питання
- [факт замовника] Чи колонки Land (`Land_TypeFuel`, `Land_TypeStorMater` ...) мають лишатись Lookup (довідник) у шаблоні LAND_REG, чи допустимий String як в AF? Дефолт: лишаємо Lookup, фікс A.
- [невідомо] Яке поле запису довідника (`NAME`?) містить `Diesel - Дизель` і чи стабільне воно між середовищами; чи є в записі інше поле (код) як ключ. Дефолт: `NAME`, перевірити в сіді довідника палива.
- [рішення продукту, D-161] Чи узгоджено людиною розширення D-161: «EntryRef для правила категорії в усіх режимах, для формул - лише Strict»? Дефолт до відповіді: так (формули Legacy не змінюються ні на біт); потрібен запис у `docs/tz/10-decisions.md` (D-nnn) - ТЗ не міняв.

---

## L2-4 (P2): `REGFIELD` лише в правилі категорії - `#REF`

### 1. Корінь - ПІДТВЕРДЖЕНО
- Знімок довідників будує `GenericCalculationModule.RegistriesAsync` (`:150-156, 182-231`) із `RegistryCodes(formulas, out readsEntryFields)` (`:237-299`), яке розбирає ЛИШЕ `MethodologyFormula.Expression` (`:242-244`). Правило (читається в `PrepareAsync`, `:121-122`, ДО цього виклику) в перелік не потрапляє. Без `readsEntryFields` Lookup-довідники колонок прив'язки (`ListLookupRegistryIdsAsync`, `:204-206`) не додаються, знімок = `null` (`:190`), і `REGFIELD` у правилі дає `#REF` (`FieldPath` без знімка -> `GetRegistryField` -> `BadReference`).
- Публікаційна сторона теж сліпа до правила: `RegistryShapesAsync(parsed)` (`PublishMethodologyHandler.cs:349`), `RequireValidReferences(parsed…)` (`:353`), `ReplaceRegistryUsesAsync(parsed…)` (`:403`) беруть лише `parsed` формул. Наслідки: (а) описка в `REGFIELD`-шляху правила не ловиться публікацією, (б) `cfg.RegistryUse` не фіксує, що версія читає довідник через правило - зміна довідника не позначає результати застарілими (RT-19/RT-25).

### 2. Мінімальний фікс
- Рантайм: передати текст правила в `RegistriesAsync`/`RegistryCodes` (розбирати його тим самим обходом, `:242-295`; `RegistryCodes` стає `IEnumerable<string>` виразів) - `readsEntryFields` і літерали кодів довідників з правила. Також ключ кешу `GetOrResolveRegistryIdsAsync` (`:215-219`) містить `readsEntryFields`/коди - при зміні коду правила ключ міняється коректно. ~15-25 рядків.
- Публікація: додати `categoryRule.Rule` (`ParsedFormula`) у `parsed` для `UsesRegistries`/`LoadShapesAsync`/`RequireValidReferences`/`BuildUses` (`[.. parsed, .. ruleFormulas]`, вже є `ruleFormulas`, `:327`). Перевірити, що ці методи не вимагають, щоб код ParsedFormula був серед `formulas` версії (`MethodologyRegistryChecks.FormulaRegistries` групує за кодом, `:102-110`; `BuildUses` `:328`) - ЦЕ НЕ ПЕРЕВІРЕНО; можливо, `BuildUses` треба навчити «синтетичного» коду `CategoryRule`.
- Обсяг: ~30-50 рядків коду + ~100 тестів, 2 коміти (рантайм; публікація/uses). Рантайм-коміт самодостатній і закриває звіт.

### 3. Безпека / контракт / міграція
- Міграція: ні (якщо `BuildUses` пише в наявну `cfg.RegistryUse` - ТАК, форма рядка та сама; джерело - правило замість формули; перевірити унікальний ключ таблиці, чи не потрібен код формули NOT NULL - ВІДКРИТО). Контракт: ні.
- Безпека: без змін; знімок довідників тепер завантажується і для версій, що раніше його не мали (лише коли правило читає довідник): +1 читання на прив'язку, не на рядок.

### 4. Тест-план
- `LookupArgumentRegistryTests`-стиль: версія БЕЗ формул з REGFIELD, правило `REGFIELD(@x,'NAME')`, Strict -> ключ читається (до фіксу `#REF`; `Без_REGFIELD_підстановки_прив_язки_не_читаються` лишається зеленим для версії без REGFIELD).
- Знімок не завантажується, якщо ні формули, ні правило не читають довідників (охоронний).
- Публікація: правило з неіснуючим полем/довідником -> проблема публікації (як для формул); ребра `RegistryUse` з'являються й зникають при видаленні правила.

### 5. Залежності
Незалежний (перевіряється на Strict). Потрібен для L2-3 (A). Той самий файл `GenericCalculationModule.cs`, що L2-3/L2-6.

### 6. Відкриті питання
- [рішення продукту] Чи вважати правило джерелом `RegistryUse` (застарілість результатів при зміні довідника)? Дефолт: так (інакше правка довідника палива не позначає документи).

---

## L2-5 (P2): публікація V2 над V1 - diff ганяє тести на старій версії

### 1. Корінь - ПІДТВЕРДЖЕНО
- `PublishMethodologyHandler.cs:122-126`: `previous = methodology.VersionOn(from.AddDays(-1))`; `BuildDiffAsync` (`:1379-1428`) для КОЖНОГО тесту виконує `RunTestAsync(testCase, Descriptor(methodology, version))` і `RunTestAsync(testCase, Descriptor(methodology, previous))` (`:1392-1394`) - той самий `testCase.Input` (аргументи V2) для попередньої версії.
- `RunTestAsync` (`:860-873`) перехоплює лише `ECR-PRD-0404`; будь-який інший `DomainException` попередньої версії (`categoryRuleFailed`, `constantAmbiguous`, `ECR-CALC-0433`…) валить публікацію НОВОЇ, хоч diff - довідкова інформація (ФВ-9.6), а вердикт виносить `JudgeAsync` лише на новій (`:130`).
- Чому «взаємно виключні входи»: V1-правило `= 'текст'` потребує `valueString` (з `entryId` `ToValue` віддає число); V2-правило `REGFIELD` потребує `entryId` (з `valueString` - `#VALUE`, `FieldPath`). Один `inputJson` не годиться для обох.

### 2. Мінімальний фікс
- У `BuildDiffAsync` обгорнути ПРОГІН ПОПЕРЕДНЬОЇ версії: на `DomainException` (коди `ECR-CALC-0422`, `0433`) - вважати `before` порожнім (усі виходи «нові», `Before = null`) і додати в `MethodologyPublicationDiff.Warnings` рядок «попередня версія не обчислилась на цьому вході: <код>». Поточна версія й `JudgeAsync` - без змін, вердикт золотого набору не послаблюється. ~15-25 рядків + ключ локалізації попередження (UiString/сід; `09-seed.sql` - власна секція) + ~80 рядків тестів. 1-2 коміти.
- Після L2-3 (A) проблема лишається лише для переходів «текстове правило -> REGFIELD-правило» (різні типи входу) - фікс усе одно потрібен.

### 3. Безпека / контракт / міграція
- Міграція: ні. Контракт: можливо - якщо `Warnings` diff'у вже в контракті відповіді публікації, нових полів нема; формат попередження - рядок. Перевірити `openapi.snapshot.json` на `warnings` (ВІДКРИТО).
- Безпека/якість: ризик - приховати поламану попередню версію; пом'якшення: попередження в diff + журналі публікації (`ResultDiffJson` пишеться, `:158-165`). Золотий вердикт нової версії не змінюється.

### 4. Тест-план (червоне до фіксу)
- Application (`CategoryRulePublishTests`-стиль): V1 Legacy з `= 'текст'`, V2 Strict з `REGFIELD`; тест `entryId`; публікація V2 -> успіх (до фіксу: виняток `categoryRuleFailed` з прогону V1). У diff є попередження.
- Охоронний: виняток прогону НОВОЇ версії все ще валить публікацію; червоний золотий набір все ще відмовляє (`RejectRedGoldenSet`).
- Мутація: прибрати `catch` -> перший тест червоний.

### 5. Залежності
Незалежний; файл `PublishMethodologyHandler.cs` (регіон `:1379-1428`) - не перетинається з L2-4 (регіон `:327-362`), але гарячий; послідовні коміти. Частково знімається L2-3.

### 6. Відкриті питання
- [рішення продукту] Чи дозволяти публікацію, якщо попередня версія не обчислюється на тестовому вході? Дефолт: так, з попередженням в diff (diff довідковий).
- [рішення продукту] Альтернатива - окремі тестові входи на версію (`calc.TestCase` на версію вже є; інше джерело входу для `before`): складніше, не рекомендую.

---

## L2-7 (P2): Strict - немає виходу замість 0 при діленні на нуль

### 1. Корінь - ПІДТВЕРДЖЕНО як ПОВЕДІНКА ЗА ЗАДУМОМ, а не регресія
Шлях у коді:
- `StrictDecimalArithmetic.Binary` (`Ecr.Expressions/Evaluation/StrictDecimalArithmetic.cs:50-58`): `/` і `%` при `b == 0m` -> `ExpressionValue.Error("#DIV/0")` (значення-помилка; у `decimal` нескінченності немає). Також `Evaluator.cs:465-470`: ділення на `null` -> `#DIV/0` у всіх режимах.
- `GenericCalculationModule.Emit` (`:578-614`) -> `MaskedZero.Prepare` (`MaskedZero.cs:38-65`): `Legacy` + `NaN/±Inf` -> `0m` із причиною (маска, обрана навмисно); `Strict` -> `null`; а помилка-значення `#DIV/0` не має `AsDouble`, причина `None` -> `Value=null` -> вихід НЕ пишеться, у трейс іде `Failed(code, "#DIV/0")` (`:606-612`). Тобто «відсутній вихід» = штатна поведінка Strict.
- Legacy: `LegacyDoubleArithmetic` віддає `+inf`/`NaN` як значення (`Evaluator.cs:514-518` коментар), маска -> 0. Це відтворює прод-рушій: NCalc 1.3.8 `1/0 = +inf`, `0/0 = NaN`, а нуль з'являється гілкою `Utilities.cs:58` (`double.IsInfinity(d) -> 0`) - `docs/legacy-ncalc-1.3.8.md` §5 (рядки 76-90, ВИМІРЯНО).

Що каже ТЗ / документи:
- `docs/tz/02-requirements.md:745-749` (ФВ-9.9): `Strict` = наскрізний `decimal`, `AwayFromZero`, вмикається явним рішенням і лише з нової `ValidFrom`. Про ділення на нуль у `Strict` у `docs/tz` НЕ сказано. Слова «Strict -> null, не нуль» у коді цитують «ФВ-9.14», але справжній ФВ-9.14 (`02-requirements.md:775`) - таксономія помилок, про null/нуль нічого; фактичне джерело - директива №05 / `H-24d-1`: `docs/build/roadmap.md:279` (I.7: «`null` у Strict»), `docs/build/questions/Q-088.md` (RESOLVED), `docs/build/02b-expressions.md:450, 467` (`#DIV/0` - значення, поширюється). Мотив у `MaskedZero.cs:59-63`: нуль у Strict «гірший за відсутність: виглядає як виміряне значення».
- `docs/tz/05-engines.md:39`: «ділення на нуль -> `null` + діагностика, не виняток» - стосується ДІАЛЕКТУ ШАБЛОНІВ (таблиця семантики null), не методологій.
- Q-088 лишає відкритим: чи маскує чинна система нуль і ВСЕРЕДИНІ методології (проміжні кроки) - «питання методологу», відповіді в репозиторії немає.
- Слово «Hours» у docs/tz у цьому контексті не знайдено (збіги - про інші речі); 2 831 компонент Hours=0/Days=0 - факт регресії RC12, не вимога ТЗ.

Висновок: звіт правильний у фактах (Strict дає відсутність, прод/Legacy - нуль), але це не дефект реалізації Strict: це задокументоване рішення (`H-24d-1`). Дефект - у виборі: Land-методологи потрапили в Strict ЛИШЕ як обхід L2-3.

### 2. Мінімальний фікс
- Рекомендація: КОДУ НЕ ЗМІНЮВАТИ. Після L2-3 (A) Land лишається в Legacy, де 0 (як у проді), Strict не потрібен.
- Якщо Strict все ж потрібен (нові методології): методолог захищає ділення в тексті (`if(Hours = 0, 0, …)`; `if` ліниво обчислює лише гілку, `Evaluator.cs:791-795`) - правка формули, не коду. Можна додати до публікаційного попередження «ділення на значення, що може бути 0, у Strict дає відсутній вихід» - НЕ рекомендую (складний аналіз, шум).
- Варіант B (код): політика Strict «`#DIV/0` -> 0 із причиною» - суперечить `H-24d-1`/Q-088 і ФВ-9.9-обґрунтуванню; вимагатиме нового прапорця версії (міграція, контракт, токен міграцій), рішення людини й запису D-nnn. НЕ рекомендується.

### 3. Безпека / контракт / міграція
- Рекомендований варіант: нічого (жодної міграції/контракту).
- Варіант B: міграція (нова колонка або значення режиму), контракт (режим версії), `docs/tz` - потрібні рішення; токен міграцій - ваш.

### 4. Тест-план
- Рекомендований: тест-документація без коду: `MaskedZeroTests` уже фіксує «Legacy -> 0, Strict -> null»; додати регресійний тест Land-подібної версії, що в Legacy `x / Hours` при Hours=0 дає 0 із причиною `Infinity` у трейсі (червоного немає - поведінка вже така; значення - закріпити).
- Для варіанта B: червоний тест «Strict `/0` -> 0» до фіксу.

### 5. Залежності
Знімається рішенням L2-3 (A). Залежить від рішення продукту (нижче).

### 6. Відкриті питання
- [рішення продукту] Land-розрахунок має лишатись у Legacy (нулі як у проді)? Дефолт: так.
- [рішення продукту / методолог] Для майбутніх Strict-методологій: відсутній вихід при `/0` прийнятний (документований)? Дефолт: так, захист у формулі. Розбіжність джерел: код цитує ФВ-9.14, фактично `H-24d-1`/Q-088 - виправити посилання в коментарі `MaskedZero.cs` (дрібна правка, окремо).
- [невідомо] Чи маскує прод-рушій нуль у проміжних кроках усередині методології (Q-088, питання методологу без відповіді).

---

## Рекомендований порядок і паралельність

| Крок | Що | Паралельно з | Примітка |
|---|---|---|---|
| 0 | L2-6 (2fb0a199) у `dev/integration` | L2-2 (51b3ae54), L2-1 | уже готові lane; торкається `GenericCalculationModule.cs` (+16), тож першим, щоб L2-3/L2-4 робились поверх |
| 1а | L2-1 (scope Row: API+імпорт+контракт) | L2-5, L2-4 | свій набір файлів; контракт - lane, регенерація останнім; кінцевий тест чекає L2-2 + expr-text-plus |
| 1б | L2-4 (REGFIELD з правила: рантайм, потім публікація) | L2-1, L2-5 | файли `GenericCalculationModule.cs`, `PublishMethodologyHandler.cs:327-362` |
| 1в | L2-5 (catch у `BuildDiffAsync`) | L2-1 | `PublishMethodologyHandler.cs:1379-1428`; не одночасно з правкою 1б у тому ж файлі - послідовні коміти однієї сесії |
| 2 | L2-3 (EntryRef для правила, Legacy) | - | ПІСЛЯ 1б (потребує REGFIELD у правилі) і L2-6; торкається `CalculationInputBuilder.cs`, `GenericCalculationModule.cs` |
| 3 | L2-7 | - | коду немає; закрити рішенням продукту й посиланням; після L2-3 повторити регресію Land у Legacy (5.4/6.1/7.7 з оригінальними кодами) |

Мінімум сесій: одна сесія веде 1б -> 2 -> 1в (спільні файли), друга - 1а (контракт). Міграцій не потрібно ні в одному рекомендованому варіанті (токен міграцій не використовується); контракт - лише L2-1 (і можливо `warnings` у L2-5).

## Зведення відкритих питань (дефолт діє до відповіді)
1. Які формули AF-пакетів мають бути Row (5.1/5.2/7.21) і чи автопозначати замикання правила при імпорті? Дефолт: явне `scope`, три формули 5.1 вручну.
2. Lookup-колонки Land лишаються Lookup (фікс A) чи стають String? Дефолт: Lookup.
3. Яке поле довідника (`NAME`?) дає `Diesel - Дизель`; чи стабільне. Дефолт: `NAME`; перевірити сід.
4. Узгодити з людиною розширення D-161 (EntryRef у правилі в усіх режимах). Дефолт: так; запис у `docs/tz/10-decisions.md` (я не змінював).
5. Правило як джерело `cfg.RegistryUse` (застарілість). Дефолт: так.
6. Публікація з попередньою версією, що не обчислюється (L2-5): дозволити з попередженням. Дефолт: так.
7. Strict `/0`: лишити як є (документоване) - Land у Legacy. Дефолт: так.
8. Не перевірено: форма рядка `cfg.RegistryUse` для правила (L2-4, публікація); наявність `warnings` у контракті публікації (L2-5); серіалізація enum `MethodologyFormulaScope` у JSON (рядок/число) для нового поля API.
