# Модель даних (SQL Server + EF Core)

Чернетка схеми. Позначення: `PK` — первинний ключ, `FK` — зовнішній, `IX` — індекс,
`UQ` — унікальний.

---

## 1. Схеми БД

| Схема | Призначення |
|-------|-------------|
| `cfg` | Метадані шаблонів: структура + **стилі** (конфігуратор) |
| `dic` | **Довідники/реєстри** — універсальний механізм (визначення в `cfg.Registry*`) |
| **`uom`** | **Одиниці вимірювання і конверсії** — метадані структури, не дані комірок |
| `doc` | Документи та їхні дані |
| `ext` | **Сирі дані із зовнішніх джерел** (PI AF та ін.) — без інтерпретації |
| `wf` | Робочий процес, статуси, погодження |
| `sec` | Користувачі, ролі, області доступу |
| `aud` | Аудит (append-only) + журнал змін структури |
| `itg` | Журнали інтеграції та збору даних |
| **`arc`** | **Архів закритих років** — дзеркало `doc.*` на columnstore |
| `calc` | Методології, версії, скрипти, результати і кроки розрахунку — **перша черга** (D-54); таблиці — B13 §5, B19 §8 |

> **Фізична схема фіксована.** Жодного `CREATE TABLE` / `ALTER TABLE` під час
> роботи системи — зміна шаблону це `INSERT`/`UPDATE` у `cfg.*`.
> Обґрунтування — [10-schema-evolution.md](10-schema-evolution.md) §1.

---

## 2. `cfg` — Конфігуратор

> **Конвенція локалізації.** Усі поля, позначені нижче як `NameEn/NameRu/NameKz`,
> `HeaderEn/Ru/Kz`, `LabelEn/Ru/Kz`, `MessageEn/Ru/Kz`, фізично реалізуються
> **однією колонкою** `NameL10n` / `HeaderL10n` / `LabelL10n` / `MessageL10n`
> типу `nvarchar(max)` з JSON `{"en":"…","ru":"…","kz":"…"}` + реєстр `sys.Language`.
> Додати мову = додати запис у `sys.Language`, а не колонку в 20 таблицях.
> Це прямий наслідок принципу «структура = дані». Для пошуку по назвах —
> computed-колонка `NameDefault` (мова за замовчуванням) з індексом.

> **Конвенція «автор дії».** Автором будь-якої дії є **`…ByUserId int`** →
> `sec.User(Id)`, а не SID Windows: у локальних облікових записів SID немає,
> а модель вимагає рівноправності провайдерів (`D-37`, ФВ-6.3). Пошуку по
> `Sid` у цьому документі бути не повинно.

> **Конвенція «ядро не знає про AF і Excel».** У `cfg.*` **немає** колонок
> `Af*` і `Legacy*`. Мапінг на PI AF і на чинний Excel-шаблон живе у схемі `ext`
> (§8.1) і належить адаптеру та bootstrap-імпортеру. Це перевіряється
> архітектурним тестом.

### cfg.Template
| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` | `int` PK | |
| `Code` | `nvarchar(50)` UQ | напр. `RDS_LAND` |
| `NameEn/NameRu/NameKz` | `nvarchar(200)` | |
| `TagsJson` | `nvarchar(1000)` NULL | довільні теги, напр. `["ECR","Land"]` — замість ECR-специфічного `LocationKind` |
| `IsActive` | `bit` | |

### cfg.TemplateVersion
| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` | `int` PK | |
| `TemplateId` | `int` FK | |
| `Version` | `nvarchar(20)` | `1.0.4.0` |
| `Status` | `tinyint` | `Draft`=0, `Published`=1, `Deprecated`=2 |
| `ClonedFromVersionId` | `int` FK NULL | для `Diff` із попередньою версією |
| **`PresentationRevision`** | `int` | інкремент при кожній презентаційній правці опублікованої версії; входить у ключ кешу |
| `PublishedAt`, `PublishedBy` | | |
| `SourceWorkbookHash` | `varbinary(32)` | для bootstrap-імпорту |

**UQ:** `(TemplateId, Version)`

> Після `Published` — **структурно immutable**. Тригер `INSTEAD OF UPDATE/DELETE`
> на всіх таблицях `cfg.*` пропускає лише поля **презентаційного шару**
> (підписи, стилі, `Ordinal`, `DisplayFormat`, `Info`/`Warning`-правила) і
> відхиляє решту. Структурні зміни -> `CloneFrom` -> нова версія
> ([10](10-schema-evolution.md) §2a).

### cfg.SheetDef
| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` | `int` PK | |
| `TemplateVersionId` | `int` FK | |
| `Code` | `nvarchar(100)` | `WaterReport_07` |
| `NameL10n` | `nvarchar(max)` | JSON; `LegacyName` (`7. Water Report`) — в `ext.LegacySheetMapping` |
| `Ordinal` | `int` | |
| `SheetGroup` | `nvarchar(50)` | `Water` — тег для `cfg.SheetGroupRule` (заміна `ExpandLinkedSheetGroups`) |
| `IsMandatory` | `bit` | |
| `IsVisible` | `bit` | |

**UQ:** `(TemplateVersionId, Code)`

### cfg.TableDef
| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` | `int` PK | |
| `SheetDefId` | `int` FK | |
| `Code` | `nvarchar(100)` | |
| `NameEn/NameRu/NameKz` | `nvarchar(300)` | |
| `Ordinal` | `int` | |
| `LayoutKind` | `tinyint` | `MonthsInColumns`=0, `MonthsInRows`=1, `Static`=2, **`PerPeriodInstance`=3** (рекомендований для нових шаблонів; 0 і 1 — для сумісності з чинним Excel) |
| `RowMode` | `tinyint` | `Fixed`=0, `Dynamic`=1, `Mixed`=2 |
| `MaxDynamicRows` | `int` NULL | |
| `HeaderStyleId` | `int` FK NULL | -> `cfg.StyleDef` |

**UQ:** `(SheetDefId, Code)`

> Мапінг на PI AF (`EventFrameTemplate`, `ElementTemplate`, `ElementName`)
> і на Excel (`TemplateRow`, `RowOffsetBase`) — **не тут**, а в
> `ext.LegacyTableMapping` (§8.1). Ядро не знає про AF.

> Ця таблиця повністю замінює словники з `Configuration.bas`.

### cfg.ColumnDef
| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` | `int` PK | |
| `TableDefId` | `int` FK | |
| `Code` | `nvarchar(100)` | |
| `HeaderEn/HeaderRu/HeaderKz` | `nvarchar(300)` | |
| `Ordinal` | `int` | порядок відображення |
| `DataType` | `tinyint` | `String`/`Int`/`Decimal`/`Bool`/`Date`/`Lookup`/`Formula` |
| `Precision`, `Scale` | `tinyint` NULL | для `Decimal` |
| `IsReadOnly`, `IsRequired`, `IsHidden` | `bit` | |
| `IsMonthColumn` | `bit` | для `LayoutKind = MonthsInColumns` |
| `MonthNumber` | `tinyint` NULL | 1..12, якщо `IsMonthColumn` |
| `DefaultValue` | `nvarchar(400)` NULL | |
| `DisplayFormat` | `nvarchar(50)` NULL | `#,##0.00` |
| `LookupRegistryDefId` | `int` FK NULL | -> `cfg.RegistryDef` (довідник для листбокса) |
| `LookupFilter` | `nvarchar(500)` NULL | звуження списку, напр. `Permit_Kind = 'Water'` |
| `CascadeFromColumnId` | `int` FK NULL | каскадні dropdown (permit -> water body) |
| `IsBusinessKey` | `bit` | значення потрапляє в `doc.Document.BusinessKey` |
| `IsScopeField` | `bit` | поле бере участь в областях доступу (`sec.RoleAssignment.ScopeJson`) |
| `IsIndexed` | `bit` | дублюється в `doc.DocumentIndexValue` для швидких фільтрів |

**UQ:** `(TableDefId, Code)`

> `AfAttributeName`, **`LegacyFieldIndex`** (позиція в `;`-рядку), `LegacyExcelColumn` —
> у `ext.LegacyColumnMapping` (§8.1).

### cfg.RowDef
Для `RowMode = Fixed` (напр. `7. Water Report` з GROUP/ITEM/BALANCE/NOTE).

| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` | `int` PK | |
| `TableDefId` | `int` FK | |
| **`RowKey`** | `nvarchar(100)` | **стабільна бізнес-ідентичність** рядка: `7001001` |
| `Ordinal` | `int` | лише порядок відображення, **не ідентичність** |
| `LabelEn/LabelRu/LabelKz` | `nvarchar(500)` | |
| `RowType` | `tinyint` | `Group`/`Item`/`Balance`/`Note`/`Header` |
| `ParentRowDefId` | `int` FK NULL | GROUP -> ITEM |
| `IsReadOnly` | `bit` | |
| `StyleId` | `int` FK NULL | -> `cfg.StyleDef` |
| `IsDeleted`, `DeletedAt`, `DeletedByUserId` | | soft delete |

> `AfAttributeName` (`Attribute_0010`), `ExcelRow` — у `ext.LegacyRowMapping` (§8.1).

**UQ:** `(TableDefId, RowKey)`

> ⚠ **Ключова відмінність від чинного рішення.** Зараз ідентичність рядка —
> **позиційна**: `Attribute_0010` = «перший рядок діапазону». Вставили рядок в Excel —
> уся нумерація попливла, старі дані стали означати інше.
>
> У новій моделі ідентичність — **`RowKey`** (стабільний код), а `Ordinal` відповідає
> лише за порядок на екрані. Переставили рядки місцями — дані залишилися на місці.
> Це і є та «чітка ідентифікація», якої зараз немає.

### cfg.FormulaDef
| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` | `int` PK | |
| `Scope` | `tinyint` | `Column`/`Row`/`Cell` |
| `ColumnDefId`, `RowDefId` | `int` FK NULL | |
| `Expression` | `nvarchar(2000)` | `SUM([Water_07].[Main].[7001001:7001005].[{Month}])` |
| `EvaluationOrder` | `int` | топологічна сортовка з графа залежностей; обчислюється при `Publish` |
| `IsCrossSheet` | `bit` | |
| `RefMode` | `tinyint` | для посилань на інші періоди/документи (`[Period:-1]`, `[PrevProject]…`): `Snapshot`=0 (типово — затверджене минуле не «пливе») / `Live`=1 |

### cfg.ValidationRule
| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` | `int` PK | |
| `Scope` | `tinyint` | `Cell`/`Row`/`Table`/`Document` |
| `TargetId` | `int` | Id відповідного Def |
| `RuleType` | `tinyint` | `Range`/`Required`/`Regex`/`List`/`Expression`/`Custom` |
| `Parameters` | `nvarchar(max)` | JSON: `{"min":0,"max":744}` |
| `Expression` | `nvarchar(2000)` NULL | |
| `Severity` | `tinyint` | `Error`/`Warning`/`Info` |
| `MessageEn/MessageRu/MessageKz` | `nvarchar(500)` | |
| `IsActive` | `bit` | |

### cfg.StyleDef — іменовані стилі
| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` | `int` PK | |
| `TemplateVersionId` | `int` FK | |
| `Code` | `nvarchar(50)` | `TableHeader`, `GroupRow`, `ReadOnlyCell` |
| `FontFamily`, `FontSize` | | |
| `IsBold`, `IsItalic` | `bit` | |
| `ForeColor`, `BackColor` | `nvarchar(9)` | `#DCE6F1` |
| `HorizontalAlign`, `VerticalAlign` | `tinyint` | |
| `BorderJson` | `nvarchar(400)` | сторони, товщина, колір |
| `NumberFormat` | `nvarchar(50)` | `#,##0.00` |
| `WrapText` | `bit` | |

**UQ:** `(TemplateVersionId, Code)`
> Стилі описуються один раз і застосовуються і в вебі, і в `.xlsx`-експорті.

### cfg.ConditionalFormatDef
`Id`, `Scope` (`Column`/`Row`/`Table`), `TargetId`, `Expression` `nvarchar(2000)`,
`StyleId` FK, `Priority` `int`, `StopIfTrue` `bit`

### cfg.TableRelationDef ⭐ — зв'язки між таблицями
Механізм, що описує, як таблиці шаблону пов'язані між собою.
**Опційний:** шаблон може не мати жодного запису — таблиці будуть незалежними.

| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` | `int` PK | |
| `TemplateVersionId` | `int` FK | |
| `Code` | `nvarchar(100)` | |
| `RelationKind` | `tinyint` | див. таблицю нижче |
| `SourceTableDefId` | `int` FK | таблиця-джерело |
| `TargetTableDefId` | `int` FK NULL | `NULL` = усі таблиці версії |
| `SourceColumnDefId` | `int` FK NULL | |
| `TargetColumnDefId` | `int` FK NULL | |
| `Cardinality` | `tinyint` | `OneToOne`/`OneToMany`/`ManyToOne` |
| `SyncMode` | `tinyint` | `Copy` (матеріалізувати) / `Reference` (посилання) / `Computed` |
| `OnSourceChange` | `tinyint` | `Propagate` / `Warn` / `Block` / `Ignore` |
| `FilterExpression` | `nvarchar(1000)` NULL | |
| `IsRequired` | `bit` | |

**Типи зв'язків (`RelationKind`):**

| Значення | Назва | Що робить | Приклад із чинного шаблону |
|----------|-------|-----------|---------------------------|
| 0 | **`MetadataSource`** | Одна таблиця постачає метадані **всім** іншим таблицям документа | `2. Contract` -> усі 18 звітних аркушів (13 полів: `File_Number`, `Contract_Number`, `Permit_Number`…) |
| 1 | `Lookup` | Колонка A посилається на рядок таблиці B | Permit number -> рядок довідника дозволів |
| 2 | **`Rollup`** | Таблиця B агрегує дані таблиці A | `7.0 Water consolidation` = суми з `7. Water Report` |
| 3 | `Mirror` | Значення B дзеркалить значення A | `7a`/`7b` беруть значення з `7. Water Report` |
| 4 | `ParentChild` | Ієрархія рядків між таблицями | GROUP-таблиця -> ITEM-таблиця |
| 5 | `Constraint` | Крос-табличне правило без переносу даних | «сума в таблиці A має дорівнювати підсумку в B» |

**Приклад для чинного шаблону ECR:**

```
TableRelationDef {
  Code            = "ContractMetadata",
  RelationKind    = MetadataSource,
  SourceTableDef  = "__Header" (аркуш "2. Contract"),
  TargetTableDef  = NULL,              // усі таблиці
  SyncMode        = Reference,         // не копіюємо, посилаємось
  OnSourceChange  = Warn,              // зміна FileNumber -> попередження (amendment flow)
  IsRequired      = true
}

TableRelationDef {
  Code            = "WaterConsolidation",
  RelationKind    = Rollup,
  SourceTableDef  = "WaterReport.Main",
  TargetTableDef  = "WaterConsolidation.Main",
  SyncMode        = Computed,          // перераховується формулами
  OnSourceChange  = Propagate          // зміна в 7. → перерахунок 7.0
}
```

**Приклад для іншого шаблону («Бюджет 2027»):**
жодного запису в `TableRelationDef` — таблиці незалежні, метаданих немає.
Система працює однаково.

> Це замінює зашиті у VBA `GetContractSheetAttributes` (метадані),
> `ExpandLinkedSheetGroups` (зв'язність аркушів) і автовиклик
> `SaveWaterConsolidationForMonth` (rollup).

### cfg.SheetGroupRule — правила складу документа
`Id`, `TemplateVersionId` FK, `RuleKind` (`RequiresAll`/`RequiresOne`/`Excludes`/`AlwaysInclude`),
`TriggerSheetDefIds` `nvarchar(400)` (JSON-масив), `AffectedSheetDefIds` `nvarchar(400)`,
`MessageEn/Ru/Kz`

Приклад: обрано `7.` / `7a` / `7b` / `7.0` -> `RequiresAll` для всіх чотирьох
(бо між ними є `Mirror` і `Rollup` зв'язки).

---

## 3. `dic` — Довідники та реєстри

> ⭐ **Немає окремих таблиць під `Permit`, `Substance`, `WaterBody`.**
> Усі довідники — від плоского списку `Generic_EquipStatus` до складного
> `Permit` із правилами і вкладеними речовинами — описуються **одним
> універсальним механізмом**. Структура довідника — це дані в `cfg.Registry*`,
> значення — в `dic.Registry*`.
>
> Повний опис — **[12-reference-data.md](12-reference-data.md)**.

### 3.1 Визначення (у схемі `cfg`)

#### cfg.RegistryDef
| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` | `int` PK | |
| `Code` | `nvarchar(100)` UQ | `Generic_EquipStatus`, `Permit`, `Substance` |
| `NameEn/NameRu/NameKz` | `nvarchar(300)` | |
| `Scope` | `tinyint` | `Global` / `Project` |
| `SourceKind` | `tinyint` | `Local` / `External` / `Hybrid` |
| **`DisplayTemplate`** | `nvarchar(400)` | `{Substance_Code} — {NameEn}` — замінює `=TEXT(V2,"0")&" - "&W2` |
| `SearchTemplate` | `nvarchar(400)` | по яких полях шукати в листбоксі |
| `IsTemporal` | `bit` | записи мають `ValidFrom`/`ValidTo` |
| `IsHierarchical` | `bit` | є `ParentEntryId` |
| `VersioningMode` | `tinyint` | `None`/`SoftDelete`/`Temporal`/`Full` |
| `ParentRegistryDefId` | `int` FK NULL | для вкладених (`PermitPollutant`) |
| `AllowUserEdit` | `bit` | |
| `TemplateVersionId` | `int` FK NULL | для `Scope = Project` |

