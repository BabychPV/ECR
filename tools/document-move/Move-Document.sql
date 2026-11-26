/*
  b14 document-move (D-579, P-1125-ONEPROJ): move ONE document to another project of the same year.
  One-off, rehearsed, run ONLY through Move-Document.ps1 (it substitutes the parameter block below and the
  Q-17 placeholder, and supplies @MvApplyToken after all safeguards passed). One sqlcmd session, one transaction:

    BEGIN TRAN -> locks -> blockers -> pre-state -> re-key (doc.Document + ALL calc.CalculationRun of the document)
      -> Q-17 Apply for this document against the TARGET calendar (@WriteLocked = 1: closed target months are written
         as a system relocation) -> removal of the non-owner month copies (only where equal to the owner, else abort)
      -> checks (Q-17 conflicts 0, owner = pre-state, no non-owner copies, projectMismatch 0)
      -> aud.SecurityEvent DocumentMovedToProject -> report
    -> COMMIT (Apply) | ROLLBACK (Preview, ALWAYS - enforced here, not by a flag of the wrapper).

  So Preview reports the real post-move state with the target calendar. Preview takes the same locks and consumes
  sequence values (doc.TableInstanceSeq / doc.TableRowSeq): run it with the worker stopped (window step W-2).

  Refusals (THROW 51810, message lists the codes; result set "blockers" before it): jobsRunning, archived,
  sameProject, otherYear, templateNotInTarget, versionMismatch, singleDocument, keyClash, periodMissing,
  workflowKindMismatch, columnRenames, projectRuns. After Q-17: q17Conflicts (51811), copyDiffers (51812),
  ownerMismatch (51813), projectMismatch (51814).

  Result sets (success): 1..5 = Q-17 (totals, per document, conflicts, noPeriodRow, lockedTargets),
  6 = MoveReport (one row), 7 = rightsDelta (users losing / gaining project access).
  ASCII only on purpose (sqlcmd / SqlClient / PowerShell 5.1 read it without BOM).
*/
-- BEGIN PARAMS
DECLARE @MvMode              varchar(10)    = 'Preview';   -- Preview | Apply
DECLARE @MvDocumentId        bigint         = NULL;
DECLARE @MvTargetProjectCode nvarchar(64)   = NULL;
DECLARE @MvCorrelationId     nvarchar(64)   = NULL;
DECLARE @MvReason            nvarchar(1000) = NULL;
DECLARE @MvUserId            int            = NULL;
DECLARE @MvAllowLive         bit            = 0;           -- only Move-Document.ps1 -AllowLive raises it
DECLARE @MvApplyToken        varchar(40)    = NULL;        -- only Move-Document.ps1 knows it
DECLARE @MvPreImage          sysname        = NULL;        -- EcrMovePre_<CorrelationId>: Apply compares the document with it
DECLARE @MvLockTimeout       int            = 60000;       -- ms for the product's edit locks (sp_getapplock)
-- END PARAMS

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 60000;

IF @MvMode NOT IN ('Preview', 'Apply') THROW 51801, 'Move: @MvMode must be Preview or Apply.', 1;
IF DB_NAME() IN (N'ECR', N'FLERT', N'EcrLegacyProd') THROW 51802, 'Move: refusing this database (legacy / foreign).', 1;
IF ISNULL(@MvApplyToken, '') <> 'MOVE-APPLY-v1' THROW 51803, 'Move: runs only through Move-Document.ps1.', 1;
IF @MvMode = 'Apply' AND DB_NAME() = N'EcrLandUi' AND @MvAllowLive = 0
    THROW 51804, 'Move: refusing to write into the live EcrLandUi without -AllowLive (human decision).', 1;
IF @MvDocumentId IS NULL OR @MvTargetProjectCode IS NULL OR @MvUserId IS NULL OR @MvCorrelationId IS NULL
    THROW 51805, 'Move: @MvDocumentId, @MvTargetProjectCode, @MvUserId and @MvCorrelationId are required.', 1;
IF @MvMode = 'Apply' AND LEN(LTRIM(RTRIM(ISNULL(@MvReason, N'')))) < 10
    THROW 51806, 'Move: Apply needs a reason (at least 10 characters).', 1;
-- review M-5: the acting user must hold SystemAdministrator (the same role the purge e2e uses)
IF NOT EXISTS (SELECT 1 FROM sec.RoleAssignment ra JOIN sec.Role r ON r.Id = ra.RoleId
               WHERE ra.UserId = @MvUserId AND r.Code = N'SystemAdministrator' AND (ra.ValidTo IS NULL OR ra.ValidTo > SYSUTCDATETIME()))
    THROW 51817, 'Move: @MvUserId must hold the SystemAdministrator role.', 1;
IF @MvMode = 'Apply' AND @MvPreImage IS NULL THROW 51818, 'Move: Apply needs the pre-image (Export-PreImage first).', 1;

