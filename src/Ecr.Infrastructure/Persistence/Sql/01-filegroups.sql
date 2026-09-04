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
--   • Перевизначити потрібно лише тоді, коли архів має лежати на іншому
--     носії: тоді заповніть @DataPath / @ArchivePath нижче.
--
-- Ідемпотентний: повторний запуск не падає і нічого не дублює.
--
--   sqlcmd -S <сервер> -d <база> -E -i 01-filegroups.sql

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @DataPath    nvarchar(260) = NULL;   -- NULL = типовий каталог інстансу
DECLARE @ArchivePath nvarchar(260) = NULL;   -- NULL = той самий, що й @DataPath

IF @DataPath IS NULL
    SET @DataPath = CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(260));

IF @DataPath IS NULL
    THROW 50020, N'Не вдалося визначити каталог даних інстансу. Задайте @DataPath у скрипті.', 1;

IF RIGHT(@DataPath, 1) <> N'\' SET @DataPath = @DataPath + N'\';
IF @ArchivePath IS NULL SET @ArchivePath = @DataPath;
IF RIGHT(@ArchivePath, 1) <> N'\' SET @ArchivePath = @ArchivePath + N'\';

DECLARE @db sysname = DB_NAME();
DECLARE @sql nvarchar(max);

-- 1. Файлові групи
DECLARE @fg TABLE (Name sysname PRIMARY KEY);
INSERT INTO @fg (Name) VALUES (N'DATA_HOT'), (N'DATA_ARCHIVE'), (N'AUDIT'), (N'INDEXES');

DECLARE @name sysname;
DECLARE fg CURSOR LOCAL FAST_FORWARD FOR SELECT Name FROM @fg;
OPEN fg;
FETCH NEXT FROM fg INTO @name;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.filegroups WHERE name = @name)
    BEGIN
        SET @sql = N'ALTER DATABASE ' + QUOTENAME(@db) + N' ADD FILEGROUP ' + QUOTENAME(@name) + N';';
        EXEC sp_executesql @sql;
    END
    FETCH NEXT FROM fg INTO @name;
END
CLOSE fg; DEALLOCATE fg;

-- 2. Файли. Розміри і приріст — за профілем навантаження: архів росте
--    ривками при архівації року, гарячі дані — рівномірно.
DECLARE @files TABLE (LogicalName sysname, FileGroup sysname, SizeMb int, GrowthMb int, UseArchivePath bit);
INSERT INTO @files (LogicalName, FileGroup, SizeMb, GrowthMb, UseArchivePath) VALUES
    (N'Ecr_hot',     N'DATA_HOT',     4096, 1024, 0),
    (N'Ecr_archive', N'DATA_ARCHIVE', 4096, 4096, 1),
    (N'Ecr_audit',   N'AUDIT',        4096, 2048, 0),
    (N'Ecr_idx',     N'INDEXES',      2048, 1024, 0);

DECLARE @logical sysname, @group sysname, @size int, @growth int, @useArc bit, @file nvarchar(400);
DECLARE fl CURSOR LOCAL FAST_FORWARD FOR
    SELECT LogicalName, FileGroup, SizeMb, GrowthMb, UseArchivePath FROM @files;
OPEN fl;
FETCH NEXT FROM fl INTO @logical, @group, @size, @growth, @useArc;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.database_files WHERE name = @logical)
    BEGIN
        SET @file = CASE WHEN @useArc = 1 THEN @ArchivePath ELSE @DataPath END + @logical + N'.ndf';
        SET @sql = N'ALTER DATABASE ' + QUOTENAME(@db) + N' ADD FILE (NAME = ' + QUOTENAME(@logical, '''')
                 + N', FILENAME = ' + QUOTENAME(@file, '''')
                 + N', SIZE = ' + CAST(@size AS nvarchar(10)) + N'MB'
                 + N', FILEGROWTH = ' + CAST(@growth AS nvarchar(10)) + N'MB)'
                 + N' TO FILEGROUP ' + QUOTENAME(@group) + N';';
        EXEC sp_executesql @sql;
    END
    FETCH NEXT FROM fl INTO @logical, @group, @size, @growth, @useArc;
END
CLOSE fl; DEALLOCATE fl;

PRINT N'Файлові групи і файли готові. Каталог даних: ' + @DataPath + N', архів: ' + @ArchivePath;
GO
