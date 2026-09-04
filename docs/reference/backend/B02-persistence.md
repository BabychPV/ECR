# B02 — Зберігання: фізична модель, партиціонування, архівація

> Закриває §11 [13] пп. **1** (модель зберігання комірок), **2** (партиціонування),
> **11** (EF Core + тригери), **12** (вирівняні індекси).
> Це найдорожче рішення в проєкті: його наслідки видно на 108 млн рядків,
> і воно погано переграється після Етапу 2.

---

## 1. Рішення: нормалізована модель — базова, з явним гейтом (BR-01)

**Базовий варіант — нормалізований `doc.CellValue`.** JSON-гібрид залишається
запасним і вмикається лише за результатом заміру Етапу 0 (§7).

### Чому нормалізована, а не одразу JSON

| Аргумент | Пояснення |
|----------|-----------|
| Складений FK неможливий у JSON | інваріант «комірка не потрапить у чужу колонку» (ТЗ §5.5, принцип 6) реалізується FK `(TableDefId, ColumnDefId)`. У JSON це лише перевірка в коді — тобто рано чи пізно баг |
| Типізація | `ValueNumeric decimal(28,10)` дає індекси, `SUM`, `WHERE > 100`. `OPENJSON` дає те саме лише через computed-колонки, тобто через ту саму нормалізацію, тільки складніше |
| `ValueRegistryEntryId` як FK | ФВ-8.7 («запис довідника не видаляється, якщо на нього посилаються дані») перевіряється базою, а не задачею вночі |
| Аудит по комірці | `aud.CellChange` природно лягає на комірку; у JSON-моделі diff двох документів треба рахувати самим |
| Порожні комірки не матеріалізуються | головна економія ([14] §3.1) працює **тільки** в нормалізованій моделі: у JSON рядок зберігається цілком |

### Чому гейт усе одно потрібен

108 млн рядків на рік — це не «багато для SQL Server», це «багато для наївного
запиту». Якщо `ReadSliceAsync` на реальному обсязі не вкладається в **600 мс**
([14] §2, розкладка бюджету), треба переходити на гібрид. Критерій — числовий,
не «на око».

### Як зроблено так, щоб перехід був дешевим

Уся фізика — за портом `ICellStore` (B01 §3.2). Логічна модель («рядок = сутність,
колонка = типізоване поле») **однакова в обох варіантах** — це прямо зафіксовано
в [07] §4. Отже, перехід = друга реалізація порту + міграційний скрипт, а не
переписування use-cases.

Запасний варіант описаний у §8, щоб рішення можна було прийняти за день, а не
проєктувати з нуля під тиском.

---

## 2. Ключі і кластерні індекси (BR-02)

### 2.1 Проблема з `Id bigint PK` у [07]

Схема в [07] §4 пропонує `doc.CellValue.Id bigint PK` **плюс** кластерний індекс
`(TableRowId, ColumnDefId)`. На 108 млн рядків це означає:

* +8 байт на рядок під `Id` (~0.9 ГБ на рік до стиснення);
* **другий** індекс (unique на `Id`), який теж треба підтримувати при кожному запису;
* `Id` не використовується **ніде** — комірка завжди адресується
  `(TableRowId, ColumnDefId)`, і саме так її шукає і читає система;
* партиційний ключ не входить у PK, тому PK не вирівняний (пастка §13.5 п.2 ТЗ).

### 2.2 Рішення

