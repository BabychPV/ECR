# Директива №16 — залишок №14 після звірки з кодом

**Дата:** 2026-09-27 · **База:** `dev/integration` @ `2b653a4c` (+ WR-03 `ea039c80`) · **Статус:** до виконання

**Походження.** 2026-09-27 людина попросила звірити
[`DIRECTIVE-14-ARCH.md`](DIRECTIVE-14-ARCH.md) (2026-09-18) з кодом. Звірку
зроблено на `dev/integration@2b653a4c` чотирма аудиторами з доказом
`файл:рядок`; ключові «відкрито/частково» оркестратор перевірив сам. Після
звірки зроблено WR-03 (`ea039c80`). Рішення людини: невиконаний залишок
переноситься сюди, №14 закрито як план.

**Коди.** Кожен пункт має код із №14 (`DAT-`/`CAL-`/`WR-`/`RD-`/`CL-`/`MI-`/`MS-`/`AR-`)
і посилається на його опис там — нових кодів для старих пунктів не вигадуємо.
Нова знахідка, якої в №14 немає, отримує код `D16-NN`.

**Достовірність.** Номери рядків — на `2b653a4c`. Перед стартом пункту
перевір `git grep`: код рухається, рядок міг зсунутися або вже бути
виправленим. Виняток — D16-03 і D16-04 (додано 2026-09-27 пізніше): їхні
рядки звірено на `dev/integration@1b2aa3a8`.

Формат, як у №14 §3: **Факт** (що робить код) → **Зробити** (що змінити) →
**Доказ** (тест, який падає на нинішньому коді). ⏱ — величина потребує
заміру; без заміру не комітити як «прискорення».

---

## 0. Що з №14 уже зроблено

| Пункт №14 | Що саме | Коміт(и) |
|---|---|---|
| DAT-01 | фальшивий `409` на рівні документа | `4855cd82` |
| DAT-02 | перерахунок: незмінене, усі рядки, транзакція | `54bdaf2e` |
| DAT-03 | перевірка довжини; TVP без обрізання | `2c4c4930`, `745d8f98` |
| DAT-04 пп. 2–3 | «немає рішення = відмова»; чужий `periodKey` → `422` | `d6729074` |
| DAT-05 | імпорт не застосовується наполовину | `4bfd1e00`, `dab77f22` |
| DAT-06 | workflow і періоди одним комітом | `2c4c4930` |
| DAT-07 | механізм збереження правок (D14-12) | `1786ed4f`, `9f853872`, `1c594ded`, `c37bfc46` |
| DAT-08 | межа помилки рендера | `817be9dc` |
| DAT-08 | банер застарілої версії, заголовки `/assets` | `13e872d7` |
| DAT-09 | подвійне створення документа | `4855cd82` |
| CAL-02, CAL-03 | замикання залежностей; лише брудні рядки | `5b6dd786` |
| CAL-05, CAL-06 | кеш виразів; версія методології раз на прив'язку | `bc2ea6bd` |
| WR-01, WR-06 | типізовані параметри; `MERGE` без незміненого | `06eba8c0` |
| WR-02 | set-based запис через TVP | `745d8f98` |
| WR-03 | рішення доступу лише для адрес батчу | `ea039c80` |
| WR-05 (основне) | `PeriodKey` у запитах | `1693a8d2` |
| RD-01 | стиснення й кеш статики | `13e872d7` |
| RD-04 | N+1 по методологіях у зрізі | `92a472cb` |
| RD-05 | single-flight, мемо 5 с, `RevisionKey` | `33a02ff8` (+ `1693a8d2`) |
| RD-06 | N+1 | `c92fa380` |
| CL-01 | локальне застосування відповіді | `90c09b25`, `c975a719` |
| CL-02, CL-03 | адресна інвалідація; без лінійного пошуку | `90c09b25` |
| MI-01 | спільне кільце ключів DataProtection | `0151635e` |
| Excel (§3.6) | стиль на колонку, `AdjustToContents` лише заголовків, пакетне читання | `a1087b3c` |
| AR-06 (частина) | `ITemplateStructure` прибрано | `33a02ff8` |