DECLARE @MvSrc int, @MvTgt int, @MvTpl int, @MvKey nvarchar(200), @MvSrcKind tinyint, @MvTgtKind tinyint,
        @MvSrcCode nvarchar(64), @MvTplWasNull bit, @MvRuns int = 0, @MvMsg nvarchar(2000),
        @MvQ17Conflicts int = 0, @MvQ17Copies int = 0, @MvQ17Locked int = 0, @MvQ17Inst int = 0, @MvQ17Rows int = 0,
        @MvRemoved int = 0, @MvCalcCopiesKept int = 0, @MvMismatch int = 0, @MvNonOwner int = 0, @MvOwnerBad int = 0,
        @MvCellsBefore int, @MvCellsAfter int, @MvSumBefore bigint, @MvSumAfter bigint, @MvNow datetime2(3);

SELECT @MvSrc = d.ProjectId, @MvKey = d.BusinessKey, @MvTplWasNull = CASE WHEN d.TemplateId IS NULL THEN 1 ELSE 0 END,
       @MvTpl = COALESCE(d.TemplateId, v.TemplateId), @MvSrcKind = p.PeriodKind, @MvSrcCode = p.Code
FROM doc.Document d
JOIN doc.Project p ON p.Id = d.ProjectId
JOIN cfg.TemplateVersion v ON v.Id = p.TemplateVersionId
WHERE d.Id = @MvDocumentId;
IF @MvSrc IS NULL THROW 51807, 'Move: document not found.', 1;

SELECT @MvTgt = p.Id, @MvTgtKind = p.PeriodKind FROM doc.Project p WHERE p.Code = @MvTargetProjectCode;
IF @MvTgt IS NULL THROW 51808, 'Move: target project not found.', 1;

CREATE TABLE #MvBlock (Code varchar(40) COLLATE DATABASE_DEFAULT NOT NULL, Detail nvarchar(4000) COLLATE DATABASE_DEFAULT NULL);
CREATE TABLE #MvBefore (TableDefId int NOT NULL, RowKey nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL, ColumnDefId int NOT NULL,
                        Yr int NOT NULL, Mon int NOT NULL, ValueString nvarchar(1000) COLLATE DATABASE_DEFAULT NULL, ValueNumeric decimal(34,16) NULL,
                        ValueDate datetime2(3) NULL, ValueBool bit NULL, ValueRegistryEntryId int NULL, ValueUnitId int NULL,
                        PRIMARY KEY (TableDefId, RowKey, ColumnDefId, Yr));
CREATE TABLE #MvCopy (PeriodKey int NOT NULL, TableRowId bigint NOT NULL, ColumnDefId int NOT NULL, RowKey nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                      IsEmpty bit NOT NULL, IsCalculated bit NOT NULL, OwnerHas bit NOT NULL, SameValue bit NOT NULL, Disp nvarchar(1000) COLLATE DATABASE_DEFAULT NULL,
                      PRIMARY KEY (PeriodKey, TableRowId, ColumnDefId));

BEGIN TRAN;

-- locks: both project rows and the document row (re-read facts below run under them)
SELECT @MvMsg = NULL FROM doc.Project WITH (UPDLOCK, HOLDLOCK) WHERE Id IN (@MvSrc, @MvTgt);
SELECT @MvMsg = NULL FROM doc.Document WITH (UPDLOCK, HOLDLOCK) WHERE Id = @MvDocumentId;

-- review M-1: the product's own edit locks (SheetEditGate names): structure exclusive, then every sheet x copy x period
-- of the document's instances and of the target year (owner periods created by the relocation), ascending.
DECLARE @MvLock nvarchar(255), @MvRc int;
SET @MvLock = CONCAT(N'ecr:doc-structure:', @MvDocumentId);
EXEC @MvRc = sp_getapplock @Resource = @MvLock, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = @MvLockTimeout;
IF @MvRc < 0 THROW 51819, 'Move: the document structure is busy (sp_getapplock).', 1;
DECLARE lk CURSOR LOCAL FAST_FORWARD FOR
    SELECT CONCAT(N'ecr:sheet-edit:', @MvDocumentId, N':', x.SheetDefId, N':', x.PeriodKey, CASE WHEN x.CopyId IS NULL THEN N'' ELSE CONCAT(N':c', x.CopyId) END)
    FROM (SELECT td.SheetDefId, ti.SheetCopyId AS CopyId, ti.PeriodKey
          FROM doc.TableInstance ti JOIN cfg.TableDef td ON td.Id = ti.TableDefId WHERE ti.DocumentId = @MvDocumentId
          UNION
          SELECT td.SheetDefId, CAST(NULL AS bigint), p.PeriodKey
          FROM doc.TableInstance ti JOIN cfg.TableDef td ON td.Id = ti.TableDefId
          JOIN doc.Period p ON p.ProjectId = @MvTgt AND p.PeriodKey / 100 = ti.PeriodKey / 100
          WHERE ti.DocumentId = @MvDocumentId) x
    ORDER BY x.SheetDefId, ISNULL(x.CopyId, 0), x.PeriodKey;
