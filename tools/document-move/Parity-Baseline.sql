/*
  b14 document-move (D-579, design R2-4): parity set, READ-ONLY. Run before (W-2) and after (W-7, W-9) the move and diff
  the two outputs (Metric, Value); every line must match except the ones marked "changes by design".
    sqlcmd -S localhost -E -d <db> -i Parity-Baseline.sql -v Land=96 Land2=100 Hse400=98 Hse401=93 Year=2026 -W -s "|" -o before.txt
  The relocation itself changes WHERE doc 98 stores its month cells (owner periods), never the values: the
  "hse400.owner.*" lines are computed per (row key, month) independent of the storage period, so they match.
  ASCII only on purpose.
*/
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;

DECLARE @Land bigint = $(Land), @Land2 bigint = $(Land2), @Hse400 bigint = $(Hse400), @Hse401 bigint = $(Hse401), @Year int = $(Year);
DECLARE @M TABLE (Ord int IDENTITY, Metric nvarchar(200), Value nvarchar(400));

-- Land 96 / 100: stored current results (AC-0: 510 for 96) and their checksum; issues; formula errors
INSERT @M (Metric, Value)
SELECT CONCAT(N'land.', d.Id, N'.currentResults'), CONCAT(COUNT(r.Id), N' / ', ISNULL(SUM(CASE WHEN r.Id IS NULL THEN 0 ELSE CAST(CHECKSUM(r.PeriodKey, r.SourceRowKey, r.OutputCode, r.Value) AS bigint) END), 0))
FROM (VALUES (@Land), (@Land2)) d(Id)
LEFT JOIN calc.CalculationRun ru ON ru.DocumentId = d.Id AND ru.Status = N'Current'
LEFT JOIN calc.CalculationResult r ON r.CalculationRunId = ru.Id
GROUP BY d.Id;
INSERT @M (Metric, Value) SELECT CONCAT(N'land.', @Land, N'.calculationIssues'), COUNT(*) FROM calc.CalculationIssue WHERE DocumentId = @Land;
INSERT @M (Metric, Value) SELECT CONCAT(N'land.', @Land, N'.formulaErrors'), COUNT(*) FROM calc.FormulaError WHERE DocumentId = @Land;

-- HSE400 98: month input values per (row key, month column) wherever stored (relocation moves storage, not values);
-- the same value in several periods counts once (copies), different values per key are reported separately
;WITH c AS (
    SELECT r.RowKey, col.Code AS ColumnCode, col.MonthNumber AS Mon, cv.ValueNumeric, cv.ValueString, ti.PeriodKey
    FROM doc.TableInstance ti
    JOIN doc.TableRow r ON r.PeriodKey = ti.PeriodKey AND r.TableInstanceId = ti.Id AND r.IsDeleted = 0
    JOIN doc.CellValue cv ON cv.PeriodKey = r.PeriodKey AND cv.TableRowId = r.Id AND cv.IsEmpty = 0 AND cv.IsCalculated = 0
    JOIN cfg.ColumnDef col ON col.Id = cv.ColumnDefId AND col.IsMonthColumn = 1
    WHERE ti.DocumentId = @Hse400 AND ti.PeriodKey / 100 = @Year),
k AS (SELECT DISTINCT RowKey, ColumnCode, ValueNumeric, ValueString FROM c)
INSERT @M (Metric, Value)
SELECT N'hse400.owner.monthValues', CONCAT(COUNT(*), N' / ', ISNULL(SUM(CAST(CHECKSUM(RowKey, ColumnCode, ValueNumeric, ValueString) AS bigint)), 0)) FROM k
UNION ALL
SELECT N'hse400.keysWithDifferentCopies', CAST(COUNT(*) AS nvarchar(400)) FROM (SELECT RowKey, ColumnCode FROM k GROUP BY RowKey, ColumnCode HAVING COUNT(*) > 1) x
UNION ALL
SELECT N'hse400.storedCells (changes by design: Jan copies removed)', CAST(COUNT(*) AS nvarchar(400)) FROM c
UNION ALL
SELECT N'hse400.nonOwnerCopies (0 after the move)', CAST(COUNT(*) AS nvarchar(400)) FROM c
WHERE EXISTS (SELECT 1 FROM doc.Document d JOIN doc.Project p ON p.Id = d.ProjectId WHERE d.Id = @Hse400 AND p.PeriodKind = 0)
  AND c.PeriodKey <> @Year * 100 + c.Mon;

-- HSE400 98: F_ values of the current runs per (row key, output code) - the composition Land reads (design M4 (1))
INSERT @M (Metric, Value)
SELECT N'hse400.currentF', CONCAT(COUNT(*), N' / ', ISNULL(SUM(CAST(CHECKSUM(x.SourceRowKey, x.OutputCode, x.Value) AS bigint)), 0))
FROM (SELECT DISTINCT r.SourceRowKey, r.OutputCode, r.Value
      FROM calc.CalculationRun ru JOIN calc.CalculationResult r ON r.CalculationRunId = ru.Id
      WHERE ru.DocumentId = @Hse400 AND ru.Status = N'Current' AND r.SourceRowKey LIKE N'F[_]%') x;

-- HSE401 93: all cells (input + formula results) - the move must not change one value (N1: v14 before the move)
INSERT @M (Metric, Value)
SELECT CONCAT(N'hse401.cells.', CASE WHEN cv.IsCalculated = 1 THEN N'calculated' ELSE N'input' END),
       CONCAT(COUNT(*), N' / ', ISNULL(SUM(CAST(CHECKSUM(r.RowKey, cv.ColumnDefId, cv.ValueNumeric, cv.ValueString, cv.ValueDate, cv.ValueRegistryEntryId) AS bigint)), 0))