Залишок кожного частково зробленого пункту — нижче, під тим самим кодом.

---

## 1. P0 — коректність і безпека

Порядок = пріоритет.

#### D16-01 · Знятий із ролі грант діє до 30 хв для носіїв ролі через групу AD
**Факт.** `ResourceGrantHandlers.cs:205` → `UserStore.RotateStampsForRoleAsync`
(`UserStore.cs:482-494`) крутить штампи лише для призначень з `UserId != null`.
Ключ кешу профілю = користувач + штамп + відбиток груп
(`AccessDecisionService.cs:56-69`), а відбиток (`:84-100`) рахує лише
призначення ролей на SID, не гранти ролей. Профіль кешується 30 хв. Тож
користувач, що має роль лише через групу AD, зберігає знятий грант до TTL.
Той самий клас: `ProjectOwnershipGrant.cs:112` — лише локальне
`InvalidateProfileAsync`; інший інстанс не бачить зміни до TTL (там це
розширення доступу, не витік). Пов'язано з MI-03 (ревізії в ключах кешів).
**Зробити.** Ревізія грантів у ключі профілю: напр. `max(Id)` + `count`
`ResourceGrant` ролей, призначених на групи сесії, або лічильник ревізії
доступу в базі, який піднімає кожна зміна грантів/призначень.
**Доказ.** Роль лише через групу, грант знято → наступний запит відмовляє;
на другому хості — теж.

#### D16-03 · Згортка даних PI не обмежена періодом екземпляра
*Звірено на `1b2aa3a8`.* Мовчки хибні числа у звіті в кожній комірці, яку
живить PI, — тому одразу після D16-01.
**Факт.** Після збору `CollectionJob.EnqueueMaterializationAsync`
(`CollectionJob.cs:168-229`, виклик `:96`) ставить `MaterializeTask` у
**кожен** екземпляр таблиць, яких стосуються мапінги сутності, — усіх
періодів, без фільтра за датами (`:198-212`; `OrderBy(PeriodKeyValue)` за
зростанням + `Take(200)`, тож за понад 200 екземплярів відсікаються якраз
найсвіжіші), — і всім одне вікно збору `[from,to)` (`:51`: `to − LookbackDays`,
типово 7 діб, `:247`). `MaterializeCollectedDataJob.AggregateAsync`
(`MaterializeCollectedDataJob.cs:149-196`) бере `ext.RawDataPoint` саме за
`task.FromUtc/ToUtc` (`:154-164`) і згортає `PeriodFold.Fold` (`:187-190`).
Меж періоду екземпляра ніхто не читає: запит до `db.Periods` (`:84-89`) бере
лише `State`, а `IPeriodStore.FindPeriodBoundsAsync` (`IPeriodStore.cs:73`;
споживачі — `PatchCellsHandler.cs:986`, `RecalculationService.cs:1076`,
`CalculationOrchestrator.cs:278`, `GenericCalculationModule.cs:445`,
`GetTableSliceHandler.cs:335`) тут не викликається. Наслідки:
(а) «Sum за місяць» = сума точок вікна збору, тобто останніх 7 діб;
(б) те саме число лягає в кожен `Open`/`Grace` період: поки попередній місяць
у `Grace`, січень і лютий отримують однакове значення, а вікно через межу
місяців домішує точки сусіднього;
(в) кожен наступний прогін перезаписує значення згорткою лише свого вікна:
`IntegrationCellPatcher.cs:91-97` оминає тільки комірки, остання зміна яких
`UserEdit` (`:67-70`, `:137-174`), а значення від інтеграції переписує;
(г) періоди `Scheduled` отримують задачу щопрогону і пишуть у журнал покриття
`SkippedPeriodClosed` «пізній збір лишається сирим»
(`MaterializeCollectedDataJob.cs:91-100`) — хибний запис.
Стеля `MaxPoints = 500 000` (`:52`) спільна на всі поля сутності; `Take`
(`:161`) за зростанням часу відрізає **хвіст** мовчки — без запису в журнал
покриття (перегляд мапінгу має `IsTruncated`, задача — ні): `Last` бере не
останню точку, `Sum` занижено. Зараз вікно 7 діб; згортка за місяць множить
точки ×4.4. Щільність точок PI — факт джерела, у коді її немає: за 1 точку/хв
місяць одного поля — 44 640 точок, тож понад 11 полів на сутність місяць
обрізається.
**Зробити.**
1. `EnqueueMaterializationAsync` — задача лише в екземпляри, чий період
   перетинає `[from,to)`. Межі — `[PeriodStart, PeriodEnd+1)` у поясі проєкту
   (`Project.TimeZoneId`, перетворення як `Period.cs:88-91`, D-68). За станом
   не фільтрувати: закритий період, що перетинає вікно, і далі має отримати
   `SkippedPeriodClosed`.
