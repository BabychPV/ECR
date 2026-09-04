# Завдання на розробку backend

> ⬛ **З 2026-09-03 діє консолідоване ТЗ — [`tz/`](../../tz/00-README.md).**
> Цей документ лишається як завдання на розробку з контрактами портів і
> порядком робіт. Джерело істини щодо рішень — [`tz/10-decisions.md`](../../tz/10-decisions.md).

**Кому:** розробнику/архітектору backend, який приймає напрацювання і доповнює ТЗ.
**Статус:** проєктна чернетка, готова до доповнення.
**Що вже вирішено, а що ні** — див. §12.

---

## 0. Як читати цей пакет документів

| Спочатку | Документ | Навіщо |
|----------|----------|--------|
| 1 | [01-as-is-overview.md](../as-is/01-as-is-overview.md) | що замінюємо і чому |
| 2 | [06-tz-architecture.md](06-tz-architecture.md) | ТЗ: вимоги, стек, рішення |
| 3 | **цей документ** | що саме програмувати, у якому порядку, з якими контрактами |
| 4 | [07-data-model.md](07-data-model.md) | схема БД |
| 5 | [09](09-template-engine.md) / [10](10-schema-evolution.md) / [11](11-security-model.md) / [12](12-reference-data.md) | чотири підсистеми ядра в деталях |
| 6 | [14-performance.md](14-performance.md) | бюджет продуктивності — **не опційний** |
| 7 | [15-verified-stack.md](15-verified-stack.md) | ліцензії, перевірені за першоджерелами |

**Вхідні матеріали:** `RDS_xxx_Template_Land_2026_v1.0.4.0 2.xlsm` (24 аркуші)
і `vba-files/` (~45 000 рядків VBA) у корені репозиторію.

---

## 1. Одним абзацом: що будуємо

Універсальний інструмент структурованої звітності: **проєкт на рік -> версійний
шаблон (аркуші, таблиці, стилі, формули, правила) -> згенеровані документи ->
заповнення -> валідація -> затвердження**. ECR — перший сценарій застосування,
але ядро не знає ні про екологію, ні про PI AF.

**Три архітектурні тези, від яких усе залежить:**

1. **Структура — це дані.** Фізична схема БД фіксована (~40 таблиць) і не
   змінюється при зміні шаблону. Додати колонку = `INSERT` у `cfg.ColumnDef`.
2. **Ідентичність явна.** Рядок ідентифікується `RowKey`, поле — `ColumnDef.Code`,
   значення довідника — `RegistryEntryId`. Ніяких позиційних і текстових ключів.
3. **Опубліковане структурно незмінне.** `TemplateVersion` після `Publish` —
   структура заморожена; **презентаційний шар** (підписи, стилі, порядок) патчиться
   «на льоту» з `PresentationRevision`. Це дає безпечну еволюцію і кеш, ключ якого
   `v{version}:r{revision}` не потребує явної інвалідації.

---

## 2. Верифіковані факти про предметну область

Отримані **автоматичним аналізом файлу**, не зі слів. На них можна спиратися.

### 2.1 Формули: 11 функцій, а не 25

| Функція | Входжень |
|---------|---------:|
| `VLOOKUP` | 429 |
| `LEFT` | 384 |
| `SUM` | 245 |
| `VALUE` | 230 |
| `IFERROR` | 228 |
| `ROUNDDOWN` / `TEXT` / `CHAR` / `CONCATENATE` / `TEXTBEFORE` / `TEXTAFTER` | 17 разом |

⚠ **429 `VLOOKUP` — це не формули, а посилання на довідники.**
У новій моделі вони зникають: їх замінює `ColumnDef.LookupRegistryDefId`.
Реальних обчислювальних формул лишається **~250** (`SUM` + арифметика).

**Наслідок для оцінки:** ризик «розбіжність рушіїв формул», який у першій
редакції ТЗ був позначений найбільшим, **істотно менший**, ніж здавалося.

