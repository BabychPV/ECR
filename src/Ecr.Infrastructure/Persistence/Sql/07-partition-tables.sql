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
    -- HSE301 M2 (§4.4): провенанс підтягування за вікном рядка належить
    -- періоду так само, як комірка, яку він пояснює.
    (N'ext',  N'RowWindowValue',    N'ps_ByPeriodKey', N'PeriodKey', 0),
    -- CELL-STYLE L3b: користувацька заливка/колір тексту/жирність комірок — розріджена, належить періоду так само,
    -- як комірка, яку оформлює; зовнішніх ключів на doc.TableRow немає (TRUNCATE ... PARTITIONS архівації).
    (N'doc',  N'CellStyle',         N'ps_ByPeriodKey', N'PeriodKey', 0),
    -- ✎ D-461: позначки «ручне значення замість формули» — розріджені, належать періоду так само, як комірка;
    -- зовнішніх ключів на doc.TableRow немає (TRUNCATE ... PARTITIONS архівації).
    (N'doc',  N'CellFormulaOverride', N'ps_ByPeriodKey', N'PeriodKey', 0),
    -- Feature (j): parts of a month value split between permits — belong to the month (PeriodKey) like the cell; no FK to
    -- doc.TableRow (logical address), so arc.usp_ArchiveYear may TRUNCATE ... PARTITIONS after copying them to arc.*.
    (N'doc',  N'CellPermitPart',    N'ps_ByPeriodKey', N'PeriodKey', 0),
    -- Аудит партиціонується ОКРЕМО, по ChangedAt: місяць зміни і звітний
    -- період — різні осі (зміна за січень може статися в березні).
    (N'aud',  N'CellChange',        N'ps_AuditByMonth', N'ChangedAt', 1),
    (N'aud',  N'StructureChange',   N'ps_AuditByMonth', N'ChangedAt', 0),
    (N'aud',  N'SecurityEvent',     N'ps_AuditByMonth', N'ChangedAt', 0),
    (N'aud',  N'PublicationEvent',  N'ps_AuditByMonth', N'ChangedAt', 0),
    (N'aud',  N'RowChange',         N'ps_AuditByMonth', N'ChangedAt', 0);

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

-- ── Індекс під вимір впливу довідника (GET /api/v1/registries/{code}/impact) ──
--
-- `RegistryImpactStore` іде від версій методологій, що читають довідник
-- (`cfg.RegistryUse`, SourceKind = 1), до `calc.CalculationResult` за
-- `MethodologyVersionId` БЕЗ фільтра по періоду. Єдиний індекс таблиці
-- `IX_CalculationResult_Lookup` починається з `PeriodKey`, тож без цього
-- індексу запит сканується по всіх партиціях: читання ростуть лінійно з
-- обсягом таблиці (замір — docs/build/perf/impact-query-2026-10-01.md).
--
-- ⚠ Індекс створюється ТУТ, а не міграцією EF: він мусить лежати на схемі
-- `ps_ByPeriodKey` (інакше 50031 нижче), а EF цього не вміє. Виконується на
-- кожному проході (ідемпотентно); на заповненій базі — офлайн-побудова
-- (Standard-редакція не має ONLINE), тому в `-Upgrade` це вікно обслуговування.
IF OBJECT_ID(N'calc.CalculationResult', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'calc.CalculationResult')
                     AND name = N'IX_CalculationResult_Version')
    CREATE NONCLUSTERED INDEX IX_CalculationResult_Version
        ON calc.CalculationResult (MethodologyVersionId, PeriodKey)
        INCLUDE (CalculationRunId, DocumentId)
        ON ps_ByPeriodKey (PeriodKey);
GO

