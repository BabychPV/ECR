/*
  Q-17 (D-457): month-column consolidation. ONE script for three modes.

    Preview (default)  read-only: builds the plan in #temp tables and returns the report.
                       Writes NOTHING to the database (only to tempdb). Safe to run in SSMS as is.
    Apply              writes (owner cells, owner instances, mirror rows, audit). Only through
                       Apply-MonthColumnConsolidation.ps1 (it substitutes the parameter block below
                       and supplies @ApplyToken after all safeguards passed).
    Purge              deletes copies (p <> owner) whose value equals the owner's. Same entry point.

  Model (docs/.. Q17-DESIGN, section "data migration"):
    - table LayoutKind = MonthsInColumns (0) of a Monthly project (Project.PeriodKind = 0);
    - month column M{m}   -> owner = the table instance of period (year, m)   (PeriodKey = year*100 + m);
    - entered non-month column -> owner = anchor = MIN(doc.Period.PeriodKey) of the project calendar in that year;
    - IsCalculated = 1 cells are never moved;
    - owner value, when non-empty, stays; other different non-empty values = CONFLICT (reported);
    - owner empty -> "last entered" copy: MAX(aud.CellChange.ChangedAt) (Origin <> 'Recalculation',
      NewValue not null), then TableRow.ModifiedAt, then the period nearest to the owner, then the smaller period;
    - rows: union of live RowKeys over copies; every existing instance gets the missing ones (mirrors),
      the anchor included;
    - locked owner (wf.ApprovalState Submitted/Approved for the sheet+period, or doc.Period.State = Closed)
      -> report only (lockedTargets) unless @WriteLocked = 1;
    - owner period without a doc.Period row -> report only (noPeriodRow), the local copy stays.

  Result sets (always, in this order):
    1. totals (one row)               2. per document
    3. conflicts (detail)             4. noPeriodRow (detail per column)       5. lockedTargets (detail)

  The script is idempotent: a second Apply plans 0 writes (copyPlanned = rowsToCreate = 0).
  ASCII only on purpose (sqlcmd / SqlClient / PowerShell 5.1 read it without BOM).
*/
-- BEGIN PARAMS
DECLARE @Mode        varchar(10)  = 'Preview';   -- Preview | Apply | Purge
DECLARE @DocumentId  bigint       = NULL;        -- NULL = all documents
DECLARE @Year        int          = NULL;        -- NULL = all years
DECLARE @ColumnScope varchar(10)  = 'All';       -- All | NonMonth (Reanchor)
DECLARE @RunId       nvarchar(64) = NULL;        -- aud.CellChange.CorrelationId
DECLARE @UserId      int          = NULL;        -- aud.CellChange.ChangedByUserId
DECLARE @WriteLocked bit          = 0;           -- 1 = also write into locked owners (admin action)
DECLARE @AllowLive   bit          = 0;           -- only the PowerShell wrapper may raise it
DECLARE @ApplyToken  varchar(40)  = NULL;        -- only the PowerShell wrapper knows it
-- END PARAMS

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 60000;
SET DEADLOCK_PRIORITY LOW;

IF @Mode NOT IN ('Preview', 'Apply', 'Purge') THROW 51701, 'Q17: @Mode must be Preview, Apply or Purge.', 1;
IF @ColumnScope NOT IN ('All', 'NonMonth') THROW 51705, 'Q17: @ColumnScope must be All or NonMonth.', 1;
IF @Mode <> 'Preview' AND ISNULL(@ApplyToken, '') <> 'Q17-APPLY-v1'
    THROW 51702, 'Q17: write modes run only through Apply-MonthColumnConsolidation.ps1.', 1;
IF @Mode <> 'Preview' AND DB_NAME() = N'EcrLandUi' AND @AllowLive = 0
    THROW 51703, 'Q17: refusing to write into the live EcrLandUi without -AllowLive (human decision).', 1;
IF @Mode <> 'Preview' AND (@RunId IS NULL OR @UserId IS NULL)
    THROW 51704, 'Q17: write modes need @RunId and @UserId.', 1;

-------------------------------------------------------------------------------------------------
-- accumulators (whole run)
-------------------------------------------------------------------------------------------------
CREATE TABLE #AccSum
(
    DocumentId bigint NOT NULL, copyCells int NOT NULL, groupsTotal int NOT NULL, copyPlanned int NOT NULL,
    conflicts int NOT NULL, stillEmpty int NOT NULL, noPeriodRow int NOT NULL, lockedTargets int NOT NULL,
    deletedRowBlocked int NOT NULL, ownerInstancesToCreate int NOT NULL, mirrorRowsToCreate int NOT NULL,
    anchorRowsToCreate int NOT NULL, copiesWritten int NOT NULL, writtenToLocked int NOT NULL,
    rowsCreated int NOT NULL, instancesCreated int NOT NULL, copiesPurged int NOT NULL,
    purgeSkippedLocked int NOT NULL, purgeKeptDifferent int NOT NULL
);
CREATE TABLE #AccConflict
(
    DocumentId bigint, Yr int, TableCode nvarchar(100) COLLATE DATABASE_DEFAULT, RowKey nvarchar(100) COLLATE DATABASE_DEFAULT,
    ColumnCode nvarchar(100) COLLATE DATABASE_DEFAULT, OwnerPeriod int, ValuesByPeriod nvarchar(max) COLLATE DATABASE_DEFAULT,
    Chosen nvarchar(1100) COLLATE DATABASE_DEFAULT, Reason varchar(40) COLLATE DATABASE_DEFAULT
);
CREATE TABLE #AccNoPeriod
(
    DocumentId bigint, Yr int, TableCode nvarchar(100) COLLATE DATABASE_DEFAULT, ColumnCode nvarchar(100) COLLATE DATABASE_DEFAULT,
    ExpectedOwnerPeriod int NULL, Groups int
);
CREATE TABLE #AccLocked
(
    DocumentId bigint, Yr int, TableCode nvarchar(100) COLLATE DATABASE_DEFAULT, RowKey nvarchar(100) COLLATE DATABASE_DEFAULT,
    ColumnCode nvarchar(100) COLLATE DATABASE_DEFAULT, OwnerPeriod int, Chosen nvarchar(1100) COLLATE DATABASE_DEFAULT,
    Reason varchar(40) COLLATE DATABASE_DEFAULT
);