### 2.2 Граф крос-аркушних залежностей: 7 ребер

```
'7. Water Report'          -> 'Configuration'      273 формули (VLOOKUP -> довідники)
'7.0 Water consolidation'  -> '7. Water Report'    216            (Rollup)
'7a.Water measurements'    -> '7. Water Report'    191            (Mirror)
'8. Waste Report'          -> 'Configuration'      156            (VLOOKUP -> довідники)
'7b.Water measurements'    -> '7. Water Report'    100            (Mirror)
'7. Water Report'          -> '2. Contract'          1            (рік зі строки версії)
'8. Waste Report'          -> '2. Contract'          1
```

**Наслідок:** увесь граф залежностей — це **три `Rollup`/`Mirror` зв'язки**
(`7.0`, `7a`, `7b` від `7.`) плюс посилання на довідники. Решта 18 аркушів
**незалежні**. Це набагато простіше, ніж виглядало.

### 2.3 Дані про «вагу» файлу

| Частина | Розмір | Коментар |
|---------|--------|----------|
| Загалом `.xlsm` | 4.73 МБ | |
| `vbaProject.bin` | 2.99 МБ | копіюється в **кожен** HSE-файл |
| `styles.xml` | 1.23 МБ | ~40 унікальних стилів, роздутих до тисяч записів |
| `externalLinks/*` | **6.41 МБ** (нестиснуто), з них `externalLink14` — 5.9 МБ | **у формулах не використовуються жодного разу** |
| `calcChain.xml` | 0.19 МБ | |

**287 defined names, з них 87 (30%) містять `#REF!`** — тобто вказують у нікуди.
16 зовнішніх книг згадуються лише в named ranges (напр. `'[2]DropdownList 2015'`,
`'[5]Attachment 5'`), у формулах — нуль посилань.

**Наслідок:** ~6.4 МБ і 87 named ranges — **мертвий вантаж**, який тягнеться
в кожен згенерований HSE-файл. При міграції їх треба ігнорувати, а не переносити.
Bootstrap-імпортер має це відфільтрувати і **видати звіт**, щоб бізнес підтвердив,
що нічого потрібного не втрачено.

### 2.4 Структура `DropdownList`

Рядки 3–4 кожної колонки — **прив'язка до комірок цільового аркуша**:

```
D: Contract_Area                → B, 6      = Contract!B6
M: Stationary_Type_of_Equipment → E5, E146  = Stationary!E5:E146
Z: Waste_Name_Facility_1        → Row 16
```

Колонка `AL` — паралельний стовпець ID (9 номерів дозволів ↔ 9 GUID з AF).

**Наслідок:** прив'язка «довідник -> комірка» вже існує як дані, просто в
незручному вигляді. Bootstrap-імпортер може її прочитати і згенерувати
`ColumnDef.LookupRegistryDefId` автоматично.

---

## 3. Структура рішення

```
Ecr.sln
├─ src/
│  ├─ Ecr.Domain/                 сутності, інваріанти, доменні події, ЖОДНИХ залежностей
│  ├─ Ecr.Application/            use-cases, порти, DTO, валідатори
│  ├─ Ecr.Infrastructure/         EF Core, репозиторії, міграції, кеш, Hangfire
│  ├─ Ecr.Formulas/               рушій формул (NCalc) + граф залежностей
│  ├─ Ecr.Excel/                  експорт/імпорт (ClosedXML)
│  ├─ Ecr.Adapters.PiAf/          IExternalDataSource: WebApi + SqlClient
│  ├─ Ecr.Calculations/           ICalculationModule (перша черга, D-54)
│  ├─ Ecr.Api/                    ASP.NET Core host
│  └─ Ecr.Web/                    React SPA
├─ tools/
│  ├─ Ecr.Bootstrap.Excel/        імпорт структури і довідників з .xlsm
│  └─ Ecr.Migration.PiAf/         міграція історичних даних
└─ tests/
   ├─ Ecr.Domain.Tests/
   ├─ Ecr.Application.Tests/
   ├─ Ecr.SchemaEvolution.Tests/  усі класи змін структури + відкат
   ├─ Ecr.Formulas.Tests/         еквівалентність NCalc / formulajs / Excel
   ├─ Ecr.Security.Tests/         20 сценаріїв з 11-security-model.md §10
   ├─ Ecr.Integration.Tests/      Testcontainers (SQL) + WireMock (PI)
   ├─ Ecr.Performance.Tests/      JMeter-сценарії (.jmx), бюджет із 14-performance.md
   └─ Ecr.Architecture.Tests/     NetArchTest — правила §4
```

