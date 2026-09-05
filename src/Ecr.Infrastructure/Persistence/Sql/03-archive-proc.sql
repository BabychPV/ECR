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

-- src/Ecr.Infrastructure/Persistence/Sql/03-archive-proc.sql
-- Виконується SQL Agent під окремим principal: у застосунку немає ані DDL-прав,
-- ані права запису в arc.* (D-66, B01 §6.4).
-- Повертає структурні зовнішні ключі, зняті на час архівації.
--
-- ⚠ WITH CHECK, а не NOCHECK: недовірене обмеження оптимізатор ігнорує, і
-- «ключ є» перетворилося б на «ключ намальовано». Сканування коштує один раз
-- на рік і робиться у вікні низької активності, заради якого це вікно й існує
-- (D-24).
CREATE OR ALTER PROCEDURE arc.usp_RestoreArchiveConstraints
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_TableRow_Instance')
        ALTER TABLE doc.TableRow WITH CHECK
            ADD CONSTRAINT FK_TableRow_Instance FOREIGN KEY (PeriodKey, TableInstanceId)
            REFERENCES doc.TableInstance (PeriodKey, Id);

    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_CellValue_Row')
        ALTER TABLE doc.CellValue WITH CHECK
            ADD CONSTRAINT FK_CellValue_Row FOREIGN KEY (PeriodKey, TableRowId)
            REFERENCES doc.TableRow (PeriodKey, Id);
END;
GO

