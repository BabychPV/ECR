/* =====================================================================
   PostCheck.sql  --  пост-перевірка стенда Land після recalc/міграції. ТІЛЬКИ ЧИТАННЯ.
   Лише SELECT WITH (NOLOCK), DECLARE, PRINT, sp_executesql зі SELECT. Значень не виводить: лише лічильники/коди/стани.
   Запускає Run-PostCheck.ps1 (1 процес sqlcmd; база -d; змінні -v):
     since        UTC yyyyMMddHHmmss (без двокрапок)       - початок вікна (старт recalc/міграції)
     projectcode  код проєкту (доки з його версією шаблону)
     period       PeriodKey для перевірки "прогони після since" (напр. 202606)
     docs         ALL або список BusinessKey/DocumentId через ; (doc-scope для before_since)
     methcodes    NONE або коди методологій через ; (остання Published версія)
     rowfilter    NONE або очікуване значення RowActivityFilter (двокрапка передається як ~: sqlcmd -v не приймає :)
   Рядки "PC|ключ|значення" наприкінці (розділ Z) - машинний підсумок для Run-PostCheck.ps1.
   Динамічні перевірки: якщо колонки/таблиці немає в схемі (старіша збірка) - друкується "відсутня".
   Порогів тут немає: їх задає Run-PostCheck.ps1 параметрами.
   ===================================================================== */
SET NOCOUNT ON;
SET LOCK_TIMEOUT 10000;
SELECT CAST(STUFF(STUFF(STUFF('$(since)',9,0,' '),12,0,':'),15,0,':') AS datetime2) AS since INTO #k1;
CREATE TABLE #doctok (tok nvarchar(100) NOT NULL);
CREATE TABLE #docscope (tok nvarchar(100) NOT NULL, DocumentId int NULL);
CREATE TABLE #methtok (tok nvarchar(100) NOT NULL);
DECLARE @lists TABLE (kind varchar(8), raw nvarchar(4000));
INSERT @lists VALUES ('doc', N'$(docs)'), ('meth', N'$(methcodes)');
DECLARE @kind varchar(8), @raw nvarchar(4000), @pos int, @tok nvarchar(100);
DECLARE lc CURSOR LOCAL FAST_FORWARD FOR SELECT kind, raw FROM @lists;
OPEN lc; FETCH NEXT FROM lc INTO @kind, @raw;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @raw = LTRIM(RTRIM(@raw)) + N';';
    WHILE LEN(@raw) > 0
    BEGIN
        SET @pos = CHARINDEX(N';', @raw);
        SET @tok = LTRIM(RTRIM(LEFT(@raw, @pos - 1)));
        SET @raw = SUBSTRING(@raw, @pos + 1, 4000);
        IF @tok = N'' OR @tok IN (N'ALL', N'NONE') CONTINUE;
        IF @kind = 'doc'
            INSERT #doctok VALUES (@tok)
        ELSE
            INSERT #methtok VALUES (@tok);
    END
    FETCH NEXT FROM lc INTO @kind, @raw;
END
CLOSE lc; DEALLOCATE lc;
INSERT #docscope (tok, DocumentId)
SELECT t.tok, d.Id
FROM #doctok t
LEFT JOIN doc.Document d WITH (NOLOCK)
       ON (t.tok NOT LIKE N'%[^0-9]%' AND d.Id = TRY_CAST(t.tok AS int))
       OR (t.tok LIKE N'%[^0-9]%' AND d.BusinessKey = t.tok);
