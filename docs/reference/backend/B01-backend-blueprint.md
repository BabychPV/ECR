# B01 — Проєкт бекенду ECR Web

> Загальна карта: межі, збірки, правила залежностей, внутрішня будова, наскрізні
> потоки, конфігурація і розгортання.
> Деталі підсистем — B02…B09. Рішення і їх обґрунтування — [B10](B10-decisions.md).

---

## 1. Межі бекенду

### 1.1 Що бекенд робить

| Група | Відповідальність |
|-------|------------------|
| **Метадані** | зберігання, версійність, `CloneFrom`/`Publish`/`Diff`, класифікація змін, міграція документів між версіями |
| **Дані** | документи, екземпляри таблиць, рядки, комірки; batch-запис із конкурентністю; аудит кожної зміни |
| **Обчислення** | розбір і виконання виразів (формули, валідація, фільтри рядків, умовне форматування), граф залежностей, інкрементний перерахунок |
| **Доступ** | дві схеми автентифікації в одну ідентичність; єдине місце рішення про доступ; аудит прав |
| **Періоди і workflow** | стани періодів за offsets, `Draft→Submitted→Approved`, snapshot при затвердженні |
| **Довідники** | універсальний реєстр: визначення, записи, темпоральність, зовнішні ключі, синхронізація |
| **Інтеграція** | збір із зовнішніх джерел через порти; PI AF — два транспорти; сирі дані без інтерпретації |
| **Обмін** | експорт/імпорт `.xlsx`, CSV/JSON |
| **Обслуговування** | архівація закритих років, перевірка інваріантів, health, метрики |

### 1.2 Що бекенд НЕ робить

* **Не рендерить.** Немає серверного HTML, немає знання про grid. API віддає дані
  і **мапу прав**; як це виглядає — справа SPA.
* **Не рахує клієнтські підказки.** `formulajs` на клієнті — окрема реальність;
  бекенд ніколи не приймає обчислене клієнтом значення (ТЗ §5.2).
* **Не змінює схему БД під час роботи.** Жодного DDL з коду — ані `ALTER TABLE`,
  ані `CREATE TABLE` (ТЗ §5.5 принцип 1). Виняток один і він вузький — **не зачіпає складу таблиць
  і колонок**:
  обслуговування партицій (B02 §3.3) — його виконує **регламентна задача
  застосунку** `PartitionMaintenanceJob` (B07 §2) під окремим обліковим записом
  із правом лише на `ALTER PARTITION SCHEME/FUNCTION`. Запланованого обʼєкта в БД
  для цього не існує — SQL Agent виводиться з експлуатації (B15 §1).
* **Не знає про PI AF і про екологію.** `Ecr.Domain` / `Ecr.Application` не мають
  посилання на адаптер; `Permit`, `WaterBody`, `Substance` — це записи реєстрів,
  а не типи в коді.
* **Рахує дані звіту, але не рендерить його** — у першій черзі. Логіка держформ
  (агрегації, вікна пермітів, коди) живе в сервісі; рендеринг і експорт у першій
  черзі виконує SSRS над матеріалізованим зрізом, у другій — власний переглядач
  ([B16](B16-ecr-reporting.md)). ⛔ Чого сервіс не робить ніколи — не віддає
  побудову показника ані базі, ані звітному інструменту.

### 1.3 Одна межа, яку легко порушити випадково

Спокуса номер один — «додати в `cfg.ColumnDef` поле `AfAttributeName`, бо так
зручніше адаптеру». Це порушує §12 п.15/18 ТЗ і ловиться архітектурним тестом.
Правильно: адаптер читає `ext.LegacyColumnMapping` і сам з'єднує його з `ColumnDefId`.

Спокуса номер два — «зробити перевірку ролі прямо в контролері». Ловиться
правилом §4.8 [13]: рішення про доступ — **тільки** через `IAccessDecisionService`.

---

## 2. Карта збірок

