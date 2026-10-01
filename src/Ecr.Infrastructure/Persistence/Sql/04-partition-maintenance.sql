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