```sql
CREATE TABLE doc.CellValue
(
    PeriodKey            int            NOT NULL,   -- партиційний ключ, YYYYMM
    TableRowId           bigint         NOT NULL,
    ColumnDefId          int            NOT NULL,
    TableDefId           int            NOT NULL,   -- денормалізовано під складений FK

    ValueString          nvarchar(1000) NULL,
    ValueNumeric         decimal(28,10) NULL,
    ValueDate            datetime2(3)   NULL,
    ValueBool            bit            NULL,
    ValueRegistryEntryId int            NULL,

    IsCalculated         bit            NOT NULL CONSTRAINT DF_CellValue_Calc  DEFAULT(0),
    IsEmpty              bit            NOT NULL CONSTRAINT DF_CellValue_Empty DEFAULT(0),

    CONSTRAINT PK_CellValue PRIMARY KEY CLUSTERED (PeriodKey, TableRowId, ColumnDefId)
        WITH (DATA_COMPRESSION = PAGE)
        ON ps_ByPeriodKey(PeriodKey),

    CONSTRAINT FK_CellValue_Row    FOREIGN KEY (PeriodKey, TableRowId)
        REFERENCES doc.TableRow (PeriodKey, Id),
    CONSTRAINT FK_CellValue_Column FOREIGN KEY (TableDefId, ColumnDefId)
        REFERENCES cfg.ColumnDef (TableDefId, Id),
    CONSTRAINT FK_CellValue_Entry  FOREIGN KEY (ValueRegistryEntryId)
        REFERENCES dic.RegistryEntry (Id)
) ON ps_ByPeriodKey(PeriodKey);
```

**Що це дає:**

| | Було в [07] | Стало |
|---|---|---|
| Індексів на таблиці | 2 (PK + кластерний) | **1** |
| Байт службових даних на рядок | ~8 (`Id`) + ключ у некластерному | 0 |
| Вирівнювання з партиціями | PK не вирівняний | PK **вирівняний** — вимога §13.5 п.2 виконана за побудовою |
| Читання рядка таблиці | seek по кластерному | той самий seek, на один рівень менше |
| `SqlBulkCopy` | треба генерувати `Id` | нічого генерувати не треба |

**Ціна:** ширший ключ (4+8+4 = 16 байт) у некластерних індексах, якщо вони
з'являться. Некластерних індексів на `CellValue` за проєктом **немає жодного** —
усі альтернативні доступи йдуть через `doc.DocumentIndexValue` ([07] §4).
Якщо колись знадобиться, наприклад, `(ColumnDefId, ValueNumeric)` для аналітики —
це аргумент за окрему аналітичну проєкцію, а не за індекс на гарячій таблиці.

### 2.3 `doc.TableRow` — так само

```sql
CREATE TABLE doc.TableRow
(
    PeriodKey       int            NOT NULL,
    Id              bigint         NOT NULL,        -- sequence, не IDENTITY (див. нижче)
    TableInstanceId bigint         NOT NULL,
    RowKey          nvarchar(100)  NOT NULL,
    RowDefId        int            NULL,
    Ordinal         int            NOT NULL,
    IsDeleted       bit            NOT NULL DEFAULT(0),
    ModifiedAt      datetime2(3)   NOT NULL,        -- «дотик» при зміні комірок
    RowVersion      rowversion     NOT NULL,

    CONSTRAINT PK_TableRow PRIMARY KEY CLUSTERED (PeriodKey, Id)
        ON ps_ByPeriodKey(PeriodKey),
    CONSTRAINT UQ_TableRow_Key UNIQUE (PeriodKey, TableInstanceId, RowKey)
        ON ps_ByPeriodKey(PeriodKey)
) ON ps_ByPeriodKey(PeriodKey);
```

`Id` береться з `SEQUENCE doc.TableRowSeq` (кеш 1000), а не `IDENTITY`: значення
потрібні **до** вставки, щоб одразу сформувати `CellValue` і завантажити обидві
таблиці одним `SqlBulkCopy`-проходом. З `IDENTITY` довелося б робити два кроки
з `OUTPUT`, а `OUTPUT` конфліктує з тригерами (§4).

`UQ_TableRow_Key` містить `PeriodKey` — інакше унікальний індекс не вирівняний
і партиційні операції неможливі (§13.5 п.2 ТЗ).

### 2.4 `cfg.ColumnDef` — унікальний ключ під складений FK

Пастка §13.5 п.3 ТЗ: EF Core сам його не створить.