OPEN lk; FETCH NEXT FROM lk INTO @MvLock;
WHILE @@FETCH_STATUS = 0
BEGIN
    EXEC @MvRc = sp_getapplock @Resource = @MvLock, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = @MvLockTimeout;
    IF @MvRc < 0 BEGIN SET @MvMsg = CONCAT(N'Move: sheet lock busy: ', @MvLock); THROW 51819, @MvMsg, 1; END;
    FETCH NEXT FROM lk INTO @MvLock;
END;
CLOSE lk; DEALLOCATE lk;

-- review M-1: Apply moves exactly the state of the pre-image (cell checksum and run ids as exported)
IF @MvPreImage IS NOT NULL
BEGIN
    IF DB_ID(@MvPreImage) IS NULL OR @MvPreImage NOT LIKE N'EcrMovePre[_]%' OR @MvPreImage LIKE N'%[^A-Za-z0-9_-]%'
        THROW 51816, 'Move: pre-image database not found.', 1;
    DECLARE @MvPiCells int, @MvPiSum bigint, @MvPiRuns nvarchar(max), @MvPiSql nvarchar(max);
    SET @MvPiSql = N'SELECT @c = Cells, @s = CellChecksum, @r = RunIds FROM ' + QUOTENAME(@MvPreImage) + N'.dbo.Manifest WHERE DocumentId = @d;';
    EXEC sp_executesql @MvPiSql, N'@d bigint, @c int OUTPUT, @s bigint OUTPUT, @r nvarchar(max) OUTPUT',
         @d = @MvDocumentId, @c = @MvPiCells OUTPUT, @s = @MvPiSum OUTPUT, @r = @MvPiRuns OUTPUT;
    IF @MvPiCells IS NULL THROW 51816, 'Move: the document is not in the pre-image.', 1;
    IF @MvPiCells <> (SELECT COUNT(*) FROM doc.CellValue cv JOIN doc.TableRow r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId
                      JOIN doc.TableInstance i ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId WHERE i.DocumentId = @MvDocumentId)
       OR @MvPiSum <> (SELECT ISNULL(SUM(CAST(CHECKSUM(cv.PeriodKey, cv.TableRowId, cv.ColumnDefId, cv.ValueString, cv.ValueNumeric, cv.ValueDate,
                              cv.ValueBool, cv.ValueRegistryEntryId, cv.ValueUnitId, cv.IsCalculated, cv.IsEmpty) AS bigint)), 0)
                       FROM doc.CellValue cv JOIN doc.TableRow r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId
                       JOIN doc.TableInstance i ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId WHERE i.DocumentId = @MvDocumentId)
       OR ISNULL(@MvPiRuns, N'') <> ISNULL((SELECT STRING_AGG(CAST(Id AS nvarchar(max)), N',') WITHIN GROUP (ORDER BY Id)
                                            FROM calc.CalculationRun WHERE DocumentId = @MvDocumentId), N'')
        THROW 51816, 'Move: the document changed since the pre-image (cells or runs) - export a new pre-image.', 1;
END;

-------------------------------------------------------------------------------------------------
-- blockers
-------------------------------------------------------------------------------------------------
IF @MvSrc = @MvTgt INSERT #MvBlock VALUES ('sameProject', @MvTargetProjectCode);

INSERT #MvBlock (Code, Detail)
SELECT 'jobsRunning', CONCAT(COUNT(*), N' queued/running job(s), e.g. ', MIN(JobId))
FROM itg.JobProgress WHERE State IN (N'Queued', N'Running') HAVING COUNT(*) > 0;

INSERT #MvBlock (Code, Detail)
SELECT 'archived', CONCAT(N'project ', p.Code, N' status ', p.Status, N' archiving ', p.IsArchiving)
FROM doc.Project p WHERE p.Id IN (@MvSrc, @MvTgt) AND (p.Status = 4 OR p.IsArchiving = 1);   -- ProjectStatus.Archived = 4 (review M-3)
IF EXISTS (SELECT 1 FROM arc.TableInstance WHERE DocumentId = @MvDocumentId)
    INSERT #MvBlock VALUES ('archived', N'document has archived instances (arc.TableInstance)');

INSERT #MvBlock (Code, Detail)
SELECT 'otherYear', CONCAT(N'instance year ', x.Yr, N' outside target ', YEAR(p.PeriodStart), N'..', YEAR(p.PeriodEnd))
FROM (SELECT DISTINCT PeriodKey / 100 AS Yr FROM doc.TableInstance WHERE DocumentId = @MvDocumentId) x
CROSS JOIN doc.Project p
WHERE p.Id = @MvTgt AND x.Yr NOT BETWEEN YEAR(p.PeriodStart) AND YEAR(p.PeriodEnd);

IF NOT EXISTS (SELECT 1 FROM doc.ProjectTemplate pt WHERE pt.ProjectId = @MvTgt AND pt.TemplateId = @MvTpl)
   AND NOT EXISTS (SELECT 1 FROM doc.Project p JOIN cfg.TemplateVersion v ON v.Id = p.TemplateVersionId
                   WHERE p.Id = @MvTgt AND v.TemplateId = @MvTpl)
    INSERT #MvBlock VALUES ('templateNotInTarget', CONCAT(N'template ', @MvTpl));

