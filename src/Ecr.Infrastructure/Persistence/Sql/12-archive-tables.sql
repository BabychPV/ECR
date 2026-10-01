-- ⚠ SET-опції задаються ЯВНО і першими.
-- `sqlcmd` за замовчуванням має `QUOTED_IDENTIFIER OFF`, а `SqlClient` — `ON`.
-- Через це скрипт, який проходить у тестах (їх виконує SqlClient), падає в
-- розгортанні (його виконує DBA через sqlcmd, `09-commands.md` §3) на будь-якій
-- таблиці з фільтрованим індексом або індексованою в'юхою. Опція ще й
-- ЗАПАМ'ЯТОВУЄТЬСЯ в момент створення процедури — тому її треба поставити до
-- першого `CREATE`, а не «якось у сесії».
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- src/Ecr.Infrastructure/Persistence/Sql/12-archive-tables.sql
-- Таблиці схеми `arc` — дзеркало структури `doc` і `calc`.
--
-- ⚠ Створюються СКРИПТОМ, а не міграцією EF, і це не обхідний шлях: різниця
-- між архівом і джерелом ФІЗИЧНА — clustered columnstore і окрема файлова
-- група. Ні того, ні того модель EF не виражає, а покласти архів поруч із
-- джерелом означало б не мати архіву взагалі: сенс саме в іншому стисненні
-- й розміщенні (АРХ-3).
--
-- ⛔ Саме тому `SWITCH PARTITION` сюди не працює: він вимагає однакової
-- файлової групи та ідентичних індексів. Перенесення йде вставкою і
-- видаленням ПІСЛЯ звірки контрольних сум (АРХ-3a).
--
-- Той самий шлях, що для `aud.*` (11) і `sys_ecr.*` (08): таблиця поза
-- моделлю EF створюється скриптом і перевіряється тестом фізичної моделі.
--
-- ⛔ Скрипт СТВОРЮЄ, але не переводить: `IF OBJECT_ID(...) IS NULL` означає, що
-- на вже розгорнутій базі зміна типу тут не застосується сама. Для `D-148`
-- (масштаб 10 → 16) це свідомо лишено так: `ALTER COLUMN` на columnstore з
-- мільярдами рядків — операція обслуговування, а не побічний ефект
-- розгортання. Розбіжність не тиха: `arc.usp_ArchiveYear` звіряє
-- `SUM(ValueNumeric)` джерела й архіву і ЗУПИНЯЄТЬСЯ з «розбіжність
-- контрольних сум» (`03-archive-proc.sql`, тест `ArchiveJobTests`). Переведення
-- наявного архіву — окремий крок у вікні обслуговування.

IF SCHEMA_ID(N'arc') IS NULL EXEC(N'CREATE SCHEMA arc');
GO

IF OBJECT_ID(N'arc.TableInstance', N'U') IS NULL
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

IF OBJECT_ID(N'arc.TableRow', N'U') IS NULL
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

IF OBJECT_ID(N'arc.CellValue', N'U') IS NULL
CREATE TABLE arc.CellValue
(
    PeriodKey            int            NOT NULL,
    TableRowId           bigint         NOT NULL,
    ColumnDefId          int            NOT NULL,
    TableDefId           int            NOT NULL,
    ValueString          nvarchar(1000) NULL,
    ValueNumeric         decimal(34,16) NULL,
    ValueDate            datetime2(3)   NULL,
    ValueBool            bit            NULL,
    ValueRegistryEntryId int            NULL,
    ValueUnitId          int            NULL,
    IsCalculated         bit            NOT NULL,
    IsEmpty              bit            NOT NULL,
    INDEX CCI_arc_CellValue CLUSTERED COLUMNSTORE
) ON [DATA_ARCHIVE];
GO

IF OBJECT_ID(N'arc.CellChange', N'U') IS NULL
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
    IsOutOfWindow   bit            NOT NULL CONSTRAINT DF_arc_CellChange_OutOfWindow DEFAULT (0),
    INDEX CCI_arc_CellChange CLUSTERED COLUMNSTORE
) ON [DATA_ARCHIVE];
GO

-- ФВ-2.16 / D-239: дзеркало aud.CellChange.IsOutOfWindow для наявних баз.
IF COL_LENGTH(N'arc.CellChange', N'IsOutOfWindow') IS NULL
    ALTER TABLE arc.CellChange
        ADD IsOutOfWindow bit NOT NULL CONSTRAINT DF_arc_CellChange_OutOfWindow DEFAULT (0);
GO

IF OBJECT_ID(N'arc.CalculationResult', N'U') IS NULL
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
    Value                decimal(34,16) NOT NULL,   -- дзеркало calc.CalculationResult (D-148)
    UnitId               int            NOT NULL,
    INDEX CCI_arc_CalculationResult CLUSTERED COLUMNSTORE
) ON [DATA_ARCHIVE];
GO

