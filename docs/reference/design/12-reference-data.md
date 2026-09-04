# Довідники та реєстри — універсальний механізм

> Вимога: аркуші `DropdownList` (списки для комірок) керуються у вебі, з версійністю.
> Permit — складніша сутність із правилами, що включає інші сутності (забруднюючі
> речовини тощо). На `Configuration` є дані зі структури AF, потрібні для ідентифікації.
> Потрібен універсальний механізм створення таких таблиць і використання їх
> у листбоксах комірок.

---

## 1. Що є зараз (перевірено по файлу)

### 1.1 Аркуш `DropdownList` — 33 плоскі списки

Структура колонки:

| Рядок | Зміст | Приклад |
|-------|-------|---------|
| 1 | контекст (для якого аркуша) | `2. Contract`, `3. Stationary` |
| 2 | **ім'я named range** — ключ, який бачить Data Validation | `Generic_EquipStatus`, `Contract_Area` |
| 3–4 | **прив'язка до комірок цільового аркуша** | `B` + `6` -> `Contract!B6`; `E5` + `E146` -> `Stationary!E5:E146`; `Row 16` |
| 5+ | значення | `Diesel - Дизель` |

Приклади прив'язок (рядки 3–4):

```
D: Contract_Area                  -> B, 6      (Contract!B6)
J: Contract_Permit_number         -> B, 24
M: Stationary_Type_of_Equipment   -> E5, E146  (Stationary!E5:E146)
Z: Waste_Name_Facility_1          -> Row 16
A: Generic_EquipStatus            -> (немає, використовується скрізь)
```

**Колонка AL** — паралельний стовпець ідентифікаторів для колонки J:

| J (Permit_Number) | AL (Id у AF) |
|-------------------|--------------|
| `KZ06VCZ14825472` | `c4eed347-52a5-11f1-8201-0050568c2e1a` |
| `KZ14VCZ14523270_2026` | `dae273ba-ec88-11f0-81f3-0050568c2e1a` |
| … (9 записів) | |

Синхронізується з AF (шаблон `Dictionary_Land`), кожен запис має атрибут
`Is_Available` — саме він визначає, показувати значення чи прибрати.

### 1.2 `Configuration` R…BL — багатополеві довідники

Це вже не плоскі списки, а **справжні реєстри з полями та ID**:

| Діапазон | Реєстр | Поля |
|----------|--------|------|
| `R:U` | Water groups | `Name_EN`, `Entry_Id`, `Entry_Type`, `Name_RU` |
| `V:Y` | Water items | те саме |
| `Z:AC` | Water balances | те саме |
| `AD:AG` | Notes | те саме |
| `AH` | *обчислюване* | `=TEXT(V2,"0")&" - "&W2` -> `Item_Display` |
| `AJ:AP` | Waste items | `Entry_Id`, `Group_Id`, `Group_Name_EN/RU`, `Name_EN/RU`, `Item_Display` |
| `AQ:AV` | **Substances** | `Substance_Code`, `Name_En/Ru`, `Density`, `Type_hazard`, `Header_Display` |
| `AW:AZ` | **Permits** | `PermitId` (GUID), `Permit_Number`, `Start_Date`, `Actual_End_Date` |
| `BA:BB` | Water bodies | `Name_water_body_Id` (GUID), `Name_En` |
| `BD:BL` | **Permit × WaterBody** | матриця з типом (`PermitWaterIntakeValue` / `PermitWaterDischargeValue`) |

### 1.3 Permit — складна сутність

З `AddPermitAir.bas` / `AddPermitWater.bas` / `AddPollutantEmissionFees.bas`:

```
Permit
├─ Метадані:   Date_Issue, Permit_Number, Category
├─ Період дії: Start_Date, Actual_End_Date
├─ Класифікація (каскад):
│     Air:   Entity -> Location      (lstEntity_Change фільтрує lstLocation)
│     Both:  Type   -> Indicator     (lstType_Change фільтрує lstIndicator)
├─ Значення:  Value_01 … Value_05
├─ Прив'язка: Location / об'єкт
├─ Водні об'єкти (M:N із типом): Permit × WaterBody -> Intake | Discharge
└─ Забруднюючі речовини (вкладена колекція):
      Pollutant[] { Number, Indicator, Value_01, Value_02 }   ← з довідника Substances
```

Плюс **правило**, яке впливає на введення даних: вікно `Start_Date…Actual_End_Date`
визначає, які місяці відкриті для рядка (`ApplyPermitMonthLocks`).

### 1.4 `Configuration` — ідентифікатори зі структури AF

