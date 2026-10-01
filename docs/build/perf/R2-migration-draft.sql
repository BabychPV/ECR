-- docs/build/perf/R2-migration-draft.sql
--
-- ЧЕРНЕТКА, НЕ РОЗГОРТАЄТЬСЯ. Лежить поза `src/.../Sql/`, тому `setup-dev-db.ps1`,
-- `deploy-ecr.ps1` і сторож «скрипти є в дереві, але не виконуються» її не бачать.
-- Виконується ВРУЧНУ, один раз, ПІСЛЯ RC і ДО прод-запуску збору, у вікні
-- обслуговування (див. docs/build/perf/R2-drill-2026-10-01.md: вимірювання, журнал,
-- відкат). План і причини: docs/build/perf/R2-rawdatapoint-plan.md.
--
-- Що робить (R2b + R2c + R2d одним проходом, потім R2e):
--   КРОК 1 (R2b+R2c+R2d): ext.RawDataPoint -> КЛАСТЕР UNIQUE (SourceEntityId, SourcePath,
--           Timestamp) під іменем UQ_RawDataPoint, PK_RawDataPoint (Id) стає NONCLUSTERED,
--           дубль-індекс зникає (кластер і є UQ), PAGE-стиснення, файлова група DATA_HOT.
--   КРОК 2 (R2e): pf_/ps_RawByMonth (Timestamp, помісячно, RANGE RIGHT, ALL TO DATA_HOT);
--           кластер переноситься на схему (DROP_EXISTING), PK стає (Id, Timestamp) — вирівняний.
--
-- Ключ кластера: 4 + 800 (nvarchar(400)) + 8 (datetime2(3)) = 812 Б, межа UNIQUE-індексу — 900 Б.
--
-- ⚠ ОНОВИТИ перед запуском: межі pf_RawByMonth (КРОК 2) мають покривати дані + запас наперед.
--    Нижче вписано межі до 2028-12-01 (≈ 26 місяців від 2026-10): `arc.usp_EnsurePartitions` (04) після
--    R2 довантажує їх щомісяця (див. R2-rawdatapoint-plan.md, розділ «Перелік змін», п. A2).
--    Дані ДО першої межі (2026-01-01) лягають у партицію 1, ПІСЛЯ останньої — в останню.
--
-- ⚠ ЦЕЙ ФАЙЛ — sqlcmd-ЧЕРНЕТКА (`:setvar`, `$(Online)`). Версія для `Sql/07-partition-tables.sql` мусить
--    бути ЧИСТИМ T-SQL: його виконують і SqlClient (SqlServerFixture.RunScriptAsync), і емулятор sqlcmd у
--    DeployScriptsRerunTests.RunLikeSqlcmdAsync, де `:setvar` не існує; а `$(...)` навіть у коментарі
--    sqlcmd підставляє як змінну. У 07: ONLINE = OFF без змінної, межі ті ж, тіло КРОКІВ 1–2 те саме.
--    Покрокова відповідність і список змін коду — у плані, розділ «Перелік змін (R2b–R2e одним проходом)».
-- ⚠ Запуск: sqlcmd -S <srv> -E -C -b -I -d <db> -v Online=OFF -i R2-migration-draft.sql
--    Online=ON лише на Enterprise/Developer (на Standard ONLINE недоступний — лишити OFF).
-- ⚠ Ідемпотентний: кожен крок дивиться в каталог і пропускається, якщо вже виконаний.
--    Повторний запуск після успіху — лише PRINT'и та перевірки.
-- ⚠ Стан бази до/після кроків друкується PRINT'ами: порівняти кількість рядків
--    «до» і «після» (КРОК 3 падає THROW, якщо не збігається з тим, що зафіксовано в КРОК 0).

:setvar Online OFF

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
GO

-- ============================================================================
-- КРОК 0. Передумови. Нічого не змінює.
-- ============================================================================
IF OBJECT_ID(N'ext.RawDataPoint', N'U') IS NULL
    THROW 50100, N'R2: ext.RawDataPoint не існує (міграції EF ще не застосовані).', 1;

IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE referenced_object_id = OBJECT_ID(N'ext.RawDataPoint'))
    THROW 50101, N'R2: на ext.RawDataPoint посилається зовнішній ключ; PK_RawDataPoint не можна знімати. Перевірити схему.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.filegroups WHERE name = N'DATA_HOT')
    THROW 50102, N'R2: немає файлової групи DATA_HOT (01-filegroups.sql не виконано).', 1;

-- Кількість рядків фіксується в розширеній властивості БД (переживає GO, зникає на КРОК 3).
DECLARE @rows bigint = (SELECT SUM(p.rows) FROM sys.partitions p
                        WHERE p.object_id = OBJECT_ID(N'ext.RawDataPoint') AND p.index_id IN (0, 1));
IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE class = 0 AND name = N'R2_RowsBefore')
    EXEC sys.sp_dropextendedproperty @name = N'R2_RowsBefore';
EXEC sys.sp_addextendedproperty @name = N'R2_RowsBefore', @value = @rows;
PRINT CONCAT(N'R2 КРОК 0: рядків ', @rows, N'; ', CONVERT(nvarchar(30), SYSUTCDATETIME(), 126));
GO

-- ============================================================================
-- КРОК 1. R2b + R2c + R2d: кластер за ключем читачів, PK Id NONCLUSTERED, PAGE, DATA_HOT.
-- Одна транзакція: таблиця або в старому стані, або в новому. Sch-M лок на весь час
-- (вікно обслуговування). Порядок підібрано під мінімум перебудов:
--   1) зняти NC-дубль UQ (дешево);
--   2) зняти кластерний PK -> купа (на таблиці без NC це зміна метаданих, без MOVE TO);
--   3) створити кластер (єдине повне сортування + запис у DATA_HOT зі стисненням);
--   4) PK NONCLUSTERED (Id) — друга, значно менша, побудова.
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'ext.RawDataPoint') AND type = 1 AND name = N'UQ_RawDataPoint')
BEGIN
    PRINT CONCAT(N'R2 КРОК 1: початок ', CONVERT(nvarchar(30), SYSUTCDATETIME(), 126));
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    IF EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'ext.RawDataPoint') AND name = N'UQ_RawDataPoint' AND type = 2)
    BEGIN
        -- UQ_RawDataPoint створено міграцією EF як UNIQUE INDEX (is_unique_constraint = 0)
        -- або як constraint — обробляємо обидва види.
        IF EXISTS (SELECT 1 FROM sys.key_constraints
                   WHERE parent_object_id = OBJECT_ID(N'ext.RawDataPoint') AND name = N'UQ_RawDataPoint')
            ALTER TABLE ext.RawDataPoint DROP CONSTRAINT UQ_RawDataPoint;
        ELSE
            DROP INDEX UQ_RawDataPoint ON ext.RawDataPoint;
    END;

    IF EXISTS (SELECT 1 FROM sys.key_constraints
               WHERE parent_object_id = OBJECT_ID(N'ext.RawDataPoint') AND name = N'PK_RawDataPoint')
        ALTER TABLE ext.RawDataPoint DROP CONSTRAINT PK_RawDataPoint;

    -- Кластер під іменем UQ_RawDataPoint: ім'я збігається з EF-моделлю (HasIndex(...).IsUnique()),
    -- тож snapshot міграцій не змінюється й EF-міграція не потрібна.
    CREATE UNIQUE CLUSTERED INDEX UQ_RawDataPoint
        ON ext.RawDataPoint (SourceEntityId, SourcePath, [Timestamp])
        WITH (DATA_COMPRESSION = PAGE, SORT_IN_TEMPDB = ON, ONLINE = $(Online))
        ON [DATA_HOT];

    ALTER TABLE ext.RawDataPoint
        ADD CONSTRAINT PK_RawDataPoint PRIMARY KEY NONCLUSTERED (Id)
        WITH (DATA_COMPRESSION = PAGE, ONLINE = $(Online))
        ON [DATA_HOT];

    COMMIT TRANSACTION;
    PRINT CONCAT(N'R2 КРОК 1: кінець ', CONVERT(nvarchar(30), SYSUTCDATETIME(), 126));