2. `AggregateAsync` — точки за межами **періоду** екземпляра (усі наявні в
   `ext.RawDataPoint`), не за вікном збору; вікно лише вирішує, які періоди
   зачеплено.
3. `MaxPoints` — стеля на поле, а не на сутність; досягнута стеля → рядок у
   журналі покриття, значення не пишеться (часткова сума — хибне число, а не
   «приблизне»).
**Доказ.** (1) Точки в січні й лютому, вікно покриває обидва → січневий
екземпляр отримує згортку лише січневих, лютневий — лише лютневих.
(2) У `RawDataPoint` є точки 1–20 січня, вікно збору — 21–27 січня → `Sum` =
сума за 1–27 січня, а не за 21–27. (3) Період, що не перетинає вікно
(`Scheduled`), задачі не отримує, у журналі покриття рядка немає. (4) Пояс
проєкту не UTC: точка о 23:30 31 січня за місцевим часом — у січні, о 00:30
1 лютого — у лютому. (5) Стеля досягнута → рядок у журналі, комірка не
змінилась. Мутація: повернути `task.FromUtc/ToUtc` замість меж періоду →
(1), (2) червоні.
**Суміжне (не входить у DoD; див. `FEATURE-HSE301-VIEW.md`).** `PeriodFold`
`Avg` — просте, не зважене за часом середнє (`PeriodFold.cs:53`);
`SourceUnitConverter.Convert` (`SourceUnitConverter.cs:109`) не викликає
ніхто, тож конверсії при завантаженні (`docs/tz/06-integration.md` §6.4 п. 3)
немає. Знайдено при звірці: мапінги не звужуються до таблиці екземпляра
(`MaterializeCollectedDataJob.cs:66-73`), тож колонки чужої таблиці
повертаються з `IntegrationCellPatcher.cs:82-88` у `KeptManual` і лягають у
журнал покриття як `ConflictKeptManual` «комірка має правку людини»
(`MaterializeCollectedDataJob.cs:124-133`) — хибний запис щопрогону, щойно
сутність живить дві таблиці.

#### DAT-04 п. 1 · Рядки створюються й комітяться до валідації
**Факт.** `PatchCellsHandler.cs:122` `BuildCellChangesAsync` → `:901`
`rowStore.CreateRowsAsync` (власний `SaveChangesAsync`, `RowStore.cs:304`);
лише потім `:123` `EnforceRequiredInputsAsync`, `:131` `EnsureValidationPasses`,
`:140` `PersistChangesAsync` (транзакція). Тест сам визнає дефект:
`CellWriteRoundTripTests.cs:485-493`.
**Зробити.** Як у №14 DAT-04 (1): валідація того, що не потребує `rowId` →
транзакція → у ній `CreateRowsAsync` → розподіл комірок нових рядків →
`ApplyAsync`.
**Доказ.** Новий рядок + значення 1001 символ → `422` і 0 рядків у
`doc.TableRow`; повтор того самого батчу — не `ECR-ROW-0409`.

