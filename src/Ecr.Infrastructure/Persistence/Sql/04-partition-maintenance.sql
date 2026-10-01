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

    -- Аудит: межі pf_AuditByMonth - на 12 місяців уперед (окрема функція, окрема процедура).
    EXEC arc.usp_EnsureAuditPartitions @MonthsAhead = 12;

    INSERT INTO itg.MaintenanceRun (JobCode, StartedAt, FinishedAt, Status)
    VALUES (N'EnsurePartitions', SYSUTCDATETIME(), SYSUTCDATETIME(), N'Completed');
END;
GO

-- Автообслуговування `pf_AuditByMonth`: межі (перші дні місяців) тримаються
-- щонайменше на @MonthsAhead місяців уперед від @Today (за замовчуванням —
-- поточна дата UTC). До цього межі закінчувалися 2027-06-01 і ніхто їх не
-- довантажував: після червня 2027 аудит мовчки йшов би в останню партицію.
--
-- ⚠ Ідемпотентна: додає лише ВІДСУТНІ межі; повторний виклик нічого не змінює,
-- дубль межі неможливий (перевірка за sys.partition_range_values). Межі, що вже
-- далеко попереду, не чіпає. Якщо функції немає (БД без партиціонування) —
-- нічого не робить і повертає @Added = 0.
--
-- ⚠ Безпека для даних: SPLIT RANGE розбиває партицію, що ВЖЕ містить межу з
-- правого боку. Для нових місяців це крайня права партиція, у якій немає рядків
-- (ChangedAt — момент запису, майбутніх дат немає), тож SPLIT — операція
-- метаданих: дані не переміщуються. Утримує SCH-M на таблицях, що лежать на
-- ps_AuditByMonth, лише на мить; `LOCK_TIMEOUT` не дає чекати вічно за довгою
-- транзакцією (виняток 1222 — прогін помилкою, повторить наступна ніч).
-- Партицію з даними (межа ВСЕРЕДИНІ вже заповненого діапазону) процедура сама
-- не створює: вона йде лише вперед від поточного місяця.
--
-- ⚠ WITH EXECUTE AS OWNER: обліковому запису застосунку DDL-прав не потрібно
-- (D-66) — потрібне лише право EXECUTE на процедуру; ALTER PARTITION FUNCTION
-- виконується від імені власника схеми (dbo). Тригери незмінності aud.* на DDL
-- не реагують.
CREATE OR ALTER PROCEDURE arc.usp_EnsureAuditPartitions
    @MonthsAhead int = 12,
    @Today date = NULL,
    @Added int = NULL OUTPUT
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET LOCK_TIMEOUT 30000;
    SET @Added = 0;

    IF NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = N'pf_AuditByMonth')
        RETURN;

    DECLARE @first date = DATEFROMPARTS(YEAR(COALESCE(@Today, CAST(GETUTCDATE() AS date))),
                                        MONTH(COALESCE(@Today, CAST(GETUTCDATE() AS date))), 1);
    DECLARE @i int = 1, @d date, @sql nvarchar(400);

    WHILE @i <= @MonthsAhead
    BEGIN
        SET @d = DATEADD(MONTH, @i, @first);

        IF NOT EXISTS (SELECT 1 FROM sys.partition_range_values rv
                       JOIN sys.partition_functions pf ON pf.function_id = rv.function_id
                       WHERE pf.name = N'pf_AuditByMonth'
                         AND CAST(rv.value AS datetime2(3)) = CAST(@d AS datetime2(3)))
        BEGIN
            ALTER PARTITION SCHEME ps_AuditByMonth NEXT USED [AUDIT];
            SET @sql = N'ALTER PARTITION FUNCTION pf_AuditByMonth() SPLIT RANGE ('''
                     + CONVERT(nvarchar(10), @d, 120) + N''');';
            EXEC sp_executesql @sql;
            SET @Added = @Added + 1;
        END

        SET @i = @i + 1;
    END
END;
GO

-- D-247 / D-236 / НФ-8.4b: архівація аудиту. Партиції aud.* (CellChange,
-- StructureChange, SecurityEvent, PublicationEvent), ПОВНІСТЮ старші за
-- @OlderThanMonths місяців, перемикаються (SWITCH PARTITION) у дзеркала
-- arc.Audit* (12-archive-tables.sql) тієї ж структури на тій самій
-- ps_AuditByMonth. Рядки не копіюються і не видаляються: це метаданкова
-- операція, тому тригери незмінності (THROW 50060 на UPDATE/DELETE) НЕ
-- спрацьовують і НЕ вимикаються — аудит лишається незмінним, лише змінює
-- таблицю-власника (в архівних дзеркалах діють такі самі тригери).
--
-- Слід: кожен перенос = рядок itg.MaintenanceRun (JobCode = 'audit-archive',
-- Status = 'Succeeded', DetailsJson: таблиця, партиція, період (початок місяця),
-- кількість рядків, виконавець ORIGINAL_LOGIN(), поріг). Перенос і запис
-- журналу — в ОДНІЙ транзакції. Збій: відкат + рядок 'Failed' з текстом помилки
-- + повторний THROW. Ціль непорожня (у архіві вже є рядки цього місяця) —
-- партиція НЕ переноситься, рядок 'Degraded' (потрібна ручна розв'язка, runbook).
--
-- ⚠ Ідемпотентна: порожні/уже перенесені партиції пропускаються мовчки (без
-- рядка журналу). Поточний і «гарячі» місяці (нові за @OlderThanMonths, мінімум 1
-- = поточний місяць ніколи не архівується) не чіпає. Читачі (AuditReader)
-- дивляться лише в aud.* — архівований період із UI історії зникає; тому дефолт
-- 24 місяці (припущення, не вказане в D-236; змінюється параметром).
--
-- ⚠ WITH EXECUTE AS OWNER: обліковому запису застосунку DDL-прав не треба (D-66)
-- — лише EXECUTE на процедуру; ALTER TABLE … SWITCH виконується від dbo.
CREATE OR ALTER PROCEDURE arc.usp_ArchiveAudit
    @OlderThanMonths int = 24,
    @Today date = NULL,
    @PartitionsSwitched int = NULL OUTPUT,
    @RowsSwitched bigint = NULL OUTPUT
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET LOCK_TIMEOUT 30000;
    SET @PartitionsSwitched = 0;
    SET @RowsSwitched = 0;

    IF NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = N'pf_AuditByMonth')
        RETURN;

    IF @OlderThanMonths IS NULL OR @OlderThanMonths < 1
        SET @OlderThanMonths = 1;

    DECLARE @t date = COALESCE(@Today, CAST(GETUTCDATE() AS date));
    DECLARE @cutoff datetime2(3) =
        DATEADD(MONTH, -@OlderThanMonths, DATEFROMPARTS(YEAR(@t), MONTH(@t), 1));
    DECLARE @maxPart int = $PARTITION.pf_AuditByMonth(@cutoff) - 1;

    DECLARE @work TABLE
    (
        Ord int IDENTITY(1,1) PRIMARY KEY,
        Src nvarchar(128) NOT NULL,
        Dst nvarchar(128) NOT NULL,
        P   int           NOT NULL
    );

    INSERT INTO @work (Src, Dst, P)
    SELECT m.Src, m.Dst, p.partition_number
    FROM (VALUES (N'aud.CellChange',      N'arc.AuditCellChange'),
                 (N'aud.StructureChange', N'arc.AuditStructureChange'),
                 (N'aud.SecurityEvent',   N'arc.AuditSecurityEvent'),
                 (N'aud.PublicationEvent', N'arc.AuditPublicationEvent')) AS m (Src, Dst)
    JOIN sys.partitions AS p
      ON p.object_id = OBJECT_ID(m.Src) AND p.index_id = 1
     AND p.partition_number <= @maxPart AND p.rows > 0
    WHERE OBJECT_ID(m.Dst) IS NOT NULL
    ORDER BY p.partition_number, m.Src;

    DECLARE @i int = 1, @n int = (SELECT COUNT(*) FROM @work);
    DECLARE @src nvarchar(128), @dst nvarchar(128), @p int, @sql nvarchar(1000);
    DECLARE @cnt bigint, @dstRows bigint, @lo datetime2(3), @started datetime2(3), @details nvarchar(2000);

    WHILE @i <= @n
    BEGIN
        SELECT @src = Src, @dst = Dst, @p = P FROM @work WHERE Ord = @i;
        SET @i = @i + 1;
        SET @started = SYSUTCDATETIME();

        SELECT @lo = CAST(rv.value AS datetime2(3))
        FROM sys.partition_range_values rv
        JOIN sys.partition_functions pf ON pf.function_id = rv.function_id
        WHERE pf.name = N'pf_AuditByMonth' AND rv.boundary_id = @p - 1;
        IF @p = 1 SET @lo = NULL;

        SELECT @dstRows = ISNULL(SUM(rows), 0) FROM sys.partitions
        WHERE object_id = OBJECT_ID(@dst) AND index_id = 1 AND partition_number = @p;

        SET @sql = N'SELECT @c = COUNT_BIG(*) FROM ' + @src
                 + N' WHERE $PARTITION.pf_AuditByMonth(ChangedAt) = ' + CAST(@p AS nvarchar(10)) + N';';

        BEGIN TRY
            BEGIN TRAN;

            EXEC sp_executesql @sql, N'@c bigint OUTPUT', @c = @cnt OUTPUT;

            IF @cnt = 0
            BEGIN
                COMMIT;
                CONTINUE;
            END

            SET @details = N'{"table":"' + @src + N'","partition":' + CAST(@p AS nvarchar(10))
                + N',"periodStart":' + ISNULL(N'"' + CONVERT(nvarchar(10), @lo, 120) + N'"', N'null')
                + N',"rows":' + CAST(@cnt AS nvarchar(20))
                + N',"by":"' + STRING_ESCAPE(ORIGINAL_LOGIN(), N'json')
                + N'","olderThanMonths":' + CAST(@OlderThanMonths AS nvarchar(10)) + N'}';

            IF @dstRows > 0
            BEGIN
                SET @details = N'{"table":"' + @src + N'","partition":' + CAST(@p AS nvarchar(10))
                    + N',"periodStart":' + ISNULL(N'"' + CONVERT(nvarchar(10), @lo, 120) + N'"', N'null')
                    + N',"reason":"ціль ' + @dst + N' має рядки в цій партиції; SWITCH пропущено"}';
                INSERT INTO itg.MaintenanceRun (JobCode, StartedAt, FinishedAt, Status, DetailsJson)
                VALUES (N'audit-archive', @started, SYSUTCDATETIME(), N'Degraded', @details);
                COMMIT;
                CONTINUE;
            END

            SET @sql = N'ALTER TABLE ' + @src + N' SWITCH PARTITION ' + CAST(@p AS nvarchar(10))
                     + N' TO ' + @dst + N' PARTITION ' + CAST(@p AS nvarchar(10)) + N';';
            EXEC sp_executesql @sql;

            INSERT INTO itg.MaintenanceRun (JobCode, StartedAt, FinishedAt, Status, DetailsJson)
            VALUES (N'audit-archive', @started, SYSUTCDATETIME(), N'Succeeded', @details);

            COMMIT;

            SET @PartitionsSwitched = @PartitionsSwitched + 1;
            SET @RowsSwitched = @RowsSwitched + @cnt;
        END TRY
        BEGIN CATCH
            IF XACT_STATE() <> 0 ROLLBACK;

            SET @details = N'{"table":"' + @src + N'","partition":' + CAST(@p AS nvarchar(10))
                + N',"error":"' + STRING_ESCAPE(LEFT(ERROR_MESSAGE(), 500), N'json') + N'"}';
            INSERT INTO itg.MaintenanceRun (JobCode, StartedAt, FinishedAt, Status, DetailsJson)
            VALUES (N'audit-archive', @started, SYSUTCDATETIME(), N'Failed', @details);

            THROW;
        END CATCH
    END
END;
GO
