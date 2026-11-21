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

-- src/Ecr.Infrastructure/Persistence/Sql/02-partitions.sql
--
-- ⚠ Ідемпотентний, як і решта скриптів теки (`Q-047`). Раніше він був єдиним,
-- хто падав на повторному запуску з `Msg 2714 There is already an object
-- named …`. Це не косметика: коли розгортання зривається на кроці після
-- `02`, оператор перезапускає послідовність — і впирається в помилку там, де
-- усе вже зроблено правильно.

-- PeriodKey = Year*100 + Sequence (R-A6). Одна партиція = один період.
--
-- ✎ PSS-09 (аудит 20.11; рішення людини 20.11 №6 — межі для поточного року, історичні
-- роки зараз не мігруються): межі — від 202601 (перший рік системи; нижче нічого не
-- створюється) до СІЧНЯ року Y+2 включно, де Y — поточний рік UTC на момент розгортання.
-- Раніше межі були зашиті до 202712: база, розгорнута у 2028+, писала поточний рік у
-- крайню праву партицію, а наступний SPLIT переміщував би ці рядки під Sch-M.
-- Січень Y+2 «закриває» грудень наступного року: дані проєкту наступного року (він може
-- з'явитися ще в цьому) лягають у власні партиції, а крайня права лишається порожньою —
-- SPLIT наперед лишається операцією метаданих.
-- Далі межі тримає `arc.usp_EnsurePartitions` (04): поточний рік, наступний і запас на
-- @MonthsAhead; `PartitionCheckJob` стежить за запасом і за рядками в крайніх партиціях.
-- ⚠ Нижня межа 202601 — стала: на ній стоять архів (`usp_ArchiveYear` вимагає 12 меж на
-- рік) і тести. Межі історичних років — окремий крок DBA ПЕРЕД міграцією історії (SPLIT
-- ще порожньої лівої партиції), а не цей скрипт.
IF NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = N'pf_ByPeriodKey')
BEGIN
    DECLARE @pkLastYear int = YEAR(SYSUTCDATETIME()) + 2;
    IF @pkLastYear < 2028 SET @pkLastYear = 2028;

    DECLARE @pkValues nvarchar(max) = N'', @pkYear int = 2026, @pkMonth int;
    WHILE @pkYear < @pkLastYear
    BEGIN
        SET @pkMonth = 1;
        WHILE @pkMonth <= 12
        BEGIN
            SET @pkValues = @pkValues + N', ' + CAST(@pkYear * 100 + @pkMonth AS nvarchar(10));
            SET @pkMonth = @pkMonth + 1;
        END
        SET @pkYear = @pkYear + 1;
    END
    SET @pkValues = @pkValues + N', ' + CAST(@pkLastYear * 100 + 1 AS nvarchar(10));

    DECLARE @pkSql nvarchar(max) =
        N'CREATE PARTITION FUNCTION pf_ByPeriodKey (int) AS RANGE RIGHT FOR VALUES ('
        + STUFF(@pkValues, 1, 2, N'') + N');';
    EXEC sys.sp_executesql @pkSql;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = N'ps_ByPeriodKey')
BEGIN
    EXEC(N'CREATE PARTITION SCHEME ps_ByPeriodKey
           AS PARTITION pf_ByPeriodKey ALL TO ([DATA_HOT]);');
END
GO

-- Аудит партиціонується ОКРЕМО, по ChangedAt: місяць зміни і звітний період —
-- різні осі (зміна за січень може статися в березні).
-- ✎ PSS-09: межі — від 2026-01-01 до пізнішої з дат 2027-06-01 (як було) і «перше число
-- поточного місяця UTC + 12 місяців»: база, розгорнута пізніше, не пише аудит у крайню
-- праву партицію до першого нічного `arc.usp_EnsureAuditPartitions`.
IF NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = N'pf_AuditByMonth')
BEGIN
    DECLARE @auNow date = CAST(SYSUTCDATETIME() AS date);
    DECLARE @auLast date = DATEADD(MONTH, 12, DATEFROMPARTS(YEAR(@auNow), MONTH(@auNow), 1));
    IF @auLast < '2027-06-01' SET @auLast = '2027-06-01';

    DECLARE @auValues nvarchar(max) = N'', @auAt date = '2026-01-01';
    WHILE @auAt <= @auLast
    BEGIN
        SET @auValues = @auValues + N', ''' + CONVERT(nvarchar(10), @auAt, 120) + N'''';
        SET @auAt = DATEADD(MONTH, 1, @auAt);
    END

    DECLARE @auSql nvarchar(max) =
        N'CREATE PARTITION FUNCTION pf_AuditByMonth (datetime2(3)) AS RANGE RIGHT FOR VALUES ('
        + STUFF(@auValues, 1, 2, N'') + N');';
    EXEC sys.sp_executesql @auSql;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = N'ps_AuditByMonth')
BEGIN
    EXEC(N'CREATE PARTITION SCHEME ps_AuditByMonth
           AS PARTITION pf_AuditByMonth ALL TO ([AUDIT]);');
END
GO
