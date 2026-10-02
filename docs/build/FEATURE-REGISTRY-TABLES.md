# FEATURE-REGISTRY-TABLES — довідники як таблиці: складені ключі, композиція, функції довідників у формулах

| | |
|---|---|
| Дата | 2026-09-27 |
| Статус | **Проєкт до виконання.** Рішення `R-1…R-21` **схвалені людиною 2026-09-27** («так, вноси D-149 в реєстр та Схвалено запропоновані рішення») і внесені в `docs/tz/10-decisions.md` як `D-150…D-170`; `R-22…R-27` ухвалені за делегуванням людини того ж дня («все інше на твій розсуд не обмежуй себе») і внесені як `D-195…D-200` — §11.2 |
| Відповіді людини 2026-09-27 | про джерело властивостей компонентів (M, nC, nS, LHV): «це і є addstream» — перевірено на файлах, §11.4 і `R-27`: nC/nS — з хімічних формул Add Stream, M — значення HYSYS (у 301 відновлюються точно), LHV в Add Stream немає |
| Питання замовника | «Чи є можливість в системі додати сутність, яка фактично є таблицею (або 3 таблицями зі зв'язками між собою) з контролем унікальності строк по певним стовпцям, та потім використовувати її в методологіях, формулах (expression)?» |
| Спирається на | `ФВ-8.1…8.14`, `ФВ-9.*`, `D-13`, `D-14`, `D-50`, `D-66`, `D-75`, `I-3`; розбір Excel `excel-analysis.md` (§2.3, §3, §1.5, §6) — робочий артефакт сесії аналізу, **поза репозиторієм**: посилання `excel-analysis.md:рядок` нижче ведуть у нього; чи переносити його в `docs/` — разом із відповіддю на RQ-1 (дані з міткою MSIP «Internal») |
| Суміжне | [FEATURE-HSE301-VIEW](FEATURE-HSE301-VIEW.md) — представлення форми 301, окремий документ; його кроки `RG1…RG3` чекають на `RT-22`, `RT-23a`, `RT-23b` цього плану (§9.3); [DIRECTIVE-16 §2 D16-04](DIRECTIVE-16.md) |

Позначки: ⛔ — межа або заборона; ⚠ — застереження; **жирне** — ключове рішення; `R-n` — рішення
цього документа (`R-1…R-21` у реєстрі `docs/tz/10-decisions.md` — `D-(149+n)`: `R-1` → `D-150`, …,
`R-21` → `D-170`); `RT-nn` — крок
плану (§9); `RQ-n` — питання до замовника (§11.3; префікс `RQ-`, щоб не плутати з журналом проєкту
`docs/build/questions/Q-NNN.md`); `файл:рядок` — перевірено читанням коду 2026-09-27.

---

## 0. Коротка відповідь

**Частково.**

1. «Таблиця» в системі вже є — це **довідник** (Registry): типізовані колонки (число, текст, дата,
   так/ні, посилання на інший довідник, одиниця), записи з вікном чинності, конструктор полів, імпорт
   CSV. Механізм один на всі довідники (`ФВ-8.1`, `docs/tz/02-requirements.md:582-583`).
2. «Три пов'язані таблиці» можна описати вже сьогодні полями-посиланнями (`Lookup`): ціль посилання
   перевіряється, а запис, на який посилаються, не видаляється.
3. **Бракує трьох речей.** (а) Унікальності рядка за набором стовпців — сьогодні унікальний лише код
   запису. (б) Доступу з формул: методологія довідника не бачить узагалі (`#REF`), формула шаблону
   читає одне поле через комірку-посилання, а пошуку за ключем і сум по рядках немає. (в) Зручного
   редактора: форма запису складається з текстових полів, посилання вводиться числом-ідентифікатором,
   значень у переліку не видно.
4. Нижче спроектовано розширення **того самого механізму**, без нової сутності й без DDL під час роботи.
   Воно дає складені ключі з гарантією в БД, відношення «композиція» (потік → кейс → склад), історію
   значень для відтворюваного перерахунку, 7 функцій довідників із однаковою семантикою в методологіях
   і шаблонах, рушій правил («Σ складу = 100 ± допуск»), а також табличний редактор з імпортом
   транспонованого експорту HYSYS. Наскрізний приклад — довідник потоків FLERT і формули форми 301
   (μ, S мас.%, EF), золотий тест — на парі (1D-2, 370 Winter).

---

## 1. Поточний стан

### 1.1 Вимога → що є

| # | Вимога замовника | Стан | Де (`файл:рядок`) | Чого бракує |
|---|---|---|---|---|
| 1 | Сутність-«таблиця» з типізованими колонками | **Є** | `RegistryDef.cs:11-47`, `RegistryFieldDef.cs:21-35`, типізовані колонки значень `RegistryValue.cs:52-62`; типи `Enums.cs:50-65`; `Formula`/`Calculated` у довіднику заборонені — `RegistryValue.cs:122-131` | — (обчислюваних полів у довіднику не буде й далі: `R-1`) |
| 2 | Унікальність рядка за певними стовпцями | **Немає** | єдиний унікальний індекс `UQ_RegistryEntry (RegistryDefId, Code)` — `DictionariesConfiguration.cs:53-54`; `IsKey` — лише ознака (`RegistryFieldDef.cs:28-29`), у DTO вона `IsScopeField` для `ScopeJson` ролей (`RegistryDtos.cs:37-49`); єдине її застосування — вимога «≥ 1 ключове поле» (`RegistryDefinitionHandlers.cs:336-347`); правило `UniqueWithin` зберігається, але його ніхто не виконує: правила читає лише опис довідника (`RegistryDefinitionHandlers.cs:52`, `:326`), а `ApplyValuesAsync` перевіряє тільки тип, обов'язковість і ціль `Lookup` (`UpsertRegistryEntryHandler.cs:189-282`) | складені ключі, гарантія в БД, перевірка при записі, імпорті й пакеті, відповідь 409 з деталями |
| 3 | Зв'язки між таблицями (1:N) | **Частково** | поле `Lookup` + `FK_RegValue_Ref` (`DictionariesConfiguration.cs:104-105`); ціль перевіряється (`UpsertRegistryEntryHandler.cs:250-253`, `:303-347`); видалення блокується, якщо запис використовують (`RegistryStore.cs:180-211`, `RegistryAdminHandlers.cs:382-405`) | відношення «композиція» (дочірні рядки як частина батька), каскад, master-detail. Зв'язки в DTO лише похідні (`Cascade`/`Hierarchy`/`Association`, `RegistryDefinitionHandlers.cs:95-129`); `RegistryRelationDef` з `ФВ-8.4` у схемі не існує (`RegistryEntryLink.cs:12-15`) |
| 4 | M:N | **Частково** | `dic.RegistryEntryLink` (`RegistryEntryLink.cs:17-59`) — лише схема й читання видів; маршрутів у `RegistriesController.cs` немає | у першій ітерації не потрібне (`R-7`) |
| 5 | Використання в методологіях | **Немає** | `MethodologyEvaluationContext.GetRegistryField` → `#REF` (`MethodologyEvaluationContext.cs:112-124`); `Lookup`-аргумент приходить порожнім: у `CalculationArgument` немає місця під id запису (`CalculationInputBuilder.cs:58-72`, `ICalculationModule.cs:190-194`); функцій довідника в каталозі немає (`DialectCatalog.cs:63-112`); контракт: «у діалекті B немає діапазонів» (`docs/build/02b-expressions.md:685-688`) | пошук за ключем, читання поля, агрегати по рядках, знімок на дату |
| 6 | Використання у формулах шаблону | **Частково** | `REGFIELD(lookup,'field')` — `FunctionRegistry.cs:67`, `TemplateFunctions.cs:294-336` | є лише читання через `Lookup`-комірку. Немає пошуку за ключем і агрегатів; поля типів `Lookup`/`Unit` не читаються (`RecalculationService.cs:1289-1313`); код поля при публікації не звіряється (видобувач мовчить — `DependencyExtractor.cs:212-226`) |
| 7 | Відтворюваність (історія значень) | **Частково** | одне значення на поле — `UQ_RegistryValue` (`DictionariesConfiguration.cs:97-98`), правка перезаписує; слід змін є лише як JSON-подія `RegistryValueChanged` в `aud.SecurityEvent` (`UpsertRegistryEntryHandler.cs:53`, `:127-140`; CSV — `RegistryEntryCsvHandlers.cs:329-343`); вікно чинності є лише на записі (`RegistryEntry.cs:53-67`) | стан «станом на» (системний час), автор кожної зміни, знімок на прогін розрахунку |
| 8 | Застарілість результатів після зміни довідника | **Немає** | свіжість (`F-05`) дивиться лише на `aud.CellChange` (`MethodologyStore.cs:385-414`) | позначка «довідник змінено після розрахунку» + явний перерахунок зачеплених |
| 9 | «Де використано» | **Частково** | `RegistryStore.cs:214-291`: колонки шаблонів, поля довідників, речовини методологій, сутності джерел, комірки | формули шаблону, формули методологій, правила довідників |
| 10 | Правила довідника | **Частково** (лише зберігаються) | `RegistryRuleDef.cs:22-175`, чотири види — `Enums.cs:622-644`, `CK_RegRule_Kind` — `ConfigurationRestConfiguration.cs:311-312` | рушій правил; агрегатна перевірка по дочірніх рядках |
| 11 | Редактор записів | **Частково** | форма: усі поля — `TextInput`, `Lookup` вводиться сирим id (`RegistryEntryEditor.tsx:218-245`); перелік показує лише код, назву, батька й чинність (`RegistriesPage.tsx:211-241`), у `RegistryEntryDto` значень немає (`RegistryEntryDto.cs:12-18`) | табличний редактор: типізовані поля, пікери, вставка блоком, master-detail, Σ |
| 12 | Імпорт | **Частково** | CSV: стеля 1 МБ (`RegistryEntryCsvHandlers.cs:65`), дубль коду у файлі — помилка (`:204-207`), наявний код — оновлення (`:216`), назва нового запису = код (`:228-233`), усе або нічого (`:286-289`), `Lookup` резолвиться за кодом цілі (`:497`) | XLSX, транспонований файл, розгортання стовпців у рядки, зіставлення стовпців, прев'ю різниці. Потрібен файл > 1 МБ: `Add Stream_v1_3.xlsx` важить 2.3 МБ |
| 13 | Експорт | **Немає** | маршрутів експорту в `RegistriesController.cs` немає | CSV/XLSX |
| 14 | Автодоповнення в редакторі формул | **Немає** | `GetExpressionMetadataHandler.cs:46-86` віддає функції, константи, формули, аргументи й шапку; `completion.ts:128` — тригери без довідників | коди довідників, поля, ключі, ланцюжки `Lookup`, одиниці |

### 1.2 Знайдені дефекти

| # | Дефект | Доказ | Де виправляється |
|---|---|---|---|
| Д-1 | `REGFIELD` у правилах валідації документа завжди дає `#REF`: контексти створюються без знімка довідника | `ValidationEngine.cs:223`, `:235` (конструктор бази без `registryFields`, `ValidationEvaluationContext.cs:36-38`) | **не тут**: [DIRECTIVE-16 §2 D16-04](DIRECTIVE-16.md) — **виконано в `5f00b791`** («Перевірити», подання) і **`400c58b1`** (шлях збереження, §5.12). Нові функції (§5) користуються **тим самим** механізмом передачі знімка; крок `RT-24` залежить від D16-04 |
| Д-2 | Незаповнене поле довідника в `REGFIELD` дає `#REF` замість `null`, а це суперечить `02b` §6.3 (`docs/build/02b-expressions.md:356-365`) | `SliceEvaluationContext.cs:267-271` + пропуск при завантаженні `RecalculationService.cs:1265-1271` | `RT-24`, рішення `R-11` — **виконано** (`da57cdab`, `4c544827`; Lookup/Unit читаються, порожнє поле існуючого запису → `null`) |
| Д-3 | Гонку на унікальному індексі мапить лише гілка для `Code`; будь-який інший унікальний індекс довідника дасть голий 500 | `UnitOfWork.cs:158-180`, фолбек `:76-79` | `RT-10b` |
| Д-4 | Правила `RequiredWhen`/`UniqueWithin`/`Expression`/`CrossRegistry` виглядають налаштованими, але не спрацьовують ніколи. Це той самий клас дефекту, від якого застерігає коментар самої сутності: «правило, якого рушій не знає, … виглядає налаштованим» | `RegistryRuleDef.cs:56-59`; виконавця немає (див. §1.1 п. 2) | `RT-17a` |
| Д-5 | Код поля в `REGFIELD` не звіряється під час публікації, тож помилка проявляється лише як `#REF` у рантаймі | `DependencyExtractor.cs:221-226` | `RT-21` |
| Д-6 | `RegistryEntry.Restore()` не має викликача в `Ecr.Application`: помилково видалений запис відновити неможливо | `RegistryEntry.cs:195-201`; пошук `\.Restore\(\)` у `src/Ecr.Application/Registries` порожній | поза обсягом (§11.5) |

### 1.3 Уточнення до вихідного аудиту

| Твердження аудиту | Результат перевірки |
|---|---|
| «`IsKey` — лише метадані (`ScopeJson`)» | **Неточно.** Опис без жодного `IsKey` відхиляється (`RegistryDefinitionHandlers.cs:336-347`), а після створення поля ознака незмінна (`RegistryFieldDef.cs:42-49`, `:69`) |
| «Значення не темпоральні (перезапис)» | Так, але **слід змін є**: JSON-подія в `aud.SecurityEvent` на кожен ручний запис і на кожен рядок CSV. Для відтворення «станом на» він непридатний |
| «Lookup вводиться сирим Id» | Так для форми. CSV резолвить `Lookup` **за кодом** цілі (`RegistryEntryCsvHandlers.cs:497`) |
| «Знімок AddStream: 64 поля, 45 компонентів» | 64 поля даних (`C:BN`) — так. Стовпців складу **49**: 46 різних компонентів і 3 дублі (`Carbon_Monoxide`/`CO`, `Hydrogen`/`H2`, `Oxygen`/`O2`). Перевірено за рядком заголовка `Current_20250805!C2:BN2` |
| Решта (EAV, D-13/14/66, `UQ_RegistryEntry`, `#REF` у методології, `ECR-CALC-0433`, F-05, D-50, I-3) | Підтверджено за вказаними рядками |

---

## 2. Цілі, не-цілі, сценарії

### 2.1 Цілі

| # | Ціль | Як перевіримо |
|---|---|---|
| G-1 | Будь-яка комбінація полів довідника може бути унікальною, і гарантує це **база** | два одночасні записи з однаковим ключем → рівно один `201`, другий — `409 ECR-REG-4092` |
| G-2 | Три пов'язані довідники описуються як «батько → частини» й редагуються як одна таблиця master-detail | сценарій С-1 без жодного введення id руками |
| G-3 | Методологія і шаблон знаходять запис за складеним ключем, читають поле з переходом через посилання й сумують по рядках | золотий тест 301 (§10, AC-1) |
| G-4 | Повторний розрахунок минулого періоду дає той самий результат | `replay` прогону `AS OF RegistryAsOfUtc` відтворює числа побітно (AC-7) |
| G-5 | Зміна довідника робить результати застарілими й пропонує перерахунок **лише** відкритих періодів | AC-8 |
| G-6 | Правила довідника виконуються, рівень (попередження / блок) обирає адміністратор | AC-5 |
| G-7 | Імпорт транспонованого експорту HYSYS і плоского знімка FLERT без Excel-макросів | AC-10 |

### 2.2 Не-цілі

| # | Не-ціль | Чому |
|---|---|---|
| N-1 | Нова сутність «таблиця» поруч із довідником | `ФВ-8.1` («один механізм»); другий механізм розійшовся б із першим на першій правці (`R-1`) |
| N-2 | Фізичні таблиці під кожен довідник | `D-13`, `D-14`, `D-66`: сервіс DDL не виконує |
| N-3 | Обчислювані поля всередині довідника (wt% тощо) | `RegistryValue.cs:122-131`: обчислене значення належить документу або методології |
| N-4 | Редактор M:N (`RegistryEntryLink`) | замінює «довідник-зв'язка» (`R-7`) |
| N-5 | Автоматичний перерахунок після зміни довідника | `D-39`: закриті періоди — ніколи; відкриті — явною дією (`R-14`) |
| N-6 | Зворотна синхронізація з `FLERT.dbo` | `D-50`: master — ECR; `R-26` |
| N-7 | Функції довідників у версіях `Legacy` | чинний рушій їх не рахував: `ECR-CALC-0433` (`02b-expressions.md:726-731`) |
| N-8 | Пошук запису на дату події (`REGFINDAT`) і зведений вигляд «компоненти в стовпцях» | наступна ітерація (§11.5, `RT-37`) |

### 2.3 Сценарії

**С-1. Адміністратор довідників заводить «Потоки FLERT»** (§3.6). Створює чотири довідники, в
`STREAM_CASE` задає ключ «Потік + Кейс HMB», у `GAS_COMPOSITION` — «Кейс + Компонент», позначає
поля `STREAM_CASE.STREAM` і `GAS_COMPOSITION.CASE` як «композицію». Правило «Σ мол.% = 100 ± 0.5»
ставить на рівні «попередження». Імпортує `Current_20250805` (плоский) і `streams Offshore`
(транспонований), переглядає різницю й застосовує. Файл `Add Stream` стає архівом (`D-50`).

**С-2. Методолог пише формули 301.** Знаходить кейс через `REGFIND` за парою (потік, кейс), рахує
μ, S мас.% і EF через `REGSUM` по рядках складу з переходом `ROW.COMPONENT.MW`. Автодоповнення
підказує довідники, поля й одиниці, кнопка «Перевірити на записі» показує число для (1D-2,
370 Winter). Публікація відхиляє описку в коді поля ще до прогону.

**С-3. Еколог-інженер оновлює склад після ревізії HMB.** Відкриває потік 1D-2 → кейс 370 Winter →
рядки складу, вставляє з Excel стовпець мол.%. Бачить живий Σ і підсвічений дубль компонента ще до
збереження, зберігає. Банер повідомляє: «2 документи відкритих періодів пораховані до цієї зміни»,
кнопка ставить перерахунок у чергу.

---

## 3. Модель даних

### 3.1 Огляд

```
cfg.RegistryDef ──1:N── cfg.RegistryFieldDef        (+ RelationKind, OnParentDelete)       [M2]
   │ (+ CodeMode, DataChangedAt) [M2]
   ├──1:N── cfg.RegistryKeyDef ──1:N── cfg.RegistryKeyField ──N:1── cfg.RegistryFieldDef    [M1]
   ├──1:N── cfg.RegistryRuleDef                     (без змін схеми)
   ├──1:N── cfg.RegistryImportProfile                                                        [M5]
   └──1:N── cfg.RegistryUse   ← хто посилається на довідник з формул і правил                [M4]
dic.RegistryEntry ──1:N── dic.RegistryValue         (SYSTEM_VERSIONING → *History)          [M3]
   └──1:N── dic.RegistryEntryKey  ← нормалізований хеш значень ключа; UX у БД               [M1]
calc.CalculationRun (+ RegistryAsOfUtc)                                                      [M4]
```

### 3.2 Зміни схеми

Схема змінюється **міграціями EF** (`D-14`). Розміщення історичних таблиць по файлових групах —
справа DBA через `Sql/*.sql` (`02a-db-schema.md:42-43`).

```sql
-- M1 · RK01RegistryKeys (крок RT-01)
CREATE TABLE cfg.RegistryKeyDef
(
    Id              int           IDENTITY(1,1) NOT NULL,
    RegistryDefId   int           NOT NULL,
    Code            nvarchar(64)  NOT NULL,          -- EcrCode: PK, BY_LEGACY_ID
    NameL10n        nvarchar(max) NOT NULL,
    IsPrimary       bit           NOT NULL CONSTRAINT DF_RegKey_Pri DEFAULT(0),
    IgnoreCase      bit           NOT NULL CONSTRAINT DF_RegKey_Case DEFAULT(1),
    IsActive        bit           NOT NULL CONSTRAINT DF_RegKey_Act DEFAULT(1),
    CreatedAt       datetime2(3)  NOT NULL,
    CreatedByUserId int           NOT NULL,
    CONSTRAINT PK_RegistryKeyDef PRIMARY KEY (Id),
    CONSTRAINT UQ_RegistryKeyDef UNIQUE (RegistryDefId, Code),
    CONSTRAINT FK_RegKey_Def FOREIGN KEY (RegistryDefId) REFERENCES cfg.RegistryDef (Id)
);
-- ⛔ Первинний ключ — рівно один активний на довідник
CREATE UNIQUE INDEX UX_RegistryKeyDef_Primary ON cfg.RegistryKeyDef (RegistryDefId)
    WHERE IsPrimary = 1 AND IsActive = 1;

CREATE TABLE cfg.RegistryKeyField
(
    RegistryKeyDefId   int     NOT NULL,
    Ordinal            tinyint NOT NULL,              -- порядок = порядок аргументів REGFIND
    RegistryFieldDefId int     NOT NULL,
    CONSTRAINT PK_RegistryKeyField PRIMARY KEY (RegistryKeyDefId, Ordinal),
    CONSTRAINT UQ_RegistryKeyField_Field UNIQUE (RegistryKeyDefId, RegistryFieldDefId),
    CONSTRAINT FK_RegKeyField_Key   FOREIGN KEY (RegistryKeyDefId)   REFERENCES cfg.RegistryKeyDef (Id),
    CONSTRAINT FK_RegKeyField_Field FOREIGN KEY (RegistryFieldDefId) REFERENCES cfg.RegistryFieldDef (Id)
);

CREATE TABLE dic.RegistryEntryKey                    -- похідні дані, D-71: відтворювані
(
    Id               bigint        IDENTITY(1,1) NOT NULL,
    RegistryEntryId  int           NOT NULL,
    RegistryKeyDefId int           NOT NULL,
    KeyHash          binary(32)    NOT NULL,          -- SHA-256 канонічного рядка (§4.2)
    KeyText          nvarchar(900) NOT NULL,          -- «1D-2 · 370 Winter» — для повідомлень
    ValidFromKey     date          NOT NULL,          -- ISNULL(ValidFrom, '0001-01-01')
    ValidTo          date          NULL,
    IsLive           bit           NOT NULL,          -- NOT IsDeleted
    CONSTRAINT PK_RegistryEntryKey PRIMARY KEY (Id),
    CONSTRAINT UQ_RegistryEntryKey_Entry UNIQUE (RegistryKeyDefId, RegistryEntryId),
    CONSTRAINT FK_RegEntryKey_Entry FOREIGN KEY (RegistryEntryId)  REFERENCES dic.RegistryEntry (Id),
    CONSTRAINT FK_RegEntryKey_Key   FOREIGN KEY (RegistryKeyDefId) REFERENCES cfg.RegistryKeyDef (Id)
);
-- Гарантія БД: для нетемпорального довідника ValidFromKey завжди '0001-01-01' → повна унікальність
CREATE UNIQUE INDEX UX_RegistryEntryKey_Live ON dic.RegistryEntryKey (RegistryKeyDefId, KeyHash, ValidFromKey)
    WHERE IsLive = 1 ON [INDEXES];
-- Пошук і діапазонне блокування при перевірці перетину вікон (§4.4)
CREATE INDEX IX_RegistryEntryKey_Hash ON dic.RegistryEntryKey (RegistryKeyDefId, KeyHash)
    INCLUDE (RegistryEntryId, ValidFromKey, ValidTo, IsLive) ON [INDEXES];

-- M2 · RK02RegistryComposition (крок RT-03)
ALTER TABLE cfg.RegistryFieldDef ADD
    RelationKind   tinyint NOT NULL CONSTRAINT DF_RegField_Rel   DEFAULT(0),   -- 0 Reference, 1 Composition
    OnParentDelete tinyint NOT NULL CONSTRAINT DF_RegField_OnDel DEFAULT(0),   -- 0 Restrict, 1 Cascade
    CONSTRAINT CK_RegField_Rel CHECK (RelationKind BETWEEN 0 AND 1 AND OnParentDelete BETWEEN 0 AND 1),
    CONSTRAINT CK_RegField_Composition CHECK (RelationKind = 0 OR DataType = 5);  -- лише Lookup
ALTER TABLE cfg.RegistryDef ADD
    CodeMode      tinyint      NOT NULL CONSTRAINT DF_RegDef_CodeMode DEFAULT(0),  -- 0 Manual, 1 Auto
    DataChangedAt datetime2(3) NULL;       -- ставить UnitOfWork разом із DataRevision (§5.10)
CREATE SEQUENCE dic.RegistryEntryCodeSeq AS bigint START WITH 1 INCREMENT BY 1;   -- код «E000012345»

-- M3 · RK03RegistryTemporalHistory (крок RT-04)
ALTER TABLE dic.RegistryEntry ADD ChangedByUserId int NULL,
    PeriodStart datetime2(3) GENERATED ALWAYS AS ROW START HIDDEN NOT NULL
        CONSTRAINT DF_RegEntry_PS DEFAULT SYSUTCDATETIME(),
    PeriodEnd   datetime2(3) GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL
        CONSTRAINT DF_RegEntry_PE DEFAULT CONVERT(datetime2(3), '9999-12-31 23:59:59.999'),
    PERIOD FOR SYSTEM_TIME (PeriodStart, PeriodEnd);
ALTER TABLE dic.RegistryEntry SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dic.RegistryEntryHistory));
-- те саме для dic.RegistryValue → dic.RegistryValueHistory (+ ChangedByUserId)

-- M4 · RK04RegistryUseAndRunAsOf (крок RT-05)
CREATE TABLE cfg.RegistryUse
(
    Id            bigint        IDENTITY(1,1) NOT NULL,
    SourceKind    tinyint       NOT NULL,   -- 0 TemplateFormula(FormulaDefId), 1 MethodologyVersion, 2 RegistryRule
    SourceId      int           NOT NULL,   -- поліморфний, як FormulaDependency.SourceKind (FormulaDependency.cs:82-86)
    FormulaCode   nvarchar(64)  NULL,       -- код формули в межах версії методології
    RegistryDefId int           NOT NULL,
    FieldPath     nvarchar(400) NULL,       -- 'COMPONENT.MW'; NULL — довідник цілком (REGFIND/агрегат)
    CONSTRAINT PK_RegistryUse PRIMARY KEY (Id),
    CONSTRAINT CK_RegUse_Kind CHECK (SourceKind BETWEEN 0 AND 2),
    CONSTRAINT FK_RegUse_Def FOREIGN KEY (RegistryDefId) REFERENCES cfg.RegistryDef (Id)
);
CREATE INDEX IX_RegistryUse_Registry ON cfg.RegistryUse (RegistryDefId) INCLUDE (SourceKind, SourceId, FormulaCode, FieldPath);
CREATE INDEX IX_RegistryUse_Source   ON cfg.RegistryUse (SourceKind, SourceId);
ALTER TABLE calc.CalculationRun ADD RegistryAsOfUtc datetime2(3) NULL;   -- момент знімка довідників

-- M5 · RK05RegistryImportProfile (крок RT-06)
CREATE TABLE cfg.RegistryImportProfile
(
    Id              int           IDENTITY(1,1) NOT NULL,
    RegistryDefId   int           NOT NULL,
    Code            nvarchar(64)  NOT NULL,
    NameL10n        nvarchar(max) NOT NULL,
    SpecJson        nvarchar(max) NOT NULL,           -- RegistryImportSpec (§4.6)
    UpdatedAt       datetime2(3)  NOT NULL,
    UpdatedByUserId int           NOT NULL,
    RowVersion      rowversion    NOT NULL,
    CONSTRAINT PK_RegistryImportProfile PRIMARY KEY (Id),
    CONSTRAINT UQ_RegistryImportProfile UNIQUE (RegistryDefId, Code),
    CONSTRAINT CK_RegImpProfile_Json CHECK (ISJSON(SpecJson) = 1),
    CONSTRAINT FK_RegImpProfile_Def FOREIGN KEY (RegistryDefId) REFERENCES cfg.RegistryDef (Id)
);
```

⚠ `PeriodStart`/`PeriodEnd` мають тип `datetime2(3)`, а не типовий для EF `datetime2(7)`: так
вимагає `D-68`. Дві зміни одного рядка в межах однієї мілісекунди дають у SQL Server історичний
рядок нульової тривалості. Це допустимо, бо на жоден момент «станом на» такий рядок не видно.

⛔ Кожну `CREATE TABLE` треба внести в `docs/build/02a-db-schema.md` §3/§5 **у тому самому кроці**,
що й міграцію, історичні таблиці `dic.*History` включно. Сторож
`PhysicalModelTests.Кожна_таблиця_контрактної_схеми_існує_або_явно_відкладена`
(`PhysicalModelTests.cs:269-317`) читає `CREATE TABLE` з 02a і вимагає, щоб таблиця існувала. Якщо
внести рядок раніше за міграцію, сторож почервоніє.

### 3.3 EF-сутності

| Сутність | Файл (новий, якщо не сказано інше) | Примітки |
|---|---|---|
| `RegistryKeyDef`, `RegistryKeyField` | `src/Ecr.Domain/Entities/Configuration/` | поля ключа й прапорці змінюються **лише** створенням нового ключа (як `RegistryFieldDef.Update`, `RegistryFieldDef.cs:42-49`); вимкнути ключ — `SetActive(false)` |
| `RegistryEntryKey` | `src/Ecr.Domain/Entities/Dictionaries/` | будує лише `RegistryKeyService`, руками не пишеться |
| `RegistryUse` | `src/Ecr.Domain/Entities/Configuration/` | переписується повністю під час публікації джерела |
| `RegistryImportProfile` | `src/Ecr.Domain/Entities/Configuration/` | `SpecJson` перевіряється на синтаксис, як `RegistryRuleDef.SetParameters` (`RegistryRuleDef.cs:115-143`) |
| `RegistryFieldDef` (змінна) | наявний | + `RelationKind`, `OnParentDelete`; ставляться **лише при створенні** поля, як `PointTo` (`RegistryFieldDef.cs:72-78`) |
| `RegistryDef` (змінна) | наявний | + `CodeMode` (лише при створенні), `DataChangedAt` (ставить `UnitOfWork`, коли зросла `DataRevision`) |
| `RegistryEntry`, `RegistryValue` (змінні) | наявні | + `ChangedByUserId` (ставить `UnitOfWork` з `ICurrentUser`); `.IsTemporal(...)` у `DictionariesConfiguration.cs` |
| `CalculationRun` (змінна) | наявний | + `RegistryAsOfUtc` = момент старту прогону |
| Нові enum | `src/Ecr.Domain/Enums/Enums.cs` — **у кінець файлу** | `RegistryRelationKind`, `ParentDeletePolicy`, `RegistryCodeMode` |

### 3.4 Міграції — по одній на крок

| Міграція | Крок | Що | Відкат |
|---|---|---|---|
| `RK01RegistryKeys` | RT-01 | 3 таблиці ключів, 3 індекси | `Down` видаляє таблиці; даних, яких не можна відтворити, у них немає |
| `RK02RegistryComposition` | RT-03 | 4 стовпці, 2 CHECK, послідовність | `Down` видаляє |
| `RK03RegistryTemporalHistory` | RT-04 | системна історія на 2 таблицях, `ChangedByUserId` | `Down`: `SYSTEM_VERSIONING = OFF`, `DROP PERIOD`, історичні таблиці **перейменовуються**, а не видаляються (`D-25`) |
| `RK04RegistryUseAndRunAsOf` | RT-05 | `cfg.RegistryUse`, `calc.CalculationRun.RegistryAsOfUtc` | `Down` видаляє |
| `RK05RegistryImportProfile` | RT-06 | `cfg.RegistryImportProfile` | `Down` видаляє |

### 3.5 Сумісність із наявними даними

| Що є | Що станеться | Рішення |
|---|---|---|
| Довідники без ключів | поводяться як сьогодні: унікальний лише `Code` | ключ вмикається явно, після перевірки дублікатів (§4.5) |
| Правила `UniqueWithin` | не виконувались ніколи; нові відхиляються (`err.ECR-REG-0422.uniqueWithinReplacedByKeys`), наявні лишаються з банером «перетворити на ключ» в один клік (§4.7) | **без автоматичної конвертації** (`R-5`): на даних можуть бути дублікати |
| `IsKey` / `noKeyField` | семантика `IsScopeField` не змінюється; вимога «≥ 1 `IsKey`» послаблюється до «≥ 1 `IsKey` **або** активний первинний ключ» | перейменування `IsKey → IsScopeField` — окремий рефакторинг (§11.5) |
| Формули з `REGFIELD` | незаповнене поле дає `null`, а не `#REF` (Д-2); поля `Lookup`/`Unit` тепер читаються | перед RT-24 порахувати формули з `REGFIELD` у корпусі (`git grep`/запит до `cfg.FormulaDef`) і вписати число в опис кроку; у коментарі `RecalculationService.cs:1148` сказано «одиниці» |
| Методології `Legacy` | не змінюються побітно: `Lookup`-аргумент і далі порожній | `R-12`: id запису передається лише версіям `Strict` |
| Історія значень до міграції M3 | системного часу до міграції немає: `AS OF` на момент раніше за міграцію порожній | прогони з `RegistryAsOfUtc = NULL` відтворюються на поточних даних **з попередженням у трейсі** |

### 3.6 Відтворюваність: вибір механізму

| Варіант | Суть | За | Проти |
|---|---|---|---|
| **A. Системна історія SQL Server (temporal), створена міграцією** | `dic.RegistryEntry`/`dic.RegistryValue` з `SYSTEM_VERSIONING`, у прогоні — `RegistryAsOfUtc`, читання `FOR SYSTEM_TIME AS OF` | історію пише база на **кожному** шляху запису: ручному, CSV, імпорті, синку, прямому SQL; EF 10 підтримує `TemporalAsOf`; доступно у Standard 2016 SP1+ (`D-28`, `docs/tz/10-decisions.md:54`); DDL виконується лише під час розгортання (`D-14`) | перша temporal-таблиця в системі; `ALTER` таких таблиць у майбутніх міграціях вимагає обережності; історія росте (`D-25` — нічого не видаляємо; обсяг малий, десятки тисяч рядків) |
| B. Застосунок веде історію сам | окрема append-only таблиця | переносно | кожен шлях запису мусить про неї пам'ятати; синк і масова вставка обходять домен (`DictionariesConfiguration.cs:32-35` прямо про це попереджає) |
| C. Знімок значень у результаті | прогін копіює прочитані значення довідника в `calc.*` | пояснюваність | ~10 тис. рядків на кожен прогін; дублювання; трейс рівня `Full` це вже частково дає (`D-27`) |

**Рішення `R-9`: варіант A.** Бізнес-чинність (`ValidFrom`/`ValidTo`, `ФВ-8.5`) і системний час
розділені, як у `B19` §3 (`docs/reference/backend/B19-calc-configurator.md:84-117`):

- **бізнес-дата** знімка — **останній день періоду**. Це та сама дата, яку сітка документа передає
  пікеру `Lookup` (`DocumentGrid.tsx:766-776`), тож формула й пікер бачать однаковий набір записів;
- **системний момент** — `CalculationRun.RegistryAsOfUtc`, він ставиться на старті прогону. Усі
  довідники одного прогону читаються `AS OF` одного моменту, тому змішаного стану не буває.
  Повторити прогін (`replay`) означає прочитати `AS OF` той самий момент.

---

## 4. Унікальність і зв'язки

### 4.1 Визначення ключа

| Властивість | Правило |
|---|---|
| Склад | упорядкований список 1–8 полів довідника. Типи: `String`, `Int`, `Decimal`, `Bool`, `Date`, `Lookup`, `Unit` |
| Первинний (`IsPrimary`) | ≤ 1 активний на довідник (`UX_RegistryKeyDef_Primary`). Усі його поля — `IsRequired` (`err.ECR-REG-0422.keyFieldNotRequired`). Саме його використовує `REGFIND` |
| Альтернативні | довільна кількість; рядок, у якому хоч одна частина ключа `null`, у перевірку не входить (як `UNIQUE` з різними `NULL`) (`R-4`) |
| Якщо первинного ключа немає | `REGFIND` шукає за `Code` запису, однією частиною (§5.4) |
| Зміна | склад, `IgnoreCase` і `IsPrimary` не змінюються. Потрібен інший ключ — заводять новий, старий вимикають. Причина та сама, що для `IsKey` (`RegistryFieldDef.cs:42-49`): зміна мовчки перебудувала б хеш кожного запису |
| Де задається | у повному стані опису (`PUT …/definition`, чернетка, публікація — `BE-24`), поле `keys[]` поруч із `fields[]`/`rules[]` |

### 4.2 Канонічний рядок і хеш

`RegistryKeyNormalizer` (`src/Ecr.Domain/Services/`) — **одна** реалізація на сервері й **одна** на
клієнті (`features/registries/keys/normalizeKey.ts`). Обидві проганяються на спільній фікстурі
`tests/Ecr.TestKit/Fixtures/registry-key-normalization.json`, так само як
`expression-equivalence.json` читають і C#, і клієнт (`ClientServerEquivalenceTests.cs:208`,
`src/Ecr.Web/src/shared/__tests__/formula-equivalence.test.ts:23`).