INSERT #MvBlock (Code, Detail)
SELECT 'versionMismatch', CONCAT(N'period ', k.PeriodKey, N': source v', doc.fn_TemplateVersionOf(@MvSrc, @MvTpl, k.PeriodKey),
                                 N' target v', ISNULL(CONVERT(nvarchar(20), doc.fn_TemplateVersionOf(@MvTgt, @MvTpl, k.PeriodKey)), N'none'))
FROM (SELECT DISTINCT PeriodKey FROM doc.TableInstance WHERE DocumentId = @MvDocumentId
      UNION   -- review M-4: also the owner periods the relocation creates
      SELECT DISTINCT (ti.PeriodKey / 100) * 100 + col.MonthNumber
      FROM doc.TableInstance ti
      JOIN cfg.TableDef td ON td.Id = ti.TableDefId AND td.LayoutKind = 0
      JOIN doc.TableRow r ON r.PeriodKey = ti.PeriodKey AND r.TableInstanceId = ti.Id AND r.IsDeleted = 0
      JOIN doc.CellValue cv ON cv.PeriodKey = r.PeriodKey AND cv.TableRowId = r.Id AND cv.IsEmpty = 0
      JOIN cfg.ColumnDef col ON col.Id = cv.ColumnDefId AND col.IsMonthColumn = 1 AND col.MonthNumber BETWEEN 1 AND 12
      WHERE ti.DocumentId = @MvDocumentId AND @MvTgtKind = 0) k
WHERE ISNULL(doc.fn_TemplateVersionOf(@MvTgt, @MvTpl, k.PeriodKey), -1) <> ISNULL(doc.fn_TemplateVersionOf(@MvSrc, @MvTpl, k.PeriodKey), -2);

IF EXISTS (SELECT 1 FROM cfg.Template t WHERE t.Id = @MvTpl AND t.SingleDocumentPerProject = 1)
   AND EXISTS (SELECT 1 FROM doc.Document d JOIN doc.Project p ON p.Id = d.ProjectId JOIN cfg.TemplateVersion v ON v.Id = p.TemplateVersionId
               WHERE d.ProjectId = @MvTgt AND d.Id <> @MvDocumentId AND COALESCE(d.TemplateId, v.TemplateId) = @MvTpl)
    INSERT #MvBlock VALUES ('singleDocument', CONCAT(N'target already has a document of template ', @MvTpl));

IF EXISTS (SELECT 1 FROM doc.Document WHERE ProjectId = @MvTgt AND BusinessKey = @MvKey AND Id <> @MvDocumentId)
    INSERT #MvBlock VALUES ('keyClash', @MvKey);

INSERT #MvBlock (Code, Detail)
SELECT 'periodMissing', CONCAT(N'target has no period ', k.PeriodKey)
FROM (SELECT DISTINCT PeriodKey FROM doc.TableInstance WHERE DocumentId = @MvDocumentId
      UNION
      -- owner periods of the month cells after relocation (monthly target only)
      SELECT DISTINCT (ti.PeriodKey / 100) * 100 + col.MonthNumber
      FROM doc.TableInstance ti
      JOIN cfg.TableDef td ON td.Id = ti.TableDefId AND td.LayoutKind = 0
      JOIN doc.TableRow r ON r.PeriodKey = ti.PeriodKey AND r.TableInstanceId = ti.Id AND r.IsDeleted = 0
      JOIN doc.CellValue cv ON cv.PeriodKey = r.PeriodKey AND cv.TableRowId = r.Id AND cv.IsEmpty = 0
      JOIN cfg.ColumnDef col ON col.Id = cv.ColumnDefId AND col.IsMonthColumn = 1 AND col.MonthNumber BETWEEN 1 AND 12
      WHERE ti.DocumentId = @MvDocumentId AND @MvTgtKind = 0) k
WHERE NOT EXISTS (SELECT 1 FROM doc.Period p WHERE p.ProjectId = @MvTgt AND p.PeriodKey = k.PeriodKey);

IF @MvSrcKind <> @MvTgtKind
   AND (EXISTS (SELECT 1 FROM wf.ApprovalState WHERE DocumentId = @MvDocumentId)
        OR EXISTS (SELECT 1 FROM wf.ApprovalEvent WHERE DocumentId = @MvDocumentId)
        OR EXISTS (SELECT 1 FROM calc.SubmissionSnapshot WHERE DocumentId = @MvDocumentId)
        OR EXISTS (SELECT 1 FROM doc.DocumentPeriodReopen WHERE DocumentId = @MvDocumentId))
    INSERT #MvBlock VALUES ('workflowKindMismatch', CONCAT(N'PeriodKind ', @MvSrcKind, N' -> ', @MvTgtKind, N' with workflow history'));

