-- tools/land-test-doc/verify-land-test-doc.sql
-- READ-ONLY check of the Land test document after a recalculation (SELECT only; no writes, no temp tables).
-- sqlcmd variables: DocumentId, PeriodKey. Output: one line per fact, tab separated: section, key, methodology, value.
--   RUN         latest run of (document, period): id, status, error text
--   CURRENT     the Current run id that the checks below use
--   RESULT      calc.CalculationResult rows of the Current run per source row key and methodology code
--   ISSUE       calc.CalculationIssue rows of the Current run (kind, row key, methodology code)
--   FAILEDSTEP  calc.CalculationStep rows with Value IS NULL (failed formula step) per source row key
--   MASKED      calc.CalculationStep rows with MaskedZero <> 0 per source row key
--   NOSUBST     per-substance template results (OutputCode like '%XXX%') with no SubstanceEntryId: the emission
--               is attributed to no pollutant (methodology version without a substance list)
--   BRANCHES    (row, substance, metric) groups with more than one written _typeN branch (double counting)
SET NOCOUNT ON;
DECLARE @doc int = $(DocumentId);
DECLARE @p int = $(PeriodKey);

SELECT TOP (1) 'RUN' AS section, CAST(r.Id AS nvarchar(30)) AS k, r.Status AS methodology, ISNULL(LEFT(REPLACE(REPLACE(r.ErrorMessage, CHAR(13), ' '), CHAR(10), ' '), 300), '') AS v
FROM calc.CalculationRun r
WHERE r.DocumentId = @doc AND r.PeriodKey = @p
ORDER BY r.Id DESC;

DECLARE @run bigint = (SELECT TOP (1) r.Id FROM calc.CalculationRun r WHERE r.DocumentId = @doc AND r.PeriodKey = @p AND r.Status = 'Current' ORDER BY r.Id DESC);
SELECT 'CURRENT' AS section, ISNULL(CAST(@run AS nvarchar(30)), '') AS k, '' AS methodology, '' AS v;

SELECT 'RESULT' AS section, res.SourceRowKey AS k, m.Code AS methodology, CAST(COUNT(*) AS nvarchar(30)) AS v
FROM calc.CalculationResult res
JOIN calc.MethodologyVersion mv ON mv.Id = res.MethodologyVersionId
JOIN calc.Methodology m ON m.Id = mv.MethodologyId
WHERE res.PeriodKey = @p AND res.CalculationRunId = @run AND res.DocumentId = @doc
GROUP BY res.SourceRowKey, m.Code;

IF OBJECT_ID(N'calc.CalculationIssue') IS NOT NULL
    EXEC sys.sp_executesql N'
        SELECT ''ISSUE'' AS section, ISNULL(i.RowKey, '''') AS k, ISNULL(m.Code, '''') AS methodology, CAST(i.Kind AS nvarchar(64)) AS v
        FROM calc.CalculationIssue i
        LEFT JOIN calc.Methodology m ON m.Id = i.MethodologyId
        WHERE i.DocumentId = @doc AND i.PeriodKey = @p AND i.CalculationRunId = @run;',
        N'@doc int, @p int, @run bigint', @doc = @doc, @p = @p, @run = @run;

SELECT 'FAILEDSTEP' AS section, ISNULL(s.SourceRowKey, '') AS k, '' AS methodology, CAST(COUNT(*) AS nvarchar(30)) AS v
FROM calc.CalculationStep s
WHERE s.PeriodKey = @p AND s.CalculationRunId = @run AND s.Value IS NULL
GROUP BY s.SourceRowKey;

SELECT 'MASKED' AS section, ISNULL(s.SourceRowKey, '') AS k, '' AS methodology, CAST(COUNT(*) AS nvarchar(30)) AS v
FROM calc.CalculationStep s
WHERE s.PeriodKey = @p AND s.CalculationRunId = @run AND s.MaskedZero <> 0
GROUP BY s.SourceRowKey;

SELECT 'NOSUBST' AS section, res.SourceRowKey AS k, m.Code AS methodology, CAST(COUNT(*) AS nvarchar(30)) AS v
FROM calc.CalculationResult res
JOIN calc.MethodologyVersion mv ON mv.Id = res.MethodologyVersionId
JOIN calc.Methodology m ON m.Id = mv.MethodologyId
WHERE res.PeriodKey = @p AND res.CalculationRunId = @run AND res.DocumentId = @doc
  AND res.OutputCode LIKE '%XXX%' AND res.SubstanceEntryId IS NULL
GROUP BY res.SourceRowKey, m.Code;

-- BRANCHES: legacy writes ONE branch (_type1/_type2/...) per row, substance and metric (tons / gsec); more than one
-- written branch means the emission is counted twice (methodology version without branch gating, D-423/D-435).
SELECT 'BRANCHES' AS section, b.SourceRowKey AS k, b.Code AS methodology, CAST(COUNT(*) AS nvarchar(30)) AS v
FROM (
    SELECT res.SourceRowKey, m.Code, res.SubstanceEntryId, LEFT(res.OutputCode, CHARINDEX('_type', res.OutputCode)) AS metric
    FROM calc.CalculationResult res
    JOIN calc.MethodologyVersion mv ON mv.Id = res.MethodologyVersionId
    JOIN calc.Methodology m ON m.Id = mv.MethodologyId
    WHERE res.PeriodKey = @p AND res.CalculationRunId = @run AND res.DocumentId = @doc AND res.OutputCode LIKE '%[_]type%'
    GROUP BY res.SourceRowKey, m.Code, res.SubstanceEntryId, LEFT(res.OutputCode, CHARINDEX('_type', res.OutputCode))
    HAVING COUNT(DISTINCT res.OutputCode) > 1
) b
GROUP BY b.SourceRowKey, b.Code;
