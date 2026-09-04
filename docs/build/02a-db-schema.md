# 02a — Схема БД. Повний DDL

> Частина контракту. Реалізується **дослівно**. Розбіжність між цим файлом і
> `docs/07-data-model.md` вирішується на користь **цього файлу** (див.
> [`01-TOR.md`](01-TOR.md) §4).
>
> **Наскрізні конвенції:**
> * усі мітки часу — `datetime2(3)` у **UTC** (`R-A3`);
> * автор дії — `…ByUserId int` → `sec.User(Id)`, **ніколи не SID** (`R-A2`);
> * коди — `nvarchar(64)`, шаблон `^[A-Za-z][A-Za-z0-9_]{0,63}$` (`R-B6`);
> * локалізовані назви — одна колонка `…L10n nvarchar(max)` з JSON;
> * емісії й обчислені величини — `decimal(28,10)`; **`float` заборонений**;
> * soft delete — `IsDeleted bit` + `DeletedAt` + `DeletedByUserId`.

## Зміст

| Якір | Схема |
|---|---|
| [`#filegroups`](#filegroups) | Файлові групи і партиціонування |
| [`#sys`](#sys) | `sys` — системні реєстри |
| [`#cfg`](#cfg) | `cfg` — метадані шаблону |
| [`#uom`](#uom) | `uom` — одиниці вимірювання |
| [`#dic`](#dic) | `dic` — дані реєстрів |
| [`#doc`](#doc) | `doc` — документи і дані |
| [`#calc`](#calc) | `calc` — розрахунки |
| [`#rpt`](#rpt) | `rpt` — звітність |
| [`#ext`](#ext) | `ext` — зовнішні джерела і legacy-мапінг |
| [`#wf`](#wf) | `wf` — робочий процес |
| [`#sec`](#sec) | `sec` — безпека |
| [`#aud`](#aud) | `aud` — аудит |
| [`#itg`](#itg) | `itg` — журнали інтеграції |
| [`#arc`](#arc) | `arc` — архів |
| [`#triggers`](#triggers) | Тригери незмінності |
| [`#archive-proc`](#archive-proc) | Процедура архівації |
| [`#seed`](#seed) | Обов'язковий seed |

---

<a id="filegroups"></a>
## 1. Файлові групи, партиціонування, схеми

> Виконується **окремими скриптами під SQL Agent**, не міграціями EF: у
> застосунку немає DDL-прав у прод (`D-66`).

```sql
-- src/Ecr.Infrastructure/Persistence/Sql/01-filegroups.sql
-- Виконує SQL Agent під окремим principal (D-66): застосунок DDL-прав не має.
--
-- ⚠ Жодного шляху в цьому скрипті не зашито (Q-029).
--   • БАЗА береться з ПІДКЛЮЧЕННЯ, з яким запущено скрипт (`sqlcmd -d <база>`),
--     через DB_NAME() — тому назва бази задається там само, де й у рядку
--     підключення застосунку, і другого місця для неї не існує.
--   • КАТАЛОГ ДАНИХ визначається автоматично: типовий каталог інстансу
--     (SERVERPROPERTY('InstanceDefaultDataPath')). Це шлях у файловій системі
--     ХОСТА SQL Server, а не адреса підключення, тому рядком підключення він
--     не задається в принципі.
--   • Заповнювати @ArchivePath потрібно лише тоді, коли архівна файлова група
--     має лежати на іншому носії — саме в цьому сенс окремої групи.
--
-- Ідемпотентний: повторний запуск не падає і нічого не дублює.
--
--   sqlcmd -S <сервер> -d <база> -E -i 01-filegroups.sql

SET NOCOUNT ON;

DECLARE @DataPath    nvarchar(260) = NULL;   -- NULL = типовий каталог інстансу
DECLARE @ArchivePath nvarchar(260) = NULL;   -- NULL = той самий, що й @DataPath

-- NULLIF: SERVERPROPERTY на нетиповій конфігурації може віддати порожній рядок,
-- і без цієї перевірки файли поїхали б у корінь диска.
SET @DataPath = NULLIF(LTRIM(RTRIM(COALESCE(
    @DataPath, CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(260))))), N'');

IF @DataPath IS NULL
    THROW 50020, N'Не вдалося визначити каталог даних інстансу. Задайте @DataPath на початку скрипта.', 1;

IF RIGHT(@DataPath, 1) <> N'\' SET @DataPath = @DataPath + N'\';

SET @ArchivePath = NULLIF(LTRIM(RTRIM(COALESCE(@ArchivePath, @DataPath))), N'');
IF RIGHT(@ArchivePath, 1) <> N'\' SET @ArchivePath = @ArchivePath + N'\';

DECLARE @db sysname = DB_NAME();
DECLARE @sql nvarchar(max);

-- ⚠ Express (EngineEdition = 4) має межу 10 ГБ на базу, а продуктивні розміри
--    дають 14 ГБ — скрипт упав би на третьому файлі з незрозумілою помилкою.
--    На Express створюємо маленькі файли: вони ростуть за потреби, а
--    партиціонування, columnstore і компресія там доступні з 2016 SP1, тобто
--    перевіряти фізичну модель на ньому можна повноцінно.
DECLARE @isExpress bit = CASE WHEN CAST(SERVERPROPERTY('EngineEdition') AS int) = 4 THEN 1 ELSE 0 END;

-- 1. Файлові групи, яких ще немає.
--    Один пакет DDL замість циклу: коротше і без курсорів у скрипті,
--    який читає людина перед запуском на проді.
SELECT @sql = STRING_AGG(
        CAST(N'ALTER DATABASE ' + QUOTENAME(@db) + N' ADD FILEGROUP ' + QUOTENAME(g.Name) + N';'
             AS nvarchar(max)), NCHAR(10))
FROM (VALUES (N'DATA_HOT'), (N'DATA_ARCHIVE'), (N'AUDIT'), (N'INDEXES')) AS g(Name)
WHERE NOT EXISTS (SELECT 1 FROM sys.filegroups f WHERE f.name = g.Name);

IF @sql IS NOT NULL EXEC sp_executesql @sql;

-- 2. Файли. Розміри і приріст — за профілем навантаження: архів росте ривками
--    при архівації року, гарячі дані — рівномірно, індекси найповільніше.
--    Виконується ПІСЛЯ кроку 1: ADD FILE вимагає наявної файлової групи.
SET @sql = NULL;

SELECT @sql = STRING_AGG(
        CAST(N'ALTER DATABASE ' + QUOTENAME(@db) + N' ADD FILE (NAME = '
             + QUOTENAME(f.LogicalName, '''')
             + N', FILENAME = ' + QUOTENAME(
                   CASE WHEN f.UseArchivePath = 1 THEN @ArchivePath ELSE @DataPath END
                   + f.LogicalName + N'.ndf', '''')
             + N', SIZE = ' + CAST(sz.SizeMb AS nvarchar(10)) + N'MB'
             + N', FILEGROWTH = ' + CAST(sz.GrowthMb AS nvarchar(10)) + N'MB)'
             + N' TO FILEGROUP ' + QUOTENAME(f.FileGroup) + N';'
             AS nvarchar(max)), NCHAR(10))
FROM (VALUES
        (N'Ecr_hot',     N'DATA_HOT',     4096, 1024, 0),
        (N'Ecr_archive', N'DATA_ARCHIVE', 4096, 4096, 1),
        (N'Ecr_audit',   N'AUDIT',        4096, 2048, 0),
        (N'Ecr_idx',     N'INDEXES',      2048, 1024, 0)
     ) AS f(LogicalName, FileGroup, SizeMb0, GrowthMb0, UseArchivePath)
CROSS APPLY (SELECT SizeMb   = CASE WHEN @isExpress = 1 THEN 64 ELSE f.SizeMb0   END,
                    GrowthMb = CASE WHEN @isExpress = 1 THEN 64 ELSE f.GrowthMb0 END) AS sz
WHERE NOT EXISTS (SELECT 1 FROM sys.database_files d WHERE d.name = f.LogicalName);

IF @sql IS NOT NULL EXEC sp_executesql @sql;

PRINT N'База ' + @db + N': файлові групи і файли готові'
    + CASE WHEN @isExpress = 1 THEN N' (Express: зменшені початкові розміри).' ELSE N'.' END;
PRINT N'  каталог даних: ' + @DataPath;
PRINT N'  каталог архіву: ' + @ArchivePath;
GO
```

```sql
-- src/Ecr.Infrastructure/Persistence/Sql/06-rcsi.sql
-- Обов'язково: без RCSI пік «останнього дня періоду» упирається в блокування
-- (D-29). Рівень SNAPSHOT НЕ вмикаємо — він важчий і дає конфлікти оновлення.
--
-- База береться з ПІДКЛЮЧЕННЯ через DB_NAME() (Q-029), а не зашита іменем:
-- назва бази живе рівно в одному місці — у рядку підключення.
--
--   sqlcmd -S <сервер> -d <база> -E -i 06-rcsi.sql

SET NOCOUNT ON;

DECLARE @db sysname = DB_NAME();
DECLARE @sql nvarchar(max);

-- ⚠ WITH ROLLBACK IMMEDIATE обриває чужі сеанси. Виконувати у вікні
--    обслуговування, а не на працюючій системі.
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = @db AND is_read_committed_snapshot_on = 1)
BEGIN
    SET @sql = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;';
    EXEC sp_executesql @sql;
END

IF EXISTS (SELECT 1 FROM sys.databases WHERE name = @db AND snapshot_isolation_state = 1)
BEGIN
    SET @sql = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET ALLOW_SNAPSHOT_ISOLATION OFF;';
    EXEC sp_executesql @sql;
END

PRINT N'RCSI увімкнено для бази ' + @db + N'; ALLOW_SNAPSHOT_ISOLATION вимкнено.';
GO
```

```sql
-- src/Ecr.Infrastructure/Persistence/Sql/02-partitions.sql
-- PeriodKey = Year*100 + Sequence (R-A6). Одна партиція = один період.
-- Межі виписуються наперед; PartitionCheckJob стежить за запасом на 6 періодів.
CREATE PARTITION FUNCTION pf_ByPeriodKey (int)
AS RANGE RIGHT FOR VALUES
(
    202601, 202602, 202603, 202604, 202605, 202606,
    202607, 202608, 202609, 202610, 202611, 202612,
    202701, 202702, 202703, 202704, 202705, 202706,
    202707, 202708, 202709, 202710, 202711, 202712
);
GO

CREATE PARTITION SCHEME ps_ByPeriodKey
AS PARTITION pf_ByPeriodKey ALL TO ([DATA_HOT]);
GO

-- Аудит партиціонується ОКРЕМО, по ChangedAt: місяць зміни і звітний період —
-- різні осі (зміна за січень може статися в березні).
CREATE PARTITION FUNCTION pf_AuditByMonth (datetime2(3))
AS RANGE RIGHT FOR VALUES
(
    '2026-01-01', '2026-02-01', '2026-03-01', '2026-04-01', '2026-05-01', '2026-06-01',
    '2026-07-01', '2026-08-01', '2026-09-01', '2026-10-01', '2026-11-01', '2026-12-01',
    '2027-01-01', '2027-02-01', '2027-03-01', '2027-04-01', '2027-05-01', '2027-06-01'
);
GO

CREATE PARTITION SCHEME ps_AuditByMonth
AS PARTITION pf_AuditByMonth ALL TO ([AUDIT]);
GO
```

```sql
-- Схеми БД (створюються міграцією EF)
CREATE SCHEMA sys_ecr;  -- «sys» зайнято SQL Server; логічна назва в коді — sys
GO
CREATE SCHEMA cfg;  GO
CREATE SCHEMA uom;  GO
CREATE SCHEMA dic;  GO
CREATE SCHEMA doc;  GO
CREATE SCHEMA calc; GO
CREATE SCHEMA rpt;  GO
CREATE SCHEMA ext;  GO
CREATE SCHEMA wf;   GO
CREATE SCHEMA sec;  GO
CREATE SCHEMA aud;  GO
CREATE SCHEMA itg;  GO
CREATE SCHEMA arc;  GO
```

> ⚠ **`sys` зарезервована SQL Server.** Фізична назва схеми — **`sys_ecr`**;
> у документації і в коді вона згадується як «sys». Це єдине місце, де фізичне
> ім'я відрізняється від логічного.

---

<a id="sys"></a>
## 2. `sys_ecr` — системні реєстри

```sql
CREATE TABLE sys_ecr.Language
(
    Code        nvarchar(8)   NOT NULL,   -- 'en', 'ru', 'kz'
    NameNative  nvarchar(100) NOT NULL,
    Ordinal     int           NOT NULL,
    IsDefault   bit           NOT NULL CONSTRAINT DF_Language_Default DEFAULT(0),
    IsActive    bit           NOT NULL CONSTRAINT DF_Language_Active  DEFAULT(1),
    CONSTRAINT PK_Language PRIMARY KEY (Code)
);
GO

-- Рівно одна мова за замовчуванням
CREATE UNIQUE INDEX UX_Language_Default ON sys_ecr.Language (IsDefault) WHERE IsDefault = 1;
GO

CREATE TABLE sys_ecr.SystemSetting
(
    [Key]       nvarchar(100)  NOT NULL,
    Value       nvarchar(max)  NULL,
    Description nvarchar(400)  NULL,
    ModifiedAt  datetime2(3)   NOT NULL,
    ModifiedByUserId int       NULL,
    CONSTRAINT PK_SystemSetting PRIMARY KEY ([Key])
);
GO

-- Каталог рядків інтерфейсу (D-95, ФВ-14.9). Тут лежить chrome клієнта —
-- меню, кнопки, підписи полів, тексти помилок форм. Причина: ФВ-14.9 обіцяє
-- «додати мову = запис у реєстр, не збірка клієнта», а це виконувано лише
-- тоді, коли рядки самого UI теж приходять із сервера.
-- Клієнт тягне зріз за мовою один раз при старті і кешує за ETag.
CREATE TABLE sys_ecr.UiString
(
    [Key]        nvarchar(200) NOT NULL,   -- 'grid.paste.confirm', 'nav.templates'
    LanguageCode nvarchar(8)   NOT NULL,
    Value        nvarchar(1000) NOT NULL,
    -- 0 = Public: віддається АНОНІМНО (сторінка входу, помилки автентифікації,
    --     загальний chrome). 1 = Private: лише після входу.
    -- Причина розділення (D-114): анонімний каталог із підписами
    -- адміністративних областей і назвами прав розкрив би поверхню
    -- функціоналу тому, хто ще не увійшов (суперечило б ФВ-14.2).
    Scope        tinyint       NOT NULL CONSTRAINT DF_UiString_Scope DEFAULT(1),
    ModifiedAt   datetime2(3)  NOT NULL,
    ModifiedByUserId int       NULL,
    CONSTRAINT PK_UiString PRIMARY KEY ([Key], LanguageCode),
    CONSTRAINT FK_UiString_Lang FOREIGN KEY (LanguageCode)
        REFERENCES sys_ecr.Language (Code),
    CONSTRAINT CK_UiString_Scope CHECK (Scope IN (0, 1))
);
GO

CREATE INDEX IX_UiString_Lang_Scope ON sys_ecr.UiString (LanguageCode, Scope)
    INCLUDE ([Key], Value);
GO

-- Версія каталогу: змінюється будь-яким записом у UiString і слугує ETag.
-- Без неї клієнт або тягне каталог щоразу, або показує застарілі підписи.
CREATE TABLE sys_ecr.UiStringRevision
(
    Id         tinyint       NOT NULL CONSTRAINT CK_UiRev_Single CHECK (Id = 1),
    Revision   int           NOT NULL,
    ModifiedAt datetime2(3)  NOT NULL,
    CONSTRAINT PK_UiStringRevision PRIMARY KEY (Id)
);
GO
```

> **Відсутній ключ — не помилка.** Якщо для мови немає рядка, клієнт бере
> мову за замовчуванням, а не показує порожнечу; ключ, якого немає взагалі,
> показується як сам ключ. Інакше одна забута локалізація ламає екран.

---

<a id="cfg"></a>
## 3. `cfg` — метадані шаблону

```sql
CREATE TABLE cfg.Template
(
    Id        int           IDENTITY(1,1) NOT NULL,
    Code      nvarchar(64)  NOT NULL,
    NameL10n  nvarchar(max) NOT NULL,
    TagsJson  nvarchar(1000) NULL,        -- ["ECR","Land"] — замість ECR-специфічних колонок
    IsActive  bit           NOT NULL CONSTRAINT DF_Template_Active DEFAULT(1),
    CreatedAt datetime2(3)  NOT NULL,
    CreatedByUserId int     NOT NULL,
    CONSTRAINT PK_Template PRIMARY KEY (Id),
    CONSTRAINT UQ_Template_Code UNIQUE (Code)
);
GO

CREATE TABLE cfg.TemplateVersion
(
    Id                   int           IDENTITY(1,1) NOT NULL,
    TemplateId           int           NOT NULL,
    Version              nvarchar(20)  NOT NULL,     -- '1.0.4.0'
    Status               tinyint       NOT NULL,     -- TemplateVersionStatus
    ClonedFromVersionId  int           NULL,
    -- Інкрементує ЗАСТОСУНОК одним statement із OUTPUT (R-B7), не тригер.
    PresentationRevision int           NOT NULL CONSTRAINT DF_TV_PresRev DEFAULT(0),
    SourceWorkbookHash   varbinary(32) NULL,
    PublishedAt          datetime2(3)  NULL,
    PublishedByUserId    int           NULL,
    CreatedAt            datetime2(3)  NOT NULL,
    CreatedByUserId      int           NOT NULL,
    CONSTRAINT PK_TemplateVersion PRIMARY KEY (Id),
    CONSTRAINT UQ_TemplateVersion UNIQUE (TemplateId, Version),
    CONSTRAINT FK_TV_Template FOREIGN KEY (TemplateId) REFERENCES cfg.Template (Id),
    CONSTRAINT FK_TV_ClonedFrom FOREIGN KEY (ClonedFromVersionId) REFERENCES cfg.TemplateVersion (Id),
    CONSTRAINT CK_TV_Published CHECK (Status <> 1 OR (PublishedAt IS NOT NULL AND PublishedByUserId IS NOT NULL))
);
GO

CREATE TABLE cfg.SheetDef
(
    Id                int           IDENTITY(1,1) NOT NULL,
    TemplateVersionId int           NOT NULL,
    Code              nvarchar(64)  NOT NULL,
    NameL10n          nvarchar(max) NOT NULL,
    Ordinal           int           NOT NULL,
    SheetGroup        nvarchar(64)  NULL,
    IsMandatory       bit           NOT NULL CONSTRAINT DF_SheetDef_Mand DEFAULT(0),
    IsVisible         bit           NOT NULL CONSTRAINT DF_SheetDef_Vis  DEFAULT(1),
    IsDeleted         bit           NOT NULL CONSTRAINT DF_SheetDef_Del  DEFAULT(0),
    DeletedAt         datetime2(3)  NULL,
    DeletedByUserId   int           NULL,
    CONSTRAINT PK_SheetDef PRIMARY KEY (Id),
    CONSTRAINT UQ_SheetDef UNIQUE (TemplateVersionId, Code),
    CONSTRAINT FK_SheetDef_TV FOREIGN KEY (TemplateVersionId) REFERENCES cfg.TemplateVersion (Id)
);
GO

CREATE TABLE cfg.TableDef
(
    Id             int           IDENTITY(1,1) NOT NULL,
    SheetDefId     int           NOT NULL,
    Code           nvarchar(64)  NOT NULL,
    NameL10n       nvarchar(max) NOT NULL,
    Ordinal        int           NOT NULL,
    LayoutKind     tinyint       NOT NULL,   -- TableLayoutKind
    RowMode        tinyint       NOT NULL,   -- TableRowMode
    MaxDynamicRows int           NULL,
    HeaderStyleId  int           NULL,
    -- Фізична модель зберігання комірок цієї таблиці (D-21): перехід на гібрид
    -- вибірковий, а не глобальний.
    StorageMode    tinyint       NOT NULL CONSTRAINT DF_TableDef_Storage DEFAULT(0),
    IsDeleted      bit           NOT NULL CONSTRAINT DF_TableDef_Del DEFAULT(0),
    DeletedAt      datetime2(3)  NULL,
    DeletedByUserId int          NULL,
    CONSTRAINT PK_TableDef PRIMARY KEY (Id),
    CONSTRAINT UQ_TableDef UNIQUE (SheetDefId, Code),
    CONSTRAINT FK_TableDef_Sheet FOREIGN KEY (SheetDefId) REFERENCES cfg.SheetDef (Id),
    CONSTRAINT CK_TableDef_MaxRows CHECK (RowMode = 0 OR MaxDynamicRows IS NULL OR MaxDynamicRows > 0)
);
GO

CREATE TABLE cfg.ColumnDef
(
    Id                   int           IDENTITY(1,1) NOT NULL,
    TableDefId           int           NOT NULL,
    Code                 nvarchar(64)  NOT NULL,
    HeaderL10n           nvarchar(max) NOT NULL,
    Ordinal              int           NOT NULL,
    DataType             tinyint       NOT NULL,   -- CellDataType
    Precision            tinyint       NULL,
    Scale                tinyint       NULL,
    IsReadOnly           bit           NOT NULL CONSTRAINT DF_ColumnDef_RO   DEFAULT(0),
    IsRequired           bit           NOT NULL CONSTRAINT DF_ColumnDef_Req  DEFAULT(0),
    IsHidden             bit           NOT NULL CONSTRAINT DF_ColumnDef_Hid  DEFAULT(0),
    IsMonthColumn        bit           NOT NULL CONSTRAINT DF_ColumnDef_Mon  DEFAULT(0),
    MonthNumber          tinyint       NULL,
    DefaultValue         nvarchar(400) NULL,
    DisplayFormat        nvarchar(50)  NULL,
    LookupRegistryDefId  int           NULL,
    LookupFilter         nvarchar(500) NULL,
    CascadeFromColumnId  int           NULL,
    UnitId               int           NULL,       -- одиниця зберігання значень колонки (ФВ-16.1)
    IsBusinessKey        bit           NOT NULL CONSTRAINT DF_ColumnDef_BK  DEFAULT(0),
    IsScopeField         bit           NOT NULL CONSTRAINT DF_ColumnDef_Sc  DEFAULT(0),
    IsIndexed            bit           NOT NULL CONSTRAINT DF_ColumnDef_Ix  DEFAULT(0),
    StyleId              int           NULL,
    IsDeleted            bit           NOT NULL CONSTRAINT DF_ColumnDef_Del DEFAULT(0),
    DeletedAt            datetime2(3)  NULL,
    DeletedByUserId      int           NULL,
    CONSTRAINT PK_ColumnDef PRIMARY KEY (Id),
    CONSTRAINT UQ_ColumnDef UNIQUE (TableDefId, Code),
    -- Ключ під складений FK із doc.CellValue. EF Core його сам не виведе —
    -- створюємо явно (ТЗ §13.5 п.3).
    CONSTRAINT UQ_ColumnDef_ForFk UNIQUE (TableDefId, Id),
    CONSTRAINT FK_ColumnDef_Table FOREIGN KEY (TableDefId) REFERENCES cfg.TableDef (Id),
    CONSTRAINT FK_ColumnDef_Cascade FOREIGN KEY (CascadeFromColumnId) REFERENCES cfg.ColumnDef (Id),
    CONSTRAINT CK_ColumnDef_Month CHECK (IsMonthColumn = 0 OR MonthNumber BETWEEN 1 AND 12),
    CONSTRAINT CK_ColumnDef_Lookup CHECK (DataType <> 5 OR LookupRegistryDefId IS NOT NULL)
);
GO

CREATE TABLE cfg.RowDef
(
    Id              int           IDENTITY(1,1) NOT NULL,
    TableDefId      int           NOT NULL,
    RowKey          nvarchar(100) NOT NULL,   -- стабільна ідентичність, НЕ Ordinal
    Ordinal         int           NOT NULL,   -- лише порядок відображення
    LabelL10n       nvarchar(max) NOT NULL,
    RowKind         tinyint       NOT NULL,   -- RowKind
    ParentRowDefId  int           NULL,
    IsReadOnly      bit           NOT NULL CONSTRAINT DF_RowDef_RO  DEFAULT(0),
    StyleId         int           NULL,
    IsDeleted       bit           NOT NULL CONSTRAINT DF_RowDef_Del DEFAULT(0),
    DeletedAt       datetime2(3)  NULL,
    DeletedByUserId int           NULL,
    CONSTRAINT PK_RowDef PRIMARY KEY (Id),
    CONSTRAINT UQ_RowDef UNIQUE (TableDefId, RowKey),
    CONSTRAINT FK_RowDef_Table FOREIGN KEY (TableDefId) REFERENCES cfg.TableDef (Id),
    CONSTRAINT FK_RowDef_Parent FOREIGN KEY (ParentRowDefId) REFERENCES cfg.RowDef (Id)
);
GO

CREATE TABLE cfg.StyleDef
(
    Id                int           IDENTITY(1,1) NOT NULL,
    TemplateVersionId int           NOT NULL,
    Code              nvarchar(64)  NOT NULL,
    FontName          nvarchar(64)  NULL,
    FontSize          decimal(4,1)  NULL,
    IsBold            bit           NOT NULL CONSTRAINT DF_Style_Bold DEFAULT(0),
    IsItalic          bit           NOT NULL CONSTRAINT DF_Style_Ital DEFAULT(0),
    ForegroundArgb    int           NULL,
    BackgroundArgb    int           NULL,
    BorderJson        nvarchar(500) NULL,
    HorizontalAlign   tinyint       NULL,
    VerticalAlign     tinyint       NULL,
    WrapText          bit           NOT NULL CONSTRAINT DF_Style_Wrap DEFAULT(0),
    NumberFormat      nvarchar(50)  NULL,
    CONSTRAINT PK_StyleDef PRIMARY KEY (Id),
    CONSTRAINT UQ_StyleDef UNIQUE (TemplateVersionId, Code),
    CONSTRAINT FK_StyleDef_TV FOREIGN KEY (TemplateVersionId) REFERENCES cfg.TemplateVersion (Id)
);
GO

CREATE TABLE cfg.FormulaDef
(
    Id            int            IDENTITY(1,1) NOT NULL,
    TableDefId    int            NOT NULL,
    Scope         tinyint        NOT NULL,   -- FormulaScope
    ColumnDefId   int            NULL,
    RowDefId      int            NULL,
    Dialect       tinyint        NOT NULL CONSTRAINT DF_Formula_Dialect DEFAULT(0),
    Expression    nvarchar(2000) NOT NULL,
    -- Топологічна сортовка; обчислюється при Publish, не в рантаймі (ФВ-9.4)
    EvaluationOrder int          NOT NULL CONSTRAINT DF_Formula_Order DEFAULT(0),
    IsCrossSheet  bit            NOT NULL CONSTRAINT DF_Formula_Cross DEFAULT(0),
    IsSnapshot    bit            NOT NULL CONSTRAINT DF_Formula_Snap  DEFAULT(0),
    IsDeleted     bit            NOT NULL CONSTRAINT DF_Formula_Del   DEFAULT(0),
    CONSTRAINT PK_FormulaDef PRIMARY KEY (Id),
    CONSTRAINT FK_Formula_Table  FOREIGN KEY (TableDefId)  REFERENCES cfg.TableDef (Id),
    CONSTRAINT FK_Formula_Column FOREIGN KEY (ColumnDefId) REFERENCES cfg.ColumnDef (Id),
    CONSTRAINT FK_Formula_Row    FOREIGN KEY (RowDefId)    REFERENCES cfg.RowDef (Id),
    CONSTRAINT CK_Formula_Scope CHECK
        ((Scope = 0 AND ColumnDefId IS NOT NULL) OR
         (Scope = 1 AND RowDefId    IS NOT NULL) OR
         (Scope = 2 AND ColumnDefId IS NOT NULL AND RowDefId IS NOT NULL))
);
GO

-- Розкритий граф залежностей. Діапазонів у рантаймі не існує: вони
-- матеріалізуються у список RowKey на момент Publish (B03 §4).
CREATE TABLE cfg.FormulaDependency
(
    Id            bigint         IDENTITY(1,1) NOT NULL,
    SourceKind    tinyint        NOT NULL,   -- 0 Formula, 1 CalculationBinding
    FormulaDefId  int            NULL,
    BindingId     int            NULL,
    DependsOnKind tinyint        NOT NULL,   -- 0 Cell, 1 Header, 2 Registry, 3 CrossPeriod, 4 CrossProject
    TableDefId    int            NULL,
    RowKey        nvarchar(100)  NULL,       -- КОНКРЕТНИЙ рядок, не діапазон
    ColumnDefId   int            NULL,
    FilterJson    nvarchar(max)  NULL,       -- предикат для RowMode = Dynamic (B03 §4.3)
    PeriodOffset  smallint       NULL,       -- [Period:-1] → -1
    SortOrder     int            NOT NULL,
    CONSTRAINT PK_FormulaDependency PRIMARY KEY (Id),
    CONSTRAINT FK_FDep_Formula FOREIGN KEY (FormulaDefId) REFERENCES cfg.FormulaDef (Id),
    CONSTRAINT CK_FDep_Source CHECK
        ((SourceKind = 0 AND FormulaDefId IS NOT NULL) OR
         (SourceKind = 1 AND BindingId    IS NOT NULL))
);
GO

-- Зворотний індекс: «які формули залежать від цієї комірки» — основа
-- інкрементного перерахунку.
CREATE INDEX IX_FormulaDependency_Reverse
    ON cfg.FormulaDependency (TableDefId, RowKey, ColumnDefId)
    INCLUDE (FormulaDefId, BindingId) ON [INDEXES];
GO

CREATE TABLE cfg.ValidationRule
(
    Id           int            IDENTITY(1,1) NOT NULL,
    TableDefId   int            NOT NULL,
    Code         nvarchar(64)   NOT NULL,
    Severity     tinyint        NOT NULL,   -- ValidationSeverity
    Scope        tinyint        NOT NULL,   -- 0 Cell, 1 Row, 2 Table, 3 Document
    ColumnDefId  int            NULL,
    Expression   nvarchar(2000) NOT NULL,
    MessageL10n  nvarchar(max)  NOT NULL,
    IsActive     bit            NOT NULL CONSTRAINT DF_VRule_Active DEFAULT(1),
    CONSTRAINT PK_ValidationRule PRIMARY KEY (Id),
    CONSTRAINT UQ_ValidationRule UNIQUE (TableDefId, Code),
    CONSTRAINT FK_VRule_Table  FOREIGN KEY (TableDefId)  REFERENCES cfg.TableDef (Id),
    CONSTRAINT FK_VRule_Column FOREIGN KEY (ColumnDefId) REFERENCES cfg.ColumnDef (Id)
);
GO

CREATE TABLE cfg.TableRelationDef
(
    Id              int           IDENTITY(1,1) NOT NULL,
    Code            nvarchar(64)  NOT NULL,
    SourceTableDefId int          NOT NULL,
    TargetTableDefId int          NOT NULL,
    RelationKind    tinyint       NOT NULL,   -- TableRelationKind
    MatchJson       nvarchar(max) NOT NULL,   -- як зіставляються рядки
    MapJson         nvarchar(max) NULL,       -- які колонки на які
    OnSourceChange  tinyint       NOT NULL CONSTRAINT DF_Rel_OnChange DEFAULT(0), -- 0 Recalc, 1 Warn, 2 Block
    IsActive        bit           NOT NULL CONSTRAINT DF_Rel_Active DEFAULT(1),
    CONSTRAINT PK_TableRelationDef PRIMARY KEY (Id),
    CONSTRAINT UQ_TableRelationDef UNIQUE (Code),
    CONSTRAINT FK_Rel_Source FOREIGN KEY (SourceTableDefId) REFERENCES cfg.TableDef (Id),
    CONSTRAINT FK_Rel_Target FOREIGN KEY (TargetTableDefId) REFERENCES cfg.TableDef (Id),
    CONSTRAINT CK_Rel_NotSelf CHECK (SourceTableDefId <> TargetTableDefId)
);
GO

-- Заміна кнопки Protect: який аркуш/таблиця в якому періоді доступні (ФВ-2.15)
CREATE TABLE cfg.PeriodAccessRuleDef
(
    Id                int          IDENTITY(1,1) NOT NULL,
    TemplateVersionId int          NOT NULL,
    SheetDefId        int          NULL,
    TableDefId        int          NULL,
    RoleId            int          NULL,       -- NULL = для всіх ролей
    FromSequence      tinyint      NULL,       -- 1..12 для Monthly; NULL = без обмеження
    ToSequence        tinyint      NULL,
    OnOutOfWindow     tinyint      NOT NULL,   -- OutOfWindowBehavior
    CONSTRAINT PK_PeriodAccessRuleDef PRIMARY KEY (Id),
    CONSTRAINT FK_PAR_TV    FOREIGN KEY (TemplateVersionId) REFERENCES cfg.TemplateVersion (Id),
    CONSTRAINT FK_PAR_Sheet FOREIGN KEY (SheetDefId) REFERENCES cfg.SheetDef (Id),
    CONSTRAINT FK_PAR_Table FOREIGN KEY (TableDefId) REFERENCES cfg.TableDef (Id),
    CONSTRAINT CK_PAR_Target CHECK (SheetDefId IS NOT NULL OR TableDefId IS NOT NULL),
    CONSTRAINT CK_PAR_Range  CHECK (FromSequence IS NULL OR ToSequence IS NULL OR FromSequence <= ToSequence)
);
GO

CREATE TABLE cfg.SheetGroupRule
(
    Id                int          IDENTITY(1,1) NOT NULL,
    TemplateVersionId int          NOT NULL,
    SheetGroup        nvarchar(64) NOT NULL,
    RuleKind          tinyint      NOT NULL,   -- 0 RequiresAll, 1 RequiresOne, 2 Excludes
    TargetGroup       nvarchar(64) NULL,
    CONSTRAINT PK_SheetGroupRule PRIMARY KEY (Id),
    CONSTRAINT FK_SGR_TV FOREIGN KEY (TemplateVersionId) REFERENCES cfg.TemplateVersion (Id)
);
GO

CREATE TABLE cfg.RegistryDef
(
    Id               int           IDENTITY(1,1) NOT NULL,
    Code             nvarchar(64)  NOT NULL,
    NameL10n         nvarchar(max) NOT NULL,
    IsTemporal       bit           NOT NULL CONSTRAINT DF_RegDef_Temp DEFAULT(0),
    SourceKind       tinyint       NOT NULL CONSTRAINT DF_RegDef_Src  DEFAULT(2), -- RegistrySourceKind
    -- Ревізія даних: без неї кеш списків або застаріває після синку, або
    -- потребує інвалідації між інстансами (П-6).
    DataRevision     int           NOT NULL CONSTRAINT DF_RegDef_Rev  DEFAULT(0),
    DefinitionVersion int          NOT NULL CONSTRAINT DF_RegDef_Ver  DEFAULT(1),
    IsActive         bit           NOT NULL CONSTRAINT DF_RegDef_Act  DEFAULT(1),
    CONSTRAINT PK_RegistryDef PRIMARY KEY (Id),
    CONSTRAINT UQ_RegistryDef UNIQUE (Code)
);
GO

CREATE TABLE cfg.RegistryFieldDef
(
    Id             int           IDENTITY(1,1) NOT NULL,
    RegistryDefId  int           NOT NULL,
    Code           nvarchar(64)  NOT NULL,
    NameL10n       nvarchar(max) NOT NULL,
    DataType       tinyint       NOT NULL,   -- CellDataType
    Ordinal        int           NOT NULL,
    IsRequired     bit           NOT NULL CONSTRAINT DF_RegField_Req DEFAULT(0),
    IsKey          bit           NOT NULL CONSTRAINT DF_RegField_Key DEFAULT(0),
    UnitId         int           NULL,       -- одиниця поля (ФВ-16.1)
    RefRegistryDefId int         NULL,       -- вкладений реєстр / M:N
    CONSTRAINT PK_RegistryFieldDef PRIMARY KEY (Id),
    CONSTRAINT UQ_RegistryFieldDef UNIQUE (RegistryDefId, Code),
    CONSTRAINT FK_RegField_Reg  FOREIGN KEY (RegistryDefId)    REFERENCES cfg.RegistryDef (Id),
    CONSTRAINT FK_RegField_Ref  FOREIGN KEY (RefRegistryDefId) REFERENCES cfg.RegistryDef (Id)
);
GO

-- Результат методології → колонка документа. Значення НЕ копіюється
-- у doc.CellValue: воно читається за посиланням (D-69, П-33).
CREATE TABLE cfg.CalculationBinding
(
    Id             int          IDENTITY(1,1) NOT NULL,
    TableDefId     int          NOT NULL,
    ColumnDefId    int          NOT NULL,
    MethodologyId  int          NOT NULL,
    OutputCode     nvarchar(64) NOT NULL,   -- який вихід методології
    MatchJson      nvarchar(max) NOT NULL,  -- як зіставити рядок документа з результатом
    IsActive       bit          NOT NULL CONSTRAINT DF_CalcBind_Act DEFAULT(1),
    CONSTRAINT PK_CalculationBinding PRIMARY KEY (Id),
    CONSTRAINT UQ_CalculationBinding UNIQUE (ColumnDefId, MethodologyId, OutputCode),
    CONSTRAINT FK_CalcBind_Table  FOREIGN KEY (TableDefId)  REFERENCES cfg.TableDef (Id),
    CONSTRAINT FK_CalcBind_Column FOREIGN KEY (ColumnDefId) REFERENCES cfg.ColumnDef (Id)
);
GO
```

---

<a id="uom"></a>
## 4. `uom` — одиниці вимірювання

```sql
CREATE TABLE uom.Dimension
(
    Id                     tinyint       NOT NULL,
    Code                   nvarchar(64)  NOT NULL,
    NameL10n               nvarchar(max) NOT NULL,
    BaseUnitId             int           NULL,     -- FK додається після uom.Unit
    IsDerived              bit           NOT NULL CONSTRAINT DF_Dim_Derived DEFAULT(0),
    NumeratorDimensionId   tinyint       NULL,
    DenominatorDimensionId tinyint       NULL,
    CONSTRAINT PK_Dimension PRIMARY KEY (Id),
    CONSTRAINT UQ_Dimension_Code UNIQUE (Code),
    CONSTRAINT FK_Dim_Num FOREIGN KEY (NumeratorDimensionId)   REFERENCES uom.Dimension (Id),
    CONSTRAINT FK_Dim_Den FOREIGN KEY (DenominatorDimensionId) REFERENCES uom.Dimension (Id),
    CONSTRAINT CK_Dim_Derived CHECK
        (IsDerived = 0 OR (NumeratorDimensionId IS NOT NULL AND DenominatorDimensionId IS NOT NULL))
);
GO

CREATE TABLE uom.Unit
(
    Id                int            IDENTITY(1,1) NOT NULL,
    Code              nvarchar(64)   NOT NULL,
    SymbolL10n        nvarchar(max)  NOT NULL,
    NameL10n          nvarchar(max)  NOT NULL,
    DimensionId       tinyint        NOT NULL,
    IsBase            bit            NOT NULL CONSTRAINT DF_Unit_Base DEFAULT(0),
    FactorToBase      decimal(38,18) NOT NULL CONSTRAINT DF_Unit_Factor DEFAULT(1),
    OffsetToBase      decimal(38,18) NOT NULL CONSTRAINT DF_Unit_Offset DEFAULT(0),
    NumeratorUnitId   int            NULL,
    DenominatorUnitId int            NULL,
    DisplayFormat     nvarchar(50)   NULL,
    IsActive          bit            NOT NULL CONSTRAINT DF_Unit_Active DEFAULT(1),
    CONSTRAINT PK_Unit PRIMARY KEY (Id),
    CONSTRAINT UQ_Unit_Code UNIQUE (Code),
    CONSTRAINT FK_Unit_Dim FOREIGN KEY (DimensionId)       REFERENCES uom.Dimension (Id),
    CONSTRAINT FK_Unit_Num FOREIGN KEY (NumeratorUnitId)   REFERENCES uom.Unit (Id),
    CONSTRAINT FK_Unit_Den FOREIGN KEY (DenominatorUnitId) REFERENCES uom.Unit (Id),
    CONSTRAINT CK_Unit_Factor CHECK (FactorToBase <> 0),
    CONSTRAINT CK_Unit_Base   CHECK (IsBase = 0 OR (FactorToBase = 1 AND OffsetToBase = 0))
);
GO

ALTER TABLE uom.Dimension
    ADD CONSTRAINT FK_Dim_BaseUnit FOREIGN KEY (BaseUnitId) REFERENCES uom.Unit (Id);
GO

CREATE UNIQUE INDEX UX_Unit_BasePerDimension
    ON uom.Unit (DimensionId) WHERE IsBase = 1;
GO

-- Явні конверсії: винятки і точні коефіцієнти. Контекстні коефіцієнти
-- (щільність, теплотворність) сюди НЕ потрапляють — вони належать
-- calc.MethodologyConstant (ФВ-16.5). Обмеження нижче робить це неможливим.
CREATE TABLE uom.Conversion
(
    Id         int            IDENTITY(1,1) NOT NULL,
    FromUnitId int            NOT NULL,
    ToUnitId   int            NOT NULL,
    Factor     decimal(38,18) NOT NULL,
    [Offset]   decimal(38,18) NOT NULL CONSTRAINT DF_Conv_Offset DEFAULT(0),
    Kind       tinyint        NOT NULL,   -- 0 Exact, 1 LegacyPinned
    Note       nvarchar(400)  NULL,
    CONSTRAINT PK_Conversion PRIMARY KEY (Id),
    CONSTRAINT UQ_Conversion UNIQUE (FromUnitId, ToUnitId),
    CONSTRAINT FK_Conv_From FOREIGN KEY (FromUnitId) REFERENCES uom.Unit (Id),
    CONSTRAINT FK_Conv_To   FOREIGN KEY (ToUnitId)   REFERENCES uom.Unit (Id),
    CONSTRAINT CK_Conv_NotSelf CHECK (FromUnitId <> ToUnitId),
    CONSTRAINT CK_Conv_Note    CHECK (Kind <> 1 OR Note IS NOT NULL)
);
GO

-- Заборона конверсій між різними розмірностями на рівні БД, а не «домовленості».
-- Це і є механізм, що не дає щільності пролізти в таблицю конверсій.
CREATE FUNCTION uom.fnSameDimension (@from int, @to int)
RETURNS bit
WITH SCHEMABINDING
AS
BEGIN
    DECLARE @r bit = 0;
    SELECT @r = CASE WHEN f.DimensionId = t.DimensionId THEN 1 ELSE 0 END
    FROM uom.Unit f CROSS JOIN uom.Unit t
    WHERE f.Id = @from AND t.Id = @to;
    RETURN ISNULL(@r, 0);
END;
GO

ALTER TABLE uom.Conversion
    ADD CONSTRAINT CK_Conv_SameDimension
        CHECK (uom.fnSameDimension(FromUnitId, ToUnitId) = 1);
GO
```

---

<a id="dic"></a>
## 5. `dic` — дані реєстрів

```sql
CREATE TABLE dic.RegistryEntry
(
    Id              int           IDENTITY(1,1) NOT NULL,
    RegistryDefId   int           NOT NULL,
    Code            nvarchar(100) NOT NULL,
    DisplayL10n     nvarchar(max) NOT NULL,
    ParentEntryId   int           NULL,       -- ієрархія / каскад
    ValidFrom       date          NULL,       -- темпоральність (ФВ-8.5)
    ValidTo         date          NULL,
    Ordinal         int           NOT NULL CONSTRAINT DF_RegEntry_Ord DEFAULT(0),
    IsActive        bit           NOT NULL CONSTRAINT DF_RegEntry_Act DEFAULT(1),
    -- Фізично не видаляється, якщо на нього посилаються дані (ФВ-8.6)
    IsDeleted       bit           NOT NULL CONSTRAINT DF_RegEntry_Del DEFAULT(0),
    DeletedAt       datetime2(3)  NULL,
    DeletedByUserId int           NULL,
    CreatedAt       datetime2(3)  NOT NULL,
    CreatedByUserId int           NOT NULL,
    CONSTRAINT PK_RegistryEntry PRIMARY KEY (Id),
    CONSTRAINT UQ_RegistryEntry UNIQUE (RegistryDefId, Code),
    CONSTRAINT FK_RegEntry_Def    FOREIGN KEY (RegistryDefId) REFERENCES cfg.RegistryDef (Id),
    CONSTRAINT FK_RegEntry_Parent FOREIGN KEY (ParentEntryId) REFERENCES dic.RegistryEntry (Id),
    CONSTRAINT CK_RegEntry_Period CHECK (ValidFrom IS NULL OR ValidTo IS NULL OR ValidFrom <= ValidTo)
);
GO

CREATE INDEX IX_RegistryEntry_Lookup
    ON dic.RegistryEntry (RegistryDefId, IsActive, IsDeleted)
    INCLUDE (Code, Ordinal, ValidFrom, ValidTo) ON [INDEXES];
GO

CREATE TABLE dic.RegistryValue
(
    Id                  bigint         IDENTITY(1,1) NOT NULL,
    RegistryEntryId     int            NOT NULL,
    RegistryFieldDefId  int            NOT NULL,
    ValueString         nvarchar(1000) NULL,
    ValueNumeric        decimal(28,10) NULL,
    ValueDate           datetime2(3)   NULL,
    ValueBool           bit            NULL,
    ValueRefEntryId     int            NULL,
    ValueUnitId         int            NULL,
    CONSTRAINT PK_RegistryValue PRIMARY KEY (Id),
    CONSTRAINT UQ_RegistryValue UNIQUE (RegistryEntryId, RegistryFieldDefId),
    CONSTRAINT FK_RegValue_Entry FOREIGN KEY (RegistryEntryId)    REFERENCES dic.RegistryEntry (Id),
    CONSTRAINT FK_RegValue_Field FOREIGN KEY (RegistryFieldDefId) REFERENCES cfg.RegistryFieldDef (Id),
    CONSTRAINT FK_RegValue_Ref   FOREIGN KEY (ValueRefEntryId)    REFERENCES dic.RegistryEntry (Id),
    CONSTRAINT FK_RegValue_Unit  FOREIGN KEY (ValueUnitId)        REFERENCES uom.Unit (Id)
);
GO

-- Зв'язки M:N між записами реєстрів (напр. дозвіл ↔ забруднюючі речовини)
CREATE TABLE dic.RegistryEntryLink
(
    Id            bigint       IDENTITY(1,1) NOT NULL,
    LeftEntryId   int          NOT NULL,
    RightEntryId  int          NOT NULL,
    LinkKind      nvarchar(64) NOT NULL,
    PayloadJson   nvarchar(max) NULL,        -- параметри зв'язку (ліміт, коефіцієнт)
    CONSTRAINT PK_RegistryEntryLink PRIMARY KEY (Id),
    CONSTRAINT UQ_RegistryEntryLink UNIQUE (LeftEntryId, RightEntryId, LinkKind),
    CONSTRAINT FK_RegLink_Left  FOREIGN KEY (LeftEntryId)  REFERENCES dic.RegistryEntry (Id),
    CONSTRAINT FK_RegLink_Right FOREIGN KEY (RightEntryId) REFERENCES dic.RegistryEntry (Id)
);
GO

-- Зовнішні ідентифікатори: GUID зберігається ПІСЛЯ зіставлення за бізнес-ключем,
-- не замість нього — GUID не переживає перенесення між AF-серверами (ER-I-05).
CREATE TABLE dic.RegistryExternalKey
(
    Id              bigint        IDENTITY(1,1) NOT NULL,
    RegistryEntryId int           NOT NULL,
    DataSourceId    int           NOT NULL,
    ExternalId      nvarchar(200) NOT NULL,
    ExternalPath    nvarchar(400) NULL,
    LastSyncedAt    datetime2(3)  NULL,
    CONSTRAINT PK_RegistryExternalKey PRIMARY KEY (Id),
    CONSTRAINT UQ_RegistryExternalKey UNIQUE (DataSourceId, ExternalId),
    CONSTRAINT FK_RegExtKey_Entry FOREIGN KEY (RegistryEntryId) REFERENCES dic.RegistryEntry (Id)
);
GO
```

---

<a id="doc"></a>
## 6. `doc` — документи і дані

```sql
CREATE TABLE doc.PeriodPolicy
(
    Id                   int          IDENTITY(1,1) NOT NULL,
    Code                 nvarchar(64) NOT NULL,
    OpenOffsetDays       int          NOT NULL CONSTRAINT DF_PP_Open  DEFAULT(0),
    GraceOffsetDays      int          NOT NULL CONSTRAINT DF_PP_Grace DEFAULT(15),
    HardCloseOffsetDays  int          NOT NULL CONSTRAINT DF_PP_Hard  DEFAULT(45),
    YearGraceOffsetDays  int          NOT NULL CONSTRAINT DF_PP_Year  DEFAULT(45),
    CONSTRAINT PK_PeriodPolicy PRIMARY KEY (Id),
    CONSTRAINT UQ_PeriodPolicy UNIQUE (Code),
    CONSTRAINT CK_PP_Order CHECK (GraceOffsetDays <= HardCloseOffsetDays)
);
GO

CREATE TABLE doc.Project
(
    Id                  int           IDENTITY(1,1) NOT NULL,
    Code                nvarchar(64)  NOT NULL,
    NameL10n            nvarchar(max) NOT NULL,
    PeriodStart         date          NOT NULL,
    PeriodEnd           date          NOT NULL,
    [Year]              smallint      NULL,        -- лише підпис для UI, не ідентичність
    TagsJson            nvarchar(500) NULL,
    TemplateVersionId   int           NOT NULL,
    PeriodKind          tinyint       NOT NULL,    -- PeriodKind
    PeriodPolicyId      int           NOT NULL,
    YearGraceOffsetDays int           NOT NULL CONSTRAINT DF_Project_YearGrace DEFAULT(45),
    -- Пояс майданчика: у ньому рахуються межі періодів, offsets і IsLateEdit (D-68)
    TimeZoneId          nvarchar(64)  NOT NULL CONSTRAINT DF_Project_Tz DEFAULT(N'Central Asia Standard Time'),
    -- Поточний період — НАША конфігурація, а не значення з AF (D-77)
    CurrentPeriodMode   tinyint       NOT NULL CONSTRAINT DF_Project_CPMode DEFAULT(0),
    CurrentPeriodId     int           NULL,
    CurrentPeriodPinnedReason nvarchar(400) NULL,
    CurrentPeriodChangedAt    datetime2(3) NULL,
    CurrentPeriodChangedByUserId int   NULL,
    ExternalSettingsJson nvarchar(max) NULL,       -- разове значення при створенні, не залежність
    Status              tinyint       NOT NULL,    -- ProjectStatus
    IsArchiving         bit           NOT NULL CONSTRAINT DF_Project_Arch DEFAULT(0),
    ClosedAt            datetime2(3)  NULL,
    ClosedByUserId      int           NULL,
    CONSTRAINT PK_Project PRIMARY KEY (Id),
    CONSTRAINT UQ_Project_Code UNIQUE (Code),
    CONSTRAINT FK_Project_TV     FOREIGN KEY (TemplateVersionId) REFERENCES cfg.TemplateVersion (Id),
    CONSTRAINT FK_Project_Policy FOREIGN KEY (PeriodPolicyId)    REFERENCES doc.PeriodPolicy (Id),
    CONSTRAINT CK_Project_Period CHECK (PeriodStart <= PeriodEnd),
    CONSTRAINT CK_Project_Pinned CHECK (CurrentPeriodMode <> 1 OR
        (CurrentPeriodId IS NOT NULL AND CurrentPeriodPinnedReason IS NOT NULL))
);
GO

CREATE TABLE doc.Period
(
    Id                int          IDENTITY(1,1) NOT NULL,
    ProjectId         int          NOT NULL,
    PeriodKey         int          NOT NULL,   -- Year*100 + Sequence (R-A6)
    Sequence          tinyint      NOT NULL,
    PeriodStart       date         NOT NULL,
    PeriodEnd         date         NOT NULL,
    State             tinyint      NOT NULL,   -- PeriodState; рахує PeriodStateJob, не запит
    -- Денормалізація обчислених меж — щоб перевірка доступу не рахувала offsets щоразу
    ComputedOpenAt    datetime2(3) NOT NULL,
    ComputedGraceAt   datetime2(3) NOT NULL,
    ComputedCloseAt   datetime2(3) NOT NULL,
    ReopenedUntil     datetime2(3) NULL,
    ReopenReason      nvarchar(400) NULL,
    StateChangedAt    datetime2(3) NOT NULL,
    CONSTRAINT PK_Period PRIMARY KEY (Id),
    CONSTRAINT UQ_Period UNIQUE (ProjectId, PeriodKey),
    CONSTRAINT FK_Period_Project FOREIGN KEY (ProjectId) REFERENCES doc.Project (Id),
    CONSTRAINT CK_Period_Range CHECK (PeriodStart <= PeriodEnd),
    -- 1..12 (D-108). Верхня межа НЕ довільна: партиційна функція перелічує
    -- межі як YYYY01..YYYY12, і Sequence = 13 мовчки ліг би в грудневу
    -- партицію та поїхав в архів разом із груднем. Monthly = 1..12,
    -- Quarterly = 1..4, Yearly = 1, Custom = до 12 періодів на рік.
    CONSTRAINT CK_Period_Seq   CHECK (Sequence BETWEEN 1 AND 12)
);
GO

ALTER TABLE doc.Project
    ADD CONSTRAINT FK_Project_CurrentPeriod FOREIGN KEY (CurrentPeriodId) REFERENCES doc.Period (Id);
GO

CREATE TABLE doc.Document
(
    Id                bigint        IDENTITY(1,1) NOT NULL,
    ProjectId         int           NOT NULL,
    BusinessKey       nvarchar(200) NOT NULL,   -- складається з колонок IsBusinessKey
    NameL10n          nvarchar(max) NULL,
    -- ⛔ Status тут НЕМАЄ (D-93): стан живе у wf.ApprovalState на аркуш×період
    CreatedAt         datetime2(3)  NOT NULL,
    CreatedByUserId   int           NOT NULL,
    ModifiedAt        datetime2(3)  NOT NULL,
    ModifiedByUserId  int           NOT NULL,
    RowVersion        rowversion    NOT NULL,
    CONSTRAINT PK_Document PRIMARY KEY (Id),
    CONSTRAINT UQ_Document UNIQUE (ProjectId, BusinessKey),
    CONSTRAINT FK_Document_Project FOREIGN KEY (ProjectId) REFERENCES doc.Project (Id)
);
GO

CREATE TABLE doc.DocumentSheet
(
    Id           bigint  IDENTITY(1,1) NOT NULL,
    DocumentId   bigint  NOT NULL,
    SheetDefId   int     NOT NULL,
    IsIncluded   bit     NOT NULL CONSTRAINT DF_DocSheet_Inc DEFAULT(1),
    CONSTRAINT PK_DocumentSheet PRIMARY KEY (Id),
    CONSTRAINT UQ_DocumentSheet UNIQUE (DocumentId, SheetDefId),
    CONSTRAINT FK_DocSheet_Doc   FOREIGN KEY (DocumentId) REFERENCES doc.Document (Id),
    CONSTRAINT FK_DocSheet_Sheet FOREIGN KEY (SheetDefId) REFERENCES cfg.SheetDef (Id)
);
GO

-- Партиціонується тією самою схемою, що й TableRow/CellValue — інакше
-- партиційний TRUNCATE при архівації до неї не застосовний (B02 §3.1).
CREATE TABLE doc.TableInstance
(
    PeriodKey    int        NOT NULL,
    Id           bigint     NOT NULL,   -- SEQUENCE doc.TableInstanceSeq
    DocumentId   bigint     NOT NULL,
    TableDefId   int        NOT NULL,
    CreatedAt    datetime2(3) NOT NULL,
    ModifiedAt   datetime2(3) NOT NULL,
    RowVersion   rowversion NOT NULL,
    CONSTRAINT PK_TableInstance PRIMARY KEY CLUSTERED (PeriodKey, Id) ON ps_ByPeriodKey(PeriodKey),
    CONSTRAINT UQ_TableInstance UNIQUE (PeriodKey, DocumentId, TableDefId) ON ps_ByPeriodKey(PeriodKey),
    CONSTRAINT FK_TableInstance_Doc   FOREIGN KEY (DocumentId) REFERENCES doc.Document (Id),
    CONSTRAINT FK_TableInstance_Table FOREIGN KEY (TableDefId) REFERENCES cfg.TableDef (Id)
) ON ps_ByPeriodKey(PeriodKey);
GO

CREATE SEQUENCE doc.TableInstanceSeq AS bigint START WITH 1 INCREMENT BY 1 CACHE 1000;
GO

-- Id із SEQUENCE, а не IDENTITY: значення потрібні ДО вставки, щоб завантажити
-- TableRow і CellValue одним проходом SqlBulkCopy (B02 §2.3).
CREATE SEQUENCE doc.TableRowSeq AS bigint START WITH 1 INCREMENT BY 1 CACHE 1000;
GO

CREATE TABLE doc.TableRow
(
    PeriodKey       int           NOT NULL,
    Id              bigint        NOT NULL,
    TableInstanceId bigint        NOT NULL,
    RowKey          nvarchar(100) NOT NULL,
    RowDefId        int           NULL,      -- для RowMode = Fixed
    Ordinal         int           NOT NULL,
    IsDeleted       bit           NOT NULL CONSTRAINT DF_TableRow_Del DEFAULT(0),
    -- Рядок посилається на запис реєстру, який перестав бути чинним у цьому
    -- періоді (ФВ-8.13, D-98). НЕ обчислюється при читанні зрізу — це вбило б
    -- бюджет 400 мс; ставиться нічною перевіркою інваріантів (ФВ-7.7) і
    -- перерахунком при зміні вікна дії запису реєстру.
    -- Читання не блокує, Submit блокує.
    IsOrphaned      bit           NOT NULL CONSTRAINT DF_TableRow_Orph DEFAULT(0),
    OrphanedAt      datetime2(3)  NULL,
    -- «Дотик» при зміні комірок. Без нього RowVersion не піднімається, і
    -- оптимістичне блокування тихо не працює (B04 §2.4).
    ModifiedAt      datetime2(3)  NOT NULL,
    RowVersion      rowversion    NOT NULL,
    CONSTRAINT PK_TableRow PRIMARY KEY CLUSTERED (PeriodKey, Id) ON ps_ByPeriodKey(PeriodKey),
    CONSTRAINT UQ_TableRow_Key UNIQUE (PeriodKey, TableInstanceId, RowKey) ON ps_ByPeriodKey(PeriodKey),
    CONSTRAINT FK_TableRow_Instance FOREIGN KEY (PeriodKey, TableInstanceId)
        REFERENCES doc.TableInstance (PeriodKey, Id),
    CONSTRAINT FK_TableRow_RowDef FOREIGN KEY (RowDefId) REFERENCES cfg.RowDef (Id)
) ON ps_ByPeriodKey(PeriodKey);
GO

-- ⚠ ОСНОВНИЙ ОБСЯГ: ~108 млн рядків на рік.
-- Некластерних індексів немає жодного: усі альтернативні доступи йдуть через
-- doc.DocumentIndexValue. Ширший ключ у некластерному індексі коштував би
-- більше, ніж дає.
CREATE TABLE doc.CellValue
(
    PeriodKey            int            NOT NULL,
    TableRowId           bigint         NOT NULL,
    ColumnDefId          int            NOT NULL,
    TableDefId           int            NOT NULL,   -- денормалізовано під складений FK
    ValueString          nvarchar(1000) NULL,
    ValueNumeric         decimal(28,10) NULL,
    ValueDate            datetime2(3)   NULL,
    ValueBool            bit            NULL,
    ValueRegistryEntryId int            NULL,
    ValueUnitId          int            NULL,       -- лише для DataType = Unit (R-A4)
    IsCalculated         bit            NOT NULL CONSTRAINT DF_CellValue_Calc  DEFAULT(0),
    IsEmpty              bit            NOT NULL CONSTRAINT DF_CellValue_Empty DEFAULT(0),
    CONSTRAINT PK_CellValue PRIMARY KEY CLUSTERED (PeriodKey, TableRowId, ColumnDefId)
        WITH (DATA_COMPRESSION = PAGE) ON ps_ByPeriodKey(PeriodKey),
    CONSTRAINT FK_CellValue_Row FOREIGN KEY (PeriodKey, TableRowId)
        REFERENCES doc.TableRow (PeriodKey, Id),
    -- Комірка фізично не може потрапити в чужу колонку
    CONSTRAINT FK_CellValue_Column FOREIGN KEY (TableDefId, ColumnDefId)
        REFERENCES cfg.ColumnDef (TableDefId, Id),
    CONSTRAINT FK_CellValue_Entry FOREIGN KEY (ValueRegistryEntryId)
        REFERENCES dic.RegistryEntry (Id),
    CONSTRAINT FK_CellValue_Unit FOREIGN KEY (ValueUnitId) REFERENCES uom.Unit (Id),
    -- Порожня комірка не має значень; заповнена має рівно одне
    CONSTRAINT CK_CellValue_Empty CHECK
        (IsEmpty = 0 OR (ValueString IS NULL AND ValueNumeric IS NULL AND ValueDate IS NULL
                     AND ValueBool IS NULL AND ValueRegistryEntryId IS NULL AND ValueUnitId IS NULL))
) ON ps_ByPeriodKey(PeriodKey);
GO

-- Дублікат IsIndexed-полів для швидких фільтрів по документах
CREATE TABLE doc.DocumentIndexValue
(
    Id           bigint         IDENTITY(1,1) NOT NULL,
    DocumentId   bigint         NOT NULL,
    ColumnDefId  int            NOT NULL,
    ValueString  nvarchar(400)  NULL,
    ValueNumeric decimal(28,10) NULL,
    ValueDate    datetime2(3)   NULL,
    CONSTRAINT PK_DocumentIndexValue PRIMARY KEY (Id),
    CONSTRAINT UQ_DocumentIndexValue UNIQUE (DocumentId, ColumnDefId),
    CONSTRAINT FK_DocIx_Doc    FOREIGN KEY (DocumentId)  REFERENCES doc.Document (Id),
    CONSTRAINT FK_DocIx_Column FOREIGN KEY (ColumnDefId) REFERENCES cfg.ColumnDef (Id)
);
GO

CREATE INDEX IX_DocumentIndexValue_Search
    ON doc.DocumentIndexValue (ColumnDefId, ValueString) INCLUDE (DocumentId) ON [INDEXES];
GO
```

---

<a id="calc"></a>
## 7. `calc` — розрахунки

```sql
CREATE TABLE calc.Methodology
(
    Id       int           IDENTITY(1,1) NOT NULL,
    Code     nvarchar(64)  NOT NULL,
    NameL10n nvarchar(max) NOT NULL,
    [Group]  nvarchar(64)  NULL,
    IsActive bit           NOT NULL CONSTRAINT DF_Meth_Active DEFAULT(1),
    CONSTRAINT PK_Methodology PRIMARY KEY (Id),
    CONSTRAINT UQ_Methodology UNIQUE (Code)
);
GO

CREATE TABLE calc.MethodologyVersion
(
    Id                int            IDENTITY(1,1) NOT NULL,
    MethodologyId     int            NOT NULL,
    Version           nvarchar(20)   NOT NULL,
    Status            tinyint        NOT NULL,   -- TemplateVersionStatus
    [Level]           tinyint        NOT NULL,   -- CalculationLevel
    -- Арифметичний режим: Legacy відтворює числа чинної системи побітово (ФВ-9.9)
    NumericMode       tinyint        NOT NULL CONSTRAINT DF_MV_Numeric  DEFAULT(0),
    -- Джерело Period.Days/Hours/Seconds. Різниця конвенції змінює ВСІ числа (D-78)
    CalendarMode      tinyint        NOT NULL CONSTRAINT DF_MV_Calendar DEFAULT(0),
    TraceLevel        tinyint        NOT NULL CONSTRAINT DF_MV_Trace    DEFAULT(1),
    EffectiveFrom     date           NULL,
    ChangeReason      nvarchar(1000) NULL,
    ContentHash       varbinary(32)  NULL,
    CreatedAt         datetime2(3)   NOT NULL,
    CreatedByUserId   int            NOT NULL,
    PublishedAt       datetime2(3)   NULL,
    PublishedByUserId int            NULL,
    CONSTRAINT PK_MethodologyVersion PRIMARY KEY (Id),
    CONSTRAINT UQ_MethodologyVersion UNIQUE (MethodologyId, Version),
    CONSTRAINT FK_MV_Methodology FOREIGN KEY (MethodologyId) REFERENCES calc.Methodology (Id),
    -- Публікація автором останньої правки заборонена системно (D-40)
    CONSTRAINT CK_MV_FourEyes CHECK (PublishedByUserId IS NULL OR PublishedByUserId <> CreatedByUserId),
    CONSTRAINT CK_MV_Published CHECK (Status <> 1 OR
        (PublishedAt IS NOT NULL AND EffectiveFrom IS NOT NULL AND ChangeReason IS NOT NULL))
);
GO

CREATE TABLE calc.MethodologyFormula
(
    Id                    int            IDENTITY(1,1) NOT NULL,
    MethodologyVersionId  int            NOT NULL,
    Code                  nvarchar(64)   NOT NULL,
    Expression            nvarchar(2000) NOT NULL,
    OutputUnitId          int            NULL,
    -- Порядок НЕ зберігається: він топологічний і рахується при Publish (ФВ-9.4).
    -- Це поле — результат обчислення, а не введення користувача.
    EvaluationOrder       int            NOT NULL CONSTRAINT DF_MF_Order DEFAULT(0),
    CONSTRAINT PK_MethodologyFormula PRIMARY KEY (Id),
    CONSTRAINT UQ_MethodologyFormula UNIQUE (MethodologyVersionId, Code),
    CONSTRAINT FK_MF_Version FOREIGN KEY (MethodologyVersionId) REFERENCES calc.MethodologyVersion (Id),
    CONSTRAINT FK_MF_Unit    FOREIGN KEY (OutputUnitId)         REFERENCES uom.Unit (Id)
);
GO

-- Контекстні коефіцієнти (щільність, теплотворність) — САМЕ ТУТ, а не в
-- uom.Conversion: вони залежать від речовини й умов і змінюються з часом (ФВ-16.5).
CREATE TABLE calc.MethodologyConstant
(
    Id                   int            IDENTITY(1,1) NOT NULL,
    MethodologyVersionId int            NOT NULL,
    Code                 nvarchar(64)   NOT NULL,
    Category             nvarchar(64)   NULL,
    Value                decimal(28,10) NOT NULL,
    UnitId               int            NOT NULL,
    ValidFrom            date           NULL,
    ValidTo              date           NULL,
    SubstanceEntryId     int            NULL,      -- прив'язка до речовини
    [Source]             nvarchar(400)  NULL,      -- звідки взято значення
    CONSTRAINT PK_MethodologyConstant PRIMARY KEY (Id),
    CONSTRAINT UQ_MethodologyConstant UNIQUE (MethodologyVersionId, Code, ISNULL(Category, N''), ISNULL(ValidFrom, '1900-01-01')),
    CONSTRAINT FK_MC_Version   FOREIGN KEY (MethodologyVersionId) REFERENCES calc.MethodologyVersion (Id),
    CONSTRAINT FK_MC_Unit      FOREIGN KEY (UnitId)               REFERENCES uom.Unit (Id),
    CONSTRAINT FK_MC_Substance FOREIGN KEY (SubstanceEntryId)     REFERENCES dic.RegistryEntry (Id),
    CONSTRAINT CK_MC_Period    CHECK (ValidFrom IS NULL OR ValidTo IS NULL OR ValidFrom <= ValidTo)
);
GO

CREATE TABLE calc.MethodologySubstance
(
    Id                   int          IDENTITY(1,1) NOT NULL,
    MethodologyVersionId int          NOT NULL,
    SubstanceEntryId     int          NOT NULL,
    Ordinal              int          NOT NULL,
    CONSTRAINT PK_MethodologySubstance PRIMARY KEY (Id),
    CONSTRAINT UQ_MethodologySubstance UNIQUE (MethodologyVersionId, SubstanceEntryId),
    CONSTRAINT FK_MS_Version   FOREIGN KEY (MethodologyVersionId) REFERENCES calc.MethodologyVersion (Id),
    CONSTRAINT FK_MS_Substance FOREIGN KEY (SubstanceEntryId)     REFERENCES dic.RegistryEntry (Id)
);
GO

-- Прив'язка методології до таблиць ПРАВИЛАМИ, а не жорстким списком (ФВ-13.3)
CREATE TABLE calc.MethodologyRule
(
    Id                   int           IDENTITY(1,1) NOT NULL,
    MethodologyVersionId int           NOT NULL,
    Code                 nvarchar(64)  NOT NULL,
    MatchJson            nvarchar(max) NOT NULL,
    Priority             int           NOT NULL CONSTRAINT DF_MR_Prio DEFAULT(100),
    IsActive             bit           NOT NULL CONSTRAINT DF_MR_Act  DEFAULT(1),
    CONSTRAINT PK_MethodologyRule PRIMARY KEY (Id),
    CONSTRAINT UQ_MethodologyRule UNIQUE (MethodologyVersionId, Code),
    CONSTRAINT FK_MR_Version FOREIGN KEY (MethodologyVersionId) REFERENCES calc.MethodologyVersion (Id)
);
GO

CREATE TABLE calc.MethodologyOutput
(
    Id                   int          IDENTITY(1,1) NOT NULL,
    MethodologyVersionId int          NOT NULL,
    Code                 nvarchar(64) NOT NULL,   -- 'tons', 'gsec'
    UnitId               int          NOT NULL,
    Ordinal              int          NOT NULL,
    CONSTRAINT PK_MethodologyOutput PRIMARY KEY (Id),
    CONSTRAINT UQ_MethodologyOutput UNIQUE (MethodologyVersionId, Code),
    CONSTRAINT FK_MO_Version FOREIGN KEY (MethodologyVersionId) REFERENCES calc.MethodologyVersion (Id),
    CONSTRAINT FK_MO_Unit    FOREIGN KEY (UnitId)               REFERENCES uom.Unit (Id)
);
GO

-- Рівень 2 драбини: скрипт як ДАНІ. Компілюється при публікації, не при прогоні.
CREATE TABLE calc.ScriptVersion
(
    Id                   int            IDENTITY(1,1) NOT NULL,
    MethodologyVersionId int            NOT NULL,
    SourceCode           nvarchar(max)  NOT NULL,
    CompiledAt           datetime2(3)   NULL,
    CompilerDiagnostics  nvarchar(max)  NULL,
    HasGreenTest         bit            NOT NULL CONSTRAINT DF_SV_Test DEFAULT(0),
    ContentHash          varbinary(32)  NOT NULL,
    CONSTRAINT PK_ScriptVersion PRIMARY KEY (Id),
    CONSTRAINT UQ_ScriptVersion UNIQUE (MethodologyVersionId),
    CONSTRAINT FK_SV_Version FOREIGN KEY (MethodologyVersionId) REFERENCES calc.MethodologyVersion (Id)
);
GO

CREATE TABLE calc.CalculationRun
(
    Id                bigint        IDENTITY(1,1) NOT NULL,
    ProjectId         int           NOT NULL,
    PeriodKey         int           NULL,        -- NULL = повний рік
    TriggeredByUserId int           NULL,        -- NULL = за розкладом
    StartedAt         datetime2(3)  NOT NULL,
    FinishedAt        datetime2(3)  NULL,
    Status            nvarchar(32)  NOT NULL,
    ModulesProfileJson nvarchar(max) NULL,       -- профіль по модулях (питання J-1)
    ErrorMessage      nvarchar(2000) NULL,
    CONSTRAINT PK_CalculationRun PRIMARY KEY (Id),
    CONSTRAINT FK_CR_Project FOREIGN KEY (ProjectId) REFERENCES doc.Project (Id)
);
GO

-- Результати НЕ пишуться в doc.CellValue: нічний перерахунок інакше писав би
-- десятки мільйонів рядків у партиції документів і роздував аудит (П-33).
CREATE TABLE calc.CalculationResult
(
    PeriodKey            int            NOT NULL,
    Id                   bigint         NOT NULL,
    CalculationRunId     bigint         NOT NULL,
    MethodologyVersionId int            NOT NULL,
    DocumentId           bigint         NOT NULL,
    SourceRowKey         nvarchar(100)  NULL,
    SubstanceEntryId     int            NULL,
    OutputCode           nvarchar(64)   NOT NULL,
    Value                decimal(28,10) NOT NULL,   -- float заборонений (D-30)
    UnitId               int            NOT NULL,
    CONSTRAINT PK_CalculationResult PRIMARY KEY CLUSTERED (PeriodKey, Id) ON ps_ByPeriodKey(PeriodKey),
    CONSTRAINT FK_CRes_Run    FOREIGN KEY (CalculationRunId)     REFERENCES calc.CalculationRun (Id),
    CONSTRAINT FK_CRes_MV     FOREIGN KEY (MethodologyVersionId) REFERENCES calc.MethodologyVersion (Id),
    CONSTRAINT FK_CRes_Unit   FOREIGN KEY (UnitId)               REFERENCES uom.Unit (Id)
) ON ps_ByPeriodKey(PeriodKey);
GO

CREATE SEQUENCE calc.CalculationResultSeq AS bigint START WITH 1 INCREMENT BY 1 CACHE 1000;
GO

CREATE INDEX IX_CalculationResult_Lookup
    ON calc.CalculationResult (PeriodKey, DocumentId, MethodologyVersionId, OutputCode)
    INCLUDE (Value, UnitId, SubstanceEntryId, SourceRowKey) ON ps_ByPeriodKey(PeriodKey);
GO

CREATE TABLE calc.CalculationInput
(
    PeriodKey        int            NOT NULL,
    Id               bigint         NOT NULL,
    CalculationRunId bigint         NOT NULL,
    DocumentId       bigint         NOT NULL,
    SourceRowKey     nvarchar(100)  NULL,
    ArgumentCode     nvarchar(64)   NOT NULL,
    Value            decimal(28,10) NULL,
    ValueString      nvarchar(400)  NULL,
    UnitId           int            NULL,
    CONSTRAINT PK_CalculationInput PRIMARY KEY CLUSTERED (PeriodKey, Id) ON ps_ByPeriodKey(PeriodKey),
    CONSTRAINT FK_CIn_Run FOREIGN KEY (CalculationRunId) REFERENCES calc.CalculationRun (Id)
) ON ps_ByPeriodKey(PeriodKey);
GO

CREATE SEQUENCE calc.CalculationInputSeq AS bigint START WITH 1 INCREMENT BY 1 CACHE 1000;
GO

-- Обсяг керується РІВНЕМ ТРЕЙСУ, а не строком зберігання (ЗБР-3):
-- записане живе назавжди, непотрібне просто не пишеться.
CREATE TABLE calc.CalculationStep
(
    PeriodKey        int            NOT NULL,
    Id               bigint         NOT NULL,
    CalculationRunId bigint         NOT NULL,
    ResultId         bigint         NULL,
    StepOrder        int            NOT NULL,
    StepCode         nvarchar(64)   NOT NULL,
    Expression       nvarchar(2000) NULL,
    Value            decimal(28,10) NULL,
    TraceJson        nvarchar(max)  NULL,
    CONSTRAINT PK_CalculationStep PRIMARY KEY CLUSTERED (PeriodKey, Id) ON ps_ByPeriodKey(PeriodKey),
    CONSTRAINT FK_CStep_Run FOREIGN KEY (CalculationRunId) REFERENCES calc.CalculationRun (Id)
) ON ps_ByPeriodKey(PeriodKey);
GO

CREATE SEQUENCE calc.CalculationStepSeq AS bigint START WITH 1 INCREMENT BY 1 CACHE 1000;
GO

-- Іммутабельний зріз при поданні: входи + результати + версії + NumericMode.
-- САМЕ ЦЕ, а не трейс, є доказовою базою (ФВ-5.7, ЗБР-3).
CREATE TABLE calc.SubmissionSnapshot
(
    Id                   bigint        IDENTITY(1,1) NOT NULL,
    DocumentId           bigint        NOT NULL,
    SheetDefId           int           NOT NULL,
    PeriodKey            int           NOT NULL,
    TemplateVersionId    int           NOT NULL,
    MethodologyVersionsJson nvarchar(max) NOT NULL,
    NumericMode          tinyint       NOT NULL,
    CalendarMode         tinyint       NOT NULL,
    PayloadJson          nvarchar(max) NOT NULL,   -- входи і результати
    ContentHash          varbinary(32) NOT NULL,
    SubmittedAt          datetime2(3)  NOT NULL,
    SubmittedByUserId    int           NOT NULL,
    CONSTRAINT PK_SubmissionSnapshot PRIMARY KEY (Id),
    CONSTRAINT FK_SS_Doc FOREIGN KEY (DocumentId) REFERENCES doc.Document (Id)
);
GO
```

---

<a id="rpt"></a>
## 8. `rpt` — звітність

> **Зріз без логіки.** Агрегації виконує сервіс при побудові; вʼюха лише
> проєктує. Індексовані вʼюхи з обчисленнями неприпустимі (ФВ-0.3).

```sql
CREATE TABLE rpt.ReportDef
(
    Id       int           IDENTITY(1,1) NOT NULL,
    Code     nvarchar(64)  NOT NULL,
    NameL10n nvarchar(max) NOT NULL,
    IsRegulatory bit       NOT NULL CONSTRAINT DF_RepDef_Reg DEFAULT(0),
    IsActive bit           NOT NULL CONSTRAINT DF_RepDef_Act DEFAULT(1),
    CONSTRAINT PK_ReportDef PRIMARY KEY (Id),
    CONSTRAINT UQ_ReportDef UNIQUE (Code)
);
GO

CREATE TABLE rpt.ReportVersion
(
    Id           int           IDENTITY(1,1) NOT NULL,
    ReportDefId  int           NOT NULL,
    Version      nvarchar(20)  NOT NULL,
    Status       tinyint       NOT NULL,
    ColumnsJson  nvarchar(max) NOT NULL,
    RulesJson    nvarchar(max) NOT NULL,
    CreatedAt    datetime2(3)  NOT NULL,
    CONSTRAINT PK_ReportVersion PRIMARY KEY (Id),
    CONSTRAINT UQ_ReportVersion UNIQUE (ReportDefId, Version),
    CONSTRAINT FK_RV_Def FOREIGN KEY (ReportDefId) REFERENCES rpt.ReportDef (Id)
);
GO

CREATE TABLE rpt.ReportSnapshot
(
    Id               bigint        IDENTITY(1,1) NOT NULL,
    ReportVersionId  int           NOT NULL,
    ProjectId        int           NOT NULL,
    PeriodKey        int           NULL,
    ParametersJson   nvarchar(max) NULL,
    CalculationRunId bigint        NULL,
    -- Статус успадковується від даних (D-65): регуляторні вʼюхи віддають
    -- лише Approved і Submitted.
    Status           tinyint       NOT NULL CONSTRAINT DF_Snap_Status DEFAULT(0),
    IsCurrent        bit           NOT NULL CONSTRAINT DF_Snap_Current DEFAULT(0),
    RowCount         int           NOT NULL CONSTRAINT DF_Snap_Rows DEFAULT(0),
    ContentHash      varbinary(32) NULL,
    BuiltAt          datetime2(3)  NOT NULL,
    BuiltByUserId    int           NULL,
    CONSTRAINT PK_ReportSnapshot PRIMARY KEY (Id),
    CONSTRAINT FK_Snap_Version FOREIGN KEY (ReportVersionId) REFERENCES rpt.ReportVersion (Id),
    CONSTRAINT FK_Snap_Project FOREIGN KEY (ProjectId)       REFERENCES doc.Project (Id)
);
GO

CREATE UNIQUE INDEX UX_ReportSnapshot_Current
    ON rpt.ReportSnapshot (ReportVersionId, ProjectId, PeriodKey) WHERE IsCurrent = 1;
GO

CREATE TABLE rpt.ReportRow
(
    SnapshotId   bigint         NOT NULL,
    RowNo        int            NOT NULL,
    ColumnCode   nvarchar(64)   NOT NULL,
    ValueString  nvarchar(1000) NULL,
    ValueNumeric decimal(28,10) NULL,
    ValueDate    datetime2(3)   NULL,
    CONSTRAINT PK_ReportRow PRIMARY KEY CLUSTERED (SnapshotId, RowNo, ColumnCode),
    CONSTRAINT FK_RepRow_Snap FOREIGN KEY (SnapshotId) REFERENCES rpt.ReportSnapshot (Id)
);
GO
```

```sql
-- src/Ecr.Infrastructure/Persistence/Sql/05-rpt-views.sql
-- Генерується під кожен звіт. ФІЛЬТР ЗА СТАТУСОМ — У ВʼЮСІ, не в RDL (D-65):
-- інакше його одного дня забудуть поставити.
CREATE OR ALTER VIEW rpt.v_WaterReport_v1
AS
SELECT s.ProjectId, s.PeriodKey, r.RowNo, r.ColumnCode,
       r.ValueString, r.ValueNumeric, r.ValueDate,
       s.BuiltAt, s.Status
FROM rpt.ReportSnapshot s
JOIN rpt.ReportRow      r ON r.SnapshotId = s.Id
JOIN rpt.ReportVersion  v ON v.Id = s.ReportVersionId
JOIN rpt.ReportDef      d ON d.Id = v.ReportDefId
WHERE d.Code = N'WaterReport'
  AND s.IsCurrent = 1
  AND s.Status IN (1, 2);   -- Approved, Submitted
GO
```

---

<a id="ext"></a>
## 9. `ext` — зовнішні джерела і legacy-мапінг

> **Уся специфіка PI AF і Excel живе тільки тут.** Ядро про неї не знає —
> перевіряється архітектурним тестом (ФВ-11.9).

```sql
CREATE TABLE ext.DataSource
(
    Id                int           IDENTITY(1,1) NOT NULL,
    Code              nvarchar(64)  NOT NULL,
    NameL10n          nvarchar(max) NOT NULL,
    Transport         tinyint       NOT NULL,   -- ExternalTransport
    Endpoint          nvarchar(400) NOT NULL,
    SecondaryEndpoint nvarchar(400) NULL,
    -- ЛИШЕ ім'я секрету, ніколи значення (ФВ-6.11)
    SecretName        nvarchar(100) NOT NULL,
    Catalog           nvarchar(200) NULL,
    MaxParallel       int           NOT NULL CONSTRAINT DF_DS_Par DEFAULT(4),
    IsActive          bit           NOT NULL CONSTRAINT DF_DS_Act DEFAULT(1),
    CONSTRAINT PK_DataSource PRIMARY KEY (Id),
    CONSTRAINT UQ_DataSource UNIQUE (Code)
);
GO

CREATE TABLE ext.SourceEntity
(
    Id            int           IDENTITY(1,1) NOT NULL,
    DataSourceId  int           NOT NULL,
    Code          nvarchar(200) NOT NULL,
    DisplayName   nvarchar(400) NULL,
    EntityPath    nvarchar(400) NULL,
    SourceKind    tinyint       NOT NULL CONSTRAINT DF_SE_Kind DEFAULT(0), -- RegistrySourceKind
    RegistryDefId int           NULL,
    IsActive      bit           NOT NULL CONSTRAINT DF_SE_Act DEFAULT(1),
    CONSTRAINT PK_SourceEntity PRIMARY KEY (Id),
    CONSTRAINT UQ_SourceEntity UNIQUE (DataSourceId, Code),
    CONSTRAINT FK_SE_DataSource FOREIGN KEY (DataSourceId)  REFERENCES ext.DataSource (Id),
    CONSTRAINT FK_SE_Registry   FOREIGN KEY (RegistryDefId) REFERENCES cfg.RegistryDef (Id)
);
GO

-- Одиниці на межі інтеграції: атрибути PI AF мають власний UOM, і це
-- найчастіше джерело мовчазних розбіжностей у числах (ФВ-16.9).
CREATE TABLE ext.EntityFieldMap
(
    Id             int           IDENTITY(1,1) NOT NULL,
    SourceEntityId int           NOT NULL,
    SourceField    nvarchar(200) NOT NULL,
    SourceUnitId   int           NULL,
    TargetKind     tinyint       NOT NULL,   -- 0 Column, 1 RegistryField
    TargetColumnDefId       int   NULL,
    TargetRegistryFieldDefId int  NULL,
    TargetUnitId   int           NULL,
    TransformCode  nvarchar(64)  NULL,
    IsActive       bit           NOT NULL CONSTRAINT DF_EFM_Act DEFAULT(1),
    CONSTRAINT PK_EntityFieldMap PRIMARY KEY (Id),
    CONSTRAINT UQ_EntityFieldMap UNIQUE (SourceEntityId, SourceField),
    CONSTRAINT FK_EFM_Entity FOREIGN KEY (SourceEntityId)  REFERENCES ext.SourceEntity (Id),
    CONSTRAINT FK_EFM_Column FOREIGN KEY (TargetColumnDefId) REFERENCES cfg.ColumnDef (Id),
    CONSTRAINT FK_EFM_Field  FOREIGN KEY (TargetRegistryFieldDefId) REFERENCES cfg.RegistryFieldDef (Id),
    CONSTRAINT FK_EFM_SrcUnit FOREIGN KEY (SourceUnitId) REFERENCES uom.Unit (Id),
    CONSTRAINT FK_EFM_TgtUnit FOREIGN KEY (TargetUnitId) REFERENCES uom.Unit (Id),
    CONSTRAINT CK_EFM_Target CHECK
        ((TargetKind = 0 AND TargetColumnDefId IS NOT NULL) OR
         (TargetKind = 1 AND TargetRegistryFieldDefId IS NOT NULL))
);
GO

CREATE TABLE ext.CollectionSchedule
(
    Id             int          IDENTITY(1,1) NOT NULL,
    SourceEntityId int          NOT NULL,
    CronExpression nvarchar(100) NOT NULL,
    LookbackDays   int          NOT NULL CONSTRAINT DF_CS_Look DEFAULT(7),
    IsEnabled      bit          NOT NULL CONSTRAINT DF_CS_En   DEFAULT(1),
    LastRunAt      datetime2(3) NULL,
    Watermark      datetime2(3) NULL,   -- оптимізація, не стан: втрата не коштує даних
    CONSTRAINT PK_CollectionSchedule PRIMARY KEY (Id),
    CONSTRAINT UQ_CollectionSchedule UNIQUE (SourceEntityId),
    CONSTRAINT FK_CS_Entity FOREIGN KEY (SourceEntityId) REFERENCES ext.SourceEntity (Id)
);
GO

-- Сирі дані зберігаються В ОДИНИЦІ ДЖЕРЕЛА — інакше повторний перерахунок
-- з архіву дасть інший результат (ФВ-16.9).
CREATE TABLE ext.RawDataPoint
(
    Id             bigint         IDENTITY(1,1) NOT NULL,
    SourceEntityId int            NOT NULL,
    SourcePath     nvarchar(400)  NOT NULL,
    [Timestamp]    datetime2(3)   NOT NULL,
    ValueNumeric   decimal(28,10) NULL,
    ValueString    nvarchar(1000) NULL,
    UnitId         int            NULL,
    Quality        nvarchar(32)   NULL,
    RetrievedAt    datetime2(3)   NOT NULL,
    CollectionRunId bigint        NOT NULL,
    CONSTRAINT PK_RawDataPoint PRIMARY KEY (Id),
    -- Ідемпотентність збору за природним ключем (ФВ-11.3)
    CONSTRAINT UQ_RawDataPoint UNIQUE (SourceEntityId, SourcePath, [Timestamp]),
    CONSTRAINT FK_RDP_Entity FOREIGN KEY (SourceEntityId) REFERENCES ext.SourceEntity (Id),
    CONSTRAINT FK_RDP_Unit   FOREIGN KEY (UnitId)         REFERENCES uom.Unit (Id)
);
GO

CREATE TABLE ext.ConsistencyRule
(
    Id             int            IDENTITY(1,1) NOT NULL,
    Code           nvarchar(64)   NOT NULL,
    SourceEntityId int            NULL,
    Expression     nvarchar(2000) NOT NULL,
    Severity       tinyint        NOT NULL,
    IsActive       bit            NOT NULL CONSTRAINT DF_CR_Act DEFAULT(1),
    CONSTRAINT PK_ConsistencyRule PRIMARY KEY (Id),
    CONSTRAINT UQ_ConsistencyRule UNIQUE (Code)
);
GO

-- Legacy-мапінг: усе, що знає про Excel і AF
CREATE TABLE ext.LegacySheetMapping
(
    Id         int           IDENTITY(1,1) NOT NULL,
    SheetDefId int           NOT NULL,
    LegacyName nvarchar(200) NOT NULL,   -- '7. Water Report'
    CONSTRAINT PK_LegacySheetMapping PRIMARY KEY (Id),
    CONSTRAINT UQ_LegacySheetMapping UNIQUE (SheetDefId),
    CONSTRAINT FK_LSM_Sheet FOREIGN KEY (SheetDefId) REFERENCES cfg.SheetDef (Id)
);
GO

CREATE TABLE ext.LegacyTableMapping
(
    Id                  int           IDENTITY(1,1) NOT NULL,
    TableDefId          int           NOT NULL,
    EventFrameTemplate  nvarchar(200) NULL,
    ElementTemplate     nvarchar(200) NULL,
    ElementName         nvarchar(200) NULL,
    TemplateRow         int           NULL,
    RowOffsetBase       int           NULL,
    CONSTRAINT PK_LegacyTableMapping PRIMARY KEY (Id),
    CONSTRAINT UQ_LegacyTableMapping UNIQUE (TableDefId),
    CONSTRAINT FK_LTM_Table FOREIGN KEY (TableDefId) REFERENCES cfg.TableDef (Id)
);
GO

CREATE TABLE ext.LegacyRowMapping
(
    Id               int           IDENTITY(1,1) NOT NULL,
    RowDefId         int           NOT NULL,
    AfAttributeName  nvarchar(100) NULL,   -- 'Attribute_0010'
    ExcelRow         int           NULL,
    CONSTRAINT PK_LegacyRowMapping PRIMARY KEY (Id),
    CONSTRAINT UQ_LegacyRowMapping UNIQUE (RowDefId),
    CONSTRAINT FK_LRM_Row FOREIGN KEY (RowDefId) REFERENCES cfg.RowDef (Id)
);
GO

CREATE TABLE ext.LegacyColumnMapping
(
    Id                int           IDENTITY(1,1) NOT NULL,
    ColumnDefId       int           NOT NULL,
    -- Позиція поля в ;-рядку. Порядок індивідуальний для кожної з ~90 таблиць —
    -- потрібен для валідації міграції (ІНТ-11).
    LegacyFieldIndex  int           NULL,
    LegacyExcelColumn nvarchar(10)  NULL,
    AfAttributeName   nvarchar(100) NULL,
    CONSTRAINT PK_LegacyColumnMapping PRIMARY KEY (Id),
    CONSTRAINT UQ_LegacyColumnMapping UNIQUE (ColumnDefId),
    CONSTRAINT FK_LCM_Column FOREIGN KEY (ColumnDefId) REFERENCES cfg.ColumnDef (Id)
);
GO
```

---

<a id="wf"></a>
## 10. `wf` — робочий процес

```sql
CREATE TABLE wf.ApprovalRoute
(
    Id                int           IDENTITY(1,1) NOT NULL,
    Code              nvarchar(64)  NOT NULL,
    NameL10n          nvarchar(max) NOT NULL,
    TemplateVersionId int           NULL,
    IsActive          bit           NOT NULL CONSTRAINT DF_AR_Act DEFAULT(1),
    CONSTRAINT PK_ApprovalRoute PRIMARY KEY (Id),
    CONSTRAINT UQ_ApprovalRoute UNIQUE (Code)
);
GO

CREATE TABLE wf.ApprovalStep
(
    Id              int          IDENTITY(1,1) NOT NULL,
    ApprovalRouteId int          NOT NULL,
    Ordinal         int          NOT NULL,
    RoleId          int          NOT NULL,
    IsOptional      bit          NOT NULL CONSTRAINT DF_AS_Opt DEFAULT(0),
    CONSTRAINT PK_ApprovalStep PRIMARY KEY (Id),
    CONSTRAINT UQ_ApprovalStep UNIQUE (ApprovalRouteId, Ordinal),
    CONSTRAINT FK_AS_Route FOREIGN KEY (ApprovalRouteId) REFERENCES wf.ApprovalRoute (Id)
);
GO

-- Гранулярність — АРКУШ × ПЕРІОД (D-38), не документ цілком
CREATE TABLE wf.ApprovalState
(
    Id                bigint        IDENTITY(1,1) NOT NULL,
    DocumentId        bigint        NOT NULL,
    SheetDefId        int           NOT NULL,
    PeriodKey         int           NOT NULL,
    Status            tinyint       NOT NULL,   -- DocumentStatus
    CurrentStepId     int           NULL,
    SubmittedAt       datetime2(3)  NULL,
    SubmittedByUserId int           NULL,
    ApprovedAt        datetime2(3)  NULL,
    ApprovedByUserId  int           NULL,
    RejectedReason    nvarchar(1000) NULL,
    ReopenedAt        datetime2(3)  NULL,
    ReopenedByUserId  int           NULL,
    ReopenReason      nvarchar(1000) NULL,
    RowVersion        rowversion    NOT NULL,
    CONSTRAINT PK_ApprovalState PRIMARY KEY (Id),
    CONSTRAINT UQ_ApprovalState UNIQUE (DocumentId, SheetDefId, PeriodKey),
    CONSTRAINT FK_ApprState_Doc   FOREIGN KEY (DocumentId) REFERENCES doc.Document (Id),
    CONSTRAINT FK_ApprState_Sheet FOREIGN KEY (SheetDefId) REFERENCES cfg.SheetDef (Id),
    -- Reopen вимагає причини (D-67)
    CONSTRAINT CK_ApprState_Reopen CHECK (ReopenedAt IS NULL OR ReopenReason IS NOT NULL)
);
GO

CREATE TABLE wf.ValidationResult
(
    Id           bigint        IDENTITY(1,1) NOT NULL,
    DocumentId   bigint        NOT NULL,
    PeriodKey    int           NOT NULL,
    RunAt        datetime2(3)  NOT NULL,
    ErrorCount   int           NOT NULL,
    WarningCount int           NOT NULL,
    InfoCount    int           NOT NULL,
    MessagesJson nvarchar(max) NOT NULL,
    CONSTRAINT PK_ValidationResult PRIMARY KEY (Id),
    CONSTRAINT FK_VRes_Doc FOREIGN KEY (DocumentId) REFERENCES doc.Document (Id)
);
GO
```

---

<a id="sec"></a>
## 11. `sec` — безпека

```sql
CREATE TABLE sec.PasswordPolicy
(
    Id                  int          IDENTITY(1,1) NOT NULL,
    Code                nvarchar(64) NOT NULL,
    MinLength           int          NOT NULL CONSTRAINT DF_PwdP_Len DEFAULT(12),
    RequireUpper        bit          NOT NULL CONSTRAINT DF_PwdP_Up  DEFAULT(1),
    RequireDigit        bit          NOT NULL CONSTRAINT DF_PwdP_Dig DEFAULT(1),
    RequireSpecial      bit          NOT NULL CONSTRAINT DF_PwdP_Spc DEFAULT(0),
    MaxFailedAttempts   int          NOT NULL CONSTRAINT DF_PwdP_Att DEFAULT(5),
    LockoutMinutes      int          NOT NULL CONSTRAINT DF_PwdP_Lck DEFAULT(15),
    ExpirationDays      int          NULL,
    CONSTRAINT PK_PasswordPolicy PRIMARY KEY (Id),
    CONSTRAINT UQ_PasswordPolicy UNIQUE (Code)
);
GO

CREATE TABLE sec.[User]
(
    Id             int           IDENTITY(1,1) NOT NULL,
    UserName       nvarchar(200) NOT NULL,
    DisplayName    nvarchar(200) NOT NULL,
    Email          nvarchar(320) NULL,
    Provider       tinyint       NOT NULL,   -- AuthProvider
    -- Заповнений лише для Windows; у локальних SID немає — і саме тому
    -- автором дії в аудиті є UserId, а не SID (R-A2).
    WindowsSid     nvarchar(200) NULL,
    PasswordHash   nvarchar(400) NULL,
    PasswordPolicyId int         NULL,
    -- Перевіряється на КОЖЕН запит: відкликання прав діє негайно
    SecurityStamp  nvarchar(64)  NOT NULL,
    FailedAttempts int           NOT NULL CONSTRAINT DF_User_Failed DEFAULT(0),
    LockedUntil    datetime2(3)  NULL,
    -- Пароль виданий разово і має бути змінений при першому вході (D-97).
    -- Доки прапорець стоїть, дозволені лише зміна пароля і вихід.
    MustChangePassword bit       NOT NULL CONSTRAINT DF_User_MustChg DEFAULT(0),
    -- Технічний обліковий запис первинного налаштування. Вимикається
    -- автоматично, щойно з'явився хоч один активний доменний адміністратор.
    IsBootstrapAdmin bit         NOT NULL CONSTRAINT DF_User_Bootstrap DEFAULT(0),
    IsActive       bit           NOT NULL CONSTRAINT DF_User_Active DEFAULT(1),
    CreatedAt      datetime2(3)  NOT NULL,
    CreatedByUserId int          NULL,
    CONSTRAINT PK_User PRIMARY KEY (Id),
    CONSTRAINT UQ_User_Name UNIQUE (UserName),
    CONSTRAINT FK_User_Policy FOREIGN KEY (PasswordPolicyId) REFERENCES sec.PasswordPolicy (Id),
    CONSTRAINT CK_User_Provider CHECK
        ((Provider = 0 AND WindowsSid IS NOT NULL AND PasswordHash IS NULL) OR
         (Provider = 1 AND PasswordHash IS NOT NULL AND WindowsSid IS NULL))
);
GO

CREATE UNIQUE INDEX UX_User_Sid ON sec.[User] (WindowsSid) WHERE WindowsSid IS NOT NULL;
GO

-- Bootstrap-адміністратор може бути лише один і лише локальний (D-97).
CREATE UNIQUE INDEX UX_User_Bootstrap ON sec.[User] (IsBootstrapAdmin)
    WHERE IsBootstrapAdmin = 1;
GO

-- Сеанс симуляції «очима користувача» (D-96). Симуляція — ЛИШЕ ЧИТАННЯ:
-- будь-яка спроба запису під нею відхиляється EditDenyReason.SimulationReadOnly.
-- Запис у цю таблицю обов'язковий: інакше «подивитися очима» стає способом
-- безслідно переглянути чужі дані.
CREATE TABLE aud.SimulationSession
(
    Id             bigint       IDENTITY(1,1) NOT NULL,
    ActorUserId    int          NOT NULL,   -- хто симулює
    SubjectUserId  int          NOT NULL,   -- чиїми очима
    Reason         nvarchar(1000) NOT NULL,
    StartedAt      datetime2(3) NOT NULL,
    EndedAt        datetime2(3) NULL,
    CONSTRAINT PK_SimSession PRIMARY KEY (Id),
    CONSTRAINT FK_SimSession_Actor   FOREIGN KEY (ActorUserId)   REFERENCES sec.[User] (Id),
    CONSTRAINT FK_SimSession_Subject FOREIGN KEY (SubjectUserId) REFERENCES sec.[User] (Id),
    CONSTRAINT CK_SimSession_NotSelf CHECK (ActorUserId <> SubjectUserId)
);
GO

CREATE INDEX IX_SimSession_Actor ON aud.SimulationSession (ActorUserId, StartedAt DESC);
GO

CREATE TABLE sec.Permission
(
    Code        nvarchar(64)  NOT NULL,
    [Group]     nvarchar(64)  NOT NULL,
    NameL10n    nvarchar(max) NOT NULL,
    -- Небезпечні права видаються окремо і не входять до складених ролей (ФВ-6.12)
    IsDangerous bit           NOT NULL CONSTRAINT DF_Perm_Dang DEFAULT(0),
    CONSTRAINT PK_Permission PRIMARY KEY (Code)
);
GO

CREATE TABLE sec.Role
(
    Id          int           IDENTITY(1,1) NOT NULL,
    Code        nvarchar(64)  NOT NULL,
    NameL10n    nvarchar(max) NOT NULL,
    IsBuiltIn   bit           NOT NULL CONSTRAINT DF_Role_Built DEFAULT(0),
    IsActive    bit           NOT NULL CONSTRAINT DF_Role_Act   DEFAULT(1),
    CONSTRAINT PK_Role PRIMARY KEY (Id),
    CONSTRAINT UQ_Role UNIQUE (Code)
);
GO

CREATE TABLE sec.RolePermission
(
    RoleId         int          NOT NULL,
    PermissionCode nvarchar(64) NOT NULL,
    CONSTRAINT PK_RolePermission PRIMARY KEY (RoleId, PermissionCode),
    CONSTRAINT FK_RolePerm_Role FOREIGN KEY (RoleId)         REFERENCES sec.Role (Id),
    CONSTRAINT FK_RolePerm_Perm FOREIGN KEY (PermissionCode) REFERENCES sec.Permission (Code)
);
GO

CREATE TABLE sec.RoleAssignment
(
    Id         int          IDENTITY(1,1) NOT NULL,
    UserId     int          NOT NULL,
    RoleId     int          NOT NULL,
    ScopeJson  nvarchar(max) NULL,     -- звуження за полями IsScopeField
    ValidFrom  date         NULL,
    ValidTo    date         NULL,
    CONSTRAINT PK_RoleAssignment PRIMARY KEY (Id),
    CONSTRAINT UQ_RoleAssignment UNIQUE (UserId, RoleId),
    CONSTRAINT FK_RoleAssign_User FOREIGN KEY (UserId) REFERENCES sec.[User] (Id),
    CONSTRAINT FK_RoleAssign_Role FOREIGN KEY (RoleId) REFERENCES sec.Role (Id)
);
GO

-- Успадкування Project → Sheet → Table → Column; IsDeny виграє ЗАВЖДИ (ФВ-6.6)
CREATE TABLE sec.ResourceGrant
(
    Id           int     IDENTITY(1,1) NOT NULL,
    RoleId       int     NOT NULL,
    ResourceKind tinyint NOT NULL,   -- ResourceKind
    ResourceId   int     NOT NULL,
    [Level]      tinyint NOT NULL,   -- GrantLevel
    IsDeny       bit     NOT NULL CONSTRAINT DF_Grant_Deny DEFAULT(0),
    CONSTRAINT PK_ResourceGrant PRIMARY KEY (Id),
    CONSTRAINT UQ_ResourceGrant UNIQUE (RoleId, ResourceKind, ResourceId),
    CONSTRAINT FK_Grant_Role FOREIGN KEY (RoleId) REFERENCES sec.Role (Id)
);
GO

CREATE TABLE sec.LoginAttempt
(
    Id         bigint        IDENTITY(1,1) NOT NULL,
    UserName   nvarchar(200) NOT NULL,
    Provider   tinyint       NOT NULL,
    IsSuccess  bit           NOT NULL,
    IpAddress  nvarchar(64)  NULL,
    AttemptedAt datetime2(3) NOT NULL,
    FailReason nvarchar(200) NULL,
    CONSTRAINT PK_LoginAttempt PRIMARY KEY (Id)
);
GO

CREATE INDEX IX_LoginAttempt_User ON sec.LoginAttempt (UserName, AttemptedAt DESC) ON [INDEXES];
GO
```

---

<a id="aud"></a>
## 12. `aud` — аудит

> **Append-only.** Обліковий запис застосунку має тут лише `INSERT` і `SELECT`;
> `UPDATE`/`DELETE` відкликані явно — це реалізує «незмінний журнал» на рівні
> БД, а не «домовленості не робити `UPDATE`» (B01 §6.4).

```sql
CREATE TABLE aud.CellChange
(
    Id              bigint         IDENTITY(1,1) NOT NULL,
    ChangedAt       datetime2(3)   NOT NULL,   -- партиційний ключ (місяць зміни ≠ звітний період)
    PeriodKey       int            NOT NULL,
    DocumentId      bigint         NOT NULL,
    TableRowId      bigint         NOT NULL,
    RowKey          nvarchar(100)  NOT NULL,
    ColumnDefId     int            NOT NULL,
    OldValue        nvarchar(1000) NULL,
    NewValue        nvarchar(1000) NULL,
    ChangedByUserId int            NOT NULL,   -- НЕ SID (R-A2)
    Origin          nvarchar(32)   NOT NULL,   -- UserEdit | Import | Recalculation | Migration
    IsLateEdit      bit            NOT NULL CONSTRAINT DF_CellChange_Late DEFAULT(0),
    CorrelationId   nvarchar(64)   NULL,
    CONSTRAINT PK_CellChange PRIMARY KEY CLUSTERED (ChangedAt, Id) ON ps_AuditByMonth(ChangedAt)
) ON ps_AuditByMonth(ChangedAt);
GO

CREATE INDEX IX_CellChange_Cell
    ON aud.CellChange (DocumentId, TableRowId, ColumnDefId, ChangedAt DESC)
    ON ps_AuditByMonth(ChangedAt);
GO

CREATE TABLE aud.StructureChange
(
    Id                bigint         IDENTITY(1,1) NOT NULL,
    ChangedAt         datetime2(3)   NOT NULL,
    TemplateVersionId int            NOT NULL,
    EntityType        nvarchar(64)   NOT NULL,
    EntityId          int            NOT NULL,
    ChangeClass       tinyint        NOT NULL,   -- ChangeClass
    Operation         nvarchar(32)   NOT NULL,
    OldJson           nvarchar(max)  NULL,
    NewJson           nvarchar(max)  NULL,
    ChangeReason      nvarchar(1000) NULL,
    ChangedByUserId   int            NOT NULL,
    CorrelationId     nvarchar(64)   NULL,
    CONSTRAINT PK_StructureChange PRIMARY KEY CLUSTERED (ChangedAt, Id) ON ps_AuditByMonth(ChangedAt)
) ON ps_AuditByMonth(ChangedAt);
GO

CREATE TABLE aud.SecurityEvent
(
    Id              bigint        IDENTITY(1,1) NOT NULL,
    ChangedAt       datetime2(3)  NOT NULL,
    EventType       nvarchar(64)  NOT NULL,
    TargetUserId    int           NULL,
    TargetRoleId    int           NULL,
    DetailsJson     nvarchar(max) NULL,
    ChangedByUserId int           NOT NULL,
    CorrelationId   nvarchar(64)  NULL,
    CONSTRAINT PK_SecurityEvent PRIMARY KEY CLUSTERED (ChangedAt, Id) ON ps_AuditByMonth(ChangedAt)
) ON ps_AuditByMonth(ChangedAt);
GO

CREATE TABLE aud.PublicationEvent
(
    Id              bigint         IDENTITY(1,1) NOT NULL,
    ChangedAt       datetime2(3)   NOT NULL,
    EntityType      nvarchar(64)   NOT NULL,   -- TemplateVersion | MethodologyVersion
    EntityId        int            NOT NULL,
    -- Diff РЕЗУЛЬТАТІВ на золотому наборі, не diff коду (ФВ-9.6)
    ResultDiffJson  nvarchar(max)  NULL,
    ChangeReason    nvarchar(1000) NOT NULL,
    ChangedByUserId int            NOT NULL,
    CONSTRAINT PK_PublicationEvent PRIMARY KEY CLUSTERED (ChangedAt, Id) ON ps_AuditByMonth(ChangedAt)
) ON ps_AuditByMonth(ChangedAt);
GO

CREATE TABLE aud.ConsistencyIssue
(
    Id           bigint        IDENTITY(1,1) NOT NULL,
    DetectedAt   datetime2(3)  NOT NULL,
    Severity     tinyint       NOT NULL,
    RuleCode     nvarchar(64)  NOT NULL,
    EntityType   nvarchar(64)  NULL,
    EntityId     bigint        NULL,
    Message      nvarchar(2000) NOT NULL,
    DetailsJson  nvarchar(max) NULL,
    ResolvedAt   datetime2(3)  NULL,
    ResolvedByUserId int       NULL,
    CONSTRAINT PK_ConsistencyIssue PRIMARY KEY (Id)
);
GO
```

---

<a id="itg"></a>
## 13. `itg` — журнали інтеграції

```sql
CREATE TABLE itg.CollectionRun
(
    Id              bigint        IDENTITY(1,1) NOT NULL,
    SourceEntityId  int           NOT NULL,
    RangeFrom       datetime2(3)  NOT NULL,
    RangeTo         datetime2(3)  NOT NULL,
    StartedAt       datetime2(3)  NOT NULL,
    FinishedAt      datetime2(3)  NULL,
    PointsRetrieved int           NOT NULL CONSTRAINT DF_CRun_Points DEFAULT(0),
    Status          nvarchar(32)  NOT NULL,
    IsCatchUp       bit           NOT NULL CONSTRAINT DF_CRun_Catch DEFAULT(0),
    ErrorMessage    nvarchar(2000) NULL,
    TriggeredByUserId int         NULL,        -- NULL = за розкладом
    CONSTRAINT PK_CollectionRun PRIMARY KEY (Id),
    CONSTRAINT FK_CRun_Entity FOREIGN KEY (SourceEntityId) REFERENCES ext.SourceEntity (Id)
);
GO

-- Журнал покриття: ознака здоров'я — саме він, а не тиша (ІНТ-3.3)
CREATE TABLE itg.CollectionCoverage
(
    Id             bigint       IDENTITY(1,1) NOT NULL,
    SourceEntityId int          NOT NULL,
    CoveredFrom    datetime2(3) NOT NULL,
    CoveredTo      datetime2(3) NOT NULL,
    CollectionRunId bigint      NOT NULL,
    CONSTRAINT PK_CollectionCoverage PRIMARY KEY (Id),
    CONSTRAINT FK_CCov_Entity FOREIGN KEY (SourceEntityId)  REFERENCES ext.SourceEntity (Id),
    CONSTRAINT FK_CCov_Run    FOREIGN KEY (CollectionRunId) REFERENCES itg.CollectionRun (Id)
);
GO

CREATE TABLE itg.ArchiveRun
(
    Id              bigint        IDENTITY(1,1) NOT NULL,
    ProjectId       int           NOT NULL,
    Direction       nvarchar(16)  NOT NULL,   -- ToArchive | FromArchive
    FromPeriodKey   int           NOT NULL,
    ToPeriodKey     int           NOT NULL,
    LastDonePeriodKey int         NULL,       -- для відновлення після збою (АРХ-3a)
    StartedAt       datetime2(3)  NOT NULL,
    FinishedAt      datetime2(3)  NULL,
    RowsMoved       bigint        NOT NULL CONSTRAINT DF_ARun_Rows DEFAULT(0),
    ChecksumSourceJson nvarchar(max) NULL,    -- три суми: COUNT, CHECKSUM_AGG, SUM
    ChecksumTargetJson nvarchar(max) NULL,
    Status          nvarchar(32)  NOT NULL,
    ErrorMessage    nvarchar(2000) NULL,
    TriggeredByUserId int         NULL,
    CONSTRAINT PK_ArchiveRun PRIMARY KEY (Id),
    CONSTRAINT FK_ARun_Project FOREIGN KEY (ProjectId) REFERENCES doc.Project (Id)
);
GO

CREATE TABLE itg.MaintenanceRun
(
    Id           bigint       IDENTITY(1,1) NOT NULL,
    JobCode      nvarchar(64) NOT NULL,
    StartedAt    datetime2(3) NOT NULL,
    FinishedAt   datetime2(3) NULL,
    Status       nvarchar(32) NOT NULL,
    DetailsJson  nvarchar(max) NULL,
    CONSTRAINT PK_MaintenanceRun PRIMARY KEY (Id)
);
GO

CREATE TABLE itg.JobProgress
(
    JobId      nvarchar(100) NOT NULL,
    JobCode    nvarchar(64)  NOT NULL,
    Percent    int           NOT NULL CONSTRAINT DF_JobP_Pct DEFAULT(0),
    Message    nvarchar(400) NULL,
    State      nvarchar(32)  NOT NULL,
    StartedAt  datetime2(3)  NOT NULL,
    UpdatedAt  datetime2(3)  NOT NULL,
    [Error]    nvarchar(2000) NULL,
    CONSTRAINT PK_JobProgress PRIMARY KEY (JobId)
);
GO
```

---

<a id="arc"></a>
## 14. `arc` — архів

> Дзеркало структури; відмінності **лише фізичні**: clustered columnstore,
> окрема файлова група, без `rowversion` і тригерів. Запис — тільки процедурою
> архівації під окремим principal.
>
> ⛔ `SWITCH PARTITION` тут **не працює і це навмисно**: він вимагає однакової
> файлової групи та ідентичних індексів, а різниця у стисненні й розміщенні і
> є сенсом архіву (АРХ-3).

```sql
CREATE TABLE arc.TableInstance
(
    PeriodKey  int          NOT NULL,
    Id         bigint       NOT NULL,
    DocumentId bigint       NOT NULL,
    TableDefId int          NOT NULL,
    CreatedAt  datetime2(3) NOT NULL,
    ModifiedAt datetime2(3) NOT NULL,
    INDEX CCI_arc_TableInstance CLUSTERED COLUMNSTORE
) ON [DATA_ARCHIVE];
GO

CREATE TABLE arc.TableRow
(
    PeriodKey       int           NOT NULL,
    Id              bigint        NOT NULL,
    TableInstanceId bigint        NOT NULL,
    RowKey          nvarchar(100) NOT NULL,
    RowDefId        int           NULL,
    Ordinal         int           NOT NULL,
    IsDeleted       bit           NOT NULL,
    ModifiedAt      datetime2(3)  NOT NULL,
    INDEX CCI_arc_TableRow CLUSTERED COLUMNSTORE
) ON [DATA_ARCHIVE];
GO

CREATE TABLE arc.CellValue
(
    PeriodKey            int            NOT NULL,
    TableRowId           bigint         NOT NULL,
    ColumnDefId          int            NOT NULL,
    TableDefId           int            NOT NULL,
    ValueString          nvarchar(1000) NULL,
    ValueNumeric         decimal(28,10) NULL,
    ValueDate            datetime2(3)   NULL,
    ValueBool            bit            NULL,
    ValueRegistryEntryId int            NULL,
    ValueUnitId          int            NULL,
    IsCalculated         bit            NOT NULL,
    IsEmpty              bit            NOT NULL,
    INDEX CCI_arc_CellValue CLUSTERED COLUMNSTORE
) ON [DATA_ARCHIVE];
GO

CREATE TABLE arc.CellChange
(
    Id              bigint         NOT NULL,
    ChangedAt       datetime2(3)   NOT NULL,
    PeriodKey       int            NOT NULL,
    DocumentId      bigint         NOT NULL,
    TableRowId      bigint         NOT NULL,
    RowKey          nvarchar(100)  NOT NULL,
    ColumnDefId     int            NOT NULL,
    OldValue        nvarchar(1000) NULL,
    NewValue        nvarchar(1000) NULL,
    ChangedByUserId int            NOT NULL,
    Origin          nvarchar(32)   NOT NULL,
    IsLateEdit      bit            NOT NULL,
    CorrelationId   nvarchar(64)   NULL,
    INDEX CCI_arc_CellChange CLUSTERED COLUMNSTORE
) ON [DATA_ARCHIVE];
GO

CREATE TABLE arc.CalculationResult
(
    PeriodKey            int            NOT NULL,
    Id                   bigint         NOT NULL,
    CalculationRunId     bigint         NOT NULL,
    MethodologyVersionId int            NOT NULL,
    DocumentId           bigint         NOT NULL,
    SourceRowKey         nvarchar(100)  NULL,
    SubstanceEntryId     int            NULL,
    OutputCode           nvarchar(64)   NOT NULL,
    Value                decimal(28,10) NOT NULL,
    UnitId               int            NOT NULL,
    INDEX CCI_arc_CalculationResult CLUSTERED COLUMNSTORE
) ON [DATA_ARCHIVE];
GO

CREATE TABLE arc.CalculationStep
(
    PeriodKey        int            NOT NULL,
    Id               bigint         NOT NULL,
    CalculationRunId bigint         NOT NULL,
    ResultId         bigint         NULL,
    StepOrder        int            NOT NULL,
    StepCode         nvarchar(64)   NOT NULL,
    Expression       nvarchar(2000) NULL,
    Value            decimal(28,10) NULL,
    TraceJson        nvarchar(max)  NULL,
    INDEX CCI_arc_CalculationStep CLUSTERED COLUMNSTORE
) ON [DATA_ARCHIVE];
GO
```

---

<a id="triggers"></a>
## 15. Тригери незмінності

> **`AFTER`-тригер із `THROW`, а не `INSTEAD OF`** (П-4): `INSTEAD OF` ламає
> `OUTPUT`-клаузу, якою EF Core користується при `SaveChanges`.
> Перелічуються **структурні** поля, а не презентаційні: список презентаційних
> росте, і про нього забувають.
>
> ⚠ Кожна таблиця з тригером **зобов'язана** мати
> `.ToTable(t => t.HasTrigger("..."))` у конфігурації EF — інакше `SaveChanges`
> падає в рантаймі (ТЗ §13.5 п.1).

```sql
CREATE OR ALTER TRIGGER cfg.TR_ColumnDef_Immutable
ON cfg.ColumnDef
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM inserted i
        JOIN deleted  d ON d.Id = i.Id
        JOIN cfg.TableDef t ON t.Id = i.TableDefId
        JOIN cfg.SheetDef s ON s.Id = t.SheetDefId
        JOIN cfg.TemplateVersion v ON v.Id = s.TemplateVersionId
        WHERE v.Status = 1   -- Published
          AND (ISNULL(i.Code,N'')       <> ISNULL(d.Code,N'')
            OR i.DataType               <> d.DataType
            OR ISNULL(i.Precision,-1)   <> ISNULL(d.Precision,-1)
            OR ISNULL(i.Scale,-1)       <> ISNULL(d.Scale,-1)
            OR i.IsRequired             <> d.IsRequired
            OR ISNULL(i.LookupRegistryDefId,-1) <> ISNULL(d.LookupRegistryDefId,-1)
            OR ISNULL(i.UnitId,-1)      <> ISNULL(d.UnitId,-1)
            OR i.IsBusinessKey          <> d.IsBusinessKey
            OR i.TableDefId             <> d.TableDefId)
    )
    BEGIN
        THROW 50001, N'Структурна зміна колонки в опублікованій версії заборонена. Використайте CloneFrom (ФВ-7.1).', 1;
    END
END;
GO

CREATE OR ALTER TRIGGER cfg.TR_RowDef_Immutable
ON cfg.RowDef
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM inserted i
        JOIN deleted  d ON d.Id = i.Id
        JOIN cfg.TableDef t ON t.Id = i.TableDefId
        JOIN cfg.SheetDef s ON s.Id = t.SheetDefId
        JOIN cfg.TemplateVersion v ON v.Id = s.TemplateVersionId
        WHERE v.Status = 1
          AND (ISNULL(i.RowKey,N'') <> ISNULL(d.RowKey,N'')
            OR i.RowKind            <> d.RowKind
            OR i.TableDefId         <> d.TableDefId
            OR ISNULL(i.ParentRowDefId,-1) <> ISNULL(d.ParentRowDefId,-1))
    )
    BEGIN
        THROW 50002, N'Структурна зміна рядка в опублікованій версії заборонена (ФВ-7.1).', 1;
    END
END;
GO

CREATE OR ALTER TRIGGER cfg.TR_FormulaDef_Immutable
ON cfg.FormulaDef
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM deleted d
        JOIN cfg.TableDef t ON t.Id = d.TableDefId
        JOIN cfg.SheetDef s ON s.Id = t.SheetDefId
        JOIN cfg.TemplateVersion v ON v.Id = s.TemplateVersionId
        WHERE v.Status = 1
    )
    BEGIN
        THROW 50003, N'Зміна або видалення формули в опублікованій версії заборонені (ФВ-7.1).', 1;
    END
END;
GO
```

---

<a id="archive-proc"></a>
## 16. Процедура архівації

```sql
-- src/Ecr.Infrastructure/Persistence/Sql/03-archive-proc.sql
-- Виконується SQL Agent під окремим principal: у застосунку немає ані DDL-прав,
-- ані права запису в arc.* (D-66, B01 §6.4).
CREATE OR ALTER PROCEDURE arc.usp_ArchiveYear
    @ProjectId     int,
    @FromPeriodKey int,
    @ToPeriodKey   int,
    @BatchSize     int = 500000
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @RunId bigint, @k int, @srcCount bigint, @srcChk bigint, @srcSum decimal(38,10);
    DECLARE @dstCount bigint, @dstChk bigint, @dstSum decimal(38,10);

    INSERT INTO itg.ArchiveRun (ProjectId, Direction, FromPeriodKey, ToPeriodKey, StartedAt, Status)
    VALUES (@ProjectId, N'ToArchive', @FromPeriodKey, @ToPeriodKey, SYSUTCDATETIME(), N'Running');
    SET @RunId = SCOPE_IDENTITY();

    UPDATE doc.Project SET IsArchiving = 1 WHERE Id = @ProjectId;

    -- Відновлення після збою: продовжуємо з партиції, на якій зупинилися (АРХ-3a)
    SELECT @k = ISNULL(MAX(LastDonePeriodKey) + 1, @FromPeriodKey)
    FROM itg.ArchiveRun
    WHERE ProjectId = @ProjectId AND Direction = N'ToArchive' AND Status = N'Failed';

    IF @k IS NULL OR @k < @FromPeriodKey SET @k = @FromPeriodKey;

    WHILE @k <= @ToPeriodKey
    BEGIN
        -- 1. Три контрольні суми на джерелі
        SELECT @srcCount = COUNT_BIG(*),
               @srcChk   = CHECKSUM_AGG(BINARY_CHECKSUM(*)),
               @srcSum   = ISNULL(SUM(ValueNumeric), 0)
        FROM doc.CellValue WHERE PeriodKey = @k;

        -- 2. Копіювання. TABLOCK → мінімальне логування і прямий запис
        --    у columnstore rowgroups.
        INSERT INTO arc.CellValue WITH (TABLOCK)
            (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString, ValueNumeric,
             ValueDate, ValueBool, ValueRegistryEntryId, ValueUnitId, IsCalculated, IsEmpty)
        SELECT PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString, ValueNumeric,
               ValueDate, ValueBool, ValueRegistryEntryId, ValueUnitId, IsCalculated, IsEmpty
        FROM doc.CellValue WHERE PeriodKey = @k;

        INSERT INTO arc.TableRow WITH (TABLOCK)
            (PeriodKey, Id, TableInstanceId, RowKey, RowDefId, Ordinal, IsDeleted, ModifiedAt)
        SELECT PeriodKey, Id, TableInstanceId, RowKey, RowDefId, Ordinal, IsDeleted, ModifiedAt
        FROM doc.TableRow WHERE PeriodKey = @k;

        INSERT INTO arc.TableInstance WITH (TABLOCK)
            (PeriodKey, Id, DocumentId, TableDefId, CreatedAt, ModifiedAt)
        SELECT PeriodKey, Id, DocumentId, TableDefId, CreatedAt, ModifiedAt
        FROM doc.TableInstance WHERE PeriodKey = @k;

        -- 3. Ті самі суми на приймачі
        SELECT @dstCount = COUNT_BIG(*),
               @dstChk   = CHECKSUM_AGG(BINARY_CHECKSUM(*)),
               @dstSum   = ISNULL(SUM(ValueNumeric), 0)
        FROM arc.CellValue WHERE PeriodKey = @k;

        -- 4. Розбіжність → СТОП. ДАНІ З ДЖЕРЕЛА НЕ ВИДАЛЯЮТЬСЯ —
        --    це головне правило процедури.
        IF (@srcCount <> @dstCount OR @srcSum <> @dstSum)
        BEGIN
            INSERT INTO aud.ConsistencyIssue (DetectedAt, Severity, RuleCode, EntityType, EntityId, Message)
            VALUES (SYSUTCDATETIME(), 2, N'ARCHIVE_CHECKSUM', N'Period', @k,
                    N'Розбіжність контрольних сум при архівації; дані джерела збережено.');

            UPDATE itg.ArchiveRun
               SET Status = N'Failed', FinishedAt = SYSUTCDATETIME(), LastDonePeriodKey = @k - 1,
                   ErrorMessage = N'Checksum mismatch'
             WHERE Id = @RunId;

            UPDATE doc.Project SET IsArchiving = 0 WHERE Id = @ProjectId;
            THROW 50010, N'Розбіжність контрольних сум при архівації.', 1;
        END

        -- 5. Збіг → звільнення партиції. TRUNCATE … WITH (PARTITIONS) звільняє
        --    майже миттєво і мінімально логується; DELETE на 108 млн роздув би журнал.
        DECLARE @part int = $PARTITION.pf_ByPeriodKey(@k);
        DECLARE @sql nvarchar(400);
        SET @sql = N'TRUNCATE TABLE doc.CellValue    WITH (PARTITIONS (' + CAST(@part AS nvarchar(10)) + N'));';
        EXEC sp_executesql @sql;
        SET @sql = N'TRUNCATE TABLE doc.TableRow     WITH (PARTITIONS (' + CAST(@part AS nvarchar(10)) + N'));';
        EXEC sp_executesql @sql;
        SET @sql = N'TRUNCATE TABLE doc.TableInstance WITH (PARTITIONS (' + CAST(@part AS nvarchar(10)) + N'));';
        EXEC sp_executesql @sql;

        UPDATE itg.ArchiveRun
           SET RowsMoved = RowsMoved + @srcCount, LastDonePeriodKey = @k
         WHERE Id = @RunId;

        SET @k = @k + 1;
    END

    UPDATE itg.ArchiveRun SET Status = N'Completed', FinishedAt = SYSUTCDATETIME() WHERE Id = @RunId;
    UPDATE doc.Project SET IsArchiving = 0, Status = 4 WHERE Id = @ProjectId;
END;
GO
```

```sql
-- src/Ecr.Infrastructure/Persistence/Sql/04-partition-maintenance.sql
-- Працює НА ВИПЕРЕДЖЕННЯ: SPLIT порожньої останньої партиції — операція
-- метаданих; SPLIT непорожньої переміщує дані з блокуванням.
CREATE OR ALTER PROCEDURE arc.usp_EnsurePartitions
    @MonthsAhead int = 6
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @d date = DATEADD(MONTH, 1, GETUTCDATE());
    DECLARE @i int = 0, @key int, @sql nvarchar(400);

    WHILE @i < @MonthsAhead
    BEGIN
        SET @key = YEAR(@d) * 100 + MONTH(@d);

        IF NOT EXISTS (SELECT 1 FROM sys.partition_range_values rv
                       JOIN sys.partition_functions pf ON pf.function_id = rv.function_id
                       WHERE pf.name = N'pf_ByPeriodKey' AND CAST(rv.value AS int) = @key)
        BEGIN
            ALTER PARTITION SCHEME ps_ByPeriodKey NEXT USED [DATA_HOT];
            SET @sql = N'ALTER PARTITION FUNCTION pf_ByPeriodKey() SPLIT RANGE (' + CAST(@key AS nvarchar(10)) + N');';
            EXEC sp_executesql @sql;
        END

        SET @d = DATEADD(MONTH, 1, @d);
        SET @i = @i + 1;
    END

    INSERT INTO itg.MaintenanceRun (JobCode, StartedAt, FinishedAt, Status)
    VALUES (N'EnsurePartitions', SYSUTCDATETIME(), SYSUTCDATETIME(), N'Completed');
END;
GO
```

---

<a id="seed"></a>
## 17. Seed

> Ідемпотентний: повторний запуск не створює дублікатів. Без нього застосунок
> не стартує.

```sql
-- Мови
MERGE sys_ecr.Language AS t
USING (VALUES (N'en', N'English', 1, 1), (N'ru', N'Русский', 2, 0), (N'kz', N'Қазақша', 3, 0))
      AS s (Code, NameNative, Ordinal, IsDefault)
ON t.Code = s.Code
WHEN NOT MATCHED THEN INSERT (Code, NameNative, Ordinal, IsDefault, IsActive)
     VALUES (s.Code, s.NameNative, s.Ordinal, s.IsDefault, 1);
GO

-- Функціональні права
MERGE sec.Permission AS t
USING (VALUES
  (N'Template.View',            N'Template',    0), (N'Template.Edit',        N'Template',    0),
  (N'Template.Publish',         N'Template',    0), (N'Template.Migrate',     N'Template',    0),
  (N'Registry.View',            N'Registry',    0), (N'Registry.EditData',    N'Registry',    0),
  (N'Registry.EditDefinition',  N'Registry',    0), (N'Registry.Publish',     N'Registry',    0),
  (N'Document.View',            N'Document',    0), (N'Document.Create',      N'Document',    0),
  (N'Document.Delete',          N'Document',    0), (N'Document.Import',      N'Document',    0),
  (N'Document.Export',          N'Document',    0), (N'Document.Reopen',      N'Document',    0),
  (N'Project.Manage',           N'Project',     0),
  (N'Period.Configure',         N'Period',      0), (N'Period.Reopen',        N'Period',      1),
  (N'Calculation.View',         N'Calculation', 0), (N'Calculation.EditFormula',  N'Calculation', 0),
  (N'Calculation.EditConstant', N'Calculation', 0), (N'Calculation.EditRule',     N'Calculation', 0),
  (N'Calculation.EditScript',   N'Calculation', 1), (N'Calculation.Publish',      N'Calculation', 1),
  (N'Calculation.Recalculate',  N'Calculation', 0),
  (N'Report.ViewRegulatory',    N'Report',      0), (N'Report.BuildSnapshot', N'Report',      0),
  (N'Report.MarkSubmitted',     N'Report',      0), (N'Report.Export',        N'Report',      0),
  (N'Integration.View',         N'Integration', 0), (N'Integration.Manage',   N'Integration', 1),
  (N'Integration.EditSchedule', N'Integration', 0),
  (N'Security.ManageUsers',     N'Security',    1), (N'Security.ManageRoles', N'Security',    1),
  (N'Security.ViewAudit',       N'Security',    0), (N'Security.Simulate',    N'Security',    1),
  (N'System.ViewHealth',        N'System',      0), (N'System.RunJob',        N'System',      1),
  (N'System.ManageLocalization', N'System',     0)
) AS s (Code, [Group], IsDangerous)
ON t.Code = s.Code
WHEN NOT MATCHED THEN INSERT (Code, [Group], NameL10n, IsDangerous)
     VALUES (s.Code, s.[Group], N'{"en":"' + s.Code + N'"}', s.IsDangerous);
GO

-- Розмірності
MERGE uom.Dimension AS t
USING (VALUES
  (1, N'Mass', 0, NULL, NULL),          (2, N'Volume', 0, NULL, NULL),
  (3, N'Energy', 0, NULL, NULL),        (4, N'Time', 0, NULL, NULL),
  (5, N'Temperature', 0, NULL, NULL),   (6, N'Amount', 0, NULL, NULL),
  (7, N'Dimensionless', 0, NULL, NULL),
  (8,  N'MassFlow',      1, 1, 4),      -- Mass / Time
  (9,  N'MassPerMass',   1, 1, 1),      -- Mass / Mass
  (10, N'MassPerEnergy', 1, 1, 3),      -- Mass / Energy
  (11, N'MassPerVolume', 1, 1, 2)       -- Mass / Volume
) AS s (Id, Code, IsDerived, Num, Den)
ON t.Id = s.Id
WHEN NOT MATCHED THEN INSERT (Id, Code, NameL10n, IsDerived, NumeratorDimensionId, DenominatorDimensionId)
     VALUES (s.Id, s.Code, N'{"en":"' + s.Code + N'"}', s.IsDerived, s.Num, s.Den);
GO

-- Базові одиниці. FactorToBase наявних одиниць МІНЯТИ ЗАБОРОНЕНО:
-- на них спираються фікстури і тести конверсій.
MERGE uom.Unit AS t
USING (VALUES
  (N'kg',   1, 1, 1.0,        0.0),     (N'm3',   2, 1, 1.0,      0.0),
  (N'J',    3, 1, 1.0,        0.0),     (N's',    4, 1, 1.0,      0.0),
  (N'K',    5, 1, 1.0,        0.0),     (N'mol',  6, 1, 1.0,      0.0),
  (N'one',  7, 1, 1.0,        0.0),
  (N't',    1, 0, 1000.0,     0.0),     (N'g',    1, 0, 0.001,    0.0),
  (N'mg',   1, 0, 0.000001,   0.0),
  (N'l',    2, 0, 0.001,      0.0),
  (N'GJ',   3, 0, 1000000000.0, 0.0),   (N'MWh',  3, 0, 3600000000.0, 0.0),
  (N'min',  4, 0, 60.0,       0.0),     (N'h',    4, 0, 3600.0,   0.0),
  (N'day',  4, 0, 86400.0,    0.0),     (N'year', 4, 0, 31536000.0, 0.0),
  (N'degC', 5, 0, 1.0,        273.15)
) AS s (Code, DimensionId, IsBase, Factor, [Offset])
ON t.Code = s.Code
WHEN NOT MATCHED THEN INSERT (Code, SymbolL10n, NameL10n, DimensionId, IsBase, FactorToBase, OffsetToBase)
     VALUES (s.Code, N'{"en":"' + s.Code + N'"}', N'{"en":"' + s.Code + N'"}',
             s.DimensionId, s.IsBase, s.Factor, s.[Offset]);
GO

-- Похідні одиниці: складаються ПОСИЛАННЯМИ на чисельник і знаменник,
-- а не розбираються з рядка (ФВ-16.2)
MERGE uom.Unit AS t
USING (VALUES
  (N'g_per_s',    8,  N'g',  N's',    0.001),
  (N't_per_year', 8,  N't',  N'year', 0.0000317097919837646),   -- 1000 / 31536000
  (N'kg_per_t',   9,  N'kg', N't',    0.001),
  (N'g_per_GJ',   10, N'g',  N'GJ',   0.000000000001),
  (N'mg_per_m3',  11, N'mg', N'm3',   0.000001),
  (N'kg_per_m3',  11, N'kg', N'm3',   1.0)
) AS s (Code, DimensionId, NumCode, DenCode, Factor)
ON t.Code = s.Code
WHEN NOT MATCHED THEN INSERT
     (Code, SymbolL10n, NameL10n, DimensionId, IsBase, FactorToBase, OffsetToBase,
      NumeratorUnitId, DenominatorUnitId)
     VALUES (s.Code, N'{"en":"' + s.Code + N'"}', N'{"en":"' + s.Code + N'"}',
             s.DimensionId, 0, s.Factor, 0,
             (SELECT Id FROM uom.Unit WHERE Code = s.NumCode),
             (SELECT Id FROM uom.Unit WHERE Code = s.DenCode));
GO

UPDATE d SET BaseUnitId = u.Id
FROM uom.Dimension d
JOIN uom.Unit u ON u.DimensionId = d.Id AND u.IsBase = 1
WHERE d.BaseUnitId IS NULL;
GO

-- Політика паролів і вбудовані ролі
MERGE sec.PasswordPolicy AS t USING (VALUES (N'Default')) AS s (Code) ON t.Code = s.Code
WHEN NOT MATCHED THEN INSERT (Code) VALUES (s.Code);
GO

MERGE sec.Role AS t
USING (VALUES (N'SystemAdministrator'), (N'TemplateAdministrator'), (N'PeriodAdministrator'),
              (N'DataEntry'), (N'Approver'), (N'Viewer'), (N'Auditor')) AS s (Code)
ON t.Code = s.Code
WHEN NOT MATCHED THEN INSERT (Code, NameL10n, IsBuiltIn)
     VALUES (s.Code, N'{"en":"' + s.Code + N'"}', 1);
GO

-- Політика періодів ECR
MERGE doc.PeriodPolicy AS t USING (VALUES (N'ECR-Standard', 0, 15, 45, 45))
      AS s (Code, O, G, H, Y) ON t.Code = s.Code
WHEN NOT MATCHED THEN INSERT (Code, OpenOffsetDays, GraceOffsetDays, HardCloseOffsetDays, YearGraceOffsetDays)
     VALUES (s.Code, s.O, s.G, s.H, s.Y);
GO

-- ── Каталог рядків інтерфейсу ────────────────────────────────────────────
-- ОБОВ'ЯЗКОВО: UiStringRevision має CHECK (Id = 1), тобто рівно один рядок.
-- Без нього запит версії поверне порожньо і ETag не сформується.
MERGE sys_ecr.UiStringRevision AS t USING (VALUES (1, 1)) AS s (Id, Rev)
      ON t.Id = s.Id
WHEN NOT MATCHED THEN INSERT (Id, Revision, ModifiedAt)
     VALUES (s.Id, s.Rev, SYSUTCDATETIME());
GO

-- Мінімальний ПУБЛІЧНИЙ набір (Scope = 0) мовою за замовчуванням.
-- Без нього перший запуск покаже сирі ключі на сторінці входу — першому,
-- що бачить будь-хто. Решта ключів додається разом із областями UI.
MERGE sys_ecr.UiString AS t
USING (VALUES
    (N'auth.title',            N'en', N'Environmental Compliance Reporting', 0),
    (N'auth.windows',          N'en', N'Sign in with Windows',              0),
    (N'auth.local',            N'en', N'Sign in with account',              0),
    (N'auth.userName',         N'en', N'User name',                         0),
    (N'auth.password',         N'en', N'Password',                          0),
    (N'auth.submit',           N'en', N'Sign in',                           0),
    (N'auth.mustChange',       N'en', N'Change your password to continue',  0),
    (N'common.save',           N'en', N'Save',                              0),
    (N'common.cancel',         N'en', N'Cancel',                            0),
    (N'common.retry',          N'en', N'Retry',                             0),
    (N'common.loading',        N'en', N'Loading…',                          0),
    (N'err.ECR-AUTH-0401',     N'en', N'Sign in to continue.',              0),
    (N'err.ECR-AUTH-0403',     N'en', N'You do not have permission for this action.', 0),
    (N'err.ECR-AUTH-0423',     N'en', N'The account is locked.',            0),
    (N'err.ECR-PWD-0428',      N'en', N'Password change is required.',      0)
) AS s ([Key], Lang, Val, Scope)
   ON t.[Key] = s.[Key] AND t.LanguageCode = s.Lang
WHEN NOT MATCHED THEN INSERT ([Key], LanguageCode, Value, Scope, ModifiedAt)
     VALUES (s.[Key], s.Lang, s.Val, s.Scope, SYSUTCDATETIME());
GO
```

> **Чому seed саме такий.** Тут лише те, без чого **перший екран непридатний**:
> форма входу і чотири помилки, які на ній можливі. Решта каталогу
> наповнюється разом із областями UI — інакше seed перетворився б на другу
> копію всіх підписів системи і розходився б із нею.
>
> `RU` і `KZ` у seed **немає** навмисно: `ФВ-14.9` гарантує підміну мовою за
> замовчуванням, і цей механізм має бути перевірений із першого дня, а не
> вперше спрацювати через рік.

> **Небезпечні права не потрапляють у вбудовані ролі автоматично.**
> `Calculation.EditScript`, `Calculation.Publish`, `Security.*`,
> `Integration.Manage`, `System.RunJob` видаються **іменованим** особам окремо
> (ФВ-6.12, D-40). Seed створює ролі порожніми за небезпечними правами
> навмисно — це не пропуск.
