-- src/Ecr.Infrastructure/Persistence/Sql/03-archive-proc.sql
-- Виконується SQL Agent під окремим principal: у застосунку немає ані DDL-прав,
-- ані права запису в arc.* (D-66, B01 §6.4).
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

    UPDATE itg.ArchiveRun SET Status = N'Completed', FinishedAt = SYSUTCDATETIME() WHERE Id = @RunId;
    UPDATE doc.Project SET IsArchiving = 0, Status = 4 WHERE Id = @ProjectId;
END;
GO