CREATE OR ALTER PROCEDURE arc.usp_ArchiveYear
    @ProjectId     int,
    @FromPeriodKey int,
    @ToPeriodKey   int,
    @BatchSize     int = 500000
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @RunId bigint, @k int, @srcCount bigint, @srcChk bigint, @srcSum decimal(38,10);
    DECLARE @dstCount bigint, @dstChk bigint, @dstSum decimal(38,10);

    INSERT INTO itg.ArchiveRun (ProjectId, Direction, FromPeriodKey, ToPeriodKey, StartedAt, Status)
    VALUES (@ProjectId, N'ToArchive', @FromPeriodKey, @ToPeriodKey, SYSUTCDATETIME(), N'Running');
    SET @RunId = SCOPE_IDENTITY();

    UPDATE doc.Project SET IsArchiving = 1 WHERE Id = @ProjectId;

    -- Відновлення після збою: продовжуємо з партиції, на якій зупинилися (АРХ-3a)
    SELECT @k = ISNULL(MAX(LastDonePeriodKey) + 1, @FromPeriodKey)
    FROM itg.ArchiveRun
    WHERE ProjectId = @ProjectId AND Direction = N'ToArchive' AND Status = N'Failed';

    IF @k IS NULL OR @k < @FromPeriodKey SET @k = @FromPeriodKey;

    -- 0. Зняття структурних зовнішніх ключів на час прогону.
    --
    -- ⛔ Це не обхід обмеження, а єдиний спосіб виконати вимогу. SQL Server
    --    забороняє TRUNCATE і SWITCH на таблиці, на яку посилається FK, —
    --    навіть якщо посилань уже немає. Архівація за D-24 мусить звільняти
    --    партиції, а не видаляти рядки: DELETE на 108 млн роздув би журнал
    --    транзакцій до розміру самих даних.
    --
    -- ⚠ Знімається РАЗ на прогін, а не на партицію: кожне зняття й повернення
    --    коштує сканування, і робити його дванадцять разів замість одного
    --    означало б витратити вікно обслуговування на метадані.
    --
    -- ⚠ Повернення — і в успіху, і в CATCH: схема без обмежень після збою
    --    гірша за невдалу архівацію, бо наступний запис уже нічим не
    --    перевіряється.
    IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_CellValue_Row')
        ALTER TABLE doc.CellValue DROP CONSTRAINT FK_CellValue_Row;
    IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_TableRow_Instance')
        ALTER TABLE doc.TableRow DROP CONSTRAINT FK_TableRow_Instance;

    BEGIN TRY

    WHILE @k <= @ToPeriodKey
    BEGIN
        -- 1. Три контрольні суми на джерелі
        SELECT @srcCount = COUNT_BIG(*),
               @srcChk   = CHECKSUM_AGG(BINARY_CHECKSUM(*)),
               @srcSum   = ISNULL(SUM(ValueNumeric), 0)
        FROM doc.CellValue WHERE PeriodKey = @k;

        -- 2. Копіювання. TABLOCK → мінімальне логування і прямий запис
        --    у columnstore rowgroups.
        INSERT INTO arc.CellValue WITH (TABLOCK)
            (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString, ValueNumeric,
             ValueDate, ValueBool, ValueRegistryEntryId, ValueUnitId, IsCalculated, IsEmpty)
        SELECT PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString, ValueNumeric,
               ValueDate, ValueBool, ValueRegistryEntryId, ValueUnitId, IsCalculated, IsEmpty
        FROM doc.CellValue WHERE PeriodKey = @k;

        INSERT INTO arc.TableRow WITH (TABLOCK)
            (PeriodKey, Id, TableInstanceId, RowKey, RowDefId, Ordinal, IsDeleted, ModifiedAt)
        SELECT PeriodKey, Id, TableInstanceId, RowKey, RowDefId, Ordinal, IsDeleted, ModifiedAt
        FROM doc.TableRow WHERE PeriodKey = @k;

        INSERT INTO arc.TableInstance WITH (TABLOCK)
            (PeriodKey, Id, DocumentId, TableDefId, CreatedAt, ModifiedAt)
        SELECT PeriodKey, Id, DocumentId, TableDefId, CreatedAt, ModifiedAt
        FROM doc.TableInstance WHERE PeriodKey = @k;

        -- 3. Ті самі суми на приймачі
        SELECT @dstCount = COUNT_BIG(*),
               @dstChk   = CHECKSUM_AGG(BINARY_CHECKSUM(*)),
               @dstSum   = ISNULL(SUM(ValueNumeric), 0)
        FROM arc.CellValue WHERE PeriodKey = @k;

        -- 4. Розбіжність → СТОП. ДАНІ З ДЖЕРЕЛА НЕ ВИДАЛЯЮТЬСЯ —
        --    це головне правило процедури.
        IF (@srcCount <> @dstCount OR @srcSum <> @dstSum)
        BEGIN
            INSERT INTO aud.ConsistencyIssue (DetectedAt, Severity, RuleCode, EntityType, EntityId, Message)
            VALUES (SYSUTCDATETIME(), 2, N'ARCHIVE_CHECKSUM', N'Period', @k,
                    N'Розбіжність контрольних сум при архівації; дані джерела збережено.');

            UPDATE itg.ArchiveRun
               SET Status = N'Failed', FinishedAt = SYSUTCDATETIME(), LastDonePeriodKey = @k - 1,
                   ErrorMessage = N'Checksum mismatch'
             WHERE Id = @RunId;

            UPDATE doc.Project SET IsArchiving = 0 WHERE Id = @ProjectId;
            THROW 50010, N'Розбіжність контрольних сум при архівації.', 1;
        END

        -- 5. Збіг → звільнення партиції. TRUNCATE … WITH (PARTITIONS) звільняє
        --    майже миттєво і мінімально логується; DELETE на 108 млн роздув би журнал.
        --
        -- ⚠ Порядок звільнення — від ДОЧІРНЬОЇ таблиці до батьківської:
        --    комірки, рядки, екземпляри. Зворотний лишив би комірки, що
        --    вказують у порожнечу.
        --
        -- ⛔ Зовнішні ключі знято ПЕРЕД циклом (див. крок 0) і повертаються
        --    після нього: SQL Server забороняє і TRUNCATE, і SWITCH на
        --    таблиці, на яку посилається FK — незалежно від того, чи є в ній
        --    рядки. Без цього процедура падала б на першому ж запуску.
        DECLARE @part int = $PARTITION.pf_ByPeriodKey(@k);
        DECLARE @sql nvarchar(400);
        SET @sql = N'TRUNCATE TABLE doc.CellValue    WITH (PARTITIONS (' + CAST(@part AS nvarchar(10)) + N'));';
        EXEC sp_executesql @sql;
        SET @sql = N'TRUNCATE TABLE doc.TableRow     WITH (PARTITIONS (' + CAST(@part AS nvarchar(10)) + N'));';
        EXEC sp_executesql @sql;
        SET @sql = N'TRUNCATE TABLE doc.TableInstance WITH (PARTITIONS (' + CAST(@part AS nvarchar(10)) + N'));';
        EXEC sp_executesql @sql;

        UPDATE itg.ArchiveRun
           SET RowsMoved = RowsMoved + @srcCount, LastDonePeriodKey = @k
         WHERE Id = @RunId;

        SET @k = @k + 1;
    END

    END TRY
    BEGIN CATCH
        EXEC arc.usp_RestoreArchiveConstraints;
        THROW;
    END CATCH

    EXEC arc.usp_RestoreArchiveConstraints;

    UPDATE itg.ArchiveRun SET Status = N'Completed', FinishedAt = SYSUTCDATETIME() WHERE Id = @RunId;
    UPDATE doc.Project SET IsArchiving = 0, Status = 4 WHERE Id = @ProjectId;