#### D16-02 · Вставка квадратична (суміжне з CL-05)
**Факт.** `DocumentGrid.tsx:530-537` `saveThroughStore` кличе `putPendingEdit`
на кожну правку; `pendingStore.ts:195-201` кожен виклик копіює всю мапу зрізу
(`new Map(pendingSlice(...))`) і сповіщає підписників. 30 000 комірок ≈
4.5·10⁸ копіювань елементів.
**Зробити.** Пакетний `putPendingEdits` — одна копія, одне сповіщення на пачку.
**Доказ.** Лічильник копій/сповіщень на 30 000 правок: одне сповіщення, копій
O(n), а не O(n²).

#### DAT-08 залишок · Оболонка з фолбека кешується; `preloadError` губить незбережене
**Факт.** (2) `Program.cs:345` `MapFallbackToFile("index.html")` без
`staticFileOptions` (ті, що на `:248-271`), тож оболонка, віддана фолбеком, іде
без `no-cache`; тест `StaticAssetsAndCompressionTests` перевіряє лише
`/index.html` напряму. (1) `staleVersion.tsx:81-91` на `vite:preloadError`
показує банер, але не зберігає незбережене; коментарі `:30-33`, `:107-109`
кажуть «D14-12 ще немає» — застаріло, `flushUnsaved` є
(`shared/ui/unsavedSources.ts:64`).
**Зробити.** Передати ті самі `staticFileOptions` у фолбек; у обробнику
`preloadError` викликати `flushUnsaved` до банера; прибрати застарілі коментарі.
**Доказ.** `GET /documents/1` → `Cache-Control: no-cache`; подія
`vite:preloadError` з незбереженою правкою → правку надіслано.

#### WR-11 · Межа батчу `PATCH`
**Факт.** Межі немає ніде: `PatchCellsRequest.cs:12-16`, контролер, обробник.
**Зробити.** Стеля 50 000 комірок → `422` зі стабільним кодом і ключем
каталогу повідомлень.
**Доказ.** 50 001 комірка → `422` із цим кодом; 50 000 — проходить.

---

## 2. P1 — шлях запису

#### D16-04 · `REGFIELD` у правилах валідації не бачить довідника
*Звірено на `1b2aa3a8`.* **Чому P1, а не P0:** хибних даних не пише і не
мовчить — кожне обчислення дає видиме попередження; правил із `REGFIELD` у
сіді немає (функцію додано 2026-09-23, `a692378a`). Але блокувальне правило з
`REGFIELD` не блокує нічого — тому першим у P1.
**Факт.** Обидва контексти правил — `SingleCellContext`
(`ValidationEngine.cs:217`, клас `:223`) і `ScopeContext` (`:102`, клас `:235`)
— кличуть базовий конструктор без знімка, тож `ValidationEvaluationContext`
бере порожній (`ValidationEvaluationContext.cs:36-38`), і `GetRegistryField`
(`:97-101`) повертає `#REF` на будь-який запис. Інших нащадків чи шляхів
передачі немає (`git grep ": ValidationEvaluationContext"` — два збіги).
Коміт `a692378a` заявляв «інжектований знімок», коментар `:91-96` —
«реальні дані, не заглушка»; викликача, що передає знімок, немає, тестів
`REGFIELD` у правилах — теж. Другий шар на шляху «Перевірити»/подання:
`TableValidation.SliceContext` віддає Lookup як `long`
(`CellValueMapping.ToRuleValue`, `CellValueMapping.cs:101-104`), а
`ScopeContext.FromObject` (`ValidationEngine.cs:248-257`) гілки `long` не має →
`Text`, і `REGFIELD` дає `#VALUE` ще до знімка (`TemplateFunctions.cs:330-333`).
На `PATCH` значення з JSON — `decimal` (`CellValueReader.cs:104-105`), там `#REF`.
**Наслідок для користувача.** Помилка обчислення правила → `Warning`
`ECR-VAL-RULE` «Rule 'X' did not return a logical answer: #REF»
(`ValidationEngine.cs:135-144`, `:164-166`, `BlocksSave: false`). Тобто
правило рівня `Error` з `REGFIELD`: коміркове — не блокує збереження
(`ValidationRule.cs:59`); рівня рядка/таблиці/документа — не блокує подання
(`SubmitSheetHandler.cs:356-358` бере лише `Severity == Error`). Хибного
блокування немає; є постійне «правило зламане» на кожному рядку і порушення,
яке проходить у подання.
**Зробити.**
1. Кроки 2–4 `RecalculationService.LoadRegistryFieldsAsync`
   (`RecalculationService.cs:1188-1286`, з `ToExpressionValue` `:1296`) —
   у спільний завантажувач знімка над `IRegistryStore`, вхід
   `(entryId, registryDefId, fieldCode)`; перерахунок кличе його без зміни
   поведінки. Не дублювати.