-- ── R5-Q1-02: результати АКТУАЛЬНОГО прогону документа (ReadCurrentAsync, перенос) ──
--
-- `CalculationResultStore.ReadCurrentAsync` (кожен GET зрізу з колонкою `Calculated`,
-- панель результатів, експорт) бере результати документа за період лише з актуальних
-- прогонів. `IX_CalculationResult_Lookup` не містить `CalculationRunId`, а результати
-- `Superseded`-прогонів не видаляються (ЗБР-1, «нічого не затирається»), тож відбір за
-- прогоном коштував key lookup на кожен результат КОЖНОГО минулого прогону — вартість
-- росла лінійно з кількістю перерахунків. Із цим індексом — seek до прогону; INCLUDE
-- покриває проєкцію `ReadCurrentAsync`, вартість не залежить від історії.
--
-- ⚠ Тут, а не в EF, з тієї ж причини, що й `IX_CalculationResult_Version` (`ps_ByPeriodKey`).
-- ⚠ `Value` в INCLUDE: міграція, що змінює тип `calc.CalculationResult.Value`, мусить зняти
-- й поставити назад і цей індекс (помилка 5074), як уже робить з `IX_CalculationResult_Lookup`.
-- На заповненій базі — офлайн-побудова (Standard без ONLINE), у `-Upgrade` це вікно обслуговування.
-- ✎ CRY01: ключ продовжено `Kind, TableInstanceId` (форма — у блоці «CRY01» нижче, він же перебудовує наявні бази).
IF OBJECT_ID(N'calc.CalculationResult', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'calc.CalculationResult')
                     AND name = N'IX_CalculationResult_DocRun')
    CREATE NONCLUSTERED INDEX IX_CalculationResult_DocRun
        ON calc.CalculationResult (PeriodKey, DocumentId, CalculationRunId, Kind, TableInstanceId)
        INCLUDE (MethodologyVersionId, OutputCode, SourceRowKey, Value, UnitId, SubstanceEntryId)
        ON ps_ByPeriodKey (PeriodKey);
GO

-- ── CRY01 (замінює блок D-401): `Kind, TableInstanceId` у КЛЮЧІ `IX_CalculationResult_DocRun` для наявних баз ──
--
-- Огляд «Calculation results» за рік (`CalculationResultStore.Year`) читає лише виходи (`Kind = 0`) і сторінкою —
-- лише екземпляри однієї таблиці. `Kind` не було в індексі взагалі, `TableInstanceId` — лише в INCLUDE, тож кожна
-- сторінка читала ВСІ числа документа за рік (синтетика 240 тис. чисел: 3 671 читання на сторінку → 768).
-- Префікс ключа (PeriodKey, DocumentId, CalculationRunId) і покриття проєкції `ReadCurrentAsync` (D-401,
-- R5-Q1-02) — без змін: нові стовпці лише в кінці ключа, `TableInstanceId` переїхав з INCLUDE у ключ.
-- Умова — індекс є, але `Kind` не 4-й ключовий стовпець або `TableInstanceId` не 5-й: перебудовує і базу до
-- D-401 (без `TableInstanceId`), і базу з INCLUDE (D-401); повторний прохід нічого не робить.
-- ⚠ `ON ps_ByPeriodKey (PeriodKey)` обов'язково: без нього індекс упав би на PRIMARY (50031 нижче).
-- Standard без ONLINE: офлайн-побудова (`DROP_EXISTING` читає й пише весь індекс), у `-Upgrade` це вікно
-- обслуговування (runbook §8.2).
IF OBJECT_ID(N'calc.CalculationResult', N'U') IS NOT NULL
   AND COL_LENGTH(N'calc.CalculationResult', N'TableInstanceId') IS NOT NULL
   AND COL_LENGTH(N'calc.CalculationResult', N'Kind') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'calc.CalculationResult')
                 AND name = N'IX_CalculationResult_DocRun')
   AND (SELECT COUNT(*)
          FROM sys.indexes AS i
          JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
          JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
         WHERE i.object_id = OBJECT_ID(N'calc.CalculationResult')
           AND i.name = N'IX_CalculationResult_DocRun'
           AND ic.is_included_column = 0
           AND ((c.name = N'Kind' AND ic.key_ordinal = 4)
                OR (c.name = N'TableInstanceId' AND ic.key_ordinal = 5))) < 2
    CREATE NONCLUSTERED INDEX IX_CalculationResult_DocRun
        ON calc.CalculationResult (PeriodKey, DocumentId, CalculationRunId, Kind, TableInstanceId)
        INCLUDE (MethodologyVersionId, OutputCode, SourceRowKey, Value, UnitId, SubstanceEntryId)
        WITH (DROP_EXISTING = ON, SORT_IN_TEMPDB = ON, DATA_COMPRESSION = PAGE)
        ON ps_ByPeriodKey (PeriodKey);
