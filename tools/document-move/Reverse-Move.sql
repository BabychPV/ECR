/*
  b14 document-move (D-577, design M6 + review B3): put ONE moved document back exactly as in its pre-image
  (Export-PreImage.sql, side database EcrMovePre_<CorrelationId>). Possible only while the source project exists
  (before the purge, window step W-10). Run ONLY through Move-Document.ps1 -Mode Reverse. One transaction:

    1. calc.FormulaError of the document -> deleted (pre-image rows re-inserted in step 5);
    2. every run of the document that is NOT in the pre-image (W-5 recalculations, the first nightly) is deleted with
       its results, steps, inputs and issues; pre-image runs that D-560 deleted are re-inserted with their ORIGINAL ids
       (IDENTITY_INSERT) and children; surviving pre-image runs get their pre-image ProjectId and Status back;
    3. instances / rows / cells / styles created after the move (owner instances 202602..202611, mirror rows) are
       deleted; cells and styles of the pre-image rows are replaced by the pre-image; row attributes restored;
    4. doc.DocumentPeriodReopen rows created after the move are deleted (wf.ApprovalState / ApprovalEvent are NOT
       touched - reported as approvalsSinceMove; the window has none for 93/98);
    5. pre-image calc.FormulaError re-inserted; doc.Document ProjectId / TemplateId back to the pre-image;
    6. checks against dbo.Manifest of the pre-image (cells, cell checksum, runs, results; projectMismatch 0) - any
       difference aborts; aud.SecurityEvent DocumentMoveReversed; report;
    -> COMMIT (Apply) | ROLLBACK (Preview, always).
  aud.* rows written by the move stay (immutable journal). Land 96/100 runs superseded after the move are NOT in the
  pre-image: after Reverse recalculate Land 96/100 for 202610/202611 and compare with the baseline (README, R-4).
  ASCII only on purpose.
*/
-- BEGIN PARAMS
DECLARE @RvMode          varchar(10)  = 'Preview';   -- Preview | Apply
DECLARE @RvDocumentId    bigint       = NULL;
DECLARE @RvPreImage      sysname      = NULL;        -- EcrMovePre_<CorrelationId>
DECLARE @RvCorrelationId nvarchar(64) = NULL;
DECLARE @RvUserId        int          = NULL;
DECLARE @RvAllowLive     bit          = 0;
DECLARE @RvApplyToken    varchar(40)  = NULL;
-- END PARAMS

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 60000;

IF @RvMode NOT IN ('Preview', 'Apply') THROW 51830, 'Reverse: @RvMode must be Preview or Apply.', 1;
IF DB_NAME() IN (N'ECR', N'FLERT', N'EcrLegacyProd') THROW 51831, 'Reverse: refusing this database.', 1;
IF ISNULL(@RvApplyToken, '') <> 'MOVE-APPLY-v1' THROW 51832, 'Reverse: runs only through Move-Document.ps1.', 1;
IF @RvMode = 'Apply' AND DB_NAME() = N'EcrLandUi' AND @RvAllowLive = 0
    THROW 51833, 'Reverse: refusing to write into the live EcrLandUi without -AllowLive.', 1;
IF @RvDocumentId IS NULL OR @RvUserId IS NULL OR @RvCorrelationId IS NULL
    THROW 51834, 'Reverse: @RvDocumentId, @RvUserId and @RvCorrelationId are required.', 1;
IF @RvPreImage IS NULL OR @RvPreImage NOT LIKE N'EcrMovePre[_]%' OR @RvPreImage LIKE N'%[^A-Za-z0-9_-]%' OR DB_ID(@RvPreImage) IS NULL
    THROW 51835, 'Reverse: pre-image database not found.', 1;

DECLARE @P nvarchar(300) = QUOTENAME(@RvPreImage) + N'.dbo.';
DECLARE @sql nvarchar(max), @n int, @RvProject int, @RvTemplate int, @RvCells int, @RvSum bigint, @RvRuns int, @RvResults int,
        @RvMsg nvarchar(2000), @RvDeletedRuns int = 0, @RvRestoredRuns int = 0, @RvDeletedInstances int = 0,
        @RvDeletedRows int = 0, @RvDeletedReopens int = 0, @RvApprovals int = 0;

