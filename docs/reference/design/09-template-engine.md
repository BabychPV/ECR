# Ядро системи: проєкт -> шаблон -> документ

Це центральний механізм. Він **не залежить від ECR і не залежить від PI AF** —
це універсальний інструмент побудови структурованої звітності.

---

## 1. Три рівні

```
┌──────────────────────────────────────────────────────────────────────────┐
│ PROJECT  «2026»                                                          │
│  рік, період дії, календар місяців, стани періодів, права                │
│  прив'язаний до однієї TemplateVersion                                   │
└────────────────────────────┬─────────────────────────────────────────────┘
                             │
┌────────────────────────────▼─────────────────────────────────────────────┐
│ TEMPLATE VERSION  «RDS Land 1.1»   [Published — структурно незмінна]     │
│                                                                          │
│  StyleDef[]          палітра, іменовані стилі, умовне форматування        │
│  SheetDef[]          аркуші                                              │
│    └ TableDef[]      таблиці на аркуші                                   │
│        ├ ColumnDef[] колонки (тип, формат, ширина, стиль, довідник)      │
│        ├ RowDef[]    рядки (для фіксованих таблиць: GROUP/ITEM/…)        │
│        ├ FormulaDef[]                                                    │
│        └ ValidationRule[]                                                │
└────────────────────────────┬─────────────────────────────────────────────┘
                             │ Generate(набір аркушів/таблиць)
┌────────────────────────────▼─────────────────────────────────────────────┐
│ DOCUMENT  «FileNumber = ABC-123»                                         │
│  назавжди прив'язаний до TemplateVersion, на якій створений              │
│                                                                          │
│  DocumentSheet[]     які аркуші увімкнені саме в цьому документі         │
│    └ TableInstance[] екземпляр таблиці × період                          │
│        └ CellValue[] значення                                            │
└──────────────────────────────────────────────────────────────────────────┘
```

---

## 2. Життєвий цикл шаблону

```
   Create ─────► [Draft] ──── редагування структури і стилів ────┐
                    │                                             │
                    │ Publish (валідація цілісності)              │
                    ▼                                             │
                [Published] ───── документи створюються ─────────►│
                    │                                             │
                    │ CloneFrom                                   │
                    ▼                                             │
              [Draft v+1] ─────────────────────────────────────────┘
                    │
                    │ Publish
                    ▼
                [Published v+1]        [Deprecated v]
                                        (нові документи не створюються,
                                         існуючі продовжують працювати)
```

### Правила

| Стан | Редагування структури | Створення документів | Видалення версії |
|------|----------------------|---------------------|------------------|
| `Draft` | ✅ вільно | ❌ | ✅ якщо немає документів |
| `Published` | ❌ структура; ✅ **презентаційний шар** — підписи, стилі, порядок, `Warning`-правила ([10](10-schema-evolution.md) §2a) | ✅ | ❌ |
| `Deprecated` | ❌ | ❌ | ❌ |

**`CloneFrom(sourceVersionId)`** створює повну глибоку копію:
`SheetDef` -> `TableDef` -> `ColumnDef` / `RowDef` / `FormulaDef` /
`ValidationRule` / `StyleDef` / `ConditionalFormatDef` / `TableRelationDef` /
`SheetGroupRule` / `PeriodAccessRuleDef` / `RegistryDef` зі `Scope = Project`
(+ їхні `ext.Legacy*Mapping`, якими володіє адаптер).
Зберігається `SourceVersionId` — щоб `Diff` міг показати, що змінилося
відносно попередньої версії.

> Це прямо реалізує вимогу: *«шаблон має бути версійним або просто створюється
> новий на основі попереднього»* — обидва варіанти є однією операцією.

---

## 3. Генерація документа

`DocumentGenerator.Generate(projectId, templateVersionId, selection, header)`

### Крок 1. Вибір складу

```
selection = {
  Sheets: [ "Water", "Waste", "Utility" ],
  Tables: [ ... ]          // опційно: не весь аркуш, а окремі таблиці
}
```

Аналог чинного `UserSelection.frm`, але:
* вибір зберігається як **дані** (`doc.DocumentSheet`), а не як фізична копія аркушів;
* можна вмикати/вимикати аркуш **після** створення документа (з перевіркою впливу).

### Крок 2. Правила складу (`SheetGroupRule`)

Декларативні правила зв'язності замість зашитого `ExpandLinkedSheetGroups`:

| Тип правила | Приклад |
|-------------|---------|
| `RequiresAll` | обрано будь-який з `7. / 7a / 7b / 7.0` -> вмикаються всі чотири (формули посилаються одна на одну) |
| `RequiresOne` | обрано `9a` -> обов'язково `9` |
| `Excludes` | `Land`-аркуші несумісні з `Offshore` |
| `AlwaysInclude` | `Contract` завжди |

Правила задаються в конфігураторі, перевіряються при генерації і при зміні складу.

### Крок 3. Матеріалізація

```
для кожного увімкненого SheetDef:
    для кожного TableDef:
        для кожного Period у календарі проєкту:
            створити TableInstance
            якщо RowMode = Fixed:
                створити CellValue для кожного (RowDef × ColumnDef)
                із DefaultValue
```

**Оптимізація:** для `RowMode = Fixed` порожні комірки **не матеріалізуються** —
створюються лише при першому введенні (lazy). Інакше 108 млн порожніх рядків на старті.
Читання повертає `DefaultValue` для відсутніх комірок.

### Крок 4. Заголовок документа

Поля шапки (`FileNumber`, контракт, регіон, підрядник, дозвіл…) — це теж
`ColumnDef` спеціальної таблиці `__Header`, а не окремі жорсткі поля.
Так шапка теж конфігурується без коду.

Для швидкого пошуку поле з `ColumnDef.IsBusinessKey` потрапляє в
`doc.Document.BusinessKey`, а поля з `IsIndexed` — у `doc.DocumentIndexValue`;
обидва з індексами. Жодного ECR-специфічного поля в `doc.Document` немає.

---

## 4. Модель стилів

### Принцип: іменовані стилі, не інлайн

```
StyleDef "TableHeader"   { Bold, BgColor #DCE6F1, Border: thin all, Align: center }
StyleDef "GroupRow"      { Bold, BgColor #F2F2F2 }
StyleDef "ItemRow"       { Normal }
StyleDef "ReadOnlyCell"  { BgColor #EFEFEF, Italic }
StyleDef "NumericMonth"  { Format "#,##0.00", Align: right }
```

Далі `TableDef.HeaderStyleId = "TableHeader"`, `RowDef(RowType=Group).StyleId = "GroupRow"` тощо.

**Чому так:**
* один опис стилів -> і веб-грід, і `.xlsx`-експорт;
* зміна корпоративної палітри = зміна кількох `StyleDef`, а не тисяч комірок;
* розмір метаданих залишається малим.

### Умовне форматування

`ConditionalFormatDef`:

| Поле | Приклад |
|------|---------|
| `Scope` | `Column` / `Row` / `Table` |
| `TargetId` | `ColumnDef.Id` |
| `Expression` | `[Value] > [Column:Limit]` |
| `StyleId` | `"Warning"` |
| `Priority` | `10` |

Обчислюється на клієнті (миттєво) і при експорті переноситься в
нативне умовне форматування Excel, де це можливо.

### Що переноситься з чинного шаблону

У поточному `.xlsm` ~900 conditional formats і мільйони стилізованих комірок
(`xl/styles.xml` — 1.3 МБ). Bootstrap-імпортер (ФВ-2.10) має:
1. зібрати унікальні комбінації форматів;
2. згорнути їх у ~30–50 іменованих `StyleDef`;
3. прив'язати до `TableDef` / `ColumnDef` / `RowDef`.

---

## 5. Зв'язування таблиць ⭐

Таблиці шаблону можуть бути пов'язані між собою. **Зв'язки — опційні:**
шаблон може не мати жодного, і тоді таблиці повністю незалежні.

Описуються через `cfg.TableRelationDef` — див. [07-data-model.md](07-data-model.md).

### 5.1 Типи зв'язків

| Тип | Що робить | Приклад із ECR | Приклад іншого шаблону |
|-----|-----------|----------------|------------------------|
| **`MetadataSource`** | одна таблиця постачає поля **всім** іншим | `2. Contract` -> усі 18 аркушів | «Шапка бюджету» -> усі розділи; або **нічого** |
| `Lookup` | колонка посилається на рядок іншої таблиці | Permit number -> довідник дозволів | Код статті -> план рахунків |
| **`Rollup`** | таблиця агрегує іншу | `7.0 Water consolidation` = суми з `7. Water Report` | «Разом по розділу» |
| `Mirror` | значення дзеркалиться | `7a`/`7b` беруть із `7.` | — |
| `ParentChild` | ієрархія рядків між таблицями | GROUP -> ITEM | — |
| `Constraint` | крос-табличне правило без переносу даних | «сума A == підсумок B» | «баланс сходиться» |