GO

-- ── D-560 / PSS-01: індекси ЗА ПРОГОНОМ на трьох таблицях `calc.*` ─────────
--
-- Прибирання неактуальних прогонів (`SupersededResultsPurger`) видаляє прогін ПОВНІСТЮ: рядки результатів, кроків,
-- входів і сам рядок `calc.CalculationRun`. Без індексу з провідним `CalculationRunId`:
--   * `DELETE … WHERE CalculationRunId = @run` сканує партицію кластерного індексу на кожен пакет;
--   * перевірка FK (`FK_CRes_Run`, `FK_CStep_Run`, `FK_CIn_Run`, NO ACTION) при видаленні рядка прогону сканує ВСЮ
--     дочірню таблицю (усі партиції) блокувальним читанням — на стенді Land `calc.CalculationStep` = 45,9 ГБ.
-- Індекс вузький (ключ `CalculationRunId` + локатор `PeriodKey, Id`), вирівняний по `ps_ByPeriodKey` (інакше 50031 нижче
-- й архівація), PAGE-стиснений. Вставка нових рядків пише в кінець діапазону свого прогону.
--
-- ⚠ Будується ОДИН раз на базі, де його ще немає. Enterprise/Developer/Azure (`EngineEdition` 3, 5, 8) — `ONLINE = ON`;
-- Standard/Express — офлайн (запис у таблицю чекає до кінця побудови; оновлення й так зупиняє служби, runbook §8.2).
-- Обсяг побудови — одне читання таблиці; після першого прибирання таблиці в рази менші.
IF OBJECT_ID(N'calc.CalculationResult', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'calc.CalculationResult') AND name = N'IX_CalculationResult_Run')
BEGIN
    IF CAST(SERVERPROPERTY('EngineEdition') AS int) IN (3, 5, 8)
        CREATE NONCLUSTERED INDEX IX_CalculationResult_Run
            ON calc.CalculationResult (CalculationRunId)
            WITH (ONLINE = ON, SORT_IN_TEMPDB = ON, DATA_COMPRESSION = PAGE)
            ON ps_ByPeriodKey (PeriodKey);
    ELSE
        CREATE NONCLUSTERED INDEX IX_CalculationResult_Run
            ON calc.CalculationResult (CalculationRunId)
            WITH (SORT_IN_TEMPDB = ON, DATA_COMPRESSION = PAGE)
            ON ps_ByPeriodKey (PeriodKey);
END
GO

IF OBJECT_ID(N'calc.CalculationStep', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'calc.CalculationStep') AND name = N'IX_CalculationStep_Run')
BEGIN
    IF CAST(SERVERPROPERTY('EngineEdition') AS int) IN (3, 5, 8)
        CREATE NONCLUSTERED INDEX IX_CalculationStep_Run
            ON calc.CalculationStep (CalculationRunId)
            WITH (ONLINE = ON, SORT_IN_TEMPDB = ON, DATA_COMPRESSION = PAGE)
            ON ps_ByPeriodKey (PeriodKey);
    ELSE
        CREATE NONCLUSTERED INDEX IX_CalculationStep_Run
            ON calc.CalculationStep (CalculationRunId)
            WITH (SORT_IN_TEMPDB = ON, DATA_COMPRESSION = PAGE)
            ON ps_ByPeriodKey (PeriodKey);
END
GO