GO
PRINT N'== 0. Контекст (UTC) ==';
SELECT @@SERVERNAME AS server_name, DB_NAME() AS db_name, SYSUTCDATETIME() AS now_utc, (SELECT since FROM #k1) AS since_window,
       (SELECT COUNT(*) FROM #docscope) AS scope_docs_requested,
       (SELECT COUNT(DISTINCT DocumentId) FROM #docscope WHERE DocumentId IS NOT NULL) AS scope_docs_resolved;
GO

/* ===================== A-10. Результати C11/C12 ===================== */
PRINT N'== A10-1. Поточні результати (Run.Status=Current) по методологіях C11/C12 ==';
SELECT m.Code, COUNT_BIG(*) AS current_results,
       SUM(CASE WHEN r.Value IS NULL THEN 1 ELSE 0 END) AS null_values,
       COUNT(DISTINCT r.DocumentId) AS docs, COUNT(DISTINCT r.PeriodKey) AS periods,
       COUNT(DISTINCT r.MethodologyVersionId) AS versions_used
FROM calc.CalculationResult r WITH (NOLOCK)
JOIN calc.CalculationRun run WITH (NOLOCK) ON run.Id = r.CalculationRunId AND run.Status = 'Current'
JOIN calc.MethodologyVersion mv WITH (NOLOCK) ON mv.Id = r.MethodologyVersionId
JOIN calc.Methodology m WITH (NOLOCK) ON m.Id = mv.MethodologyId
WHERE m.Code LIKE 'ECW[_]C1[12]%'
GROUP BY m.Code ORDER BY m.Code;
GO
PRINT N'== A10-2. Поточні результати C11/C12 по періодах 202601..202612 ==';
SELECT r.PeriodKey, COUNT_BIG(*) AS current_results,
       SUM(CASE WHEN r.Value IS NULL THEN 1 ELSE 0 END) AS null_values,
       COUNT(DISTINCT r.DocumentId) AS docs, COUNT(DISTINCT mv.MethodologyId) AS methodologies
FROM calc.CalculationResult r WITH (NOLOCK)
JOIN calc.CalculationRun run WITH (NOLOCK) ON run.Id = r.CalculationRunId AND run.Status = 'Current'
JOIN calc.MethodologyVersion mv WITH (NOLOCK) ON mv.Id = r.MethodologyVersionId
JOIN calc.Methodology m WITH (NOLOCK) ON m.Id = mv.MethodologyId AND m.Code LIKE 'ECW[_]C1[12]%'
WHERE r.PeriodKey BETWEEN 202601 AND 202612
GROUP BY r.PeriodKey ORDER BY r.PeriodKey;
GO

/* ===================== LP-40 (44 колонки-мости) ===================== */
CREATE TABLE #lp40 (tbl varchar(20) NOT NULL, col varchar(60) NOT NULL);
INSERT #lp40 VALUES
   ('T9_01','Category1'),('T9_02','Density20'),('T9_04','Density20'),('T9_04','TotalSulphurContent'),('T9_04','Vddg'),('T9_04','Vwdg'),
   ('T9_07','Density20'),('T9_07','TotalSulphurContent'),('T9_07','Vddg'),('T9_07','Category1'),('T9_08','Category1'),('T9_08','Density'),
   ('T9_08','FuelLowerHeating'),('T9_08','H2Smass'),('T9_08','MercaptanSulphurContent'),('T9_10','Density'),('T9_10','FuelLowerHeating'),
   ('T9_10','H2Smass'),('T9_10','MercaptanSulphurContent'),('T9_11','Density20'),('T9a_16','Category1'),('T9a_17','Land_Power'),
   ('T9a_17','Density'),('T9a_17','FuelLowerHeating'),('T9a_17','H2Smass'),('T9a_17','MercaptanSulphurContent'),('T9a_17a','Category1'),
   ('T9a_19','Category1'),('T9a_24','Density20'),('T9a_25','Density20'),('T9a_25','TotalSulphurContent'),('T9a_25','Vddg'),
   ('T9a_25','Vwdg'),('T9a_26','Density20'),('T9a_27','Density20'),('T9a_28','Density20'),('T10_01','Density20'),
   ('T10_03','Density0'),('T10_03','TotalSulphurContent'),('T10_03','Vddg'),('T11_01','Density20'),('T11_03','Density0'),
   ('T11_03','TotalSulphurContent'),('T11_03','Vddg');
GO
PRINT N'== LP-40. 44 колонки-мости: комірок, непорожніх і помилок ==';
DECLARE @tv int = (SELECT TemplateVersionId FROM doc.Project WITH (NOLOCK) WHERE Code = N'$(projectcode)');
SELECT w.tbl, w.col, COUNT_BIG(cv.TableRowId) AS cells,
       SUM(CASE WHEN cv.IsEmpty = 0 THEN 1 ELSE 0 END) AS non_empty,
       SUM(CASE WHEN cv.ValueString LIKE '#%' THEN 1 ELSE 0 END) AS errs
FROM #lp40 w
LEFT JOIN cfg.TableDef td WITH (NOLOCK) ON td.Code = w.tbl AND td.IsDeleted = 0
     AND td.SheetDefId IN (SELECT s.Id FROM cfg.SheetDef s WITH (NOLOCK) WHERE s.TemplateVersionId = @tv)
LEFT JOIN cfg.ColumnDef cd WITH (NOLOCK) ON cd.TableDefId = td.Id AND cd.Code = w.col AND cd.IsDeleted = 0
LEFT JOIN doc.CellValue cv WITH (NOLOCK) ON cv.TableDefId = td.Id AND cv.ColumnDefId = cd.Id
GROUP BY w.tbl, w.col ORDER BY w.tbl, w.col;
GO

/* ===================== Період: час прогонів vs публікація/міграція ===================== */
PRINT N'== P-1. Current-прогони періоду $(period): скільки завершилось після since (у doc-scope і поза ним) ==';
DECLARE @since datetime2 = (SELECT since FROM #k1);
DECLARE @all bit = CASE WHEN EXISTS (SELECT 1 FROM #docscope) THEN 0 ELSE 1 END;
SELECT COUNT(*) AS current_runs_period, MIN(r.FinishedAt) AS oldest_finished, MAX(r.FinishedAt) AS newest_finished,
       SUM(CASE WHEN @all = 1 OR s.DocumentId IS NOT NULL THEN 1 ELSE 0 END) AS in_scope_runs,
       SUM(CASE WHEN (@all = 1 OR s.DocumentId IS NOT NULL) AND (r.FinishedAt < @since OR r.FinishedAt IS NULL) THEN 1 ELSE 0 END) AS in_scope_before_since,
       SUM(CASE WHEN r.FinishedAt < @since OR r.FinishedAt IS NULL THEN 1 ELSE 0 END) AS all_before_since
FROM calc.CalculationRun r WITH (NOLOCK)
LEFT JOIN (SELECT DISTINCT DocumentId FROM #docscope WHERE DocumentId IS NOT NULL) s ON s.DocumentId = r.DocumentId
WHERE r.Status = 'Current' AND r.PeriodKey = $(period);
GO
PRINT N'== P-2. Публікації методологічних версій після since ==';
SELECT m.Code, mv.Version, mv.Status, mv.EffectiveFrom, mv.PublishedAt, mv.NumericMode
FROM calc.MethodologyVersion mv WITH (NOLOCK) JOIN calc.Methodology m WITH (NOLOCK) ON m.Id = mv.MethodologyId
WHERE mv.PublishedAt >= (SELECT since FROM #k1)
ORDER BY mv.PublishedAt;
GO
PRINT N'== P-3. Версії шаблону ==';
SELECT tv.Id, tv.Version, tv.Status AS status_0draft_1pub, tv.ClonedFromVersionId, tv.PublishedAt, tv.UpdatedAt
FROM cfg.TemplateVersion tv WITH (NOLOCK) ORDER BY tv.Id;
GO
PRINT N'== P-4. Дублі поточних прогонів: >1 Current на (проєкт, документ, період). Очікується 0 рядків ==';
SELECT ProjectId, ISNULL(DocumentId, 0) AS document_id, PeriodKey, COUNT(*) AS current_runs
FROM calc.CalculationRun WITH (NOLOCK) WHERE Status = 'Current'
GROUP BY ProjectId, ISNULL(DocumentId, 0), PeriodKey HAVING COUNT(*) > 1;
GO

/* ===================== Failed / Cancelled після since ===================== */
PRINT N'== F1. calc.CalculationRun зі Status Failed/Cancelled після since ==';
SELECT Status, COUNT(*) AS runs, MIN(StartedAt) AS first_started, MAX(StartedAt) AS last_started
FROM calc.CalculationRun WITH (NOLOCK)
WHERE Status IN ('Failed','Cancelled') AND StartedAt >= (SELECT since FROM #k1)
GROUP BY Status;
GO
PRINT N'== F2. itg.JobProgress після since: стани ==';
SELECT State, COUNT(*) AS n, MIN(CreatedAt) AS first_created, MAX(UpdatedAt) AS last_updated
FROM itg.JobProgress WITH (NOLOCK)
WHERE COALESCE(UpdatedAt, CreatedAt) >= (SELECT since FROM #k1)
GROUP BY State ORDER BY State;
GO

/* ===================== Версії шаблону й документи ===================== */
PRINT N'== V1. Проєкт: версія шаблону і кількість документів ==';
SELECT p.Code AS project, p.TemplateVersionId, tv.Version AS template_version,
       (SELECT COUNT(*) FROM doc.Document d WITH (NOLOCK) WHERE d.ProjectId = p.Id) AS documents
FROM doc.Project p WITH (NOLOCK) JOIN cfg.TemplateVersion tv WITH (NOLOCK) ON tv.Id = p.TemplateVersionId;
GO
PRINT N'== V2. TableInstance по версії шаблону: документів / екземплярів ==';
SELECT tv.Version AS template_version, COUNT(DISTINCT ti.DocumentId) AS documents, COUNT_BIG(*) AS table_instances,
       COUNT(DISTINCT ti.TableDefId) AS table_defs
FROM doc.TableInstance ti WITH (NOLOCK)
JOIN cfg.TableDef td WITH (NOLOCK) ON td.Id = ti.TableDefId
JOIN cfg.SheetDef sd WITH (NOLOCK) ON sd.Id = td.SheetDefId
JOIN cfg.TemplateVersion tv WITH (NOLOCK) ON tv.Id = sd.TemplateVersionId
GROUP BY tv.Version ORDER BY tv.Version;
GO
PRINT N'== V3. wf.ApprovalState по статусах ==';
SELECT a.Status, COUNT(*) AS n FROM wf.ApprovalState a WITH (NOLOCK) GROUP BY a.Status ORDER BY a.Status;
GO
PRINT N'== V4. CellValue: усього/введених/обчислених/помилок ==';
SELECT COUNT_BIG(*) AS cells_total,
       SUM(CASE WHEN cv.IsEmpty = 0 THEN 1 ELSE 0 END) AS non_empty,
       SUM(CASE WHEN cv.IsEmpty = 0 AND cv.IsCalculated = 0 THEN 1 ELSE 0 END) AS entered,
       SUM(CASE WHEN cv.IsEmpty = 0 AND cv.IsCalculated = 1 THEN 1 ELSE 0 END) AS calculated,
       SUM(CASE WHEN cv.ValueString LIKE '#%' THEN 1 ELSE 0 END) AS error_cells
FROM doc.CellValue cv WITH (NOLOCK);
GO
PRINT N'== V5. Комірки-помилки (#...) по таблицях: топ-10 ==';
SELECT TOP 10 td.Code AS tbl, COUNT_BIG(*) AS error_cells
FROM doc.CellValue cv WITH (NOLOCK) JOIN cfg.TableDef td WITH (NOLOCK) ON td.Id = cv.TableDefId
WHERE cv.ValueString LIKE '#%' GROUP BY td.Code ORDER BY error_cells DESC;
GO

/* ===================== TableInstanceId у нових результатах ===================== */
PRINT N'== TII-1. calc.CalculationResult.TableInstanceId: нові результати (Run.StartedAt >= since) ==';
IF COL_LENGTH('calc.CalculationResult', 'TableInstanceId') IS NULL
    PRINT N'колонка відсутня: calc.CalculationResult.TableInstanceId';
ELSE
    EXEC sp_executesql N'
    SELECT m.Code, COUNT_BIG(*) AS new_results,
           SUM(CASE WHEN r.TableInstanceId IS NULL THEN 1 ELSE 0 END) AS null_tbl,
           SUM(CASE WHEN r.TableInstanceId IS NOT NULL THEN 1 ELSE 0 END) AS not_null_tbl
    FROM calc.CalculationResult r WITH (NOLOCK)
    JOIN calc.CalculationRun run WITH (NOLOCK) ON run.Id = r.CalculationRunId
    JOIN calc.MethodologyVersion mv WITH (NOLOCK) ON mv.Id = r.MethodologyVersionId
    JOIN calc.Methodology m WITH (NOLOCK) ON m.Id = mv.MethodologyId
    WHERE run.StartedAt >= (SELECT since FROM #k1)
    GROUP BY m.Code ORDER BY m.Code;';
GO
PRINT N'== TII-2. Висячі TableInstanceId: результат посилається на неіснуючий екземпляр (очікується 0) ==';
IF COL_LENGTH('calc.CalculationResult', 'TableInstanceId') IS NULL
    PRINT N'колонка відсутня: calc.CalculationResult.TableInstanceId';
ELSE
    EXEC sp_executesql N'
    SELECT COUNT_BIG(*) AS dangling_new_results
    FROM calc.CalculationResult r WITH (NOLOCK)
    JOIN calc.CalculationRun run WITH (NOLOCK) ON run.Id = r.CalculationRunId AND run.StartedAt >= (SELECT since FROM #k1)
    WHERE r.TableInstanceId IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM doc.TableInstance ti WITH (NOLOCK) WHERE ti.Id = r.TableInstanceId AND ti.DocumentId = r.DocumentId);';
GO

/* ===================== BRANCH_BOTH ===================== */
PRINT N'== BB. BRANCH_BOTH у TraceJson / ModulesProfileJson / JobProgress після since (очікується 0) ==';
DECLARE @since datetime2 = (SELECT since FROM #k1);
SELECT 'CalculationStep.TraceJson' AS src, COUNT_BIG(*) AS hits
FROM calc.CalculationStep s WITH (NOLOCK) JOIN calc.CalculationRun run WITH (NOLOCK) ON run.Id = s.CalculationRunId
WHERE run.StartedAt >= @since AND s.TraceJson LIKE '%BRANCH_BOTH%'
UNION ALL
SELECT 'CalculationRun.ModulesProfileJson', COUNT_BIG(*) FROM calc.CalculationRun WITH (NOLOCK)
WHERE StartedAt >= @since AND ModulesProfileJson LIKE '%BRANCH_BOTH%'
UNION ALL
SELECT 'JobProgress.Message/Error/Payload', COUNT_BIG(*) FROM itg.JobProgress WITH (NOLOCK)
WHERE COALESCE(UpdatedAt, CreatedAt) >= @since
  AND (Message LIKE '%BRANCH_BOTH%' OR Error LIKE '%BRANCH_BOTH%' OR Payload LIKE '%BRANCH_BOTH%');
GO

/* ===================== RowActivityFilter ===================== */
PRINT N'== RF-1. calc.Methodology.RowActivityFilter: скільки методологій з фільтром ==';
IF COL_LENGTH('calc.Methodology', 'RowActivityFilter') IS NULL
    PRINT N'колонка відсутня: calc.Methodology.RowActivityFilter';
ELSE
    EXEC sp_executesql N'
    SELECT COUNT(*) AS methodologies,
           SUM(CASE WHEN RowActivityFilter IS NOT NULL THEN 1 ELSE 0 END) AS with_filter,
           COUNT(DISTINCT RowActivityFilter) AS distinct_filters
    FROM calc.Methodology WITH (NOLOCK);';
GO
PRINT N'== RF-2. Відхилені правилом категорії / без правила (JobProgress.Message) після since ==';
SELECT
  SUM(CASE WHEN Message LIKE '%jobs.recalcCategoryRuleRejected%' THEN 1 ELSE 0 END) AS jobs_category_rule_rejected,
  SUM(CASE WHEN Message LIKE '%jobs.recalcNoMatchingRule%' THEN 1 ELSE 0 END) AS jobs_no_matching_rule,
  SUM(CASE WHEN Message LIKE '%jobs.recalcMethodologyUnpublished%' THEN 1 ELSE 0 END) AS jobs_methodology_unpublished,
  COUNT(*) AS jobs_total_since
FROM itg.JobProgress WITH (NOLOCK)
WHERE COALESCE(UpdatedAt, CreatedAt) >= (SELECT since FROM #k1);
GO

/* ===================== Z. Машинний підсумок для Run-PostCheck.ps1 ===================== */
PRINT N'== Z. PC| підсумок (парситься скриптом) ==';
DECLARE @since datetime2 = (SELECT since FROM #k1);
DECLARE @tv int = (SELECT TemplateVersionId FROM doc.Project WITH (NOLOCK) WHERE Code = N'$(projectcode)');
DECLARE @all bit = CASE WHEN EXISTS (SELECT 1 FROM #docscope) THEN 0 ELSE 1 END;
DECLARE @n bigint, @n2 bigint, @n3 bigint, @s nvarchar(2000);

SELECT @n = COUNT(*) FROM #docscope;
PRINT 'PC|scope_docs_requested|' + CAST(@n AS varchar(20));
SELECT @n = COUNT(DISTINCT DocumentId) FROM #docscope WHERE DocumentId IS NOT NULL;
PRINT 'PC|scope_docs_resolved|' + CAST(@n AS varchar(20));
PRINT 'PC|scope_all|' + CAST(@all AS varchar(1));

SELECT @n = COUNT_BIG(*), @n2 = SUM(CASE WHEN r.Value IS NULL THEN 1 ELSE 0 END), @n3 = COUNT(DISTINCT mv.MethodologyId)
FROM calc.CalculationResult r WITH (NOLOCK)
JOIN calc.CalculationRun run WITH (NOLOCK) ON run.Id = r.CalculationRunId AND run.Status = 'Current'
JOIN calc.MethodologyVersion mv WITH (NOLOCK) ON mv.Id = r.MethodologyVersionId
JOIN calc.Methodology m WITH (NOLOCK) ON m.Id = mv.MethodologyId AND m.Code LIKE 'ECW[_]C1[12]%';
PRINT 'PC|a10_current_results|' + CAST(ISNULL(@n,0) AS varchar(20));
PRINT 'PC|a10_null_values|' + CAST(ISNULL(@n2,0) AS varchar(20));
PRINT 'PC|a10_methodologies|' + CAST(ISNULL(@n3,0) AS varchar(20));

SELECT @n = COUNT_BIG(*)
FROM calc.CalculationResult r WITH (NOLOCK)
JOIN calc.CalculationRun run WITH (NOLOCK) ON run.Id = r.CalculationRunId AND run.Status = 'Current' AND r.PeriodKey = $(period)
JOIN calc.MethodologyVersion mv WITH (NOLOCK) ON mv.Id = r.MethodologyVersionId
JOIN calc.Methodology m WITH (NOLOCK) ON m.Id = mv.MethodologyId AND m.Code LIKE 'ECW[_]C1[12]%';
PRINT 'PC|period_results|' + CAST(ISNULL(@n,0) AS varchar(20));

-- прогони періоду: doc-scope (за замовчуванням усі документи) + окремо "поза межами"
SELECT @n = SUM(CASE WHEN @all = 1 OR s.DocumentId IS NOT NULL THEN 1 ELSE 0 END),
       @n2 = SUM(CASE WHEN (@all = 1 OR s.DocumentId IS NOT NULL) AND (r.FinishedAt < @since OR r.FinishedAt IS NULL) THEN 1 ELSE 0 END),
       @n3 = SUM(CASE WHEN r.FinishedAt < @since OR r.FinishedAt IS NULL THEN 1 ELSE 0 END)
FROM calc.CalculationRun r WITH (NOLOCK)
LEFT JOIN (SELECT DISTINCT DocumentId FROM #docscope WHERE DocumentId IS NOT NULL) s ON s.DocumentId = r.DocumentId
WHERE r.Status = 'Current' AND r.PeriodKey = $(period);
PRINT 'PC|period_current_runs|' + CAST(ISNULL(@n,0) AS varchar(20));
PRINT 'PC|period_current_before_since|' + CAST(ISNULL(@n2,0) AS varchar(20));
PRINT 'PC|period_current_before_since_all_docs|' + CAST(ISNULL(@n3,0) AS varchar(20));

SELECT @n = COUNT(*) FROM (SELECT 1 AS x FROM calc.CalculationRun WITH (NOLOCK) WHERE Status = 'Current'
       GROUP BY ProjectId, ISNULL(DocumentId, 0), PeriodKey HAVING COUNT(*) > 1) d;
PRINT 'PC|dup_current_groups|' + CAST(ISNULL(@n,0) AS varchar(20));

SELECT @n = COUNT(*) FROM calc.CalculationRun WITH (NOLOCK) WHERE Status = 'Failed' AND StartedAt >= @since;
PRINT 'PC|runs_failed_since|' + CAST(@n AS varchar(20));
SELECT @n = COUNT(*) FROM calc.CalculationRun WITH (NOLOCK) WHERE Status = 'Cancelled' AND StartedAt >= @since;
PRINT 'PC|runs_cancelled_since|' + CAST(@n AS varchar(20));
SELECT @n = COUNT(*) FROM itg.JobProgress WITH (NOLOCK) WHERE State = 'Failed' AND COALESCE(UpdatedAt, CreatedAt) >= @since;
PRINT 'PC|jobs_failed_since|' + CAST(@n AS varchar(20));
SELECT @n = COUNT(*) FROM itg.JobProgress WITH (NOLOCK) WHERE State = 'Cancelled' AND COALESCE(UpdatedAt, CreatedAt) >= @since;
PRINT 'PC|jobs_cancelled_since|' + CAST(@n AS varchar(20));
SELECT @n = COUNT(*) FROM itg.JobProgress WITH (NOLOCK) WHERE State IN ('Queued','Running','Retrying');
PRINT 'PC|queue_open|' + CAST(@n AS varchar(20));

-- шаблон і документи
SELECT @s = (SELECT TOP 1 tv.Version FROM cfg.TemplateVersion tv WITH (NOLOCK) WHERE tv.Status = 1 ORDER BY tv.Id DESC);
PRINT 'PC|template_latest_published|' + ISNULL(@s, 'none');
SELECT @s = (SELECT tv.Version FROM doc.Project p WITH (NOLOCK) JOIN cfg.TemplateVersion tv WITH (NOLOCK) ON tv.Id = p.TemplateVersionId WHERE p.Code = N'$(projectcode)');
PRINT 'PC|project_template|' + ISNULL(@s, 'none');
SELECT @n = COUNT(*) FROM doc.Document WITH (NOLOCK);
PRINT 'PC|documents_total|' + CAST(@n AS varchar(20));
SELECT @n = COUNT(DISTINCT ti.DocumentId) FROM doc.TableInstance ti WITH (NOLOCK)
  JOIN cfg.TableDef td WITH (NOLOCK) ON td.Id = ti.TableDefId
  JOIN cfg.SheetDef sd WITH (NOLOCK) ON sd.Id = td.SheetDefId AND sd.TemplateVersionId = @tv;
PRINT 'PC|documents_on_project_template|' + CAST(@n AS varchar(20));
SELECT @n = COUNT(DISTINCT ti.DocumentId) FROM doc.TableInstance ti WITH (NOLOCK)
  JOIN cfg.TableDef td WITH (NOLOCK) ON td.Id = ti.TableDefId
  JOIN cfg.SheetDef sd WITH (NOLOCK) ON sd.Id = td.SheetDefId AND sd.TemplateVersionId <> @tv;
PRINT 'PC|documents_on_other_template|' + CAST(@n AS varchar(20));
SELECT @n = COUNT(*) FROM wf.ApprovalState WITH (NOLOCK);
PRINT 'PC|approval_rows|' + CAST(@n AS varchar(20));

-- клітинки
SELECT @n = SUM(CASE WHEN cv.IsEmpty = 0 AND cv.IsCalculated = 0 THEN 1 ELSE 0 END),
       @n2 = SUM(CASE WHEN cv.ValueString LIKE '#%' THEN 1 ELSE 0 END)
FROM doc.CellValue cv WITH (NOLOCK);
PRINT 'PC|cells_entered|' + CAST(ISNULL(@n,0) AS varchar(20));
PRINT 'PC|cells_error|' + CAST(ISNULL(@n2,0) AS varchar(20));

-- LP-40
;WITH per AS (
  SELECT w.tbl, w.col, SUM(CASE WHEN cv.IsEmpty = 0 THEN 1 ELSE 0 END) AS ne,
         SUM(CASE WHEN cv.ValueString LIKE '#%' THEN 1 ELSE 0 END) AS errs
  FROM #lp40 w
  LEFT JOIN cfg.TableDef td WITH (NOLOCK) ON td.Code = w.tbl AND td.IsDeleted = 0
       AND td.SheetDefId IN (SELECT s.Id FROM cfg.SheetDef s WITH (NOLOCK) WHERE s.TemplateVersionId = @tv)
  LEFT JOIN cfg.ColumnDef cd WITH (NOLOCK) ON cd.TableDefId = td.Id AND cd.Code = w.col AND cd.IsDeleted = 0
  LEFT JOIN doc.CellValue cv WITH (NOLOCK) ON cv.TableDefId = td.Id AND cv.ColumnDefId = cd.Id
  GROUP BY w.tbl, w.col)
SELECT @n = SUM(CASE WHEN ne > 0 THEN 1 ELSE 0 END), @n2 = SUM(ne), @n3 = SUM(errs) FROM per;
PRINT 'PC|lp40_columns_with_values|' + CAST(ISNULL(@n,0) AS varchar(20));
PRINT 'PC|lp40_non_empty|' + CAST(ISNULL(@n2,0) AS varchar(20));
PRINT 'PC|lp40_errs|' + CAST(ISNULL(@n3,0) AS varchar(20));

-- TableInstanceId (динамічно)
IF COL_LENGTH('calc.CalculationResult', 'TableInstanceId') IS NULL
    PRINT 'PC|tableinstanceid_column|absent';
ELSE
BEGIN
    PRINT 'PC|tableinstanceid_column|present';
    EXEC sp_executesql
        N'SELECT @a = COUNT_BIG(*), @b = SUM(CASE WHEN r.TableInstanceId IS NULL THEN 1 ELSE 0 END)
          FROM calc.CalculationResult r WITH (NOLOCK)
          JOIN calc.CalculationRun run WITH (NOLOCK) ON run.Id = r.CalculationRunId
          WHERE run.StartedAt >= @s;',
        N'@s datetime2, @a bigint OUTPUT, @b bigint OUTPUT', @s = @since, @a = @n OUTPUT, @b = @n2 OUTPUT;
    PRINT 'PC|tii_new_results|' + CAST(ISNULL(@n,0) AS varchar(20));
    PRINT 'PC|tii_new_null|' + CAST(ISNULL(@n2,0) AS varchar(20));
END

-- BRANCH_BOTH
SELECT @n = (SELECT COUNT_BIG(*) FROM calc.CalculationStep s WITH (NOLOCK) JOIN calc.CalculationRun run WITH (NOLOCK) ON run.Id = s.CalculationRunId
              WHERE run.StartedAt >= @since AND s.TraceJson LIKE '%BRANCH_BOTH%')
           + (SELECT COUNT_BIG(*) FROM calc.CalculationRun WITH (NOLOCK)
              WHERE StartedAt >= @since AND (ModulesProfileJson LIKE '%BRANCH_BOTH%' OR ErrorMessage LIKE '%BRANCH_BOTH%'))
           + (SELECT COUNT_BIG(*) FROM itg.JobProgress WITH (NOLOCK)
              WHERE COALESCE(UpdatedAt, CreatedAt) >= @since AND (Message LIKE '%BRANCH_BOTH%' OR Error LIKE '%BRANCH_BOTH%' OR Payload LIKE '%BRANCH_BOTH%'));
PRINT 'PC|branch_both_hits|' + CAST(ISNULL(@n,0) AS varchar(20));

-- RowActivityFilter: очікуване значення передає Run-PostCheck.ps1 (rowfilter); NONE = не порівнювати
IF COL_LENGTH('calc.Methodology', 'RowActivityFilter') IS NULL
    PRINT 'PC|rowactivityfilter_column|absent';
ELSE
BEGIN
    PRINT 'PC|rowactivityfilter_column|present';
    DECLARE @rf nvarchar(200) = REPLACE(N'$(rowfilter)', N'~', N':');
    DECLARE @rf_with bigint, @rf_ok bigint, @rf_oth bigint, @rf_nul nvarchar(max), @rf_othc nvarchar(max);
    EXEC sp_executesql N'
        SELECT @w = SUM(CASE WHEN RowActivityFilter IS NOT NULL THEN 1 ELSE 0 END),
               @ok = SUM(CASE WHEN @rf <> N''NONE'' AND RowActivityFilter = @rf THEN 1 ELSE 0 END),
               @on = SUM(CASE WHEN @rf <> N''NONE'' AND RowActivityFilter IS NOT NULL AND RowActivityFilter <> @rf THEN 1 ELSE 0 END)
          FROM calc.Methodology WITH (NOLOCK);
        SELECT @nc = STUFF((SELECT '','' + Code FROM calc.Methodology WITH (NOLOCK) WHERE RowActivityFilter IS NULL ORDER BY Code FOR XML PATH('''')), 1, 1, '''');
        SELECT @oc = STUFF((SELECT '','' + Code FROM calc.Methodology WITH (NOLOCK)
                            WHERE @rf <> N''NONE'' AND RowActivityFilter IS NOT NULL AND RowActivityFilter <> @rf ORDER BY Code FOR XML PATH('''')), 1, 1, '''');',
        N'@rf nvarchar(200), @w bigint OUTPUT, @ok bigint OUTPUT, @on bigint OUTPUT, @nc nvarchar(max) OUTPUT, @oc nvarchar(max) OUTPUT',
        @rf = @rf, @w = @rf_with OUTPUT, @ok = @rf_ok OUTPUT, @on = @rf_oth OUTPUT, @nc = @rf_nul OUTPUT, @oc = @rf_othc OUTPUT;
    PRINT 'PC|rowactivityfilter_methodologies|' + CAST(ISNULL(@rf_with,0) AS varchar(20));
    PRINT 'PC|rowfilter_value_ok|' + CAST(ISNULL(@rf_ok,0) AS varchar(20));
    PRINT 'PC|rowfilter_null_codes|' + ISNULL(@rf_nul, '');
    PRINT 'PC|rowfilter_other|' + CAST(ISNULL(@rf_oth,0) AS varchar(20));
    PRINT 'PC|rowfilter_other_codes|' + ISNULL(@rf_othc, '');
END
SELECT @n = COUNT(*) FROM itg.JobProgress WITH (NOLOCK)
  WHERE COALESCE(UpdatedAt, CreatedAt) >= @since AND Message LIKE '%jobs.recalcCategoryRuleRejected%';
PRINT 'PC|jobs_category_rule_rejected|' + CAST(@n AS varchar(20));

-- остання Published версія заданих методологій: CODE=ver;CODE=ver
SELECT @s = STUFF((SELECT ';' + t.tok + '=' + ISNULL((SELECT TOP 1 mv.Version FROM calc.MethodologyVersion mv WITH (NOLOCK)
                       JOIN calc.Methodology m WITH (NOLOCK) ON m.Id = mv.MethodologyId
                       WHERE m.Code = t.tok AND mv.Status = 1 ORDER BY mv.Id DESC), 'none')
                   FROM #methtok t ORDER BY t.tok FOR XML PATH('')), 1, 1, '');
PRINT 'PC|methodology_latest_published|' + ISNULL(@s, '');
GO
PRINT N'== КІНЕЦЬ PostCheck.sql ==';
GO