IF EXISTS (SELECT 1 FROM cfg.ColumnRenameProject WHERE ProjectId = @MvSrc)
    INSERT #MvBlock VALUES ('columnRenames', N'source project has cfg.ColumnRenameProject rows');

IF EXISTS (SELECT 1 FROM calc.CalculationRun WHERE ProjectId = @MvSrc AND DocumentId IS NULL)
    INSERT #MvBlock VALUES ('projectRuns', N'source project has project-level runs (DocumentId NULL)');

IF EXISTS (SELECT 1 FROM #MvBlock)
BEGIN
    SELECT MoveBlocker = Code, Detail FROM #MvBlock ORDER BY Code;
    SELECT @MvMsg = CONCAT(N'Move refused: ', STRING_AGG(CAST(Code AS nvarchar(max)), N', ')) FROM (SELECT DISTINCT Code FROM #MvBlock) b;
    ROLLBACK;
    THROW 51810, @MvMsg, 1;
END;

-------------------------------------------------------------------------------------------------
-- pre-state: month values per (table, row key, month column, year) and the cell checksum of the document
-------------------------------------------------------------------------------------------------
INSERT #MvBefore (TableDefId, RowKey, ColumnDefId, Yr, Mon, ValueString, ValueNumeric, ValueDate, ValueBool, ValueRegistryEntryId, ValueUnitId)
SELECT x.TableDefId, x.RowKey, x.ColumnDefId, x.Yr, x.Mon, x.ValueString, x.ValueNumeric, x.ValueDate, x.ValueBool, x.ValueRegistryEntryId, x.ValueUnitId
FROM (SELECT ti.TableDefId, r.RowKey, cv.ColumnDefId, ti.PeriodKey / 100 AS Yr, col.MonthNumber AS Mon,
             cv.ValueString, cv.ValueNumeric, cv.ValueDate, cv.ValueBool, cv.ValueRegistryEntryId, cv.ValueUnitId,
             ROW_NUMBER() OVER (PARTITION BY ti.TableDefId, r.RowKey, cv.ColumnDefId, ti.PeriodKey / 100
                                ORDER BY CASE WHEN ti.PeriodKey = (ti.PeriodKey / 100) * 100 + col.MonthNumber THEN 0 ELSE 1 END, ti.PeriodKey) AS rn
      FROM doc.TableInstance ti
      JOIN cfg.TableDef td ON td.Id = ti.TableDefId AND td.LayoutKind = 0
      JOIN doc.TableRow r ON r.PeriodKey = ti.PeriodKey AND r.TableInstanceId = ti.Id AND r.IsDeleted = 0
      JOIN doc.CellValue cv ON cv.PeriodKey = r.PeriodKey AND cv.TableRowId = r.Id AND cv.IsEmpty = 0 AND cv.IsCalculated = 0
      JOIN cfg.ColumnDef col ON col.Id = cv.ColumnDefId AND col.IsMonthColumn = 1 AND col.MonthNumber BETWEEN 1 AND 12
      WHERE ti.DocumentId = @MvDocumentId AND ti.SheetCopyId IS NULL) x
WHERE x.rn = 1;

SELECT @MvCellsBefore = COUNT(*), @MvSumBefore = ISNULL(SUM(CAST(CHECKSUM(r.RowKey, cv.ColumnDefId, cv.ValueString, cv.ValueNumeric, cv.ValueDate,
                                                               cv.ValueBool, cv.ValueRegistryEntryId, cv.ValueUnitId) AS bigint)), 0)
FROM doc.TableInstance ti
JOIN doc.TableRow r ON r.PeriodKey = ti.PeriodKey AND r.TableInstanceId = ti.Id AND r.IsDeleted = 0
JOIN doc.CellValue cv ON cv.PeriodKey = r.PeriodKey AND cv.TableRowId = r.Id AND cv.IsEmpty = 0
WHERE ti.DocumentId = @MvDocumentId;

-------------------------------------------------------------------------------------------------
-- re-key: the document (with its own template fixed) and EVERY run of it (any status)
-------------------------------------------------------------------------------------------------
UPDATE doc.Document SET ProjectId = @MvTgt, TemplateId = @MvTpl WHERE Id = @MvDocumentId;
UPDATE calc.CalculationRun SET ProjectId = @MvTgt WHERE DocumentId = @MvDocumentId;
SET @MvRuns = @@ROWCOUNT;

-------------------------------------------------------------------------------------------------
-- Q-17 Apply for this document against the target calendar (no-op for a non-monthly target)
-------------------------------------------------------------------------------------------------
-- @@Q17@@
IF @@TRANCOUNT <> 1 THROW 51815, 'Move: Q-17 left the transaction in an unexpected state.', 1;

SELECT @MvQ17Conflicts = ISNULL(SUM(conflicts), 0), @MvQ17Copies = ISNULL(SUM(copiesWritten), 0),
       @MvQ17Locked = ISNULL(SUM(writtenToLocked), 0), @MvQ17Inst = ISNULL(SUM(instancesCreated), 0),
       @MvQ17Rows = ISNULL(SUM(rowsCreated), 0)
FROM #AccSum;
IF @MvQ17Conflicts > 0
BEGIN
    SET @MvMsg = CONCAT(N'Move: Q-17 reports ', @MvQ17Conflicts, N' conflict(s); nothing moved.');
    THROW 51811, @MvMsg, 1;
END;

-------------------------------------------------------------------------------------------------
-- non-owner month copies: removed only where equal to the owner (else abort)
-------------------------------------------------------------------------------------------------
IF @MvTgtKind = 0
BEGIN
    INSERT #MvCopy (PeriodKey, TableRowId, ColumnDefId, RowKey, IsEmpty, IsCalculated, OwnerHas, SameValue, Disp)
    SELECT cv.PeriodKey, cv.TableRowId, cv.ColumnDefId, r.RowKey, cv.IsEmpty, cv.IsCalculated,
           CASE WHEN o.IsEmpty = 0 THEN 1 ELSE 0 END,
           CASE WHEN o.IsEmpty = 0 AND EXISTS (SELECT cv.ValueString, cv.ValueNumeric, cv.ValueDate, cv.ValueBool, cv.ValueRegistryEntryId, cv.ValueUnitId
                                              INTERSECT
                                              SELECT o.ValueString, o.ValueNumeric, o.ValueDate, o.ValueBool, o.ValueRegistryEntryId, o.ValueUnitId)
                THEN 1 ELSE 0 END,
           CAST(COALESCE(cv.ValueString, n.s, CONVERT(nvarchar(30), cv.ValueDate, 126),
                         CASE cv.ValueBool WHEN 1 THEN N'true' WHEN 0 THEN N'false' END,
                         CONVERT(nvarchar(20), cv.ValueRegistryEntryId), CONVERT(nvarchar(20), cv.ValueUnitId)) AS nvarchar(1000))
    FROM doc.TableInstance ti
    JOIN cfg.TableDef td ON td.Id = ti.TableDefId AND td.LayoutKind = 0
    JOIN doc.TableRow r ON r.PeriodKey = ti.PeriodKey AND r.TableInstanceId = ti.Id AND r.IsDeleted = 0
    JOIN doc.CellValue cv ON cv.PeriodKey = r.PeriodKey AND cv.TableRowId = r.Id
    JOIN cfg.ColumnDef col ON col.Id = cv.ColumnDefId AND col.IsMonthColumn = 1 AND col.MonthNumber BETWEEN 1 AND 12
    CROSS APPLY (SELECT OwnerKey = (ti.PeriodKey / 100) * 100 + col.MonthNumber) k
    OUTER APPLY (SELECT TOP (1) ocv.IsEmpty, ocv.ValueString, ocv.ValueNumeric, ocv.ValueDate, ocv.ValueBool, ocv.ValueRegistryEntryId, ocv.ValueUnitId
                 FROM doc.TableInstance oti
                 JOIN doc.TableRow orow ON orow.PeriodKey = oti.PeriodKey AND orow.TableInstanceId = oti.Id AND orow.IsDeleted = 0 AND orow.RowKey = r.RowKey
                 JOIN doc.CellValue ocv ON ocv.PeriodKey = orow.PeriodKey AND ocv.TableRowId = orow.Id AND ocv.ColumnDefId = cv.ColumnDefId
                 WHERE oti.DocumentId = @MvDocumentId AND oti.TableDefId = ti.TableDefId AND oti.PeriodKey = k.OwnerKey AND oti.SheetCopyId IS NULL) o
    CROSS APPLY (SELECT t = CONVERT(nvarchar(60), cv.ValueNumeric)) a
    CROSS APPLY (SELECT z = REPLACE(RTRIM(REPLACE(a.t, N'0', N' ')), N' ', N'0')) z
    CROSS APPLY (SELECT s = CASE WHEN a.t IS NULL THEN NULL WHEN CHARINDEX(N'.', a.t) = 0 THEN a.t
                                 WHEN RIGHT(z.z, 1) = N'.' THEN LEFT(z.z, LEN(z.z) - 1) ELSE z.z END) n
    WHERE ti.DocumentId = @MvDocumentId AND ti.SheetCopyId IS NULL AND ti.PeriodKey <> k.OwnerKey
      AND EXISTS (SELECT 1 FROM doc.Period p WHERE p.ProjectId = @MvTgt AND p.PeriodKey = k.OwnerKey);

    SELECT @MvCalcCopiesKept = COUNT(*) FROM #MvCopy WHERE IsCalculated = 1;
    SELECT @MvMismatch = COUNT(*) FROM #MvCopy WHERE IsCalculated = 0 AND IsEmpty = 0 AND SameValue = 0;
    IF @MvMismatch > 0
    BEGIN
        SELECT TOP (50) MoveCopyDiffers = PeriodKey, RowKey, ColumnDefId, Disp, OwnerHas FROM #MvCopy
        WHERE IsCalculated = 0 AND IsEmpty = 0 AND SameValue = 0 ORDER BY PeriodKey, RowKey, ColumnDefId;
        SET @MvMsg = CONCAT(N'Move: ', @MvMismatch, N' non-owner month copy(ies) differ from the owner; nothing moved.');
        THROW 51812, @MvMsg, 1;
    END;

    SET @MvNow = CONVERT(datetime2(3), SYSUTCDATETIME());
    INSERT aud.CellChange (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, NewValue,
                           ChangedByUserId, Origin, IsLateEdit, CorrelationId, IsOutOfWindow, IsOverwrite)
    SELECT @MvNow, PeriodKey, @MvDocumentId, TableRowId, RowKey, ColumnDefId, Disp, NULL, @MvUserId, N'Migration', 0, @MvCorrelationId, 0, 0
    FROM #MvCopy WHERE IsCalculated = 0 AND IsEmpty = 0;

    DELETE cv
    FROM doc.CellValue cv
    JOIN #MvCopy c ON c.PeriodKey = cv.PeriodKey AND c.TableRowId = cv.TableRowId AND c.ColumnDefId = cv.ColumnDefId
    WHERE c.IsCalculated = 0;
    SET @MvRemoved = @@ROWCOUNT;
END;

-------------------------------------------------------------------------------------------------
-- checks
-------------------------------------------------------------------------------------------------
-- this document must be consistent (abort); the whole database is reported (live W-9 expects 0)
DECLARE @MvProjectMismatch int = (SELECT COUNT(*) FROM calc.CalculationRun r JOIN doc.Document d ON d.Id = r.DocumentId WHERE r.ProjectId <> d.ProjectId AND d.Id = @MvDocumentId);
DECLARE @MvProjectMismatchAll int = (SELECT COUNT(*) FROM calc.CalculationRun r JOIN doc.Document d ON d.Id = r.DocumentId WHERE r.ProjectId <> d.ProjectId);

IF @MvTgtKind = 0
BEGIN
    SELECT @MvNonOwner = COUNT(*)
    FROM doc.TableInstance ti
    JOIN cfg.TableDef td ON td.Id = ti.TableDefId AND td.LayoutKind = 0
    JOIN doc.TableRow r ON r.PeriodKey = ti.PeriodKey AND r.TableInstanceId = ti.Id AND r.IsDeleted = 0
    JOIN doc.CellValue cv ON cv.PeriodKey = r.PeriodKey AND cv.TableRowId = r.Id AND cv.IsEmpty = 0 AND cv.IsCalculated = 0
    JOIN cfg.ColumnDef col ON col.Id = cv.ColumnDefId AND col.IsMonthColumn = 1 AND col.MonthNumber BETWEEN 1 AND 12
    WHERE ti.DocumentId = @MvDocumentId AND ti.SheetCopyId IS NULL AND ti.PeriodKey <> (ti.PeriodKey / 100) * 100 + col.MonthNumber;

    SELECT @MvOwnerBad = COUNT(*)
    FROM #MvBefore b
    WHERE NOT EXISTS (
        SELECT 1
        FROM doc.TableInstance oti
        JOIN doc.TableRow orow ON orow.PeriodKey = oti.PeriodKey AND orow.TableInstanceId = oti.Id AND orow.IsDeleted = 0 AND orow.RowKey = b.RowKey
        JOIN doc.CellValue ocv ON ocv.PeriodKey = orow.PeriodKey AND ocv.TableRowId = orow.Id AND ocv.ColumnDefId = b.ColumnDefId AND ocv.IsEmpty = 0
        WHERE oti.DocumentId = @MvDocumentId AND oti.TableDefId = b.TableDefId AND oti.PeriodKey = b.Yr * 100 + b.Mon AND oti.SheetCopyId IS NULL
          AND EXISTS (SELECT ocv.ValueString, ocv.ValueNumeric, ocv.ValueDate, ocv.ValueBool, ocv.ValueRegistryEntryId, ocv.ValueUnitId
                      INTERSECT
                      SELECT b.ValueString, b.ValueNumeric, b.ValueDate, b.ValueBool, b.ValueRegistryEntryId, b.ValueUnitId));
END;

IF @MvOwnerBad > 0
BEGIN
    SET @MvMsg = CONCAT(N'Move: ', @MvOwnerBad, N' month value(s) are not in their owner period after relocation; nothing moved.');
    THROW 51813, @MvMsg, 1;
END;

IF @MvProjectMismatch > 0
BEGIN
    SET @MvMsg = CONCAT(N'Move: ', @MvProjectMismatch, N' calc.CalculationRun row(s) with ProjectId <> document project; nothing moved.');
    THROW 51814, @MvMsg, 1;
END;

SELECT @MvCellsAfter = COUNT(*), @MvSumAfter = ISNULL(SUM(CAST(CHECKSUM(r.RowKey, cv.ColumnDefId, cv.ValueString, cv.ValueNumeric, cv.ValueDate,
                                                             cv.ValueBool, cv.ValueRegistryEntryId, cv.ValueUnitId) AS bigint)), 0)
FROM doc.TableInstance ti
JOIN doc.TableRow r ON r.PeriodKey = ti.PeriodKey AND r.TableInstanceId = ti.Id AND r.IsDeleted = 0
JOIN doc.CellValue cv ON cv.PeriodKey = r.PeriodKey AND cv.TableRowId = r.Id AND cv.IsEmpty = 0
WHERE ti.DocumentId = @MvDocumentId;

-------------------------------------------------------------------------------------------------
-- audit event + report
-------------------------------------------------------------------------------------------------
DECLARE @MvDetails nvarchar(max) = (
    SELECT documentId = @MvDocumentId, documentKey = @MvKey, templateId = @MvTpl, templateIdWasNull = @MvTplWasNull,
           fromProjectId = @MvSrc, fromProjectCode = @MvSrcCode, toProjectId = @MvTgt, toProjectCode = @MvTargetProjectCode,
           reason = @MvReason, runsRekeyed = @MvRuns, q17CopiesWritten = @MvQ17Copies, q17WrittenToLocked = @MvQ17Locked,
           q17InstancesCreated = @MvQ17Inst, q17RowsCreated = @MvQ17Rows, nonOwnerCopiesRemoved = @MvRemoved,
           cellsBefore = @MvCellsBefore, cellsAfter = @MvCellsAfter, correlationId = @MvCorrelationId,
           originalLogin = ORIGINAL_LOGIN()
    FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);

INSERT aud.SecurityEvent (ChangedAt, EventType, TargetUserId, TargetRoleId, DetailsJson, ChangedByUserId, CorrelationId)
VALUES (CONVERT(datetime2(3), SYSUTCDATETIME()), N'DocumentMovedToProject', NULL, NULL, @MvDetails, @MvUserId, @MvCorrelationId);

SELECT MoveReport = @MvMode, DatabaseName = DB_NAME(), DocumentId = @MvDocumentId, DocumentKey = @MvKey, TemplateId = @MvTpl,
       TemplateIdWasNull = @MvTplWasNull, FromProject = @MvSrcCode, FromPeriodKind = @MvSrcKind, ToProject = @MvTargetProjectCode,
       ToPeriodKind = @MvTgtKind, RunsRekeyed = @MvRuns, Q17Conflicts = @MvQ17Conflicts, Q17CopiesWritten = @MvQ17Copies,
       Q17WrittenToLocked = @MvQ17Locked, Q17InstancesCreated = @MvQ17Inst, Q17RowsCreated = @MvQ17Rows,
       NonOwnerCopiesRemoved = @MvRemoved, CalculatedCopiesKept = @MvCalcCopiesKept, NonOwnerCopiesLeft = @MvNonOwner,
       OwnerMismatch = @MvOwnerBad, ProjectMismatch = @MvProjectMismatch, ProjectMismatchAllDocuments = @MvProjectMismatchAll, MonthValuesBefore = (SELECT COUNT(*) FROM #MvBefore),
       CellsBefore = @MvCellsBefore, CellsAfter = @MvCellsAfter, ChecksumBefore = @MvSumBefore, ChecksumAfter = @MvSumAfter,
       CorrelationId = @MvCorrelationId;

-- rights delta (M1, review L-4): effective project access = an allow grant and no deny grant on the project, through an
-- active assignment; scoped assignments (ScopeJson) are listed separately - they apply only inside their scope.
;WITH a AS (
    SELECT ra.UserId, rg.ResourceId AS ProjectId, rg.IsDeny, CASE WHEN ra.ScopeJson IS NULL THEN 0 ELSE 1 END AS Scoped
    FROM sec.ResourceGrant rg
    JOIN sec.RoleAssignment ra ON ra.RoleId = rg.RoleId
    WHERE rg.ResourceKind = 0 AND rg.ResourceId IN (@MvSrc, @MvTgt) AND ra.UserId IS NOT NULL
      AND (rg.IsDeny = 1 OR rg.Level > 0) AND (ra.ValidTo IS NULL OR ra.ValidTo > SYSUTCDATETIME())),
g AS (
    SELECT UserId, ProjectId, MAX(Scoped) AS Scoped FROM a
    WHERE IsDeny = 0 AND NOT EXISTS (SELECT 1 FROM a d WHERE d.IsDeny = 1 AND d.UserId = a.UserId AND d.ProjectId = a.ProjectId)
    GROUP BY UserId, ProjectId)
SELECT RightsDelta = CASE WHEN s.UserId IS NOT NULL THEN 'lose' ELSE 'gain' END, UserId = COALESCE(s.UserId, t.UserId),
       ScopedAssignment = COALESCE(s.Scoped, t.Scoped)
FROM (SELECT UserId, Scoped FROM g WHERE ProjectId = @MvSrc) s
FULL JOIN (SELECT UserId, Scoped FROM g WHERE ProjectId = @MvTgt) t ON t.UserId = s.UserId
WHERE s.UserId IS NULL OR t.UserId IS NULL
ORDER BY 1, 2;
IF @MvMode = 'Apply' COMMIT; ELSE ROLLBACK;