SET @sql = N'SELECT @p = d.ProjectId, @t = d.TemplateId FROM ' + @P + N'doc_Document d WHERE d.Id = @d;
             SELECT @c = m.Cells, @s = m.CellChecksum, @r = m.Runs, @x = m.Results FROM ' + @P + N'Manifest m WHERE m.DocumentId = @d;';
EXEC sp_executesql @sql, N'@d bigint, @p int OUTPUT, @t int OUTPUT, @c int OUTPUT, @s bigint OUTPUT, @r int OUTPUT, @x int OUTPUT',
     @d = @RvDocumentId, @p = @RvProject OUTPUT, @t = @RvTemplate OUTPUT, @c = @RvCells OUTPUT, @s = @RvSum OUTPUT,
     @r = @RvRuns OUTPUT, @x = @RvResults OUTPUT;
IF @RvProject IS NULL OR @RvCells IS NULL THROW 51836, 'Reverse: the document is not in this pre-image.', 1;
IF NOT EXISTS (SELECT 1 FROM doc.Project WHERE Id = @RvProject)
    THROW 51837, 'Reverse: the source project no longer exists (purged) - only a full RESTORE can undo the move now.', 1;

CREATE TABLE #RvRun (Id bigint PRIMARY KEY);
CREATE TABLE #RvInst (PeriodKey int NOT NULL, Id bigint NOT NULL, PRIMARY KEY (PeriodKey, Id));
CREATE TABLE #RvRow (PeriodKey int NOT NULL, Id bigint NOT NULL, PRIMARY KEY (PeriodKey, Id));
CREATE TABLE #RvReopen (Id int PRIMARY KEY);
SET @sql = N'INSERT #RvRun SELECT Id FROM ' + @P + N'calc_CalculationRun WHERE DocumentId = @d;
             INSERT #RvInst SELECT PeriodKey, Id FROM ' + @P + N'doc_TableInstance WHERE DocumentId = @d;
             INSERT #RvRow SELECT r.PeriodKey, r.Id FROM ' + @P + N'doc_TableRow r JOIN ' + @P + N'doc_TableInstance i
                    ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId WHERE i.DocumentId = @d;
             INSERT #RvReopen SELECT Id FROM ' + @P + N'doc_DocumentPeriodReopen WHERE DocumentId = @d;';
EXEC sp_executesql @sql, N'@d bigint', @d = @RvDocumentId;

BEGIN TRAN;
SELECT @n = NULL FROM doc.Document WITH (UPDLOCK, HOLDLOCK) WHERE Id = @RvDocumentId;

-- 1. formula errors of the document (re-inserted from the pre-image in step 5)
DELETE FROM calc.FormulaError WHERE DocumentId = @RvDocumentId;