```
Ecr.sln
├─ src/
│  ├─ Ecr.Domain/                сутності, інваріанти, доменні події. Залежності: BCL
│  ├─ Ecr.Expressions/           ⭐ лексер/парсер/AST/компілятор виразів. Залежності: BCL + NCalc
│  ├─ Ecr.Application/           use-cases, порти, DTO. Залежності: Domain, Expressions,
│  │                             FluentValidation, Mapster  (❌ EF Core, ❌ адаптери)
│  ├─ Ecr.Infrastructure/        EF Core, репозиторії, міграції, кеш, черга задач
│  ├─ Ecr.Formulas/              рушій формул поверх Ecr.Expressions: граф, порядок,
│  │                             інкрементний перерахунок
│  ├─ Ecr.Excel/                 ClosedXML: експорт/імпорт, двонаправлений мапер координат
│  ├─ Ecr.Adapters.PiAf/         IExternalDataSource: WebApi + SqlClient
│  ├─ Ecr.Calculations/          ICalculationModule (перша черга, D-54)
│  ├─ Ecr.Api/                   ASP.NET Core host, контролери, OpenAPI, автентифікація
│  └─ Ecr.Web/                   React SPA
├─ tools/
│  ├─ Ecr.Bootstrap.Excel/       .xlsm + VBA → cfg.* / dic.* / ext.Legacy*
│  ├─ Ecr.Migration.PiAf/        історія 2025–2026 → doc.*
│  └─ Ecr.DataGen/               ⭐ генератор синтетичних 108 млн рядків для Етапу 0
└─ tests/                        9 проєктів — склад у [13] §3
```

### 2.1 Чому `Ecr.Expressions` — окрема збірка (BR-05)

У системі **чотири** місця, де є вирази користувача:

| Місце | Приклад |
|-------|---------|
| `FormulaDef.Expression` | `SUM([Water_07].[Main].[7001001:7001005].[{Period}])` |
| `ValidationRule.Expression` | `[Row].[Total] = [Row].[Q1] + [Row].[Q2]` |
| `RoleResourceGrant.RowFilterExpression` | `[Row].[PermitId] IN [User].[AllowedPermits]` |
| `ConditionalFormatDef.Expression` | `[Value] > [Column:Limit]` |

Якщо кожне матиме свій парсер — це чотири різні набори багів і чотири різні
поведінки на крайових випадках. Тому: **одна граматика, один парсер, один AST,
різні контексти розв'язання** (B03). Збірка не залежить ні від EF, ні від Domain —
її можна тестувати ізольовано і використовувати з `Ecr.Bootstrap.Excel`.

### 2.2 Правила залежностей (NetArchTest, CI)

До восьми правил із [13] §4 додаються чотири:

| # | Правило | Навіщо |
|---|---------|--------|
| 9 | `Ecr.Expressions` не залежить від `Ecr.Domain` і `Ecr.Infrastructure` | щоб мову виразів можна було використати в bootstrap-утиліті і в тестах без БД |
| 10 | Ніде поза `Ecr.Infrastructure` немає `Microsoft.Data.SqlClient` і `SqlBulkCopy` | інакше SQL просочиться в use-cases |
| 11 | Жодний тип у `Ecr.Domain`/`Ecr.Application` не містить у назві `Af`, `PiAf`, `Legacy`, `Excel`, `Permit`, `WaterBody`, `Substance` | автоматична перевірка «ядро домен-агностичне» |
| 12 | Жодного `DateTime.Now` / `DateTime.UtcNow` поза реалізацією `IClock` | інакше стани періодів і `IsLateEdit` неможливо тестувати |

Правило 11 — груба, але дуже дієва евристика: воно ловить саме ту помилку,
яку роблять на третьому місяці, коли «треба швидко».

---

## 3. Внутрішня будова `Ecr.Application`

### 3.1 Організація за можливостями, не за типами

