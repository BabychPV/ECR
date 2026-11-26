/*
  b14 document-move (D-579, design M6/B3): pre-image of the documents to move, BEFORE Move-Document -Mode Apply.
  Creates the side database @PiDatabase (EcrMovePre_<CorrelationId>) on the same instance and copies, for the listed
  documents, everything Reverse-Move.sql needs to put them back byte-equal:
    doc.Document, doc.TableInstance, doc.TableRow, doc.CellValue, doc.CellStyle (all periods),
    calc.CalculationRun (all statuses) + calc.CalculationResult / Step / Input / Issue of those runs,
    calc.FormulaError, doc.DocumentPeriodReopen, wf.ApprovalState, wf.ApprovalEvent of the documents,
    and dbo.Manifest (source database, documents, counts, cell checksum per document).
  Refuses when the side database already exists (one pre-image per CorrelationId; never overwritten).
  Reads the source only (no writes to the ECR database). Run ONLY through Move-Document.ps1 -Mode PreImage.
  ASCII only on purpose.
*/
-- BEGIN PARAMS
DECLARE @PiDatabase      sysname       = NULL;   -- EcrMovePre_<CorrelationId>
DECLARE @PiDocuments     nvarchar(400) = NULL;   -- '98,93'
DECLARE @PiCorrelationId nvarchar(64)  = NULL;
DECLARE @PiDataPath      nvarchar(400) = NULL;   -- folder for the side database files; NULL = instance default
-- END PARAMS

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF @PiDatabase IS NULL OR @PiDatabase NOT LIKE N'EcrMovePre[_]%' OR @PiDatabase LIKE N'%[^A-Za-z0-9_-]%'
    THROW 51820, 'PreImage: @PiDatabase must be EcrMovePre_<CorrelationId> ([A-Za-z0-9_-]).', 1;
IF DB_NAME() IN (N'ECR', N'FLERT', N'EcrLegacyProd') THROW 51821, 'PreImage: refusing this database.', 1;
IF DB_ID(@PiDatabase) IS NOT NULL THROW 51822, 'PreImage: the side database already exists (one pre-image per CorrelationId).', 1;
IF @PiDocuments IS NULL OR @PiDocuments LIKE N'%[^0-9,]%' THROW 51823, 'PreImage: @PiDocuments must be a comma list of ids.', 1;

DECLARE @Docs TABLE (DocumentId bigint PRIMARY KEY);
INSERT @Docs SELECT DISTINCT CAST(value AS bigint) FROM STRING_SPLIT(@PiDocuments, N',') WHERE value <> N'';
IF EXISTS (SELECT 1 FROM @Docs d WHERE NOT EXISTS (SELECT 1 FROM doc.Document x WHERE x.Id = d.DocumentId))
    THROW 51824, 'PreImage: a listed document does not exist.', 1;