2. Набір полів для правил — `DependencyExtractor` над AST правила (у правил
   немає рядків у `cfg.FormulaDependency`, тож крок 1 перерахунку не
   годиться); `entryId` — з Lookup-комірок, які правила читають. Правила без
   `REGFIELD` — нуль додаткових звернень (храповик WR-04 не зсувається).
3. Знімок — параметром у `ValidateCell`/`ValidateScope` → конструктори
   `SingleCellContext`/`ScopeContext`. Викликачі: `PatchCellsHandler.Validate`
   (`PatchCellsHandler.cs:1762-1803`) і `TableValidation.Run`
   (`TableValidation.cs:46-78`) ← `ValidateDocumentHandler.cs:109`,
   `SubmitSheetHandler.cs:357`. «Перевірити» і подання — з одним знімком
   (ФВ-5.4, `TableValidation.cs:11-16`).
4. Гілка `long` у `ScopeContext.FromObject` — інакше «Перевірити»/подання
   лишаться на `#VALUE`.
**Доказ.** Довідник із полем `Limit`, правило рівня `Error`
`REGFIELD([Permit],'Limit') > 0`, Lookup-комірка заповнена: (1) коміркове
(`ValidateCell`) — `Limit = 5` → жодного повідомлення, `Limit = 0` → `Error`
правила, не `ECR-VAL-RULE`; (2) рівня рядка на `PATCH` — те саме; (3) рівня
рядка через «Перевірити» і `Submit` — `Limit = 0` блокує подання. На
нинішньому коді — `ECR-VAL-RULE` з `#REF` у (1), (2) і з `#VALUE` у (3).
Мутація: не передати знімок → (1)–(3) червоні; прибрати гілку `long` →
червоний лише (3).

#### WR-04 · Менше звернень у `PATCH`
**Храповик першим:** сценарний тест «`PATCH` 100 комірок ≤ N звернень» на
`SqlClientCommandCounter`; базова лінія 41 (`MS-01-BASELINE.md:89`), ціль ≤ 10.
**Зробити.**
1. Прибрати `rowStore.TouchRowsAsync` після `ApplyAsync`
   (`PatchCellsHandler.cs:1586`).
2. Контролер передає розв'язаний `TableInstanceRef` (`CellsController.cs:74`,
   `PatchCellsHandler.cs:271`); `ExcelImporter` теж кличе `HandleAsync` —
   узгодити.
3. `RowStore.GetRowsAsync` → `(RowKey, Id, RowVersion, IsOrphaned)` замість
   пари `GetRowVersionsAsync` + `GetRowIdsAsync` (`:376-377`).
4. `OUTPUT inserted.Id, inserted.RowVersion` у `ClaimRowsAsync`
   (`NormalizedCellStore.cs:542`), без перечитування у `BuildResponseAsync`
   (`PatchCellsHandler.cs:1739`).
5. `BuildContextAsync` — 4 запити (`AccessDecisionService.cs:857,872,879,900`)
   → один.
**Доказ.** Храповик опускається з 41 до ≤ 10 і падає на регресії.

