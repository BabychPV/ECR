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