IF OBJECT_ID(N'arc.CalculationStep', N'U') IS NULL
CREATE TABLE arc.CalculationStep
(
    PeriodKey        int            NOT NULL,
    Id               bigint         NOT NULL,
    CalculationRunId bigint         NOT NULL,
    ResultId         bigint         NULL,
    StepOrder        int            NOT NULL,
    StepCode         nvarchar(64)   NOT NULL,
    Expression       nvarchar(2000) NULL,
    Value            decimal(34,16) NULL,       -- дзеркало calc.CalculationStep (D-148)
    TraceJson        nvarchar(max)  NULL,
    INDEX CCI_arc_CalculationStep CLUSTERED COLUMNSTORE
) ON [DATA_ARCHIVE];
GO

-- ⛔ Колонки, що з'явилися в `calc.*` ПІСЛЯ першого розгортання, додаються ЛИШЕ
-- тут, а не в `CREATE TABLE` вище: той на розгорнутій базі не виконується
-- (`IF OBJECT_ID … IS NULL`), і колонка в ньому дійшла б лише до свіжих баз.
-- Додавання колонки — не `ALTER COLUMN` із шапки: на columnstore це зміна
-- метаданих, а не переписування мільярдів рядків.
--
-- ⚠ Тип і NULL-придатність — рівно як у джерелі: сторож
-- `ArchiveMirrorTests` звіряє кожну колонку `calc.CalculationResult` і
-- `calc.CalculationStep` з її дзеркалом на розгорнутій базі.
--
-- HSE301 F6 (`D-175`): вид результату — вихід чи проміжне значення.
IF COL_LENGTH(N'arc.CalculationResult', N'Kind') IS NULL
    ALTER TABLE arc.CalculationResult
        ADD Kind tinyint NOT NULL CONSTRAINT DF_arc_CRes_Kind DEFAULT (0);
GO

-- `H-24d-1`: причина маскування в нуль. Колонку `calc.CalculationStep` додали
-- без дзеркала — знайдено сторожем `ArchiveMirrorTests` у кроці HSE301 F6.
IF COL_LENGTH(N'arc.CalculationStep', N'MaskedZero') IS NULL
    ALTER TABLE arc.CalculationStep
        ADD MaskedZero tinyint NOT NULL CONSTRAINT DF_arc_CStep_Masked DEFAULT (0);
GO

-- HSE301 F6: адреса кроку трейсу — документ, рядок, речовина (§7.1).
IF COL_LENGTH(N'arc.CalculationStep', N'DocumentId') IS NULL
    ALTER TABLE arc.CalculationStep ADD DocumentId bigint NULL;
GO

IF COL_LENGTH(N'arc.CalculationStep', N'SourceRowKey') IS NULL
    ALTER TABLE arc.CalculationStep ADD SourceRowKey nvarchar(100) NULL;
GO

IF COL_LENGTH(N'arc.CalculationStep', N'SubstanceEntryId') IS NULL
    ALTER TABLE arc.CalculationStep ADD SubstanceEntryId int NULL;
GO

-- D4 аудиту: позначка осиротілого рядка (`doc.TableRow.IsOrphaned`). Без неї
-- архівування+відновлення скидало прапорець у 0, і осиротілі рядки переставали
-- блокувати подання (ECR-SUB-4221). Копіюють `usp_ArchiveYear`/`usp_RestoreYear`.
IF COL_LENGTH(N'arc.TableRow', N'IsOrphaned') IS NULL
    ALTER TABLE arc.TableRow
        ADD IsOrphaned bit NOT NULL CONSTRAINT DF_arc_TableRow_Orph DEFAULT (0);
GO

-- D-247 / НФ-8.4b: архів аудиту — SWITCH партицій aud.* у дзеркала arc.Audit*.
--
-- ⛔ Чому НЕ існуючий arc.CellChange: він clustered columnstore на [DATA_ARCHIVE],
-- а SWITCH вимагає ідентичної структури (rowstore PK, ті самі індекси, та сама
-- схема партиціонування й файлові групи). Тому цілі SWITCH — окремі таблиці
-- `arc.Audit*`: колонка в колонку, індекс в індекс, на ТІЙ САМІЙ ps_AuditByMonth(ChangedAt).
-- Партиція n джерела перемикається в партицію n цілі (метаданкова операція,
-- без копіювання рядків). Переміщення на дешевший диск — справа DBA (файлова група
-- [AUDIT] партицій, що старіють, див. docs/build/audit-archive-runbook.md).
--
-- ⚠ Колонки й індекси мають ЗБІГАТИСЯ з `11-audit-tables.sql`: SWITCH падає на
-- будь-якій розбіжності (Msg 4943/4904/4912/4913). Додаєш колонку чи індекс до
-- `aud.*` — додай сюди (сторож AuditArchiveSwitchTests).
IF OBJECT_ID(N'arc.AuditCellChange', N'U') IS NULL
BEGIN
    CREATE TABLE arc.AuditCellChange
    (
        Id              bigint         IDENTITY(1,1) NOT NULL,
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
        IsLateEdit      bit            NOT NULL CONSTRAINT DF_arcAudCellChange_Late DEFAULT(0),
        CorrelationId   nvarchar(64)   NULL,
        IsOutOfWindow   bit            NOT NULL CONSTRAINT DF_arcAudCellChange_OutOfWindow DEFAULT(0),
        CONSTRAINT PK_arcAuditCellChange PRIMARY KEY CLUSTERED (ChangedAt, Id) ON ps_AuditByMonth(ChangedAt)
    ) ON ps_AuditByMonth(ChangedAt);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CellChange_Cell'
               AND object_id = OBJECT_ID(N'arc.AuditCellChange'))
    CREATE INDEX IX_CellChange_Cell
        ON arc.AuditCellChange (DocumentId, TableRowId, ColumnDefId, ChangedAt DESC)
        ON ps_AuditByMonth(ChangedAt);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CellChange_OutOfWindow'
               AND object_id = OBJECT_ID(N'arc.AuditCellChange'))
    CREATE INDEX IX_CellChange_OutOfWindow
        ON arc.AuditCellChange (DocumentId, PeriodKey, TableRowId, ColumnDefId)
        WHERE IsOutOfWindow = 1
        ON ps_AuditByMonth(ChangedAt);