#### WR-05 залишок · `PeriodKey` там, де його ще немає
**Факт.** `RowStore.ResolveTableInstanceAsync` (`RowStore.cs:35-44`, позначено
«названо й НЕ зроблено»), `AccessDecisionService.cs:536-543`,
`NormalizedCellStore.cs:202-203` — запити до партиціонованих таблиць без
`PeriodKey`.
**Зробити.** Додати `PeriodKey` у предикат.
**Доказ.** План запиту — пошук в одній партиції (або сторож на текст запиту).

#### WR-07 ⏱ · Зняти `HOLDLOCK`
`NormalizedCellStore.cs:702`. Лише після плану виконання, заміру замків і
тесту гонки двох записів однієї комірки.

#### WR-08 залишок ⏱ · Аудит під навантаженням
`OPTIMIZE_FOR_SEQUENTIAL_KEY` на `PK_CellChange` і `IX_CellChange_Cell`
(`Sql/11-audit-tables.sql:56,64`). Спершу замір конкуренції на вставці.

#### WR-09 · `SEQUENCE … CACHE`
**Факт.** `doc.TableInstanceSeq`, `doc.TableRowSeq`, `calc.CalculationResultSeq`
(`EcrDbContext.cs:247-259`) без кешу, хоча коментар `:232` його обіцяє.
**Зробити.** `CACHE 1000` — окремою міграцією.
**Доказ.** `sys.sequences.cache_size = 1000` для всіх трьох.

#### WR-10 ⏱ · Пул контекстів і з'єднань
`AddDbContextPool` (`DependencyInjection.cs:49`; конструктор контексту вже бере
лише options) + `Max Pool Size`/`Min Pool Size` у `deploy-ecr.ps1`. Спершу
замір.

---

## 3. P1 — шлях читання

#### RD-03 · Менше звернень у зрізі
**Факт.** Чотири читання `doc.TableRow` на зріз (`GetTableSliceHandler.cs:130,132,158`
+ `AccessDecisionService.cs:319`); `TableInstance` — двічі; `Documents`/`Projects`
— повторно; стилі без кешу (`StyleCatalog.cs:22`); одиниці кешуються лише в
межах запиту.
**Зробити.** Одне читання рядків, передане далі; кеш стилів на ревізію.
Храповик першим, як у WR-04.
**Доказ.** Сценарний храповик: зріз ≤ 8 звернень.

#### RD-02 · Права зрізу: «за замовчуванням + винятки»
**Факт.** Контракт досі плоска мапа: `TableSliceDto.cs:32` `CellPermissions`,
`GetTableSliceHandler.cs:259-277`.
**Зробити.** `defaultPermission` + `columnPermissions` + `cellExceptions`.
Зміна контракту — одним PR (сервер, `openapi.snapshot.json`, `schema.d.ts`,
клієнт).
**Доказ.** Зріз 500×60 з однією забороненою коміркою → один виняток, не 30 000
записів.

#### RD-05 залишок · Стеля кешу
`MethodologyRequiredColumnsCache.cs:172-176` ставить `Size` без `SizeLimit` —
стеля не діє. Задати `SizeLimit` або прибрати `Size`.

#### RD-06 залишок · Пагінація адміністративних переліків
`GET /registries/{code}/entries` без пагінації (`RegistriesController.cs:79-83`,
стеля 50 000 у `RegistryStore.cs:23`) — зачепить Lookup-редактор. Методології
без курсора (`MethodologiesController.cs:97-100`).

---

## 4. P1 — черга, перерахунок, кілька інстансів

#### MI-02 · Черга в базі з виключністю на ціль
**Факт.** Quartz у пам'яті (`DependencyInjection.cs:226-232`, коментар
«D14-01, ще не зроблене»).
**Зробити.** Черга в базі:
(а) виключність на `TargetKey` — зараз `QuartzJobScheduler.cs:228-238` лише
локально;
(б) Cancel/Restart прапорцями в `itg.JobProgress` — зараз `:387-431` локально;
(в) постановка в транзакції викликача; прибрати `deferRecalculationUntilMi02`
(`PatchCellsHandler.cs:66-97,150-161`, `ExcelImporter.cs`).
**Доказ.** Два хости, одна ціль → одна задача виконується; скасування з
другого хоста зупиняє задачу на першому; відкат транзакції викликача → задачі
немає.