-- 2. runs: post-move runs out, pre-image runs back (original ids)
DECLARE @Post TABLE (Id bigint PRIMARY KEY);
INSERT @Post SELECT r.Id FROM calc.CalculationRun r WHERE r.DocumentId = @RvDocumentId AND NOT EXISTS (SELECT 1 FROM #RvRun x WHERE x.Id = r.Id);
DELETE FROM calc.CalculationStep WHERE CalculationRunId IN (SELECT Id FROM @Post);
DELETE FROM calc.CalculationInput WHERE CalculationRunId IN (SELECT Id FROM @Post);
DELETE FROM calc.CalculationIssue WHERE CalculationRunId IN (SELECT Id FROM @Post);
DELETE FROM calc.CalculationResult WHERE CalculationRunId IN (SELECT Id FROM @Post);
DELETE FROM calc.CalculationRun WHERE Id IN (SELECT Id FROM @Post);
SET @RvDeletedRuns = @@ROWCOUNT;

SET @sql = N'UPDATE r SET ProjectId = p.ProjectId, Status = p.Status FROM calc.CalculationRun r JOIN ' + @P + N'calc_CalculationRun p ON p.Id = r.Id WHERE p.DocumentId = @d;';
EXEC sp_executesql @sql, N'@d bigint', @d = @RvDocumentId;

-- generic "insert what is missing" from the pre-image: (target, pre-image table, key join, identity)
DECLARE @Plan TABLE (Ord int IDENTITY, Tgt nvarchar(128), Pre nvarchar(128), KeyJoin nvarchar(400), HasIdentity bit);
INSERT @Plan (Tgt, Pre, KeyJoin, HasIdentity) VALUES
    (N'calc.CalculationRun',    N'calc_CalculationRun',    N't.Id = p.Id', 1),
    (N'calc.CalculationResult', N'calc_CalculationResult', N't.PeriodKey = p.PeriodKey AND t.Id = p.Id', 0),
    (N'calc.CalculationStep',   N'calc_CalculationStep',   N't.PeriodKey = p.PeriodKey AND t.Id = p.Id', 0),
    (N'calc.CalculationInput',  N'calc_CalculationInput',  N't.PeriodKey = p.PeriodKey AND t.Id = p.Id', 0),
    (N'calc.CalculationIssue',  N'calc_CalculationIssue',  N't.Id = p.Id', 1);

DECLARE @ord int = 1, @max int, @tgt nvarchar(128), @pre nvarchar(128), @key nvarchar(400), @ident bit, @cols nvarchar(max);

-- 3. cells: instances and rows created after the move out; pre-image rows get their cells and styles back
DECLARE @PostInst TABLE (PeriodKey int, Id bigint, PRIMARY KEY (PeriodKey, Id));
INSERT @PostInst SELECT i.PeriodKey, i.Id FROM doc.TableInstance i
WHERE i.DocumentId = @RvDocumentId AND NOT EXISTS (SELECT 1 FROM #RvInst x WHERE x.PeriodKey = i.PeriodKey AND x.Id = i.Id);
DECLARE @PostRow TABLE (PeriodKey int, Id bigint, PRIMARY KEY (PeriodKey, Id));
INSERT @PostRow SELECT r.PeriodKey, r.Id FROM doc.TableRow r JOIN doc.TableInstance i ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId
WHERE i.DocumentId = @RvDocumentId AND NOT EXISTS (SELECT 1 FROM #RvRow x WHERE x.PeriodKey = r.PeriodKey AND x.Id = r.Id);

DELETE cs FROM doc.CellStyle cs JOIN @PostRow x ON x.PeriodKey = cs.PeriodKey AND x.Id = cs.TableRowId;
DELETE cv FROM doc.CellValue cv JOIN @PostRow x ON x.PeriodKey = cv.PeriodKey AND x.Id = cv.TableRowId;
DELETE cs FROM doc.CellStyle cs JOIN #RvRow x ON x.PeriodKey = cs.PeriodKey AND x.Id = cs.TableRowId;
DELETE cv FROM doc.CellValue cv JOIN #RvRow x ON x.PeriodKey = cv.PeriodKey AND x.Id = cv.TableRowId;
DELETE r FROM doc.TableRow r JOIN @PostRow x ON x.PeriodKey = r.PeriodKey AND x.Id = r.Id;
SET @RvDeletedRows = @@ROWCOUNT;
DELETE i FROM doc.TableInstance i JOIN @PostInst x ON x.PeriodKey = i.PeriodKey AND x.Id = i.Id;
SET @RvDeletedInstances = @@ROWCOUNT;

SET @sql = N'UPDATE r SET RowKey = p.RowKey, RowDefId = p.RowDefId, Ordinal = p.Ordinal, IsDeleted = p.IsDeleted, IsOrphaned = p.IsOrphaned,
                    OrphanedAt = p.OrphanedAt, ModifiedAt = p.ModifiedAt
             FROM doc.TableRow r JOIN ' + @P + N'doc_TableRow p ON p.PeriodKey = r.PeriodKey AND p.Id = r.Id
             WHERE EXISTS (SELECT 1 FROM #RvRow x WHERE x.PeriodKey = p.PeriodKey AND x.Id = p.Id);';
EXEC (@sql);

INSERT @Plan (Tgt, Pre, KeyJoin, HasIdentity) VALUES
    (N'doc.TableInstance', N'doc_TableInstance', N't.PeriodKey = p.PeriodKey AND t.Id = p.Id', 0),
    (N'doc.TableRow',      N'doc_TableRow',      N't.PeriodKey = p.PeriodKey AND t.Id = p.Id', 0),
    (N'doc.CellValue',     N'doc_CellValue',     N't.PeriodKey = p.PeriodKey AND t.TableRowId = p.TableRowId AND t.ColumnDefId = p.ColumnDefId', 0),
    (N'doc.CellStyle',     N'doc_CellStyle',     N't.PeriodKey = p.PeriodKey AND t.TableRowId = p.TableRowId AND t.ColumnDefId = p.ColumnDefId', 0),
    (N'calc.FormulaError', N'calc_FormulaError', N't.Id = p.Id', 1);

-- column names of the pre-image tables (sys.columns of THAT database; OBJECT_ID with a 3-part name)
CREATE TABLE #RvPreCols (Tbl nvarchar(128) COLLATE DATABASE_DEFAULT NOT NULL, Name nvarchar(128) COLLATE DATABASE_DEFAULT NOT NULL);
SET @sql = N'INSERT #RvPreCols (Tbl, Name) SELECT o.name, c.name FROM ' + QUOTENAME(@RvPreImage) + N'.sys.columns c JOIN '
         + QUOTENAME(@RvPreImage) + N'.sys.objects o ON o.object_id = c.object_id WHERE o.type = ''U'';';
EXEC (@sql);
SELECT @max = MAX(Ord) FROM @Plan;
WHILE @ord <= @max
BEGIN
    SELECT @tgt = Tgt, @pre = Pre, @key = KeyJoin, @ident = HasIdentity FROM @Plan WHERE Ord = @ord;
    SELECT @cols = STRING_AGG(CAST(QUOTENAME(c.name) AS nvarchar(max)), N', ') WITHIN GROUP (ORDER BY c.column_id)
    FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id
    WHERE c.object_id = OBJECT_ID(@tgt) AND c.is_computed = 0 AND ty.name <> N'timestamp'
      AND c.name IN (SELECT Name FROM #RvPreCols WHERE Tbl = @pre);
    SET @sql = CASE WHEN @ident = 1 THEN N'SET IDENTITY_INSERT ' + @tgt + N' ON; ' ELSE N'' END
        + N'INSERT ' + @tgt + N' (' + @cols + N') SELECT ' + REPLACE(@cols, N'[', N'p.[') + N' FROM ' + @P + QUOTENAME(@pre) + N' p'
        + N' WHERE NOT EXISTS (SELECT 1 FROM ' + @tgt + N' t WHERE ' + @key + N')'
        + CASE WHEN @pre IN (N'calc_CalculationRun', N'calc_FormulaError', N'doc_TableInstance') THEN N' AND p.DocumentId = @d'
               WHEN @pre LIKE N'calc[_]%' THEN N' AND p.CalculationRunId IN (SELECT Id FROM #RvRun)'
               WHEN @pre = N'doc_TableRow' THEN N' AND EXISTS (SELECT 1 FROM #RvRow x WHERE x.PeriodKey = p.PeriodKey AND x.Id = p.Id)'
               WHEN @pre IN (N'doc_CellValue', N'doc_CellStyle') THEN N' AND EXISTS (SELECT 1 FROM #RvRow x WHERE x.PeriodKey = p.PeriodKey AND x.Id = p.TableRowId)'
               ELSE N'' END
        + N'; SET @n = @@ROWCOUNT;'
        + CASE WHEN @ident = 1 THEN N' SET IDENTITY_INSERT ' + @tgt + N' OFF;' ELSE N'' END;
    EXEC sp_executesql @sql, N'@d bigint, @n int OUTPUT', @d = @RvDocumentId, @n = @n OUTPUT;
    IF @pre = N'calc_CalculationRun' SET @RvRestoredRuns = @n;
    SET @ord += 1;
END;

-- 4. document reopens created after the move
DELETE FROM doc.DocumentPeriodReopen WHERE DocumentId = @RvDocumentId AND Id NOT IN (SELECT Id FROM #RvReopen);
SET @RvDeletedReopens = @@ROWCOUNT;
SET @sql = N'SELECT @n = (SELECT COUNT(*) FROM wf.ApprovalState s WHERE s.DocumentId = @d AND NOT EXISTS (SELECT 1 FROM ' + @P + N'wf_ApprovalState p WHERE p.Id = s.Id))
                       + (SELECT COUNT(*) FROM wf.ApprovalEvent e WHERE e.DocumentId = @d AND NOT EXISTS (SELECT 1 FROM ' + @P + N'wf_ApprovalEvent p WHERE p.Id = e.Id));';
EXEC sp_executesql @sql, N'@d bigint, @n int OUTPUT', @d = @RvDocumentId, @n = @RvApprovals OUTPUT;

-- 5. the document itself
UPDATE doc.Document SET ProjectId = @RvProject, TemplateId = @RvTemplate WHERE Id = @RvDocumentId;

-- 6. checks against the manifest
DECLARE @Cells int, @Sum bigint, @Runs int, @Results int, @Mismatch int;
SELECT @Cells = COUNT(*), @Sum = ISNULL(SUM(CAST(CHECKSUM(cv.PeriodKey, cv.TableRowId, cv.ColumnDefId, cv.ValueString, cv.ValueNumeric, cv.ValueDate,
                                cv.ValueBool, cv.ValueRegistryEntryId, cv.ValueUnitId, cv.IsCalculated, cv.IsEmpty) AS bigint)), 0)
FROM doc.CellValue cv JOIN doc.TableRow r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId
JOIN doc.TableInstance i ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId WHERE i.DocumentId = @RvDocumentId;
SELECT @Runs = COUNT(*) FROM calc.CalculationRun WHERE DocumentId = @RvDocumentId;
SELECT @Results = COUNT(*) FROM calc.CalculationResult x JOIN calc.CalculationRun r ON r.Id = x.CalculationRunId WHERE r.DocumentId = @RvDocumentId;
SELECT @Mismatch = COUNT(*) FROM calc.CalculationRun r JOIN doc.Document d ON d.Id = r.DocumentId WHERE r.ProjectId <> d.ProjectId;

IF @Cells <> @RvCells OR @Sum <> @RvSum OR @Runs <> @RvRuns OR @Results <> @RvResults OR @Mismatch <> 0
BEGIN
    SET @RvMsg = CONCAT(N'Reverse: state differs from the pre-image (cells ', @Cells, N'/', @RvCells, N', checksum ', @Sum, N'/', @RvSum,
                        N', runs ', @Runs, N'/', @RvRuns, N', results ', @Results, N'/', @RvResults, N', projectMismatch ', @Mismatch, N'); nothing changed.');
    THROW 51838, @RvMsg, 1;
END;

INSERT aud.SecurityEvent (ChangedAt, EventType, TargetUserId, TargetRoleId, DetailsJson, ChangedByUserId, CorrelationId)
SELECT CONVERT(datetime2(3), SYSUTCDATETIME()), N'DocumentMoveReversed', NULL, NULL,
       (SELECT documentId = @RvDocumentId, toProjectId = @RvProject, preImage = @RvPreImage, deletedRuns = @RvDeletedRuns,
               restoredRuns = @RvRestoredRuns, deletedInstances = @RvDeletedInstances, deletedRows = @RvDeletedRows,
               deletedReopens = @RvDeletedReopens, correlationId = @RvCorrelationId FOR JSON PATH, WITHOUT_ARRAY_WRAPPER),
       @RvUserId, @RvCorrelationId;

SELECT ReverseReport = @RvMode, DocumentId = @RvDocumentId, ProjectId = @RvProject, TemplateId = @RvTemplate,
       DeletedRuns = @RvDeletedRuns, RestoredRuns = @RvRestoredRuns, DeletedInstances = @RvDeletedInstances, DeletedRows = @RvDeletedRows,
       DeletedReopens = @RvDeletedReopens, ApprovalsSinceMove = @RvApprovals, Cells = @Cells, CellChecksum = @Sum, Runs = @Runs,
       Results = @Results, ProjectMismatch = @Mismatch;

IF @RvMode = 'Apply' COMMIT; ELSE ROLLBACK;