| Комірка | Що це | Навіщо |
|---------|-------|--------|
| `B1` / `B2` | SERVER / DB | адреса AF-бази |
| `H3` = `Location`, `I3` = `Land` | тип і назва локації | контекст документа |
| `J3` = `c4eed347-52a5-11f1-…` | GUID з AF; **збігається з ID дозволу `KZ06VCZ14825472`** (`DropdownList!AL5`) — ймовірно кеш ID обраного дозволу, а не локації; уточнити при bootstrap | ідентифікація в AF |
| `K3` = `2026`, `K4` = `25` | рік, службовий параметр | |
| `J4` | GUID згенерованого файлу | ідентифікація документа |
| `L1…L8` | таймстемпи синхронізацій | throttling при відкритті |
| `DropdownList!AL` | ID дозволів у AF | резолвінг `Permit_Number -> PermitId` |
| `AW`, `BA` | GUID дозволів і водних об'єктів | у даних зберігаються **ID, а не назви** |

**Висновок:** усе це — **зовнішні ідентифікатори**, розкидані по комірках.
Потрібен один механізм зіставлення «наш запис ↔ запис у зовнішній системі».

---

## 2. Три проблеми, які треба вирішити

| # | Проблема | Наслідок |
|---|----------|----------|
| 1 | **Значення = відображуваний текст.** У комірку пишеться `"Diesel - Дизель"`, а не ID | перейменували значення в довіднику — історичні дані «відв'язалися» |
| 2 | **Структура довідника зашита в колонки Excel** | додати поле в довідник = правка макета + VBA |
| 3 | **Немає версійності записів** | значення прибрали з довідника — старий документ показує порожньо або «биту» позицію |

> Проблема 1 — та сама, що й із рядками таблиць (позиційна ідентичність замість
> явної). Розв'язується так само: **зберігати ID, показувати текст**.

---

## 3. Рішення: універсальний реєстр (Registry)

Той самий підхід, що й для шаблонів: **структура довідника — це дані**.

```
cfg.RegistryDef            «Довідник» — визначення
 ├─ cfg.RegistryFieldDef   поля (Code, DataType, IsKey, IsDisplay, Lookup…)
 ├─ cfg.RegistryRelationDef зв'язки з іншими реєстрами
 └─ cfg.RegistryRuleDef    правила (вікна дії, обов'язковість, вирази)
        │
        ▼
dic.RegistryEntry          запис (з версійністю)
 ├─ dic.RegistryValue      значення полів
 └─ dic.RegistryExternalKey зовнішні ID (AF WebId тощо)
```

**Ключова ідея:** плоский список (`Generic_EquipStatus`) і складна сутність
(`Permit`) — **це один і той самий механізм**, різниця лише в кількості полів
і наявності зв'язків.

### 3.1 `cfg.RegistryDef`

| Поле | Призначення |
|------|-------------|
| `Code` | `Generic_EquipStatus`, `Permit`, `Substance` — ключ для `ColumnDef.LookupRegistryDefId` |
| `NameL10n` | локалізовані назви — JSON `{"en","ru","kz"}` + реєстр `sys.Language` (конвенція — [07](07-data-model.md) §2); те саме для `HeaderL10n`/`MessageL10n` нижче |
| `Scope` | `Global` (спільний) / `Project` (свій на кожен проєкт) |
| `SourceKind` | `Local` (ведеться у вебі) / `External` (синхронізується) / `Hybrid` |
| **`DisplayTemplate`** | `{Code} - {NameEn}` — замінює формули `=TEXT(V2,"0")&" - "&W2` |
| `SearchTemplate` | по яких полях шукати в листбоксі |
| **`IsTemporal`** | чи мають записи період дії (`ValidFrom`/`ValidTo`) |
| `IsHierarchical` | чи є `ParentEntryId` (групи -> позиції) |
| `AllowUserEdit` | чи можна редагувати у вебі |
| `VersioningMode` | `None` / `SoftDelete` / `Temporal` / `Full` (див. §5) |

### 3.2 `cfg.RegistryFieldDef`

Та сама форма, що й `ColumnDef` — свідомо:

| Поле | Призначення |
|------|-------------|
| `Code`, `HeaderEn/Ru/Kz` | |
| `DataType` | `String`/`Int`/`Decimal`/`Bool`/`Date`/`Lookup`/`Formula` |
| `IsKey` | входить у бізнес-ключ запису |
| `IsRequired`, `IsUnique` | |
| `IsDisplay` | бере участь у `DisplayTemplate` |
| `IsSearchable`, `IsFilterable` | |
| **`LookupRegistryDefId`** | поле посилається на інший реєстр -> **каскади** |
| `CascadeFromFieldId` | фільтрувати за значенням іншого поля |
| `Ordinal`, `StyleId` | |