#### cfg.RegistryFieldDef
Та сама форма, що й `cfg.ColumnDef` — свідомо.

| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` | `int` PK | |
| `RegistryDefId` | `int` FK | |
| `Code` | `nvarchar(100)` | |
| `HeaderEn/HeaderRu/HeaderKz` | `nvarchar(300)` | |
| `DataType` | `tinyint` | `String`/`Int`/`Decimal`/`Bool`/`Date`/`Lookup`/`Formula` |
| `IsKey`, `IsRequired`, `IsUnique` | `bit` | |
| `IsDisplay`, `IsSearchable`, `IsFilterable` | `bit` | |
| **`LookupRegistryDefId`** | `int` FK NULL | поле посилається на інший реєстр |
| `CascadeFromFieldId` | `int` FK NULL | каскадна фільтрація |
| `IsExternallyManaged` | `bit` | для `SourceKind = Hybrid` — поле тільки з джерела |
| `Ordinal`, `StyleId` | | |

**UQ:** `(RegistryDefId, Code)`

#### cfg.RegistryRelationDef
`Id`, `RelationKind` (`Cascade`/`Composition`/`Association`/`Hierarchy`),
`SourceRegistryDefId`, `TargetRegistryDefId`, `ThroughRegistryDefId` NULL (для M:N),
`SourceFieldId`, `TargetFieldId`, `IsRequired`

#### cfg.RegistryRuleDef
`Id`, `RegistryDefId` FK, `RuleKind` (`ValidityWindow`/`RequiredWhen`/`UniqueWithin`/
`Expression`/`CrossRegistry`), `Parameters` JSON, `Expression`, `Severity`,
`MessageEn/Ru/Kz`

#### cfg.RegistrySyncMapping
`Id`, `RegistryDefId` FK, `DataSourceId` FK, `ExternalTemplate`,
`KeyMappingJson`, `FieldMappingsJson`, `OnMissingInSource`
(`MarkOrphaned`/`Deactivate`/`Ignore`), `Schedule`, `IsActive`

### 3.2 Дані (схема `dic`)

#### dic.RegistryEntry
| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` | `int` PK | |
| `RegistryDefId` | `int` FK | |
| **`EntryKey`** | `nvarchar(200)` | бізнес-ключ із полів `IsKey` |
| `ParentEntryId` | `int` FK NULL | ієрархія / вкладена колекція |
| `DisplayText` | `nvarchar(600)` | матеріалізований `DisplayTemplate` (для швидких списків) |
| `IsAvailable` | `bit` | аналог `Is_Available` з AF |
| `ValidFrom`, `ValidTo` | `date` NULL | для `IsTemporal` |
| `Ordinal` | `int` | |
| `CreatedAt/ByUserId`, `UpdatedAt/ByUserId` | | |
| `RowVersion` | `rowversion` | |

**UQ:** `(RegistryDefId, EntryKey, ValidFrom)`
**IX:** `(RegistryDefId, IsAvailable, Ordinal)`, `(RegistryDefId, ValidFrom, ValidTo)`

#### dic.RegistryValue
| Колонка | Тип |
|---------|-----|
| `Id` `int` PK, `RegistryEntryId` FK, `RegistryFieldDefId` FK |
| `ValueString`, `ValueNumeric`, `ValueDate`, `ValueBool` |
| `ValueRefEntryId` `int` FK NULL — для полів `Lookup` |

**Кластерний індекс:** `(RegistryEntryId, RegistryFieldDefId)`

> Обсяг довідників малий (< 100 тис. записів), тому EAV тут нічого не коштує
> і дає повну гнучкість. Для «гарячих» полів — `dic.RegistryIndexValue`
> (денормалізація, як `doc.DocumentIndexValue`).

#### dic.RegistryExternalKey ⭐
| Колонка | Призначення |
|---------|-------------|
| `Id` `int` PK | |
| `RegistryEntryId` FK | наш запис |
| `DataSourceId` FK | яка зовнішня система |
| `ExternalId` `nvarchar(200)` | WebId / GUID у ній |
| `ExternalPath` `nvarchar(1000)` | `\\SERVER\DB\Element\|Attribute` |
| `LastSyncedAt` `datetime2` | |
| `SyncStatus` `tinyint` | `Matched` / `Orphaned` / `Conflict` |

**UQ:** `(DataSourceId, ExternalId)`

> Замінює GUID-и, розкидані по `Configuration!J3`, `AW`, `BA`
> і `DropdownList!AL`. Один запис може мати ID у кількох системах;
> зниклий у джерелі позначається `Orphaned` явно.

#### dic.RegistryAssociation
Для `RelationKind = Association` (M:N із атрибутами) —
аналог матриці `Configuration!BD:BL`.

`Id`, `RelationDefId` FK, `SourceEntryId` FK, `TargetEntryId` FK,
`AttributesJson` (напр. `{"Usage":"Discharge"}`)

**UQ:** `(RelationDefId, SourceEntryId, TargetEntryId)`

#### dic.RegistryEntryHistory
Для `VersioningMode = Full`: `Id`, `RegistryEntryId`, `FieldDefId`,
`OldValue`, `NewValue`, `ChangedAt`, **`ChangedByUserId`** (FK -> `sec.User`). Append-only.

### 3.3 Як лягають чинні довідники

| Зараз | Стає |
|-------|------|
| `DropdownList` A…AG (33 списки) | 33 × `RegistryDef` з 1–3 полями, `VersioningMode = SoftDelete` |
| `DropdownList` J + AL | `Registry "Permit"` + `RegistryExternalKey` |
| `Configuration` R:AH | `WaterGroup`, `WaterItem`, `WaterBalance`, `Note` |
| `Configuration` AJ:AP | `WasteItem` з `Hierarchy` (Group -> Item) |
| `Configuration` AQ:AV | `Substance` |
| `Configuration` AW:AZ | `Permit` (`Permit_Kind = Water`), `IsTemporal = 1` |
| `Configuration` BA:BB | `WaterBody` |
| `Configuration` BD:BL | `RegistryAssociation` `Permit × WaterBody` -> `Usage` |
| `AddPermitAir/Water`, `AddPollutantEmissionFees` + 3 форми | `Registry "Permit"` + `Composition` -> `PermitPollutant` |
| 5 модулів синхронізації (≈4 400 рядків) | `RegistrySyncMapping` + один generic-синхронізатор |

---

## 3a. `uom` — Одиниці вимірювання і конверсії

> **Чому окремий механізм, а не звичайний реєстр.** Реєстр (`cfg.Registry*` +
> `dic.*`) — це **значення, на які посилаються комірки**. Одиниця вимірювання —
> це **метадані про колонку, константу і результат розрахунку**: на неї
> посилається структура, а не дані. Крім того, конверсії мають арифметичну
> семантику, яку рушій зобов'язаний виконувати детерміновано і перевіряти при
> публікації. Тому `uom` — окрема, невелика і фіксована схема, як `sys.Language`.