### 5.2 `MetadataSource` — випадок аркуша «Контракт»

Це найважливіший для чинного шаблону тип. Зараз він реалізований жорстко:
`Action.GetContractSheetAttributes()` читає 13 фіксованих комірок `B6:B18`
і **дописує їх до кожного** запису, що йде в PI AF.

У новій системі:

```
cfg.TableRelationDef {
  Code           = "ContractMetadata"
  RelationKind   = MetadataSource
  SourceTableDef = "__Header"     // таблиця аркуша "2. Contract"
  TargetTableDef = NULL           // NULL = усі таблиці документа
  SyncMode       = Reference      // не копіюємо — посилаємось
  OnSourceChange = Warn           // зміна FileNumber → amendment flow
  IsRequired     = true           // без заповненої шапки Submit неможливий
}
```

**`SyncMode` визначає фізику:**

| Режим | Як зберігається | Коли застосовувати |
|-------|-----------------|--------------------|
| `Reference` | значення живе **в одному місці** (`__Header`), решта таблиць посилаються | **за замовчуванням** — немає дублювання, немає розсинхрону |
| `Copy` | значення матеріалізується в кожну таблицю на момент прив'язки | коли потрібен «зліпок» метаданих на момент затвердження |
| `Computed` | обчислюється формулою | rollup-и |

> Для ECR правильний вибір — `Reference`. Дублювання 13 полів у 90 таблиць ×
> 12 місяців (як зараз у PI AF) — це ~14 000 копій одного й того самого
> значення на документ.
>
> `Copy` знадобиться хіба для **утиліти валідації міграції** (відтворення `;`-рядка,
> D-44 — публікації в AF немає), де формат вимагає плоского запису — там метадані підмішуються
> на льоту при формуванні `;`-рядка, у самій БД не дублюються.

### 5.3 Доступ до метаданих у формулах і правилах

```
[__Header].[Contract].[PermitNumber]          посилання на поле шапки
[__Header].[Contract].[ReportYear]

// у формулі
IF([__Header].[Contract].[OnshoreOffshore] = "Offshore"; [Value] * 1.15; [Value])

// у правилі валідації
[__Header].[Contract].[PermitNumber] <> ""
```

Якщо в шаблоні немає `MetadataSource`-зв'язку, такі посилання просто
не резолвяться і відхиляються при публікації версії — тобто **шаблон без
метаданих валідний**, він просто їх не використовує.

### 5.4 `OnSourceChange` — що робити при зміні джерела

| Значення | Поведінка | Приклад |
|----------|-----------|---------|
| `Propagate` | тихо перерахувати залежні | rollup `7.0` після зміни `7.` |
| `Warn` | показати попередження зі списком зачепленого | зміна `FileNumber` (amendment flow) |
| `Block` | заборонити зміну, якщо є залежні затверджені дані | зміна дозволу в затвердженому документі |
| `Ignore` | нічого не робити | довідкові зв'язки |

Це замінює:
* `modB16AmendmentFlow` (764 рядки VBA) -> `OnSourceChange = Warn`;
* автовиклик `SaveWaterConsolidationForMonth` -> `OnSourceChange = Propagate`;
* `ExpandLinkedSheetGroups` -> `SheetGroupRule` + наявність `Mirror`/`Rollup` зв'язків.

### 5.5 Граф зв'язків і перевірка при публікації

При `Publish` версії будується орієнтований граф `TableRelationDef` + `FormulaDef`:

1. **Цикли заборонені** — `A -> B -> A` відхиляється;
2. **Топологічний порядок** зберігається в `FormulaDef.EvaluationOrder` —
   визначає послідовність перерахунку;
3. **Недосяжні посилання** (на видалену/неіснуючу таблицю) відхиляються;
4. **Обов'язкові зв'язки** (`IsRequired`) перевіряються при `Submit` документа.

---

## 6. Універсальність: що робить ядро незалежним