### 3.3 `cfg.RegistryRelationDef` — зв'язки між реєстрами

| `RelationKind` | Що це | Приклад із ECR |
|----------------|-------|----------------|
| `Cascade` | значення поля B фільтрується вибором у полі A | `Type -> Indicator`; `Entity -> Location` |
| **`Composition`** | вкладена колекція (частина батька) | `Permit -> Pollutant[]` |
| **`Association`** | M:N із власними атрибутами | `Permit × WaterBody` -> `Usage` (Intake/Discharge) |
| `Hierarchy` | батько-нащадок у межах одного реєстру | Waste Group -> Waste Item |

### 3.4 `cfg.RegistryRuleDef` — правила

| `RuleKind` | Приклад |
|------------|---------|
| `ValidityWindow` | `Start_Date <= period <= Actual_End_Date` -> використовується `SourceWindow` у правилах доступу до періодів |
| `RequiredWhen` | `Indicator` обов'язковий, якщо `Type = 'Emission'` |
| `UniqueWithin` | `Permit_Number` унікальний у межах локації |
| `Expression` | `Value_01 <= Value_02` |
| `CrossRegistry` | усі речовини дозволу мають бути в довіднику `Substance` |

---

## 4. Permit як приклад конфігурації

Нічого спеціального в коді — тільки конфігурація:

```
RegistryDef "Permit"
  Scope           = Global
  SourceKind      = Hybrid          ← можна вести у вебі і синхронізувати з AF
  IsTemporal      = true
  VersioningMode  = Temporal
  DisplayTemplate = "{Permit_Number}"

  Fields:
    Permit_Number      String   IsKey, IsRequired, IsDisplay
    Permit_Kind        Lookup   -> Registry "PermitKind"  (Air | Water)
    Date_Issue         Date     IsRequired
    Start_Date         Date     IsRequired
    Actual_End_Date    Date     IsRequired
    Category           String
    Type               Lookup   -> Registry "PermitType"
    Indicator          Lookup   -> Registry "PermitIndicator"
                                CascadeFrom = Type          ← lstType_Change
    Entity             Lookup   -> Registry "Entity"
    Location           Lookup   -> Registry "Location"
                                CascadeFrom = Entity        ← lstEntity_Change
    Value_01..Value_05 Decimal

  Relations:
    Composition  Permit -> "PermitPollutant"
    Association  Permit <-> "WaterBody"  through "PermitWaterBody" (Usage)

  Rules:
    ValidityWindow   from=Start_Date  to=Actual_End_Date
    Expression       Start_Date < Actual_End_Date
    CrossRegistry    PermitPollutant.Substance IN Registry "Substance"

RegistryDef "PermitPollutant"          ← вкладена колекція
  Scope = Global,  ParentRegistry = "Permit"
  Fields:
    Number     Int      IsKey
    Substance  Lookup   -> Registry "Substance"
    Indicator  Lookup   -> Registry "PermitIndicator"
    Value_01   Decimal
    Value_02   Decimal

RegistryDef "Substance"
  DisplayTemplate = "{Substance_Code} — {NameEn}"
  Fields:
    Substance_Code  String  IsKey
    NameEn, NameRu  String  IsDisplay
    Density         String
    Type_hazard     Lookup -> Registry "HazardType"
```

**Що це замінює:** `AddPermitAir.bas` (536), `AddPermitWater.bas` (530),
`AddPollutantEmissionFees.bas` (409), `frmPermitAir.frm` (221),
`frmPermitWater.frm` (179), `frmPollutantEmissionFees.frm` (128),
`PermitConstantSync.bas` (574), `PermitWaterSync.bas` (837) — **≈3 400 рядків VBA
стають конфігурацією і одним generic-CRUD.**

---

## 5. Версійність довідників

Треба розрізняти дві різні речі. Обидві потрібні.

### 5.1 Версійність СТРУКТУРИ довідника

Так само, як `TemplateVersion`: `RegistryDef` належить `TemplateVersion`
(для `Scope = Project`) або має власну версію (для `Scope = Global`).
Опублікована структура незмінна; зміна — нова версія + `CloneFrom`.

Класи змін ті самі: 🔵 `Presentation` (підпис, `DisplayTemplate`) / 🟢 `Safe` (додати поле) /
🟡 `Guarded` (звузити тип) / 🔴 `Breaking` (видалити поле, змінити `Code`) — див.
[10-schema-evolution.md](10-schema-evolution.md).

