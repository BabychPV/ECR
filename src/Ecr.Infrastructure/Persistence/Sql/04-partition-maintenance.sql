-- src/Ecr.Infrastructure/Persistence/Sql/04-partition-maintenance.sql
-- Працює НА ВИПЕРЕДЖЕННЯ: SPLIT порожньої останньої партиції — операція
-- метаданих; SPLIT непорожньої переміщує дані з блокуванням.
CREATE OR ALTER PROCEDURE arc.usp_EnsurePartitions
    @MonthsAhead int = 6
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @d date = DATEADD(MONTH, 1, GETUTCDATE());
    DECLARE @i int = 0, @key int, @sql nvarchar(400);

    WHILE @i < @MonthsAhead
    BEGIN
        SET @key = YEAR(@d) * 100 + MONTH(@d);

        IF NOT EXISTS (SELECT 1 FROM sys.partition_range_values rv
                       JOIN sys.partition_functions pf ON pf.function_id = rv.function_id
                       WHERE pf.name = N'pf_ByPeriodKey' AND CAST(rv.value AS int) = @key)
        BEGIN
            ALTER PARTITION SCHEME ps_ByPeriodKey NEXT USED [DATA_HOT];
            SET @sql = N'ALTER PARTITION FUNCTION pf_ByPeriodKey() SPLIT RANGE (' + CAST(@key AS nvarchar(10)) + N');';
            EXEC sp_executesql @sql;
        END

        SET @d = DATEADD(MONTH, 1, @d);
        SET @i = @i + 1;
    END

    INSERT INTO itg.MaintenanceRun (JobCode, StartedAt, FinishedAt, Status)
    VALUES (N'EnsurePartitions', SYSUTCDATETIME(), SYSUTCDATETIME(), N'Completed');
END;
GO