```
Ecr.Application/
├─ Abstractions/          порти: IUnitOfWork, IClock, ICurrentUser, ICellStore,
│                         IMetadataCache, IJobScheduler, IExternalDataSource, …
├─ Templates/             Commands/ Queries/ Validators/ Dtos/
├─ SchemaEvolution/
├─ Registries/
├─ Documents/             ← найгарячіше: Cells/, Rows/, Generation/
├─ Periods/
├─ Workflow/
├─ Security/
├─ Integration/
├─ Excel/
└─ Common/                Result<T>, помилки, пагінація, ProblemDetails-коди
```

Без MediatR: команди — це звичайні класи-сервіси з одним публічним методом
і явними залежностями. Причина прагматична — на 12 підсистемах діагностика
через явний стек викликів дешевша, ніж через pipeline behaviors, а поведінки,
заради яких беруть MediatR (логування, транзакція, валідація), у нас і так
живуть у чітко визначених місцях: middleware, `IUnitOfWork`, `FluentValidation`.

### 3.2 Ключові порти (доповнення до [13] §6)

`[13] §6` фіксує шість портів (`IExternalDataSource`, `IPiAfDataReader`,
~~`IExternalDataSink`~~ — **не створюється, D-88**, `ICalculationModule`,
`IAccessDecisionService`, `IFormulaEngine`). Практика показує, що для гарячого шляху потрібні
ще три — саме вони дозволяють поміняти фізичну модель зберігання без переписування
use-cases (це і є страховка під відкрите питання §12 п.6 ТЗ).

```csharp
// ── Читання/запис комірок. За цим портом ховається ВЕСЬ вибір фізичної моделі
//    (нормалізована CellValue vs JSON-гібрид). Application про нього не знає.
public interface ICellStore
{
    Task<TableSlice> ReadSliceAsync(TableSliceRequest request, CancellationToken ct);

    Task<CellWriteResult> ApplyAsync(
        CellWriteBatch batch, CancellationToken ct);           // одна транзакція

    Task<IReadOnlyList<CellRef>> ReadDependentsAsync(
        IReadOnlyCollection<CellRef> changed, CancellationToken ct);
}

// ── Метадані опублікованої версії: завантажуються один раз і живуть у пам'яті.
public interface IMetadataCache
{
    ValueTask<TemplateSchema> GetAsync(int templateVersionId, CancellationToken ct);
    // Ключ усередині: v{versionId}:r{presentationRevision} — інвалідація не потрібна
}

// ── Планувальник фонових задач. Абстрагує Hangfire/Quartz — рішення §12 п.9 ТЗ
//    ще не прийнято ІБ, і не має тягнути за собою переписування коду.
public interface IJobScheduler
{
    Task<string> EnqueueAsync<TJob>(object args, CancellationToken ct) where TJob : IBackgroundJob;
    Task<string> ScheduleAsync<TJob>(object args, DateTimeOffset runAt, CancellationToken ct) where TJob : IBackgroundJob;
    Task<JobStatus> GetStatusAsync(string jobId, CancellationToken ct);
}
```

> **Чому `ICellStore` — найважливіший порт у системі.** Відкрите питання §12 п.6
> ТЗ («нормалізована модель чи JSON-гібрид») закривається лише після заміру на
> 108 млн рядків. Якщо use-cases пишуть напряму `db.CellValues.Where(...)`, зміна
> моделі означає переписати половину `Ecr.Application`. За портом — це заміна
> однієї реалізації в `Ecr.Infrastructure` і нічого більше. Порт треба зафіксувати
> **до** початку Етапу 0.5, а не після.

---

## 4. Наскрізні потоки

### 4.1 Відкриття таблиці (бюджет 1.5 с p95, [14] §2 п.3)