FROM doc.TableInstance ti
JOIN doc.TableRow r ON r.PeriodKey = ti.PeriodKey AND r.TableInstanceId = ti.Id AND r.IsDeleted = 0
JOIN doc.CellValue cv ON cv.PeriodKey = r.PeriodKey AND cv.TableRowId = r.Id AND cv.IsEmpty = 0
WHERE ti.DocumentId = @Hse401
GROUP BY cv.IsCalculated;

-- review H-1 / W-1: HSE401 v14 - formulas of the version doc 93 is on (period version of its project x template):
-- withPeriodDays must be 0, withYearLength must be 9 (H_RATE, H_RQ1..4_T/H)
DECLARE @V401 int = (SELECT doc.fn_TemplateVersionOf(d.ProjectId, COALESCE(d.TemplateId, v.TemplateId), @Year * 100 + 1)
                     FROM doc.Document d JOIN doc.Project p ON p.Id = d.ProjectId JOIN cfg.TemplateVersion v ON v.Id = p.TemplateVersionId
                     WHERE d.Id = @Hse401);
INSERT @M (Metric, Value)
SELECT N'hse401.version', CONCAT(@V401, N' ', (SELECT Version FROM cfg.TemplateVersion WHERE Id = @V401))
UNION ALL
SELECT N'hse401.formulas.withPeriodDays (must be 0)', CAST(COUNT(*) AS nvarchar(400))
FROM cfg.FormulaDef f JOIN cfg.TableDef td ON td.Id = f.TableDefId JOIN cfg.SheetDef s ON s.Id = td.SheetDefId
WHERE s.TemplateVersionId = @V401 AND f.IsDeleted = 0 AND f.Expression LIKE N'%[[]Period].Days%'
UNION ALL
SELECT N'hse401.formulas.withYearLength (must be 9)', CAST(COUNT(*) AS nvarchar(400))
FROM cfg.FormulaDef f JOIN cfg.TableDef td ON td.Id = f.TableDefId JOIN cfg.SheetDef s ON s.Id = td.SheetDefId
WHERE s.TemplateVersionId = @V401 AND f.IsDeleted = 0 AND f.Expression LIKE N'%[[]Period].Year [%] 4 = 0, 366, 365%';
-- one non-GOLD HSE400 document with instances in the year (H6) - must be @Hse400 alone
INSERT @M (Metric, Value)
SELECT N'hse400.workDocumentsOfYear', STRING_AGG(CAST(x.Id AS nvarchar(20)), N',')
FROM (SELECT DISTINCT d.Id FROM doc.Document d
      JOIN doc.Project p ON p.Id = d.ProjectId
      JOIN doc.TableInstance ti ON ti.DocumentId = d.Id AND ti.PeriodKey / 100 = @Year
      JOIN cfg.TableDef td ON td.Id = ti.TableDefId
      JOIN cfg.SheetDef s ON s.Id = td.SheetDefId
      JOIN cfg.TemplateVersion v ON v.Id = s.TemplateVersionId
      JOIN cfg.Template t ON t.Id = v.TemplateId
      WHERE t.Code = N'HSE400' AND p.Code <> N'HSE400_GOLD') x;

-- runs on a project other than their document's (M2) and the job queue (M2): both 0
INSERT @M (Metric, Value) SELECT N'projectMismatch', COUNT(*) FROM calc.CalculationRun r JOIN doc.Document d ON d.Id = r.DocumentId WHERE r.ProjectId <> d.ProjectId;
INSERT @M (Metric, Value) SELECT N'jobs.queuedOrRunning', COUNT(*) FROM itg.JobProgress WHERE State IN (N'Queued', N'Running');

-- rpt month views of HSE400 / HSE401 (D-573 "_m"): rows and checksum WITHOUT the period / project service columns
DECLARE @v sysname, @sql nvarchar(max), @cols nvarchar(max);
DECLARE vc CURSOR LOCAL FAST_FORWARD FOR
    SELECT v.name FROM sys.views v JOIN sys.schemas s ON s.schema_id = v.schema_id
    WHERE s.name = N'rpt' AND (v.name LIKE N'v[_]HSE400[_]%[_]m' OR v.name LIKE N'v[_]HSE401[_]%[_]m') ORDER BY v.name;
OPEN vc; FETCH NEXT FROM vc INTO @v;
WHILE @@FETCH_STATUS = 0
BEGIN
    SELECT @cols = STRING_AGG(CAST(QUOTENAME(c.name) AS nvarchar(max)), N', ')
    FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
    WHERE c.object_id = OBJECT_ID(N'rpt.' + QUOTENAME(@v)) AND c.name NOT IN (N'_PeriodKey', N'_ProjectId', N'_ProjectCode', N'_PeriodId')
      AND t.name NOT IN (N'xml', N'text', N'ntext', N'image', N'geography', N'geometry');
    SET @sql = N'SELECT @m = CONCAT(COUNT(*), N'' / '', ISNULL(SUM(CAST(BINARY_CHECKSUM(' + @cols + N') AS bigint)), 0)) FROM (SELECT DISTINCT '
             + @cols + N' FROM rpt.' + QUOTENAME(@v) + N') x;';
    DECLARE @val nvarchar(400);
    EXEC sp_executesql @sql, N'@m nvarchar(400) OUTPUT', @m = @val OUTPUT;
    INSERT @M (Metric, Value) VALUES (N'rpt.' + @v + N' (distinct rows without _PeriodKey/_Project*)', @val);
    FETCH NEXT FROM vc INTO @v;
END;
CLOSE vc; DEALLOCATE vc;

SELECT Metric, Value FROM @M ORDER BY Ord;
