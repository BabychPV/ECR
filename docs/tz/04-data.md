# 04. Модель даних

> Повний DDL — [`../build/02a-db-schema.md`](../build/02a-db-schema.md).
> Тут — рішення і те, чому вони саме такі.

## 4.1 Схеми

| Схема | Призначення |
|---|---|
| `cfg` | метадані: шаблони, версії, аркуші, таблиці, колонки, рядки, стилі, формули, правила |
| `doc` | дані: проєкти, періоди, документи, екземпляри таблиць, рядки, комірки |
| `reg` | реєстри (довідники): визначення, поля, записи, значення |
| `uom` | одиниці вимірювання: розмірності, одиниці, конверсії |
| `calc` | методології, версії, константи, результати, трейс, прив'язки |
| `sec` | користувачі, ролі, права, гранти, призначення |
| `aud` | аудит комірок, структури, доступу |
| `itg` | джерела, мапінги, розклади, журнал збору, покриття |
| `ext` | сире зовнішнє: `ext.AfSnapshot`, `ext.EntityFieldMap` — єдине місце, де дозволено `Af*` |
| `rpt` | звітний контракт для SSRS: зрізи і вʼюхи `rpt.v_*` |
| `arc` | архів: історичні партиції на дешевому сховищі |
| `sys` | системне: мови, налаштування, версія схеми, журнал міграцій |
| `job` | стан фонових задач (таблиці планувальника) |

## 4.2 Ключі даних документа

Це найважливіше рішення фізичної моделі (D-84).

```sql
CREATE TABLE doc.TableRow (
    PeriodKey        int           NOT NULL,
    Id               bigint        NOT NULL,   -- із SEQUENCE, не IDENTITY
    TableInstanceId  bigint        NOT NULL,
    RowKey           varchar(100)  NOT NULL,
    ParentRowKey     varchar(100)  NULL,
    Ordinal          int           NOT NULL,
    IsDeleted        bit           NOT NULL CONSTRAINT DF_TableRow_IsDeleted DEFAULT 0,
    CreatedAt        datetime2(3)  NOT NULL,
    CreatedByUserId  int           NOT NULL,
    ModifiedAt       datetime2(3)  NOT NULL,
    ModifiedByUserId int           NOT NULL,
    CONSTRAINT PK_TableRow PRIMARY KEY CLUSTERED (PeriodKey, Id),
    CONSTRAINT UQ_TableRow_Key UNIQUE (PeriodKey, TableInstanceId, RowKey)
) ON PS_ByPeriod(PeriodKey);

CREATE TABLE doc.CellValue (
    PeriodKey     int            NOT NULL,
    TableRowId    bigint         NOT NULL,
    ColumnDefId   int            NOT NULL,
    TableDefId    int            NOT NULL,
    ValueDecimal  decimal(28,10) NULL,
    ValueString   nvarchar(400)  NULL,
    ValueDate     datetime2(3)   NULL,
    ValueBool     bit            NULL,
    ValueRegistryEntryId bigint  NULL,
    ValueUnitId   int            NULL,   -- D-87: одиниця на рядок
    IsEmpty       bit            NOT NULL CONSTRAINT DF_CellValue_IsEmpty DEFAULT 0,
    IsCalculated  bit            NOT NULL CONSTRAINT DF_CellValue_IsCalculated DEFAULT 0,
    RowVersion    rowversion     NOT NULL,
    CONSTRAINT PK_CellValue PRIMARY KEY CLUSTERED (PeriodKey, TableRowId, ColumnDefId),
    CONSTRAINT FK_CellValue_Row FOREIGN KEY (PeriodKey, TableRowId)
        REFERENCES doc.TableRow (PeriodKey, Id),
    CONSTRAINT FK_CellValue_Column FOREIGN KEY (TableDefId, ColumnDefId)
        REFERENCES cfg.ColumnDef (TableDefId, Id)
) ON PS_ByPeriod(PeriodKey);
```

**Чому без сурогатного `Id` у `CellValue`.** При 108 млн рядків сурогат — це
+8 байтів на рядок і ще один унікальний індекс, який ніхто не використовує для
пошуку: звертаються завжди за трійкою. Економія ≈0.9 ГБ/рік плюс менший розмір
некластерних індексів.

**Чому `SEQUENCE`, а не `IDENTITY`.** `SqlBulkCopy` має завантажити рядки і їхні
комірки **одним проходом**. Для цього `TableRow.Id` мусить бути відомий **до**
вставки. `IDENTITY` віддає значення лише після — це другий прохід і подвійна
вартість імпорту.

**Чому складені FK.** `(PeriodKey, TableRowId)` і `(TableDefId, ColumnDefId)`
гарантують: комірка не може посилатися на рядок з іншого періоду, а колонка —
на іншу таблицю. Без цього узгодженість довелося б перевіряти джобом раз на
добу.

## 4.3 `PeriodKey`

```
PeriodKey = Year * 100 + Sequence
```

| `PeriodKind` | `Sequence` | Приклад |
|---|---|---|
| `Monthly` | 1…12 | `202603` = березень 2026 (**збігається** з `YYYYMM`) |
| `Quarterly` | 1…4 | `202602` = II квартал 2026 |
| `Yearly` | 1 | `202601` = 2026 рік |
| `Custom` | задається вручну | унікальність `(ProjectId, Year, Sequence)` |

> ⚠️ **Виводити місяць із `PeriodKey` арифметикою заборонено.** Збіг із `YYYYMM`
> справедливий лише для `Monthly`. Місяць береться з `ColumnDef.MonthNumber` або
> з меж періоду.