---

## 4. Архітектурні правила, які перевіряються тестами

Не «домовленості», а автотести в CI. Без них шари розповзуться за три спринти.

| # | Правило | Перевірка |
|---|---------|-----------|
| 1 | `Ecr.Domain` не залежить ні від чого, крім BCL | NetArchTest |
| 2 | `Ecr.Domain` і `Ecr.Application` **не посилаються** на `Ecr.Adapters.PiAf` | NetArchTest — інакше «незалежність від AF» декларація |
| 3 | `Ecr.Application` не посилається на EF Core | NetArchTest — доступ через порти |
| 4 | Жодного `DbContext` у контролерах | NetArchTest |
| 5 | Жодного `.Result` / `.Wait()` | Roslyn analyzer |
| 6 | Жодного `.ToList()` без `Take()` у Application | Roslyn analyzer |
| 7 | Усі публічні API мають OpenAPI-опис | тест на згенерований OpenAPI-документ (`Microsoft.AspNetCore.OpenApi`) |
| 8 | Рішення про доступ — **тільки** через `IAccessDecisionService` | NetArchTest: заборона прямих перевірок ролей поза ним |

---

## 5. Порядок робіт по модулях

Порядок обраний так, щоб кожен наступний модуль спирався на готовий попередній,
і щоб найризикованіше було зроблено раніше.

### М0. Каркас (Етап 0)

| Задача | Критерій готовності |
|--------|---------------------|
| Solution, CI/CD, DEV-середовище | `dotnet build` + деплой на DEV з конвеєра |
| Перевірка ліцензій у CI (§7 [15](15-verified-stack.md)) — **першою задачею** | збірка падає на GPL/AGPL/SSPL/Polyform/cFOSS/noncommercial |
| `Ecr.Architecture.Tests` із правилами §4 | усі 8 правил зелені |
| **Прототип: 108 млн рядків `CellValue`** | заміряні п.3 і п.5 бюджету [14](14-performance.md) |
| **Прототип grid**: RevoGrid (основний) і Glide Data Grid (запасний) на `9. Utility` (471×60) | вставка з Excel, fill, undo працюють; для RevoGrid підтверджено, що це в MIT-ядрі |
| Тест еквівалентності формул (11 функцій) | звіт розбіжностей NCalc / formulajs / Excel |

> **Gate:** без результатів двох прототипів не починати М1.
> Вони визначають модель зберігання і grid-компонент.

### М1. Конфігуратор структури

**Домен:** `Template`, `TemplateVersion`, `SheetDef`, `TableDef`, `ColumnDef`,
`RowDef`, `FormulaDef`, `ValidationRule`, `StyleDef`, `ConditionalFormatDef`,
`TableRelationDef`, `SheetGroupRule`, `PeriodAccessRuleDef`.

**Ключові інваріанти (тестуються):**
* опублікована версія **структурно** незмінна — на рівні домену **і** тригера БД;
  тригер пропускає лише презентаційні поля та інкрементує `PresentationRevision`;
* `CloneFrom` копіює весь граф глибоко;
* `Publish` валідує цілісність (формули парсяться, немає циклів, посилання резолвяться).

**Критерій готовності:**
* структура чинного шаблону 1.0.4.0 заведена повністю (~90 таблиць);
* ⭐ **тест на універсальність:** створено проєкт «Бюджет 2027» з іншими
  аркушами і **без жодного `TableRelationDef`** — без нового коду.