| Аспект | Як досягається |
|--------|----------------|
| Немає «екологічних» сутностей у ядрі | `Permit`, `WaterBody`, `Substance` — це записи `cfg.RegistryDef` / `dic.RegistryEntry` (конфігурація), а не таблиці домену |
| Немає AF/Excel у ядрі | `Af*`/`Legacy*` — лише в `ext.Legacy*Mapping`; `doc.Project` не має полів AF |
| Немає фіксованих мов | локалізація — JSON `…L10n` + `sys.Language` |
| Немає PI AF у ядрі | тільки порт `IExternalDataSource`; адаптер — окрема збірка |
| Немає зашитих аркушів | усе через `cfg.*` |
| Немає зашитих правил | `ValidationRule` з виразами |
| Немає зашитих періодів | календар періодів налаштовується на проєкті (місяць / квартал / рік / довільний) |
| Немає зашитої шапки | таблиця `__Header` |

**Перевірка на універсальність:** система має дозволяти створити проєкт
«Бюджет 2027» із зовсім іншими аркушами і таблицями — **без жодного рядка нового коду**.
Це критерій приймання Етапу 1.

---

## 6a. Правила доступу до періодів — у шаблоні ⭐

Чинна кнопка **«Protect sheets»** зашита в код: `Protection.ToggleProtection`
знає номери рядків-заголовків для кожного аркуша (`hdrRows = Array(3,4,14,15,…)`),
знає, що `7. Water Report` має місяці в колонках I…T, і окремо перевіряє
вікна дії дозволів. Кожна зміна макета — правка коду в кількох місцях.

У новій системі це **конфігурація шаблону** (`cfg.PeriodAccessRuleDef`),
що задається у вебі при створенні шаблону.

### Типи правил

| `RuleKind` | Що робить | Що замінює |
|------------|-----------|------------|
| `AlwaysReadOnly` | завжди тільки читання | `Range("A6:E90").Locked = True` у `Sheet_Config.Init*` |
| `HeaderRows` | рядки-заголовки завжди заблоковані | масиви номерів рядків у `Protection.bas` |
| `EditablePeriodOnly` | редагується лише період у стані `Open`/`Grace` | ядро `ToggleProtection` |
| `RelativeWindow` | вікно «поточний період ± N» | «попередній місяць за cutoff day» |
| `SourceWindow` | вікно береться з довідника (дати дії дозволу) | `ApplyPermitMonthLocks` |
| `Expression` | довільна умова над значеннями рядка | нове — раніше вимагало коду |

Кожне правило має `Scope` (`Sheet`/`Table`/`Column`/`Row`), `Priority`
і `IsDeny`. Конкретніше перекриває загальніше; `IsDeny` виграє.

### Приклад: `7. Water Report`

```
Rule 1  Scope=Sheet   Target="7. Water Report"   Kind=EditablePeriodOnly
Rule 2  Scope=Row     RowType=Header             Kind=HeaderRows
Rule 3  Scope=Column  Target="A..E"              Kind=AlwaysReadOnly
Rule 4  Scope=Row     RowType=Group              Kind=AlwaysReadOnly  ← формульні підсумки
Rule 5  Scope=Row     RowType=Item               Kind=SourceWindow
        Parameters = { source: "Registry:Permit",
                       keyColumn: "PermitId",
                       from: "StartDate", to: "ActualEndDate",
                       onOutOfWindow: "LockAndWarn" }
```

### `onOutOfWindow`

| Значення | Поведінка |
|----------|-----------|
| `Lock` | заблокувати мовчки |
| `LockAndWarn` | заблокувати з попередженням |
| `LockAndClear` | заблокувати і **очистити** — поточна поведінка Excel |
| `Warn` | лише попередити, не блокувати |

> ⚠ `LockAndClear` знищує дані. Зараз це поведінка за замовчуванням.
> У новій системі має лишитися **явним вибором із підтвердженням**,
> а не типовою поведінкою.

### Типова політика періодів у шаблоні

Шаблон містить `PeriodPolicy` за замовчуванням (`OpenOffsetDays` /
`GraceOffsetDays` / `HardCloseOffsetDays`). При створенні проєкту вона
підставляється; проєкт може перевизначити.

### Попередній перегляд

У конструкторі — матриця `період × аркуш` із кольорами
«редагується / тільки читання / приховано». Адміністратор бачить результат
правил **до публікації версії**, а не з'ясовує його на живих даних.

### Пароль прибирається

`1qazxcde3` і вся механіка `frmPassword` зникають. Хто що може —
визначає роль ([11-security-model.md](11-security-model.md)),
а коли — правила періодів і стан `Period`.