-------------------------------------------------------------------------------------------------
-- per-document working tables
-------------------------------------------------------------------------------------------------
CREATE TABLE #Docs (DocumentId bigint NOT NULL PRIMARY KEY, ProjectId int NOT NULL);
CREATE TABLE #Inst (PeriodKey int NOT NULL, InstId bigint NOT NULL, TableDefId int NOT NULL, Yr int NOT NULL, SheetDefId int NOT NULL,
                    PRIMARY KEY (PeriodKey, InstId));
CREATE TABLE #Row (PeriodKey int NOT NULL, RowId bigint NOT NULL, InstId bigint NOT NULL, TableDefId int NOT NULL, Yr int NOT NULL,
                   RowKey nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL, RowDefId int NULL, Ordinal int NOT NULL,
                   IsDeleted bit NOT NULL, ModifiedAt datetime2(7) NOT NULL, PRIMARY KEY (PeriodKey, RowId));
CREATE TABLE #Col (TableDefId int NOT NULL, ColumnDefId int NOT NULL, ColCode nvarchar(100) COLLATE DATABASE_DEFAULT,
                   IsMonth bit NOT NULL, MonthNumber tinyint NULL, PRIMARY KEY (TableDefId, ColumnDefId));
CREATE TABLE #Anchor (Yr int NOT NULL PRIMARY KEY, AnchorPeriod int NULL);
CREATE TABLE #Own (Yr int NOT NULL, TableDefId int NOT NULL, ColumnDefId int NOT NULL, IsMonth bit NOT NULL, SheetDefId int NOT NULL,
                   OwnerPeriod int NULL, HasPeriodRow bit NOT NULL, PRIMARY KEY (Yr, TableDefId, ColumnDefId));
CREATE TABLE #Cell (Yr int NOT NULL, TableDefId int NOT NULL, ColumnDefId int NOT NULL, RowKey nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                    P int NOT NULL, RowId bigint NOT NULL, InstId bigint NOT NULL, IsEmpty bit NOT NULL, IsCalc bit NOT NULL,
                    RowModAt datetime2(7) NOT NULL, Disp nvarchar(1000) COLLATE DATABASE_DEFAULT NULL,
                    VK nvarchar(1400) COLLATE Latin1_General_BIN2 NOT NULL,
                    ValueString nvarchar(1000) COLLATE DATABASE_DEFAULT NULL, ValueNumeric decimal(34,16) NULL, ValueDate datetime2(3) NULL,
                    ValueBool bit NULL, ValueRegistryEntryId int NULL, ValueUnitId int NULL,
                    PRIMARY KEY (Yr, TableDefId, ColumnDefId, RowKey, P));
CREATE TABLE #Grp (Yr int NOT NULL, TableDefId int NOT NULL, ColumnDefId int NOT NULL, RowKey nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                   SheetDefId int NOT NULL, OwnerPeriod int NULL, HasPeriodRow bit NOT NULL,
                   OwnerRowId bigint NULL, OwnerRowDeleted bit NOT NULL DEFAULT 0, OwnerCellExists bit NOT NULL DEFAULT 0,
                   OwnerNonEmpty bit NOT NULL DEFAULT 0, OwnerVK nvarchar(1400) COLLATE Latin1_General_BIN2 NULL,
                   OwnerDisp nvarchar(1000) COLLATE DATABASE_DEFAULT NULL, OwnerLocked bit NOT NULL DEFAULT 0,
                   NCand int NOT NULL DEFAULT 0, NDistinct int NOT NULL DEFAULT 0, HasDiffCand bit NOT NULL DEFAULT 0,
                   IsConflict bit NOT NULL DEFAULT 0, ChosenP int NULL, ChosenDisp nvarchar(1000) COLLATE DATABASE_DEFAULT NULL,
                   Status varchar(20) COLLATE DATABASE_DEFAULT NULL, Reason varchar(40) COLLATE DATABASE_DEFAULT NULL,
                   PRIMARY KEY (Yr, TableDefId, ColumnDefId, RowKey));
CREATE TABLE #Cand (Yr int NOT NULL, TableDefId int NOT NULL, ColumnDefId int NOT NULL, RowKey nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                    P int NOT NULL, RowId bigint NOT NULL, IsOwner bit NOT NULL, OwnerPeriod int NULL,
                    VK nvarchar(1400) COLLATE Latin1_General_BIN2 NOT NULL, Disp nvarchar(1000) COLLATE DATABASE_DEFAULT NULL,
                    RowModAt datetime2(7) NOT NULL, LastAt datetime2(3) NULL, Rn int NULL,
                    PRIMARY KEY (Yr, TableDefId, ColumnDefId, RowKey, P));
CREATE TABLE #Top2 (Yr int NOT NULL, TableDefId int NOT NULL, ColumnDefId int NOT NULL, RowKey nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                    Reason varchar(40) COLLATE DATABASE_DEFAULT NOT NULL, PRIMARY KEY (Yr, TableDefId, ColumnDefId, RowKey));
CREATE TABLE #Union (Yr int NOT NULL, TableDefId int NOT NULL, RowKey nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                     RowDefId int NULL, Ordinal int NOT NULL, PRIMARY KEY (Yr, TableDefId, RowKey));
CREATE TABLE #NewInst (PeriodKey int NOT NULL, TableDefId int NOT NULL, Yr int NOT NULL, NewId bigint NULL, PRIMARY KEY (PeriodKey, TableDefId));
CREATE TABLE #NewRows (PeriodKey int NOT NULL, TableDefId int NOT NULL, Yr int NOT NULL, InstId bigint NULL,
                       RowKey nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL, RowDefId int NULL, Ordinal int NOT NULL,
                       NewId bigint NULL, PRIMARY KEY (PeriodKey, TableDefId, RowKey));