#### CAL-01 · Злиття постановок і справжній debounce (поверх MI-02)
**Факт.** Ключ задачі з `Guid.NewGuid()` (`QuartzJobScheduler.cs:107`); задача
на кожен `PATCH` (`PatchCellsHandler.cs:1710`); `RollupDebounce`
(`RecalculationService.cs:52`) читає лише тест. Замір: 20 `PATCH` → 20 задач
(`MS-01-BASELINE.md:180`).
**Доказ.** 20 `PATCH` у вікні → 1 задача; правка колонки без залежних → 0
задач.

#### MI-03 · Ревізія структури в ключах кешів
**Факт.** `StructureRevision` у `cfg.TemplateVersion` — 0 збігів; ключ
`MetadataCache.cs:224-225` лише з `PresentationRevision`; інвалідація локальна
(`:184-205`).
**Зробити.** `StructureRevision` у версії шаблону і в ключі кешу.
**Доказ.** Зміна структури на хості A → хост B бачить нову структуру без
чекання TTL.
Права й `ProjectOwnershipGrant` — див. D16-01.

#### CAL-04 · Кеш структурного плану (після MI-03)
План будується на кожен прогін (`RecalculationService.cs:342`, `:299-301`).
Кешувати за ревізією структури.

#### CAL-07 ⏱ · Паралелізм по прив'язках
Зараз паралелізм по версіях (`CalculationOrchestrator.cs:93-95`), не по
прив'язках. Спершу замір.

---

## 5. P1 — клієнт і Excel

#### CL-01 залишок · Після перерахунку зріз не оновлюється
`useCellPatch.ts:126-129,346-350`, `DocumentGrid.tsx:266-270`: після
завершення перерахунку зріз не перезапитується. Або один адресний перезапит
обчислених колонок, або змінити DoD CL-01 і записати це.

#### CL-04 ⏱ · Стабільні `columns` і `source`
`DocumentGrid.tsx:674-680,878-938`. Спершу профіль рендера.

#### CL-05 · Вставка 30 000 комірок: пачки, прогрес, скасування
`DocumentGrid.tsx:1000-1114` — синхронно. Після D16-02.

#### CL-06 · Пам'ять вкладки на вісім годин
Бюджет undo за комірками, а не за кроками (`undo.ts:32,96-98`); LRU сіток
(`SheetTables.tsx:151-156`) — після винесення `overrides` і `UndoStack` із
сітки.

#### Excel (§3.6 №14) залишок
Книга копіюється в пам'ять: `ExcelExportJob.cs:55-57` (`ToArray`),
`ExportStore.cs:40-48,63`, порт `IExportStore.cs:32` на `byte[]` → потік або
порції. Бюджет 8 с p95 на 91×500×60 не міряно (коміт `a1087b3c`: 6.9–11.4 с на
фейках).

---

## 6. Виміри

- **MS-01** — колонка «після» (`--http-gate` на поточному коді) + сценарні
  храповики WR-04/RD-03.
- **MS-02** — `GateBenchmark.cs:517-518` рахує `SUM` по партиції; замінити на
  справжній rollup (`ФВ-3.5`).
- **MS-03** — `OPEN-ITEMS.md:23-31` досі «Заблоковані обладнанням»; переписати:
  Q-063/Q-190 блокує MS-01, не обладнання. Рішення №18
  (`DIRECTIVE-15-DECISIONS.md:34`): міряти на одній машині.

---

## 7. P2 — борг

- **AR-01** — `rowversion` + `If-Match`. Зараз 8 `IsRowVersion`; без нього —
  чернетка шаблону, ролі/гранти, періоди, методології, звіти. Почати з
  чернетки шаблону.