Партиційна функція — по `PeriodKey`, права межа, партиція на період.
Партиції на наступний рік створює `arc.usp_EnsurePartitions` завчасно
(SQL Agent, D-66).

## 4.4 Партиціонування і рівні зберігання

| Рівень | Що | Де | Індекс |
|---|---|---|---|
| Гарячий | поточний + попередній рік | `FG_HOT` | rowstore |
| Теплий | 2–3 роки | `FG_WARM` | rowstore + `PAGE` compression |
| Архів | старше | `FG_ARCHIVE` | clustered columnstore |

Дані **не видаляються** — вони переїжджають (D-25).

## 4.5 Архівація

`SWITCH PARTITION` тут **неможливий**: він вимагає однакової файлової групи і
сумісної структури індексів, а в нас різні файлові групи і різний тип індексу
(rowstore → columnstore). Це помилка вихідного ТЗ, виправлена D-23.

Реальна процедура (`arc.usp_ArchiveYear`):

1. `INSERT … SELECT … WITH (TABLOCK)` у таблицю архіву (мінімальне журналювання).
2. Звірка **трьох контрольних сум** джерела й приймача:
   `COUNT_BIG(*)`, `CHECKSUM_AGG(BINARY_CHECKSUM(*))`, `SUM(ValueDecimal)`.
3. Лише при збігу всіх трьох — `TRUNCATE TABLE … WITH (PARTITIONS (n))`.
4. Крок фіксується в `arc.ArchiveRun`; операція **відновлювана** з місця збою.

Зупинка системи не потрібна — потрібне вікно низької активності (D-24).
Дані джерела не видаляються до збігу сум (D-24).

## 4.5a Одиниці вимірювання (`uom`)

```sql
CREATE TABLE uom.Dimension (
    Id          int IDENTITY PRIMARY KEY,
    Code        varchar(64)  NOT NULL UNIQUE,   -- Mass, Volume, Time, MassFlow
    BaseUnitId  int          NULL               -- FK додається після uom.Unit
);

CREATE TABLE uom.Unit (
    Id           int IDENTITY PRIMARY KEY,
    Code         varchar(64)    NOT NULL UNIQUE, -- kg, t, m3, g_per_s
    DimensionId  int            NOT NULL REFERENCES uom.Dimension(Id),
    FactorToBase decimal(38,18) NOT NULL,
    IsBase       bit            NOT NULL DEFAULT 0
);

CREATE TABLE uom.Conversion (
    Id           int IDENTITY PRIMARY KEY,
    FromUnitId   int            NOT NULL REFERENCES uom.Unit(Id),
    ToUnitId     int            NOT NULL REFERENCES uom.Unit(Id),
    Factor       decimal(38,18) NOT NULL,
    CONSTRAINT UQ_Conv UNIQUE (FromUnitId, ToUnitId),
    CONSTRAINT CK_Conv_SameDimension
        CHECK (uom.fn_SameDimension(FromUnitId, ToUnitId) = 1)
);
```

`uom.fn_SameDimension` — скалярна функція з `WITH SCHEMABINDING`, яка робить
D-74 **фізично неможливим до порушення**: конверсію між кілограмами і метрами
не вставиш навіть напряму в БД.

**Контекстні коефіцієнти сюди не потрапляють** (D-75). Щільність води — не
конверсія `m3 → kg`: вона залежить від температури, а для нафти взагалі інша.
Такі величини живуть у `calc.MethodologyConstant` із власною одиницею,
темпоральністю і версійністю.

## 4.6 Обсяг

| Сутність | Рядків/рік | Через 5 років |
|---|---:|---:|
| `doc.CellValue` | ~21.6 млн | ~108 млн |
| `aud.CellChange` | ~43 млн | ~216 млн |
| `calc.CalculationResult` | ~4 млн | ~20 млн |
| `ext.AfSnapshot` | залежить від S-2 | — |

Розрахунок від D-62 (≥200 документів/рік, цільовий сценарій 300, запас 500).

## 4.7 Транзакції

- `READ_COMMITTED_SNAPSHOT` — **обов'язковий** (D-29). Без нього пік
  «останнього дня періоду» впирається в блокування читачів письменниками.
- Рівень `SNAPSHOT` не використовується.
- Довгих транзакцій немає ніде: batch-PATCH — одна коротка транзакція,
  перерахунок — порційно, архівація — покроково з фіксацією.

## 4.8 Retention

**Нічого не затирається** (D-25). Автоматичного видалення первинних і
журнальних даних немає ніде в системі. Обсяг керується рівнями зберігання
(§4.4), а не строками.

Прибирати можна лише **похідні артефакти** (D-71):

| Можна прибирати | Не можна ніколи |
|---|---|
| `rpt.ReportSnapshot`, крім `IsSubmitted` і `IsCurrent` | `doc.*` |
| кеші | `aud.*` |
| тимчасові файли експорту | `calc.CalculationResult` і записаний трейс |
| | `ext.*`, `itg.*`, `arc.*` |

Аудит партиціонується по `ChangedAt` і архівується **щомісячно** (D-26): він
удвічі більший за дані, тому річний цикл тут не годиться.

Обсяг трейсу керується **рівнем** (`TraceLevel`: `Off` / `ErrorsOnly` /
`Full`), а не строком зберігання (D-27). Відтворюваність забезпечує
іммутабельний зріз вхідних даних при поданні (ФВ-5.7), а не збережені кроки.