CREATE TABLE #Write (PeriodKey int NOT NULL, RowId bigint NOT NULL, ColumnDefId int NOT NULL, TableDefId int NOT NULL, RowKey nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                     Disp nvarchar(1000) COLLATE DATABASE_DEFAULT NULL, OwnerLocked bit NOT NULL,
                     ValueString nvarchar(1000) COLLATE DATABASE_DEFAULT NULL, ValueNumeric decimal(34,16) NULL, ValueDate datetime2(3) NULL,
                     ValueBool bit NULL, ValueRegistryEntryId int NULL, ValueUnitId int NULL, PRIMARY KEY (PeriodKey, RowId, ColumnDefId));
CREATE TABLE #Purge (PeriodKey int NOT NULL, RowId bigint NOT NULL, ColumnDefId int NOT NULL, RowKey nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                     IsEmpty bit NOT NULL, Disp nvarchar(1000) COLLATE DATABASE_DEFAULT NULL, DocumentId bigint NOT NULL,
                     PRIMARY KEY (PeriodKey, RowId, ColumnDefId));

INSERT #Docs (DocumentId, ProjectId)
SELECT d.Id, d.ProjectId
FROM doc.Document d JOIN doc.Project pr ON pr.Id = d.ProjectId
WHERE pr.PeriodKind = 0 AND (@DocumentId IS NULL OR d.Id = @DocumentId);

DECLARE @Doc bigint, @Prj int;
DECLARE @cCopyCells int, @cGroups int, @cCopyPlanned int, @cConflicts int, @cStillEmpty int, @cNoPeriod int, @cLocked int,
        @cDelBlocked int, @cNewInst int, @cMirror int, @cAnchorRows int,
        @wCopies int, @wLocked int, @wRows int, @wInst int, @wPurged int, @pSkipLocked int, @pKeptDiff int, @n int;
DECLARE @Now datetime2(3);

