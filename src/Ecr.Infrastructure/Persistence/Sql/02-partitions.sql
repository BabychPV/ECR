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
-- Межі виписуються наперед; PartitionCheckJob стежить за запасом на 6 періодів.
IF NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = N'pf_ByPeriodKey')
BEGIN
    CREATE PARTITION FUNCTION pf_ByPeriodKey (int)
    AS RANGE RIGHT FOR VALUES
    (
        202601, 202602, 202603, 202604, 202605, 202606,
        202607, 202608, 202609, 202610, 202611, 202612,
        202701, 202702, 202703, 202704, 202705, 202706,
        202707, 202708, 202709, 202710, 202711, 202712
    );
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
IF NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = N'pf_AuditByMonth')
BEGIN
    CREATE PARTITION FUNCTION pf_AuditByMonth (datetime2(3))
    AS RANGE RIGHT FOR VALUES
    (
        '2026-01-01', '2026-02-01', '2026-03-01', '2026-04-01', '2026-05-01', '2026-06-01',
        '2026-07-01', '2026-08-01', '2026-09-01', '2026-10-01', '2026-11-01', '2026-12-01',
        '2027-01-01', '2027-02-01', '2027-03-01', '2027-04-01', '2027-05-01', '2027-06-01'
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = N'ps_AuditByMonth')
BEGIN
    EXEC(N'CREATE PARTITION SCHEME ps_AuditByMonth
           AS PARTITION pf_AuditByMonth ALL TO ([AUDIT]);');
END
GO
