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

-- Зіставлення перевіряється ПЕРШИМ і саме тут (02a §1.0, Q-061): це остання
-- мить, коли його ще можна виправити — база порожня, і її досить перестворити.
-- Після заповнення зміна зіставлення означає перенесення всіх даних.
--
-- Падаємо лише на CS: саме чутливість до регістру змінює ПОВЕДІНКУ, бо від неї
-- залежить, чи UQ_Template_Code вважає 'ABC' і 'abc' одним кодом. Відхилення в
-- мовній частині (Cyrillic_General_CI_AS замість еталонного) поведінку
-- унікальності не змінює — про нього достатньо повідомити.
DECLARE @Collation nvarchar(200) =
    CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(200));

IF @Collation LIKE N'%[_]CS[_]%'
    THROW 50032, N'Зіставлення бази чутливе до регістру (CS). Унікальність бізнес-кодів працюватиме інакше, ніж описано. Перестворіть базу: CREATE DATABASE ... COLLATE Latin1_General_100_CI_AS_SC (02a §1.0).', 1;

IF @Collation <> N'Latin1_General_100_CI_AS_SC'
    PRINT N'ПОПЕРЕДЖЕННЯ: зіставлення бази ' + @Collation
        + N' відрізняється від еталонного Latin1_General_100_CI_AS_SC (02a §1.0). '
        + N'Унікальність бізнес-кодів не змінюється (CI збережено), але порядок сортування — так.';

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

-- ⛔ Одного лише видання НЕ ДОСИТЬ, і коштувало це цілого диска. Замовник
--    ухвалив ставити локально **Developer Edition** (`H-19`), і в неї
--    EngineEdition = 3 — та сама, що в Enterprise. Перевірка вище мовчала, і
--    КОЖНА тестова база народжувалася на 14 ГБ: 4096 + 4096 + 4096 + 2048.
--    Інстанс на цій машині ще й називається `SQLEXPRESS`, тобто ім'я казало
--    «Express», а видання — ні; сімнадцять тестових баз з'їли 152 ГБ і
--    зупинили роботу помилкою «operating system error 112».
--
-- ⚠ Видання — це про МЕЖУ (Express не витягне 14 ГБ), а розмір файлів має
--    вирішувати ПРИЗНАЧЕННЯ бази: тестовій на кілька сотень рядків продуктивні
--    розміри не потрібні на жодному виданні. Призначення скрипт вивести не
--    може — його треба сказати, і сказати ЯВНО.
--
--    Позначка ставиться на базі одразу після CREATE DATABASE:
--
--        EXEC sys.sp_addextendedproperty @name = N'Ecr_SmallFiles', @value = 1;
--
--    Її ставить `SqlServerFixture`; DBA в розгортанні не ставить нічого, і
--    продуктивна поведінка не змінюється ні на байт. Умовчання лишається
--    продуктивним навмисно: база, яка мовчки отримала 64 МБ замість 4 ГБ,
--    деградує під навантаженням непомітно, а це гірше за зайвий рядок у
--    чек-листі розгортання.
DECLARE @markedSmall bit = CASE WHEN EXISTS (
        SELECT 1 FROM sys.extended_properties
        WHERE class = 0 AND name = N'Ecr_SmallFiles')
    THEN 1 ELSE 0 END;

DECLARE @isSmall bit = CASE WHEN @isExpress = 1 OR @markedSmall = 1 THEN 1 ELSE 0 END;

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
CROSS APPLY (SELECT SizeMb   = CASE WHEN @isSmall = 1 THEN 64 ELSE f.SizeMb0   END,
                    GrowthMb = CASE WHEN @isSmall = 1 THEN 64 ELSE f.GrowthMb0 END) AS sz
WHERE NOT EXISTS (SELECT 1 FROM sys.database_files d WHERE d.name = f.LogicalName);

IF @sql IS NOT NULL EXEC sp_executesql @sql;

PRINT N'База ' + @db + N': файлові групи і файли готові'
    + CASE WHEN @isSmall = 1
           THEN N' (зменшені початкові розміри: '
              + CASE WHEN @isExpress = 1 THEN N'Express' ELSE N'позначка Ecr_SmallFiles' END
              + N').'
           ELSE N'.' END;
PRINT N'  каталог даних: ' + @DataPath;
PRINT N'  каталог архіву: ' + @ArchivePath;
GO