### М2. Еволюція структури

`ISchemaEvolutionService`:

```csharp
Task<ChangeImpact> AnalyzeAsync(SchemaChange change, CancellationToken ct);
Task<Result>       ApplyAsync(SchemaChange change, ChangeStrategy? strategy, CancellationToken ct);
Task<MigrationPlan> PlanDocumentMigrationAsync(long documentId, int targetVersionId, CancellationToken ct);
Task<Result>       MigrateDocumentAsync(long documentId, int targetVersionId, bool dryRun, CancellationToken ct);
```

**Критерій готовності:** усі чотири класи змін (`Presentation`/`Safe`/`Guarded`/`Breaking`)
поводяться за [10-schema-evolution.md](10-schema-evolution.md);
є тест «застосували -> відкотили -> дані байт-у-байт незмінні».

### М3. Безпека

* два провайдери автентифікації, одна cookie застосунку;
* `sec.Permission` (системний каталог), `Role`, `RoleResourceGrant`, `RoleAssignment`;
* `IAccessDecisionService` + `AccessProfile`-кеш.

**Критерій готовності:** усі 20 сценаріїв з [11-security-model.md](11-security-model.md) §10 зелені,
зокрема — локальний і доменний користувач з однаковими ролями мають **ідентичні** права.

### М4. Довідники та реєстри

`cfg.Registry*` + `dic.Registry*` + generic-CRUD + generic-синхронізатор.

**Критерій готовності:** `Permit` з каскадами, вкладеними речовинами,
M:N з водними об'єктами і вікном дії **заведено як конфігурацію, без коду**.

### М5. Дані документа

`Document`, `TableInstance`, `TableRow`, `CellValue`, batch-запис, оптимістичне блокування.

**Критерій готовності:** бюджет п.3 і п.5 [14-performance.md](14-performance.md) на реальному обсязі.

### М6. Формули і валідація

**Критерій готовності:** `7. Water Report` (394 формули, GROUP/ITEM/BALANCE, permits)
працює повністю; значення збігаються з Excel до 10-го знака.

### М7. Періоди і workflow

Стани періоду з offsets, `PeriodStateJob`, `Draft -> Submitted -> Approved`,
`IsLateEdit` в аудиті.

### М8. Excel export/import

### М9. Адаптер PI AF (два транспорти)

### М10. Архівація

### М11. Міграція історії

---

## 6. Контракти портів (те, що треба зафіксувати першим)

Ці інтерфейси — межа між ядром і рештою світу. Їх варто узгодити **до** написання
реалізацій, бо вони визначають, чи буде ядро справді незалежним.

