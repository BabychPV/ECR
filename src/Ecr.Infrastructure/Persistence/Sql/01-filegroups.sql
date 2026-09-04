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
--   • Заповнювати @ArchivePath потрібно лише тоді, коли архівна файлова група
--     має лежати на іншому носії — саме в цьому сенс окремої групи.
--
-- Ідемпотентний: повторний запуск не падає і нічого не дублює.
--
--   sqlcmd -S <сервер> -d <база> -E -i 01-filegroups.sql

SET NOCOUNT ON;

DECLARE @DataPath    nvarchar(260) = NULL;   -- NULL = типовий каталог інстансу
DECLARE @ArchivePath nvarchar(260) = NULL;   -- NULL = той самий, що й @DataPath

-- NULLIF: SERVERPROPERTY на нетиповій конфігурації може віддати порожній рядок,
-- і без цієї перевірки файли поїхали б у корінь диска.
SET @DataPath = NULLIF(LTRIM(RTRIM(COALESCE(
    @DataPath, CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(260))))), N'');

IF @DataPath IS NULL
    THROW 50020, N'Не вдалося визначити каталог даних інстансу. Задайте @DataPath на початку скрипта.', 1;

IF RIGHT(@DataPath, 1) <> N'\' SET @DataPath = @DataPath + N'\';

SET @ArchivePath = NULLIF(LTRIM(RTRIM(COALESCE(@ArchivePath, @DataPath))), N'');
IF RIGHT(@ArchivePath, 1) <> N'\' SET @ArchivePath = @ArchivePath + N'\';

DECLARE @db sysname = DB_NAME();
DECLARE @sql nvarchar(max);

-- ⚠ Express (EngineEdition = 4) має межу 10 ГБ на базу, а продуктивні розміри
--    дають 14 ГБ — скрипт упав би на третьому файлі з незрозумілою помилкою.
--    На Express створюємо маленькі файли: вони ростуть за потреби, а
--    партиціонування, columnstore і компресія там доступні з 2016 SP1, тобто
--    перевіряти фізичну модель на ньому можна повноцінно.
DECLARE @isExpress bit = CASE WHEN CAST(SERVERPROPERTY('EngineEdition') AS int) = 4 THEN 1 ELSE 0 END;

-- 1. Файлові групи, яких ще немає.
--    Один пакет DDL замість циклу: коротше і без курсорів у скрипті,
--    який читає людина перед запуском на проді.
SELECT @sql = STRING_AGG(
        CAST(N'ALTER DATABASE ' + QUOTENAME(@db) + N' ADD FILEGROUP ' + QUOTENAME(g.Name) + N';'
             AS nvarchar(max)), NCHAR(10))
FROM (VALUES (N'DATA_HOT'), (N'DATA_ARCHIVE'), (N'AUDIT'), (N'INDEXES')) AS g(Name)
WHERE NOT EXISTS (SELECT 1 FROM sys.filegroups f WHERE f.name = g.Name);

IF @sql IS NOT NULL EXEC sp_executesql @sql;

-- 2. Файли. Розміри і приріст — за профілем навантаження: архів росте ривками
--    при архівації року, гарячі дані — рівномірно, індекси найповільніше.
--    Виконується ПІСЛЯ кроку 1: ADD FILE вимагає наявної файлової групи.
SET @sql = NULL;

SELECT @sql = STRING_AGG(
        CAST(N'ALTER DATABASE ' + QUOTENAME(@db) + N' ADD FILE (NAME = '
             + QUOTENAME(f.LogicalName, '''')
             -- ⚠ Ім'я БАЗИ у фізичному імені файла обов'язкове. Без нього
             -- дві бази ECR на одному інстансі неможливі: друга падає з
             -- «One or more files listed in the statement could not be found
             -- or could not be initialized», бо шлях уже зайнятий першою.
             -- Це не лише про тести: dev і test на спільному сервері — це
             -- звичайна ситуація (`Q-055`).
             + N', FILENAME = ' + QUOTENAME(
                   CASE WHEN f.UseArchivePath = 1 THEN @ArchivePath ELSE @DataPath END
                   + @db + N'_' + f.LogicalName + N'.ndf', '''')
             + N', SIZE = ' + CAST(sz.SizeMb AS nvarchar(10)) + N'MB'
             + N', FILEGROWTH = ' + CAST(sz.GrowthMb AS nvarchar(10)) + N'MB)'
             + N' TO FILEGROUP ' + QUOTENAME(f.FileGroup) + N';'
             AS nvarchar(max)), NCHAR(10))
FROM (VALUES
        (N'Ecr_hot',     N'DATA_HOT',     4096, 1024, 0),
        (N'Ecr_archive', N'DATA_ARCHIVE', 4096, 4096, 1),
        (N'Ecr_audit',   N'AUDIT',        4096, 2048, 0),
        (N'Ecr_idx',     N'INDEXES',      2048, 1024, 0)
     ) AS f(LogicalName, FileGroup, SizeMb0, GrowthMb0, UseArchivePath)
CROSS APPLY (SELECT SizeMb   = CASE WHEN @isExpress = 1 THEN 64 ELSE f.SizeMb0   END,
                    GrowthMb = CASE WHEN @isExpress = 1 THEN 64 ELSE f.GrowthMb0 END) AS sz
WHERE NOT EXISTS (SELECT 1 FROM sys.database_files d WHERE d.name = f.LogicalName);

IF @sql IS NOT NULL EXEC sp_executesql @sql;

PRINT N'База ' + @db + N': файлові групи і файли готові'
    + CASE WHEN @isExpress = 1 THEN N' (Express: зменшені початкові розміри).' ELSE N'.' END;
PRINT N'  каталог даних: ' + @DataPath;
PRINT N'  каталог архіву: ' + @ArchivePath;
GO