GO

IF OBJECT_ID(N'arc.AuditStructureChange', N'U') IS NULL
BEGIN
    CREATE TABLE arc.AuditStructureChange
    (
        Id                bigint         IDENTITY(1,1) NOT NULL,
        ChangedAt         datetime2(3)   NOT NULL,
        TemplateVersionId int            NOT NULL,
        EntityType        nvarchar(64)   NOT NULL,
        EntityId          int            NOT NULL,
        ChangeClass       tinyint        NOT NULL,
        Operation         nvarchar(32)   NOT NULL,
        OldJson           nvarchar(max)  NULL,
        NewJson           nvarchar(max)  NULL,
        ChangeReason      nvarchar(1000) NULL,
        ChangedByUserId   int            NOT NULL,
        CorrelationId     nvarchar(64)   NULL,
        CONSTRAINT PK_arcAuditStructureChange PRIMARY KEY CLUSTERED (ChangedAt, Id) ON ps_AuditByMonth(ChangedAt)
    ) ON ps_AuditByMonth(ChangedAt);
END
GO

IF OBJECT_ID(N'arc.AuditSecurityEvent', N'U') IS NULL
BEGIN
    CREATE TABLE arc.AuditSecurityEvent
    (
        Id              bigint        IDENTITY(1,1) NOT NULL,
        ChangedAt       datetime2(3)  NOT NULL,
        EventType       nvarchar(64)  NOT NULL,
        TargetUserId    int           NULL,
        TargetRoleId    int           NULL,
        DetailsJson     nvarchar(max) NULL,
        ChangedByUserId int           NOT NULL,
        CorrelationId   nvarchar(64)  NULL,
        CONSTRAINT PK_arcAuditSecurityEvent PRIMARY KEY CLUSTERED (ChangedAt, Id) ON ps_AuditByMonth(ChangedAt)
    ) ON ps_AuditByMonth(ChangedAt);
END
GO

IF OBJECT_ID(N'arc.AuditPublicationEvent', N'U') IS NULL
BEGIN
    CREATE TABLE arc.AuditPublicationEvent
    (
        Id              bigint         IDENTITY(1,1) NOT NULL,
        ChangedAt       datetime2(3)   NOT NULL,
        EntityType      nvarchar(64)   NOT NULL,
        EntityId        int            NOT NULL,
        ResultDiffJson  nvarchar(max)  NULL,
        ChangeReason    nvarchar(1000) NOT NULL,
        ChangedByUserId int            NOT NULL,
        CONSTRAINT PK_arcAuditPublicationEvent PRIMARY KEY CLUSTERED (ChangedAt, Id) ON ps_AuditByMonth(ChangedAt)
    ) ON ps_AuditByMonth(ChangedAt);
END
GO

-- Архівований аудит так само незмінний: UPDATE/DELETE відхиляються (THROW 50060,
-- як у aud.*). SWITCH (туди й назад) — DDL, тригерів не запускає.
CREATE OR ALTER TRIGGER arc.TR_AuditCellChange_Immutable
ON arc.AuditCellChange
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 50060, N'Архів arc.AuditCellChange незмінний: UPDATE і DELETE заборонені (ФВ-5.21).', 1;
END;
GO

CREATE OR ALTER TRIGGER arc.TR_AuditStructureChange_Immutable
ON arc.AuditStructureChange
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 50060, N'Архів arc.AuditStructureChange незмінний: UPDATE і DELETE заборонені (ФВ-5.21).', 1;
END;
GO

CREATE OR ALTER TRIGGER arc.TR_AuditSecurityEvent_Immutable
ON arc.AuditSecurityEvent
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 50060, N'Архів arc.AuditSecurityEvent незмінний: UPDATE і DELETE заборонені (ФВ-5.21).', 1;
END;
GO

CREATE OR ALTER TRIGGER arc.TR_AuditPublicationEvent_Immutable
ON arc.AuditPublicationEvent
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 50060, N'Архів arc.AuditPublicationEvent незмінний: UPDATE і DELETE заборонені (ФВ-5.21).', 1;
END;
GO