IF OBJECT_ID(N'calc.CalculationInput', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'calc.CalculationInput') AND name = N'IX_CalculationInput_Run')
BEGIN
    IF CAST(SERVERPROPERTY('EngineEdition') AS int) IN (3, 5, 8)
        CREATE NONCLUSTERED INDEX IX_CalculationInput_Run
            ON calc.CalculationInput (CalculationRunId)
            WITH (ONLINE = ON, SORT_IN_TEMPDB = ON, DATA_COMPRESSION = PAGE)
            ON ps_ByPeriodKey (PeriodKey);
    ELSE
        CREATE NONCLUSTERED INDEX IX_CalculationInput_Run
            ON calc.CalculationInput (CalculationRunId)
            WITH (SORT_IN_TEMPDB = ON, DATA_COMPRESSION = PAGE)
            ON ps_ByPeriodKey (PeriodKey);
END
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
  AND (SCHEMA_NAME(t.schema_id) IN (N'doc', N'calc', N'aud')
       OR (SCHEMA_NAME(t.schema_id) = N'ext' AND t.name = N'RowWindowValue'));

IF LEN(@recheck) > 0 EXEC sp_executesql @recheck;
GO

-- Перевірка: жоден зовнішній ключ гарячих схем не лишається недовіреним.
IF EXISTS
(
    SELECT 1
    FROM sys.foreign_keys AS fk
    JOIN sys.tables AS t ON t.object_id = fk.parent_object_id
    WHERE (fk.is_not_trusted = 1 OR fk.is_disabled = 1)
      AND (SCHEMA_NAME(t.schema_id) IN (N'doc', N'calc', N'aud')
           OR (SCHEMA_NAME(t.schema_id) = N'ext' AND t.name = N'RowWindowValue'))
)
    THROW 50032, N'Частина зовнішніх ключів недовірена або вимкнена.', 1;
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
      AND SCHEMA_NAME(t.schema_id) IN (N'doc', N'calc', N'aud', N'ext')
      AND t.name IN (N'TableInstance', N'TableRow', N'CellValue',
                     N'CalculationResult', N'CalculationInput', N'CalculationStep',
                     N'CellChange', N'StructureChange', N'SecurityEvent', N'PublicationEvent', N'RowChange',
                     N'RowWindowValue', N'CellStyle', N'CellFormulaOverride', N'CellPermitPart')
      AND ds.type_desc <> N'PARTITION_SCHEME'   -- type_desc, а не type: у type код 'PS'
)
    THROW 50031, N'Частина індексів партиційованих таблиць лишилася поза схемою партиціонування.', 1;
GO

-- ── D-560 / PSS-01: PAGE-стиснення великих таблиць розрахунку — лише там, де це безпечно онлайн ──
--
-- `calc.CalculationStep` (трейс, ~3 КБ на рядок), `calc.CalculationResult`, `calc.CalculationInput` — найбільші таблиці
-- бази (стенд Land: 45,9 / 4,0 / 0,2 ГБ, усе NONE). Скрипт стискає САМ лише ПОРОЖНІ партиції (і таблицю цілком, поки вона
-- порожня, — нова база): перебудова порожньої партиції — метадані, мілісекунди. Нові дані лягають у майбутні періоди,
-- а `SPLIT` (`usp_EnsurePartitions`) успадковує стиснення партиції, яку ділить, — тож усе нове пишеться стисненим.
--
-- ⛔ НЕПОРОЖНІ партиції скрипт НЕ чіпає: перебудова переписує дані (на Standard — офлайн під Sch-M, таблиця недоступна;
-- на стенді — десятки ГБ). Це крок DBA після першого прибирання неактуальних прогонів (runbook §4.2, «Стиснення»):
--   ALTER INDEX ALL ON calc.CalculationStep REBUILD PARTITION = <n> WITH (DATA_COMPRESSION = PAGE, ONLINE = ON);
-- (`ONLINE = ON` — Enterprise/Developer/Azure; на Standard/Express без нього, у вікні). Скрипт лише називає їх у PRINT.
--
-- ⚠ Перебудова бере Sch-M на таблицю: на живій базі з відкритими читачами — `LOCK_TIMEOUT` 30 с і пропуск партиції з
-- PRINT (наступний прогін скрипта повторить), а не зависання кроку оновлення.
SET LOCK_TIMEOUT 30000;