```csharp
// ── Зовнішні джерела ───────────────────────────────────────────────
public interface IExternalDataSource
{
    string Code { get; }
    Task<HealthResult> CheckHealthAsync(CancellationToken ct);
    Task<CollectionResult> CollectAsync(CollectionRequest request, CancellationToken ct);
}

// Дві реалізації: PiWebApiDataReader і PiSqlClientDataReader
public interface IPiAfDataReader
{
    Task<IReadOnlyList<AfElement>>   GetElementsAsync(AfQuery q, CancellationToken ct);
    Task<IReadOnlyList<AfEventFrame>> GetEventFramesAsync(AfQuery q, CancellationToken ct);
    Task<IReadOnlyList<AfAttributeValue>> GetAttributeValuesAsync(
        IReadOnlyCollection<string> refs, CancellationToken ct);
    AfTransportKind Transport { get; }   // WebApi | SqlClient
}

// ⛔ НЕ СТВОРЮЄТЬСЯ ВЗАГАЛІ (D-88, уточнює D-44).
// Запису в AF немає, а порожній контракт «на майбутнє» — це запрошення
// колись його реалізувати. Оголошення нижче лишено як історія рішення.
//
// public interface IExternalDataSink
// {
//     Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct);
// }

// ── Розрахунки (перша черга, D-54) ─────────────────────────────────────
public interface ICalculationModule
{
    string Code { get; }
    string MethodologyVersion { get; }
    Task<CalculationResult> CalculateAsync(CalculationContext ctx, CancellationToken ct);
}

// ── Доступ ─────────────────────────────────────────────────────────
public interface IAccessDecisionService
{
    Task<AccessProfile> BuildProfileAsync(int userId, int templateVersionId, CancellationToken ct);
    AccessLevel  GetLevel(AccessProfile profile, ResourceRef resource);
    EditDecision CanEdit(AccessProfile profile, CellRef cell, DocumentState doc, PeriodState period);
}

// EditDecision повертає не bool, а причину — UI має пояснювати, чому поле сіре
public readonly record struct EditDecision(bool Allowed, EditDenyReason Reason);

public enum EditDenyReason
{
    None, NoPermission, PeriodClosed, DocumentApproved,
    StructurallyReadOnly, CalculatedCell, OutsidePermitWindow, RowFiltered
}

// ── Формули ────────────────────────────────────────────────────────
public interface IFormulaEngine
{
    FormulaParseResult Parse(string expression);
    Task<RecalculationResult> RecalculateAsync(RecalculationScope scope, CancellationToken ct);
}
```

**Правило для `ICalculationModule`:** це **окрема** точка розширення від
`IFormulaEngine`. Розрахунок емісій — не Excel-формула; не можна припускати,
що всі обчислювані значення виражаються формульним синтаксисом.

---

## 7. Конвенції API

| Аспект | Рішення |
|--------|---------|
| Стиль | REST + OpenAPI; типи для фронту генеруються (`orval` / `openapi-typescript`) |
| Версіонування | `/api/v1/...`; ламаючі зміни — нова версія |
| Помилки | RFC 7807 `ProblemDetails` + машиночитний `errorCode` |
| Валідація | 422 зі списком `{path, code, message, severity}` — щоб UI підсвітив конкретну комірку |
| Ідемпотентність | `Idempotency-Key` на мутаціях, що можуть повторитися (імпорт, публікація) |
| Конкурентність | `If-Match` / `ETag` на основі `RowVersion` -> 409 з diff |
| Пагінація | cursor-based; `limit` обов'язковий, max 500 |
| Довгі операції | 202 + `jobId`; прогрес через `/api/v1/jobs/{id}` |
| Локалізація | `Accept-Language`; довідники віддають `DisplayText` уже вибраною мовою |
| Кореляція | `X-Correlation-Id` наскрізь, потрапляє в логи і в аудит |

**Формат batch-запису комірок** (найгарячіший ендпоінт):

```
PATCH /api/v1/documents/{id}/cells
{
  "tableInstanceId": 9001,
  "baseVersion": "0x00000000000A1B2C",
  "changes": [
    { "rowKey": "7001001", "columnCode": "MonthValue", "value": 125.5 },
    { "rowKey": "7001002", "columnCode": "MonthValue", "value": null, "isEmpty": true }
  ]
}
→ 200 { "applied": 2, "recalculated": [...], "validation": [...], "newVersion": "..." }
→ 409 { "conflicts": [ { "rowKey": "...", "columnCode": "...", "theirValue": ... } ] }
```

Один запит на діапазон, не на комірку — це прямо випливає з бюджету
п.5 у [14-performance.md](14-performance.md).

---

## 8. Алгоритми, які треба перенести 1:1

Від них залежить сумісність із наявними даними. Кожен — з автотестом на реальних
прикладах із чинного файлу.