DECLARE dc CURSOR LOCAL FAST_FORWARD FOR SELECT DocumentId, ProjectId FROM #Docs ORDER BY DocumentId;
OPEN dc;
FETCH NEXT FROM dc INTO @Doc, @Prj;
WHILE @@FETCH_STATUS = 0
BEGIN
    TRUNCATE TABLE #Inst; TRUNCATE TABLE #Row; TRUNCATE TABLE #Col; TRUNCATE TABLE #Anchor; TRUNCATE TABLE #Own;
    TRUNCATE TABLE #Cell; TRUNCATE TABLE #Grp; TRUNCATE TABLE #Cand; TRUNCATE TABLE #Top2; TRUNCATE TABLE #Union;
    TRUNCATE TABLE #NewInst; TRUNCATE TABLE #NewRows; TRUNCATE TABLE #Write; TRUNCATE TABLE #Purge;
    SELECT @wCopies = 0, @wLocked = 0, @wRows = 0, @wInst = 0, @wPurged = 0, @pSkipLocked = 0, @pKeptDiff = 0;

    IF @Mode <> 'Preview' BEGIN TRAN;
    BEGIN TRY
        ---------------------------------------------------------------------------------------
        -- A. instances / rows / columns / owners of the document
        ---------------------------------------------------------------------------------------
        INSERT #Inst (PeriodKey, InstId, TableDefId, Yr, SheetDefId)
        SELECT ti.PeriodKey, ti.Id, ti.TableDefId, ti.PeriodKey / 100, td.SheetDefId
        FROM doc.TableInstance ti
        JOIN cfg.TableDef td ON td.Id = ti.TableDefId
        WHERE ti.DocumentId = @Doc AND td.LayoutKind = 0 AND td.IsDeleted = 0
          AND (@Year IS NULL OR ti.PeriodKey / 100 = @Year);

        INSERT #Row (PeriodKey, RowId, InstId, TableDefId, Yr, RowKey, RowDefId, Ordinal, IsDeleted, ModifiedAt)
        SELECT r.PeriodKey, r.Id, r.TableInstanceId, i.TableDefId, i.Yr, r.RowKey, r.RowDefId, r.Ordinal, r.IsDeleted, r.ModifiedAt
        FROM #Inst i
        JOIN doc.TableRow r ON r.PeriodKey = i.PeriodKey AND r.TableInstanceId = i.InstId;

        INSERT #Col (TableDefId, ColumnDefId, ColCode, IsMonth, MonthNumber)
        SELECT c.TableDefId, c.Id, c.Code, c.IsMonthColumn, c.MonthNumber
        FROM cfg.ColumnDef c
        WHERE c.TableDefId IN (SELECT TableDefId FROM #Inst) AND c.IsDeleted = 0
          AND (@ColumnScope = 'All' OR c.IsMonthColumn = 0);

        INSERT #Anchor (Yr, AnchorPeriod)
        SELECT y.Yr, (SELECT MIN(p.PeriodKey) FROM doc.Period p WHERE p.ProjectId = @Prj AND p.PeriodKey / 100 = y.Yr)
        FROM (SELECT DISTINCT Yr FROM #Inst) y;

        INSERT #Own (Yr, TableDefId, ColumnDefId, IsMonth, SheetDefId, OwnerPeriod, HasPeriodRow)
        SELECT y.Yr, y.TableDefId, c.ColumnDefId, c.IsMonth, y.SheetDefId,
               CASE WHEN c.IsMonth = 1
                    THEN CASE WHEN c.MonthNumber BETWEEN 1 AND 12 THEN y.Yr * 100 + c.MonthNumber END
                    ELSE a.AnchorPeriod END,
               0
        FROM (SELECT DISTINCT Yr, TableDefId, SheetDefId FROM #Inst) y
        JOIN #Col c ON c.TableDefId = y.TableDefId
        LEFT JOIN #Anchor a ON a.Yr = y.Yr;

        UPDATE o SET HasPeriodRow = 1
        FROM #Own o
        WHERE o.OwnerPeriod IS NOT NULL
          AND EXISTS (SELECT 1 FROM doc.Period p WHERE p.ProjectId = @Prj AND p.PeriodKey = o.OwnerPeriod);

        ---------------------------------------------------------------------------------------
        -- B. cells of the input columns (owner cell may be IsCalculated = 1; copies never)
        ---------------------------------------------------------------------------------------
        INSERT #Cell (Yr, TableDefId, ColumnDefId, RowKey, P, RowId, InstId, IsEmpty, IsCalc, RowModAt, Disp, VK,
                      ValueString, ValueNumeric, ValueDate, ValueBool, ValueRegistryEntryId, ValueUnitId)
        SELECT r.Yr, r.TableDefId, cv.ColumnDefId, r.RowKey, cv.PeriodKey, r.RowId, r.InstId, cv.IsEmpty, cv.IsCalculated, r.ModifiedAt,
               CAST(COALESCE(cv.ValueString, n.s, CONVERT(nvarchar(30), cv.ValueDate, 126),
                             CASE cv.ValueBool WHEN 1 THEN N'true' WHEN 0 THEN N'false' END,
                             CONVERT(nvarchar(20), cv.ValueRegistryEntryId), CONVERT(nvarchar(20), cv.ValueUnitId)) AS nvarchar(1000)),
               CONCAT(ISNULL(cv.ValueString, N''), N'|', ISNULL(a.t, N''), N'|', ISNULL(CONVERT(nvarchar(30), cv.ValueDate, 126), N''), N'|',
                      ISNULL(CONVERT(nvarchar(1), cv.ValueBool), N''), N'|', ISNULL(CONVERT(nvarchar(20), cv.ValueRegistryEntryId), N''), N'|',
                      ISNULL(CONVERT(nvarchar(20), cv.ValueUnitId), N'')),
               cv.ValueString, cv.ValueNumeric, cv.ValueDate, cv.ValueBool, cv.ValueRegistryEntryId, cv.ValueUnitId
        FROM #Row r
        JOIN #Own o ON o.Yr = r.Yr AND o.TableDefId = r.TableDefId
        JOIN doc.CellValue cv ON cv.PeriodKey = r.PeriodKey AND cv.TableRowId = r.RowId AND cv.ColumnDefId = o.ColumnDefId
        CROSS APPLY (SELECT t = CONVERT(nvarchar(60), cv.ValueNumeric)) a
        CROSS APPLY (SELECT z = REPLACE(RTRIM(REPLACE(a.t, N'0', N' ')), N' ', N'0')) z
        CROSS APPLY (SELECT s = CASE WHEN a.t IS NULL THEN NULL
                                     WHEN CHARINDEX(N'.', a.t) = 0 THEN a.t
                                     WHEN RIGHT(z.z, 1) = N'.' THEN LEFT(z.z, LEN(z.z) - 1)
                                     ELSE z.z END) n
        WHERE r.IsDeleted = 0 AND (cv.IsCalculated = 0 OR cv.PeriodKey = o.OwnerPeriod);

        ---------------------------------------------------------------------------------------
        -- C. groups (doc, year, table, column, RowKey) and the owner state of each
        ---------------------------------------------------------------------------------------
        INSERT #Grp (Yr, TableDefId, ColumnDefId, RowKey, SheetDefId, OwnerPeriod, HasPeriodRow)
        SELECT DISTINCT c.Yr, c.TableDefId, c.ColumnDefId, c.RowKey, o.SheetDefId, o.OwnerPeriod, o.HasPeriodRow
        FROM #Cell c
        JOIN #Own o ON o.Yr = c.Yr AND o.TableDefId = c.TableDefId AND o.ColumnDefId = c.ColumnDefId;

        UPDATE g SET OwnerCellExists = 1,
                     OwnerNonEmpty = CASE WHEN oc.IsEmpty = 0 THEN 1 ELSE 0 END,
                     OwnerVK = CASE WHEN oc.IsEmpty = 0 THEN oc.VK END,
                     OwnerDisp = CASE WHEN oc.IsEmpty = 0 THEN oc.Disp END
        FROM #Grp g
        JOIN #Cell oc ON oc.Yr = g.Yr AND oc.TableDefId = g.TableDefId AND oc.ColumnDefId = g.ColumnDefId
                     AND oc.RowKey = g.RowKey AND oc.P = g.OwnerPeriod;

        UPDATE g SET OwnerRowId = r.RowId
        FROM #Grp g
        JOIN #Row r ON r.PeriodKey = g.OwnerPeriod AND r.TableDefId = g.TableDefId AND r.RowKey = g.RowKey AND r.IsDeleted = 0;

        UPDATE g SET OwnerRowDeleted = 1
        FROM #Grp g
        WHERE g.OwnerRowId IS NULL AND g.OwnerPeriod IS NOT NULL
          AND EXISTS (SELECT 1 FROM #Row r WHERE r.PeriodKey = g.OwnerPeriod AND r.TableDefId = g.TableDefId AND r.RowKey = g.RowKey AND r.IsDeleted = 1);

        UPDATE g SET OwnerLocked = 1
        FROM #Grp g
        WHERE g.OwnerPeriod IS NOT NULL
          AND (EXISTS (SELECT 1 FROM wf.ApprovalState s
                       WHERE s.DocumentId = @Doc AND s.SheetDefId = g.SheetDefId AND s.PeriodKey = g.OwnerPeriod AND s.Status IN (1, 2))
               OR EXISTS (SELECT 1 FROM doc.Period p WHERE p.ProjectId = @Prj AND p.PeriodKey = g.OwnerPeriod AND p.State = 3));

        -- candidates (copies) + the owner cell, with the audit "last entered" timestamp
        INSERT #Cand (Yr, TableDefId, ColumnDefId, RowKey, P, RowId, IsOwner, OwnerPeriod, VK, Disp, RowModAt)
        SELECT c.Yr, c.TableDefId, c.ColumnDefId, c.RowKey, c.P, c.RowId, CASE WHEN c.P = g.OwnerPeriod THEN 1 ELSE 0 END,
               g.OwnerPeriod, c.VK, c.Disp, c.RowModAt
        FROM #Cell c
        JOIN #Grp g ON g.Yr = c.Yr AND g.TableDefId = c.TableDefId AND g.ColumnDefId = c.ColumnDefId AND g.RowKey = c.RowKey
        WHERE c.IsEmpty = 0 AND (c.IsCalc = 0 OR c.P = g.OwnerPeriod);

        UPDATE cd SET LastAt = x.mx
        FROM #Cand cd
        CROSS APPLY (SELECT MAX(a.ChangedAt) AS mx
                     FROM aud.CellChange a
                     WHERE a.DocumentId = @Doc AND a.PeriodKey = cd.P AND a.TableRowId = cd.RowId AND a.ColumnDefId = cd.ColumnDefId
                       AND a.Origin <> N'Recalculation' AND a.NewValue IS NOT NULL) x;

        ;WITH r AS (
            SELECT Rn, ROW_NUMBER() OVER (PARTITION BY Yr, TableDefId, ColumnDefId, RowKey
                                          ORDER BY CASE WHEN LastAt IS NULL THEN 1 ELSE 0 END, LastAt DESC, RowModAt DESC,
                                                   ABS(P - ISNULL(OwnerPeriod, P)) ASC, P ASC) AS x
            FROM #Cand WHERE IsOwner = 0)
        UPDATE r SET Rn = x;

        UPDATE g SET NCand = s.n, NDistinct = s.d
        FROM #Grp g
        JOIN (SELECT Yr, TableDefId, ColumnDefId, RowKey, COUNT(*) n, COUNT(DISTINCT VK) d
              FROM #Cand WHERE IsOwner = 0 GROUP BY Yr, TableDefId, ColumnDefId, RowKey) s
          ON s.Yr = g.Yr AND s.TableDefId = g.TableDefId AND s.ColumnDefId = g.ColumnDefId AND s.RowKey = g.RowKey;

        UPDATE g SET HasDiffCand = 1
        FROM #Grp g
        WHERE g.OwnerNonEmpty = 1
          AND EXISTS (SELECT 1 FROM #Cand k WHERE k.Yr = g.Yr AND k.TableDefId = g.TableDefId AND k.ColumnDefId = g.ColumnDefId
                                              AND k.RowKey = g.RowKey AND k.IsOwner = 0 AND k.VK <> g.OwnerVK);

        UPDATE g SET ChosenP = k.P, ChosenDisp = k.Disp
        FROM #Grp g
        JOIN #Cand k ON k.Yr = g.Yr AND k.TableDefId = g.TableDefId AND k.ColumnDefId = g.ColumnDefId AND k.RowKey = g.RowKey AND k.Rn = 1;

        UPDATE #Grp SET IsConflict = CASE WHEN OwnerNonEmpty = 1 THEN HasDiffCand WHEN NDistinct >= 2 THEN 1 ELSE 0 END;

        -- why the winner won (owner empty, >= 2 distinct values)
        INSERT #Top2 (Yr, TableDefId, ColumnDefId, RowKey, Reason)
        SELECT g.Yr, g.TableDefId, g.ColumnDefId, g.RowKey,
               CASE WHEN ISNULL(w.LastAt, '1900-01-01') <> ISNULL(o2.LastAt, '1900-01-01') THEN 'ByAuditChangedAt'
                    WHEN w.RowModAt <> o2.RowModAt THEN 'ByRowModifiedAt'
                    WHEN ABS(w.P - w.OwnerPeriod) <> ABS(o2.P - o2.OwnerPeriod) THEN 'ByNearestPeriod'
                    ELSE 'ByLowerPeriod' END
        FROM #Grp g
        JOIN #Cand w ON w.Yr = g.Yr AND w.TableDefId = g.TableDefId AND w.ColumnDefId = g.ColumnDefId AND w.RowKey = g.RowKey AND w.Rn = 1
        CROSS APPLY (SELECT TOP (1) k.* FROM #Cand k
                     WHERE k.Yr = g.Yr AND k.TableDefId = g.TableDefId AND k.ColumnDefId = g.ColumnDefId AND k.RowKey = g.RowKey
                       AND k.IsOwner = 0 AND k.VK <> w.VK ORDER BY k.Rn) o2
        WHERE g.OwnerNonEmpty = 0 AND g.NDistinct >= 2;

        UPDATE g SET Status = CASE
                WHEN g.NCand = 0 AND g.OwnerNonEmpty = 0 THEN 'StillEmpty'
                WHEN g.NCand = 0 THEN 'OwnerOnly'
                WHEN g.OwnerPeriod IS NULL OR g.HasPeriodRow = 0 THEN 'NoPeriodRow'
                WHEN g.OwnerNonEmpty = 1 THEN 'OwnerKept'
                WHEN g.OwnerRowDeleted = 1 THEN 'BlockedDeletedRow'
                WHEN g.OwnerLocked = 1 AND @WriteLocked = 0 THEN 'LockedTarget'
                ELSE 'CopyPlanned' END,
            Reason = CASE
                WHEN g.OwnerNonEmpty = 1 THEN CASE WHEN g.HasDiffCand = 1 THEN 'OwnerKept' ELSE 'OwnerOnly' END
                WHEN g.NDistinct >= 2 THEN ISNULL(t.Reason, 'ByLowerPeriod')
                WHEN g.NCand > 0 THEN 'SingleValue'
                ELSE NULL END
        FROM #Grp g
        LEFT JOIN #Top2 t ON t.Yr = g.Yr AND t.TableDefId = g.TableDefId AND t.ColumnDefId = g.ColumnDefId AND t.RowKey = g.RowKey;

        ---------------------------------------------------------------------------------------
        -- D. composition of rows: union of live RowKeys -> missing rows in every existing instance
        ---------------------------------------------------------------------------------------
        INSERT #Union (Yr, TableDefId, RowKey, RowDefId, Ordinal)
        SELECT Yr, TableDefId, RowKey, RowDefId, Ordinal
        FROM (SELECT r.Yr, r.TableDefId, r.RowKey, r.RowDefId, r.Ordinal,
                     ROW_NUMBER() OVER (PARTITION BY r.Yr, r.TableDefId, r.RowKey
                                        ORDER BY CASE WHEN r.PeriodKey = a.AnchorPeriod THEN 0 ELSE 1 END, r.PeriodKey) AS rn
              FROM #Row r LEFT JOIN #Anchor a ON a.Yr = r.Yr
              WHERE r.IsDeleted = 0) x
        WHERE rn = 1;

        INSERT #NewInst (PeriodKey, TableDefId, Yr)
        SELECT DISTINCT g.OwnerPeriod, g.TableDefId, g.Yr
        FROM #Grp g
        WHERE g.Status = 'CopyPlanned'
          AND NOT EXISTS (SELECT 1 FROM #Inst i WHERE i.PeriodKey = g.OwnerPeriod AND i.TableDefId = g.TableDefId);

        INSERT #NewRows (PeriodKey, TableDefId, Yr, InstId, RowKey, RowDefId, Ordinal)
        SELECT t.PeriodKey, t.TableDefId, t.Yr, t.InstId, u.RowKey, u.RowDefId, u.Ordinal
        FROM (SELECT PeriodKey, InstId, TableDefId, Yr FROM #Inst
              UNION ALL
              SELECT PeriodKey, NULL, TableDefId, Yr FROM #NewInst) t
        JOIN #Union u ON u.Yr = t.Yr AND u.TableDefId = t.TableDefId
        WHERE NOT EXISTS (SELECT 1 FROM #Row r WHERE r.PeriodKey = t.PeriodKey AND r.TableDefId = t.TableDefId AND r.RowKey = u.RowKey);

        ---------------------------------------------------------------------------------------
        -- counters + detail rows (these describe the PLAN, identical for Preview and Apply)
        ---------------------------------------------------------------------------------------
        SELECT @cCopyCells = ISNULL((SELECT COUNT(*) FROM #Cand WHERE IsOwner = 0), 0),
               @cGroups = (SELECT COUNT(*) FROM #Grp),
               @cCopyPlanned = (SELECT COUNT(*) FROM #Grp WHERE Status = 'CopyPlanned'),
               @cConflicts = (SELECT COUNT(*) FROM #Grp WHERE IsConflict = 1),
               @cStillEmpty = (SELECT COUNT(*) FROM #Grp WHERE Status = 'StillEmpty'),
               @cNoPeriod = (SELECT COUNT(*) FROM #Grp WHERE Status = 'NoPeriodRow'),
               @cLocked = (SELECT COUNT(*) FROM #Grp WHERE Status = 'LockedTarget'),
               @cDelBlocked = (SELECT COUNT(*) FROM #Grp WHERE Status = 'BlockedDeletedRow'),
               @cNewInst = (SELECT COUNT(*) FROM #NewInst),
               @cMirror = (SELECT COUNT(*) FROM #NewRows nr WHERE NOT EXISTS (SELECT 1 FROM #Anchor a WHERE a.Yr = nr.Yr AND a.AnchorPeriod = nr.PeriodKey)),
               @cAnchorRows = (SELECT COUNT(*) FROM #NewRows nr WHERE EXISTS (SELECT 1 FROM #Anchor a WHERE a.Yr = nr.Yr AND a.AnchorPeriod = nr.PeriodKey));

        INSERT #AccConflict (DocumentId, Yr, TableCode, RowKey, ColumnCode, OwnerPeriod, ValuesByPeriod, Chosen, Reason)
        SELECT @Doc, g.Yr, td.Code, g.RowKey, col.Code, g.OwnerPeriod, v.vals,
               CASE WHEN g.Status = 'CopyPlanned' OR g.Status = 'LockedTarget' OR g.Status = 'BlockedDeletedRow' THEN CONCAT(N'p', g.ChosenP, N'=', g.ChosenDisp)
                    WHEN g.OwnerNonEmpty = 1 THEN CONCAT(N'owner p', g.OwnerPeriod, N'=', g.OwnerDisp)
                    ELSE N'(none: ' + g.Status + N')' END,
               g.Reason
        FROM #Grp g
        JOIN cfg.TableDef td ON td.Id = g.TableDefId
        JOIN cfg.ColumnDef col ON col.Id = g.ColumnDefId
        CROSS APPLY (SELECT STRING_AGG(CAST(CONCAT(CASE WHEN k.IsOwner = 1 THEN N'*' ELSE N'' END, k.P, N'=', k.Disp, N' @',
                                                   ISNULL(CONVERT(varchar(23), k.LastAt, 121), 'n/a')) AS nvarchar(max)), N'; ')
                                      WITHIN GROUP (ORDER BY k.P) AS vals
                     FROM #Cand k
                     WHERE k.Yr = g.Yr AND k.TableDefId = g.TableDefId AND k.ColumnDefId = g.ColumnDefId AND k.RowKey = g.RowKey) v
        WHERE g.IsConflict = 1;

        INSERT #AccNoPeriod (DocumentId, Yr, TableCode, ColumnCode, ExpectedOwnerPeriod, Groups)
        SELECT @Doc, g.Yr, td.Code, col.Code, g.OwnerPeriod, COUNT(*)
        FROM #Grp g
        JOIN cfg.TableDef td ON td.Id = g.TableDefId
        JOIN cfg.ColumnDef col ON col.Id = g.ColumnDefId
        WHERE g.Status = 'NoPeriodRow'
        GROUP BY g.Yr, td.Code, col.Code, g.OwnerPeriod;

        INSERT #AccLocked (DocumentId, Yr, TableCode, RowKey, ColumnCode, OwnerPeriod, Chosen, Reason)
        SELECT @Doc, g.Yr, td.Code, g.RowKey, col.Code, g.OwnerPeriod, CONCAT(N'p', g.ChosenP, N'=', g.ChosenDisp), N'lockedTargetNeedsDecision'
        FROM #Grp g
        JOIN cfg.TableDef td ON td.Id = g.TableDefId
        JOIN cfg.ColumnDef col ON col.Id = g.ColumnDefId
        WHERE g.Status = 'LockedTarget';

        ---------------------------------------------------------------------------------------
        -- E. APPLY: instances, rows, owner cells, audit
        ---------------------------------------------------------------------------------------
        IF @Mode = 'Apply'
        BEGIN
            SET @Now = CONVERT(datetime2(3), SYSUTCDATETIME());

            IF EXISTS (SELECT 1 FROM #NewInst)
            BEGIN
                CREATE TABLE #NI (PeriodKey int NOT NULL, TableDefId int NOT NULL, NewId bigint NOT NULL);
                INSERT #NI (PeriodKey, TableDefId, NewId)
                SELECT PeriodKey, TableDefId, NEXT VALUE FOR doc.TableInstanceSeq FROM #NewInst;
                UPDATE ni SET NewId = x.NewId FROM #NewInst ni JOIN #NI x ON x.PeriodKey = ni.PeriodKey AND x.TableDefId = ni.TableDefId;
                DROP TABLE #NI;

                INSERT doc.TableInstance (PeriodKey, Id, DocumentId, TableDefId, CreatedAt, ModifiedAt)
                SELECT PeriodKey, NewId, @Doc, TableDefId, @Now, @Now FROM #NewInst;
                SET @wInst = @@ROWCOUNT;
            END;

            IF EXISTS (SELECT 1 FROM #NewRows)
            BEGIN
                CREATE TABLE #NR (PeriodKey int NOT NULL, TableDefId int NOT NULL, RowKey nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL, NewId bigint NOT NULL);
                INSERT #NR (PeriodKey, TableDefId, RowKey, NewId)
                SELECT PeriodKey, TableDefId, RowKey, NEXT VALUE FOR doc.TableRowSeq FROM #NewRows;
                UPDATE nr SET NewId = x.NewId
                FROM #NewRows nr JOIN #NR x ON x.PeriodKey = nr.PeriodKey AND x.TableDefId = nr.TableDefId AND x.RowKey = nr.RowKey;
                DROP TABLE #NR;

                INSERT doc.TableRow (PeriodKey, Id, TableInstanceId, RowKey, RowDefId, Ordinal, IsDeleted, IsOrphaned, ModifiedAt)
                SELECT nr.PeriodKey, nr.NewId, COALESCE(nr.InstId, ni.NewId), nr.RowKey, nr.RowDefId, nr.Ordinal, 0, 0, @Now
                FROM #NewRows nr
                LEFT JOIN #NewInst ni ON ni.PeriodKey = nr.PeriodKey AND ni.TableDefId = nr.TableDefId
                WHERE COALESCE(nr.InstId, ni.NewId) IS NOT NULL;
                SET @wRows = @@ROWCOUNT;
            END;

            INSERT #Write (PeriodKey, RowId, ColumnDefId, TableDefId, RowKey, Disp, OwnerLocked,
                           ValueString, ValueNumeric, ValueDate, ValueBool, ValueRegistryEntryId, ValueUnitId)
            SELECT g.OwnerPeriod, COALESCE(g.OwnerRowId, nr.NewId), g.ColumnDefId, g.TableDefId, g.RowKey, c.Disp, g.OwnerLocked,
                   c.ValueString, c.ValueNumeric, c.ValueDate, c.ValueBool, c.ValueRegistryEntryId, c.ValueUnitId
            FROM #Grp g
            JOIN #Cell c ON c.Yr = g.Yr AND c.TableDefId = g.TableDefId AND c.ColumnDefId = g.ColumnDefId AND c.RowKey = g.RowKey AND c.P = g.ChosenP
            LEFT JOIN #NewRows nr ON nr.PeriodKey = g.OwnerPeriod AND nr.TableDefId = g.TableDefId AND nr.RowKey = g.RowKey
            WHERE g.Status = 'CopyPlanned' AND COALESCE(g.OwnerRowId, nr.NewId) IS NOT NULL;

            -- "MERGE" of the owner cell: an empty owner cell is filled, an absent one inserted
            UPDATE cv SET ValueString = w.ValueString, ValueNumeric = w.ValueNumeric, ValueDate = w.ValueDate, ValueBool = w.ValueBool,
                          ValueRegistryEntryId = w.ValueRegistryEntryId, ValueUnitId = w.ValueUnitId, IsEmpty = 0, IsCalculated = 0
            FROM doc.CellValue cv
            JOIN #Write w ON w.PeriodKey = cv.PeriodKey AND w.RowId = cv.TableRowId AND w.ColumnDefId = cv.ColumnDefId
            WHERE cv.IsEmpty = 1;
            SET @n = @@ROWCOUNT;

            INSERT doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString, ValueNumeric, ValueDate, ValueBool,
                                  ValueRegistryEntryId, ValueUnitId, IsCalculated, IsEmpty)
            SELECT w.PeriodKey, w.RowId, w.ColumnDefId, w.TableDefId, w.ValueString, w.ValueNumeric, w.ValueDate, w.ValueBool,
                   w.ValueRegistryEntryId, w.ValueUnitId, 0, 0
            FROM #Write w
            WHERE NOT EXISTS (SELECT 1 FROM doc.CellValue cv WHERE cv.PeriodKey = w.PeriodKey AND cv.TableRowId = w.RowId AND cv.ColumnDefId = w.ColumnDefId);
            SET @wCopies = @n + @@ROWCOUNT;
            SELECT @wLocked = COUNT(*) FROM #Write WHERE OwnerLocked = 1;

            INSERT aud.CellChange (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, NewValue,
                                   ChangedByUserId, Origin, IsLateEdit, CorrelationId, IsOutOfWindow, IsOverwrite)
            SELECT @Now, w.PeriodKey, @Doc, w.RowId, w.RowKey, w.ColumnDefId, NULL, w.Disp,
                   @UserId, N'Migration', 0, @RunId, 0, 0
            FROM #Write w;

            UPDATE r SET ModifiedAt = @Now
            FROM doc.TableRow r JOIN (SELECT DISTINCT PeriodKey, RowId FROM #Write) w ON w.PeriodKey = r.PeriodKey AND w.RowId = r.Id;
        END;

        ---------------------------------------------------------------------------------------
        -- F. PURGE: copies (p <> owner) equal to a non-empty owner value; locked copies and different values stay
        ---------------------------------------------------------------------------------------
        IF @Mode = 'Purge'
        BEGIN
            SET @Now = CONVERT(datetime2(3), SYSUTCDATETIME());

            INSERT #Purge (PeriodKey, RowId, ColumnDefId, RowKey, IsEmpty, Disp, DocumentId)
            SELECT c.P, c.RowId, c.ColumnDefId, c.RowKey, c.IsEmpty, c.Disp, @Doc
            FROM #Cell c
            JOIN #Grp g ON g.Yr = c.Yr AND g.TableDefId = c.TableDefId AND g.ColumnDefId = c.ColumnDefId AND g.RowKey = c.RowKey
            WHERE c.P <> g.OwnerPeriod AND c.IsCalc = 0 AND g.OwnerNonEmpty = 1 AND (c.IsEmpty = 1 OR c.VK = g.OwnerVK)
              AND NOT EXISTS (SELECT 1 FROM wf.ApprovalState s
                              WHERE s.DocumentId = @Doc AND s.SheetDefId = g.SheetDefId AND s.PeriodKey = c.P AND s.Status IN (1, 2))
              AND NOT EXISTS (SELECT 1 FROM doc.Period p WHERE p.ProjectId = @Prj AND p.PeriodKey = c.P AND p.State = 3);

            SELECT @pSkipLocked = COUNT(*)
            FROM #Cell c
            JOIN #Grp g ON g.Yr = c.Yr AND g.TableDefId = c.TableDefId AND g.ColumnDefId = c.ColumnDefId AND g.RowKey = c.RowKey
            WHERE c.P <> g.OwnerPeriod AND c.IsCalc = 0 AND g.OwnerNonEmpty = 1 AND (c.IsEmpty = 1 OR c.VK = g.OwnerVK)
              AND NOT EXISTS (SELECT 1 FROM #Purge x WHERE x.PeriodKey = c.P AND x.RowId = c.RowId AND x.ColumnDefId = c.ColumnDefId);

            SELECT @pKeptDiff = COUNT(*)
            FROM #Cell c
            JOIN #Grp g ON g.Yr = c.Yr AND g.TableDefId = c.TableDefId AND g.ColumnDefId = c.ColumnDefId AND g.RowKey = c.RowKey
            WHERE c.P <> g.OwnerPeriod AND c.IsCalc = 0 AND g.OwnerNonEmpty = 1 AND c.IsEmpty = 0 AND c.VK <> g.OwnerVK;

            INSERT aud.CellChange (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, NewValue,
                                   ChangedByUserId, Origin, IsLateEdit, CorrelationId, IsOutOfWindow, IsOverwrite)
            SELECT @Now, PeriodKey, @Doc, RowId, RowKey, ColumnDefId, Disp, NULL, @UserId, N'Migration', 0, @RunId, 0, 0
            FROM #Purge WHERE IsEmpty = 0;

            DELETE cv
            FROM doc.CellValue cv
            JOIN #Purge p ON p.PeriodKey = cv.PeriodKey AND p.RowId = cv.TableRowId AND p.ColumnDefId = cv.ColumnDefId;
            SET @wPurged = @@ROWCOUNT;
        END;

        IF @Mode <> 'Preview' COMMIT;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK;
        THROW;
    END CATCH;

    INSERT #AccSum (DocumentId, copyCells, groupsTotal, copyPlanned, conflicts, stillEmpty, noPeriodRow, lockedTargets, deletedRowBlocked,
                    ownerInstancesToCreate, mirrorRowsToCreate, anchorRowsToCreate, copiesWritten, writtenToLocked, rowsCreated,
                    instancesCreated, copiesPurged, purgeSkippedLocked, purgeKeptDifferent)
    VALUES (@Doc, @cCopyCells, @cGroups, @cCopyPlanned, @cConflicts, @cStillEmpty, @cNoPeriod, @cLocked, @cDelBlocked,
            @cNewInst, @cMirror, @cAnchorRows, @wCopies, @wLocked, @wRows, @wInst, @wPurged, @pSkipLocked, @pKeptDiff);

    FETCH NEXT FROM dc INTO @Doc, @Prj;
END;
CLOSE dc; DEALLOCATE dc;

-------------------------------------------------------------------------------------------------
-- result sets
-------------------------------------------------------------------------------------------------
SELECT Mode = @Mode, RunId = @RunId, DatabaseName = DB_NAME(), Documents = COUNT(*),
       copyCells = ISNULL(SUM(copyCells), 0), groupsTotal = ISNULL(SUM(groupsTotal), 0),
       copyPlanned = ISNULL(SUM(copyPlanned), 0), conflicts = ISNULL(SUM(conflicts), 0), stillEmpty = ISNULL(SUM(stillEmpty), 0),
       noPeriodRow = ISNULL(SUM(noPeriodRow), 0), lockedTargets = ISNULL(SUM(lockedTargets), 0),
       deletedRowBlocked = ISNULL(SUM(deletedRowBlocked), 0), ownerInstancesToCreate = ISNULL(SUM(ownerInstancesToCreate), 0),
       mirrorRowsToCreate = ISNULL(SUM(mirrorRowsToCreate), 0), anchorRowsToCreate = ISNULL(SUM(anchorRowsToCreate), 0),
       copiesWritten = ISNULL(SUM(copiesWritten), 0), writtenToLocked = ISNULL(SUM(writtenToLocked), 0),
       rowsCreated = ISNULL(SUM(rowsCreated), 0), instancesCreated = ISNULL(SUM(instancesCreated), 0),
       copiesPurged = ISNULL(SUM(copiesPurged), 0), purgeSkippedLocked = ISNULL(SUM(purgeSkippedLocked), 0),
       purgeKeptDifferent = ISNULL(SUM(purgeKeptDifferent), 0)
FROM #AccSum;

SELECT * FROM #AccSum ORDER BY DocumentId;
SELECT * FROM #AccConflict ORDER BY DocumentId, Yr, TableCode, ColumnCode, RowKey;
SELECT * FROM #AccNoPeriod ORDER BY DocumentId, Yr, TableCode, ColumnCode;
SELECT * FROM #AccLocked ORDER BY DocumentId, Yr, TableCode, ColumnCode, RowKey;
