# 02c — Тестові фікстури

> Частина контракту. Це **золотий набір ПК-2**: усе, що потрібно для перевірки
> коректності, **вбудоване тут** — до реальних `.xlsm`, VBA і дампів SQL
> звертатися під час розробки не треба (`08-workflow.md` §8).
>
> Фікстури побудовані **за мотивами** реального `7. Water Report`: та сама
> структура (`GROUP`/`ITEM`/`BALANCE`/`NOTE`), ті самі типи залежностей
> (rollup між аркушами, дозволи з вікнами дії, крос-періодні посилання,
> каскадні довідники, одиниці), але зменшений обсяг і синтетичні числа.
>
> **Очікувані значення пораховані вручну і перевірені.** Якщо код дає інше —
> правий цей файл, а не код (`08-workflow.md` §4).

## Зміст

| Якір | Розділ |
|---|---|
| [`#structure`](#structure) | Структура шаблону |
| [`#registries`](#registries) | Реєстри і одиниці |
| [`#formulas`](#formulas) | Формули з очікуваними результатами |
| [`#data`](#data) | Дані документа |
| [`#expected`](#expected) | Очікувані значення |
| [`#access`](#access) | Матриця доступу |
| [`#calc`](#calc) | Методологія і розрахунок |
| [`#edge`](#edge) | Крайові випадки |
| [`#json`](#json) | Машинний формат для завантаження в тестах |

---

<a id="structure"></a>
## 1. Структура шаблону

Шаблон `WATER_DEMO`, версія `1.0.0.0`, `PeriodKind = Monthly`.

### Аркуш `Water_07` — «Water Report»

**Таблиця `Main`**, `LayoutKind = MonthsInColumns`, `RowMode = Fixed`.

Колонки:

| Code | Header (en) | DataType | Unit | Ordinal | Прим. |
|---|---|---|---|---:|---|
| `RowLabel` | Row | `String` | — | 1 | `IsReadOnly` |
| `Permit` | Permit | `Lookup` → `PERMIT` | — | 2 | |
| `WaterBody` | Water body | `Lookup` → `WATER_BODY` | — | 3 | каскад від `Permit` |
| `Jan` | January | `Decimal(18,3)` | `m3` | 4 | `IsMonthColumn`, `MonthNumber = 1` |
| `Feb` | February | `Decimal(18,3)` | `m3` | 5 | `IsMonthColumn`, `MonthNumber = 2` |
| `Mar` | March | `Decimal(18,3)` | `m3` | 6 | `IsMonthColumn`, `MonthNumber = 3` |
| `Total` | Total Q1 | `Formula` | `m3` | 7 | `IsReadOnly` |
| `Note` | Note | `String` | — | 8 | |

Рядки:

| RowKey | Ordinal | RowKind | Label (en) | Parent |
|---|---:|---|---|---|
| `7001000` | 1 | `Group` | Surface water intake | — |
| `7001001` | 2 | `Item` | River A | `7001000` |
| `7001002` | 3 | `Item` | River B | `7001000` |
| `7001003` | 4 | `Item` | Reservoir C | `7001000` |
| `7001100` | 5 | `Group` | Groundwater intake | — |
| `7001101` | 6 | `Item` | Well 1 | `7001100` |
| `7001102` | 7 | `Item` | Well 2 | `7001100` |
| `7009000` | 8 | `Balance` | TOTAL INTAKE | — |
| `7009900` | 9 | `Note` | Methodology note | — |

### Аркуш `Water_070` — «Water consolidation»

**Таблиця `Rollup`**, `LayoutKind = MonthsInColumns`, `RowMode = Fixed`.

| Code | DataType | Unit |
|---|---|---|
| `SourceKind` | `String` | — |
| `Jan`, `Feb`, `Mar` | `Formula` | `m3` |
| `Density` | `Decimal` | `kg_per_m3` |
| `TotalTons` | `Formula` | `t` |

| RowKey | RowKind | Label |
|---|---|---|
| `C001` | `Item` | Surface |
| `C002` | `Item` | Ground |
| `C009` | `Balance` | TOTAL |

### Аркуш `Waste_08` — «Waste Report»

**Таблиця `Items`**, `LayoutKind = Static`, `RowMode = Dynamic`,
`MaxDynamicRows = 500`.

| Code | DataType | Unit | Прим. |
|---|---|---|---|
| `WasteType` | `Lookup` → `WASTE_TYPE` | — | |
| `Amount` | `Decimal(18,3)` | — | одиниця на рядок |
| `AmountUnit` | `Unit` | — | `DataType = Unit` (`R-A4`) |
| `AmountKg` | `Formula` | `kg` | приведення до спільної одиниці |

Ця таблиця існує саме для перевірки **одиниці на рядок** і **предикатних
діапазонів** — двох механізмів, які легко реалізувати неправильно.

---

<a id="registries"></a>
## 2. Реєстри

### `PERMIT` — темпоральний, з вкладеними речовинами

| Code | Display (en) | ValidFrom | ValidTo |
|---|---|---|---|
| `P-001` | Permit 001 | 2026-01-01 | 2026-06-30 |
| `P-002` | Permit 002 | 2026-04-01 | 2027-12-31 |
| `P-003` | Permit 003 | 2025-01-01 | 2025-12-31 |

Поля: `LimitM3` (`Decimal`, unit `m3`), `Authority` (`String`).

| Entry | LimitM3 | Authority |
|---|---:|---|
| `P-001` | 150000.000 | MinEco |
| `P-002` | 220000.000 | MinEco |
| `P-003` | 90000.000 | MinEco |

### `WATER_BODY` — каскад від `PERMIT`

| Code | Display | Parent (`PERMIT`) |
|---|---|---|
| `WB-A` | River A | `P-001` |
| `WB-B` | River B | `P-001` |
| `WB-C` | Reservoir C | `P-002` |
| `WB-W` | Aquifer West | `P-002` |

### `WASTE_TYPE`

| Code | Display | HazardClass |
|---|---|---|
| `W-01` | Drilling cuttings | 4 |
| `W-02` | Used oil | 3 |

### Одиниці, задіяні у фікстурах

Із seed (`02a#seed`), значення **не змінювати**:

| Code | Dimension | FactorToBase |
|---|---|---|
| `kg` | Mass | 1 (базова) |
| `t` | Mass | 1000 |
| `g` | Mass | 0.001 |
| `m3` | Volume | 1 (базова) |
| `l` | Volume | 0.001 |
| `kg_per_m3` | MassPerVolume | 1 |
| `s` | Time | 1 (базова) |
| `h` | Time | 3600 |
| `g_per_s` | MassFlow | 0.001 |

---

<a id="formulas"></a>
## 3. Формули

| # | Де | Вираз | Перевіряє |
|---|---|---|---|
| F1 | `Water_07.Main.Total` (колонка) | `SUM([Jan], [Feb], [Mar])` | базову агрегацію в межах рядка |
| F2 | `Water_07.Main.7009000` (рядок `Balance`) | `SUM([Main].[7001001:7001003].[{Month}]) + SUM([Main].[7001101:7001102].[{Month}])` | **діапазони** і `{Month}` |
| F3 | `Water_070.Rollup.C001` | `SUM([Water_07].[Main].[7001001:7001003].[{Month}])` | **крос-аркушний rollup** |
| F4 | `Water_070.Rollup.C002` | `SUM([Water_07].[Main].[7001101:7001102].[{Month}])` | те саме |
| F5 | `Water_070.Rollup.C009` | `[C001].[{Month}] + [C002].[{Month}]` | посилання на рядок тієї ж таблиці |
| F6 | `Water_070.Rollup.TotalTons` | `CONVERT(([Jan] + [Feb] + [Mar]) * [Density], 'kg', 't')` | **конверсію** і коефіцієнт зі стовпця документа |
| F7 | `Waste_08.Items.AmountKg` | `CONVERT([Amount], [AmountUnit], 'kg')` | **одиницю на рядок** |
| F8 | `Water_07.Main.Note` (валідація) | `IF([Total] > 0, TRUE, FALSE)` | булеву логіку |
| F9 | крос-період | `[Period:-1].[Main].[7009000].[Total]` | вихід за межі проєкту → `null` |
| F10 | предикат | `SUM([Items].[WHERE [WasteType] = 'W-01'].[AmountKg])` | **предикатний діапазон** |

⛔ У шаблоні щільність — **звичайний стовпець документа** `[Density]`, а не
константа методології. Було `CST_WATER_DENSITY`, і цей вираз не належав
жодному діалекту (`P-07`): `CST.` доступний лише методологіям, а `[Jan]`
недоступний їм — методологія не читає комірки документа. Шаблон, що знає про
константи методології, перестає бути переносним, і саме заради цього діалектів
два.

⚠ Коефіцієнт видимий у даних, а не захований у «конверсії м³ → т»: він
залежить від речовини й умов (`ФВ-16.5`).

---

<a id="data"></a>
## 4. Дані документа

Проєкт `ECR-2026-DEMO`, `PeriodStart = 2026-01-01`, `PeriodEnd = 2026-12-31`,
`TimeZoneId = Central Asia Standard Time`, політика `ECR-Standard`.

Документ `DOC-DEMO-01`, `BusinessKey = 'PLANT-A'`.

`Water_07.Main`, `PeriodKey = 202601` (січень):

| RowKey | Permit | WaterBody | Jan | Feb | Mar |
|---|---|---|---:|---:|---:|
| `7001001` | `P-001` | `WB-A` | 1200.500 | 1150.000 | 1300.250 |
| `7001002` | `P-001` | `WB-B` | 800.000 | *(порожня)* | 950.750 |
| `7001003` | `P-002` | `WB-C` | 2000.000 | 2100.500 | *(явна порожнеча)* |
| `7001101` | `P-002` | `WB-W` | 450.125 | 460.000 | 470.375 |
| `7001102` | `P-002` | `WB-W` | 300.000 | 310.000 | 320.000 |

> Рядок `7001002.Feb` — **комірки немає** (`DefaultValue` не заданий → `null`).
> Рядок `7001003.Mar` — **`IsEmpty = 1`** (явна порожнеча).
> Це два різні стани, і формула має обробити їх однаково (`null` у `SUM`
> поглинається), але **аудит і експорт мають їх розрізняти** (`R-B4`).

`Waste_08.Items` (динамічна таблиця, `PeriodKey = 202601`):

| RowKey (GUID) | WasteType | Amount | AmountUnit |
|---|---|---:|---|
| `a1b2c3d4e5f60718293a4b5c6d7e8f90` | `W-01` | 12.500 | `t` |
| `b2c3d4e5f60718293a4b5c6d7e8f90a1` | `W-01` | 3400.000 | `kg` |
| `c3d4e5f60718293a4b5c6d7e8f90a1b2` | `W-02` | 250.000 | `kg` |

---

<a id="expected"></a>
## 5. Очікувані значення

> Пораховані вручну. Округлення — `MidpointRounding.AwayFromZero` до 3 знаків.

### 5.1 `Water_07.Main.Total` (F1)

| RowKey | Обчислення | Очікуване |
|---|---|---:|
| `7001001` | 1200.500 + 1150.000 + 1300.250 | **3650.750** |
| `7001002` | 800.000 + `null` + 950.750 | **1750.750** |
| `7001003` | 2000.000 + 2100.500 + `null` (IsEmpty) | **4100.500** |
| `7001101` | 450.125 + 460.000 + 470.375 | **1380.500** |
| `7001102` | 300.000 + 310.000 + 320.000 | **930.000** |

### 5.2 `Water_07.Main.7009000` — `Balance` (F2)

| Колонка | Обчислення | Очікуване |
|---|---|---:|
| `Jan` | (1200.500 + 800.000 + 2000.000) + (450.125 + 300.000) | **4750.625** |
| `Feb` | (1150.000 + null + 2100.500) + (460.000 + 310.000) | **4020.500** |
| `Mar` | (1300.250 + 950.750 + null) + (470.375 + 320.000) | **3041.375** |
| `Total` | 4750.625 + 4020.500 + 3041.375 | **11812.500** |

### 5.3 `Water_070.Rollup` (F3–F5)

| RowKey | Jan | Feb | Mar |
|---|---:|---:|---:|
| `C001` (Surface) | **4000.500** | **3250.500** | **2251.000** |
| `C002` (Ground) | **750.125** | **770.000** | **790.375** |
| `C009` (TOTAL) | **4750.625** | **4020.500** | **3041.375** |

> `C009` має збігатися з `7009000` порядково — це **інваріант**, а не збіг:
> два різні шляхи обчислення того самого числа. Розбіжність означає помилку
> в графі залежностей.

### 5.4 `Water_070.Rollup.C009.TotalTons` (F6)

```
(4750.625 + 4020.500 + 3041.375) m3
  = 11812.500 m3
  × [Density] = 1000 kg_per_m3 → 11812500.000 kg
  CONVERT(kg → t): / 1000      → 11812.500 t
```

Очікуване: **11812.500** `t`.

### 5.5 `Waste_08.Items.AmountKg` (F7) і предикат (F10)

| RowKey | Amount | Unit | AmountKg |
|---|---:|---|---:|
| `a1b2…` | 12.500 | `t` | **12500.000** |
| `b2c3…` | 3400.000 | `kg` | **3400.000** |
| `c3d4…` | 250.000 | `kg` | **250.000** |

`SUM([Items].[WHERE [WasteType] = 'W-01'].[AmountKg])` = 12500.000 + 3400.000 =
**15900.000**.

### 5.6 Крос-період (F9)

`PeriodKey = 202601` — перший період проєкту, тому
`[Period:-1].[Main].[7009000].[Total]` = **`null`**, **не помилка**.

Для `PeriodKey = 202602` те саме посилання дає значення січня.

---

<a id="access"></a>
## 6. Матриця доступу

Користувачі:

| UserName | Provider | Роль | Гранти |
|---|---|---|---|
| `AGS\ivanov` | `Windows` | `DataEntry` | `Project:1 = Write` |
| `petrenko` | `Local` | `DataEntry` | `Project:1 = Write`, **`Column:Total = None` (IsDeny)** |
| `AGS\shevchuk` | `Windows` | `Approver` | `Project:1 = Approve` |
| `viewer1` | `Local` | `Viewer` | `Project:1 = Read` |

Правило періодів шаблону: аркуш `Waste_08` доступний **лише в періодах 1–3**
(`FromSequence = 1`, `ToSequence = 3`, `OnOutOfWindow = ReadOnly`).

Очікувані рішення `IAccessDecisionService`:

| # | Користувач | Дія | Період | Очікування |
|---|---|---|---|---|
| A1 | `ivanov` | запис `Main.7001001.Jan` | `202601`, `Open` | `Allow` |
| A2 | `petrenko` | запис `Main.7001001.Jan` | `202601`, `Open` | `Allow` |
| A3 | `petrenko` | запис `Main.7001001.Total` | `202601`, `Open` | `Deny(ColumnReadOnly)` — колонка `Formula` |
| A4 | `viewer1` | запис `Main.7001001.Jan` | `202601`, `Open` | `Deny(NoGrant)` |
| A5 | `ivanov` | запис | `202601`, `Grace` | `Allow`, `IsLateEdit = true` |
| A6 | `ivanov` | запис | `202601`, `Closed` | `Deny(PeriodClosed)` |
| A7 | `shevchuk` (`Approve`) | запис | `202601`, `Closed` | `Deny(PeriodClosed)` — **навіть із `Manage`** |
| A8 | `ivanov` | запис `Waste_08` | `202604`, `Open` | `Deny(OutOfAccessWindow)` |
| A9 | `ivanov` | запис | `202601`, документ `Submitted` | `Deny(DocumentSubmitted)` |
| A10 | `petrenko` | запис `Main.7001001.Note` | `202601`, `Open` | `Allow` — `IsDeny` лише на `Total` |
| A11 | `ivanov` | `Submit` аркуша `Water_07` | `202601` | `Deny(NoGrant)` — потрібен рівень `Submit` |
| A12 | `shevchuk` | `Approve` | `202601`, `Submitted` | `Allow` |

> **A7 — ключовий сценарій.** Закритий період блокує запис усім, включно з
> найвищим грантом. Якщо він проходить — модель доступу зламана.

---

<a id="calc"></a>
## 7. Методологія і розрахунок

Методологія `WATER_DISCHARGE`, версія `1.0.0.0`, `Level = Configuration`,
`NumericMode = Legacy`, `CalendarMode = Actual`.

Речовини (реєстр `SUBSTANCE`): `SUB-COD` (ХСК), `SUB-TSS` (завислі речовини).

Константи:

| Code | Category | Value | Unit | Substance |
|---|---|---:|---|---|
| `EF` | `default` | 0.250 | `kg_per_m3` | `SUB-COD` |
| `EF` | `default` | 0.080 | `kg_per_m3` | `SUB-TSS` |
| `CST_WATER_DENSITY` | — | 1000.000 | `kg_per_m3` | — |

Виходи:

| Code | Unit |
|---|---|
| `tons` | `t` |
| `gsec` | `g_per_s` |

Формули:

```
!Volume   = @Jan + @Feb + @Mar                                  -- m3
!MassKg   = !Volume * CST.EF                                    -- kg
 tons     = CONVERT(!MassKg, 'kg', 't')
 gsec     = CONVERT(!MassKg, 'kg', 'g') / [Period].Seconds
```

### Очікувані результати для рядка `7001001`

`!Volume` = 3650.750 m³

**`SUB-COD`** (EF = 0.250):
```
!MassKg = 3650.750 × 0.250            = 912.6875 kg
tons    = 912.6875 / 1000             = 0.9126875 t   → 0.912688 (6 знаків)
```

**`SUB-TSS`** (EF = 0.080):
```
!MassKg = 3650.750 × 0.080            = 292.06 kg
tons    = 292.06 / 1000               = 0.29206 t     → 0.292060
```

**`gsec` для `SUB-COD`, `PeriodKey = 202601`, `CalendarMode = Actual`:**
```
[Period].Seconds = 31 днів × 86400    = 2678400 s
gsec = 912.6875 × 1000 / 2678400      = 912687.5 / 2678400
                                       = 0.340758... g/s → 0.340758
```

> ⚠ Було `0.340774` — **помилка арифметики в пакеті** (`P-06`). Жодний
> дільник її не дає: `0.340774` вимагав би `30.9986` доби. Підтверджено
> власником золотого набору; виправлено і тут, і в `water-demo.json`.
> Відношення режимів лишається точним — `31/30`.

### Той самий розрахунок із `CalendarMode = Fixed360`

```
[Period].Seconds = 30 × 86400         = 2592000 s
gsec = 912687.5 / 2592000             = 0.352117... g/s → 0.352117
```

> **Різниця 3.3% при однакових вхідних даних.** Це і є та пастка, заради якої
> існує `CalendarMode` (`D-78`): без явного режиму розбіжність виглядає як
> помилка формули, а не як різниця календарної конвенції. Тест на обидва
> режими обов'язковий.

---

<a id="edge"></a>
## 8. Крайові випадки

| # | Випадок | Вхід | Очікування |
|---|---|---|---|
| E1 | `SUM` порожньої множини | усі комірки `null` | `0`, не `null` |
| E2 | `AVERAGE` порожньої множини | те саме | `null`, не `0` |
| E3 | `null` у множенні | `null * 0` | `null`, **не `0`** |
| E4 | Ділення на нуль | `[Jan] / 0` | `#DIV/0`, не виняток |
| E5 | Ділення на `null` | `[Jan] / null` | `#DIV/0` |
| E6 | Поширення помилки | `#DIV/0 + 1` | `#DIV/0` |
| E7 | `IFERROR` перехоплює помилку | `IFERROR(1/0, -1)` | `-1` |
| E8 | `IFERROR` **не** перехоплює `null` | `IFERROR(null, -1)` | `null` |
| E9 | Конкатенація з `null` | `null & 'x'` | `'x'` |
| E10 | Порівняння з `null` | `null > 1` | `null` |
| E11 | Рівність `null` | `null = null` | `TRUE` |
| E12 | Округлення .5 | `ROUND(2.5, 0)` | `3`, **не 2** (не банківське) |
| E13 | Округлення від'ємного | `ROUND(-2.5, 0)` | `-3` |
| E14 | Конверсія різних розмірностей | `CONVERT(1, 'kg', 'm3')` | `#UNIT` / `ECR-UOM-0422` |
| E15 | Конверсія з offset | `CONVERT(0, 'degC', 'K')` | `273.15` |
| E16 | Крос-період за межу | `[Period:-1]` у першому періоді | `null` |
| E17 | Цикл у графі | `A = B + 1`, `B = A + 1` | `ECR-TMPL-4221` при `Publish` |
| E18 | Тип у порівнянні | `[Jan] > 'text'` | помилка при `Publish` |
| E19 | Зміна `Ordinal` після `Publish` | переставити `7001002` і `7001003` | результат F2 **не змінюється** |
| E20 | Дублікат `RowKey` | створити рядок з наявним ключем | `ECR-ROW-0409` |
| E21 | Одиниця на рядок без `CONVERT` | `SUM([Items].[Amount])` де одиниці різні | `ECR-TMPL-4223` при `Publish` |
| E22 | Порожня комірка з `DefaultValue` | комірки немає, `DefaultValue = '0'` | `0` |
| E23 | `IsEmpty` ігнорує `DefaultValue` | `IsEmpty = 1`, `DefaultValue = '0'` | `null` |
| E24 | `decimal` без втрати точності | `0.1 + 0.2` | рівно `0.3` |
| E25 | Понад `Scale` при **вставці** | `1200.5006` у `Jan` (`Decimal(18,3)`) | `1200.501` + позначка «округлено» (D-109) |
| E26 | Понад `Scale` через **API** | той самий `1200.5006` у `PATCH` | `ECR-CELL-0422`, комірка не змінюється |
| E27 | Округлення при вставці — від нуля | `1200.5005` → `Scale = 3` | `1200.501`, **не** `1200.500` |
| E28 | Вставка **на межі** `Scale` | `1200.500` у `Decimal(18,3)` | `1200.500`, позначки округлення **немає** |
| E29 | `Sequence` поза діапазоном | створити період із `Sequence = 13` | `ECR-PRD-4224` (D-108) |
| E30 | Зміна поясу після періоду | `TimeZoneId` у проєкті з відкритим періодом | `ECR-PRD-0409` (ФВ-1.1a) |

> **E25–E28 — це фікстура під `D-109`**, і головне в ній — E28. Правило легко
> реалізувати так, що позначка «округлено» з'являється на кожному значенні з
> дробовою частиною, а не лише на змінених. Тоді лічильник «округлено 12
> значень» перестане щось означати, і користувач припинить на нього дивитися.

> **E24 — причина, чому `float` заборонений.** У `double` `0.1 + 0.2` дає
> `0.30000000000000004`, і на мільйонах рядків це перетворюється на розбіжність
> зі звітом регулятора.

---

<a id="json"></a>
## 9. Машинний формат

Фікстури завантажуються в тестах із цього JSON. Файл створюється на ПК-2 як
`tests/Ecr.TestKit/Fixtures/water-demo.json` (див. `06-tests.md`).

```json
{
  "template": {
    "code": "WATER_DEMO",
    "version": "1.0.0.0",
    "periodKind": "Monthly",
    "sheets": [
      {
        "code": "Water_07",
        "nameL10n": { "en": "Water Report", "ru": "Отчёт по воде", "kz": "Су есебі" },
        "ordinal": 1,
        "tables": [
          {
            "code": "Main",
            "layoutKind": "MonthsInColumns",
            "rowMode": "Fixed",
            "columns": [
              { "code": "RowLabel",  "dataType": "String",  "ordinal": 1, "isReadOnly": true },
              { "code": "Permit",    "dataType": "Lookup",  "ordinal": 2, "lookupRegistry": "PERMIT" },
              { "code": "WaterBody", "dataType": "Lookup",  "ordinal": 3, "lookupRegistry": "WATER_BODY", "cascadeFrom": "Permit" },
              { "code": "Jan",       "dataType": "Decimal", "ordinal": 4, "precision": 18, "scale": 3, "unit": "m3", "isMonthColumn": true, "monthNumber": 1 },
              { "code": "Feb",       "dataType": "Decimal", "ordinal": 5, "precision": 18, "scale": 3, "unit": "m3", "isMonthColumn": true, "monthNumber": 2 },
              { "code": "Mar",       "dataType": "Decimal", "ordinal": 6, "precision": 18, "scale": 3, "unit": "m3", "isMonthColumn": true, "monthNumber": 3 },
              { "code": "Total",     "dataType": "Formula", "ordinal": 7, "unit": "m3", "isReadOnly": true },
              { "code": "Note",      "dataType": "String",  "ordinal": 8 }
            ],
            "rows": [
              { "rowKey": "7001000", "ordinal": 1, "rowKind": "Group",   "labelL10n": { "en": "Surface water intake" } },
              { "rowKey": "7001001", "ordinal": 2, "rowKind": "Item",    "labelL10n": { "en": "River A" },     "parent": "7001000" },
              { "rowKey": "7001002", "ordinal": 3, "rowKind": "Item",    "labelL10n": { "en": "River B" },     "parent": "7001000" },
              { "rowKey": "7001003", "ordinal": 4, "rowKind": "Item",    "labelL10n": { "en": "Reservoir C" }, "parent": "7001000" },
              { "rowKey": "7001100", "ordinal": 5, "rowKind": "Group",   "labelL10n": { "en": "Groundwater intake" } },
              { "rowKey": "7001101", "ordinal": 6, "rowKind": "Item",    "labelL10n": { "en": "Well 1" },      "parent": "7001100" },
              { "rowKey": "7001102", "ordinal": 7, "rowKind": "Item",    "labelL10n": { "en": "Well 2" },      "parent": "7001100" },
              { "rowKey": "7009000", "ordinal": 8, "rowKind": "Balance", "labelL10n": { "en": "TOTAL INTAKE" } },
              { "rowKey": "7009900", "ordinal": 9, "rowKind": "Note",    "labelL10n": { "en": "Methodology note" } }
            ],
            "formulas": [
              { "id": "F1", "scope": "Column", "column": "Total",
                "expression": "SUM([Jan], [Feb], [Mar])" },
              { "id": "F2", "scope": "Row", "row": "7009000",
                "expression": "SUM([Main].[7001001:7001003].[{Month}]) + SUM([Main].[7001101:7001102].[{Month}])" }
            ]
          }
        ]
      },
      {
        "code": "Water_070",
        "nameL10n": { "en": "Water consolidation" },
        "ordinal": 2,
        "tables": [
          {
            "code": "Rollup",
            "layoutKind": "MonthsInColumns",
            "rowMode": "Fixed",
            "columns": [
              { "code": "SourceKind", "dataType": "String",  "ordinal": 1 },
              { "code": "Jan",        "dataType": "Formula", "ordinal": 2, "unit": "m3", "isMonthColumn": true, "monthNumber": 1 },
              { "code": "Feb",        "dataType": "Formula", "ordinal": 3, "unit": "m3", "isMonthColumn": true, "monthNumber": 2 },
              { "code": "Mar",        "dataType": "Formula", "ordinal": 4, "unit": "m3", "isMonthColumn": true, "monthNumber": 3 },
              { "code": "TotalTons",  "dataType": "Formula", "ordinal": 5, "unit": "t" }
            ],
            "rows": [
              { "rowKey": "C001", "ordinal": 1, "rowKind": "Item",    "labelL10n": { "en": "Surface" } },
              { "rowKey": "C002", "ordinal": 2, "rowKind": "Item",    "labelL10n": { "en": "Ground" } },
              { "rowKey": "C009", "ordinal": 3, "rowKind": "Balance", "labelL10n": { "en": "TOTAL" } }
            ],
            "formulas": [
              { "id": "F3", "scope": "Row", "row": "C001",
                "expression": "SUM([Water_07].[Main].[7001001:7001003].[{Month}])" },
              { "id": "F4", "scope": "Row", "row": "C002",
                "expression": "SUM([Water_07].[Main].[7001101:7001102].[{Month}])" },
              { "id": "F5", "scope": "Row", "row": "C009",
                "expression": "[C001].[{Month}] + [C002].[{Month}]" },
              { "id": "F6", "scope": "Cell", "row": "C009", "column": "TotalTons",
                "expression": "CONVERT(([Jan] + [Feb] + [Mar]) * [Density], 'kg', 't')" }
            ]
          }
        ]
      },
      {
        "code": "Waste_08",
        "nameL10n": { "en": "Waste Report" },
        "ordinal": 3,
        "tables": [
          {
            "code": "Items",
            "layoutKind": "Static",
            "rowMode": "Dynamic",
            "maxDynamicRows": 500,
            "columns": [
              { "code": "WasteType",  "dataType": "Lookup",  "ordinal": 1, "lookupRegistry": "WASTE_TYPE" },
              { "code": "Amount",     "dataType": "Decimal", "ordinal": 2, "precision": 18, "scale": 3 },
              { "code": "AmountUnit", "dataType": "Unit",    "ordinal": 3 },
              { "code": "AmountKg",   "dataType": "Formula", "ordinal": 4, "unit": "kg", "isReadOnly": true }
            ],
            "rows": [],
            "formulas": [
              { "id": "F7",  "scope": "Column", "column": "AmountKg",
                "expression": "CONVERT([Amount], [AmountUnit], 'kg')" }
            ]
          }
        ]
      }
    ],
    "periodAccessRules": [
      { "sheet": "Waste_08", "fromSequence": 1, "toSequence": 3, "onOutOfWindow": "ReadOnly" }
    ]
  },

  "registries": [
    {
      "code": "PERMIT",
      "isTemporal": true,
      "fields": [
        { "code": "LimitM3",   "dataType": "Decimal", "unit": "m3" },
        { "code": "Authority", "dataType": "String" }
      ],
      "entries": [
        { "code": "P-001", "displayL10n": { "en": "Permit 001" }, "validFrom": "2026-01-01", "validTo": "2026-06-30",
          "values": { "LimitM3": "150000.000", "Authority": "MinEco" } },
        { "code": "P-002", "displayL10n": { "en": "Permit 002" }, "validFrom": "2026-04-01", "validTo": "2027-12-31",
          "values": { "LimitM3": "220000.000", "Authority": "MinEco" } },
        { "code": "P-003", "displayL10n": { "en": "Permit 003" }, "validFrom": "2025-01-01", "validTo": "2025-12-31",
          "values": { "LimitM3": "90000.000", "Authority": "MinEco" } }
      ]
    },
    {
      "code": "WATER_BODY",
      "isTemporal": false,
      "fields": [],
      "entries": [
        { "code": "WB-A", "displayL10n": { "en": "River A" },       "parent": "P-001" },
        { "code": "WB-B", "displayL10n": { "en": "River B" },       "parent": "P-001" },
        { "code": "WB-C", "displayL10n": { "en": "Reservoir C" },   "parent": "P-002" },
        { "code": "WB-W", "displayL10n": { "en": "Aquifer West" },  "parent": "P-002" }
      ]
    },
    {
      "code": "WASTE_TYPE",
      "isTemporal": false,
      "fields": [ { "code": "HazardClass", "dataType": "Int" } ],
      "entries": [
        { "code": "W-01", "displayL10n": { "en": "Drilling cuttings" }, "values": { "HazardClass": "4" } },
        { "code": "W-02", "displayL10n": { "en": "Used oil" },          "values": { "HazardClass": "3" } }
      ]
    },
    {
      "code": "SUBSTANCE",
      "isTemporal": false,
      "fields": [],
      "entries": [
        { "code": "SUB-COD", "displayL10n": { "en": "COD" } },
        { "code": "SUB-TSS", "displayL10n": { "en": "Total suspended solids" } }
      ]
    }
  ],

  "project": {
    "code": "ECR-2026-DEMO",
    "periodStart": "2026-01-01",
    "periodEnd": "2026-12-31",
    "periodKind": "Monthly",
    "timeZoneId": "Central Asia Standard Time",
    "periodPolicy": "ECR-Standard"
  },

  "document": {
    "businessKey": "PLANT-A",
    "periodKey": 202601,
    "tables": {
      "Water_07.Main": [
        { "rowKey": "7001001", "cells": { "Permit": "P-001", "WaterBody": "WB-A", "Jan": "1200.500", "Feb": "1150.000", "Mar": "1300.250" } },
        { "rowKey": "7001002", "cells": { "Permit": "P-001", "WaterBody": "WB-B", "Jan": "800.000",  "Mar": "950.750" } },
        { "rowKey": "7001003", "cells": { "Permit": "P-002", "WaterBody": "WB-C", "Jan": "2000.000", "Feb": "2100.500", "Mar": { "isEmpty": true } } },
        { "rowKey": "7001101", "cells": { "Permit": "P-002", "WaterBody": "WB-W", "Jan": "450.125",  "Feb": "460.000",  "Mar": "470.375" } },
        { "rowKey": "7001102", "cells": { "Permit": "P-002", "WaterBody": "WB-W", "Jan": "300.000",  "Feb": "310.000",  "Mar": "320.000" } }
      ],
      "Waste_08.Items": [
        { "rowKey": "a1b2c3d4e5f60718293a4b5c6d7e8f90", "cells": { "WasteType": "W-01", "Amount": "12.500",   "AmountUnit": "t"  } },
        { "rowKey": "b2c3d4e5f60718293a4b5c6d7e8f90a1", "cells": { "WasteType": "W-01", "Amount": "3400.000", "AmountUnit": "kg" } },
        { "rowKey": "c3d4e5f60718293a4b5c6d7e8f90a1b2", "cells": { "WasteType": "W-02", "Amount": "250.000",  "AmountUnit": "kg" } }
      ]
    }
  },

  "methodology": {
    "code": "WATER_DISCHARGE",
    "version": "1.0.0.0",
    "level": "Configuration",
    "numericMode": "Legacy",
    "calendarMode": "Actual",
    "substances": ["SUB-COD", "SUB-TSS"],
    "constants": [
      { "code": "EF", "category": "default", "value": "0.250", "unit": "kg_per_m3", "substance": "SUB-COD" },
      { "code": "EF", "category": "default", "value": "0.080", "unit": "kg_per_m3", "substance": "SUB-TSS" },
      { "code": "CST_WATER_DENSITY", "value": "1000.000", "unit": "kg_per_m3" }
    ],
    "outputs": [
      { "code": "tons", "unit": "t" },
      { "code": "gsec", "unit": "g_per_s" }
    ],
    "formulas": [
      { "code": "Volume", "expression": "@Jan + @Feb + @Mar" },
      { "code": "MassKg", "expression": "!Volume * CST.EF" },
      { "code": "tons",   "expression": "CONVERT(!MassKg, 'kg', 't')" },
      { "code": "gsec",   "expression": "CONVERT(!MassKg, 'kg', 'g') / [Period].Seconds" }
    ]
  },

  "expected": {
    "Water_07.Main.Total": {
      "7001001": "3650.750", "7001002": "1750.750", "7001003": "4100.500",
      "7001101": "1380.500", "7001102": "930.000"
    },
    "Water_07.Main.7009000": {
      "Jan": "4750.625", "Feb": "4020.500", "Mar": "3041.375", "Total": "11812.500"
    },
    "Water_070.Rollup": {
      "C001": { "Jan": "4000.500", "Feb": "3250.500", "Mar": "2251.000" },
      "C002": { "Jan": "750.125",  "Feb": "770.000",  "Mar": "790.375"  },
      "C009": { "Jan": "4750.625", "Feb": "4020.500", "Mar": "3041.375", "TotalTons": "11812.500" }
    },
    "Waste_08.Items.AmountKg": {
      "a1b2c3d4e5f60718293a4b5c6d7e8f90": "12500.000",
      "b2c3d4e5f60718293a4b5c6d7e8f90a1": "3400.000",
      "c3d4e5f60718293a4b5c6d7e8f90a1b2": "250.000"
    },
    "Waste_08.PredicateSum_W01": "15900.000",
    "calculation": {
      "7001001": {
        "SUB-COD": { "tons": "0.912688", "gsec_Actual": "0.340758", "gsec_Fixed360": "0.352117" },
        "SUB-TSS": { "tons": "0.292060" }
      }
    }
  }
}
```

> **Правило використання.** Тести читають цей JSON, а не хардкодять числа.
> Якщо очікуване значення треба змінити — це або помилка у фікстурі (тоді
> `questions.md` і зупинка), або зміна вимоги (тоді спершу `tz/10-decisions.md`).
> Підганяти очікування під те, що повернув код, **заборонено**
> (`08-workflow.md` §4).