| # | Алгоритм | Джерело у VBA |
|---|----------|---------------|
| 1 | Формат експорту значення (error -> `""`, empty -> `"NULL"`, число -> плоский десятковий за форматом комірки) | `CellValueForExport` (`HSE_Utility.bas`) |
| 2 | Іменування Event Frame `<base>_<user>_<yyyy_mm_dd_HH_MM_SS>` | `General.GenerateEventFrameName` |
| 3 | `Attribute_XXXX` = `Format(rowOffset * 10, "0000")` | усі `MapAttributes` |
| 4 | Порядок полів у `;`-рядку — **індивідуальний для кожної таблиці** | кожен `MapAttributes` окремо |
| 5 | `Permit_Number -> PermitId`, `Name_En -> Name_water_body_Id` | `DictionaryEF.PermitNumberToId` |
| 6 | Витяг ID з композита `"7001001 - Name"` | `HSE_WaterReport.MapAttributes` |
| 7 | Перевірка діапазону років | `General.ValidateStartYearVsContractYear` |
| 8 | Перетин вікна дії permit із місяцем | `Sheet_Config.ApplyPermitMonthLocks` |
| 9 | «Редагований місяць» за cutoff day | `Protection.GetEditableMonthName` |
| 10 | `8. Waste Report`: місяць -> рядок = `3 + (m-1)*33` | `BulkSave.RowForMonthIndex` |
| 11 | UTC <-> local конверсія | `General.ConvertUtcToLocal`, `TimeZoneBias` |
| 12 | Розширення групи водних аркушів | `UserSelection.ExpandLinkedSheetGroups` |

> П. 4 — найпідступніший: порядок полів **різний для кожної з ~90 таблиць**
> і ніде не задокументований, крім самого коду. Bootstrap-імпортер має
> витягнути його автоматично з `MapAttributes` і записати в
> `ext.LegacyColumnMapping.LegacyFieldIndex`. Робити це вручну — гарантована помилка.

---

## 9. Утиліти, які треба зробити рано

### 9.1 `Ecr.Bootstrap.Excel`

Без неї заведення структури — це ~3 600 записів руками.

| Вхід | Вихід |
|------|-------|
| `.xlsm` + `Configuration.bas` | `SheetDef` / `TableDef` / `ColumnDef` / `RowDef` |
| `Sheet_Config.bas` (`Init*`) | `IsReadOnly` для діапазонів |
| `Protection.bas` (`hdrRows`) | `PeriodAccessRuleDef` типу `HeaderRows` |
| Data Validation з аркушів | `ValidationRule` |
| `styles.xml` | ~40 `StyleDef` (згорнути тисячі комбінацій) |
| Формули | `FormulaDef` (крім `VLOOKUP` -> `LookupRegistryDefId`) |
| `DropdownList` (рядки 2–4 + значення) | `RegistryDef` + прив'язка до `ColumnDef` |
| `Configuration` R…BL | реєстри `Substance`, `Permit`, `WaterBody`, waste/water items |
| `MapAttributes` у 19 модулях | **`ext.LegacyColumnMapping.LegacyFieldIndex`** |

**Обов'язково:** звіт про те, що **не** імпортовано — 87 `#REF!`-імен,
16 зовнішніх книг, мертві діапазони. Бізнес має підтвердити, що це не потрібно.

### 9.2 `Ecr.Migration.PiAf`

Вивантаження історії 2025–2026, парсинг `;`-рядків за `LegacyFieldIndex`,
створення документів, **round-trip-звірка** (згенерувати `;`-рядок заново
і побайтно порівняти).

⚠ Окремий крок, який легко недооцінити: **зіставлення текстових значень
довідників з ID**. У даних лежить `"Diesel - Дизель"`, а не ID.

---

## 10. Definition of Done для будь-якої задачі

- [ ] Unit-тести на доменну логіку
- [ ] Інтеграційний тест на Testcontainers (якщо є БД)
- [ ] Архітектурні тести зелені
- [ ] OpenAPI оновлено, типи фронту перегенеровано
- [ ] Метрики і логи додано (`CorrelationId` наскрізь)
- [ ] Якщо ендпоінт є в таблиці §2 [14-performance.md](14-performance.md) — заміряно і в бюджеті
- [ ] Якщо зачіпає права — сценарій у `Ecr.Security.Tests`
- [ ] Якщо зачіпає структуру — сценарій у `Ecr.SchemaEvolution.Tests`
- [ ] Немає нових залежностей поза [15-verified-stack.md](15-verified-stack.md)