### 5.2 Версійність ЗАПИСІВ (`VersioningMode`)

| Режим | Поведінка | Кому |
|-------|-----------|------|
| `None` | звичайний CRUD, видалення фізичне | тимчасові технічні списки |
| **`SoftDelete`** | `IsAvailable = 0`, запис лишається | плоскі списки (`Generic_EquipStatus`) — **аналог `Is_Available` в AF** |
| **`Temporal`** | `ValidFrom` / `ValidTo`; у кожен момент часу діє свій набір | `Permit`, тарифи, ставки |
| `Full` | `Temporal` + повна історія змін кожного поля | те, що перевіряє регулятор |

**Головне правило:**

> Запис довідника **ніколи не видаляється фізично**, якщо на нього посилаються дані.
> Документ, що посилався на значення, продовжує його резолвити — навіть якщо
> значення прибрали з активного списку.

Що бачить користувач:

| Ситуація | Поведінка |
|----------|-----------|
| Новий документ | у листбоксі лише активні значення на дату періоду |
| Старий документ зі знятим значенням | значення показується з позначкою «архівне», редагування не примусове |
| Спроба зберегти з архівним значенням | `Warning`, не `Error` (інакше не відкриєш старий документ) |
| Звіт за минулий рік | значення резолвиться так, як діяло **тоді** |

### 5.3 Темпоральний резолвінг

Для `IsTemporal = true` запит завжди має дату:

```sql
-- список для листбокса на вересень 2026
SELECT e.Id, e.DisplayText
FROM dic.RegistryEntry e
WHERE e.RegistryDefId = @permit
  AND e.IsAvailable = 1
  AND (e.ValidFrom IS NULL OR e.ValidFrom <= '2026-09-30')
  AND (e.ValidTo   IS NULL OR e.ValidTo   >= '2026-09-01')
```

Це те саме перетинання, що робить зараз `ApplyPermitMonthLocks` — але один раз,
у загальному механізмі, а не окремим кодом на кожен випадок.

---

## 6. Зв'язок із таблицями документів

### 6.1 Прив'язка до комірок

Те, що зараз лежить у рядках 3–4 аркуша `DropdownList`
(`Contract_Area -> B, 6`), стає полем **`ColumnDef.LookupRegistryDefId`**:

```
ColumnDef "PermitNumber"
  DataType         = Lookup
  LookupRegistryDefId = Registry "Permit"
  CascadeFromColumnId = ColumnDef "Entity"     ← каскад усередині таблиці
  LookupFilter     = "Permit_Kind = 'Water'"   ← звуження списку
```

Прив'язка стає **явною і типізованою**, замість двох текстових комірок,
які треба вручну тримати в синхроні з макетом аркуша.

### 6.2 Що фактично зберігається в комірці

| | Зараз | Стане |
|---|---|---|
| Значення в комірці | `"Diesel - Дизель"` (текст) | `ValueRegistryEntryId = 42` (FK) |
| Відображення | той самий текст | `DisplayTemplate` + мова користувача |
| Перейменували значення | історичні дані «відв'язалися» | усі документи бачать нову назву, зв'язок цілий |
| Пошук по значенню | `LIKE '%Дизель%'` по всіх комірках | `WHERE ValueRegistryEntryId = 42` |

### 6.3 Каскади

Каскад описується один раз у реєстрі (`CascadeFromFieldId`) або в таблиці
(`CascadeFromColumnId`) і працює однаково:
* у листбоксі комірки;
* у формі редагування довідника;
* при імпорті з Excel (значення поза каскадом -> помилка валідації).

Замінює `lstType_Change` / `lstEntity_Change` у формах і
`UpdateWaterBodyDropdown` (`Sheet_Config.bas`) — усе це стає одним механізмом.

---

## 7. Зовнішні ідентифікатори

Замість GUID-ів, розкиданих по `Configuration!J3`, `AW`, `BA`, `DropdownList!AL`:

### dic.RegistryExternalKey
| Колонка | Призначення |
|---------|-------------|
| `RegistryEntryId` FK | наш запис |
| `DataSourceId` FK | яка зовнішня система (`ext.DataSource`) |
| `ExternalId` | WebId / GUID у ній |
| `ExternalPath` | `\\SERVER\DB\Element\|Attribute` |
| `LastSyncedAt` | |
| `SyncStatus` | `Matched` / `Orphaned` / `Conflict` |

**UQ:** `(DataSourceId, ExternalId)`