```
GET /api/v1/documents/{id}/tables/{tableCode}?period=9&offset=0&limit=500

 1. Middleware      → ідентичність із cookie, перевірка SecurityStamp (з кешу)   ~5 мс
 2. AccessProfile   → з кешу (userId+versionId+securityStamp)                    ~5 мс
 3. IMetadataCache  → TemplateSchema (immutable, у пам'яті)                      ~1 мс
 4. AccessDecision  → рівень на Sheet/Table, набір прихованих колонок,
                      RowFilterExpression → SQL-предикат                        ~10 мс
 5. ICellStore      → ReadSliceAsync: один запит, проєкція у плоский DTO        ~600 мс
 6. Формули         → нічого не рахується: значення вже в БД (IsCalculated=1)      0 мс
 7. Валідація       → останній результат із wf.ValidationResult (кеш)            ~20 мс
 8. Відповідь       → { columns[], rows[], cells[], permissions{}, validation[] }
```

**Три речі, які тут критичні і які легко зробити неправильно:**

1. Права **не резолвляться на комірку**. Відповідь містить мапу
   `{ columnCode → level }` і `{ rowKey → editable|reason }` — grid далі сам.
2. Формули **не рахуються при читанні**. Обчислені значення матеріалізовані
   в `CellValue` з `IsCalculated = 1`; читання — це читання.
3. Порожні комірки **не існують у БД** і не передаються. Клієнт бере
   `ColumnDef.DefaultValue`. На реальних звітах це найбільший виграш ([14] §3.1).

### 4.2 Batch-запис комірок (бюджет 300 мс на 100 комірок)

Детально — [B04](B04-write-path.md). Коротко:

```
PATCH /api/v1/documents/{id}/cells
  → перевірка доступу (одна на батч, не на комірку)
  → перевірка стану періоду і документа
  → cell-level валідація (тип, діапазон, список) — до запису
  → ТРАНЗАКЦІЯ:
        UPDATE/INSERT комірок (TVP, один round-trip)
        «торкнутися» TableRow → зростає RowVersion
        INSERT в aud.CellChange (набором, не по одній)
        перерахунок формул, що залежать, у межах ЦЬОГО TableInstance
        row/table-валідація зачеплених рядків
    COMMIT
  → позначити крос-аркушні залежності «брудними» (поза транзакцією)
  → відповідь: applied, recalculated[], validation[], newVersions{}
```

### 4.3 Публікація версії шаблону

```
POST /api/v1/templates/{id}/versions/{v}/publish

 1. MetadataValidator: усі вирази парсяться; усі посилання резолвяться;
    усі LookupRegistryDefId існують і опубліковані
 2. Побудова графа: FormulaDef + TableRelationDef → перевірка на цикли
 3. Топологічний порядок → FormulaDef.EvaluationOrder
 4. ⭐ Матеріалізація діапазонів: [7001001:7001005] → явний список RowKey
    за Ordinal НА ЦЕЙ МОМЕНТ → cfg.FormulaDependency  (B03 §4)
 5. Обчислення contentHash структурного шару
 6. Status = Published, PresentationRevision = 0
 7. aud.SchemaChange
```

Після цього структурний шар не змінюється **ніколи** — тому все, пораховане
на кроках 3–5, дійсне вічно і кешується без TTL.

### 4.4 Збір даних із зовнішнього джерела

```
Hangfire/Quartz → DataCollectionJob(dataSourceId, kind, period)
  → IExternalDataSource.CollectAsync
       PiAf: вибір транспорту за ext.DataSource.TransportKind
             WebApi   → POST /batch, Polly, failover primary/secondary
             SqlClient→ параметризований SELECT з WHERE на боці PI
  → сирі значення → ext.RawDataPoint (SqlBulkCopy, ідемпотентно за UQ)
  → itg.DataCollectionRun: скільки, скільки нових, помилки, тривалість
  → (окремим кроком, окремою задачею) ext.DataMappingDef → doc.CellValue
```

**Збір і мапінг — дві різні задачі.** Сире зберігається завжди; інтерпретація
може змінитися і бути переграною без повторного походу в PI.