```sql
ALTER TABLE cfg.ColumnDef
    ADD CONSTRAINT UQ_ColumnDef_Table_Id UNIQUE (TableDefId, Id);
```

Без цього `FK_CellValue_Column` просто не створюється, і головна гарантія
цілісності («комірка фізично не може потрапити в чужу колонку») лишається
декларацією.

---

## 3. Партиціонування (BR-03)

### 3.1 Партиційний ключ — `PeriodKey`, а не `PeriodId`

[07] §4 каже «партиції по `PeriodId`». `PeriodId` — сурогат із `IDENTITY`, і це
погано працює як межа партицій:

* межі партицій треба знати **наперед**, а сурогат відомий лише після `INSERT`;
* сурогати не впорядковані за часом між проєктами (проєкт 2027 може отримати
  менші Id, ніж дозаведений період 2026);
* «архівувати рік» = «архівувати 12 конкретних Id», які треба спершу знайти,
  і `TRUNCATE ... WITH (PARTITIONS)` доводиться будувати динамічно.

**Рішення: `PeriodKey int = Year * 100 + Sequence`** (`202609` = вересень 2026).
Денормалізується в `doc.TableInstance`, `doc.TableRow`, `doc.CellValue`.
`doc.TableInstance` партиціонується тим самим `ps_ByPeriodKey` — інакше
партиційний `TRUNCATE` при архівації (§6.2) до неї не застосовний.
`aud.CellChange` партиціонована **окремо**, по `ChangedAt` (`ps_AuditByMonth`):
місяць зміни і звітний період — різні осі.
Джерело — `doc.Period`; для `PeriodKind = Yearly` — `Year*100 + 1`,
для `Quarterly` — `Year*100 + квартал`.

```sql
CREATE PARTITION FUNCTION pf_ByPeriodKey (int)
AS RANGE RIGHT FOR VALUES
   (202601, 202602, …, 202612, 202701, …);       -- по одній на період

CREATE PARTITION SCHEME ps_ByPeriodKey
AS PARTITION pf_ByPeriodKey TO
   ([DATA_HOT], [DATA_HOT], …, [DATA_HOT]);
```

**Що це дає:**
* межа = зрозуміле число, яке можна порахувати в голові;
* «архівувати 2026» = діапазон `202601…202612`, тобто суцільні партиції;
* запит за один місяць читає рівно одну партицію (partition elimination), і це
  видно в плані;
* нові партиції створює регламентна задача (§3.3) заздалегідь — застосунок
  жодного DDL не робить.

### 3.2 Гранулярність

Одна партиція = один період (місяць для ECR). На рік — 12 партицій, кожна ~9 млн
рядків `CellValue`. Ліміт SQL Server — 15 000 партицій, тобто запасу на 1 000 років;
реальне обмеження — час обслуговування, і 12/рік тут абсолютно комфортні.