Це дає:
* один запис може мати ID у **кількох** зовнішніх системах;
* `Orphaned` (запис зник у AF) — видно явно, а не «тихо не оновилося»;
* при перемиканні AF-сервера ідентифікація не ламається.

`Configuration!J3` (GUID з AF, за фактом збігається з ID дозволу з `DropdownList!AL5`)
стає `RegistryExternalKey` відповідного запису реєстру — а не «GUID у комірці,
про яку знає лише VBA». Що саме він ідентифікує — з'ясувати при bootstrap-імпорті.

---

## 8. Синхронізація із зовнішнім джерелом

`SourceKind` визначає напрямок:

| Режим | Хто master | Поведінка |
|-------|-----------|-----------|
| `Local` | ECR | синхронізації немає; повний CRUD у вебі |
| `External` | AF | CRUD у вебі заблокований; тільки читання і перегляд журналу |
| `Hybrid` | обидва | поля позначені `IsExternallyManaged` тільки читаються; решта редагується |

`cfg.RegistrySyncMapping` — конфігурований мапінг (не код):

```
RegistrySyncMapping "Permit <- AF"
  DataSource        = PiAf_Prod   (TransportKind = WebApi)
  ExternalTemplate  = "HSE_Permit_Water"
  KeyMapping        = PermitId -> ExternalId
  FieldMappings:
      Permit_Number     <- attribute "Permit_Number"
      Start_Date        <- attribute "Start_Date"
      Actual_End_Date   <- attribute "Actual_End_Date"
      IsAvailable       <- attribute "Is_Available"
  OnMissingInSource = MarkOrphaned      ← не видаляти
  Schedule          = daily 03:00
```

Замінює 5 модулів синхронізації (`DictionaryEF`, `WR_DictionarySync`,
`WasteReportMetricSync`, `PermitConstantSync`, `PermitWaterSync` — **≈4 400 рядків**)
одним механізмом плюс конфігурацією.

> ⚠ Питання «хто master для довідників» залишається відкритим і може змінитися
> після аналізу бекенду — тому `SourceKind` є полем налаштування, а не
> архітектурним припущенням.

---

## 9. UI

### Екран «Довідники»
Список реєстрів: код, назва, кількість записів, джерело, останній синк, версія структури.

### Конструктор реєстру
* поля (як конструктор таблиці): тип, обов'язковість, ключ, посилання на інший реєстр;
* `DisplayTemplate` із живим попереднім переглядом;
* зв'язки (каскади, вкладені колекції, M:N);
* правила;
* мапінг на зовнішнє джерело;
* публікація версії структури.

### Редактор записів
* grid із фільтрами і пошуком;
* для `IsTemporal` — перемикач «станом на дату» і шкала періодів дії;
* для `Composition` — вкладена таблиця (речовини всередині картки дозволу);
* для `Association` — редактор M:N із атрибутами (Permit × WaterBody -> Usage);
* імпорт/експорт `.xlsx`;
* **«Де використовується»** — у яких `ColumnDef` і скількох документах
  задіяно значення (обов'язково перед тим, як його прибирати).

---

## 10. Міграція наявних довідників

| Джерело | Кількість | Що робимо |
|---------|-----------|-----------|
| `DropdownList` A…AG | 33 списки | -> `RegistryDef` з 1–3 полями, `VersioningMode = SoftDelete` |
| `DropdownList` J + AL | 9 дозволів | -> реєстр `Permit` + `RegistryExternalKey` |
| `Configuration` R:AH | 4 водні реєстри | -> `WaterGroup`, `WaterItem`, `WaterBalance`, `Note` |
| `Configuration` AJ:AP | waste items | -> `WasteItem` з ієрархією `Group -> Item` |
| `Configuration` AQ:AV | речовини | -> `Substance` |
| `Configuration` AW:AZ | дозволи (вода) | -> `Permit` (`Permit_Kind = Water`) |
| `Configuration` BA:BB | водні об'єкти | -> `WaterBody` |
| `Configuration` BD:BL | матриця | -> `Association` `Permit × WaterBody` з `Usage` |
| `Configuration` B1/B2/J3 | адреси і WebId AF | -> `ext.DataSource` + `RegistryExternalKey` |

**Bootstrap-імпортер** (те саме, що для шаблону) читає ці діапазони
і генерує визначення реєстрів автоматично — інакше це сотні записів руками.

**Найважливіший крок міграції:** у документах значення-тексти
(`"Diesel - Дизель"`) треба **зіставити з ID записів реєстру**.
Незіставлені -> звіт, ручний розбір. Це той самий клас робіт, що й розбір
`;`-рядків, і його треба закласти в оцінку Етапу 5.