| Тип частини | Канонічна форма (`тег:значення`) | Приклад |
|---|---|---|
| `String` | `S:` + NFC → обрізка → послідовність пробільних символів стає одним пробілом → (якщо `IgnoreCase`) `ToUpperInvariant` | `' 370  winter'` → `S:370 WINTER` |
| `Int`, `Decimal` | `N:` + інваріантний десятковий без хвостових нулів; `-0` → `0` | `49.9999977539011000` → `N:49.9999977539011` |
| `Bool` | `B:1` / `B:0` | |
| `Date` | `D:yyyy-MM-dd` | |
| `Lookup` | `L:` + **id** цільового запису: перейменування цілі ключ не змінює (`ФВ-8.8`) | `L:162` |
| `Unit` | `U:` + id одиниці | |

Частини з'єднуються символом U+001F (unit separator) у порядку `Ordinal`, результат хешується
SHA-256 від UTF-8. `KeyText` — людський вигляд: текст як введено, число інваріантне, `Lookup` —
назва цілі (`1D-2 · 370 Winter`). Тег типу не дає `'1'` (текст) і `1` (число) зійтися в один ключ.

### 4.3 Запис: алгоритм і гонки

```
Upsert / Batch / CSV / Import (одна точка: UpsertRegistryEntryHandler.ApplyValuesAsync, як сьогодні)
 1. ApplyValuesAsync(...)                  — тип, обов'язковість, ціль Lookup (без змін)
 2. RegistryKeyService.Compute(entry)      — для кожного активного KeyDef: частини → хеш; PK з null-частиною вже
                                             відхилив крок 1 (поле PK обов'язкове)
 3. У межах пакета: група (KeyDef, хеш) > 1 з перетином вікон → помилка рядка keyDuplicateInBatch (обидва рядки)
 4. uow.ExecuteInTransactionAsync:
      SELECT RegistryEntryId, ValidFromKey, ValidTo FROM dic.RegistryEntryKey WITH (UPDLOCK, HOLDLOCK)
       WHERE RegistryKeyDefId = @k AND KeyHash = @h AND IsLive = 1 AND RegistryEntryId NOT IN (@пакет)
      знайдено й вікна перетинаються → 409 ECR-REG-4092 keyTaken | keyWindowOverlap
      upsert рядків dic.RegistryEntryKey; SaveChanges
 5. Гонку, яку не закрив крок 4, ловить UX_RegistryEntryKey_Live → UnitOfWork.TryMapDuplicateKey:
      гілка RegistryEntryKey → 409 ECR-REG-4092 keyTakenConcurrently
```

- ⚠ `UPDLOCK, HOLDLOCK` має сенс лише всередині транзакції. Сьогодні `UpsertRegistryEntryHandler`
  зберігає через `uow.SaveChangesAsync` без явної транзакції (`UpsertRegistryEntryHandler.cs:117`),
  тому крок 4 обгортає запис у `ExecuteInTransactionAsync` (вже використовується:
  `RegistryDefinitionHandlers.cs:361-382`). Прецедент блокування — `DocumentKeyStore.IsKeyTakenAsync`
  (`UnitOfWork.cs:103-105`).
- ⚠ **Обмін ключами в одному пакеті** (A: k1→k2, B: k2→k1): SQL Server перевіряє унікальність на
  кожну інструкцію, тож порядок `UPDATE` від EF може тимчасово порушити індекс. Тому ключі
  переписуються у дві фази: спершу `IsLive = 0` для змінених рядків ключа, потім нові значення.
- 409 несе деталі: `key` (код ключа), `keyText`, `values` (поле → значення), `entryId` і `entryCode`
  конфліктного запису. Для `keyTakenConcurrently` відомий лише переможений запис — та сама
  причина, що в `UnitOfWork.cs:165-172`.

### 4.4 Темпоральність

- Для довідника з `IsTemporal = true` ключ унікальний **у кожен момент бізнес-часу**: два записи з
  однаковим хешем припустимі, якщо їхні напівінтервали `[ValidFrom, ValidTo)` не перетинаються. Умова
  перетину — та сама, що `ValidityWindow.OverlapsSegment` (`ValidityWindow.cs:97-99`), другої
  реалізації не пишемо.
- Гарантія БД для темпорального довідника **часткова**: індекс ловить лише точний збіг початку
  (`ValidFromKey`). Перетин перевіряє крок 4 під діапазонним блокуванням. Навіть якщо рядок змінили
  повз застосунок, це помітить нічна перевірка інваріантів (`ФВ-7.7`), до якої додається
  `RegistryKeyOverlapCheck` (§11.5).
- Зміна вікна (`SetEntryValidityHandler`) повторно перевіряє перетин для всіх ключів запису.

### 4.5 Ввімкнення ключа на наявних даних

1. `POST …/keys/check` (живий, до збереження) і публікація опису виконують **той самий** алгоритм:
   читають значення полів ключа всіх живих записів, будують хеші в пам'яті (на FLERT це ≈ 10 тис.
   рядків) і групують (з перетином вікон для темпорального довідника).
2. Є групи > 1 — публікація відхиляється: `409 ECR-REG-4092 existingDuplicates`, `groups`, до 20
   прикладів `{keyText, entries[]}`. Конструктор показує ці приклади з посиланням «Показати в
   редакторі» (§8.3).
3. Груп немає — рядки `dic.RegistryEntryKey` заповнюються в **тій самій** транзакції, що й опис.

### 4.6 CSV, пакет, імпорт

| Шлях | Ключ |
|---|---|
| `POST …/entries/import` (наявний CSV) | дубль ключа у файлі дає помилку рядка `keyDuplicateInFile` з номерами обох рядків. Для довідника з первинним ключем і `CodeMode = Auto` стовпець `code` необов'язковий: наявний запис знаходиться **за первинним ключем** |
| `POST …/entries/batch` | кроки 1–5 §4.3, один звіт на пакет; `dryRun=true` нічого не пише |
| `POST …/import/preview`, `…/import/apply` | той самий конвеєр, що й пакет. Порядок: батьки → діти (композиція); ключ дитини, яка посилається на ще не збереженого батька, будується за **тимчасовим id батька в плані** |

`RegistryImportSpec` (JSON, зберігається профілем):

```jsonc
{
  "sheet": "streams Offshore",            // для xlsx
  "orientation": "Columns",              // Rows | Columns (транспонований HYSYS)
  "headerRows": 2, "firstData": 3,        // рядки заголовка (№ потоку, HMB) і перший рядок даних
  "targets": [
    { "registry": "STREAM",      "match": "Key:BY_NUMBER_ONSHORE", "fields": { "NUMBER": "#1", "IS_ONSHORE": "=false" } },
    { "registry": "STREAM_CASE", "match": "Key:PK", "fields": { "STREAM": "@STREAM", "CASE_NAME": "#2", "T_C": "Temperature" } },
    { "registry": "GAS_COMPOSITION", "match": "Key:PK",
      "unpivot": { "sources": "Nitrogen..Methanol", "keyField": "COMPONENT", "keyMatch": "COMPONENT.HYSYS_NAME",
                   "valueField": "MOL_PCT", "skipZero": true },
      "fields": { "CASE": "@STREAM_CASE" } }
  ],
  "duplicateSources": "MustMatch"         // Carbon_Monoxide і CO → той самий компонент: значення мусять збігтися
}
```

`#n` — рядок заголовка, `@X` — запис, знайдений ціллю `X` у тому самому рядку або стовпці, `=v` —
константа. `duplicateSources = MustMatch`: якщо два джерела заповнюють одне поле (дублі
`Carbon_Monoxide`/`CO` у `Current_20250805`) і їхні значення розходяться більш ніж на 1e-12, рядок
отримує помилку `importDuplicateSourceMismatch`. Саме ці дублі дають хибну Σ 102–109 % в Excel
(`excel-analysis.md:382-391`).

### 4.7 `UniqueWithin` → ключі

`UniqueWithin` з параметрами `{field, within[]}` — це рівно альтернативний ключ `within[] + field`.
Автоматично не конвертується (`R-5`): на даних можуть бути дублікати, а правило досі не виконувалось.
Конструктор показує банер «Правило X ніколи не перевірялось. Перетворити на ключ?». Дія запускає
§4.5, у разі успіху створює ключ і вимикає правило (`SetActive(false)`, `RegistryRuleDef.cs:174`).
Правило вимикається, а не видаляється, як того вимагає `RegistryDefinitionHandlers.cs:537-544`.

### 4.8 Композиція (master-detail)

| Правило | Деталі |
|---|---|
| Що це | поле `Lookup` дочірнього довідника з `RelationKind = Composition`: рядок **є частиною** батька. Для порівняння: `Reference` — просто посилання (`GAS_COMPOSITION.COMPONENT`) |
| Обмеження опису | рівно одне поле `Composition` на довідник; воно `IsRequired`; ціль — **інший** довідник (ієрархія всередині одного — це `ParentEntryId`); циклів між довідниками немає (DFS по ребрах композиції); `err.ECR-REG-0422.composition*` |
| Час | дочірній довідник **нетемпоральний** (`compositionChildTemporal`). Рядок видно рівно тоді, коли видно батька; ланцюжок перевіряється рекурсивно в `RegistryResolver`. Так версія кейсу HMB автоматично «несе» свій склад |
| Видалення батька | `Restrict` (типово) — наявна відмова `409 ECR-REG-0409 entryReferenced`: `CountReferencesAsync` уже рахує посилання з полів довідників (`RegistryStore.cs:188-195`). `Cascade` — діти логічно видаляються в тій самій транзакції (рекурсивно), їхні рядки ключів отримують `IsLive = 0`. ⚠ Підрахунок посилань іде запитом до БД, де незбережені зміни не видно, тому виклик отримує параметр «виключити дітей композиції цього батька» |
| Рекомендація конструктора | первинний ключ дитини включає поле композиції (`CASE` + `COMPONENT`); конструктор пропонує такий ключ сам |
| Код запису | для довідників із первинним ключем радимо `CodeMode = Auto`: код `E` + 9 цифр послідовності `dic.RegistryEntryCodeSeq` задовольняє `EcrCode` (`^[A-Za-z][A-Za-z0-9_]{0,63}$`, `EcrCode.cs:15`). Код із кириличного ключа («ПК-3 (370-220) лето») зібрати неможливо, бо `EcrCode` приймає лише латиницю |