---

## 11. Що backend-розробник має доповнити в ТЗ

Свідомо залишено на нього — тут потрібне рішення того, хто писатиме код:

| # | Тема | Що вирішити |
|---|------|-------------|
| 1 | Модель зберігання комірок | ✅ **закрито**: нормалізована базова + гейт BR-07; гібрид — **вибірково по таблицях** (`StorageMode`), не глобально ([tz/10](../../tz/10-decisions.md) D-21) |
| 2 | Стратегія партиціонування | ✅ **закрито**: ключ `PeriodKey` = `Year*100 + Sequence`, PK `(PeriodKey, TableRowId, ColumnDefId)`; архівація — `INSERT…SELECT` + звірка + `TRUNCATE … WITH (PARTITIONS)`, **не `SWITCH`** ([tz/04](../../tz/04-data.md) §4.5) |
| 3 | Синтаксис виразів | точна граматика формул, правил валідації, `RowFilterExpression`; чи один парсер на все |
| 4 | Модель конкурентності | `RowVersion` на рядку vs на `TableInstance`; поведінка при конфлікті |
| 5 | Транзакційні межі | що в одній транзакції при batch-записі з перерахунком і валідацією |
| 6 | Структура схеми `calc` | під розрахункові модулі — після формування контексту по бекенду |
| 7 | Формат `ChangeSet` і `MigrationPlan` | серіалізація, зберігання, відтворюваність |
| 8 | Стратегія кешу при кількох інстансах | `IMemoryCache` vs `IDistributedCache` (SQL) vs Garnet — для кожного виду кешу; **не Redis** |
| 9 | Політика ретраїв до PI AF | таймаути, backoff, circuit breaker, поведінка при частковій відмові |
| 10 | Формат аудиту | ✅ **частково закрито**: запис **пакетний**; retention — «нічого не затирається» (D-25); партиціонування по `ChangedAt`, архівація щомісячна |
| 11 | **EF Core + тригери** | `HasTrigger()` на таблицях `cfg.*` з тригерами immutability, або `AFTER`-тригер з `ROLLBACK` замість `INSTEAD OF` (ТЗ §13.5 п.1) |
| 12 | **Вирівняні індекси при партиціонуванні** | `PeriodKey` у PK і **всіх** unique-індексах `CellValue`/`TableRow`. `arc.*` навмисно **не** ідентична (columnstore, окрема файлова група), тому `SWITCH` не використовується |
| 13 | **Семантика діапазонів у формулах** | «діапазон рядків» = список `RowKey`, матеріалізований за `Ordinal` на момент `Publish` (§13.5 п.5) |
| 14 | **Bootstrap із VBA** | межа автоматики vs ручної звірки по ~90 таблицях (§13.5 п.4) |

> Оцінка реалістичності обсягу і строків — **ТЗ §13**. Ключове: починати з
> **вертикального зрізу** (`7. Water Report` крізь усі шари, без UI конструктора),
> а не з generic-конфігуратора.

---

## 12. Реєстр рішень

### ✅ Прийнято — не переглядати без вагомої причини