> Альтернатива «одна партиція на рік» відкидається: тоді запит за вересень читає
> увесь рік, а це саме той сценарій, який дає пік навантаження ([14] §1.3 — усі
> користувачі б'ють в один період).

### 3.3 Обслуговування партицій

Регламентна задача `PartitionMaintenanceJob` (щомісяця, B07):

```
1. Порахувати, які PeriodKey знадобляться на наступні 6 місяців
2. Для кожного відсутнього:  ALTER PARTITION SCHEME … NEXT USED [DATA_HOT]
                             ALTER PARTITION FUNCTION … SPLIT RANGE (@key)
3. Записати в itg.MaintenanceRun; невдача → aud.ConsistencyIssue (Critical)
```

`SPLIT` порожньої останньої партиції — операція метаданих, майже миттєва.
`SPLIT` непорожньої — переміщення даних із блокуванням; саме тому задача
працює **на випередження**, а старт застосунку лише перевіряє наявність запасу
(B01 §6.3, крок 7).

### 3.4 Файлові групи

| Група | Що | Диск |
|-------|----|------|
| `PRIMARY` | `cfg`, `dic`, `sec`, `wf`, `itg`, `rpt`, системні | будь-який |
| `DATA_HOT` | `doc.*` і `calc.*` — партиції поточного і минулого року | NVMe |
| `DATA_ARCHIVE` | `arc.*` (архів `doc.*` і `calc.*`) — columnstore | повільніший, дешевший |
| `AUDIT` | `aud.CellChange` партиції | окремий, щоб запис аудиту не конкурував із гарячим I/O |

---

## 4. EF Core і тригери (BR-04)

### 4.1 Проблема

EF Core 7+ використовує `OUTPUT`-клаузу для отримання згенерованих значень.
Таблиця з будь-яким тригером ламає це з runtime-помилкою. Обов'язково:

```csharp
modelBuilder.Entity<ColumnDef>()
    .ToTable("ColumnDef", "cfg", t => t.HasTrigger("TR_ColumnDef_Immutable"));
```

— для **кожної** таблиці `cfg.*`, на якій є тригер immutability, і для
`aud.CellChange`, якщо там з'явиться тригер заборони `UPDATE`.

### 4.2 `AFTER` замість `INSTEAD OF`

[10] §2a пропонує `INSTEAD OF UPDATE`, який «пропускає лише презентаційні поля».
Це працює, але має три недоліки:

1. `INSTEAD OF` робить `OUTPUT` на таблиці **непридатним взагалі** — не лише
   незручним; будь-яка робота EF з такою таблицею вимагає обхідних шляхів;
2. тригер має **сам переписати** `UPDATE`, тобто продублювати список презентаційних
   колонок у T-SQL; при додаванні колонки в `cfg.*` про це забувають;
3. логіка «що презентаційне» опиняється у двох місцях — у C# і в T-SQL.

**Рішення: `AFTER UPDATE, DELETE` тригер, який лише *забороняє*:**

```sql
CREATE OR ALTER TRIGGER cfg.TR_ColumnDef_Immutable
ON cfg.ColumnDef
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (
        SELECT 1
        FROM   deleted d
        JOIN   cfg.TableDef  td ON td.Id = d.TableDefId
        JOIN   cfg.SheetDef  sd ON sd.Id = td.SheetDefId
        JOIN   cfg.TemplateVersion tv ON tv.Id = sd.TemplateVersionId
        WHERE  tv.[Status] = 1 /* Published */
    ) RETURN;                                   -- не Published → нічого не перевіряємо

    IF EXISTS (SELECT 1 FROM deleted d LEFT JOIN inserted i ON i.Id = d.Id
               WHERE i.Id IS NULL)
    BEGIN
        THROW 51001, 'Видалення структури опублікованої версії заборонено', 1;
    END;

    IF EXISTS (                                  -- будь-яка СТРУКТУРНА зміна
        SELECT 1
        FROM   inserted i JOIN deleted d ON d.Id = i.Id
        WHERE  i.Code                 <> d.Code
            OR i.DataType             <> d.DataType
            OR ISNULL(i.Precision,-1) <> ISNULL(d.Precision,-1)
            OR ISNULL(i.Scale,-1)     <> ISNULL(d.Scale,-1)
            OR i.IsRequired           <> d.IsRequired
            OR ISNULL(i.LookupRegistryDefId,-1) <> ISNULL(d.LookupRegistryDefId,-1)
            OR i.IsBusinessKey        <> d.IsBusinessKey
            OR i.TableDefId           <> d.TableDefId
    )
    BEGIN
        THROW 51002,
          'Структурна зміна опублікованої версії заборонена. Створіть нову версію (Clone).', 1;
    END;
END;
```

Тобто: **тригер перелічує структурні поля і забороняє їх змінювати; усе інше —
презентаційне і проходить.** Список структурних полів короткий і майже не росте
(це `Code`, тип, ключовість, обов'язковість, довідник, належність), тоді як
презентаційних полів багато і вони додаються постійно. Так забути про нову
колонку значно важче — і забути в **безпечний** бік.

### 4.3 `PresentationRevision` інкрементує застосунок, не тригер

Якби тригер сам робив `UPDATE cfg.TemplateVersion SET PresentationRevision += 1`,
EF отримав би розсинхрон concurrency-токена і рекурсивні спрацювання. Тому:

```
ТРАНЗАКЦІЯ:
   UPDATE cfg.<Def>            -- презентаційні поля; тригер пропускає
   UPDATE cfg.TemplateVersion  -- PresentationRevision += 1
   INSERT aud.SchemaChange     -- ChangeClass = Presentation, Before/After JSON
COMMIT
```

Одна транзакція, одне джерело істини, повний аудит. `CHECK`-обмеження
`PresentationRevision >= 0` + перевірка в `ConsistencyCheckJob`: не буває
`aud.SchemaChange` з класом `Presentation` без відповідного інкремента.

### 4.4 Інші місця, де EF потребує явних вказівок

| Ситуація | Що зробити |
|----------|-----------|
| `rowversion` на `doc.TableRow` | `.IsRowVersion()`; в EF це concurrency-токен «безкоштовно» |
| Партиційований ключ у PK | `.HasKey(x => new { x.PeriodKey, x.Id })` — і **ніколи** не міняти `PeriodKey` в існуючому рядку (це перенесення між партиціями) |
| Складений FK | `.HasForeignKey(nameof(CellValue.TableDefId), nameof(CellValue.ColumnDefId)).HasPrincipalKey(…)` — і `UQ_ColumnDef_Table_Id` створити явно в міграції |
| `decimal(28,10)` | `.HasPrecision(28, 10)` — інакше EF візьме дефолт і округлення поїде |
| Читання | `AsNoTracking()` за замовчуванням: `optionsBuilder.UseQueryTrackingBehavior(NoTracking)` |
| Ліниве завантаження | вимкнено; `UseLazyLoadingProxies` не підключаємо взагалі |

---

## 5. Масові операції (BR-21)

> **Межа «що дозволено в БД»** ([B22](B22-nocode-and-realism.md) §3.4): усе нижче —
> **доступ до даних**, а не обчислення. Процедура пакетного запису робить `MERGE`
> за переданим набором і не містить жодного предметного правила. Обчислення,
> агрегації і правила живуть у сервісі; CLR у БД **немає взагалі**.

`EFCore.BulkExtensions` заборонена (cFOSS). Засоби — з коробки:

| Задача | Засіб |
|--------|-------|
| Генерація документа, імпорт `.xlsx`, міграція з AF | `SqlBulkCopy` у **тимчасову** таблицю + `MERGE`/`INSERT…SELECT` у цільову |
| Batch-PATCH до ~1000 комірок | **TVP** (table-valued parameter) + одна процедура `doc.usp_ApplyCellBatch` |
| Масове очищення (`LockAndClear`, скасування імпорту) | `ExecuteDeleteAsync` / `ExecuteUpdateAsync` |
| Архівація | `INSERT…SELECT` батчами + `TRUNCATE … WITH (PARTITIONS)` |
| Аудит | `INSERT…SELECT` із того самого TVP — один запит на батч |

> **Чому TVP, а не `SqlBulkCopy` для гарячого шляху.** Батч на 100 комірок — це
> ~10 КБ. `SqlBulkCopy` виграє на десятках тисяч рядків, а на сотні його накладні
> витрати (створення тимчасової таблиці, окремий round-trip) з'їдають виграш.
> TVP дає одну процедуру, одну транзакцію, один round-trip і план запиту, який
> кешується. Межа перемикання (~2000 рядків) вимірюється на Етапі 0.

---

## 6. Архівація (BR-06) — чому `SWITCH PARTITION` тут не працює

### 6.1 Констатація

ТЗ АРХ-3 припускає `SWITCH PARTITION` у `arc.*`. `SWITCH PARTITION` вимагає, щоб
джерело і приймач були:

* на **одній файловій групі**;
* з **ідентичною** структурою — включно з типом і набором індексів.

А `arc.*` за проєктом ([07] §10) — **columnstore на окремій файловій групі**.
Тобто дві умови з двох порушені одночасно й свідомо: саме різниця в стисненні
й розміщенні і є сенсом архіву.

### 6.2 Рішення

```
ArchiveJob(projectId):

1. keys = [YYYY01 … YYYY12]                              -- партиції року
2. src  = SELECT COUNT_BIG(*), CHECKSUM_AGG(BINARY_CHECKSUM(*)),
                 SUM(ValueNumeric)
          FROM doc.CellValue WHERE PeriodKey IN keys
3. батчами по ~500k:
       INSERT INTO arc.CellValue WITH (TABLOCK)
       SELECT … FROM doc.CellValue WHERE PeriodKey = @k
       -- TABLOCK → мінімальне логування і прямий запис у columnstore rowgroups
4. dst = ті самі три показники на arc.CellValue
5. IF src <> dst  → ROLLBACK усього; aud.ConsistencyIssue(Critical); СТОП
                    (дані з doc.* НЕ видаляються — це головне правило)
6. TRUNCATE TABLE doc.CellValue WITH (PARTITIONS (@from TO @to));
   -- те саме для doc.TableRow і doc.TableInstance (той самий ps_ByPeriodKey)
   -- aud.CellChange: діапазон партицій обчислюється ОКРЕМО, за вікном ChangedAt
   --                 (ps_AuditByMonth), а не за PeriodKey
7. itg.ArchiveRun: напрямок, рядків, контрольні суми, тривалість
```

`TRUNCATE TABLE … WITH (PARTITIONS)` (SQL Server 2016+) звільняє партиції
майже миттєво і **мінімально логується** — на відміну від `DELETE` батчами,
який на 108 млн рядків роздує лог і триматиме блокування годинами.

### 6.3 Розархівація (`Reopen` закритого року)

Та сама процедура у зворотному напрямку, з тими самими звірками. Обов'язкова —
без неї `Reopen` для архівного року (ТЗ АРХ-5) неможливий, а він передбачений
бізнес-процесом.

### 6.4 Прозоре читання

`ICellStore` сам обирає джерело:

```csharp
// у реалізації, не в use-case
var source = period.PeriodKey >= _archiveThreshold ? Hot : Archive;
```

Поріг береться з `doc.Project.Status`/`itg.ArchiveRun`, а не з дати — інакше
під час самої архівації читання ловить напівстан. Під час виконання `ArchiveJob`
проєкт має прапорець `IsArchiving`, і читання йде **з джерела**, доки крок 6
не завершився.

---

## 7. Гейт Етапу 0: що саме міряємо (BR-07)

`tools/Ecr.DataGen` генерує реалістичний обсяг — не рівномірний шум, а розподіл,
знятий із чинного шаблону:

| Параметр | Значення |
|----------|----------|
| Документів | 100 / **300 (цільовий)** / 500 — питання №7 **закрито 2026-09-03**: замовник називає **≥ 200** на рік, отже цільовий сценарій гейта — 300, запас на зростання — 500 |
| Таблиць на документ | ~90 |
| Рядків на таблицю | розподіл із реального шаблону: медіана ~30, хвіст до 471 |
| Колонок | 7…60 |
| **Заповненість** | **35 / 60 / 90 %** — три сценарії; від цього залежить усе |
| Періодів | 12 |

**Критерії проходження (з [14] §2):**

| # | Замір | Ціль | Що робимо, якщо не проходить |
|---|-------|------|------------------------------|
| 1 | `ReadSliceAsync` 500×60 | **< 600 мс** p95 | спершу — покриття індексом і `PAGE`-стиснення; далі — JSON-гібрид (§8) |
| 2 | `ApplyAsync` 100 комірок | **< 150 мс** p95 (з 300 мс бюджету половина — на все інше) | зменшити роботу в транзакції (B04 §4) |
| 3 | Агрегація по періоду (rollup `7.0`) | < 500 мс | покриваючий індекс над **уже матеріалізованими** значеннями rollup (`IsCalculated = 1`, пише `RecalculationJob`); за потреби — окремий зріз, який будує сервіс. ⛔ **Індексована вʼюха неприпустима** — ER-N-02 ([B22](B22-nocode-and-realism.md) §3.4) |
| 4 | Повний цикл архівації року | без блокування робочих запитів | зменшити батч, вікно обслуговування |
| 5 | Розмір `doc.CellValue` після `PAGE` | оцінка ГБ/рік для sizing | — |
| 6 | 125 RPS в одну партицію 15 хв | без ескалації блокувань, p95 у бюджеті | `READ COMMITTED SNAPSHOT` (див. B04 §4.3) |

> Замір №6 — найважливіший і його найлегше пропустити. Пік у ECR **не розподілений**:
> усі 100 користувачів в останні дні періоду працюють з **однією й тією ж партицією**
> ([14] §1.3). Рівномірне навантаження на 12 партицій нічого не доводить.

---

## 8. Запасний варіант: JSON-гібрид (BR-08)

Проєктується зараз, реалізується лише за результатом гейта.

```sql
ALTER TABLE doc.TableRow ADD DataJson nvarchar(max) NULL;
-- {"MonthValue": 125.5, "Permit": 15, "ReportType": "ITEM"}
--    ключ = ColumnDef.Code, значення — типізоване за DataType
```

| Аспект | Як розв'язується |
|--------|------------------|
| Індексовані поля | `ColumnDef.IsIndexed` → persisted computed-колонка `JSON_VALUE(DataJson, '$.Code')` + індекс. Тобто «5–10 ключових полів», як і передбачає ТЗ §5.1 |
| Складений FK | **втрачається**. Замінюється перевіркою в `ICellStore` + щонічним `ConsistencyCheckJob`. Це і є головна ціна варіанту, і її треба назвати вголос |
| FK на `RegistryEntry` | так само втрачається → перевірка в коді + нічна задача |
| Аудит | diff двох JSON у `ICellStore` перед записом; формат `aud.CellChange` не змінюється |
| Порожні комірки | ключ просто відсутній у JSON |
| Міграція між моделями | `INSERT INTO TableRow(DataJson) SELECT … FOR JSON` — один прохід, оборотний |

**Гібрид, а не чистий JSON:** `ValueRegistryEntryId` для `Lookup`-колонок
лишається окремою таблицею-зв'язкою `doc.CellRegistryRef (PeriodKey, TableRowId,
ColumnDefId, RegistryEntryId)` — щоб не втратити FK там, де він реально захищає
дані (ФВ-8.7). Це невеликий обсяг: `Lookup`-колонок у шаблоні одиниці на таблицю.

---

## 9. Чек-лист фізичної моделі

- [ ] `PeriodKey` в PK і в **усіх** unique-індексах `doc.TableRow` / `doc.CellValue`
- [ ] `UQ_ColumnDef_Table_Id` створений явно в міграції
- [ ] Складений FK `(TableDefId, ColumnDefId)` існує і має тест «спроба вставити
      комірку в колонку чужої таблиці → помилка БД, не помилка коду»
- [ ] `HasTrigger()` оголошений для кожної таблиці з тригером
- [ ] Жодного некластерного індексу на `doc.CellValue` без заміру, що доводить потребу
- [ ] `DATA_COMPRESSION = PAGE` на гарячих партиціях, columnstore на `arc.*`
- [ ] `PartitionMaintenanceJob` створює партиції на 6 місяців наперед
- [ ] Права: `aud.*` — `INSERT`/`SELECT`, без `UPDATE`/`DELETE`
- [ ] `ArchiveJob` не видаляє джерело, доки контрольні суми не зійшлися
- [ ] `ICellStore` — єдине місце, де є знання про фізичну модель
