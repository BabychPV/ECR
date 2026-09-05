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

-- src/Ecr.Infrastructure/Persistence/Sql/07-partition-tables.sql
--
-- Прив'язує партиційовані таблиці до схем партиціонування.
--
-- ⚠ ЧОМУ ЦЕ ОКРЕМИЙ СКРИПТ, А НЕ МІГРАЦІЯ EF
-- `ON ps_ByPeriodKey(PeriodKey)` — частина `CREATE TABLE`/`CREATE INDEX`, і
-- `migrationBuilder.CreateTable` цього не вміє: жодної анотації для розміщення
-- на схемі партиціонування в EF Core немає. Тому міграції створюють ФОРМУ
-- (стовпці, ключі, FK, індекси, CHECK), а цей скрипт — ФІЗИЧНЕ РОЗМІЩЕННЯ.
-- Без нього таблиці лягають на PRIMARY, і тоді мовчки не працює нічого з
-- моделі архівації: ні `TRUNCATE … WITH (PARTITIONS)` у `arc.usp_ArchiveYear`,
-- ні `SPLIT`/`MERGE` у `04-partition-maintenance.sql`, ні `PartitionCheckJob`.
-- Помилки при цьому не буде — процедура виконається і не звільнить нічого.
--
-- ПОРЯДОК ЗАПУСКУ: 01-filegroups → 02-partitions → міграції EF → 07 → 06-rcsi.
--
-- Скрипт ідемпотентний: індекс, який уже лежить на потрібній схемі,
-- пропускається. Перенесення робиться через `DROP_EXISTING = ON`, а не
-- `DROP`+`CREATE`: обмеження (PK/UNIQUE) і посилання на них із чужих FK при
-- цьому зберігаються. `ALTER INDEX … REBUILD` тут не годиться взагалі — він
-- не вміє змінювати розміщення.
--
-- Таблиці, яких ще немає (calc.*, aud.* до етапів 3–5), просто пропускаються.

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @map TABLE
(
    SchemaName  sysname,
    TableName   sysname,
    SchemeName  sysname,
    PartColumn  sysname,
    CompressPk  bit
);

-- Дані документів: партиціонуються за PeriodKey. Одна партиція = один період.
INSERT @map (SchemaName, TableName, SchemeName, PartColumn, CompressPk) VALUES
    (N'doc',  N'TableInstance',     N'ps_ByPeriodKey', N'PeriodKey', 0),
    (N'doc',  N'TableRow',          N'ps_ByPeriodKey', N'PeriodKey', 0),
    -- ⚠ ОСНОВНИЙ ОБСЯГ: ~108 млн рядків на рік. PAGE-стиснення кластерного
    -- індексу тут не оптимізація, а умова, за якої обсяг узагалі керований.
    (N'doc',  N'CellValue',         N'ps_ByPeriodKey', N'PeriodKey', 1),
    (N'calc', N'CalculationResult', N'ps_ByPeriodKey', N'PeriodKey', 0),
    (N'calc', N'CalculationInput',  N'ps_ByPeriodKey', N'PeriodKey', 0),
    (N'calc', N'CalculationStep',   N'ps_ByPeriodKey', N'PeriodKey', 0),
    -- Аудит партиціонується ОКРЕМО, по ChangedAt: місяць зміни і звітний
    -- період — різні осі (зміна за січень може статися в березні).
    (N'aud',  N'CellChange',        N'ps_AuditByMonth', N'ChangedAt', 1),
    (N'aud',  N'StructureChange',   N'ps_AuditByMonth', N'ChangedAt', 0),
    (N'aud',  N'SecurityEvent',     N'ps_AuditByMonth', N'ChangedAt', 0),
    (N'aud',  N'PublicationEvent',  N'ps_AuditByMonth', N'ChangedAt', 0);

-- Схема партиціонування має існувати: без 02-partitions.sql далі нема сенсу.
IF NOT EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = N'ps_ByPeriodKey')
    THROW 50030, N'Схеми ps_ByPeriodKey немає. Спершу виконайте 02-partitions.sql.', 1;