### 4.5 Архівація закритого року

```
PeriodStateJob виявляє: Project.Status → Closed (рік завершився + YearGraceOffsetDays)
  → ArchiveJob(projectId)
       контрольні суми на джерелі (COUNT + CHECKSUM_AGG по партиціях року)
       INSERT ... SELECT батчами → arc.* (columnstore)
       контрольні суми на приймачі
       розбіжність → ROLLBACK + aud.ConsistencyIssue + СТОП
       збіг → TRUNCATE TABLE doc.CellValue WITH (PARTITIONS (...))
       itg.ArchiveRun
```

⚠ `SWITCH PARTITION` тут **не працює** (різні файлові групи і різний тип індексу) —
деталі і обґрунтування в [B02](B02-persistence.md) §6. Це відхилення від формулювання
ТЗ АРХ-3, винесене в [B10](B10-decisions.md) як пропозиція правки.

---

## 4а. Карта API

Фронтенд генерує типи з OpenAPI (`B21` §12, FE-1), тому кожна область має
мати ресурс. Нижче — карта: підсистема → ресурс → що робить → чи довга операція.
Усе під `/api/v1`. Деталі полів — у профільних документах.

| Підсистема | Ресурс | Методи | Призначення | 202? |
|-----------|--------|--------|-------------|:----:|
| Документи | `/documents/{id}/tables/{code}` | GET | зріз таблиці (§4.1) | — |
| | `/documents/{id}/cells` | PATCH | батч-запис (§4.2) | — |
| | `/documents/{id}/import` | POST | імпорт `.xlsx` | ✔ |
| Шаблони | `/templates`, `/templates/{id}/versions` | GET/POST/PUT | структура як дані | — |
| | `/templates/{id}/versions/{v}/impact` | GET | аналіз впливу (§5) | — |
| | `/templates/{id}/versions/{v}/publish` | POST | публікація (§4.3) | ✔ |
| Реєстри | `/registries`, `/registries/{code}/entries` | GET/POST/PUT | довідники | — |
| | `/registries/{code}/entries/{id}/usage` | GET | «де використовується» | — |
| Розрахунки | `/calc/methodologies[/{id}/versions]` | GET/POST/PUT | дерево методологій | — |
| | `/calc/versions/{id}/formulas`, `/constants`, `/rules` | GET/POST/PUT/DELETE | вміст версії | — |
| | `/calc/versions/{id}/tests[/run]` | GET/POST | тест-кейси і прогін | — |
| | `/calc/versions/{id}/coverage` | GET | покриття правил на даних | ✔ |
| | `/calc/versions/{id}/publish` | POST | публікація версії | ✔ |
| | `/calc/scripts/{id}/versions` | GET/POST | рівень 2 (Roslyn) | — |
| | `/calc/pipelines[/{id}/versions]` | GET/POST/PUT | конвеєр | — |
| | `/calc/runs`, `/calc/runs/{id}` | GET/POST | прогони і слід | ✔ |
| Звітність | `/reports`, `/reports/{code}/versions` | GET/POST/PUT | визначення звіту | — |
| | `/reports/{code}/snapshots` | GET/POST | побудова і читання зрізу | ✔ |
| | `/reports/{code}/snapshots/{id}/export` | GET | вивантаження | — |
| Джерела | `/sources`, `/sources/{id}/test` | GET/POST/PUT | джерела і перевірка зв'язку | — |
| | `/sources/{id}/catalog` | GET | каталог сутностей джерела | — |
| | `/sources/{id}/entities[/{eid}/mapping]` | GET/POST/PUT | сутності і мапінг | — |
| | `/sources/{id}/entities/{eid}/collect` | POST | «Забрати зараз» | ✔ |
| | `/schedules` | GET/POST/PUT | розклад збору | — |
| | `/consistency/rules`, `/consistency/runs` | GET/POST | перевірки | ✔ |
| Безпека | `/security/roles`, `/profiles`, `/me/profile` | GET/POST/PUT | RBAC і ефективні права | — |
| Періоди | `/periods`, `/periods/{id}/access` | GET/POST/PUT | періоди і матриця доступу | — |
| Workflow | `/workflow/tasks`, `/workflow/transitions` | GET/POST | затвердження | — |
| Задачі | `/jobs/{id}` | GET/DELETE | прогрес і скасування (FE-4) | — |
| Експлуатація | `/ops/health`, `/ops/metrics`, `/ops/alerts` | GET/PUT | дашборд і правила алертів | — |

