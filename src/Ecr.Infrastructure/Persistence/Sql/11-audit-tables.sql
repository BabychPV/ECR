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

-- src/Ecr.Infrastructure/Persistence/Sql/11-audit-tables.sql
--
-- Таблиці схеми `aud` — аудит (§12 `02a-db-schema.md`).
--
-- ⚠ ЧОМУ ЦЕ ОКРЕМИЙ СКРИПТ
-- Та сама причина, що у `08-system-tables.sql`: у таблиць `aud.*` **немає
-- доменних сутностей**, доступ до них іде через порт `IAuditWriter`
-- пакетним записом, а чого немає в моделі EF — того міграція не створить.
-- Без цього скрипта модуль 1.9 (аудит) неможливо ні реалізувати, ні
-- перевірити: `IAuditWriter` не має куди писати (`Q-049`).
--
-- ⚠ ПОРЯДОК: цей скрипт має йти ПЕРЕД `07-partition-tables.sql`, бо `07`
-- переносить `aud.CellChange`, `StructureChange`, `SecurityEvent` і
-- `PublicationEvent` на `ps_AuditByMonth`. Якщо таблиць ще немає, `07` їх
-- просто пропускає — і аудит лишається на `PRIMARY` з усіма наслідками
-- `Q-035`.
--
-- Витягнуто ДОСЛІВНО з `02a-db-schema.md` §12.
-- Ідемпотентний: усе під `IF OBJECT_ID(...) IS NULL`.

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF SCHEMA_ID(N'aud') IS NULL
    EXEC(N'CREATE SCHEMA aud AUTHORIZATION dbo;');
GO

IF OBJECT_ID(N'aud.CellChange', N'U') IS NULL
BEGIN
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
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CellChange_Cell'
               AND object_id = OBJECT_ID(N'aud.CellChange'))
BEGIN
    CREATE INDEX IX_CellChange_Cell
        ON aud.CellChange (DocumentId, TableRowId, ColumnDefId, ChangedAt DESC)
        ON ps_AuditByMonth(ChangedAt);
END
GO

IF OBJECT_ID(N'aud.StructureChange', N'U') IS NULL
BEGIN
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
END
GO

IF OBJECT_ID(N'aud.SecurityEvent', N'U') IS NULL
BEGIN
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
END
GO

IF OBJECT_ID(N'aud.PublicationEvent', N'U') IS NULL
BEGIN
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
END
GO

IF OBJECT_ID(N'aud.ConsistencyIssue', N'U') IS NULL
BEGIN
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
END
GO

-- ⚠ Додано 2026-09-04 (Q-071). Таблиця була в §12 схеми з самого початку, але
-- у цей скрипт не потрапила: `Q-049` перелічив `aud.*` на око і пропустив
-- одну з шести. Наслідок мовчазний і рівно того класу, проти якого скрипт
-- писався — сеанс симуляції нема куди записати, а без запису «подивитися
-- очима» стає способом безслідно переглянути чужі дані (D-96).
--
-- НЕ партиціонується, на відміну від решти `aud.*`: сеансів симуляції одиниці
-- на місяць, і `ps_AuditByMonth` тут дав би порожні партиції без користі.
IF OBJECT_ID(N'aud.SimulationSession', N'U') IS NULL
BEGIN
    CREATE TABLE aud.SimulationSession
    (
        Id             bigint       IDENTITY(1,1) NOT NULL,
        ActorUserId    int          NOT NULL,   -- хто симулює
        SubjectUserId  int          NOT NULL,   -- чиїми очима
        Reason         nvarchar(1000) NOT NULL,
        StartedAt      datetime2(3) NOT NULL,
        EndedAt        datetime2(3) NULL,
        CONSTRAINT PK_SimSession PRIMARY KEY (Id),
        -- FK на sec.[User] тут доречні, на відміну від решти aud.*: сеансів
        -- одиниці, а посилання на неіснуючого користувача зробило б журнал
        -- симуляцій непридатним саме тоді, коли він потрібен.
        CONSTRAINT FK_SimSession_Actor   FOREIGN KEY (ActorUserId)   REFERENCES sec.[User] (Id),
        CONSTRAINT FK_SimSession_Subject FOREIGN KEY (SubjectUserId) REFERENCES sec.[User] (Id),
        CONSTRAINT CK_SimSession_NotSelf CHECK (ActorUserId <> SubjectUserId)
    );
END
GO