| # | Рішення | Обґрунтування |
|---|---------|---------------|
| 1 | **.NET 10 LTS** | .NET 8/9 — EOL 10.11.2026 |
| 2 | **EF Core**, не DataObjects.Net | схема фіксована -> головна перевага DataObjects.Net не працює; ТЗ §5.4-A |
| 3 | **SQL — система обліку**, PI AF — джерело | AF не дає транзакційності, версійності, RBAC |
| 4 | **Структура = дані**, жодного DDL під час роботи | [10-schema-evolution.md](10-schema-evolution.md) §1 |
| 5 | **Immutable published versions** | безпечна еволюція + тривіальний кеш |
| 6 | **React + TypeScript**, один SPA | адмінка і робота користувача разом |
| 7 | **RevoGrid** (MIT, активний; Glide Data Grid — запасний) | AG Grid Community не має fill handle і range selection; Glide Data Grid не публікувався ~3 роки |
| 8 | **Серверний рушій формул — авторитетний** | знімає ризик розбіжності двох рушіїв |
| 9 | **Два провайдери автентифікації, одна ідентичність** | |
| 10 | **Сервісний обліковий запис до PI**, не Kerberos-делегування | локальні користувачі не мають Windows-ідентичності |
| 11 | **Ролі — дані, не enum** | |
| 12 | **Архівація закритих років** у `arc.*` | робочі таблиці не ростуть з роками |
| 13 | **Два транспорти до PI AF** | RTQP не потребує Kerberos і не вміє писати; Web API вміє писати |
| 14 | **Без Redis**: `IMemoryCache` + `IDistributedCache` на SQL; Garnet (MIT), якщо знадобиться кеш-сервер | Redis — AGPLv3/SSPL, суперечить політиці |
| 15 | **`Microsoft.AspNetCore.OpenApi` + Scalar**, не Swashbuckle | нативно у .NET 10 |
| 16 | **JMeter** для навантажувальних тестів, не NBomber | NBomber v5+ комерційний для організацій |
| 17 | **`SqlBulkCopy`**, не EFCore.BulkExtensions | cFOSS — платна від $1M виручки |
| 18 | **`Af*`/`Legacy*` — у `ext.Legacy*Mapping`, не в `cfg.*`** | ядро не знає про AF і Excel; перевіряється архітектурним тестом |
| 19 | **Локалізація — JSON-колонка `…L10n`**, не `NameEn/Ru/Kz` | нова мова = запис, не міграція |
| 20 | **Презентаційний шар опублікованої версії патчиться «на льоту»**; структурний — заморожений | [10](10-schema-evolution.md) §2a |
| 21 | Формули з посиланнями `[Period:-1]`, `[PrevProject]…` | перенесення залишків минулого року — не вручну |

### ❓ Відкрито

| # | Питання | Хто | Коли |
|---|---------|-----|------|
| 1 | Модель зберігання комірок | архітектор | за прототипом М0 |
| 2 | Чи є PI SQL DAS (RTQP) у контурі | PI-адмін | М0 |
| 3 | Чи дозволена LGPL (Hangfire) | ІБ / юристи | М0 |
| 4 | Гранулярність затвердження | бізнес | до М7 |
| 5 | Політика паролів, 2FA | ІБ | до М3 |
| 6 | SMTP у контурі | інфраструктура | до М3 |
| 7 | Скільки документів на рік реально | бізнес | М0 — впливає на sizing |
| 8 | Термін зберігання архіву | бізнес + ІБ | до М10 |
| 9 | Доля legacy-аркушів (`9c`, `HSE_373/400/401_Air`) | бізнес | до М1 |

### ⚠ Може змінитися після аналізу бекенду

Ці питання стосуються системи, яку перероблятимуть окремо. Відповіді можуть
змінитися — тому адаптер PI AF навмисно зроблений змінним.

| # | Питання | Статус 2026-09-03 |
|---|---------|-------------------|
| 1 | Довідники: master в AF чи в ECR | ✅ ECR, поетапно через `SourceKind` (D-49) |
| 2 | Чи потрібна зворотна публікація в AF узагалі | ✅ **Ні, взагалі** (D-44) |
| 3 | Склад даних, що збираються з AF | ✅ після переходу — лише сирі виміри і FLERT (ER-I-02) |
| 4 | Як саме лягають розрахункові модулі емісій у схему `calc` | ✅ методології як дані, драбина 1/2/3, `calc.*` — `tz/05`, B13 |

> **Жодне з них не зачіпає ядро** — лише обсяг робіт по адаптеру.
> Це і було метою винесення PI AF за `IExternalDataSource`.