END
ELSE
    PRINT N'R2 КРОК 1: пропущено (кластер UQ_RawDataPoint уже є).';
GO

-- ============================================================================
-- КРОК 2. R2e: помісячне партиціонування за Timestamp.
-- Межі: RANGE RIGHT. Узгодити з даними: рядки до першої межі потрапляють у партицію 1,
-- після останньої — в останню (вона росте, доки PartitionCheckJob/04 не додасть межі).
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = N'pf_RawByMonth')
BEGIN
    CREATE PARTITION FUNCTION pf_RawByMonth (datetime2(3))
    AS RANGE RIGHT FOR VALUES
    (
        '2026-01-01', '2026-02-01', '2026-03-01', '2026-04-01', '2026-05-01', '2026-06-01',
        '2026-07-01', '2026-08-01', '2026-09-01', '2026-10-01', '2026-11-01', '2026-12-01',
        '2027-01-01', '2027-02-01', '2027-03-01', '2027-04-01', '2027-05-01', '2027-06-01',
        '2027-07-01', '2027-08-01', '2027-09-01', '2027-10-01', '2027-11-01', '2027-12-01',
        '2028-01-01', '2028-02-01', '2028-03-01', '2028-04-01', '2028-05-01', '2028-06-01',
        '2028-07-01', '2028-08-01', '2028-09-01', '2028-10-01', '2028-11-01', '2028-12-01'
    );
    PRINT N'R2 КРОК 2: створено pf_RawByMonth.';
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = N'ps_RawByMonth')
BEGIN
    EXEC (N'CREATE PARTITION SCHEME ps_RawByMonth AS PARTITION pf_RawByMonth ALL TO ([DATA_HOT]);');
    PRINT N'R2 КРОК 2: створено ps_RawByMonth.';
END;
GO

IF NOT EXISTS (SELECT 1
               FROM sys.indexes i
               JOIN sys.partition_schemes ps ON ps.data_space_id = i.data_space_id
               WHERE i.object_id = OBJECT_ID(N'ext.RawDataPoint') AND i.type = 1 AND ps.name = N'ps_RawByMonth')
BEGIN
    PRINT CONCAT(N'R2 КРОК 2: початок ', CONVERT(nvarchar(30), SYSUTCDATETIME(), 126));
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    -- PK знімається ПЕРЕД перебудовою кластера: інакше NC перебудовується двічі.
    IF EXISTS (SELECT 1 FROM sys.key_constraints
               WHERE parent_object_id = OBJECT_ID(N'ext.RawDataPoint') AND name = N'PK_RawDataPoint')
        ALTER TABLE ext.RawDataPoint DROP CONSTRAINT PK_RawDataPoint;

    -- DROP_EXISTING: одна перебудова, ключ і унікальність не змінюються.
    CREATE UNIQUE CLUSTERED INDEX UQ_RawDataPoint
        ON ext.RawDataPoint (SourceEntityId, SourcePath, [Timestamp])
        WITH (DROP_EXISTING = ON, DATA_COMPRESSION = PAGE, SORT_IN_TEMPDB = ON, ONLINE = $(Online))
        ON ps_RawByMonth ([Timestamp]);

    -- Вирівняний PK: унікальний індекс на схемі мусить містити стовпець партиціонування.
    -- Унікальність Id на практиці тримає IDENTITY; альтернатива (PK Id невирівняний на DATA_HOT)
    -- блокує SWITCH/партиційне видалення — вибір описано в R2-drill-2026-10-01.md.
    ALTER TABLE ext.RawDataPoint
        ADD CONSTRAINT PK_RawDataPoint PRIMARY KEY NONCLUSTERED (Id, [Timestamp])
        WITH (DATA_COMPRESSION = PAGE, ONLINE = $(Online))
        ON ps_RawByMonth ([Timestamp]);

    COMMIT TRANSACTION;
    PRINT CONCAT(N'R2 КРОК 2: кінець ', CONVERT(nvarchar(30), SYSUTCDATETIME(), 126));