Кожен ресурс із ✔ повертає `202 + { jobId }`; клієнт стежить через `/jobs/{id}`
і скасовує через `DELETE /jobs/{id}`.

---

## 5. API: конвенції понад [13] §7

`[13] §7` фіксує стиль, версіонування, `ProblemDetails`, ідемпотентність,
`ETag`/`If-Match`, пагінацію, 202+jobId, локалізацію, кореляцію. Доповнення:

| Аспект | Рішення |
|--------|---------|
| **Коди помилок** | стабільний машиночитний `errorCode` у форматі `ECR-<домен>-<номер>` (`ECR-CELL-0409` конфлікт версій). Клієнт ніколи не парсить текст |
| **Причина відмови в доступі** | `EditDenyReason` віддається клієнту як рядок enum, не як текст. Локалізація — на клієнті |
| **Часткові успіхи** | заборонені для мутацій даних. `PATCH /cells` — або весь батч, або 409. Часткові успіхи допускаються лише в **фонових** задачах (імпорт, збір), і тоді результат — звіт |
| **`limit` обов'язковий** | max 500 для списків; для зрізу таблиці — типово **500**, max **2000** (те саме значення в B21 §3.2 і B04 §4.4) |
| **Формат `null`** | **Три** різні операції (B04 §2.2, §6): `value: null` = «стерти значення» (рядок `CellValue` видаляється); `isEmpty: true` = «явно порожньо» (рядок існує з `IsEmpty = 1`); відсутність поля `value` = «не чіпати». Плутанина тут коштує даних |
| **`If-Match`** | на рівні **рядка**, не документа (B04 §2). Заголовок несе версію `TableInstance`; версії рядків — у тілі |
| **Аналіз впливу** | `GET /api/v1/templates/{id}/versions/{v}/impact` → `{ documents, cells, periods }` по кожному елементу `ChangeSet` (B10 §3.2); `GET /api/v1/registries/{code}/entries/{id}/usage` → де використовується запис реєстру. Обидва — читання, кешовані на `ChangeSet.hash` |

---

## 6. Конфігурація і розгортання

### 6.1 Що живе у конфігураційних файлах

ТЗ ФВ-2.14 категоричний: **у файлах — тільки рядки підключення і технічні
параметри інфраструктури**. Практичний перелік:

```jsonc
{
  "ConnectionStrings": { "Ecr": "…" },          // сама строка — з Key Vault/DPAPI
  "Schema": { "StartupMode": "Validate" },       // Validate | Migrate | Recreate
  "Cache":  { "DistributedProvider": "SqlServer" },
  "Jobs":   { "Provider": "Quartz", "WorkerCount": 4 },   // порт IBackgroundJobScheduler (D-09)
  "Telemetry": { "OtlpEndpoint": "…" },
  "Auth":   { "CookieName": "ecr.auth", "SlidingHours": 8 }
  // ⛔ НЕ тут: політика паролів, offsets періодів, ролі, мапінги, адреси PI AF
}
```

Адреси PI AF — у `ext.DataSource` (БД), політика паролів — у `sec.PasswordPolicy`,
offsets — у `doc.PeriodPolicy`. Це не педантизм: інакше DEV/TEST/PROD розповзаються,
і «чому на тесті інша поведінка» стає щоденним питанням.