### 4.9 M:N

**`R-7`: M:N у першій ітерації — довідник-зв'язка.** Це довідник із двома `Lookup`, атрибутами й
первинним ключем по обох полях. `GAS_COMPOSITION` саме такий: кейс × компонент → мол.%. Так атрибути
зв'язку типізовані, мають ключі й правила і доступні з формул тими самими функціями. `RegistryEntryLink`
(`PayloadJson` без схеми, `RegistryEntryLink.cs:58-59`) лишається для наявних каскадів і в цьому
документі не розвивається.

---

## 5. Мова виразів

### 5.1 Принцип

- **Розширення контракту `02b`** (`docs/build/02b-expressions.md:571-574` каже: «набір закритий:
  розширення — зміна контракту»). Цей документ і є запитом на таку зміну, як було з `REGFIELD`
  2026-09-23 (`02b-expressions.md:580-587`).
- **Однакова семантика в `Template` і `Methodology`.** Імена ВЕЛИКИМИ літерами, як у `CONVERT`,
  `SUBSTANCE`, `REGFIELD`. У методологіях регістр значущий (`DialectCatalog.cs:118-125`), тож
  правильне написання лише одне.
- У методологіях функції мають ярус **`Extension`**. У версії `Legacy` це помилка публікації
  `ECR-CALC-0433` (`DialectCatalog.cs:271-273`), бо чинний рушій таких функцій не мав. Діалект
  `Report` їх не отримує: там заборонене все, крім власного рядка (`Parser.cs:595-608`).
- **Жодного звернення до БД під час обчислення** (`02b-expressions.md:782-785`,
  `MethodologyFunctions.cs:28-32`). Знімок довідників завантажується до прогону, як це вже робить
  `REGFIELD` (`RecalculationService.cs:1151-1287`).
- Агрегати — **спеціальні форми** з лінивими аргументами, як `IFERROR` і `if` (`Evaluator.cs:694-712`,
  `:737-748`): фільтр і вираз обчислюються для кожного рядка у власній області `ROW`.
- Це **не** повернення діапазонів у методологію (`02b-expressions.md:685-688`). Агрегація за
  **періодом** лишається роботою звітного рушія. `REGSUM` сумує рядки **довідника** — властивість
  довідкового об'єкта (склад потоку), а не часовий ряд.

### 5.2 EBNF — розширення §1 `02b`

```ebnf
primary        = literal | reference | function_call | row_ref | this_ref | "(" expression ")" ;

row_ref        = "ROW" "." code { "." code } ;
                 (* поле рядка, що перебирається REG-агрегатом або REGONE, або поле запису,
                    який перевіряє правило довідника; усі сегменти, крім останнього, — Lookup-поля *)
this_ref       = "THIS" ;
                 (* лише в правилах довідника: запис, що перевіряється, як EntryRef *)

(* функції довідників — звичайні function_call; їхні аргументи-коди — рядкові літерали: *)
registry_code  = string ;          (* 'STREAM_CASE' — резолвиться при публікації *)
field_path     = string ;          (* 'COMPONENT.MW' — сегменти через крапку *)
```

- `ROW` і `THIS` — зарезервовані префікси без урахування регістру, як `CST`/`HDR`
  (`Parser.cs:614-622`). Колізії немає: голе ім'я без `@` поза імпортом і так помилка
  (`Parser.cs:652-669`).
- Вкладені агрегати: внутрішній `ROW` затіняє зовнішній. Щоб дістатися зовнішнього рядка, у правилах
  є `THIS`, у формулах — `!Formula`.
- AST: `RowFieldNode(IReadOnlyList<string> Path)` і `ThisNode` в `Ast/AstNode.cs`; `AstPrinter`
  друкує їх назад дослівно.

### 5.3 Типи

| Тип | Статично (публікація) | У рантаймі | Правила |
|---|---|---|---|
| `EntryRef<R>` | посилання на запис довідника `R`: результат `REGFIND`/`REGONE`, `REGFIELD` на полі `Lookup`, `Lookup`-колонка, `@Arg` з `Lookup`-колонки (лише `Strict`), `THIS` | `Number` (id запису) — той самий вибір, що вже діє для `Lookup`-комірок (`RecalculationService.cs:1120-1128`) | дозволено `=`/`<>` з `EntryRef` **того самого** `R`, `in(...)`; арифметика й порівняння з числом — помилка публікації `expr.entryRefMisuse` |
| значення поля | тип поля: `Int`/`Decimal` → `Number` з одиницею поля; `String` → `Text`; `Bool` → `Boolean`; `Date` → `Date`; `Lookup` → `EntryRef`; `Unit` → `Text` (код одиниці) | — | порожнє поле → `null` (`R-11`) |

### 5.4 Функції

`R` — код довідника (рядковий літерал); `p` — шлях поля (рядковий літерал); `f` — `Boolean` над
`ROW.*`; `e` — вираз над `ROW.*`. Рядки перебираються в порядку `(Ordinal, Id)`, тож перша помилка
завжди та сама.

| Функція | Сигнатура | Результат | Семантика |
|---|---|---|---|
| `REGFIND` | `REGFIND(R, k1 [, k2 …])` | `EntryRef<R>` | запис, видимий на дату знімка, за **первинним** ключем (`k` — у порядку `Ordinal`). Без первинного ключа — за `Code`, одна частина. Частини нормалізуються за §4.2. Не знайдено → `#N/A`; будь-яка частина `null` → `null` |
| `REGONE` | `REGONE(R, f)` | `EntryRef<R>` | рівно один видимий запис, для якого `f` = `TRUE`. 0 → `#N/A`, > 1 → `#MULTI`. Якщо `f` рівністю покриває всі поля якогось ключа, пошук іде за індексом ключа |
| `REGFIELD` | `REGFIELD(entry, p)` | тип поля | значення поля; `p` з крапками проходить через `Lookup`-поля (`'COMPONENT.MW'`). `entry` = `null` → `null`; незаповнене поле → `null`; немає поля → `#REF`; невидимий запис → `#REF`. **Розширює** наявну `REGFIELD(lookup,'field')` (`TemplateFunctions.cs:294-336`): однокрокова форма працює як раніше |
| `REGSUM` | `REGSUM(R, f, e)` | `Number` | Σ `e` по видимих рядках `R`, де `f` = `TRUE`; `null` поглинаються; порожня множина → `0` (як `SUM`, `02b-expressions.md:333-343`) |
| `REGAVG` | `REGAVG(R, f, e)` | `Number` | середнє не-`null`; порожня → `null` |
| `REGMIN` / `REGMAX` | `REGMIN(R, f, e)` | `Number`/`Date` | мін./макс. не-`null`; порожня → `null` |
| `REGCOUNT` | `REGCOUNT(R, f)` | `Number` | кількість рядків, де `f` = `TRUE` |

Спільне:
- `f` дає `null` → рядок не входить; `f` чи `e` дає помилку → агрегат повертає **першу** помилку
  (так поширюються помилки за §6.4).
- Шлях через `Lookup` на запис, невидимий на дату знімка (закритий), → `#REF` у значенні рядка:
  тихо підставити «щось інше» гірше.
- **Індексний шлях:** якщо верхній кон'юнкт `f` має форму `ROW.<Lookup> = <вираз без ROW>` або
  рівностями покриває ключ, перебираються лише відповідні рядки (індекс знімка, §5.7). Інакше —
  повний перегляд, і кожен рядок рахується в бюджет `Evaluator.MaxEvaluationSteps = 20 000`
  (`02b-expressions.md:388-391`). Публікація попереджає `expr.registryScanUnindexed`.

### 5.5 Помилки

**Значення** (додаються до `ExpressionErrors.cs` і `02b` §6.4; заголовків `err.*` не мають, як
`#BUDGET`, `ExpressionErrors.cs:136-139`):

| Значення | Коли |
|---|---|
| `#N/A` | `REGFIND`/`REGONE` не знайшли запису |
| `#MULTI` | `REGONE` знайшла більше одного |
| `#REF` (наявне) | немає поля або довідника (опис змінили після публікації); запис невидимий на дату знімка |

**Діагностика публікації** (коди наявні, нові лише `messageKey` у `09-seed.sql`):

| # (продовження `02b` §12) | Перевірка | Код | `messageKey` |
|---|---|---|---|
| 15 | довідник існує, код — літерал | `ECR-TMPL-4222` | `expr.registryUnknown`, `expr.registryCodeMustBeLiteral` |
| 16 | кожен сегмент шляху існує, проміжні — `Lookup` | `ECR-TMPL-4222` | `expr.registryFieldUnknown`, `expr.registryFieldNotLookup` |
| 17 | кількість і типи частин `REGFIND` = первинний ключ | `ECR-TMPL-0422` | `expr.registryKeyArity`, `expr.registryKeyPartType` |
| 18 | `ROW.` лише в області агрегата або правила, `THIS` — лише в правилі | `ECR-TMPL-4222` | `expr.rowReferenceOutsideScope`, `expr.thisOutsideRule` |
| 19 | `EntryRef` не бере участі в арифметиці й порівняннях з числом | `ECR-TMPL-4222` | `expr.entryRefMisuse` |
| 20 | одиниці полів беруть участь у перевірках 9–10 `02b` | `ECR-TMPL-4223` | наявні |
| 21 | `Legacy` + функції довідників | `ECR-CALC-0433` | наявний |
| (попередження) | фільтр без індексного шляху | — | `expr.registryScanUnindexed` |

### 5.6 Приклади на формулах форми 301