DECLARE @create nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@PiDatabase)
    + CASE WHEN @PiDataPath IS NULL THEN N''
           ELSE N' ON (NAME = ' + QUOTENAME(@PiDatabase + N'_data') + N', FILENAME = N''' + REPLACE(@PiDataPath, N'''', N'''''')
                + N'\' + @PiDatabase + N'.mdf'') LOG ON (NAME = ' + QUOTENAME(@PiDatabase + N'_log') + N', FILENAME = N'''
                + REPLACE(@PiDataPath, N'''', N'''''') + N'\' + @PiDatabase + N'_log.ldf'')' END;
EXEC (@create);
SET @create = N'ALTER DATABASE ' + QUOTENAME(@PiDatabase) + N' SET RECOVERY SIMPLE';
EXEC (@create);

DECLARE @list nvarchar(400) = (SELECT STRING_AGG(CAST(DocumentId AS nvarchar(20)), N',') FROM @Docs);
DECLARE @t TABLE (Ord int IDENTITY, Src nvarchar(128), Dst nvarchar(128), Filter nvarchar(max));
INSERT @t (Src, Dst, Filter) VALUES
    (N'doc.Document',             N'doc_Document',             N'Id IN (' + @list + N')'),
    (N'doc.TableInstance',        N'doc_TableInstance',        N'DocumentId IN (' + @list + N')'),
    (N'doc.TableRow',             N'doc_TableRow',             N'EXISTS (SELECT 1 FROM doc.TableInstance i WHERE i.PeriodKey = s.PeriodKey AND i.Id = s.TableInstanceId AND i.DocumentId IN (' + @list + N'))'),
    (N'doc.CellValue',            N'doc_CellValue',            N'EXISTS (SELECT 1 FROM doc.TableRow r JOIN doc.TableInstance i ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId WHERE r.PeriodKey = s.PeriodKey AND r.Id = s.TableRowId AND i.DocumentId IN (' + @list + N'))'),
    (N'doc.CellStyle',            N'doc_CellStyle',            N'EXISTS (SELECT 1 FROM doc.TableRow r JOIN doc.TableInstance i ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId WHERE r.PeriodKey = s.PeriodKey AND r.Id = s.TableRowId AND i.DocumentId IN (' + @list + N'))'),
    (N'calc.CalculationRun',      N'calc_CalculationRun',      N'DocumentId IN (' + @list + N')'),
    (N'calc.CalculationResult',   N'calc_CalculationResult',   N'CalculationRunId IN (SELECT Id FROM calc.CalculationRun WHERE DocumentId IN (' + @list + N'))'),
    (N'calc.CalculationStep',     N'calc_CalculationStep',     N'CalculationRunId IN (SELECT Id FROM calc.CalculationRun WHERE DocumentId IN (' + @list + N'))'),
    (N'calc.CalculationInput',    N'calc_CalculationInput',    N'CalculationRunId IN (SELECT Id FROM calc.CalculationRun WHERE DocumentId IN (' + @list + N'))'),
    (N'calc.CalculationIssue',    N'calc_CalculationIssue',    N'CalculationRunId IN (SELECT Id FROM calc.CalculationRun WHERE DocumentId IN (' + @list + N'))'),
    (N'calc.FormulaError',        N'calc_FormulaError',        N'DocumentId IN (' + @list + N')'),
    (N'doc.DocumentPeriodReopen', N'doc_DocumentPeriodReopen', N'DocumentId IN (' + @list + N')'),
    (N'wf.ApprovalState',         N'wf_ApprovalState',         N'DocumentId IN (' + @list + N')'),
    (N'wf.ApprovalEvent',         N'wf_ApprovalEvent',         N'DocumentId IN (' + @list + N')'),
    -- review M-7: row-keyed manual values are restored by Reverse like cells
    (N'doc.CellFormulaOverride',  N'doc_CellFormulaOverride',  N'EXISTS (SELECT 1 FROM doc.TableInstance i WHERE i.PeriodKey = s.PeriodKey AND i.Id = s.TableInstanceId AND i.DocumentId IN (' + @list + N'))');

-- consistency: the window stops the worker and users first (W-2), so plain READ COMMITTED copies are stable;
-- a cross-database SNAPSHOT transaction is not used (the side database does not allow snapshot isolation).
DECLARE @ord int = 1, @max int = (SELECT MAX(Ord) FROM @t), @sql nvarchar(max), @src nvarchar(128), @dst nvarchar(128), @filter nvarchar(max), @cols nvarchar(max);
WHILE @ord <= @max
BEGIN
    SELECT @src = Src, @dst = Dst, @filter = Filter FROM @t WHERE Ord = @ord;
    -- rowversion columns are not copied (a restore cannot insert them; nothing reads them back)
    SELECT @cols = STRING_AGG(CAST(N's.' + QUOTENAME(c.name) AS nvarchar(max)), N', ') WITHIN GROUP (ORDER BY c.column_id)
    FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id
    WHERE c.object_id = OBJECT_ID(@src) AND ty.name <> N'timestamp';
    SET @sql = N'SELECT ' + @cols + N' INTO ' + QUOTENAME(@PiDatabase) + N'.dbo.' + QUOTENAME(@dst) + N' FROM ' + @src + N' AS s WHERE ' + @filter + N';';
    EXEC (@sql);
    SET @ord += 1;
END;

SET @sql = N'SELECT d.DocumentId, SourceDatabase = DB_NAME(), CorrelationId = @c, CreatedAt = SYSUTCDATETIME(),
       ProjectId = doc.ProjectId,
       Cells = (SELECT COUNT(*) FROM doc.CellValue cv JOIN doc.TableRow r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId
                JOIN doc.TableInstance i ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId WHERE i.DocumentId = d.DocumentId),
       CellChecksum = (SELECT ISNULL(SUM(CAST(CHECKSUM(cv.PeriodKey, cv.TableRowId, cv.ColumnDefId, cv.ValueString, cv.ValueNumeric, cv.ValueDate,
                                cv.ValueBool, cv.ValueRegistryEntryId, cv.ValueUnitId, cv.IsCalculated, cv.IsEmpty) AS bigint)), 0)
                FROM doc.CellValue cv JOIN doc.TableRow r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId
                JOIN doc.TableInstance i ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId WHERE i.DocumentId = d.DocumentId),
       Runs = (SELECT COUNT(*) FROM calc.CalculationRun WHERE DocumentId = d.DocumentId),
       Results = (SELECT COUNT(*) FROM calc.CalculationResult x JOIN calc.CalculationRun r ON r.Id = x.CalculationRunId WHERE r.DocumentId = d.DocumentId),
       RunIds = (SELECT STRING_AGG(CAST(Id AS nvarchar(max)), N'','') WITHIN GROUP (ORDER BY Id) FROM calc.CalculationRun WHERE DocumentId = d.DocumentId),
       -- review M-7: side tables Reverse does NOT restore - it refuses when any of these counts changed since the pre-image
       SideRows = CONCAT(
           (SELECT COUNT(*) FROM doc.RowLock WHERE DocumentId = d.DocumentId), N''|'',
           (SELECT COUNT(*) FROM doc.EditAssignment WHERE DocumentId = d.DocumentId), N''|'',
           (SELECT COUNT(*) FROM doc.CellPermitPart WHERE DocumentId = d.DocumentId), N''|'',
           (SELECT COUNT(*) FROM doc.PermitSegment WHERE DocumentId = d.DocumentId), N''|'',
           (SELECT COUNT(*) FROM doc.TableDiscussion WHERE DocumentId = d.DocumentId), N''|'',
           (SELECT COUNT(*) FROM ext.SourceEventLink l JOIN doc.TableInstance i ON i.PeriodKey = l.PeriodKey AND i.Id = l.TableInstanceId WHERE i.DocumentId = d.DocumentId), N''|'',
           (SELECT COUNT(*) FROM ext.RowWindowValue w JOIN doc.TableInstance i ON i.PeriodKey = w.PeriodKey AND i.Id = w.TableInstanceId WHERE i.DocumentId = d.DocumentId), N''|'',
           (SELECT COUNT(*) FROM wf.ApprovalState WHERE DocumentId = d.DocumentId), N''|'',
           (SELECT COUNT(*) FROM wf.ApprovalEvent WHERE DocumentId = d.DocumentId))
INTO ' + QUOTENAME(@PiDatabase) + N'.dbo.Manifest
FROM (SELECT CAST(value AS bigint) AS DocumentId FROM STRING_SPLIT(@l, N'','')) d
JOIN doc.Document doc ON doc.Id = d.DocumentId;';
EXEC sp_executesql @sql, N'@c nvarchar(64), @l nvarchar(400)', @c = @PiCorrelationId, @l = @list;

SET @sql = N'SELECT PreImage = @db, * FROM ' + QUOTENAME(@PiDatabase) + N'.dbo.Manifest ORDER BY DocumentId;';
EXEC sp_executesql @sql, N'@db sysname', @db = @PiDatabase;