END
ELSE
    PRINT N'R2 КРОК 2: пропущено (кластер уже на ps_RawByMonth).';
GO

-- ============================================================================
-- КРОК 3. Перевірка. Падає THROW, якщо щось не так; нічого не змінює,
-- окрім зняття тимчасової властивості.
-- ============================================================================
DECLARE @before bigint = CAST((SELECT value FROM sys.extended_properties WHERE class = 0 AND name = N'R2_RowsBefore') AS bigint);
DECLARE @after bigint = (SELECT SUM(p.rows) FROM sys.partitions p
                         WHERE p.object_id = OBJECT_ID(N'ext.RawDataPoint') AND p.index_id = 1);
IF @before IS NOT NULL AND @before <> @after
    THROW 50110, N'R2: кількість рядків після перебудови не збігається з фіксацією в КРОК 0.', 1;

IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'ext.RawDataPoint') AND is_not_trusted = 1)
    THROW 50111, N'R2: зовнішній ключ ext.RawDataPoint став недовіреним (is_not_trusted = 1); виконати ALTER TABLE ... WITH CHECK CHECK CONSTRAINT.', 1;

IF (SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'ext.RawDataPoint') AND type = 1 AND name = N'UQ_RawDataPoint' AND is_unique = 1) <> 1
    THROW 50112, N'R2: кластерного унікального індексу UQ_RawDataPoint немає.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'ext.RawDataPoint') AND name = N'PK_RawDataPoint' AND type = 2 AND is_primary_key = 1)
    THROW 50113, N'R2: PK_RawDataPoint NONCLUSTERED відсутній.', 1;

IF EXISTS (SELECT 1 FROM sys.partitions WHERE object_id = OBJECT_ID(N'ext.RawDataPoint') AND data_compression_desc <> N'PAGE' AND rows > 0)
    THROW 50114, N'R2: є партиції без PAGE-стиснення.', 1;

IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE class = 0 AND name = N'R2_RowsBefore')
    EXEC sys.sp_dropextendedproperty @name = N'R2_RowsBefore';

PRINT CONCAT(N'R2 КРОК 3: OK, рядків ', @after);

-- Підсумок: розмір і розподіл за партиціями.
SELECT i.name AS IndexName, i.type_desc, p.partition_number, p.rows, p.data_compression_desc,
       CAST(SUM(a.total_pages) * 8 / 1024.0 AS decimal(18,1)) AS TotalMB
FROM sys.indexes i
JOIN sys.partitions p ON p.object_id = i.object_id AND p.index_id = i.index_id
JOIN sys.allocation_units a ON a.container_id = p.partition_id
WHERE i.object_id = OBJECT_ID(N'ext.RawDataPoint') AND p.rows > 0
GROUP BY i.name, i.type_desc, p.partition_number, p.rows, p.data_compression_desc
ORDER BY i.name, p.partition_number;
GO

-- ============================================================================
-- ВІДКАТ (не виконується; для DBA). Повернення до «PK Id кластерний + NC UQ» — ще одна
-- повна перебудова такого ж обсягу, тому основний відкат — ВІДНОВЛЕННЯ з резервної копії,
-- зробленої перед вікном. Логічний зворотний скрипт:
--
--   BEGIN TRANSACTION;
--   ALTER TABLE ext.RawDataPoint DROP CONSTRAINT PK_RawDataPoint;
--   DROP INDEX UQ_RawDataPoint ON ext.RawDataPoint;               -- -> купа
--   ALTER TABLE ext.RawDataPoint ADD CONSTRAINT PK_RawDataPoint PRIMARY KEY CLUSTERED (Id) ON [PRIMARY];
--   CREATE UNIQUE NONCLUSTERED INDEX UQ_RawDataPoint
--       ON ext.RawDataPoint (SourceEntityId, SourcePath, [Timestamp]) ON [PRIMARY];
--   COMMIT;
--   -- за потреби: DROP PARTITION SCHEME ps_RawByMonth; DROP PARTITION FUNCTION pf_RawByMonth;
-- ============================================================================