DECLARE @cmp TABLE (TableName sysname, PartitionNumber int, RowCnt bigint);

INSERT @cmp (TableName, PartitionNumber, RowCnt)
SELECT t.name, p.partition_number, MAX(CASE WHEN p.index_id IN (0, 1) THEN p.rows END)
FROM sys.tables AS t
JOIN sys.partitions AS p ON p.object_id = t.object_id
WHERE SCHEMA_NAME(t.schema_id) = N'calc'
  AND t.name IN (N'CalculationResult', N'CalculationStep', N'CalculationInput')
GROUP BY t.name, p.partition_number
HAVING SUM(CASE WHEN p.data_compression_desc <> N'PAGE' THEN 1 ELSE 0 END) > 0;

DECLARE @cTable sysname, @cPartition int, @cSql nvarchar(max), @cDone int = 0, @cSkipped int = 0;

-- Порожня таблиця (нова база): один оператор на таблицю.
DECLARE cmpTables CURSOR LOCAL FAST_FORWARD FOR
    SELECT TableName FROM @cmp GROUP BY TableName HAVING SUM(RowCnt) = 0;
OPEN cmpTables;
FETCH NEXT FROM cmpTables INTO @cTable;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @cSql = N'ALTER INDEX ALL ON calc.' + QUOTENAME(@cTable) + N' REBUILD WITH (DATA_COMPRESSION = PAGE);';
    BEGIN TRY
        EXEC sys.sp_executesql @cSql;
        DELETE @cmp WHERE TableName = @cTable;
        SET @cDone += 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 1222 THROW;
        PRINT N'  D-560: ' + @cTable + N' зайнята (LOCK_TIMEOUT) — стиснення пропущено, повторить наступний прогін 07.';
        DELETE @cmp WHERE TableName = @cTable;
    END CATCH;
    FETCH NEXT FROM cmpTables INTO @cTable;
END
CLOSE cmpTables;
DEALLOCATE cmpTables;

-- Наявна база: лише порожні партиції.
DECLARE cmpParts CURSOR LOCAL FAST_FORWARD FOR
    SELECT TableName, PartitionNumber FROM @cmp WHERE RowCnt = 0 ORDER BY TableName, PartitionNumber;
OPEN cmpParts;
FETCH NEXT FROM cmpParts INTO @cTable, @cPartition;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @cSql = N'ALTER INDEX ALL ON calc.' + QUOTENAME(@cTable) + N' REBUILD PARTITION = '
              + CAST(@cPartition AS nvarchar(10)) + N' WITH (DATA_COMPRESSION = PAGE);';
    BEGIN TRY
        EXEC sys.sp_executesql @cSql;
        SET @cDone += 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() <> 1222 THROW;
        SET @cSkipped += 1;
    END CATCH;
    FETCH NEXT FROM cmpParts INTO @cTable, @cPartition;
END
CLOSE cmpParts;
DEALLOCATE cmpParts;

SET LOCK_TIMEOUT -1;

DECLARE @cPending nvarchar(max) =
    (SELECT STRING_AGG(CONVERT(nvarchar(max), TableName + N'#' + CAST(PartitionNumber AS nvarchar(10))
                       + N' (' + CAST(RowCnt AS nvarchar(20)) + N' rows)'), N', ')
       FROM @cmp WHERE RowCnt > 0);

PRINT N'D-560: стиснуто PAGE (таблиць/партицій): ' + CAST(@cDone AS nvarchar(10))
    + N'; пропущено через блокування: ' + CAST(@cSkipped AS nvarchar(10)) + N'.';
IF @cPending IS NOT NULL
    PRINT N'D-560: непорожні нестиснені партиції calc.* — крок DBA (runbook §4.2, «Стиснення»): ' + @cPending;
GO