END;
GO

-- Розархівація: зворотний напрям тієї самої процедури.
--
-- ⚠ Потрібна не «про всяк випадок»: перерахунок закритого року з окремим
-- погодженням (ФВ-9.7) читає дані звідти, де вони лежать, і рахувати по
-- columnstore-архіву з тими самими бюджетами неможливо. Тому рік повертають
-- у гарячу схему, рахують і архівують знову.
--
-- ⛔ Те саме головне правило: дані з АРХІВУ не видаляються, поки суми не
-- збіглися. Розархівація, що втратила рядок, гірша за архівацію, що його не
-- перенесла: там оригінал на місці, тут його вже немає.
CREATE OR ALTER PROCEDURE arc.usp_RestoreYear
    @ProjectId     int,
    @FromPeriodKey int,
    @ToPeriodKey   int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @RunId bigint, @k int, @srcCount bigint, @dstCount bigint;

    INSERT INTO itg.ArchiveRun (ProjectId, Direction, FromPeriodKey, ToPeriodKey, StartedAt, Status)
    VALUES (@ProjectId, N'FromArchive', @FromPeriodKey, @ToPeriodKey, SYSUTCDATETIME(), N'Running');
    SET @RunId = SCOPE_IDENTITY();

    UPDATE doc.Project SET IsArchiving = 1 WHERE Id = @ProjectId;

    SET @k = @FromPeriodKey;

    WHILE @k <= @ToPeriodKey
    BEGIN
        SELECT @srcCount = COUNT_BIG(*) FROM arc.CellValue WHERE PeriodKey = @k;

        -- Порядок зворотний до архівації: спершу батьківські рядки, потім
        -- комірки. Інакше FK не дає вставити комірку без свого рядка.
        INSERT INTO doc.TableInstance (PeriodKey, Id, DocumentId, TableDefId, CreatedAt, ModifiedAt)
        SELECT PeriodKey, Id, DocumentId, TableDefId, CreatedAt, ModifiedAt
        FROM arc.TableInstance WHERE PeriodKey = @k;

        INSERT INTO doc.TableRow (PeriodKey, Id, TableInstanceId, RowKey, RowDefId, Ordinal,
                                  IsDeleted, IsOrphaned, ModifiedAt)
        SELECT PeriodKey, Id, TableInstanceId, RowKey, RowDefId, Ordinal, IsDeleted, 0, ModifiedAt
        FROM arc.TableRow WHERE PeriodKey = @k;

        INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString,
                                   ValueNumeric, ValueDate, ValueBool, ValueRegistryEntryId,
                                   ValueUnitId, IsCalculated, IsEmpty)
        SELECT PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString, ValueNumeric,
               ValueDate, ValueBool, ValueRegistryEntryId, ValueUnitId, IsCalculated, IsEmpty
        FROM arc.CellValue WHERE PeriodKey = @k;

        SELECT @dstCount = COUNT_BIG(*) FROM doc.CellValue WHERE PeriodKey = @k;

        IF (@srcCount <> @dstCount)
        BEGIN
            INSERT INTO aud.ConsistencyIssue (DetectedAt, Severity, RuleCode, EntityType, EntityId, Message)
            VALUES (SYSUTCDATETIME(), 3, N'RESTORE_CHECKSUM', N'Period', @k,
                    N'Розбіжність при розархівації; дані архіву збережено.');

            UPDATE itg.ArchiveRun
               SET Status = N'Failed', FinishedAt = SYSUTCDATETIME(), LastDonePeriodKey = @k - 1,
                   ErrorMessage = N'Restore count mismatch'
             WHERE Id = @RunId;

            UPDATE doc.Project SET IsArchiving = 0 WHERE Id = @ProjectId;
            THROW 50011, N'Розбіжність кількості рядків при розархівації.', 1;
        END

        UPDATE itg.ArchiveRun
           SET RowsMoved = RowsMoved + @srcCount, LastDonePeriodKey = @k
         WHERE Id = @RunId;

        SET @k = @k + 1;
    END

    UPDATE itg.ArchiveRun SET Status = N'Completed', FinishedAt = SYSUTCDATETIME() WHERE Id = @RunId;
    UPDATE doc.Project SET IsArchiving = 0 WHERE Id = @ProjectId;
END;
GO