Вхід події (рядок таблиці подій): `@Volume` (ст.м³, `C17`), `@Duration` (с, `C12`), `@Density`
(кг/ст.м³, `C25`), `@Stream` (`Lookup` → `STREAM`), `@HmbCase` (текст «370 Winter», `C24`). Формули
методології (`Strict`), коди формул — `!…`, константи — `CST.…`
(`M_S` = 32.064, `M_CO2` = 44.00, `K_SO2` = 0.02, `ETA` = 0.9984, `OX` = 0.995; розбір
`excel-analysis.md:220-249` брав 32.06 і 44.01, але перевірка 2026-09-27 на самому файлі дала
32.064 і 44.00 з округленням — [FEATURE-HSE301-VIEW §6.5](FEATURE-HSE301-VIEW.md#65-після-feature-registry-tables-крок-rg1),
рішення HSE301 V-23). ⚠ Коди констант тут ілюстративні: у методології `HSE301.FLARE`
вони інші (`K_S` задано по речовинах, `OX_CO2`), а остаточні формули кроку `RG1` —
[FEATURE-HSE301-VIEW §6.5](FEATURE-HSE301-VIEW.md#65-після-feature-registry-tables-крок-rg1):

```
CASE    = REGFIND('STREAM_CASE', @Stream, @HmbCase)
MU      = REGSUM('GAS_COMPOSITION', ROW.CASE = !CASE, ROW.MOL_PCT * ROW.COMPONENT.MW) / 100
S_WT    = CST.M_S * REGSUM('GAS_COMPOSITION', ROW.CASE = !CASE, ROW.COMPONENT.N_S * ROW.MOL_PCT) / !MU
EF_RAW  = CST.M_CO2 * REGSUM('GAS_COMPOSITION', ROW.CASE = !CASE, ROW.COMPONENT.N_C * ROW.MOL_PCT) / 100 / !MU
EF      = Round(!EF_RAW, 3)                                         -- звіт округлює до 3 знаків
H2S_WT  = REGSUM('GAS_COMPOSITION', ROW.CASE = !CASE && ROW.COMPONENT = REGFIND('COMPONENT', 'H2S'),
                 ROW.MOL_PCT * ROW.COMPONENT.MW) / !MU
M       = CONVERT(@Volume * @Density, 'kg', 't')
SO2     = CST.K_SO2 * !M * !S_WT * CST.ETA
SO2_GS  = CONVERT(!SO2, 't', 'g') / @Duration
H2S     = !M * !H2S_WT / 100 * (1 - CST.ETA)
CO2_GHG = !M * !EF * CST.OX
W_SOUND = 91.5 * Sqrt(1.3 * (273 + REGFIELD(!CASE, 'T_C')) / !MU)
```

- `EF_RAW` містить і CO₂ складу: у `COMPONENT` для CO₂ `N_C = 1`. Це та сама формула
  `K·(Σ nC·x + x_CO2)/100/μ`, що в `excel-analysis.md:238`, але з K = 44.00 і округленням, а не
  обрізанням: лише так вона відтворює EF файлу на всіх 11 різних складах (FEATURE-HSE301-VIEW
  §6.5). Псевдокомпоненти HYSYS (`CN1_35*`, `CN2_35*`, `CN3_16*`) мають `N_C = N_S = 0`.
- `ROW.COMPONENT = REGFIND('COMPONENT','H2S')` порівнює `EntryRef` з `EntryRef`. У `COMPONENT`
  первинного ключа немає, тому пошук іде за кодом запису.
- Одиниця змінюється лише `CONVERT` (`D-74`), як у формулах `HSE301.FLARE` (FEATURE-HSE301-VIEW
  §6.3); множника `/1000` чи `·10⁶` у формулах немає. EF округлює `Round(x, 3)` — функція
  діалекту методологій (`DialectCatalog.cs:79`; у `Strict` середина — від нуля,
  `MethodologyFunctions.cs:125-134`), тож і множника `1000` для обрізання більше немає.
- Відсутній у складі компонент → порожня множина → `0`. Методології `IFERROR` недоступний
  (`DialectCatalog.cs:172`), тому агрегат з фільтром тут правильніший за `REGFIND` по рядку складу.

Та сама μ у **формулі шаблону** (колонка документа, `[HmbCase]` — `Lookup`-комірка на `STREAM_CASE`):
`REGSUM('GAS_COMPOSITION', ROW.CASE = [HmbCase], ROW.MOL_PCT * ROW.COMPONENT.MW) / 100`.

Те саме в **правилі довідника** `STREAM_CASE` (діалект правил, §6):
`ABS(REGSUM('GAS_COMPOSITION', ROW.CASE = THIS, ROW.MOL_PCT) - 100) <= 0.5`.

### 5.7 Знімок довідників

| Аспект | Рішення |
|---|---|
| Порт | `IRegistrySnapshotLoader` (`src/Ecr.Application/Ports/`) → `RegistrySnapshotLoader` (`src/Ecr.Infrastructure/Persistence/`). Порт **називається в `02-contracts.md`** (сторож `EndpointCoverageTests.Кожен_порт_застосунку_названий_у_контракті`, `EndpointCoverageTests.cs:1283`) |
| Інтерфейс у рушії | `IRegistrySnapshot` (`src/Ecr.Expressions/Evaluation/`); `IEvaluationContext.Registries` з типовим `null` (як `GetRegistryField`, `IEvaluationContext.cs:68-69`). Без знімка функції повертають `#REF` |
| Що вантажиться | лише довідники з `cfg.RegistryUse` формул прогону, плюс цілі `Lookup` зі шляхів полів |
| Дата | бізнес: останній день періоду; системна: `FOR SYSTEM_TIME AS OF @RegistryAsOfUtc` |
| Видимість | `!IsDeleted`, вікно містить дату, батько композиції видимий (рекурсивно) |
| Індекси в пам'яті | записи за id; значення (запис, поле); `(KeyDefId, хеш) → записи`; `(поле Lookup, ціль) → записи` для полів, що стоять у фільтрах рівністю |
| Кеш | на прогін: `(RegistryDefId, дата, RegistryAsOfUtc)`, спільний для всіх прив'язок прогону. Завантажується в `GenericCalculationModule.PrepareAsync` (один раз на прив'язку, `GenericCalculationModule.cs:60-81`), а не в `ExecuteAsync` на кожен рядок |
| Обсяг FLERT | ≈ 220 кейсів × ≤ 49 рядків + 126 потоків + 46 компонентів → ≈ 11 тис. записів, ≈ 30 тис. значень, одиниці МБ. До бюджету 10 хв на річний перерахунок (`D-63`) це додає одне читання на прогін |
| Інші споживачі | перерахунок шаблону (`SliceEvaluationContext`), правила валідації — через механізм [D16-04](DIRECTIVE-16.md), правила довідника (дата — «без обмеження», див. §6), прев'ю (дата від користувача) |

### 5.8 Залежності й «де використано»

- `DependencyExtractor` видає ребро «довідник/поле» для кожного `R` і `p` (і для `ROW.a.b` —
  ланцюжок). Під час публікації джерела ці ребра переписуються в `cfg.RegistryUse`. Шаблонний
  `REGFIELD` і далі дає `FormulaDependency` з `DependsOnKind = 2` (`DependencyExtractor.cs:22-27`):
  воно потрібне інкрементному перерахунку й лишається.
- `GET …/usage` отримує три нові види: `TemplateFormula`, `MethodologyFormula`, `RegistryRule`
  (`RegistryStore.FindDefinitionUsageAsync`, `RegistryStore.cs:214-291`).

### 5.9 Публікація

Перевірки 15–21 (§5.5) виконує та сама пара `ReferenceResolver`/`TypeChecker` для обох діалектів.
Форми довідників (поля, типи, одиниці, ключі) надає `IRegistryShapeSource`: у публікації — з БД, у
клієнтській перевірці — з `GET /expressions/metadata`. Публікація методології також пише
`cfg.RegistryUse` (крок RT-23b), публікація шаблону — RT-24.

### 5.10 Застарілість і перерахунок

1. `UnitOfWork` ставить `RegistryDef.DataChangedAt = UtcNow` щоразу, коли зросла `DataRevision`.
   Ревізія вже росте на кожну зміну записів (`UpsertRegistryEntryHandler.cs:112-115`).
2. `GetCalculationFreshnessAsync` (`MethodologyStore.cs:385-414`) додає до `InputsChangedAt` другий
   доданок: `MAX(DataChangedAt)` довідників з `cfg.RegistryUse` версій методології й формул шаблону,
   що дали результати документа, з умовою `DataChangedAt > r.StartedAt`. `CalculationFreshness`
   отримує поле `ChangedRegistries[]`, дописане в кінець запису. Панель результатів показує
   `calculation.staleRegistry`: «Довідник "{name}" змінено після розрахунку».
3. Перерахунок **ніколи не ставиться автоматично** (`R-14`): одна правка складу — це серія з
   десятків збережень, а бюджет прогону обмежений (`D-63`). Замість цього:
   `GET /registries/{code}/impact` показує документи **відкритих** періодів (`Open`/`Grace`), що
   залежать від довідника, а `POST …/recalculate-impacted` ставить їх у чергу (`202`, `jobId`).
   Закриті періоди не пропонуються взагалі (`D-39`).

### 5.11 Автодоповнення й підказки

| Позиція курсора | Пропозиція |
|---|---|
| `REGFIND('│`, `REGSUM('│`, `REGONE('│` | коди довідників із назвою і позначками «ключ», «композиція» |
| після `,` у `REGFIND` | підказка сигнатури: частини первинного ключа з типами (`STREAM: Stream → STREAM`, `CASE_NAME: Text`) |
| `ROW.│`, `ROW.COMPONENT.│` | поля довідника області, далі — поля цілі `Lookup`; одиниця праворуч |
| `REGFIELD(x, '│` | шляхи полів довідника статичного типу `x` |
| hover на полі | `GAS_COMPOSITION.MOL_PCT — Decimal · % · required · part of STREAM_CASE` |
| діагностика | наявний потік `POST /expressions/validate` → маркери (`markers.ts`) |

`GET /expressions/metadata` додає `registries[]`: код, назва, `isTemporal`, поля (тип, одиниця,
ціль `Lookup`, `relationKind`), ключі. Показуються лише довідники, до яких у користувача є
`Registry.View`.

### 5.12 Правила валідації документа

Дефект Д-1 виправляє [DIRECTIVE-16 §2 D16-04](DIRECTIVE-16.md), не цей документ. Нові функції
отримують знімок **тим самим** шляхом, яким D16-04 передає його в `ValidationEvaluationContext`
(параметр конструктора, `ValidationEvaluationContext.cs:36-38`). Другого механізму не заводимо.
Крок RT-24 стартує після D16-04.

✓ **D16-04 виконано в `5f00b791`** (перевірено за комітом на `origin/dev/integration`
2026-09-27). Що це означає для плану:

- знімок полів довідника тепер будує спільний `RegistryFieldSnapshotLoader`
  (`src/Ecr.Application/Registries/RegistryFieldSnapshotLoader.cs`): кроки 3–4
  `RecalculationService.LoadRegistryFieldsAsync` винесено туди, і його кличуть і перерахунок, і
  `TableValidation.RunAsync` («Перевірити» — `ValidateDocumentHandler`, подання —
  `SubmitSheetHandler`, один знімок на обидва). Отже посилання `RecalculationService.cs:11xx-13xx`
  у цьому документі — стан **до** `5f00b791`;
- RT-22/RT-24 **розширюють цей завантажувач** (або роблять його фасадом над
  `IRegistrySnapshotLoader`), а не заводять поруч другий;
- ✓ шлях **збереження** закрито в `400c58b1` (перевірено читанням коду на
  `origin/dev/integration` 2026-09-27): `PatchCellsHandler` будує знімок тим самим
  `TableValidation.LoadRegistryFieldsAsync` (`PatchCellsHandler.cs:151-152`) і передає його в
  `ValidateCell`/`ValidateScope`. Записи довідника беруться лише з комірок самого батча, без
  зрізу таблиці: правило комірки бачить своє значення, правило рядка — надіслане, рівні
  таблиці й документа на PATCH не виконуються. Отже нові функції довідника в правилах
  працюватимуть на всіх трьох шляхах, щойно RT-24 розширить завантажувач; у DoD RT-24 входить
  і шлях збереження.

---

## 6. Правила довідника

| Вид (`RegistryRuleKind`) | Що перевіряє | Коли `Expression` | Параметри | Шаблон у конструкторі |
|---|---|---|---|---|
| `RequiredWhen` | поле `field` заповнене, якщо умова `TRUE` | умова над `ROW.*`/`THIS` | `{"field":"X"}` | «Обов'язкове, якщо…» |
| `Expression` | предикат `TRUE` (`null` — не порушення, як `CHECK`; помилка-значення — порушення з кодом помилки) | сам предикат | довільні | «Власний вираз»; «Сума дочірніх» |
| `CrossRegistry` | значення поля знайдене в іншому довіднику за його ключем | компілюється в `REGFIND(target, ROW.field)` ≠ `#N/A` | `{"field":"X","registry":"R","key":"PK"}` | «Має існувати в…» |
| `UniqueWithin` | — | **нові відхиляються** (`R-5`), наявні пропонується перетворити на ключ (§4.7) | — | — |

**Шаблон «Сума дочірніх»** — це правило виду `Expression` з параметрами
`{"template":"childSum","child":"GAS_COMPOSITION","field":"MOL_PCT","target":100,"tolerance":0.5}`,
з яких генерується вираз `ABS(REGSUM(child, ROW.<compositionField> = THIS, ROW.field) - target) <= tolerance OR REGCOUNT(child, ROW.<compositionField> = THIS) = 0`.
✎ RT-17a: хвіст `OR REGCOUNT(…) = 0` — бо пакет пише один довідник і батько записується раніше за
своїх дітей, тож без нього правило рівня `Error` не дало б створити жодного батька; наслідок — порожній
склад (дітей ще немає або всіх видалено) теж проходить. Сітка читає ці параметри для живого індикатора Σ (§8.4). Вираз правила **не розбирається на клієнті**.

| Аспект | Рішення |
|---|---|
| Діалект | граматика `Template` (регістр не важить, `ABS`, `IF`, `SUM` є) + `ROW.`/`THIS`; `@Arg`/`CST.` недоступні. Правило перевіряє дані, а не рахує (так само мислить `ValidationEvaluationContext.cs:12-17`) |
| Область | `THIS` — запис, що перевіряється; верхньорівневий `ROW.x` = поле `THIS` |
| Дата знімка | правила бачать усі **живі** записи незалежно від вікна: правило про довідник, а не про період |
| Момент | під час збереження запису (upsert, пакет, CSV, імпорт) — для змінених записів **і** їхніх батьків композиції, чиї правила агрегують змінений дочірній довідник. Звідки відомо, які саме: `cfg.RegistryUse` з `SourceKind = 2` |
| Рівні | `Info`/`Warning` — зберегти й повернути `warnings[]`; `Error` — відмова `422 ECR-REG-4221` з переліком `{rule, entry, message}` |
| ⚠ Агрегатні правила й поштучний ввід | сітка зберігає склад **пакетом на батька** (`POST …/entries/batch`), тож блок `Error` перевіряє стан після всього пакета, а не після кожного рядка. Інакше Σ = 100 неможливо ввести рядок за рядком |
| Перевірка на наявних даних | `POST …/rules/check` до збереження правила: «220 записів перевірено, 4 порушують». На FLERT очікувано: `LPG Column Overhead` 75.4/80.6 %, азот (усі нулі) — `excel-analysis.md:386-391` |
| Публікація опису | вирази правил розбираються й типізуються (§5.9); неправильний вираз — `422 ECR-REG-0422 ruleExpressionInvalid` з діагностикою |

---

## 7. API

### 7.1 Ендпоінти

База `/api/v1`, конвенції `02-contracts.md` §8: сторінки курсорні (`:3186-3198`), `decimal` передається
рядком, довгі операції повертають `202` (`:3212-3213`). Кожен рядок потрапляє в `02-contracts.md` §9
**у кроці, що реалізує ендпоінт**, разом зі споживачем у клієнті (`EndpointCoverageTests.cs:37`, `:120`,
`:993`).

| Метод | Шлях | Право (+ грант) | Тіло → відповідь | Помилки | Крок |
|---|---|---|---|---|---|
| `PUT` | `/registries/{code}/definition` (наявний; так само `…/draft` і `…/publish`) | `Registry.EditDefinition` + `Registry.Publish` | + `keys[]`, `fields[].relationKind`, `fields[].onParentDelete`, `codeMode` → `{definitionVersion}` | 404 `REG-0404`; 422 `REG-0422` (нові `messageKey`, §7.2); 409 `REG-4092 existingDuplicates` | RT-11, RT-12 |
| `POST` | `/registries/{code}/keys/check` | `Registry.EditDefinition` | `{fieldCodes[], ignoreCase}` → `{checked, groups, sample[≤20]:{keyText, entries:[{id,code}]}}` | 404, 422 | RT-11 |
| `POST` | `/registries/{code}/rules/check` | `Registry.EditDefinition` | `{rule: RegistryRuleSaveDto}` → `{checked, violations, sample[≤20]:{entryId, code, messageKey, params}}` | 404; 422 (діагностика виразу) | RT-17b |
| `GET` | `/registries/{code}/rows?asOf=&parentEntryId=&q=&field.<F>=&sort=&cursor=&limit=` | `Registry.View` (+ `Read`) | → `{items[], nextCursor, totalCount}` (приклад нижче) | 404; 422 `REQ-0422 pageSizeOutOfRange` для `limit` поза `1…500` (`02-contracts.md` §8, як решта курсорних переліків); 422 `REQ-0422 asOfRequired` для темпорального довідника без `asOf` (як `GetRegistryEntriesHandler.cs:82-91`) | RT-13 |
| `POST` | `/registries/{code}/entries/batch?dryRun=` | `Registry.EditData` (+ `Write`) | `{items[≤2000]}` → звіт (приклад нижче); завжди 200, як імпорт CSV (`RegistriesController.cs:266-275`) | 404; 409 `REG-4092 keyTakenConcurrently` (гонка під час застосування); 422 `REQ-0422 batchTooLarge` | RT-14 |
| `POST` | `/registries/{code}/entries` (наявний) | `Registry.EditData` | + ключі, правила; `RegistryEntryIdResponse` дописує в кінець `warnings[]` | + 409 `REG-4092`, 409 `REG-4093 entryChanged` (якщо передано `baseVersion`), 422 `REG-4221` | RT-10a, RT-17a |
| `GET` | `/registries/{code}/entries/{id}/history?cursor=&limit=` | `Registry.View` | → `{items:[{at, byUserId, kind, field?, oldValue?, newValue?}], nextCursor}`, `kind` ∈ `created`, `value`, `validity`, `deleted` | 404 | RT-15 |
| `GET` | `/registries/{code}/export?format=csv\|xlsx&asOf=&includeChildren=` | `Registry.View` | файл (`text/csv`, `…spreadsheetml.sheet`); ≤ `Registries:ExportMaxRows` (50 000) | 404; 422 `REQ-0422 exportTooLarge` | RT-16 |
| `POST` | `/registries/{code}/import/preview` | `Registry.EditData` | multipart `{file, spec}` → `{previewToken, summary:{new, changed, unchanged, errors, warnings}, changes[≤200], errors[]}`; ≤ `Registries:WorkbookImportMaxBytes` (10 МБ) | 404; 422 `REQ-0422 importFileTooLarge` / `importUnreadable`; 422 `REG-0422 import*` | RT-18a |
| `POST` | `/registries/{code}/import/apply` | `Registry.EditData` | multipart `{file, spec, previewToken}` → звіт (≤ 5 000 рядків) або `202 {jobId, statusUrl}` | 409 `REG-4093 importPreviewStale` (змінилася `DataRevision`); як preview | RT-18b |
| `GET`/`PUT`/`DELETE` | `/registries/{code}/import-profiles[/{profileCode}]` | `Registry.View` / `Registry.EditData` | профіль: `{code, nameL10n, spec, rowVersion}`; `PUT` з `If-Match` | 404; 409 `REG-4093 profileChanged` | RT-18c |
| `GET` | `/registries/{code}/impact` | `Registry.View` + `Calculation.View` | → `{items:[{documentId, businessKey, periodKey, periodState, via:["methodology:HSE301"...]}], total}` | 404 | RT-25 |
| `POST` | `/registries/{code}/recalculate-impacted` | `Calculation.Recalculate` | `{documentIds?, reason}` → `202 {jobId, statusUrl}` | 404; 422 (закритий період у переліку) | RT-25 |
| `GET` | `/registries/{code}/usage` (наявний) | `Registry.EditDefinition` | + види `TemplateFormula`, `MethodologyFormula`, `RegistryRule` | — | RT-19 |
| `GET` | `/expressions/metadata` (наявний) | `Calculation.View` | + `registries[]` (§5.11) | — | RT-26 |
| `POST` | `/expressions/preview` | `Calculation.View` + `Registry.View` | `{expression, dialect, methodologyVersionId?, asOf, arguments:{Name: value}}` → `{value, type, unit, errorCode?, reads:[{fn, registry, detail, result}]}` | 422 (діагностика) | RT-26 |

Приклад рядка `GET …/rows`:

```jsonc
{ "items": [ {
    "id": 4411, "code": "E000004411", "display": "1D-2 · 370 Winter", "parentEntryId": null,
    "validFrom": null, "validTo": null,
    "version": "AAABkWmN3kM=",                 // PeriodStart запису й значень (max) — жетон конкуренції
    "keyText": "1D-2 · 370 Winter",
    "values": {
      "STREAM":    { "value": "162", "display": "1D-2 · HP Separator Gas" },
      "CASE_NAME": { "value": "370 Winter" },
      "T_C":       { "value": "49.9999977539011", "unit": "degC" } },
    "warnings": [ { "rule": "SUM_100", "messageKey": "registries.rules.violated" } ],
    "childCounts": { "GAS_COMPOSITION": 34 } } ],
  "nextCursor": "eyJpZCI6NDQxMX0", "totalCount": 220 }
```

Приклад пакета:

```jsonc
POST /api/v1/registries/GAS_COMPOSITION/entries/batch?dryRun=true
{ "items": [
  { "clientRowId": "r1", "op": "upsert", "id": 9001, "baseVersion": "AAABkWmN3kM=", "values": { "MOL_PCT": "12.4246690" } },
  { "clientRowId": "r2", "op": "upsert", "values": { "CASE": "4411", "COMPONENT": "31", "MOL_PCT": "0.0003" } },
  { "clientRowId": "r3", "op": "delete", "id": 9005, "baseVersion": "AAABkWmN3kQ=" } ] }
→ 200
{ "applied": false, "dryRun": true,
  "rows": [ { "clientRowId": "r2", "status": "error", "entryId": null,
              "errors": [ { "field": null, "errorCode": "ECR-REG-4092", "messageKey": "err.ECR-REG-4092.keyTaken",
                            "params": { "key": "PK", "keyText": "1D-2 · 370 Winter · COS", "entryCode": "E000009017" } } ] } ],
  "rules": [ { "entryId": 4411, "rule": "SUM_100", "severity": "Warning", "messageKey": "registries.rules.violated",
               "params": { "value": "100.0003" } } ] }
```

### 7.2 Коди помилок

Формат `ECR-<ДОМЕН>-<HTTP><номер>` (`02-contracts.md:3069-3070`). Для кожного коду в одному кроці
робиться все таке: рядок у §7, константа в `ErrorCodes.cs`, `Details["messageKey"]`, рядки
`err.<код>` і `err.<код>.<випадок>` у `09-seed.sql` (англійською) і, для 409, арм у
`ExceptionHandlingMiddleware`. Без арму `BusinessRuleException` доїжджає як 422
(`ExceptionHandlingMiddleware.cs:433-444`).

| Код | HTTP | Коли | `messageKey` | Тип винятку / арм |
|---|---|---|---|---|
| `ECR-REG-4092` | 409 | конфлікт унікального ключа | `keyTaken`, `keyTakenConcurrently`, `keyWindowOverlap`, `keyDuplicateInFile`, `keyDuplicateInBatch`, `existingDuplicates` | `BusinessRuleException` + **арм 409** (`ErrorCodes.RegistryKeyConflict`); гілка гонки в `TryMapDuplicateKey` → `ConcurrencyConflictException` |
| `ECR-REG-4093` | 409 | запис змінено іншим після читання; прев'ю імпорту застаріло; профіль змінено | `entryChanged`, `importPreviewStale`, `profileChanged` | `ConcurrencyConflictException` (409 за типом, арм не потрібен) |
| `ECR-REG-4221` | 422 | порушено правило довідника рівня `Error` | `ruleViolated` (`rule`, `entryCode`, `message`) | `BusinessRuleException` (422 без арму) |
| `ECR-REG-0422` (наявний, опис у §7 розширюється) | 422 | опис або імпорт суперечливий | `keyFieldUnknown`, `keyFieldNotRequired`, `keyFieldTypeNotAllowed`, `keyImmutable`, `primaryKeyTwice`, `compositionMoreThanOne`, `compositionCycle`, `compositionChildTemporal`, `compositionTargetSelf`, `codeModeImmutable`, `uniqueWithinReplacedByKeys`, `ruleExpressionInvalid`, `importUnpivotTargetUnknown`, `importDuplicateSourceMismatch`, `importParentUnresolved` | наявний |
| `ECR-REQ-0422` (наявний) | 422 | межі запиту | `batchTooLarge`, `exportTooLarge`, `importFileTooLarge`, `importUnreadable` | наявний |

### 7.3 Права

**Нових прав немає** (`R-16`). Опис, ключі, правила — `Registry.EditDefinition` (+`Registry.Publish`
для прямого збереження, `RegistryDefinitionHandlers.cs:274-279`). Дані, пакет, імпорт —
`Registry.EditData` через `RegistryAccess.RequireAsync` з грантом `Write` на довідник
(`UpsertRegistryEntryHandler.cs:64-69`), так само як наявний CSV. Рядки, історія, експорт —
`Registry.View`. Перерахунок — `Calculation.Recalculate`. Прев'ю виразу — `Calculation.View` +
`Registry.View`: прев'ю показує значення довідника.

---

## 8. Інтерфейс

### 8.1 Принципи (з `docs/design/hybrid/KIT.md`)

- Спокій за замовчуванням: інспектор закритий, одна головна дія на екран, колір лише там, де щось
  не так (`KIT.md:25-35`).
- Шаблон редактора (`KIT.md:194-218`): `editor-head`, під ним `split`, стан збереження **словами**
  («Saved 14:03», «3 unsaved changes»).
- Підтвердження за таблицею `KIT.md:386-395`: імпорт — `Wizard`; видалення з наслідками —
  `ConfirmDialog`; перерахунок — `tasks` + тост.
- Мови: рядки інтерфейсу — ключі каталогу, сід — лише англійською (`SeedCatalogTextTests`); дати —
  `@mantine/dates` (`<input type="date">` заборонено, `DIRECTIVE-15-FRONTEND.md:114`).

### 8.2 Карта екранів

| Екран | Адреса | Стан сьогодні | Що змінюється |
|---|---|---|---|
| Довідники | `/admin/registries` | `RegistriesPage.tsx` | у рядку довідника — «Відкрити дані» (перехід на `…/entries`) і позначки «Ключ», «Частина X». Форма `RegistryEntryEditor` лишається для плоских довідників без ключів |
| Визначення довідника | `/admin/registries/:code/definition` | вкладки Fields, Relations, Rules, Mapping, History, Usage (`RegistryConstructorPage.tsx:262-273`) | **+ Keys**; Relations → ERD + налаштування композиції; Rules → шаблони + Monaco + перевірка; Usage → нові види |
| **Дані довідника** | `/admin/registries/:code/entries` | маршруту немає (у `DIRECTIVE-15-FRONTEND.md:186` він запланований) | новий табличний редактор, master-detail, шторка запису |
| Імпорт | `?dialog=import-entries` на екрані даних | `RegistryImportPanel.tsx` (CSV) | `Wizard` з 4 кроків + Review |
| Редактор формул | вкладка формул методології / формули шаблону | `ExpressionEditor.tsx` (Monaco) | автодоповнення довідників, hover, панель «Check on a record» |

### 8.3 Конструктор

**Вкладка «Keys»**

```
┌ ← Back to Registries ─────────────────────────────────────────────────────────────────────────────────────┐
│ Stream cases  STREAM_CASE            Draft · Saved 14:03                      [Check] [More ▾] [Publish…]  │
├ Fields 9 │ Keys 1 │ Relations 2 │ Rules 1 │ Mapping │ Usage 4 │ History ──────────────────────────────────┤
│ A key makes a combination of fields unique. Formulas find an entry by the primary key (REGFIND).          │
│                                                                                                           │
│ ┌ Primary key · PK ─────────────────────────────────────────────────────────────────────────────── [⋯] ┐  │
│ │  [⠿ 1 Stream ✕]  [⠿ 2 Case name ✕]   [+ Add field ▾]                                                │  │
│ │  ☑ Ignore letter case and extra spaces        ☑ Validity windows must not overlap                   │  │
│ │  No duplicates in 220 existing entries · checked 14:02  [↻ Check again]                             │  │
│ └───────────────────────────────────────────────────────────────────────────────────────────────────────┘  │
│ [+ Add alternative key]                                                                                   │
└───────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

Стан «є дублікати» (перевірка йде за 600 мс після зміни чипів; кнопка Publish вимкнена з
підказкою):

```
│ │  ⚠ 11 duplicate groups in existing entries — the key cannot be published yet.                        │  │
│ │    1D-2 · No composition     E000000031, E000000034      [Show in editor]                             │  │
│ │    KE01… · SOR               E000000102, E000000118      [Show in editor]                             │  │
│ │    … 9 more                                                                                            │  │
```

- Чипи — `Mantine Pill` з ручкою перетягування. Порядок з клавіатури: `Alt+←/→` на чипі (порядок —
  це порядок аргументів `REGFIND`). Видалення — `Delete`.
- «+ Add field» пропонує лише дозволені типи; поле з `IsRequired = false` для первинного ключа
  позначене як недоступне з поясненням «make it required first».
- Для наявного ключа чипи лише для читання (§4.1, рядок «Зміна»); дія «Replace key…» веде до
  `ConfirmDialog`: «створить новий ключ і вимкне цей».

**Вкладка «Relations»** — міні-ERD (SVG без бібліотек, `role="img"` + текстова таблиця нижче як
доступний еквівалент):

```
│  ┌──────────────┐ ◆──────────────────────▶ ┌──────────────┐ ◆──────────────────────▶ ┌──────────────────┐ │
│  │ STREAM       │   STREAM_CASE.STREAM      │ STREAM_CASE  │   GAS_COMPOSITION.CASE   │ GAS_COMPOSITION  │ │
│  │ 126 entries  │   composition             │ 220 entries  │   composition            │ 10 780 entries   │ │
│  └──────────────┘                           └──────────────┘                          └────────┬─────────┘ │
│                                                                    GAS_COMPOSITION.COMPONENT ┊ reference │
│                                                                                     ┌──────────▼───────┐ │
│                                                                                     │ COMPONENT · 46   │ │
│                                                                                     └──────────────────┘ │
│  Field                        Kind          Target        When the parent is deleted     Used by          │
│  CASE                         Composition   STREAM_CASE   Delete its parts too ▾          —               │
│  COMPONENT                    Reference     COMPONENT     —                               —               │
```

Поточний довідник виділено рамкою `--accent`. На вузлі: `Enter` відкриває довідник, `Tab` — наступний
вузол. Композиція задається під час **створення** поля (у вкладці Fields — перемикач «Part of
parent»). Тут її видно, а політика видалення змінюється тільки новим полем, як і `PointTo`.

**Вкладка «Rules»**

```
│ Rules 2                                                                        [+ Add rule ▾]           │
│ ┌ SUM_100 · Children add up to a value · Warning ─────────────────────────────── On ● · [⋯] ┐          │
│ │ Children GAS_COMPOSITION · field MOL_PCT · target 100 ± 0.5                                │          │
│ │ Checked 220 entries: 4 violate  (LPG Column Overhead 75.4 %, Nitrogen 0 %, …)  [Show]     │          │
│ └────────────────────────────────────────────────────────────────────────────────────────────┘          │
│ ┌ NUMBER_REPEATS · Custom expression · Warning ────────────────────────────────── On ● · [⋯] ┐          │
│ │ REGCOUNT('STREAM', ROW.NUMBER = REGFIELD(THIS,'NUMBER')                                     │          │
│ │                    AND ROW.IS_ONSHORE = REGFIELD(THIS,'IS_ONSHORE')) <= 1      (Monaco)     │          │
│ │ Message: "Stream number is used by another stream"   Severity: Info | [Warning] | Error    │          │
│ └────────────────────────────────────────────────────────────────────────────────────────────┘          │
```

«+ Add rule» пропонує шаблони: Children add up to a value / Required when / Must exist in another
registry / Custom expression. Рівень задає `SegmentedControl`. Для рівня `Error` показується
наслідок: «saving entries that break it will be refused».

**Вкладка «Usage»** — наявна `RegistryUsage.tsx`, до якої додано види `Template formula`,
`Methodology formula`, `Registry rule`. Рядок: вид · де (`HSE301 v3 · MU`) · посилання.

### 8.4 Редактор даних

**Master-detail** (довідник, до якого ведуть композиції; ланцюжок знаходиться сам):

```
┌ ← Back to Registries ─────────────────────────────────────────────────────────────────────────────────────┐
│ Flare streams  STREAM                      As of [30 Sep 2026 ▾]   [Export ▾] [Import…]   [Save 3 changes]  │
│ Streams, their HMB cases and gas composition. Values apply to calculations of open periods.               │
│ ⚠ 2 documents in open periods were calculated before your last change.        [Review and recalculate…]   │
├───────────────────────────────────────────────────────────────────────────────────────────────────────────┤
│ [Search number or name…]  [Onshore: All ▾]  [Group: All ▾]                        126 streams · 3 unsaved   │
├─ Streams ────────────────────────────────────────────────┬─ Cases of 1D-2 · HP Separator Gas ─────────────┤
│  № stream  Name                  Onshore Sour Group HMB  │  Case         T, °C    ρ op    MW      Z       │
│  1D-1      HP Separator Inlet    No      Yes  1     370  │  370 Winter   50.00   110.10  23.05   0.7485   │
│▶ 1D-2      HP Separator Gas      No      Yes  1     370  │▶ 370 Summer ● 75.00   …       …       …        │
│  1D-7      …                                             │  + Add case                  Ctrl+Enter        │
│  …                                                       ├─ Composition · 1D-2 · 370 Summer ──────────────┤
│                                                          │  Component             mol %                   │
│                                                          │  N2   Nitrogen         1.435977                │
│                                                          │  H2S  Hydrogen sulfide ●12.424669   edited     │
│                                                          │  CO   Carbon monoxide  ⚠ 0.000100  Same key    │
│                                                          │  CO   Carbon monoxide  ⚠ 0.000100  as row 7    │
│                                                          │  + Add component                               │
│                                                          │  Σ mol % 100.0002 · target 100 ± 0.5  ✓        │
├──────────────────────────────────────────────────────────┴────────────────────────────────────────────────┤
│ 3 unsaved changes · Ctrl+S to save                                                                        │
└───────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

**Плоский довідник** (`COMPONENT`):

```
│ Components  COMPONENT                                  [Export ▾] [Import…]  [+ Add entry]  [Save]      │
│ [Search…]                                                                              46 entries      │
│  Code    Name              Formula   HYSYS name     M, g/mol    nC   nS   LHV, MJ/Sm³                   │
│  CH4     Methane           CH4       Methane        16.0429     1    0    —                             │
│  H2S     Hydrogen sulfide  H2S       H2S            34.076      0    1    —                             │
│  C6      n-Hexane          C6H14     C6_1           85.36       6    0    —                             │
```

Назви й формули — з Add Stream, `N_C`/`N_S` — з формули, M — значення HYSYS, відновлені з
301; LHV у джерелах немає, тому поле порожнє (§11.4, `R-27`).

| Взаємодія | Поведінка |
|---|---|
| Типізовані редактори | `Decimal`/`Int` — числове поле з інваріантною крапкою; `Bool` — `BoolCellEditor`; `Date` — `DateCellEditor` (`@mantine/dates`); `Lookup` — пікер з пошуком (`LookupCellEditor` + `listCellEditor`, до 50 показаних, фільтр набором, `listCellEditor.ts:47`, `:327`), показує назву, а не id; `Unit` — `UnitCellEditor` |
| Вставка з Excel | `Ctrl+V` блоком: `parseClipboard`/`planPaste` (`features/grid/clipboard.ts:29`, `:118`); `Lookup` зіставляється за кодом, назвою або псевдонімом (`HYSYS_NAME`); чого не зіставлено — підсвічується, «n cells could not be matched» |
| Живі перевірки | після паузи 600 мс брудні рядки йдуть у `batch?dryRun=true`. Дубль ключа в пакеті видно миттєво (клієнтський `normalizeKey`), дубль із БД — з відповіді dryRun. Помилки й попередження прив'язані до комірок |
| Σ | з параметрів шаблону правила `childSum` (§6): рахується на клієнті з поточних значень сітки, лише для показу; авторитетний результат — сервер |
| Збереження | `Ctrl+S` / кнопка → `batch` (без dryRun) по батьку. Успіх — «Saved 14:03» і банер застарілості, якщо `impact.total > 0` |
| Undo/Redo | `UndoStack` (`features/grid/undo.ts:41`) до збереження |
| Фільтри | пошук за кодом, назвою й текстовими полями; `FilterBar` для `Bool` і `Lookup`-полів; `As of` — для темпорального довідника |
| Видалення рядка | `Ctrl+Shift+Delete` → у дочірньому довіднику — одразу в пакеті (оборотно до збереження); запис, на який посилаються, дає `409 REG-0409`, показується з розкладом видів і дією «Close with a date» |

| Стан | Вигляд |
|---|---|
| Завантаження | скелет сітки 8×6 (`AsyncBoundary`) |
| Порожньо | `EmptyState`: «No entries yet» — «Add entries one by one or import them from a CSV or Excel file.» [Import from file] [Add entry] |
| Нічого не знайдено | «Nothing matches these filters» [Clear filters] |
| Помилка | `ErrorState`: код, correlation id з копіюванням, [Retry] |
| Конфлікт | `Banner warning`: «2 rows were changed by M. Tulegenov after you opened them» + на рядку [Keep mine] [Take theirs] (зразок — `features/grid/ConflictPanel.tsx`) |
| Лише читання | `Banner info`: «Read only: editing needs Registry.EditData» або «…mastered by an external source» (`SourceKind = External`); замки на комірках, кнопок Save/Import немає |
| Незбережене при виході | `UnsavedGuard` (`shared/ui/UnsavedGuard.tsx`) |

### 8.5 Шторка запису (`?panel=<id>`)

Вкладки **Details** (`KeyValue`: ключ, код, вікно, автор, «Changed 27 Sep 14:03 by D. Akhmetova») ·
**History** · **Where used**.

```
│ History                                                   Show values as of [01 Jan 2026 ▾]            │
│  When               Who             Field          Change                                              │
│  27 Sep 2026 14:03  D. Akhmetova    H2S (mol %)    12.4246686 → 12.4246690                            │
│  27 Sep 2026 14:03  D. Akhmetova    —              validity 01 Jan 2026 → —                           │
│  05 Aug 2025 09:12  import          —              created                                            │
```

### 8.6 Майстер імпорту

```
┌ Import into Flare streams ─────────────────────────────────────────────────────────────── ✕ ┐
│ ① File  ② Layout  ③ Mapping  ④ Preview  ⑤ Review                                           │
│ Layout                                                                                     │
│ Profile  [HYSYS offshore export ▾]  [Save as profile…]                                     │
│ Sheet    [streams Offshore ▾]                                                              │
│ Each ( ) row is an entry   (●) column is an entry — transposed, e.g. HYSYS                 │
│ Header rows [2]   Row 1 = stream number   Row 2 = HMB case   Data from row [3]             │
│ ┌ preview of the file ───────────────────────────────────────────────┐                     │
│ │         │ 1D-2        │ 1D-2        │ 1D-7        │                 │                     │
│ │         │ 370 Winter  │ 370 Summer  │ 370 Winter  │                 │                     │
│ │ T       │ 50.0        │ 75.0        │ …           │                 │                     │
│ └─────────────────────────────────────────────────────────────────────┘                     │
│                                                              [Back]            [Next]      │
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

```
│ Mapping                                                                                    │
│  Source             →  Target                                   Status                     │
│  Row 1 (number)     →  STREAM · № stream (find by key)          matched 126                │
│  Row 2 (HMB case)   →  STREAM_CASE · Case name                  —                          │
│  Temperature        →  STREAM_CASE · T, °C                      —                          │
│  Nitrogen…Methanol  →  GAS_COMPOSITION rows (49 columns)        46 components matched      │
│                        component by HYSYS name · value → mol %  ⚠ 3 pairs fill the same    │
│                        ☑ skip zero values                         component: must match     │
│  Density, MW, Z     →  STREAM_CASE · ρ op, MW, Z                —                          │
```

```
│ Preview — nothing is saved yet                                                             │
│  12 new · 34 changed · 174 unchanged · 3 errors · 4 warnings        [Changes] [Errors] [Warn]│
│  Errors                                                                                    │
│  Col AL  1D-65 · 370 Summer   importDuplicateSourceMismatch  CO 0.0012 ≠ Carbon_Monoxide 0.0 │
│  …                                                                                         │
│  Warnings                                                                                  │
│  LPG Column Overhead · 370 Winter   SUM_100   Σ mol % = 75.4 (target 100 ± 0.5)            │
```

- Далі з помилками не пускає (`validate` кроку повертає текст). Review показує підсумок і дієслово
  «Import 46 entries». Результат — `ResultBanner`: «Imported: 12 new, 34 changed. 2 documents may
  need recalculation» [Review].
- Більше 5 000 рядків → `202`, «continues in My tasks» (`tasks`).
- Профіль зберігає `RegistryImportSpec` (`cfg.RegistryImportProfile`), щоб наступна ревізія HMB
  імпортувалась у два кліки.

### 8.7 Редактор формул

```
┌ Formula MU · HSE301 v4 (Strict) ──────────────────────────────────────────────────────────┐
│ 1 │ REGSUM('GAS_COMPOSITION', ROW.CASE = !CASE, ROW.MOL_PCT * ROW.COMPONENT.│             │
│   │                                                          ┌──────────────────────┐      │
│   │                                                          │ MW        g/mol      │      │
│   │                                                          │ N_C       —          │      │
│   │                                                          │ N_S       —          │      │
│   │                                                          │ LHV       MJ/Sm³     │      │
│   │                                                          └──────────────────────┘      │
├ Check on a record ─────────────────────────────────────────────────────────────────────────┤
│ As of [31 Jan 2026]   @Stream [1D-2 · HP Separator Gas ▾]   @HmbCase [370 Winter]  [Run ▶] │
│ Result  23.0544679 · Number · g/mol                                                        │
│ Registry reads  REGFIND STREAM_CASE (1D-2, 370 Winter) → E000000217                        │
│                 REGSUM GAS_COMPOSITION · 34 rows · index CASE                              │
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

Панель згорнута за замовчуванням (`Collapsible`). Пікери аргументів будуються з типів `@Arg`:
`Lookup` → пікер довідника, число → поле. `!Formula`, від яких залежить вираз, рахуються разом із
ним.

### 8.8 Клавіатура (редактор даних)

| Клавіша | Дія |
|---|---|
| `←↑→↓`, `Home/End`, `PgUp/PgDn` | навігація по комірках |
| `Enter` / `F2` | редагувати; `Enter` у редакторі — підтвердити й униз (нормалізація `keyCode` — `features/grid/keyboardCompat.ts`) |
| `Tab` / `Shift+Tab` | підтвердити й праворуч/ліворуч |
| `Esc` | скасувати редагування; друге `Esc` закриває верхній шар |
| `Alt+↓` | відкрити пікер `Lookup`/дати |
| `Ctrl+C` / `Ctrl+V` | копіювати / вставити блок |
| `Ctrl+Z` / `Ctrl+Y` | скасувати / повторити |
| `Ctrl+Enter` | новий рядок у поточній панелі |
| `Ctrl+Shift+Delete` | видалити виділені рядки (до збереження) |
| `Ctrl+S` | зберегти |
| `Ctrl+F` | пошук у панелі |
| `F6` / `Shift+F6` | наступна / попередня панель (потоки → кейси → склад) |
| `Ctrl+.` | відкрити шторку запису |
| `?` | довідка клавіш |

### 8.9 Доступність

- Обидві теми тільки через токени (`KIT.md` §7.1). Для кожної нової пари «текст/тло» (дубль ключа
  `--danger-soft`, змінена комірка, Σ поза допуском) додається рядок у `contrast.test.ts`: текст
  4.5, межі й фокус 3 (`DIRECTIVE-15-FRONTEND.md:154-157`).
- Стан рядка передається **не лише кольором**: іконка, текст «Same key as row 7» у `title` і
  `aria-describedby`, `aria-invalid="true"` на комірці.
- Оголошення через `aria-live="polite"`: «3 unsaved changes», «Saved 14:03», підсумок dryRun
  («2 errors, 1 warning»). Сітка: `role="grid"`, `aria-rowcount`, заголовки колонок із типом і
  одиницею, як у `DocumentGrid.a11y-status.test.tsx`.
- ERD: `role="img"` + `aria-label`, таблиця зв'язків нижче як еквівалент; вузли фокусовані.
- Нові екрани й діалоги додаються в `src/test/__tests__/accessibility.part{1,2,3}.a11y.test.tsx`
  (обидві теми) і `a11yFixtures.tsx` (`emptyBodyFor`); гейти `a11y (dark)` / `a11y (light)`.

### 8.10 Компоненти

| Перевикористовується | Шлях |
|---|---|
| Сітка | `@revolist/react-datagrid` — вже в стеку для документа (`DocumentGrid.tsx:3-4`). ⚠ `DIRECTIVE-15-FRONTEND.md:122` каже «`DataTable` — не RevoGrid, той лише для сітки документа» про **переліки**; редактор даних — сітка, що редагується, тож виняток названо рішенням `R-19`. `DocumentGrid.tsx` **не змінюється** |
| Утиліти сітки | `features/grid/clipboard.ts`, `undo.ts`, `selection.ts`, `keyboardCompat.ts`, `columnWidths.ts`, `LookupCellEditor.ts`, `BoolCellEditor.ts`, `DateCellEditor.ts`, `UnitCellEditor.ts`, `listCellEditor.ts` |
| Оболонка й стани | `shared/ui/PageHeader.tsx`, `FilterBar.tsx`, `AsyncBoundary.tsx`, `ErrorAlert.tsx`, `Banner.tsx`, `DetailDrawer.tsx`, `KeyValue.tsx`, `TwoLine.tsx`, `CodeText.tsx`, `StatusBadge.tsx`, `UnsavedGuard.tsx`, `ConfirmModal.tsx`, `ReasonModal.tsx`, `Wizard/Wizard.tsx`, `DataTable/DataTable.tsx` (прев'ю імпорту, історія) |
| Вирази | `features/expressions/ExpressionEditor.tsx`, `completion.ts`, `hover.ts`, `describe.ts`, `language.ts`, `markers.ts` |

| Новий | Шлях |
|---|---|
| Сторінка даних | `pages/admin/RegistryEntriesPage.tsx` |
| Сітка довідника | `features/registries/editor/RegistryGrid.tsx`, `columns.ts`, `useRegistryRows.ts`, `pendingRows.ts`, `validateRows.ts`, `pasteRows.ts`, `RegistryGridToolbar.tsx` |
| Master-detail | `editor/MasterDetailLayout.tsx`, `editor/compositionChain.ts`, `editor/SumIndicator.tsx` |
| Шторка | `editor/EntryDrawer.tsx`, `editor/EntryHistory.tsx`, `editor/ExportMenu.tsx` |
| Ключі | `features/registries/keys/KeysTab.tsx`, `KeyChips.tsx`, `normalizeKey.ts`, `api.ts` |
| Зв'язки, правила | `features/registries/relations/RelationsDiagram.tsx`, `CompositionSettings.tsx`; `rules/RulesTab.tsx`, `rules/ruleTemplates.ts`, `rules/api.ts` |
| Імпорт | `features/registries/import/ImportWizard.tsx`, `import/steps/{File,Layout,Mapping,Preview}Step.tsx`, `import/mappingModel.ts`, `import/api.ts` |
| Застарілість | `features/registries/impact/ImpactBanner.tsx`, `impact/api.ts` |
| Формули | `features/expressions/RegistryPreviewPanel.tsx` |

### 8.11 Рядки інтерфейсу (ключі `UiString`, `en`)

| Ключ | Текст |
|---|---|
| `registries.editor.openData` | Open data |
| `registries.editor.asOf` | As of |
| `registries.editor.unsaved` | {count} unsaved changes |
| `registries.editor.saved` | Saved {time} |
| `registries.editor.saveChanges` | Save {count} changes |
| `registries.editor.addChild` | Add {name} |
| `registries.editor.readOnly` | Read only: editing needs the Registry.EditData permission. |
| `registries.editor.readOnlyExternal` | Read only: this registry is mastered by an external source. |
| `registries.editor.conflict` | {count} rows were changed by {user} after you opened them. |
| `registries.editor.keepMine` / `.takeTheirs` | Keep mine / Take theirs |
| `registries.editor.dupInBatch` | Same key as row {row}. |
| `registries.editor.dupExisting` | Key already used by {entryCode} ({keyText}). |
| `registries.editor.sum` | Σ {field} {value} |
| `registries.editor.sumTarget` | target {target} ± {tolerance} |
| `registries.editor.sumOutside` | Outside the tolerance by {delta} |
| `registries.editor.emptyTitle` | No entries yet |
| `registries.editor.emptyText` | Add entries one by one or import them from a CSV or Excel file. |
| `registries.editor.unmatchedPaste` | {count} cells could not be matched to a registry entry. |
| `registries.impact.stale` | {count} documents in open periods were calculated before your last change. |
| `registries.impact.review` | Review and recalculate… |
| `registries.keys.tab` | Keys |
| `registries.keys.hint` | A key makes a combination of fields unique. Formulas find an entry by the primary key. |
| `registries.keys.primary` / `.alternative` | Primary key / Alternative key |
| `registries.keys.ignoreCase` | Ignore letter case and extra spaces |
| `registries.keys.noOverlap` | Validity windows must not overlap |
| `registries.keys.ok` | No duplicates in {count} existing entries |
| `registries.keys.duplicates` | {groups} duplicate groups in existing entries — the key cannot be published yet. |
| `registries.keys.showInEditor` | Show in editor |
| `registries.relations.composition` | Part of parent |
| `registries.relations.cascade` / `.restrict` | Delete its parts too / Block while parts exist |
| `registries.rules.template.childSum` | Children add up to a value |
| `registries.rules.template.requiredWhen` | Required when |
| `registries.rules.template.crossRegistry` | Must exist in another registry |
| `registries.rules.template.custom` | Custom expression |
| `registries.rules.checked` | Checked {count} entries: {violations} violate |
| `registries.rules.violated` | Rule {rule} is not met |
| `registries.usageKind.templateFormula` / `.methodologyFormula` / `.registryRule` | Template formula / Methodology formula / Registry rule |
| `registries.import.orientationRows` / `.orientationColumns` | Each row is an entry / Each column is an entry (transposed, e.g. HYSYS) |
| `registries.import.unpivot` | Turn {count} columns into {child} rows |
| `registries.import.summary` | {new} new · {changed} changed · {unchanged} unchanged · {errors} errors |
| `registries.import.formulaMismatch` | The file gives formula {fileFormula} for {component}, but the registry has {registryFormula}. The registry value is kept. |
| `registries.import.formulaMissing` | {component} has no chemical formula in the file; carbon and sulphur atoms are set to 0. |
| `expr.preview.title` / `.run` / `.reads` | Check on a record / Run / Registry reads |
| `expr.registryUnknown` | Registry "{registry}" does not exist. |
| `expr.registryFieldUnknown` | Registry "{registry}" has no field "{field}". |
| `expr.registryFieldNotLookup` | Field "{field}" is not a reference, so "{next}" cannot follow it. |
| `expr.registryKeyArity` | {registry} is found by {count} key values ({fields}); {given} given. |
| `expr.rowReferenceOutsideScope` | ROW.{field} can be used only inside REGSUM, REGAVG, REGMIN, REGMAX, REGCOUNT or REGONE. |
| `expr.entryRefMisuse` | An entry of {registry} can only be compared with another entry of {registry}. |
| `expr.registryScanUnindexed` | This filter reads every entry of {registry}; compare a reference or key field with = to make it fast. |
| `err.ECR-REG-4092` (заголовок) / `.keyTaken` | Key already in use / Another entry ({entryCode}) already has {key} = {keyText}. |
| `err.ECR-REG-4093` (заголовок) / `.entryChanged` | Changed by someone else / Entry {entryCode} was changed after you opened it. |
| `err.ECR-REG-4221` (заголовок) / `.ruleViolated` | Registry rule not met / {rule}: {message} |
| `calculation.staleRegistry` | Registry "{name}" was changed after this calculation. |

Повний перелік ключів кожного кроку наводиться в його коміті; сторожі `Кожен_рядок_якого_просить_клієнт_є_в_каталозі`
і `Жоден_ключ_каталогу_не_повторюється_в_seed` (`EndpointCoverageTests.cs:158`, `:797`) перевіряють
повноту.

---

## 9. План виконання

### 9.0 Порядок роботи в репозиторії

- **Одна спільна гілка `dev/integration`, один PR у `main`, мерж раз на тиждень по семи зелених**
  (`CLAUDE.md`, розділ від 2026-09-20). Крок = атомарна серія комітів `[TYPE] опис`. Розмір кроку
  рахуємо як diff окремого PR, хоча PR спільний.
- Пуш: власна гілка від `origin/dev/integration` в **окремому worktree** → `git fetch` →
  `git rebase origin/dev/integration` (лише незапушені коміти) → `git push origin HEAD:dev/integration`.
  `--force`, `--no-verify`, `--admin` заборонені. Червоний гейт лагодиться наступним комітом у ту саму
  гілку.
- Спільні файли — `09-seed.sql`, `contracts/openapi.snapshot.json`, `src/Ecr.Web/src/api/schema.d.ts`,
  `shared/ui/**`. Про зміни в них попереджаємо іншу сесію, **регенерацію контракту**
  (`ECR_UPDATE_SNAPSHOT=1` + `schema.d.ts`) робимо останнім комітом кроку після rebase.
- **Append-only блоки для паралельності.** RT-01 заводить іменовані секції-маркери в
  `09-seed.sql` (`-- RT: keys`, `-- RT: data`, `-- RT: expressions`, `-- RT: ui-registries`,
  `-- RT: ui-expressions`) і в `src/Ecr.Application/DependencyInjection.cs` та
  `src/Ecr.Infrastructure/DependencyInjection.cs` (реєстрації handler'ів тут ручні,
  `DependencyInjection.cs:217`, `:244`). Далі кожен трек дописує лише у свій блок. `02-contracts.md`
  §7/§9 і `ErrorCodes.cs` дописуються рядками в різних місцях.
- Перед пушем — **повний** прогін (не лише своїх каталогів): `dotnet test` по кожному тестовому
  проєкту, `npm test`, `npm run lint`, `tsc --noEmit`. Кроки з UI і стендом додатково проганяють
  `tools/e2e-stand.ps1` з окремого worktree, кроки з міграціями й API — `tools/smoke.ps1`.

### 9.1 Кроки

Режим: «посл.» — послідовно у своєму треку. «хв. N» — хвиля N (§9.3): кроки однієї хвилі йдуть
паралельно, файли не перетинаються (перевірено колонкою «Файли для запису», за винятком узгоджених
append-only блоків).

| # | Підзадача | Файли для запису | Залежить від | DoD (як перевіримо) | Режим |
|---|---|---|---|---|---|
| RT-00 | Контракт мови, рішення, вимоги | `docs/build/FEATURE-REGISTRY-TABLES.md` (перенесення цього файлу), `docs/build/02b-expressions.md` (§1 EBNF `row_ref`/`this_ref`, §5 `EntryRef`, §6.4 `#N/A`/`#MULTI`, §7/§8 рядки функцій зі статусом «RT-20a/b», §12 перевірки 15–21), `docs/tz/10-decisions.md` (`R-1…R-21` → `D-150…D-170` — ✓ внесено 2026-09-27; `R-22…R-27` — окремо), `docs/tz/02-requirements.md` (`ФВ-8.15…8.18`, `ФВ-9.18`, `ФВ-9.19` — вносяться 2026-09-27 паралельним кроком документації після схвалення людиною рішень `R-*`; `RequirementCensus.cs:64-65`), `contracts/trace-exempt.md` (звільнення «до RT-…» для кожної нової ФВ) | ✓ схвалення `R-1…R-21` людиною — виконано 2026-09-27 | `RequirementTraceTests.Кожна_вимога_має_тест_або_явне_звільнення` зелений (нова ФВ або звільнена, або вже має тест); повний прогін зелений (02b структурно не читає жоден сторож: в `AggregateFunctionTests.cs:15,32` і `MethodologyOperatorTests.cs:106,133` — лише коментарі) | хв. 1 |
| RT-01 | M1: схема ключів | `Domain/Entities/Configuration/RegistryKeyDef.cs`, `RegistryKeyField.cs`, `Domain/Entities/Dictionaries/RegistryEntryKey.cs`, `Infrastructure/Persistence/Configurations/RegistryKeysConfiguration.cs`, `EcrDbContext.cs` (3 `DbSet`), міграція `RK01RegistryKeys` (+Designer, `EcrDbContextModelSnapshot.cs`), `docs/build/02a-db-schema.md` §3/§5, маркери в `09-seed.sql` і обох `DependencyInjection.cs`, `tests/Ecr.Infrastructure.Tests/Persistence/RegistryKeysSchemaTests.cs` | RT-00 | міграція вгору й униз на локальному SQL; `UX_RegistryEntryKey_Live` не пускає другий живий хеш, пропускає `IsLive = 0`; `UX_RegistryKeyDef_Primary` не пускає два первинні | хв. 2 |
| RT-02 | Нормалізатор ключа: сервер + клієнт на одній фікстурі | `Domain/Services/RegistryKeyNormalizer.cs`, `tests/Ecr.TestKit/Fixtures/registry-key-normalization.json` (≥ 40 випадків: регістр, пробіли, NFC, кирилиця, `-0`, хвостові нулі, `Lookup`, `null`-частини), `tests/Ecr.Domain.Tests/Registries/RegistryKeyNormalizerTests.cs`, `src/Ecr.Web/src/features/registries/keys/normalizeKey.ts`, `…/keys/__tests__/normalizeKey.test.ts` | RT-00 | обидва набори проганяють **усю** фікстуру; однакові хеші | хв. 2 |
| RT-07 | Мова: `ROW.`/`THIS`, інтерфейс знімка, значення помилок | `Ecr.Expressions/Ast/AstNode.cs`, `Ast/AstPrinter.cs`, `Parsing/Parser.cs`, `Evaluation/IRegistrySnapshot.cs`, `Binding/IRegistryShapeSource.cs`, `Evaluation/IEvaluationContext.cs` (член `Registries` з типовим `null`), `ExpressionErrors.cs`, `tests/Ecr.Expressions.Tests/Parsing/RowReferenceParsingTests.cs` | RT-00 | `ROW.COMPONENT.MW` → `RowFieldNode`; друк дерева повертає той самий текст; `THIS` поза режимом правил — діагностика; `CST.`/`HDR.` не зачеплені | хв. 2 |
| RT-03 | M2: композиція, режим коду, мітка зміни даних | `Domain/Enums/Enums.cs` (3 enum у кінець), `RegistryFieldDef.cs`, `RegistryDef.cs`, `Configurations/ConfigurationRestConfiguration.cs`, `Configurations/DictionariesConfiguration.cs` (послідовність), `Persistence/UnitOfWork.cs` (`DataChangedAt` при зміні `DataRevision`), міграція `RK02RegistryComposition`, `02a-db-schema.md`, тести домену й схеми | RT-01 | `CK_RegField_Composition` не пускає композицію на не-`Lookup`; `DataChangedAt` ставиться саме тоді, коли зросла ревізія | хв. 3, посл. |
| RT-04 | M3: системна історія + автор зміни | `DictionariesConfiguration.cs` (`.IsTemporal(...)`, `HasPrecision(3)`), `RegistryEntry.cs`, `RegistryValue.cs` (`ChangedByUserId`), `UnitOfWork.cs` (автор з `ICurrentUser`), міграція `RK03RegistryTemporalHistory`, `02a-db-schema.md` (+ `CREATE TABLE dic.RegistryEntryHistory/RegistryValueHistory`), `tests/Ecr.Infrastructure.Tests/Persistence/RegistryTemporalTests.cs` | RT-03 | `AS OF` момент до правки повертає старе значення; історичний рядок має автора; перетворення id `int↔long` (`DictionariesConfiguration.cs:41`) працює в `TemporalAsOf` | хв. 3, посл. |
| RT-05 | M4: використання довідників і момент знімка | `Domain/Entities/Configuration/RegistryUse.cs`, `Domain/Entities/Calculations/CalculationRun.cs`, конфігурації, міграція `RK04RegistryUseAndRunAsOf`, `02a-db-schema.md`, тести | RT-04 | `CalculationRun.Start` ставить `RegistryAsOfUtc`; `CK_RegUse_Kind` працює | хв. 3, посл. |
| RT-06 | M5: профілі імпорту | `Domain/Entities/Configuration/RegistryImportProfile.cs`, `Configurations/RegistryImportProfileConfiguration.cs`, `EcrDbContext.cs`, міграція `RK05RegistryImportProfile`, `02a-db-schema.md`, тести | RT-05 | `CK_RegImpProfile_Json` не пускає невалідний JSON | хв. 3, посл. |
| RT-10a | Ключі при записі: сервіс, сховище, 409 | `Application/Registries/Keys/RegistryKeyService.cs`, `Ports/IRegistryKeyStore.cs`, `Infrastructure/Persistence/RegistryKeyStore.cs` (`UPDLOCK, HOLDLOCK`), `UpsertRegistryEntryHandler.cs` (виклик після `ApplyValuesAsync`, транзакція), `Domain/Errors/ErrorCodes.cs` (`RegistryKeyConflict`), `Api/Errors/ExceptionHandlingMiddleware.cs` (арм 409), `09-seed.sql` (блок keys), `02-contracts.md` (§7 рядок, порт), `contracts/trace-exempt.md` (зняти рядок `ФВ-8.15`), тести | RT-01, RT-02, RT-04 | другий запис із тим самим ключем → 409 з `keyText`, `entryCode`; для темпорального довідника дубль у вікні, що не перетинається, проходить; тести мають `[Trait("Requirement","ФВ-8.15")]` | хв. 4 |
| RT-10b | Ключі: гонка, CSV, вікно, видалення | `UnitOfWork.cs` (гілка `RegistryEntryKey` → `keyTakenConcurrently`, Д-3), `RegistryEntryCsvHandlers.cs` (дубль ключа у файлі; пошук наявного за первинним ключем), `SetEntryValidityHandler.cs` (перевірка перетину), `RegistryAdminHandlers.cs` (видалення → `IsLive = 0`), `09-seed.sql`, тести (два паралельні upsert на SQL) | RT-10a | з двох одночасних записів рівно один успішний, другий — 409, а не 500; дубль у CSV з номерами обох рядків | хв. 5 |
| RT-11 | Ключі в описі + живий пошук дублікатів | `Registries/Dto/RegistryDefinitionDto.cs` (`RegistryKeyDto`, `RegistryKeySaveDto`, `Keys`), `RegistryDefinitionHandlers.cs` (ApplyKeys, наповнення, `existingDuplicates`, послаблення `noKeyField`), `RegistryDefinitionDraftHandlers.cs`, `Registries/Keys/CheckRegistryKeyHandler.cs`, `Api/Controllers/RegistryDefinitionToolsController.cs` (`POST keys/check`), `02-contracts.md` §9, `09-seed.sql`, `src/Ecr.Web/src/features/registries/keys/api.ts` (споживач) + тест; регенерація контракту | RT-10b | публікація ключа на даних із дублікатами → 409 з прикладами; без дублікатів — рядки ключів заповнено в тій самій транзакції | хв. 6 |
| RT-12 | Композиція й авто-код | `RegistryDefinitionHandlers.cs` (обмеження §4.8), `RegistryAdminHandlers.cs` (каскад), `UpsertRegistryEntryHandler.cs` і `RegistryEntryCsvHandlers.cs` (код із послідовності), `RegistryResolver.cs` (видимість дітей), `Ports/IRegistryStore.cs` + `RegistryStore.cs` (`NextEntryCodeAsync`, підрахунок посилань без дітей), `09-seed.sql`, `contracts/trace-exempt.md` (зняти `ФВ-8.16`), тести | RT-11, RT-03 | цикл композиції → 422; каскад видаляє 3 рівні й звільняє ключі; дитина невидима, коли невидимий батько | хв. 7 |
| RT-13 | Рядки зі значеннями | `Application/Registries/Rows/GetRegistryRowsHandler.cs`, `Rows/RegistryRowDto.cs`, `Ports/IRegistryRowsQuery.cs`, `Infrastructure/Persistence/RegistryRowsQuery.cs`, `Api/Controllers/RegistryRowsController.cs` (`GET rows`), `02-contracts.md` §9 + порт, `src/Ecr.Web/src/features/registries/rows/api.ts` + тест; регенерація контракту | RT-04, RT-10a | курсор без пропусків; `decimal` рядком; `limit > 500` → 400 (`ApiConventionTests.Розмір_сторінки_понад_максимум_відхиляється_400`); `version` змінюється після правки значення | хв. 5 |
| RT-14 | Пакетний запис із `dryRun` | `Rows/RegistryBatchHandler.cs`, `Rows/RegistryBatchDtos.cs`, `RegistryRowsController.cs` (`POST entries/batch`), `ErrorCodes.cs` (`RegistryEntryChanged` 4093), `09-seed.sql`, `02-contracts.md` §7/§9, `web features/registries/rows/api.ts` (+`saveBatch`); регенерація | RT-13, RT-12 | `dryRun` нічого не пише (перевірка — лічильник рядків і `DataRevision`); обмін ключами в пакеті проходить (дві фази); застарілий `baseVersion` → рядок `entryChanged` | хв. 8 |
| RT-15 | Історія запису | `Rows/GetRegistryEntryHistoryHandler.cs`, `RegistryRowsQuery.cs` (`TemporalAll`), `RegistryRowsController.cs`, `02-contracts.md` §9, web споживач; регенерація | RT-14 | правка значення, вікна й видалення дають три рядки історії з автором | хв. 9 |
| RT-16 | Експорт CSV/XLSX | `Registries/Export/ExportRegistryHandler.cs`, `Ports/IRegistryWorkbookWriter.cs`, `Ecr.Adapters.Excel/RegistryWorkbookWriter.cs` (ClosedXML, `Directory.Packages.props:53`), `Ecr.Adapters.Excel/DependencyInjection.cs`, `RegistryRowsController.cs`, `02-contracts.md` §9 + порт, web споживач; регенерація | RT-15 | експорт → імпорт того самого файлу дає «0 changed»; `decimal` без втрати знаків | хв. 10 |
| RT-17a | Рушій правил + інтеграція в запис | `Registries/Rules/RegistryRuleEngine.cs`, `RegistryRuleContext.cs`, `RegistryRuleTemplates.cs`, `UpsertRegistryEntryHandler.cs`, `Rows/RegistryBatchHandler.cs`, `RegistryEntryCsvHandlers.cs`, `RegistryDefinitionHandlers.cs` (компіляція виразів, відмова `UniqueWithin`, `RegistryUse` з `SourceKind = 2`), `ErrorCodes.cs` (`RegistryRuleViolation` 4221), `09-seed.sql`, `02-contracts.md` §7, `contracts/trace-exempt.md` (зняти `ФВ-8.18`), тести | RT-12, RT-14, RT-20b, RT-21, RT-22 | `Error` у пакеті → 422 з переліком; `Warning` → `warnings[]`; зміна дитини перевіряє правило батька | хв. 9 |
| RT-17b | Перевірка правила на наявних даних | `Registries/Rules/CheckRegistryRuleHandler.cs`, `RegistryDefinitionToolsController.cs` (`POST rules/check`), `02-contracts.md` §9, `web features/registries/rules/api.ts`; регенерація | RT-17a | на фікстурі FLERT: 4 порушення `SUM_100` з правильними записами | хв. 10 |
| RT-18a | Імпорт: специфікація, планувальник, прев'ю | `Registries/Import/RegistryImportSpec.cs`, `RegistryImportPlanner.cs`, `RegistryImportPreviewHandler.cs`, `Ports/IRegistryWorkbookReader.cs`, `Ecr.Adapters.Excel/RegistryWorkbookReader.cs`, `Api/Controllers/RegistryImportController.cs` (`POST import/preview`), `02-contracts.md` §9 + порт, `09-seed.sql`, `web features/registries/import/api.ts`, фікстури `tests/Ecr.TestKit/Fixtures/registry-import/{current-flat.csv,streams-offshore-transposed.xlsx,components-addstream.xlsx}` (підписи компонентів — не дані замовника, §11.4; `component-mw-hysys.csv` з RT-23a перевикористовується); регенерація | RT-14, RT-06 | транспонований фрагмент розгортається в потоки/кейси/рядки складу; дублі `CO`/`Carbon_Monoxide` з різними значеннями → помилка рядка; 0-значення пропускаються за прапорцем; профіль `ADDSTREAM_COMPONENTS` з підписів `streams Onshore!A10:A54` дає `FORMULA`, `N_C`, `N_H`, `N_S` (кирилиця `С`/`Н` у «Toluene (С7Н8)» нормалізується; «Propyl mercaptan (C5H8S)» проти наявного `C3H8S` — попередження `registries.import.formulaMismatch`, значення не перезаписується); псевдокомпонент без формули (`CN1_35*`) — `N_C`/`N_S` = 0 з попередженням `registries.import.formulaMissing` | хв. 10 |
| RT-18b | Імпорт: застосування | `Import/RegistryImportApplyHandler.cs`, `Import/RegistryImportJob.cs` (через `IBackgroundJobScheduler`), `RegistryImportController.cs`, `02-contracts.md` §9, web споживач; регенерація | RT-18a, RT-17a | > 5 000 рядків → 202 і задача; застаріле прев'ю → 409 `importPreviewStale`; `ApiConventionTests.Довга_операція_повертає_202…` | хв. 11 |
| RT-18c | Профілі імпорту | `Import/RegistryImportProfileHandlers.cs`, `RegistryImportController.cs`, `02-contracts.md` §9, web споживач; регенерація | RT-18b | `If-Match` зі старим `rowVersion` → 409 `profileChanged` | хв. 12 |
| RT-19 | «Де використано»: формули й правила | `RegistryStore.cs` (`FindDefinitionUsageAsync` + 3 види з `cfg.RegistryUse`), `Application/Common/UsageKinds` (константи), `09-seed.sql` (`registries.usageKind.*`), `web features/registries/RegistryUsage.tsx` (підписи), тести | RT-23b, RT-24, RT-17a | формула методології, формула шаблону й правило видно у відповіді з правильним посиланням | хв. 11 |
| RT-20a | Функції пошуку: `REGFIND`, `REGONE`, `REGFIELD` (шлях) | `Ecr.Expressions/Evaluation/RegistryForms.cs`, `Evaluation/Evaluator.cs` (диспетчер спецформ в обох діалектах), `Functions/DialectCatalog.cs` (Extension), `Functions/FunctionRegistry.cs`, `Functions/TemplateFunctions.cs`, `tests/Ecr.TestKit/InMemoryRegistrySnapshot.cs`, `tests/Ecr.Expressions.Tests/Functions/RegistryLookupFunctionTests.cs`, `ClientServerEquivalenceTests.cs` (лічильник 13 → 15, виключення), `DialectCatalogTests.cs` | RT-07 | `#N/A`, `#MULTI`, `null`-частина → `null`, шлях через `Lookup`, невидимий запис → `#REF`; `Legacy` → `IsAllowedIn = false` | хв. 3 |
| RT-20b | Агрегати й область `ROW` | `RegistryForms.cs`, `Evaluation/RowScopeContext.cs`, `DialectCatalog.cs`, `FunctionRegistry.cs`, `ClientServerEquivalenceTests.cs` (15 → 20), `tests/Ecr.Expressions.Tests/Functions/RegistryAggregateFunctionTests.cs` | RT-20a | порожня множина (`REGSUM` = 0, решта — `null`), `null` поглинаються, перша помилка; індексний шлях переглядає лише дітей (лічильник кроків); повний перегляд 30 тис. рядків → `#BUDGET` | хв. 4 |
| RT-21 | Статична перевірка: довідники, поля, ключі, одиниці, `EntryRef`, залежності | `Binding/ReferenceResolver.cs`, `Binding/TypeChecker.cs`, `Binding/UnitChecker.cs`, `Binding/DependencyExtractor.cs`, `09-seed.sql` (блок expressions, `expr.*`), тести | RT-20b | перевірки 15–21 (§5.5) з позиціями; Д-5 закрито (описка в `REGFIELD` дає помилку публікації) | хв. 5 |
| RT-22 | Завантажувач знімка | `Ports/IRegistrySnapshotLoader.cs`, `Application/Registries/RegistrySnapshot.cs`, `Infrastructure/Persistence/RegistrySnapshotLoader.cs`, DI (блок expressions), `02-contracts.md` (порт), `contracts/trace-exempt.md` (зняти `ФВ-8.17`), `tests/Ecr.Infrastructure.Tests/Persistence/RegistrySnapshotLoaderTests.cs` | RT-04, RT-05, RT-07 | дата: запис, закритий 15-го, невидимий на кінець місяця; `AS OF`: правка після моменту невидима; видалені й діти невидимого батька відсутні | хв. 4 |
| RT-23a | Методологія бачить довідник + **золотий тест 301** | `Ecr.Calculations/CalculationInputBuilder.cs` (`Lookup` → `EntryRef`, лише `Strict`), `Application/Ports/ICalculationModule.cs` (`CalculationArgument` + `long? EntryId = null` у кінець), `MethodologyEvaluationContext.cs`, `GenericCalculationModule.cs` (знімок у `PrepareAsync`), `CalculationOrchestrator.cs` (`RegistryAsOfUtc`, кеш знімків на прогін), `tests/Ecr.Calculations.Tests/Registries/Hse301GoldenTests.cs`, `tests/Ecr.TestKit/Fixtures/flert-1d2-370-winter.json` (склад кейсу — за робочим припущенням RQ-1), `tests/Ecr.TestKit/Fixtures/registry-import/component-mw-hysys.csv` (новий: код, формула, `N_C`, `N_S`, M HYSYS для 33 компонентів моря, §11.4), `contracts/trace-exempt.md` (зняти `ФВ-9.18`) | RT-20b, RT-22 | AC-1 (§10); версія `Legacy` дає побітно ті самі числа, що до кроку | хв. 5 |
| RT-23b | Публікація методології: перевірки й `RegistryUse` | `Calculations/MethodologyPublishChecks.cs`, `PublishMethodologyHandler.cs`, `Ports/IRegistryUseStore.cs`, `Infrastructure/Persistence/RegistryUseStore.cs`, тести | RT-21, RT-23a | описка в полі → 422 з позицією; `REGSUM` у `Legacy` → `ECR-CALC-0433`; `RegistryUse` переписано | хв. 6 |
| RT-24 | Шаблони на знімку; `REGFIELD` читає `Lookup`/`Unit`; `null` для порожнього (Д-2) | `Registries/RegistryFieldSnapshotLoader.cs` (з D16-04: → `IRegistrySnapshotLoader`), `Recalculation/RecalculationService.cs`, `Recalculation/SliceEvaluationContext.cs`, публікація шаблону (`Templates/PublishChecks.cs`: перевірки + `RegistryUse`), тести | RT-22, RT-21, **D16-04** ([DIRECTIVE-16 §2 D16-04](DIRECTIVE-16.md), виконано в `5f00b791` і `400c58b1`) | формула шаблону з `REGSUM` рахується в перерахунку; правило валідації документа з функцією довідника рахується через механізм D16-04 на «Перевірити», поданні **і збереженні** (`PatchCellsHandler`, `400c58b1`, §5.12); храповик `PatchCellsQueryCountTests` без змін | хв. 7 |
| RT-25 | Свіжість і перерахунок зачеплених | `Infrastructure/Persistence/MethodologyStore.cs` (`GetCalculationFreshnessAsync`), `Ports/IMethodologyStore.cs` (`ChangedRegistries` у кінець), `Documents/GetCalculationResultsHandler.cs`, `Registries/Impact/RegistryImpactHandlers.cs`, `Api/Controllers/RegistryImpactController.cs`, `02-contracts.md` §9, `09-seed.sql`, `contracts/trace-exempt.md` (зняти `ФВ-9.19`), `web features/registries/impact/api.ts`; регенерація | RT-05, RT-23b, RT-24 | правка складу → результат документа «застарілий» з назвою довідника; `impact` не повертає закритих періодів; `recalculate-impacted` → 202 | хв. 8 |
| RT-26 | Метадані мови й прев'ю | `Expressions/GetExpressionMetadataHandler.cs` (`registries[]`), `Expressions/PreviewExpressionHandler.cs`, `Api/Controllers/ExpressionsController.cs` (`POST preview`), `02-contracts.md` §9, `web features/expressions/api.ts`; регенерація | RT-22, RT-21 | прев'ю μ для (1D-2, 370 Winter) = AC-1; без `Registry.View` → 403 | хв. 6 |
| RT-30 | Сторінка даних: маршрут, каркас, читання | `app/routes.ts`, `pages/admin/RegistryEntriesPage.tsx`, `features/registries/editor/{RegistryGrid.tsx,columns.ts,useRegistryRows.ts}`, `pages/admin/RegistriesPage.tsx` (посилання), `api/queryKeys.ts` (append), `09-seed.sql` (блок ui-registries), `src/test/__tests__/a11yFixtures.tsx`, `accessibility.part3.a11y.test.tsx`, `e2e/screenshots.spec.ts` | RT-13 | 4 стани + лише читання; a11y обох тем; знімок e2e обох тем | хв. 6 |
| RT-31 | Редагування в сітці | `editor/{RegistryGrid.tsx,pendingRows.ts,validateRows.ts,pasteRows.ts,RegistryGridToolbar.tsx}`, `09-seed.sql`, тести | RT-30, RT-14, RT-02 | вставка блоку з Excel з `Lookup` за назвою; дубль ключа підсвічено до збереження; `Ctrl+S` → один пакет; конфлікт → банер | хв. 9 |
| RT-32 | Master-detail + Σ | `editor/{MasterDetailLayout.tsx,compositionChain.ts,SumIndicator.tsx}`, `RegistryEntriesPage.tsx`, тести, a11y | RT-31, RT-12, RT-17a | 3 панелі за ланцюжком композиції; `F6`; Σ = серверному значенню правила на фікстурі | хв. 10 |
| RT-33 | Шторка запису, історія, експорт | `editor/{EntryDrawer.tsx,EntryHistory.tsx,ExportMenu.tsx}`, `RegistryGrid.tsx` (відкриття шторки), тести | RT-32, RT-15, RT-16 | `?panel=<id>` відкриває шторку за посиланням; «as of» показує старе значення | хв. 11 |
| RT-34 | Конструктор: вкладка «Keys» | `features/registries/keys/{KeysTab.tsx,KeyChips.tsx}`, `pages/admin/RegistryConstructorPage.tsx`, `features/registries/definition.ts`, `registryDraft.ts`, `09-seed.sql`, тести, a11y | RT-11 | живий пошук дублікатів; порядок чипів з клавіатури; Publish вимкнено з поясненням | хв. 7 |
| RT-35 | Конструктор: «Relations» (ERD), «Rules», «Usage» | `features/registries/relations/{RelationsDiagram.tsx,CompositionSettings.tsx}`, `rules/{RulesTab.tsx,ruleTemplates.ts}`, `RegistryConstructor.tsx` (заміна `RegistryRelations`/`RegistryRules`), `RegistryConstructorPage.tsx`, `09-seed.sql`, тести, a11y | RT-34, RT-17b, RT-19, RT-40 | шаблон «Children add up» генерує вираз; «Check existing entries» показує 4 порушення; ERD доступна з клавіатури | хв. 12 |
| RT-36 | Майстер імпорту | `features/registries/import/{ImportWizard.tsx,mappingModel.ts,steps/*.tsx}`, `RegistryImportPanel.tsx` (заміна на майстер), `09-seed.sql`, тести, a11y | RT-18c, RT-31 | транспонований файл → прев'ю → застосування → банер застарілості | хв. 13 |
| RT-40 | Monaco: автодоповнення, підпис, hover | `features/expressions/{completion.ts,hover.ts,describe.ts,language.ts,monaco.ts}`, тести | RT-26 | `ROW.COMPONENT.` пропонує поля `COMPONENT`; hover показує тип і одиницю; `ROW`/`THIS` підсвічені | хв. 7 |
| RT-41 | «Check on a record» | `features/expressions/RegistryPreviewPanel.tsx`, `ExpressionEditor.tsx` (вбудувати), тести, a11y | RT-40 | число для (1D-2, 370 Winter) = AC-1; стан помилки з `#N/A` | хв. 8 |
| RT-50 | Наскрізний сценарій FLERT, e2e, smoke | `tests/Ecr.Scenarios.Tests/Registries/FlertRegistryScenario.cs`, `src/Ecr.Web/e2e/registryEditor.spec.ts`, `tools/smoke.ps1` (2 кроки: ключ → 409; `REGSUM` у прев'ю) | усі, крім RT-37 | AC-1…AC-15; `smoke.ps1` і `e2e-stand.ps1` зелені з окремого worktree | хв. 14, посл. |
| RT-51 | Документація | `docs/reference/design/12-reference-data.md` (§3–4 — позначки «реалізовано RT-…»), `docs/build/02b-expressions.md` (статуси), `docs/build/FEATURE-REGISTRY-TABLES.md` (стан кроків) | RT-50 | `git grep "заплановано RT-" docs` порожній | хв. 14, посл. |
| RT-37 | (P2) Зведений вигляд «компоненти в стовпцях» | `editor/{PivotView.tsx,pivot.ts}`, тести | RT-32 | окремим рішенням після приймання | після |

### 9.2 Сторожі, мутаційні докази, розмір

Сім гейтів CI (`build`, `test`, `honesty-guard`, `server`, `client`, `a11y (dark)`, `a11y (light)`)
проходять на кожен пуш. Нижче — **специфічні** сторожі, які крок зачіпає. **Мутаційний доказ** —
навмисна поломка перевіреного, яка мусить зробити названий тест червоним. Результат «червоний до /
зелений після» вноситься в коміт кроку. Розмір — оцінка diff без згенерованих файлів (міграції,
`openapi.snapshot.json`, `schema.d.ts`).

| # | Сторожі | Мутаційний доказ | Розмір (з них тести) |
|---|---|---|---|
| RT-00 | — (документи) | не застосовно | ~250 |
| RT-01 | `PhysicalModelTests` (контрактні таблиці, схеми, `float`), перевірка незастосованих змін моделі EF | прибрати `WHERE IsLive = 1` → `Видалений_запис_не_блокує_ключ` червоний; прибрати `UNIQUE` → `Дубль_живого_ключа_відхиляється` червоний | ~350 (150) |
| RT-02 | спільна фікстура (C# + TS) | прибрати згортання пробілів у C# → випадок `'370  Winter'` червоний у C#, TS зелений, тобто розбіжність видно | ~300 (180) |
| RT-07 | `AstPrinter` round-trip, наявні тести парсера | прибрати гілку `ROW` у `ParseIdentifier` → `ROW_COMPONENT_MW_розбирається_у_шлях` червоний | ~300 (150) |
| RT-03 | `PhysicalModelTests`, міграції | прибрати `CK_RegField_Composition` → `Композиція_лише_на_Lookup` червоний; не ставити `DataChangedAt` → `Мітка_зміни_росте_з_ревізією` червоний | ~250 (100) |
| RT-04 | `PhysicalModelTests` (історичні таблиці в 02a) | прибрати `.IsTemporal` у `RegistryValue` → `AS_OF_повертає_старе_значення` червоний | ~250 (120) |
| RT-05 | `PhysicalModelTests` | не ставити `RegistryAsOfUtc` у `Start` → `Прогін_фіксує_момент_знімка` червоний | ~200 (80) |
| RT-06 | `PhysicalModelTests` | прибрати `ISJSON` → `Профіль_з_невалідним_JSON_відхиляється` червоний | ~200 (80) |
| RT-10a | `ContractIntegrityTests` (§7 ↔ `ErrorCodes`), `ServerErrorCodeLiteralTests`, `ErrorTitleCatalogTests`, `MessageKeyRatchetTests`, `SeedCatalogTextTests`, `RequirementTraceTests`, `Кожен_порт_застосунку_названий_у_контракті` | прибрати виклик `RegistryKeyService` з upsert → `Другий_запис_з_тим_самим_ключем_409` червоний; прибрати арм → той самий тест отримує 422 | ~400 (200) |
| RT-10b | ті самі + інтеграційні на SQL | прибрати гілку в `TryMapDuplicateKey` → `Гонка_двох_записів_дає_409_а_не_500` червоний (перевірено до фіксу) | ~380 (200) |
| RT-11 | `EndpointCoverageTests` (контракт ↔ контролер ↔ клієнт ↔ право; `Кожна_успішна_відповідь_має_оголошений_тип`), `ApiConventionTests`, звірка OpenAPI | не перевіряти дублікати при публікації → `Ключ_на_даних_з_дублікатами_409` червоний | ~750 (320, ≥ 40 %) |
| RT-12 | `ContractIntegrityTests`, `SeedCatalogTextTests`, `RequirementTraceTests` | вимкнути DFS → `Цикл_композиції_відхиляється` червоний; не виключати дітей з підрахунку → `Каскад_видаляє_батька_з_частинами` червоний | ~400 (200) |
| RT-13 | `EndpointCoverageTests`, `ApiConventionTests.Списковий_ендпоінт_повертає_сторінку…`, `.Курсор_наступної_сторінки…`, `.Числа_передаються_рядком…` | прибрати `ORDER BY Id` із курсора → `Курсор_без_пропусків` червоний | ~700 (300) |
| RT-14 | `EndpointCoverageTests`, `ContractIntegrityTests` (4093), OpenAPI | прибрати двофазний запис ключів → `Обмін_ключами_в_пакеті` червоний; `dryRun` без відкату → `DryRun_не_змінює_ревізію` червоний | ~780 (330) |
| RT-15 | `EndpointCoverageTests`, OpenAPI | читати поточну таблицю замість `TemporalAll` → `Історія_має_три_рядки` червоний | ~450 (200) |
| RT-16 | `EndpointCoverageTests`, `Кожен_порт_застосунку_названий_у_контракті`, OpenAPI | писати `decimal` через `double` → `Експорт_імпорт_без_змін` червоний (16 знаків) | ~550 (230) |
| RT-17a | `ContractIntegrityTests` (4221), `ServerErrorCodeLiteralTests`, `RequirementTraceTests` | не переоцінювати правило батька при зміні дитини → `Зміна_рядка_складу_перевіряє_Σ_кейсу` червоний | ~400 (200) |
| RT-17b | `EndpointCoverageTests`, OpenAPI | перевіряти лише перші N записів → `Усі_220_записів_перевірено` червоний | ~350 (160) |
| RT-18a | `EndpointCoverageTests`, `Кожен_порт…`, OpenAPI | прибрати `MustMatch` → `Дублі_CO_з_різними_значеннями_помилка` червоний | ~800 (340) |
| RT-18b | `ApiConventionTests.Довга_операція_повертає_202…`, OpenAPI | не звіряти `DataRevision` у токені → `Застаріле_прев'ю_409` червоний | ~500 (220) |
| RT-18c | `EndpointCoverageTests`, OpenAPI | ігнорувати `If-Match` → `Старий_rowVersion_409` червоний | ~350 (160) |
| RT-19 | `EndpointCoverageTests.Кожен_рядок_якого_просить_клієнт…` | прибрати вид `MethodologyFormula` → `Формула_методології_у_використанні` червоний | ~250 (120) |
| RT-20a | `ClientServerEquivalenceTests` (лічильник і виключення), `DialectCatalogTests.Ядро_це_рівно_двадцять_дві_функції` (лишається 22), `EvaluatorDepthGuardTests` | повернути `#REF` замість `#N/A` → `REGFIND_не_знайшов_дає_NA` червоний; дозволити `Legacy` → `REGFIND_у_Legacy_недозволена` червоний | ~400 (200) |
| RT-20b | те саме + бюджет | вимкнути індексний шлях → `Індексний_шлях_перебирає_лише_дітей` червоний (лічильник кроків) | ~400 (220) |
| RT-21 | `MessageKeyRatchetTests`, `SeedCatalogTextTests` | прибрати перевірку сегмента шляху → `Описка_в_полі_REGFIELD_помилка_публікації` червоний | ~400 (200) |
| RT-22 | `Кожен_порт…`, `RequirementTraceTests` | прибрати фільтр видимості батька → `Діти_невидимого_кейсу_невидимі` червоний | ~400 (200) |
| RT-23a | `RequirementTraceTests`; наявні тести `Legacy` | передавати `EntryRef` і в `Legacy` → `Legacy_побітно_як_до_кроку` червоний; зламати `ROW.COMPONENT.MW` на `MW` кейсу → `Hse301_MU` червоний | ~400 (220) |
| RT-23b | наявні тести публікації методології | не писати `RegistryUse` → `Публікація_записує_використання` червоний | ~350 (170) |
| RT-24 | наявні тести перерахунку; D16-04 | повернути `#REF` для порожнього поля → `Порожнє_поле_довідника_null` червоний | ~400 (200) |
| RT-25 | `EndpointCoverageTests`, `ApiConventionTests` (202), `RequirementTraceTests` | прибрати доданок `DataChangedAt` → `Правка_довідника_робить_результат_застарілим` червоний; прибрати фільтр стану періоду → `Impact_без_закритих_періодів` червоний | ~750 (320) |
| RT-26 | `EndpointCoverageTests`, OpenAPI | не перевіряти `Registry.View` → `Прев'ю_без_права_403` червоний | ~600 (260) |
| RT-30 | `accessibility.part3.a11y` (обидві теми), `contrast.test.ts`, `lintRules.test.ts`, `screenshots.spec.ts`, `EndpointCoverageTests.Кожен_рядок_якого_просить_клієнт…` | прибрати `aria-rowcount` → a11y-тест сітки червоний | ~400 (160) |
| RT-31 | a11y, `renderFeedback`-лічильники (без регресу рендерів) | прибрати виклик `normalizeKey` → `Дубль_ключа_підсвічено_до_збереження` червоний | ~400 (200) |
| RT-32 | a11y обох тем, `contrast.test.ts` | рахувати Σ без дочірніх рядків з дубль-стовпців → `Σ_дорівнює_серверному` червоний | ~350 (160) |
| RT-33 | a11y, `screenshots.spec.ts` | не передавати `asOf` → `Історія_показує_старе_значення` червоний | ~350 (160) |
| RT-34 | a11y, `constructor.test.tsx` | прибрати дебаунс-виклик перевірки → `Живий_пошук_дублікатів` червоний | ~350 (160) |
| RT-35 | a11y, `constructor.test.tsx` | зламати генерацію виразу шаблону → `Шаблон_childSum_дає_вираз` червоний | ~400 (180) |
| RT-36 | a11y, `Wizard.a11y.test.tsx` | пропустити крок Preview → `Імпорт_без_прев'ю_неможливий` червоний | ~400 (180) |
| RT-40 | `intellisense.test.ts`, `completion.test.ts` | не проходити `Lookup` у шляху → `ROW_COMPONENT_пропонує_поля_COMPONENT` червоний | ~350 (180) |
| RT-41 | a11y | показувати `#N/A` як число 0 → `Прев'ю_показує_помилку` червоний | ~300 (140) |
| RT-50 | `smoke.ps1`, `e2e-stand.ps1` (ci-exempt, вручну) | вимкнути `UX_RegistryEntryKey_Live` на стенді → крок smoke «дубль → 409» червоний | ~400 |
| RT-51 | — | не застосовно | ~150 |

### 9.3 Хвилі (≤ 4 паралельно, файли не перетинаються)

| Хвиля | Кроки | Чому саме так |
|---|---|---|
| 1 | RT-00 | контракт до коду |
| 2 | RT-01 ‖ RT-02 ‖ RT-07 | міграція, нормалізатор і парсер у різних проєктах |
| 3 | RT-03 → RT-04 → RT-05 → RT-06 (посл.) ‖ RT-20a | міграції по одній; `Ecr.Expressions` не перетинається з `Domain/Infrastructure` |
| 4 | RT-10a ‖ RT-20b ‖ RT-22 | довідники ‖ мова ‖ завантажувач |
| 5 | RT-10b ‖ RT-13 ‖ RT-21 ‖ RT-23a | `UnitOfWork`/CSV ‖ новий контролер рядків ‖ Binding ‖ Calculations |
| 6 | RT-11 ‖ RT-23b ‖ RT-26 ‖ RT-30 | опис ‖ публікація методології ‖ метадані ‖ UI-каркас |
| 7 | RT-12 ‖ RT-24 (після D16-04 — виконано в `5f00b791` і `400c58b1`) ‖ RT-34 ‖ RT-40 | |
| 8 | RT-14 ‖ RT-25 ‖ RT-41 | |
| 9 | RT-15 ‖ RT-17a ‖ RT-31 | |
| 10 | RT-16 ‖ RT-17b ‖ RT-18a ‖ RT-32 | |
| 11 | RT-18b ‖ RT-19 ‖ RT-33 | |
| 12 | RT-18c ‖ RT-35 | |
| 13 | RT-36 | |
| 14 | RT-50 → RT-51 | інтеграція |

⚠ Перетини, узгоджені як append-only: `09-seed.sql` (власні секції), `02-contracts.md` (§7 — рядки
кодів, §9 — рядки маршрутів у різних місцях таблиці, перелік портів), обидва `DependencyInjection.cs`
і `Ecr.Adapters.Excel/DependencyInjection.cs` (власні блоки; хвиля 10 — RT-16 і RT-18a), `api/queryKeys.ts`
(власні ключі), `contracts/trace-exempt.md` (кожен крок знімає **свій** рядок). `docs/tz/02-requirements.md`
правиться лише в RT-00 і RT-51. Кожен крок з регенерацією контракту робить її останнім комітом
після rebase.

⚠ **Спільне з [FEATURE-HSE301-VIEW](FEATURE-HSE301-VIEW.md#113-хвилі-й-перетин-файлів)
(узгоджено 2026-09-27).** Спільних **таблиць** немає: цей план змінює `cfg.Registry*`,
`dic.Registry*` і `calc.CalculationRun` (`RegistryAsOfUtc`), HSE301 — `ext.EntityFieldMap`,
`ext.RowWindow*`, `ext.SourceEvent*`, `calc.MethodologyFormula`, `calc.MethodologyOutput`,
`calc.CalculationResult`, `calc.CalculationStep` і `cfg.ViewDef`/`cfg.ViewVersion`. Серіалізується інше:

- **Міграції** обох документів (`RK01…RK05` тут, `HSE301M1…M5` там) переписують
  `EcrDbContextModelSnapshot.cs`, тож черга **одна на обидві сесії**: у польоті не більше однієї
  міграції, наступна генерується після rebase на попередню, у порядку готовності.
- **Код розрахунку:** `ICalculationModule.cs` і `GenericCalculationModule.cs` (HSE301 A3a → A3b →
  тут RT-23a), `PublishMethodologyHandler.cs` (HSE301 A3a → тут RT-23b),
  `MethodologyEvaluationContext.cs` (HSE301 F1 → тут RT-23a). Якщо обидва кроки готові
  одночасно, першим іде HSE301: його типові значення (`Scope = Substance`, `Kind = Output`) чинної
  поведінки не змінюють, тож RT-23a їх не помічає.
- **Дописування у свої секції:** `Enums.cs`, `EcrDbContext.cs`, `ErrorCodes.cs`,
  `ExceptionHandlingMiddleware.cs`, `09-seed.sql` (`-- RT: …` тут, `-- HSE301:<крок>` там),
  обидва `DependencyInjection.cs`, `Ecr.Adapters.Excel/DependencyInjection.cs` (тут RT-16/RT-18a,
  там B6), `02-contracts.md` §7/§9, `02a-db-schema.md`.
- Коди помилок не перетинаються: тут `ECR-REG-4092/4093/4221`, там `ECR-VIEW-*` і нові
  `messageKey` чинних `ECR-INT-*`/`ECR-CALC-*`.

---

## 10. Критерії приймання

| # | Критерій | Як перевіряється |
|---|---|---|
| AC-1 | **Золотий тест 301.** Методологія `Strict` з формулами §5.6 на даних `STREAM`/`STREAM_CASE`/`GAS_COMPOSITION`/`COMPONENT`, вхід події `AI_Int_SG_V8!C` (V = 269.258 ст.м³ `C17`, Duration = 930 с `C12`, ρ = 0.9589 `C25`, потік 1D-2, кейс 370 Winter; склад — `Current_20250805`, рядок 217), дає: **μ = 23.0544679165262** (`C26`) ± 1e-9 відносно; **S = 17.2965446772043 мас.%** (`C27`) ± 1e-9; **EF_RAW = 2.0869542** і **EF = 2.087** точно (`C103`); **M = 0.2581915 т** (`C104`); **SO2 = 0.0891735 т** (`C112`); **SO2 = 95.885 г/с** (`C129`) ± 1e-3; **CO2 = 0.5361514 т** (`C105`) — з EF, **округленим** до 2.087 | `Hse301GoldenTests` (RT-23a, у пам'яті) + `FlertRegistryScenario` (RT-50, реальний SQL, імпорт → публікація → прогін). **Джерело чисел:** входи й очікувані значення — `hse301.xlsx` (`AI_Int_SG_V8`, рядки 17–27, 30–63, 101–129; `excel-analysis.md:260-289`); властивості компонентів — фікстура `component-mw-hysys.csv` (формули й `N_C`/`N_S` — з підписів Add Stream `streams Onshore!A10:A54`; M = `wt% · μ / об.%` аркуша `AI_Int_SG_V8`, 33 компоненти моря), а в RT-50 — `COMPONENT`, імпортований профілем `ADDSTREAM_COMPONENTS`, з тими самими M; константи `M_S = 32.064`, `M_CO2 = 44.00` з `Round(…, 3)`. Перевірено 2026-09-27 на файлі: μ і S збігаються до 2e-14 на всіх трьох складах SG, EF — на всіх 11 складах (3 SG + 8 FG). Формула й точність не підганяються: розбіжність — дефект у даних фікстури або в рушії |
| AC-2 | Складений ключ гарантує база: з двох одночасних записів рівно один успішний, другий — `409 ECR-REG-4092` з `keyText` і кодом конфліктного запису | інтеграційний тест RT-10b; `smoke.ps1` |
| AC-3 | Ключ `' 370  winter'` дорівнює `'370 Winter'` за `IgnoreCase = true`; `Lookup` порівнюється за id | фікстура RT-02 |
| AC-4 | Ключ не публікується на даних із дублікатами; конструктор показує приклади до збереження | RT-11, RT-34 |
| AC-5 | Правило «Σ мол.% = 100 ± 0.5» на рівні `Warning` зберігає і попереджає, на рівні `Error` відхиляє пакет; на фікстурі FLERT 4 порушення | RT-17a/b |
| AC-6 | Композиція: видалення кейсу з `Restrict` → 409; з `Cascade` → кейс і склад видалені, ключі звільнені; склад невидимий, коли кейс закритий датою | RT-12 |
| AC-7 | Повтор прогону `AS OF RegistryAsOfUtc` після правки складу дає побітно ті самі результати, що й первинний прогін | RT-22/RT-23a |
| AC-8 | Правка складу позначає результати документа відкритого періоду застарілими з назвою довідника; `impact` не показує закритих періодів; автоматичного перерахунку немає | RT-25 |
| AC-9 | `REGFIND` / `REGSUM` у версії `Legacy` → `ECR-CALC-0433`; наявні версії `Legacy` рахують побітно так само, як до змін | RT-20a, RT-23a |
| AC-10 | Імпорт транспонованого фрагмента `streams Offshore` і плоского `Current_20250805` дає 126 потоків, 220 кейсів, рядки складу; дублі `CO`/`Carbon_Monoxide` з різними значеннями — помилка рядка; файл 2.3 МБ приймається | RT-18a/b, RT-36 |
| AC-11 | Редактор: вставка блоку з Excel з `Lookup` за назвою; дубль ключа видно до збереження; `Ctrl+S` — один пакет; конфлікт версій показано | e2e `registryEditor.spec.ts` |
| AC-12 | a11y обох тем зелений на нових екранах; уся робота в редакторі досяжна з клавіатури (§8.8) | гейти `a11y (dark)`/`a11y (light)`, `keyboardPath.spec.ts` |
| AC-13 | Автодоповнення пропонує довідники, поля, ланцюжки; описка в полі — помилка публікації з позицією | RT-40, RT-21 |
| AC-14 | «Де використано» показує формули методології, шаблону й правила | RT-19 |
| AC-15 | Сім гейтів зелені; `smoke.ps1` і `e2e-stand.ps1` зелені з окремого worktree | RT-50 |

---

## 11. Ризики, рішення, питання, чек-лист

### 11.1 Ризики

| Ризик | Ймовірність | Вплив | Пом'якшення |
|---|---|---|---|
| Перша temporal-таблиця в системі: майбутні `ALTER` цих таблиць складніші, історія на `[PRIMARY]` | середня | середній | міграції — лише через EF (він сам вимикає й вмикає версіювання); розміщення історії — завдання DBA у `Sql/`; тест `AS OF` у RT-04 |
| Перетин вікон темпорального ключа гарантує застосунок, а не індекс | низька | середній | `UPDLOCK, HOLDLOCK`; точний дубль початку — в індексі; нічна перевірка (§11.5) |
| Мова росте: шаблон 13 → 20 функцій, методологія +8 `Extension` | висока (свідомо) | середній | одна семантика, спецформи в одному файлі (`RegistryForms.cs`), лічильники в `ClientServerEquivalenceTests` і `DialectCatalogTests` фіксують склад |
| Повний перегляд великого довідника впирається в бюджет | середня | низький | індексний шлях; попередження під час публікації; `#BUDGET` видно в трейсі |
| Зміна семантики `REGFIELD` (порожнє → `null`) змінить числа наявних формул шаблону | низька | середній | перед RT-24 порахувати формули з `REGFIELD`; зміну показати в описі PR; `IFERROR`, що ловив `#REF`, тепер отримає `null` — перелічити такі місця |
| Нові `@Arg` з `Lookup` у `Strict` змінять числа формул, які досі мовчки отримували `null` | низька | середній | лише `Strict` (`R-12`); перевірка публікації типу `EntryRef` не пускає арифметику |
| Якість даних Excel (дублі стовпців, неповні склади, скопійовані ρ/MW/Z для 1D-1 — `excel-analysis.md:486-488`; хибна формула «Propyl mercaptan (C5H8S)» і кириличні `С`/`Н` у підписах Add Stream) потрапить у довідник | висока | середній | `MustMatch`, правило Σ як `Warning`, прев'ю імпорту (`R-23`, `R-24`); формула з файлу не перезаписує наявну (`registries.import.formulaMismatch`), кирилиця нормалізується (RT-18a) |
| RevoGrid поза `DocumentGrid` — нова точка складності a11y | середня | середній | переюз утиліт сітки; a11y-тести з першого кроку (RT-30) |
| Обсяг: ~35 кроків | висока | середній | хвилі; P2 (`RT-37`) окремо; кожен крок відвантажується в `dev/integration` зеленим |
| Дані FLERT у фікстурах (мітка MSIP «Internal», `excel-analysis.md:331`) | середня | високий | робоче припущення RQ-1: у репозиторії — синтетичні фікстури й дані, що не належать замовнику (назви й формули компонентів, їхні молярні маси); склад реальних кейсів лежить поза репозиторієм, і золотий тест на ньому пропускається, якщо файлу немає |
| Молярні маси компонентів суші (не 33 компоненти моря, відновлені з 301) узято з формули, а не з HYSYS | середня | низький (для 301 — нуль: на морі їх немає) | позначка джерела в полі `SOURCE` запису `COMPONENT`; значення HYSYS замінюють формульні одним імпортом, без зміни моделі (`R-27`) |

### 11.2 Реєстр рішень

**`R-1…R-21` схвалені людиною 2026-09-27** («так, вноси D-149 в реєстр та Схвалено
запропоновані рішення») і внесені в `docs/tz/10-decisions.md` як `D-150…D-170` (`R-n` →
`D-(149+n)`). Реєстр — єдине джерело істини (`docs/tz/10-decisions.md:3-5`); тут лишаються
обґрунтування й посилання на розділи. **`R-22…R-27` ухвалені за делегуванням людини
2026-09-27** («все інше на твій розсуд не обмежуй себе») і внесені в реєстр як
`D-195…D-200` (`R-n` → `D-(173+n)`).

| # | Статус | Рішення | Обґрунтування |
|---|---|---|---|
| R-1 | ✓ схвалено 2026-09-27 → `D-150` | Розширюємо Registry; нової сутності «таблиця» немає; обчислюваних полів у довіднику немає | `ФВ-8.1`; `D-13/14/66`; `RegistryValue.cs:122-131` |
| R-2 | ✓ схвалено 2026-09-27 → `D-151` | Складений ключ = `cfg.RegistryKeyDef` + похідна `dic.RegistryEntryKey` з SHA-256 канонічного рядка + фільтрований `UNIQUE` | EAV не дає унікального індексу на значеннях; хеш фіксованої довжини обходить межу 900 байт ключа індексу |
| R-3 | ✓ схвалено 2026-09-27 → `D-152` | Нормалізація: текст — NFC, обрізка, згортання пробілів, `IgnoreCase` за замовчуванням; число канонічне; `Lookup` — за id; тег типу | узгоджено з CI-зіставленням бази (`02a-db-schema.md:54-59`) і `ФВ-8.8` |
| R-4 | ✓ схвалено 2026-09-27 → `D-153` | Поля первинного ключа обов'язкові; в альтернативних ключах `null`-частини не перевіряються | `REGFIND` мусить мати повну адресу; семантика `UNIQUE` з різними `NULL` |
| R-5 | ✓ схвалено 2026-09-27 → `D-154` | `UniqueWithin` замінюється ключами, без автоконвертації; нові правила цього виду відхиляються | правило не виконувалось, на даних можуть бути дублікати |
| R-6 | ✓ схвалено 2026-09-27 → `D-155` | Композиція — ознака `Lookup`-поля; один батько; дочірній довідник нетемпоральний і видимий разом із батьком; видалення `Restrict`/`Cascade` | 3 таблиці FLERT — дерево; версія кейсу несе свій склад |
| R-7 | ✓ схвалено 2026-09-27 → `D-156` | M:N — довідник-зв'язка; `RegistryEntryLink` не розвивається | типізовані атрибути, ключі, правила, формули працюють однаково |
| R-8 | ✓ схвалено 2026-09-27 → `D-157` | `CodeMode = Auto` (`E` + 9 цифр послідовності) для довідників із первинним ключем | `EcrCode` не приймає кирилицю ключа |
| R-9 | ✓ схвалено 2026-09-27 → `D-158` | Системна історія SQL Server (`SYSTEM_VERSIONING`) через міграцію + `CalculationRun.RegistryAsOfUtc`; бізнес-дата знімка — останній день періоду | §3.6; пікер сітки вже бере кінець періоду (`DocumentGrid.tsx:766-776`) |
| R-10 | ✓ схвалено 2026-09-27 → `D-159` | 7 нових функцій + розширений `REGFIELD`; імена ВЕЛИКИМИ; `Extension`; агрегати — спецформи; `ROW.`/`THIS`; статичний `EntryRef`, у рантаймі — `Number` | §5; прецедент спецформ — `Evaluator.cs:694-748`; прецедент числа-id — `RecalculationService.cs:1120-1128` |
| R-11 | ✓ схвалено 2026-09-27 → `D-160` | Помилки `#N/A`/`#MULTI`; незаповнене поле → `null` (і в наявному `REGFIELD`) | `02b` §6.3; Д-2 |
| R-12 | ✓ схвалено 2026-09-27 → `D-161` | `Lookup`-аргументи методології передаються як `EntryRef` лише у версіях `Strict` | `Legacy` зобов'язаний давати побітно ті самі числа (`02b-expressions.md:294-298`) |
| R-13 | ✓ схвалено 2026-09-27 → `D-162` | Знімок довідників завантажується до прогону; жодних звернень до БД під час обчислення; повний перегляд — у межах бюджету | `02b-expressions.md:782-785`; `D-63` |
| R-14 | ✓ схвалено 2026-09-27 → `D-163` | Зміна довідника позначає результати застарілими; перерахунок — явною дією, лише відкриті періоди | `D-39`; серії правок; `D-63` |
| R-15 | ✓ схвалено 2026-09-27 → `D-164` | Правила `Expression`/`RequiredWhen`/`CrossRegistry` виконуються; `Error` блокує пакет; «Сума дочірніх» — шаблон `Expression` з параметрами | §6 |
| R-16 | ✓ схвалено 2026-09-27 → `D-165` | Нових прав немає | наявні `Registry.*`/`Calculation.*` покривають усі дії |
| R-17 | ✓ схвалено 2026-09-27 → `D-166` | Жетон конкуренції рядка — момент початку системного періоду (`PeriodStart`); конфлікт → `409 ECR-REG-4093` | temporal дає версію безкоштовно; сьогодні записи довідника конкуренції не мають |
| R-18 | ✓ схвалено 2026-09-27 → `D-167` | Фізичні властивості компонентів (M, nC, nS, LHV) — у довіднику `COMPONENT`, а не в `calc.MethodologyConstant` | `D-75` (`docs/tz/10-decisions.md:126`) про **контекстні** коефіцієнти методології; властивості компонента — довідкові дані, спільні для методологій, версіюються історією довідника. Уточнення формулювання `D-75` — разом із внесенням. Джерело властивостей («це і є addstream») і що з нього фактично береться — `R-27` |
| R-19 | ✓ схвалено 2026-09-27 → `D-168` | Редактор даних на RevoGrid (виняток із `DIRECTIVE-15-FRONTEND.md:122`); `DocumentGrid.tsx` не змінюється | редагована сітка, вставка блоком, `Lookup`-редактори вже є для RevoGrid |
| R-20 | ✓ схвалено 2026-09-27 → `D-169` | XLSX читається й пишеться через ClosedXML в `Ecr.Adapters.Excel` за портами | `D-18` (`docs/tz/10-decisions.md:39`); пакет уже в стеку (`Directory.Packages.props:53`), нової залежності немає (`D-12`) |
| R-21 | ✓ схвалено 2026-09-27 → `D-170` | Імпорт — прев'ю з токеном (`DataRevision`) → застосування; транспонування, розгортання стовпців, псевдоніми, `MustMatch` для дублів | реальна форма даних замовника (`excel-analysis.md:340-346`, `:382-391`) |
| R-22 | ✓ ухвалено за делегуванням 2026-09-27 → `D-195` | RQ-3: допуск Σ складу й рівень правила — **параметри правила**, не константи: типово `SUM_100` = `Warning`, ± 0.5 мол.%; адміністратор довідника змінює їх у вкладці «Rules» без релізу | «допуски як налаштування»; у даних уже є 75 % і 99.6–100.2 %, і блок `Error` за замовчуванням зупинив би імпорт знімка |
| R-23 | ✓ ухвалено за делегуванням 2026-09-27 → `D-196` | RQ-5: дублі стовпців (`CO`/`Carbon_Monoxide`, `H2`/`Hydrogen`, `O2`/`Oxygen`): однакові значення (≤ 1e-12) зливаються мовчки; різні — помилка рядка `importDuplicateSourceMismatch`, людина обирає авторитетний стовпець у прев'ю, вибір зберігається в профілі (`duplicateSources: {"CO": "Carbon_Monoxide"}`) і далі діє без питань | політика імпорту; ні «перший виграє», ні «сума» не пояснюють число, а рішення людини відтворюване |
| R-24 | ✓ ухвалено за делегуванням 2026-09-27 → `D-197` | RQ-6: неповні (`LPG Column Overhead` 75.4/80.6 %) і нульові (азот) склади імпортуються як є, з попередженням правила `SUM_100`; автонормалізації до 100 % немає | політика імпорту; нормалізація змінила б склад, який HYSYS дав саме таким, а попередження лишає рішення людині |
| R-25 | ✓ ухвалено за делегуванням 2026-09-27 → `D-198` | RQ-8: `STREAM.LEGACY_ID` — необов'язкове `Int`, унікальне, коли заповнене (альтернативний ключ `BY_LEGACY_ID`); імпорт заповнює його з `StreamID`, нові потоки в ECR — без нього | трасування до FLERT коштує одне поле; вимагати його для нових потоків нема з чого — FLERT їх не видає |
| R-26 | ✓ ухвалено за делегуванням 2026-09-27 → `D-199` | RQ-9: зворотної синхронізації в `FLERT.dbo` немає (`N-6`); до переходу форми 301 на представлення ECR (`D-149`) FLERT живе своєю копією, `Add Stream` — лише для читання (`I-3`) | `D-50`: master — ECR; `D-44`: у зовнішні системи не пишемо; напівміра «синхронізуємо обидва» дає розбіжності без відповідального (`B14-ecr-integration-and-data.md:315`) |
| R-27 | ✓ ухвалено за делегуванням 2026-09-27 → `D-200` | Джерела властивостей `COMPONENT` (людина: «це і є addstream»; перевірено на файлах 2026-09-27, §11.4): `NAME`, `FORMULA` і з неї `N_C`/`N_H`/`N_S` — імпорт профілем `ADDSTREAM_COMPONENTS` з підписів Add Stream `streams Onshore!A10:A54`; `HYSYS_NAME` — з заголовків `Current_20250805!O2:BN2` і `streams Offshore!B12:B45`; `MW` — значення бібліотеки HYSYS: для 33 компонентів моря відновлені з 301 (`wt% · μ / об.%`), для решти — з формули до підтвердження (`SOURCE` запису каже, звідки); `LHV` — необов'язкове поле, а LHV суміші — необов'язкове поле кейсу `STREAM_CASE.LHV_STD` | в Add Stream немає ні M, ні LHV компонентів (перевірено по всіх 13 аркушах); маси з формул не відтворюють μ файлу (23.0572 проти 23.0544679), значення HYSYS — відтворюють до 2e-14; нейтральний дефолт для LHV замінюється одним імпортом без переробки |

### 11.3 Питання до замовника

**Закриті 2026-09-27.**

| # | Питання | Як закрито |
|---|---|---|
| RQ-2 | Джерело властивостей компонентів (M, nC, nS, LHV); які саме M дали μ = 23.0544679 | людина: «це і є addstream». Перевірено на файлах (§11.4, `R-27`): `N_C`/`N_S` — з хімічних формул у підписах Add Stream; M — значення бібліотеки HYSYS, які відновлюються з самого 301 і дають μ до 2e-14; **LHV компонентів в Add Stream немає** — це єдине, що лишається відкритим (RQ-2a нижче) |
| RQ-3 | Допуск Σ складу і рівень правила | ухвалено за делегуванням — `R-22` |
| RQ-5 | Яке значення дубльованих стовпців авторитетне | ухвалено за делегуванням — `R-23` |
| RQ-6 | Неповні й нульові склади: як є чи виправити | ухвалено за делегуванням — `R-24` |
| RQ-8 | Чи потрібен `LEGACY_ID` новим потокам | ухвалено за делегуванням — `R-25` |
| RQ-9 | Чи мусить `FLERT.dbo` отримувати зміни словника | ухвалено за делегуванням — `R-26` |

**Лишається відкритим — одне питання.**

| # | Питання | Хто | Чому важливо | Нейтральний дефолт |
|---|---|---|---|---|
| RQ-2a | З якого джерела замовник бере нижчу теплоту згоряння (LHV): бібліотека HYSYS, ISO 6976 чи інше — і для компонентів, чи вже для суміші кейсу? У 301 є лише LHV суміші (36.0412787449075 для 1D-2/370 Winter), в Add Stream — нічого | методолог | NCV і парникові CH4/N2O рахуються від LHV | LHV суміші — поле кейсу `STREAM_CASE.LHV_STD`, яке вводить чи імпортує методолог; `COMPONENT.LHV` — необов'язкове, і коли його заповнять, методологія перейде на `Σ x·LHV_i` без зміни моделі (`R-27`); не блокує |

**Робочі припущення — до підтвердження на приймальному тестуванні, не блокують.** Факти
світу замовника, яких система знати не може; діє нейтральний дефолт, заміна не потребує
переробки.

| # | Факт | Хто підтверджує | Робоче припущення |
|---|---|---|---|
| RQ-1 | Чи можна класти в репозиторій фрагмент `Current_20250805` (3 кейси) як тестову фікстуру (мітка MSIP «Internal») | власник даних / ІБ | у репозиторії — синтетичні фікстури й дані, що замовнику не належать (назви, формули й молярні маси компонентів); золотий тест на реальному складі пропускається, якщо файлу поза репозиторієм немає |
| RQ-4 | Звідки береться стандартна густина ρ = 0.9589 для 1D-2 зима: окреме поле чи розрахунок (μ/24.04 дає 0.9590) | методолог | ρ — вхід події; у `STREAM_CASE` — необов'язкове поле `RHO_STD` |
| RQ-7 | Хто (роль, люди) веде довідник потоків в ECR після переходу | замовник | роль `TemplateAdministrator` з грантом `Write` на 4 довідники |

### 11.4 Приклад налаштування «Потоки FLERT»

| Довідник | Поля (тип, обов'язковість, одиниця) | Ключі | Зв'язки | Правила |
|---|---|---|---|---|
| `COMPONENT` (46 записів) | код запису = код компонента (`CH4`, `H2S`, `MEA`…), `CodeMode = Manual`; назва en/ru — `DisplayL10n`; `FORMULA` String; `HYSYS_NAME` String (псевдонім імпорту: `Methane`, `IC4_1`, `nBMercaptan`…); `MW` Decimal, g/mol, обов'язкове; `N_C` Int, обов'язкове; `N_H` Int; `N_S` Int, обов'язкове; `LHV` Decimal, MJ/Sm³, необов'язкове (RQ-2a); `SOURCE` String — звідки M і LHV (`HYSYS, recovered from HSE301`, `formula`, …) | альтернативний `BY_HYSYS` (`HYSYS_NAME`); первинного немає → `REGFIND` за кодом | — | `CrossRegistry` не потрібне |
| `STREAM` (126) | код `S{StreamID}` для імпортованих, `CodeMode = Manual`; `LEGACY_ID` Int; `NUMBER` String (`1D-2`, буває порожнім); назва — `DisplayL10n`; `IS_ONSHORE`, `IS_SOUR`, `IS_HMB`, `EXCLUDE_IN_LIST` Bool; `GROUP` Int; `HMB_DOC` String (`370`, `ПК-3 (370-220)`) | альтернативний `BY_LEGACY_ID` (`LEGACY_ID`); природного ключа немає (`excel-analysis.md:393-402`) | — | `NUMBER_REPEATS` (Info) §8.3 |
| `STREAM_CASE` (220), `IsTemporal = true` | `STREAM` Lookup → `STREAM`, **Composition**, обов'язкове; `CASE_NAME` String, обов'язкове (`370 Winter`, `ПК-3 (370-220) лето`, `No composition`); `T_C` Decimal, °C; `DENSITY_OP` Decimal, кг/м³ (робоча, з HYSYS); `MW_HYSYS` Decimal; `Z` Decimal; `RHO_STD` Decimal, кг/ст.м³ (RQ-4); `LHV_STD` Decimal, MJ/Sm³, необов'язкове (LHV суміші, RQ-2a); `SEASON` String (`Winter`/`Summer`; імпорт виводить його із суфікса `CASE_NAME`: «370 Winter» → Winter, «ПК-3 (370-220) лето» → Summer; порожнє, якщо суфікса немає) — за ним FEATURE-HSE301-VIEW §4.7.6 обирає кейс потоку, коли подія з PI несе лише сезон; `CodeMode = Auto` | **PK (`STREAM`, `CASE_NAME`)**, `IgnoreCase`; 0 дублікатів на знімку (`excel-analysis.md:398`) | батько — `STREAM` | `SUM_100` (`childSum`, Warning ± 0.5) |
| `GAS_COMPOSITION` (≤ 220 × 49) | `CASE` Lookup → `STREAM_CASE`, **Composition**, обов'язкове, `Cascade`; `COMPONENT` Lookup → `COMPONENT`, обов'язкове; `MOL_PCT` Decimal, %, обов'язкове; `CodeMode = Auto` | **PK (`CASE`, `COMPONENT`)** | батько — `STREAM_CASE`; посилання — `COMPONENT` | — |

Нормалізація Excel → рядки: кожен рядок `Current_20250805` (потік × кейс) дає один `STREAM_CASE` і до
49 рядків `GAS_COMPOSITION`. 49 стовпців складу `Nitrogen…Methanol` зіставляються з `COMPONENT` за
`HYSYS_NAME`. Три пари дублів зводяться до одного компонента з `MustMatch`, нулі пропускаються.
Атрибути потоку однакові в межах `StreamID` (`excel-analysis.md:404-405`), тому `STREAM` будується
один раз на `StreamID`. `Density`/`Molecular_Weight`/`Z` лягають у поля кейсу **з позначкою
«HYSYS, робочі умови»**: для розрахунку 301 вони не використовуються, μ рахується зі складу
(`excel-analysis.md:482-484`).

**`COMPONENT` з Add Stream (відповідь людини «це і є addstream»; перевірено на копіях файлів
2026-09-27 — openpyxl, дампи аркушів).**

| Властивість | Що є в `Add Stream_v1_3.xlsx` | Звідки береться в `COMPONENT` |
|---|---|---|
| назва, формула | аркуш `streams Onshore`, стовпець A, рядки 10–54 (те саме — `TOx!A9:A53`): 45 двомовних підписів виду «Methane (CH4), vol% - Метан (CH4), % об.», «Butyl mercaptan (C4H10S), vol% - …». Формули немає у трьох псевдокомпонентів HYSYS: `CN1_35*`, `CN2_35*`, `CN3_16*` | профіль `ADDSTREAM_COMPONENTS` (нижче) |
| `N_C`, `N_H`, `N_S` | прямо не записані; **виводяться з формули** підпису. Дві пастки файлу: «Toluene (С7Н8)» і «Ethylbenzene (С8H10)» пишуть `С`/`Н` кирилицею (U+0421, U+041D) — імпорт нормалізує їх до латиниці; «Propyl mercaptan (C5H8S)» — описка (у 301, `AI_Int_SG_V8!B58`, той самий компонент — `C3H8S`, і маса 76.15 саме його), тож формула з файлу не перезаписує наявну (`registries.import.formulaMismatch`) | з формули; псевдокомпоненти — 0 (так рахує файл: на 1D-1/370 Summer з 4.1 об.% псевдокомпонентів будь-яке `N_C ≥ 1` зламало б EF) |
| `HYSYS_NAME` | заголовки складу `Current_20250805!O2:BN2` (`Methane`, `IC4_1`, `MMercaptan`…; 49 стовпців складу, бо `AX`, `AZ`, `BB` — `Density`, `Molecular_Weight`, `Z_CompressibilityFactor`) і рядки `streams Offshore!B12:B45` (`IC4_1*`, `M-Mercaptan`…); порядок і написання з підписами Onshore не збігаються, тож відповідність — явним переліком у профілі | профіль, перелік `aliases` |
| `MW` | **немає** молярних мас компонентів: `Current_20250805!AZ` — `Molecular_Weight` кейсу (23.05 для 1D-2 зима), `streams Onshore` рядки 58, 64, 78 — `Molecular Weight` фаз потоку. Маси з формул (атомні маси IUPAC) дають μ = 23.0572 замість 23.0544679 | значення бібліотеки HYSYS, **відновлені з 301**: `M_i = wt%_i · μ / об.%_i` (`AI_Int_SG_V8`, рядки 30–63 і 66–99), для 33 з 34 компонентів моря (`TEG` має 0 об.% у всіх подіях). Приклади: CH4 16.0429, N2 28.013, H2S 34.076, `IC5_18*` 71.76, `C6_21*` 85.36, `CN1_35*` 230.85, `CN3_16*` 500.0. З ними μ збігається з файлом до 2e-14 на всіх трьох складах SG. Компоненти суші (`SO2`, `NH3`, `CO`, `MEA`, `DEA`, `Methanol`…) — з формули, `SOURCE = formula`, до значень HYSYS |
| `LHV` | **немає** ні для компонентів, ні для суміші (пошук «Heating», «LHV», «теплота», «МДж» по всіх 13 аркушах — жодного змістовного збігу) | необов'язкове поле; LHV суміші — `STREAM_CASE.LHV_STD` (RQ-2a) |

Профіль `ADDSTREAM_COMPONENTS` (`RegistryImportSpec`, §4.6):

```jsonc
{
  "sheet": "streams Onshore", "orientation": "Rows", "firstData": 10, "lastData": 54,
  "targets": [
    { "registry": "COMPONENT", "match": "Key:BY_HYSYS",
      "parse": { "column": "A", "pattern": "^(?<en>.+?) \\((?<formula>[^)]+)\\), vol% - (?<ru>.+?) \\(",
                 "normalize": "cyrillicLookalikes" },
      "fields": { "FORMULA": "$formula", "N_C": { "atoms": "C" }, "N_H": { "atoms": "H" }, "N_S": { "atoms": "S" },
                  "DisplayL10n.en": "$en", "DisplayL10n.ru": "$ru" },
      "onConflict": { "FORMULA": "KeepRegistry" } }
  ],
  "aliases": { "Methane (CH4)": ["Methane", "CH4"], "Methyl mercaptan (CH4S)": ["MMercaptan", "M-Mercaptan"] /* … 49 */ },
  "massSource": "component-mw-hysys.csv"        // M і SOURCE — окремим файлом, бо в Add Stream їх немає
}
```

⚠ `parse` (іменовані групи `$…`), `{ "atoms": … }`, `onConflict` і `massSource` — розширення
`RegistryImportSpec` §4.6, яке робить RT-18a. `atoms` — перетворення під час імпорту, а не
обчислюване поле довідника (`R-1`): у довіднику `N_C`/`N_H`/`N_S` — звичайні `Int`, які людина
може виправити.

### 11.5 Поза обсягом / наступні кроки

- `[debt]` Перейменувати `IsKey` → `IsScopeField` у домені (окремий рефакторинг, `CLAUDE.md` §4).
- `[debt]` Ендпоінт відновлення запису (Д-6), з повторною перевіркою ключів (`keyTaken` при
  відновленні).
- `[parallel]` Нічна перевірка `RegistryKeyOverlapCheck` + `RegistryKeyConsistencyCheck` (хеші
  відповідають значенням) у наборі `ФВ-7.7`.
- `[parallel]` `REGFINDAT(date, R, k…)` — пошук на дату події, коли з'явиться вимога.
- `[parallel]` `RT-37` зведений вигляд; довідкова колонка «мас.%» у редакторі складу.
- `[question]` Зважені суміші потоків (30/70, 76/17/7) і «середній потік» (`excel-analysis.md:547-548`)
  — окрема функція довідника чи дані? Потрібна вимога замовника.

### 11.6 Чек-лист

**До старту**
- [x] Рішення `R-1…R-21` схвалені людиною 2026-09-27 й перенесені в `docs/tz/10-decisions.md` як `D-150…D-170` (RT-00).
- [x] `R-22…R-27` (ухвалені за делегуванням) внесено в реєстр як `D-195…D-200` (2026-09-27).
- [ ] Нові `ФВ-8.15…8.18`, `ФВ-9.18`, `ФВ-9.19` у `docs/tz/02-requirements.md` і звільнення «до RT-…» у `contracts/trace-exempt.md` — паралельний крок документації 2026-09-27.
- [x] D16-04 ([DIRECTIVE-16 §2 D16-04](DIRECTIVE-16.md)) — виконано в `5f00b791` і `400c58b1` (шлях збереження); RT-24 може стартувати (§5.12).
- [x] RQ-2, RQ-3, RQ-5, RQ-6, RQ-8, RQ-9 закрито 2026-09-27 (відповідь людини або `R-22…R-27`).
- [ ] RQ-2a (джерело LHV) запитано; RQ-1, RQ-4, RQ-7 — робочі припущення до приймального тестування (робота на дефолтах не зупиняється).
- [ ] `Get-PSDrive -Name H,F` — місця досить для стендів (`CLAUDE.md`, розділ про диски).

**Кожен крок**
- [ ] План-рядок із §9.1 скопійовано в опис коміту; файли поза списком — зупинка й питання.
- [ ] Крок, що реалізує ФВ, додає тест із `[Trait("Requirement","ФВ-…")]` або `it('ФВ-…: …')` і знімає свій рядок у `contracts/trace-exempt.md` (тест на неоголошену ФВ — «осиротілий», `RequirementTraceTests.cs:50-53`).
- [ ] Коди помилок: §7, `ErrorCodes`, `messageKey`, сід (англійською), арм для 409 — разом.
- [ ] Ендпоінт: §9, `ProducesResponseType`, споживач у клієнті, регенерація контракту останнім комітом.
- [ ] Таблиця: `02a-db-schema.md` у тому самому кроці, що міграція.
- [ ] Мутаційний доказ: поломка → червоний тест → повернення → зелений; записано в коміті.
- [ ] Повний прогін перед пушем; rebase → push у `dev/integration`; `git diff origin/dev/integration` чистий від чужих файлів.

**Перед тижневим мержем**
- [ ] Сім гейтів зелені на `dev/integration`.
- [ ] `smoke.ps1` і `e2e-stand.ps1` зелені з окремого worktree.
- [ ] AC-1…AC-15 відмічені з посиланнями на тести.