DECLARE @sql       nvarchar(max),
        @sch       sysname,
        @tbl       sysname,
        @scheme    sysname,
        @partCol   sysname,
        @idxName   sysname,
        @isUnique  bit,
        @isClust   bit,
        @filter    nvarchar(max),
        @keyCols   nvarchar(max),
        @incCols   nvarchar(max),
        @compress  nvarchar(20),
        @moved     int = 0,
        @skipped   int = 0;

DECLARE cur CURSOR LOCAL FAST_FORWARD FOR
    SELECT m.SchemaName,
           m.TableName,
           m.SchemeName,
           m.PartColumn,
           i.name,
           i.is_unique,
           CASE WHEN i.type = 1 THEN 1 ELSE 0 END,
           i.filter_definition,
           -- Партиційний стовпець ОБОВ'ЯЗКОВО присутній у ключі кожного
           -- унікального індексу — інакше SQL Server відмовиться його
           -- вирівняти. У 02a-db-schema.md усі ключі так і виписані.
           STUFF((SELECT N', ' + QUOTENAME(c.name)
                         + CASE WHEN ic.is_descending_key = 1 THEN N' DESC' ELSE N'' END
                  FROM sys.index_columns ic
                  JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                  WHERE ic.object_id = i.object_id
                    AND ic.index_id = i.index_id
                    AND ic.is_included_column = 0
                  ORDER BY ic.key_ordinal
                  FOR XML PATH(''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N''),
           STUFF((SELECT N', ' + QUOTENAME(c.name)
                  FROM sys.index_columns ic
                  JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                  WHERE ic.object_id = i.object_id
                    AND ic.index_id = i.index_id
                    AND ic.is_included_column = 1
                  ORDER BY ic.index_column_id
                  FOR XML PATH(''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N''),
           CASE WHEN m.CompressPk = 1 AND i.type = 1 THEN N'PAGE' ELSE N'NONE' END
    FROM @map AS m
    JOIN sys.tables  AS t ON t.name = m.TableName AND SCHEMA_NAME(t.schema_id) = m.SchemaName
    JOIN sys.indexes AS i ON i.object_id = t.object_id AND i.type IN (1, 2)
    JOIN sys.data_spaces AS ds ON ds.data_space_id = i.data_space_id
    WHERE ds.name <> m.SchemeName;         -- те, що вже на схемі, не чіпаємо

OPEN cur;
FETCH NEXT FROM cur INTO @sch, @tbl, @scheme, @partCol, @idxName,
                         @isUnique, @isClust, @filter, @keyCols, @incCols, @compress;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'CREATE '
             + CASE WHEN @isUnique = 1 THEN N'UNIQUE ' ELSE N'' END
             + CASE WHEN @isClust  = 1 THEN N'CLUSTERED ' ELSE N'NONCLUSTERED ' END
             + N'INDEX ' + QUOTENAME(@idxName)
             + N' ON ' + QUOTENAME(@sch) + N'.' + QUOTENAME(@tbl)
             + N' (' + @keyCols + N')'
             + CASE WHEN @incCols IS NOT NULL THEN N' INCLUDE (' + @incCols + N')' ELSE N'' END
             + CASE WHEN @filter  IS NOT NULL THEN N' WHERE ' + @filter ELSE N'' END
             + N' WITH (DROP_EXISTING = ON, DATA_COMPRESSION = ' + @compress + N')'
             + N' ON ' + QUOTENAME(@scheme) + N'(' + QUOTENAME(@partCol) + N');';

    PRINT N'  ' + @sql;
    EXEC sys.sp_executesql @sql;
    SET @moved += 1;

    FETCH NEXT FROM cur INTO @sch, @tbl, @scheme, @partCol, @idxName,
                             @isUnique, @isClust, @filter, @keyCols, @incCols, @compress;
END

CLOSE cur;
DEALLOCATE cur;

SELECT @skipped = COUNT(*)
FROM @map AS m
JOIN sys.tables AS t ON t.name = m.TableName AND SCHEMA_NAME(t.schema_id) = m.SchemaName
JOIN sys.indexes AS i ON i.object_id = t.object_id AND i.type IN (1, 2)
JOIN sys.data_spaces AS ds ON ds.data_space_id = i.data_space_id
WHERE ds.name = m.SchemeName;

PRINT N'Перенесено індексів: ' + CAST(@moved AS nvarchar(10))
    + N'; усього на схемах партиціонування: ' + CAST(@skipped AS nvarchar(10)) + N'.';
GO

-- ── Повернення довіри зовнішнім ключам ───────────────────────────────────
--
-- ⛔ Перебудова кластерного індексу через `DROP_EXISTING` знімає з зовнішніх
-- ключів ознаку ДОВІРЕНОСТІ: перенесення таблиці на схему партиціонування
-- пересоздає індекс, на який ключ посилається, і SQL Server більше не
-- ручається, що дані йому відповідають.
--
-- ⚠ Наслідок тихий і подвійний. Оптимізатор недовірене обмеження ІГНОРУЄ —
-- плани стають гіршими без жодної помилки. І `D-117` прямо розрізняє
-- `WITH CHECK` від `NOCHECK` як «ключ є» проти «ключ намальовано»: після
-- розгортання ми опинялися саме в другому стані, не знаючи про це.
--
-- ⚠ Тут це коштує нічого: скрипт виконується на порожніх таблицях одразу
-- після міграцій. На заповненій базі та сама команда сканує все — і саме тому
-- вона стоїть у розгортанні, а не в обслуговуванні.
DECLARE @recheck nvarchar(max) = N'';

SELECT @recheck = @recheck
     + N'ALTER TABLE ' + QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name)
     + N' WITH CHECK CHECK CONSTRAINT ' + QUOTENAME(fk.name) + N';' + CHAR(10)
FROM sys.foreign_keys AS fk
JOIN sys.tables AS t ON t.object_id = fk.parent_object_id
WHERE fk.is_not_trusted = 1
  AND fk.is_disabled = 0
  AND SCHEMA_NAME(t.schema_id) IN (N'doc', N'calc', N'aud');

IF LEN(@recheck) > 0 EXEC sp_executesql @recheck;
GO

-- Перевірка: жоден зовнішній ключ гарячих схем не лишається недовіреним.
IF EXISTS
(
    SELECT 1
    FROM sys.foreign_keys AS fk
    JOIN sys.tables AS t ON t.object_id = fk.parent_object_id
    WHERE fk.is_not_trusted = 1
      AND fk.is_disabled = 0
      AND SCHEMA_NAME(t.schema_id) IN (N'doc', N'calc', N'aud')
)
    THROW 50032, N'Частина зовнішніх ключів лишилася недовіреною: оптимізатор їх ігноруватиме.', 1;
GO

-- Перевірка: після скрипту жоден індекс партиційованої таблиці не має лежати
-- поза своєю схемою. Якщо лишився — далі йти не можна, бо архівація
-- «працюватиме» і не звільнятиме нічого.
IF EXISTS
(
    SELECT 1
    FROM sys.indexes AS i
    JOIN sys.tables  AS t  ON t.object_id = i.object_id
    JOIN sys.data_spaces AS ds ON ds.data_space_id = i.data_space_id
    WHERE i.type IN (1, 2)
      AND SCHEMA_NAME(t.schema_id) IN (N'doc', N'calc', N'aud')
      AND t.name IN (N'TableInstance', N'TableRow', N'CellValue',
                     N'CalculationResult', N'CalculationInput', N'CalculationStep',
                     N'CellChange', N'StructureChange', N'SecurityEvent', N'PublicationEvent')
      AND ds.type_desc <> N'PARTITION_SCHEME'   -- type_desc, а не type: у type код 'PS'
)
    THROW 50031, N'Частина індексів партиційованих таблиць лишилася поза схемою партиціонування.', 1;
GO