### 6.2 IIS і дві схеми автентифікації

Пастка §13.5 п.7 ТЗ, яка коштує дня на першому деплої:

```
IIS site
  Anonymous Authentication  = Enabled     ← інакше локальні користувачі
  Windows Authentication    = Disabled       не дійдуть до форми входу
        │
        ▼
ASP.NET Core
  DefaultScheme = Cookie
  AddNegotiate()  — застосовується ТІЛЬКИ на endpoint /login/windows
                    через [Authorize(AuthenticationSchemes = NegotiateDefaults...)]
```

Обидва endpoint'и (`/login/windows`, `/login/local`) підписують **ту саму** cookie
з тими самими claims. Решта API про провайдера не знає (B05 §1).

### 6.3 Старт застосунку

Послідовність із ТЗ §5.4-B, з двома уточненнями:

```
1. Retry-очікування доступності БД (БД піднімається довше застосунку)
2. Порівняння міграцій: у БД є те, чого немає у збірці → ФАТАЛЬНО
3. Validate (PROD): pending.Any() → ФАТАЛЬНО зі списком
   Migrate (TEST): sp_getapplock → Migrate() → release
4. Ідемпотентний seed: sec.Permission, вбудовані ролі, admin, PeriodPolicy, cfg.Language
5. MetadataValidator: Critical → не стартуємо; Warning → лог + /health/metadata
6. ⭐ Прогрів: завантажити метадані активних Published-версій у пам'ять
7. ⭐ Перевірка партицій: чи є вільна партиція під наступний період
   → якщо ні, Warning у health (партиції створює задача, не старт)
```

Кроки 6–7 додані: без прогріву перші користувачі після деплою платять за
завантаження схеми; без перевірки партицій система тихо доживає до дня, коли
всі нові дані падають в останню партицію.

### 6.4 Права облікового запису застосунку в БД

| Схема | Права |
|-------|-------|
| `cfg`, `dic`, `doc`, `wf`, `sec`, `itg`, `ext`, `calc`, `rpt` | `SELECT`, `INSERT`, `UPDATE`, `DELETE` |
| `aud` | **тільки `INSERT`** і `SELECT`; `UPDATE`/`DELETE` відкликані явно |
| `arc` | `SELECT`; запис — лише через процедуру архівації під окремим principal |
| DDL | **немає взагалі** у PROD. Міграції накочує конвеєр окремим обліковим записом |

Це прямо реалізує ФВ-5.21 («незмінний журнал») на рівні БД, а не «домовленості
не робити `UPDATE`».

---

## 7. Що цей документ уточнює відносно ТЗ

Повний список із обґрунтуванням — [B10](B10-decisions.md). Коротко тут, щоб
не загубилося:

| # | Місце в ТЗ | Уточнення |
|---|-----------|-----------|
| 1 | [07] `doc.CellValue.Id bigint PK` | прибрати сурогат; кластерний PK `(PeriodKey, TableRowId, ColumnDefId)` — B02 §2 |
| 2 | [07] «партиціонування по `PeriodId`» | партиціонувати по денормалізованому `PeriodKey = Year*100+Sequence` — B02 §5 |
| 3 | ТЗ АРХ-3 «`SWITCH PARTITION` або `DELETE` батчами» | `INSERT…SELECT` + звірка + `TRUNCATE … WITH (PARTITIONS)` — B02 §6 |
| 4 | [10] §2a «тригер `INSTEAD OF UPDATE`» | `AFTER`-тригер із `THROW`; `PresentationRevision` інкрементує застосунок — B02 §4 |
| 5 | [13] §6 (шість портів) | + `ICellStore`, `IMetadataCache`, `IJobScheduler` — §3.2 вище |
| 6 | ТЗ §5.2 (NCalc) | NCalc як **обчислювач**, поверх власного парсера посилань; одна мова на 4 контексти — B03 |
