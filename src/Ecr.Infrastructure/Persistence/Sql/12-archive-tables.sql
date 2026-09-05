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
    INDEX CCI_arc_CellChange CLUSTERED COLUMNSTORE
) ON [DATA_ARCHIVE];
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
    Value                decimal(28,10) NOT NULL,
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
    Value            decimal(28,10) NULL,
    TraceJson        nvarchar(max)  NULL,
    INDEX CCI_arc_CalculationStep CLUSTERED COLUMNSTORE
) ON [DATA_ARCHIVE];
GO