- **AR-02** — в'юха `rpt.v_WaterReport_v1` зашита (`05-rpt-views.sql:13-27`);
  зріз звіту вже з `ColumnsJson` (`4973c65e`).
- **AR-03** — порт `IDocumentExporter { Format }` замість `if/else` у
  `ExcelExportJob.cs:49-64`.
- **AR-04** — стратегії на `CellDataType` (134 розгалуження в 35 файлах).
- **AR-05** — `TemplateVersionPage.tsx`: 1770 рядків, 18 мутацій.
- **AR-06** — мертве: `IUnitOfWork.BeginTransactionAsync` (`IUnitOfWork.cs:12`,
  `UnitOfWork.cs:208`), `RollupDebounce`, `isRetryable` (`api/client.ts:546`),
  npm `zustand`/`react-hook-form`/`zod`/`@formulajs/formulajs` без імпортів
  (`package.json:22,34,36,37`); `Hybrid`/`SwitchStorage` — після MS-03.
- **AR-07** — `Idempotency-Key` на створювальні `POST`.
- **MI-04** — сторожі: (а) мутабельні `static` з allowlist (`Parser._cache`,
  `SingleFlight`); (б) `LayerRulesTests.cs:69-72` дивиться лише
  `/Controllers/` — порушення `Startup/RecurringScheduleService.cs:141,345`,
  `StartupSequence.cs:44` та ін.; (в) `rowversion` на агрегатах AR-01 (ratchet).
- **MI-03** права / `ProjectOwnershipGrant` — див. D16-01.
- **DAT-03** — окремий ключ «задовгий текст» із `{length,max}`; зараз загальний
  `err.ECR-CELL-0422.validationBlocked` (`PatchCellsHandler.cs:1228`).
- **DAT-07** — тест «вставка → `beforeunload` до відповіді → маяк».
- Дрібне: `new Parser()` повз кеш (`FormulaTranslator.cs:77`,
  `ReportRowRules.cs:215`); `operations-runbook.md:186` називає
  `dbo.DataProtectionKeys` замість `sec.DataProtectionKey`; `DENY` на ключі
  DataProtection не заскриптовано; `lookupIdOfText` лінійний
  (`LookupCellEditor.ts:61-68`).

---

## 8. Порядок

P0 (§1) → храповик WR-04 → WR-04/WR-05 (`PatchCellsHandler.cs`,
`AccessDecisionService.cs` — серіалізовано) → RD-03 → RD-02.
MI-02 → CAL-01. MI-03 → CAL-04. Виміри MS-01 — після WR-04/RD-03.
D16-03 ні з ким файлів не ділить (`CollectionJob.cs`,
`MaterializeCollectedDataJob.cs`; `IntegrationCellPatcher.cs` — лише
читання) — паралельно з рештою P0. D16-04 — до WR-04 (спільний
`PatchCellsHandler.cs`; коректність раніше за швидкість) і до CAL-01/CAL-04
(спільний `RecalculationService.cs`).

**Файли, за які конкурують пункти** (у цьому порядку, не паралельно):

| Файл | Черга |
|---|---|
| `PatchCellsHandler.cs` | DAT-04 → WR-11 → D16-04 → WR-04 → MI-02(в) → CAL-01 |
| `AccessDecisionService.cs` | D16-01 → WR-04(5) → WR-05 → RD-03 → RD-02 |
| `NormalizedCellStore.cs` | WR-04(4) → WR-05 → WR-07 |
| `RecalculationService.cs` | D16-04 → CAL-01 → CAL-04 |
| `DocumentGrid.tsx` | D16-02 → CL-05 → CL-04 → CL-06 |

---

## 9. Стан виконання

Стан читається `git log`, таблиця — довідка; оновлюється в тому ж коміті, що
закриває рядок.

| Рядок | Стан | Коміт |
|---|---|---|
| D16-03 | відкрито | — |
| D16-04 | відкрито | — |