### uom.Dimension — розмірність (величина)
| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` | `tinyint` PK | |
| `Code` | `nvarchar(30)` UQ | `Mass`, `Volume`, `Energy`, `Time`, `Temperature`, `Amount`, `Dimensionless`, `MassFlow`, `MassPerMass`, `MassPerEnergy` |
| `NameL10n` | `nvarchar(max)` | JSON |
| `BaseUnitId` | `int` FK NULL | канонічна одиниця розмірності (`kg`, `m3`, `J`, `s`) |
| `IsDerived` | `bit` | похідна = відношення двох розмірностей |
| `NumeratorDimensionId` | `tinyint` FK NULL | для похідної: `MassFlow = Mass / Time` |
| `DenominatorDimensionId` | `tinyint` FK NULL | |

Розмірності — **системний перелік**, поповнюється рідко і релізом. Це не обмежує
універсальність: нова одиниця в межах наявної розмірності — це `INSERT`.

### uom.Unit — одиниця
| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` | `int` PK | |
| `Code` | `nvarchar(30)` UQ | `t`, `kg`, `g`, `m3`, `GJ`, `h`, `s`, `g_per_s`, `t_per_year`, `kg_per_t`, `mg_per_m3` |
| `SymbolL10n` | `nvarchar(max)` | JSON: `{"en":"t","ru":"т","kz":"т"}` — символ буває локалізований |
| `NameL10n` | `nvarchar(max)` | JSON |
| `DimensionId` | `tinyint` FK | |
| `IsBase` | `bit` | базова одиниця своєї розмірності |
| **`FactorToBase`** | `decimal(38,18)` | `t` → `1000` (база `kg`) |
| **`OffsetToBase`** | `decimal(38,18)` | потрібен лише для температури (`°C` → `K`: offset `273.15`) |
| `NumeratorUnitId` | `int` FK NULL | для похідних: `g_per_s` = `g` / `s` |
| `DenominatorUnitId` | `int` FK NULL | |
| `DisplayFormat` | `nvarchar(50)` NULL | `#,##0.000` |
| `IsActive` | `bit` | |

**UQ:** `(Code)`; **IX:** `(DimensionId, IsActive)`

> Похідні одиниці **складаються з двох посилань**, а не розбираються з рядка.
> Повної алгебри розмірностей навмисно немає — вона не потрібна для звітності
> і коштує дорого. Достатньо перевірки «чисельник і знаменник збігаються за
> розмірністю».

### uom.Conversion — явні конверсії (винятки і точні коефіцієнти)
| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` | `int` PK | |
| `FromUnitId`, `ToUnitId` | `int` FK | **UQ** разом |
| `Factor` | `decimal(38,18)` | |
| `Offset` | `decimal(38,18)` | |
| `Kind` | `tinyint` | `Exact` = 0 (заміщає маршрут через базу, щоб уникнути подвійного округлення), `LegacyPinned` = 1 (коефіцієнт, зафіксований заради сумісності з чинними числами) |
| `Note` | `nvarchar(400)` | обов'язковий для `LegacyPinned`: звідки взято |

**Маршрут конверсії:**

```
1. FromUnitId = ToUnitId                        → значення без змін
2. Є рядок uom.Conversion (From, To)            → value * Factor + Offset
3. Однакова DimensionId                         → через базу:
      base   = value * From.FactorToBase + From.OffsetToBase
      result = (base - To.OffsetToBase) / To.FactorToBase
4. Різні DimensionId                            → ⛔ ПОМИЛКА, не здогадка
```

> ⛔ **Чого в `uom` немає і не буде: контекстних коефіцієнтів.**
> Щільність (м³ → кг), теплотворність (т → ГДж), молярна маса — це **не
> конверсії одиниць**. Вони залежать від речовини, температури, тиску і
> змінюються з часом. Їхнє місце — `calc.MethodologyConstant` з власною
> одиницею (`kg_per_m3`), темпоральністю і версійністю.
>
> Це найдорожча помилка в системах такого класу: варто пустити щільність у
> таблицю конверсій — і числа починають «пливти» глобально, а знайти причину
> неможливо, бо конверсія виглядає технічною деталлю.

### Де вимірюються посилання на одиниці

| Місце | Колонка | Сенс |
|---|---|---|
| `cfg.ColumnDef` | `UnitId` `int` FK NULL | одиниця, у якій зберігаються значення колонки |
| `cfg.ColumnDef` | `DataType = Unit` | комірка **сама зберігає** `UnitId` — одиниця на рядок (напр. кількість відходів) |
| `cfg.RegistryFieldDef` | `UnitId` FK NULL | поле реєстру (ліміт дозволу тощо) |
| `calc.MethodologyConstant` | `UnitId` FK | коефіцієнт емісії: `kg_per_t`, `g_per_GJ` |
| `calc.MethodologyOutput` | `UnitId` FK | одиниця результату: `t`, `g_per_s` |
| `calc.CalculationResult` | `UnitId` FK | фактична одиниця збереженого результату |
| `ext.EntityFieldMap` | `SourceUnitId`, `TargetUnitId` FK NULL | одиниця на межі інтеграції: атрибути PI AF мають власний UOM |

`doc.CellValue` **не має** колонки одиниці: одиниця визначена колонкою
(`ColumnDef.UnitId`) або, для `DataType = Unit`, зберігається як значення
`ValueRegistryEntryId`-подібного посилання на `uom.Unit`.

## 4. `doc` — Документи та дані

### doc.Project
| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` | `int` PK | |
| `Code` | `nvarchar(50)` UQ | `ECR-2026-LAND` |
| `Name` | `nvarchar(200)` | `2026` |
| **`PeriodStart`, `PeriodEnd`** | `date` | **джерело істини** — довільний діапазон (календарний рік, фіскальний, інший) |
| `Year` | `smallint` NULL | лише підпис для UI/фільтрів, не ідентичність |
| `TagsJson` | `nvarchar(500)` NULL | напр. `["ECR","Land"]` |
| `TemplateVersionId` | `int` FK | |
| `PeriodKind` | `tinyint` | `Monthly`/`Quarterly`/`Yearly`/`Custom` |
| `PeriodPolicyId` | `int` FK | -> `doc.PeriodPolicy` |
| **`YearGraceOffsetDays`** | `int` | **скільки днів після завершення року дані ще редагуються** |
| **`TimeZoneId`** | `nvarchar(64)` | пояс майданчика (`Central Asia Standard Time`); у ньому рахуються межі періодів, offsets і `IsLateEdit` (D-68) |
| **`CurrentPeriodMode`** | `tinyint` | `Auto` = 0 (веде `PeriodStateJob`), `Pinned` = 1 (зафіксовано адміністратором) |
| **`CurrentPeriodId`** | `int` FK NULL | -> `doc.Period`. **Замінює `AF_CurrentReportPeriod`** — це наша конфігурація, не значення з AF |
| `CurrentPeriodPinnedReason` | `nvarchar(400)` NULL | обов'язкова при `Pinned` |
| `CurrentPeriodChangedAt`, `CurrentPeriodChangedByUserId` | | аудит перемикання |
| `ExternalSettingsJson` | `nvarchar(max)` | початкові налаштування, зібрані з зовнішнього джерела при створенні проєкту (для ECR — `CALCULATION_START/ENDDATETIME`); **разове значення, не постійна залежність** |
| `Status` | `tinyint` | `Draft`/`Active`/`Grace`/`Closed`/`Archived` |
| `ClosedAt`, `ClosedByUserId` | | |

> **`CurrentPeriod` — поточний звітний період проєкту.** У чинному рішенні це
> `AF_CurrentReportPeriod` — **один рядок на всю систему**, на який дивляться синки,
> тригери і PI Vision. У новій моделі це **атрибут проєкту**, і система ним
> **володіє**: нічого не читається з AF і нічого туди не пишеться.
>
> * `Auto` — `PeriodStateJob` виставляє найраніший період у стані `Open`;
>   якщо відкритих немає — найпізніший у `Grace`; якщо і таких немає — `NULL`.
> * `Pinned` — адміністратор (`Period.Configure`) фіксує період вручну з причиною;
>   UI показує це явно, бо стан неочевидний.
>
> **Для чого використовується:** період за замовчуванням при відкритті документів;
> період за замовчуванням для розкладів збору і побудови зрізів; значення
> параметра за замовчуванням у звітах.
>
> ⚠ **Для чого НЕ використовується: для рішень про доступ.**
> `IAccessDecisionService` спирається на **стан періоду**, а не на `CurrentPeriod`.
> Інакше «пін» перетворився б на приховане право редагувати закрите.