---

## 7. Періоди

`Project.PeriodKind`: `Monthly` (12) / `Quarterly` (4) / `Yearly` (1) / `Custom`.

Для ECR — `Monthly`. Але ядро не зашиває 12 місяців.

`TableDef.LayoutKind` визначає, як періоди лягають у таблицю:

| `LayoutKind` | Розкладка | Приклад із чинного шаблону |
|--------------|-----------|----------------------------|
| `MonthsInColumns` | період = колонка; один `TableInstance` на всі періоди | більшість аркушів |
| `MonthsInRows` | період = блок рядків | `8. Waste Report` (12 блоків по 33 рядки) |
| `PerPeriodInstance` | окремий `TableInstance` на кожен період | найчистіший варіант |
| `Static` | без періодів | шапка, довідкові таблиці |

> **Рекомендація:** для нових шаблонів використовувати `PerPeriodInstance` —
> це найпростіша і найшвидша модель. `MonthsInColumns` / `MonthsInRows`
> підтримуються **лише для сумісності** з чинним шаблоном ECR.

---

## 8. API ядра (ескіз)

```
POST   /api/v1/projects                                 створити проєкт
POST   /api/v1/projects/{id}/periods/{p}/reopen         відкрити період (PeriodAdministrator)
PUT    /api/v1/projects/{id}/period-policy              offsets проєкту

GET    /api/v1/templates
POST   /api/v1/templates/{id}/versions                  нова версія (Draft)
POST   /api/v1/templates/{id}/versions/{v}/clone-from/{src}
POST   /api/v1/templates/{id}/versions/{v}/publish
GET    /api/v1/templates/{id}/versions/{a}/diff/{b}

POST   /api/v1/versions/{v}/sheets                      CRUD структури
POST   /api/v1/versions/{v}/tables
POST   /api/v1/versions/{v}/columns
POST   /api/v1/versions/{v}/changes/analyze-impact      ← звіт про вплив (будь-яка зміна)
PATCH  /api/v1/versions/{v}/presentation               ← презентаційні правки Published-версії
GET    /api/v1/versions/{v}/styles
POST   /api/v1/versions/{v}/relations                   зв'язки таблиць
POST   /api/v1/versions/{v}/period-rules                правила доступу до періодів
GET    /api/v1/versions/{v}/access-matrix?period=…      матриця період × аркуш

GET    /api/v1/registries                               довідники
POST   /api/v1/registries/{r}/entries
GET    /api/v1/registries/{r}/entries?asOf=2026-09-30   темпоральний резолвінг
GET    /api/v1/registries/{r}/entries/{e}/usages        «де використовується»

POST   /api/v1/documents                                згенерувати документ
GET    /api/v1/documents/{id}/sheets/{s}/tables/{t}?period=9
PATCH  /api/v1/documents/{id}/cells                     batch-запис комірок (If-Match)
POST   /api/v1/documents/{id}/recalculate
POST   /api/v1/documents/{id}/validate
POST   /api/v1/documents/{id}/submit
POST   /api/v1/documents/{id}/approve
POST   /api/v1/documents/{id}/migrate-to/{version}      ← перехід на нову версію (dryRun)
GET    /api/v1/documents/{id}/export?format=xlsx        → 202 + jobId
POST   /api/v1/documents/{id}/import                    (preview -> apply)
GET    /api/v1/jobs/{id}                                прогрес довгих операцій
```

Конвенції (версіонування, `ProblemDetails`, `ETag`/`If-Match`, cursor-пагінація,
202 + `jobId`) — [13-backend-assignment.md](13-backend-assignment.md) §7.

---

## 9. Що це дає порівняно з Excel

| Excel зараз | Нова система |
|-------------|--------------|
| HSE-файл = фізична копія книги + копія всього VBA | документ = рядки в БД; логіка одна на всіх |
| Патч логіки треба розкотити по N файлах вручну | виправлення застосовується миттєво до всіх |
| Додати колонку = правки в 5 місцях коду + реліз | `INSERT` через UI за 10 секунд |
| Немає версійності шаблону | immutable versions + diff + міграція |
| Стилі дублюються в кожній комірці кожного файлу | ~40 іменованих стилів на весь шаблон |
| Один файл = один користувач | одночасна робота з оптимістичним блокуванням |
| Структура жорстко прив'язана до PI AF | ядро не знає про AF узагалі |