> Прив'язка до джерела даних (`AfServer`, `PiPrimaryUrl` тощо) винесена в
> `ext.DataSource` — проєкт лише посилається на джерело, щоб ядро не знало про PI AF.

### doc.PeriodPolicy — offsets, налаштовуються адміністратором
| Колонка | Тип | Примітка | Приклад ECR |
|---------|-----|----------|-------------|
| `Id` | `int` PK | | |
| `Code` | `nvarchar(50)` | | `ECR-Standard` |
| `OpenOffsetDays` | `int` | коли відкривається, від **початку** періоду (може бути від'ємним) | `0` |
| **`GraceOffsetDays`** | `int` | **днів після завершення періоду, коли редагування ще дозволене** | **`15`** |
| `HardCloseOffsetDays` | `int` | остаточне закриття, від завершення періоду | `30` |
| `RequireReasonInGrace` | `bit` | обов'язкова причина при пізній зміні | `1` |
| `RequireReapprovalAfterGrace` | `bit` | документ повертається в `Draft` | `0` |
| `ReopenDurationDays` | `int` | на скільки днів `Reopen` відкриває період | `7` |

**Обмеження:** `HardCloseOffsetDays >= GraceOffsetDays >= 0` — CHECK-обмеження.

### doc.Period
| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` | `int` PK | |
| `ProjectId` | `int` FK | |
| `Year` `smallint`, `Sequence` `tinyint` | | 1..12 для `Monthly`, 1..4 для `Quarterly` |
| `StartDate`, `EndDate` | `date` | фактичні межі періоду |
| `State` | `tinyint` | `Scheduled`/`Open`/`Grace`/`Closed` |
| `PeriodPolicyIdOverride` | `int` FK NULL | override політики для конкретного періоду |
| `GraceOffsetDaysOverride` | `int` NULL | «за грудень дали 30 днів замість 15» |
| `ComputedOpenAt`, `ComputedGraceAt`, `ComputedCloseAt` | `datetime2` | обчислені з offsets — денормалізація для швидких перевірок |
| `ReopenedUntil` | `datetime2` NULL | якщо адміністратор відкрив період тимчасово |
| `ReopenReason` | `nvarchar(1000)` NULL | |
| `OpenedAt`, `ClosedAt`, `ClosedByUserId` | | |

**UQ:** `(ProjectId, Year, Sequence)`
**IX:** `(ProjectId, State)`

### Розв'язання offsets — порядок пріоритету

```
1. Period.ReopenedUntil          — якщо ще діє, період відкритий безумовно
2. Period.GraceOffsetDaysOverride
3. Period.PeriodPolicyIdOverride
4. Project.PeriodPolicyId
5. Глобальна політика за замовчуванням
+  sec.RoleAssignment.ExtraGraceDays — додається до розв'язаного значення
```

Обчислення виконує `PeriodStateJob` (щодня) і записує результат у
`ComputedOpenAt` / `ComputedGraceAt` / `ComputedCloseAt`.
Перевірка «чи можна редагувати» — це порівняння з денормалізованими датами,
а не перерахунок ланцюжка пріоритетів на кожен запит.

> `sec.RoleAssignment.ExtraGraceDays` — скільки днів понад загальні має ця роль
> у цій області (напр. контролер якості +10). Див. `sec` у §6.

### doc.Document
**Домен-агностична сутність.** Жодних ECR-специфічних полів — усі поля шапки
(контракт, регіон, дозвіл…) живуть у таблиці `__Header` шаблону як звичайні `ColumnDef`.

| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` | `bigint` PK | |
| `Guid` | `uniqueidentifier` UQ | аналог `Configuration!J4` |
| `ProjectId` | `int` FK | |
| `TemplateVersionId` | `int` FK | **readonly після створення** |
| `BusinessKey` | `nvarchar(200)` | значення поля, позначеного `ColumnDef.IsBusinessKey` (для ECR = `FileNumber`) |
| `Title` | `nvarchar(400)` | обчислюваний підпис для списків |
| `Status` | `tinyint` | `Draft`/`Submitted`/`Approved`/`Rejected` |
| `CreatedAt/ByUserId`, `UpdatedAt/ByUserId` | | |
| `RowVersion` | `rowversion` | оптимістичне блокування |

**IX:** `(ProjectId, BusinessKey)`, `(ProjectId, Status)`

### doc.DocumentIndexValue — денормалізація для пошуку
Щоб не робити `JOIN` через `CellValue` на кожен фільтр у списку документів,
поля з `ColumnDef.IsIndexed = 1` дублюються сюди при збереженні.

`Id`, `DocumentId` FK, `ColumnDefId` FK, `ValueString`, `ValueNumeric`, `ValueRegistryEntryId`

**IX:** `(ColumnDefId, ValueString)`, `(ColumnDefId, ValueRegistryEntryId)`

> Для ECR сюди потрапляють `FileNumber`, `ContractNumber`, `Region`, `Contractor`,
> `PermitId` — тобто саме те, за чим зараз доводиться вивантажувати 1000 EF
> і фільтрувати у VBA.

### doc.DocumentSheet
Які аркуші увімкнені в документі (аналог вибору в `UserSelection.frm`, але як дані).

`Id`, `DocumentId` FK, `SheetDefId` FK, `IsIncluded` `bit`, `IncludedAt`, `IncludedByUserId`

### doc.TableInstance
Екземпляр таблиці = `(документ × таблиця × період)`.

| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` | `bigint` PK | |
| `DocumentId` | `bigint` FK | |
| `TableDefId` | `int` FK | |
| `PeriodId` | `int` FK | |
| `TemplateVersionId` | `int` | денормалізовано, звіряється з `Document` (інваріант 4) |
| `Status` | `tinyint` | `Draft`/`Submitted`/`Approved` |
| `RowCount` | `int` | |
| `LastCalculatedAt` | `datetime2` | |
| `RowVersion` | `rowversion` | |

**UQ:** `(DocumentId, TableDefId, PeriodId)`
> Для сумісності це прямий аналог одного Event Frame у PI AF.

### doc.TableRow — рядок як сутність
| Колонка | Тип | Примітка |
|---------|-----|----------|
| `PeriodKey` | `int` | `Year*100 + Sequence`, **partition key** |
| `Id` | `bigint` | з **`SEQUENCE doc.TableRowSeq`**, не `IDENTITY`: значення потрібне **до** вставки, щоб завантажити рядок і комірки одним проходом `SqlBulkCopy` |
| `TableInstanceId` | `bigint` FK | |
| **`RowKey`** | `nvarchar(100)` | стабільна ідентичність (з `RowDef.RowKey` або GUID `"N"` для динамічних) |
| `RowDefId` | `int` FK NULL | для `RowMode = Fixed` |
| `Ordinal` | `int` | порядок відображення |
| `IsDeleted` | `bit` | |
| **`ModifiedAt`** | `datetime2(3)` | «дотик» при зміні комірок — **без нього `RowVersion` не піднімається і оптимістичне блокування тихо не працює** |
| `RowVersion` | `rowversion` | |

**PK CLUSTERED:** `(PeriodKey, Id)` на `ps_ByPeriodKey`
**UQ:** `(PeriodKey, TableInstanceId, RowKey)` на `ps_ByPeriodKey`

> ⚠ Партиційний ключ **зобов'язаний** входити в PK і в усі unique-індекси,
> інакше індекси не вирівняні й партиційні операції неможливі.

### doc.CellValue ⚠ основний обсяг
| Колонка | Тип | Примітка |
|---------|-----|----------|
| `PeriodKey` | `int` | `Year*100 + Sequence`, **partition key**; детермінований, не сурогатний. Для `Monthly` збігається з `YYYYMM`; для `Quarterly` — `YYYY01…YYYY04`; для `Yearly` — `YYYY01` |
| `TableRowId` | `bigint` FK | |
| `ColumnDefId` | `int` | |
| `TableDefId` | `int` | денормалізовано для складеного FK |
| `ValueString` | `nvarchar(1000)` NULL | |
| `ValueNumeric` | `decimal(28,10)` NULL | |
| `ValueDate` | `datetime2` NULL | |
| `ValueBool` | `bit` NULL | |
| `ValueRegistryEntryId` | `int` NULL | FK -> `dic.RegistryEntry` |
| `ValueUnitId` | `int` NULL | FK -> `uom.Unit`; **лише** при `ColumnDef.DataType = Unit` (одиниця на рядок, ФВ-16.8) |
| `IsCalculated` | `bit` | значення обчислене формулою |
| `IsEmpty` | `bit` | явно порожня vs невідома (аналог `"NULL"` у чинному експорті) |

**Складений FK — гарантує, що комірка не потрапить у чужу колонку:**
```sql
CONSTRAINT FK_CellValue_Column
  FOREIGN KEY (TableDefId, ColumnDefId)
  REFERENCES cfg.ColumnDef (TableDefId, Id)
```

**Первинний / кластерний ключ:** `(PeriodKey, TableRowId, ColumnDefId)` —
без окремого сурогатного `Id`. Причини: PK вирівняний із партиціями (обов'язкова
умова партиціонування), економія ~0.9 ГБ/рік на зайвому індексі, і природна
унікальність комірки.
**Партиціонування:** по `PeriodKey`, схема `ps_ByPeriodKey` — спільна
для `doc.CellValue`, `doc.TableRow`, `doc.TableInstance`.
**Стиснення:** `PAGE`

> ⚠ **Партиційний ключ — `PeriodKey`, а не сурогатний `PeriodId`.** Межі партицій
> виписуються наперед і читаються людиною; «архівувати 2026 рік» — це суцільний
> діапазон партицій від `202601`, а не пошук множини `Id`. **Код не має права
> виводити місяць із `PeriodKey` арифметикою** — місяць береться з `doc.Period`. Ключ партиціонування
> має входити в PK і в **усі** unique-індекси таблиці (ТЗ §13.5 п. 2).

### Що саме тут змінилося порівняно з чинним рішенням

| | Зараз (PI AF) | Стане (SQL) |
|---|---|---|
| Рядок | `Attribute_0010 = "1;7001001;ITEM;094f83a4-…;125.5;1;0;0;"` | `TableRow { RowKey = "7001001" }` |
| Ідентичність рядка | **позиційна** (`0010` = перший рядок діапазону) | **`RowKey`** — стабільний бізнес-ключ |
| Поле | позиція в `;`-рядку, ніде не описана | `CellValue.ColumnDefId` -> `ColumnDef { Code, DataType, … }` |
| Тип | завжди `String` | `Decimal` / `Int` / `Date` / `Bool` / `Lookup` |
| Посилання на довідник | GUID усередині рядка, без FK | `ValueRegistryEntryId` -> FK на `dic.RegistryEntry` |
| Запит «де споживання > 100» | неможливий без вивантаження і парсингу | звичайний `WHERE ValueNumeric > 100` |
| Вставка колонки | ламає всі раніше збережені рядки | `INSERT` у `cfg.ColumnDef`, старі дані не зачеплені |

> **Альтернативна фізична реалізація (див. ТЗ §5.1):** `doc.TableRow.DataJson nvarchar(max)`
> замість окремих `CellValue` — з `OPENJSON`-індексами на 5–10 ключових полів.
> Логічна модель («рядок = сутність, колонка = типізоване поле») **однакова в обох варіантах**;
> відрізняється лише спосіб зберігання. Рішення — після навантажувального тесту на Етапі 0.

### doc.DocumentSnapshot
Незмінна копія на момент `Approved`.

`Id`, `DocumentId` FK, `Version` `int`, `SnapshotReason` (`Approved`/`BeforeMigration`),
`TemplateVersionId`, `ApprovedAt/ByUserId`,
`ContentJson` `nvarchar(max)` (стиснутий), `ContentHash` `varbinary(32)`

> Використовується двічі: як зліпок при затвердженні і як точка відкату
> перед міграцією документа на нову версію шаблону.

---

## 5. `wf` — Робочий процес

### wf.ApprovalRoute
`Id`, `ProjectId` FK, `Name`, `StepCount`

### wf.ApprovalStep
`Id`, `ApprovalRouteId` FK, `StepNumber`, `RequiredAccessLevel` (типово `Approve`),
`ResourceType`/`ResourceId` NULL (на чому потрібен рівень), `ScopeExpression`

> Крок вимагає **рівня доступу на ресурсі**, а не «роль зі списку» — тому
> маршрути не ламаються при перейменуванні ролей (ТЗ ФВ-5.17).

### wf.ApprovalRequest
| Колонка | Тип |
|---------|-----|
| `Id` `bigint` PK |
| `DocumentId` FK, `PeriodId` FK NULL, `SheetDefId` FK NULL |
| `CurrentStep` `int`, `Status` `tinyint` |
| `SubmittedAt/By`, `CompletedAt` |

### wf.ApprovalAction
`Id`, `ApprovalRequestId` FK, `StepNumber`, `Action` (`Approve`/`Reject`/`Reopen`),
`ActorUserId`, `ActedAt`, `Comment` `nvarchar(2000)`

### wf.ValidationResult
Результати останньої валідації (кеш для UI).

`Id`, `TableInstanceId` FK, `TableRowId` NULL, `RowKey` NULL, `ColumnDefId` NULL,
`ValidationRuleId` FK, `Severity`, `Message`, `DetectedAt`

---

## 6. `sec` — Автентифікація і доступ

> Повний опис моделі, алгоритм розв'язання прав і UI —
> **[11-security-model.md](11-security-model.md)**. Тут — лише перелік таблиць.

| Таблиця | Призначення |
|---------|-------------|
| `sec.User` | користувачі обох провайдерів: `AuthProvider` = `Windows` \| `Local`; для локальних — `PasswordHash`, `SecurityStamp`, lockout, 2FA |
| `sec.PasswordHistory` | заборона повторення N останніх паролів |
| `sec.PasswordPolicy` | політика паролів **як дані**, редагується в UI |
| `sec.UserSession` | активні сесії, примусове завершення |
| `sec.Permission` | **системний каталог** атомарних прав (`Template.Publish`, `Period.Reopen`, …) |
| `sec.Role` | **створюється адміністратором**; `IsSystem` — вбудовані, не видаляються |
| `sec.RolePermission` | функціональні права ролі |
| `sec.RoleResourceGrant` | ресурсні гранти: `ResourceType` (`Global`/`Project`/`Sheet`/`Table`/`Column`) -> `AccessLevel` (`None`…`Manage`), `IsDeny`, `RowFilterExpression` |
| `sec.RoleAssignment` | призначення ролі на AD-групу / доменного / локального користувача, з областю, `IsReadOnly`, `ExtraGraceDays`, строком дії |
| `sec.AuthAuditEntry` | події входу (append-only; пароль ніколи не потрапляє) |
| `sec.AccessAuditEntry` | зміни ролей, грантів, призначень, `Impersonate` |

**Ключове для моделі даних:**
* `sec.User.Id` — внутрішній ідентифікатор, на нього посилається весь аудит
  (не SID — бо локальні користувачі SID не мають);
* `CHECK`: `AuthProvider = Local` -> `PasswordHash IS NOT NULL`;
  `AuthProvider = Windows` -> `Sid IS NOT NULL AND PasswordHash IS NULL`;
* `sec.RoleResourceGrant.ResourceId` посилається на `cfg.SheetDef` / `cfg.TableDef` /
  `cfg.ColumnDef` — при soft delete структури гранти не стають «висячими».

---

## 7. `aud` — Аудит (append-only)

### aud.CellChange
| Колонка | Тип |
|---------|-----|
| `Id` `bigint` PK |
| `TableInstanceId` `bigint`, `TableRowId` `bigint`, `RowKey` `nvarchar(100)`, `ColumnDefId` `int` |
| `OldValue`, `NewValue` `nvarchar(1000)` |
| `ChangedAt` `datetime2(3)`, **`ChangedByUserId` `int`** (FK -> `sec.User`) |
| `DocumentStatusAtChange` `tinyint` |
| `PeriodStateAtChange` `tinyint` | стан періоду на момент зміни |
| **`IsLateEdit`** `bit` | зміна виконана у стані `Grace` або після `Reopen` |
| `LateEditReason` `nvarchar(1000)` NULL | якщо `RequireReasonInGrace = 1` |
| `ClientIp` `nvarchar(45)`, `CorrelationId` `uniqueidentifier` |

> `IsLateEdit` дає відповідь на питання аудиту «які дані правили після закриття
> періоду» — зараз таке питання не має відповіді взагалі.

**Партиціонування** по `ChangedAt` (місяць). **Columnstore** для архівних партицій.
Права: `INSERT` only; `UPDATE`/`DELETE` заборонені на рівні БД.

### aud.DocumentEvent
`Id`, `DocumentId`, `EventType`, `Payload nvarchar(max)`, `OccurredAt`, `ActorUserId`

### aud.SchemaChange — журнал змін структури
| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` `bigint` PK | | |
| `TemplateVersionId` `int` | | |
| `EntityType` `nvarchar(50)` | | `ColumnDef` / `TableDef` / … |
| `EntityId` `int` | | |
| `ChangeType` `nvarchar(50)` | | `Add` / `Modify` / `SoftDelete` |
| `ChangeClass` `tinyint` | | `Presentation` / `Safe` / `Guarded` / `Breaking` |
| `BeforeJson`, `AfterJson` `nvarchar(max)` | | |
| `ImpactReportJson` `nvarchar(max)` | | звіт про вплив, показаний користувачу |
| `StrategyJson` `nvarchar(max)` NULL | | обрана стратегія для `Guarded` |
| `ConfirmedByUserId`, `ChangedAt` | | |

### aud.OrphanedValue — значення, що втратили структуру при міграції
`Id`, `DocumentId`, `TableInstanceId`, `RowKey`, `SourceColumnCode`,
`ValueJson`, `OrphanedAt`, `MigrationEventId`

> Нічого не зникає безслідно. Дані з видалених колонок архівуються сюди.

### aud.ConsistencyIssue — результати `ConsistencyCheckJob`
`Id`, `CheckCode`, `Severity`, `EntityType`, `EntityId`, `Details nvarchar(max)`,
`DetectedAt`, `ResolvedAt`, `ResolvedByUserId`

---

## 8. `ext` — Сирі дані із зовнішніх джерел

Дані, зібрані з PI AF (та інших джерел), **зберігаються без інтерпретації**.
Мапінг «сире значення -> комірка документа» — окремий конфігурований крок.

### ext.DataSource
| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` | `int` PK | |
| `Code` | `nvarchar(50)` UQ | `PiAf_Prod` |
| `Kind` | `tinyint` | `PiAf` / `Sql` / `File` / `Api` |
| **`TransportKind`** | `tinyint` | для `PiAf`: `WebApi` = 0, `SqlClient` = 1 |
| `PrimaryUrl`, `SecondaryUrl` | `nvarchar(300)` | для `WebApi` |
| `SqlConnectionName` | `nvarchar(100)` NULL | для `SqlClient`; сама строка — у Key Vault |
| `AfServer`, `AfDatabase` | `nvarchar(100)` | |
| `TimeoutMs`, `MaxBatchSize` | `int` | |
| `IsActive` | `bit` | |
| `ConnectionJson` | `nvarchar(max)` | інші параметри, **без секретів** |

> Один і той самий AF може бути заведений двічі — як `WebApi` (метадані, довідники)
> і як `SqlClient` (масове читання). Мапінги (`ext.DataMappingDef`) посилаються
> на потрібне джерело.

### ext.RawDataPoint ⚠ великий обсяг
| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` | `bigint` PK | |
| `DataSourceId` | `int` FK | |
| `SourcePath` | `nvarchar(1000)` | `\\SERVER\DB\Element|Attribute` |
| `SourceWebId` | `nvarchar(200)` NULL | WebId PI AF |
| `Timestamp` | `datetime2` | час значення |
| `RawValue` | `nvarchar(max)` | **як прийшло, без конвертації** |
| `ValueNumeric` | `decimal(28,10)` NULL | якщо парситься |
| `Quality` | `nvarchar(50)` NULL | Good/Bad/Questionable |
| `RetrievedAt` | `datetime2` | коли ми це забрали |
| `CollectionRunId` | `bigint` FK | |

**UQ (ідемпотентність):** `(DataSourceId, SourcePath, Timestamp)`
**Партиціонування:** по `Timestamp` (місяць)

### 8.1 `ext.Legacy*Mapping` — мапінг ядра на PI AF і на чинний Excel ⭐

Усе, що прив'язує домен-агностичне ядро до конкретного зовнішнього світу
(шаблони AF, позиції в `;`-рядку, координати Excel), живе **тут**, а не в `cfg.*`.
Власники — адаптер PI AF і bootstrap-імпортер. Ядро цих таблиць не читає.

#### ext.LegacySheetMapping
`SheetDefId` FK PK, `LegacyExcelSheetName` (`7. Water Report`)

#### ext.LegacyTableMapping
| Колонка | Примітка |
|---------|----------|
| `TableDefId` FK PK | |
| `DataSourceId` FK | до якого AF |
| `AfEventFrameTemplate` | `HSE_Land_Utility_9_05` |
| `AfElementTemplate` | `Utility_9_05_Template` |
| `AfElementName` | `Utility_9_05` |
| `LegacyTemplateRow` | `241` |
| `LegacyRowOffsetBase` | база для `Attribute_XXXX` |

**IX:** `(DataSourceId, AfEventFrameTemplate)`

#### ext.LegacyColumnMapping
| Колонка | Примітка |
|---------|----------|
| `ColumnDefId` FK PK | |
| `AfAttributeName` NULL | для колонок-метаданих (`File_Number`) |
| **`LegacyFieldIndex`** | **позиція в `;`-рядку** — критично для round-trip |
| `LegacyExcelColumn` | номер колонки Excel |
| `ExportRule` | `PreserveDecimals` / `Standard` — який `CellValueForExport*` застосовувати |

**UQ:** `(ColumnDefId)`; **IX:** по `TableDefId` (через join) + `LegacyFieldIndex`

#### ext.LegacyRowMapping
`RowDefId` FK PK, `AfAttributeName` (`Attribute_0010`), `LegacyExcelRow`

> Якщо колись PI AF зникне з ландшафту — видаляється схема `ext.Legacy*`,
> і **жодна таблиця ядра не змінюється**. Це і є практична перевірка,
> що ядро справді універсальне.

### ext.DataMappingDef — сире значення -> комірка
`Id`, `TemplateVersionId` FK, `DataSourceId` FK, `SourcePathPattern`,
`TargetTableDefId`, `TargetRowKey`, `TargetColumnDefId`,
`TransformExpression` NULL, `IsActive`

> Мапінг конфігурується, а не пишеться кодом. Це те саме metadata-driven,
> що й уся решта системи.

---

## 9. `itg` — Журнали інтеграції

### itg.DataCollectionRun
`Id`, `DataSourceId` FK, `RunKind` (`Dictionary`/`RawData`/`Settings`),
`StartedAt`, `FinishedAt`, `ItemsFetched`, `ItemsNew`, `ItemsUpdated`,
`Status`, `ErrorMessage`, `TriggeredByUserId` NULL (NULL = за розкладом)

### itg.DictionarySyncRun
`Id`, `RegistryDefId` FK, `CollectionRunId` FK, `Added`, `Updated`,
`Deactivated`, `Status`, `ErrorMessage`

### ~~itg.ExternalPublication~~ — **не створюється**
> Рішення D-44 (2026-09-03): **зворотної публікації в AF немає взагалі**, тому
> журнал публікацій не потрібен. Визначення лишено закресленим як історія —
> якби колись з'явився інший зовнішній приймач, воно стало б відправною точкою.

| Колонка | Тип |
|---------|-----|
| `Id` `bigint` PK, `TableInstanceId` `bigint` FK |
| `SinkCode` `nvarchar(50)` (`PiAf`) |
| `ExternalId` `nvarchar(200)` NULL (WebId EF) |
| `ExternalName` `nvarchar(400)` |
| `Status` `tinyint`, `AttemptCount` `int`, `LastAttemptAt` |
| `RequestBody`, `ResponseBody` `nvarchar(max)` |
| `HttpStatus` `int`, `DurationMs` `int`, `ErrorMessage` `nvarchar(2000)` |

---

## 10. `arc` — Архів закритих років

Дзеркало робочих таблиць. **Архівується все, що накопичується**, а не лише дані
документів — політика зберігання «нічого не затирається» (ТЗ §12 п. 25) означає,
що обсяг контролюється рівнями зберігання, а не строками:

| Джерело | Архів | Партиційний ключ джерела |
|---|---|---|
| `doc.TableInstance`, `doc.TableRow`, `doc.CellValue` | `arc.*` однойменні | `PeriodKey` = `Year*100 + Sequence` |
| `aud.CellChange` — історія змін комірок | `arc.CellChange` | **`ChangedAt` по місяцях** (`ps_AuditByMonth`) |
| `aud.*` — журнали безпеки, зміни прав, публікації версій | `arc.*` однойменні | `ChangedAt` по місяцях |
| `calc.CalculationStep`, `calc.CalculationInput` | `arc.CalculationStep`, `arc.CalculationInput` | `PeriodKey` |
| `itg.*` — прогони збору, архівації, консистентності | `arc.*` однойменні | по місяцях |

**Структура ідентична** джерелу, відмінності лише фізичні:

| | `doc.*` (гарячі) | `arc.*` (архів) |
|---|---|---|
| Індекс | rowstore, кластерний по `(TableRowId, ColumnDefId)` | **clustered columnstore** |
| Стиснення | `PAGE` | columnstore (5–10×) |
| `rowversion` | є | немає |
| Тригери | є | немає |
| Файлова група | основна | окрема (за потреби окремий диск) |
| Запис | постійний | лише при архівації / розархівації |

**Коли переноситься:** проєкт перейшов у `Closed` — тобто рік завершився
**і** сплив `Project.YearGraceOffsetDays` (напр. +45 днів).

### itg.ArchiveRun
`Id`, `ProjectId` FK, `Direction` (`ToArchive`/`FromArchive`), `StartedAt`, `FinishedAt`,
`RowsMoved`, `ChecksumSource`, `ChecksumTarget`, `Status`, `ErrorMessage`, `TriggeredByUserId`

**Правила:**
* перенесення **батчами по ~500 тис. рядків**: `INSERT INTO arc.* WITH (TABLOCK)
  SELECT … WHERE PeriodKey = @k` — мінімальне логування і прямий запис у
  columnstore rowgroups;
* звірка **трьох** контрольних сум до і після: `COUNT_BIG(*)`,
  `CHECKSUM_AGG(BINARY_CHECKSUM(*))`, `SUM(ValueNumeric)`;
* розбіжність -> `ROLLBACK` + `aud.ConsistencyIssue(Critical)` + СТОП;
  **дані з джерела не видаляються** — головне правило процедури;
* збіг -> `TRUNCATE TABLE doc.* WITH (PARTITIONS (@from TO @to))` — звільняє
  партиції майже миттєво, мінімально логується;
* ⛔ **`SWITCH PARTITION` не застосовний**: він вимагає однакової файлової групи
  та ідентичних індексів, а `arc.*` — columnstore на окремій групі. `DELETE`
  батчами теж не підходить — роздуває журнал на 108 млн рядків;
* операція **відновлювана**: після збою продовжується з партиції, на якій
  зупинилася; під час виконання проєкт має прапорець `IsArchiving`;
* читання архіву **прозоре**: репозиторій обирає джерело за станом
  `doc.Project.Status`/`itg.ArchiveRun`, **не за датою** — інакше читання ловить
  напівстан під час самої архівації;
* `Reopen` архівного року виконує зворотне перенесення тією самою процедурою;
* **автоматичного видалення немає ніде** — ні в `arc.*`, ні в `aud.*`, ні в
  `calc.*`; тільки явна адміністративна операція з підтвердженням і аудитом;
* архівація **аудиту — щомісячна**, не річна: `aud.CellChange` дає ~2× рядків
  від `CellValue`, і до архівації цей обсяг лежить у гарячій зоні.

---

## 11. Оцінка обсягу і план партиціонування

| Таблиця | Рядків/рік (оцінка) | Стратегія |
|---------|---------------------|-----------|
| `cfg.*` | < 15 000 | звичайні таблиці, кеш у пам'яті (immutable -> кеш назавжди) |
| `dic.RegistryEntry` / `dic.RegistryValue` | < 100 000 / < 500 000 | звичайні + IX |
| `doc.Document` | **≥ 200** (факт замовника), цільовий сценарій **300**, запас **500** | звичайна |
| `doc.TableInstance` | 500 × 90 × 12 ≈ **540 000** | звичайна + IX |
| `doc.TableRow` | 540 000 × ~30 ≈ **16 млн** | партиції по `PeriodKey` |
| **`doc.CellValue`** | 16 млн × ~7 ≈ **108 млн** | **партиції по `PeriodKey`**, PK `(PeriodKey, TableRowId, ColumnDefId)`, стиснення `PAGE` |
| `aud.CellChange` | ~2× від `CellValue` | партиції по `ChangedAt` (місяць), **щомісячна архівація** |
| `ext.RawDataPoint` | залежить від частоти збору (питання S-2) | партиції по `Timestamp`, архівація без видалення |
| **`arc.*`** | накопичується **без верхньої межі** | columnstore, окрема файлова група `DATA_ARCHIVE` на розширюваному диску |

> **Ключове:** завдяки архівації робочі таблиці тримають обсяг **приблизно одного
> року** (~108 млн рядків `CellValue`), а не N років. Тобто система не деградує
> з часом — це головний аргумент за збереження чинного підходу з архівом.

> ⚠ **Обов'язковий Етап 0:** згенерувати синтетичні 108 млн рядків, заміряти
> час відкриття таблиці, збереження діапазону, агрегації, **а також повний цикл
> архівації одного року**. Якщо не проходить ФВ-6.10 — переходити на JSON-гібрид
> (логічна модель при цьому не змінюється).

---

## 12. Приклад: як старе перетворюється на нове

**Було (PI AF) — один рядок без ідентифікації полів:**
```
EventFrame "HSE_Land_07_test_2026_09_02_10_15_33"
  Template = HSE_Land_07
  StartTime = 2026-09-01T00:00:00Z
  Attribute_0010 = "1;7001001;ITEM;094f83a4-…;0151202a-…;;125.5;1;0;0;0;"
  Attribute_0020 = "2;7001002;ITEM;094f83a4-…;0151202a-…;;340.2;1;0;0;0;"
  File_Number    = "test"
  Contract_Number= "321"
```
Що означає сьоме значення в цьому рядку — знає **тільки код VBA**.

**Стало (SQL) — сутність із іменованими типізованими полями:**
```
doc.Document      Id=42  FileNumber='test'  ProjectId=1  TemplateVersionId=7

doc.TableInstance Id=9001  DocumentId=42  TableDefId=17 (WaterReport.Main)  PeriodId=9

doc.TableRow      Id=770001  TableInstanceId=9001  RowKey='7001001'  Ordinal=1
                  Id=770002  TableInstanceId=9001  RowKey='7001002'  Ordinal=2

doc.CellValue     TableRowId=770001  ColumnDef 'No'           ValueNumeric  = 1
                  TableRowId=770001  ColumnDef 'ReportId'     ValueString   = '7001001'
                  TableRowId=770001  ColumnDef 'ReportType'   ValueString   = 'ITEM'
                  TableRowId=770001  ColumnDef 'Permit'       ValueRegistryEntryId = 15  → Registry 'Permit'
                  TableRowId=770001  ColumnDef 'WaterBody'    ValueRegistryEntryId = 3   → Registry 'WaterBody'
                  TableRowId=770001  ColumnDef 'PrevBalance'  IsEmpty       = 1
                  TableRowId=770001  ColumnDef 'MonthValue'   ValueNumeric  = 125.5
                  TableRowId=770001  ColumnDef 'IsOffshore'   ValueBool     = 1
```

**Метадані контракту не дублюються в кожному рядку** — вони приходять через
`cfg.TableRelationDef { RelationKind = MetadataSource, SyncMode = Reference }`
з таблиці `__Header` документа. Один запис на документ, а не 90 копій.

**Тепер можливі звичайні запити:**
```sql
-- «покажи всі позиції зі споживанням > 100 м³ у вересні по дозволу X»
SELECT d.FileNumber, r.RowKey, cv.ValueNumeric
FROM doc.CellValue cv
JOIN doc.TableRow r      ON r.Id = cv.TableRowId
JOIN doc.TableInstance ti ON ti.Id = r.TableInstanceId
JOIN doc.Document d       ON d.Id = ti.DocumentId
JOIN cfg.ColumnDef c      ON c.Id = cv.ColumnDefId
WHERE c.Code = 'MonthValue'
  AND cv.ValueNumeric > 100
  AND cv.PeriodId = 9;
```
У чинному рішенні це означало б вивантажити 1000 Event Frames і розпарсити
`;`-рядки у VBA-циклі.

**Зворотна публікація в AF** (якщо потрібна на перехідний період) відновлює
точно той самий `;`-рядок, використовуючи `ext.LegacyColumnMapping.LegacyFieldIndex`
для порядку полів.
