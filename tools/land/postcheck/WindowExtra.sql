/* WindowExtra.sql: необов'язковий розділ -Window7Isolation пост-перевірки: ізоляція колонок одного документа/таблиці
   (класичний випадок T9_04: Load-LandRows записує лише SubstanceConsumption20Dgr1atm-колонки, решта групи - лише перерахунок)
   + стан результатів заданих методологій + TableInstanceId NULL у нових Result/Step/Input.
   ТІЛЬКИ ЧИТАННЯ: SELECT WITH (NOLOCK), DECLARE, PRINT, sp_executesql зі SELECT. Значень не виводить: лише лічильники/ID/коди.
   Запускає Run-PostCheck.ps1 (1 процес sqlcmd; -v):
     since        UTC yyyyMMddHHmmss (без двокрапок)
     projectcode  код проєкту (версія шаблону)
     isodoc       BusinessKey документа для ізоляції
     istable      код таблиці (за замовчуванням T9_04)
     pfrom, pto   межі PeriodKey (за замовчуванням 202601..202607)
     wmeth        NONE або CODE[@поріг];CODE[@поріг]... (@ замість =: sqlcmd -v не приймає = у значенні) методологій для перевірки Current
   Машинний підсумок: рядки "WX|ключ|значення". Порогів у SQL немає: поріг із wmeth друкується як *_base. */
SET NOCOUNT ON;
SET LOCK_TIMEOUT 10000;
DECLARE @since datetime2 = CAST(STUFF(STUFF(STUFF('$(since)',9,0,' '),12,0,':'),15,0,':') AS datetime2);
DECLARE @pfrom int = $(pfrom), @pto int = $(pto);
DECLARE @n bigint, @n2 bigint, @n3 bigint, @s nvarchar(400);

DECLARE @tv int;
SELECT @tv = TemplateVersionId FROM doc.Project WITH (NOLOCK) WHERE Code = N'$(projectcode)';
IF @tv IS NULL SELECT TOP 1 @tv = Id FROM cfg.TemplateVersion WITH (NOLOCK) WHERE Status = 1 ORDER BY Id DESC;
DECLARE @td int, @doc int;
SELECT TOP 1 @td = td.Id FROM cfg.TableDef td WITH (NOLOCK) JOIN cfg.SheetDef sd WITH (NOLOCK) ON sd.Id = td.SheetDefId
 WHERE td.Code = N'$(istable)' AND td.IsDeleted = 0 AND sd.TemplateVersionId = @tv ORDER BY td.Id;
SELECT TOP 1 @doc = Id FROM doc.Document WITH (NOLOCK) WHERE BusinessKey = N'$(isodoc)';

PRINT N'== 0. Контекст ==';
SELECT DB_NAME() AS db_name, SYSUTCDATETIME() AS now_utc, @since AS since_window, @tv AS template_version_id,
       (SELECT Version FROM cfg.TemplateVersion WITH (NOLOCK) WHERE Id = @tv) AS template, @td AS table_def_id, @doc AS iso_document_id;
SELECT @s = Version FROM cfg.TemplateVersion WITH (NOLOCK) WHERE Id = @tv;
PRINT 'WX|template|' + ISNULL(@s, 'none');
PRINT 'WX|iso_tabledef|' + ISNULL(CAST(@td AS varchar(20)), N'відсутня');
PRINT 'WX|iso_doc|' + ISNULL(CAST(@doc AS varchar(20)), N'відсутня');

/* --- список методологій wmeth --- */
CREATE TABLE #wm (code varchar(60) NOT NULL, base bigint NULL);
DECLARE @raw nvarchar(4000) = LTRIM(RTRIM(N'$(wmeth)')) + N';', @pos int, @tok nvarchar(100), @eq int;
WHILE LEN(@raw) > 0
BEGIN
    SET @pos = CHARINDEX(N';', @raw);
    SET @tok = LTRIM(RTRIM(LEFT(@raw, @pos - 1)));
    SET @raw = SUBSTRING(@raw, @pos + 1, 4000);
    IF @tok = N'' OR @tok = N'NONE' CONTINUE;
    SET @eq = CHARINDEX(N'@', @tok);
    IF @eq = 0
        INSERT #wm VALUES (@tok, NULL)
    ELSE
        INSERT #wm VALUES (LEFT(@tok, @eq - 1), TRY_CAST(SUBSTRING(@tok, @eq + 1, 40) AS bigint));
END

/* --- групи колонок таблиці ізоляції --- */
CREATE TABLE #col (ColumnDefId int PRIMARY KEY, Code nvarchar(200), grp varchar(8));
INSERT #col
SELECT c.Id, c.Code,
  CASE WHEN c.Code LIKE 'SubstanceConsumption20Dgr1atm[_]M[0-9][0-9]' THEN 'G20'
       WHEN c.Code LIKE 'SubstanceConsumption[_]M[0-9][0-9]' THEN 'KgH'
       WHEN c.Code LIKE 'SubstanceConsumption[_]M[0-9][0-9][_]%' THEN 'Kg'
       WHEN c.Code LIKE 'OperatingTimeReleaseOnColdVentYesNo[_]M%' THEN 'OT'
       WHEN c.Code IN ('Density20','TotalSulphurContent','Vddg','Vwdg') THEN 'KEY4'
       ELSE 'OTHER' END
FROM cfg.ColumnDef c WITH (NOLOCK) WHERE c.TableDefId = @td AND c.IsDeleted = 0;
SELECT grp, COUNT(*) AS columns_n FROM #col GROUP BY grp ORDER BY grp;
SELECT @n = COUNT(*) FROM #col WHERE grp = 'G20';
PRINT 'WX|cols_g20|' + CAST(ISNULL(@n,0) AS varchar(20));

/* ===== (а) рядки та комірки ===== */
PRINT N'== a1. Рядки таблиці ізоляції по періодах ==';
SELECT tr.PeriodKey, COUNT(*) AS rows_n, SUM(CASE WHEN tr.IsOrphaned = 1 THEN 1 ELSE 0 END) AS orphaned,
       SUM(CASE WHEN tr.ModifiedAt >= @since THEN 1 ELSE 0 END) AS modified_since
FROM doc.TableRow tr WITH (NOLOCK) JOIN doc.TableInstance ti WITH (NOLOCK) ON ti.Id = tr.TableInstanceId AND ti.PeriodKey = tr.PeriodKey
WHERE ti.DocumentId = @doc AND ti.TableDefId = @td AND tr.IsDeleted = 0 AND tr.PeriodKey BETWEEN @pfrom AND @pto
GROUP BY tr.PeriodKey ORDER BY tr.PeriodKey;
SELECT @n = COUNT(*) FROM doc.TableRow tr WITH (NOLOCK) JOIN doc.TableInstance ti WITH (NOLOCK) ON ti.Id = tr.TableInstanceId AND ti.PeriodKey = tr.PeriodKey
 WHERE ti.DocumentId = @doc AND ti.TableDefId = @td AND tr.IsDeleted = 0 AND tr.PeriodKey BETWEEN @pfrom AND @pto;
SELECT @n2 = COUNT(*) FROM doc.TableRow tr WITH (NOLOCK) JOIN doc.TableInstance ti WITH (NOLOCK) ON ti.Id = tr.TableInstanceId AND ti.PeriodKey = tr.PeriodKey
 WHERE ti.DocumentId = @doc AND ti.TableDefId = @td AND tr.IsDeleted = 0 AND tr.PeriodKey BETWEEN @pfrom AND @pto AND tr.RowKey LIKE '%Legacy%';
PRINT 'WX|iso_rows|' + CAST(ISNULL(@n,0) AS varchar(20));
PRINT 'WX|iso_rows_legacy_key|' + CAST(ISNULL(@n2,0) AS varchar(20));

PRINT N'== a2. Комірки по групах колонок x період ==';
SELECT cv.PeriodKey, k.grp, COUNT_BIG(*) AS cells,
       SUM(CASE WHEN cv.IsEmpty = 1 THEN 1 ELSE 0 END) AS empty_n,
       SUM(CASE WHEN cv.IsEmpty = 0 AND cv.IsCalculated = 0 THEN 1 ELSE 0 END) AS entered,
       SUM(CASE WHEN cv.IsEmpty = 0 AND cv.IsCalculated = 1 THEN 1 ELSE 0 END) AS calculated,
       SUM(CASE WHEN cv.ValueString LIKE '#%' THEN 1 ELSE 0 END) AS err_cells
FROM doc.CellValue cv WITH (NOLOCK)
JOIN #col k ON k.ColumnDefId = cv.ColumnDefId
JOIN doc.TableRow tr WITH (NOLOCK) ON tr.Id = cv.TableRowId AND tr.PeriodKey = cv.PeriodKey AND tr.IsDeleted = 0
JOIN doc.TableInstance ti WITH (NOLOCK) ON ti.Id = tr.TableInstanceId AND ti.PeriodKey = tr.PeriodKey AND ti.DocumentId = @doc AND ti.TableDefId = @td
WHERE cv.TableDefId = @td AND cv.PeriodKey BETWEEN @pfrom AND @pto
GROUP BY cv.PeriodKey, k.grp ORDER BY cv.PeriodKey, k.grp;

DECLARE @g varchar(8), @c bigint, @e bigint, @en bigint, @ca bigint;
DECLARE @grps TABLE (grp varchar(8));
INSERT @grps VALUES ('G20'),('KgH'),('Kg'),('OT'),('KEY4'),('OTHER');
DECLARE gc CURSOR LOCAL FAST_FORWARD FOR SELECT grp FROM @grps;
OPEN gc; FETCH NEXT FROM gc INTO @g;
WHILE @@FETCH_STATUS = 0
BEGIN
    SELECT @c = COUNT_BIG(*),
           @e = SUM(CASE WHEN cv.IsEmpty = 1 THEN 1 ELSE 0 END),
           @en = SUM(CASE WHEN cv.IsEmpty = 0 AND cv.IsCalculated = 0 THEN 1 ELSE 0 END),
           @ca = SUM(CASE WHEN cv.IsEmpty = 0 AND cv.IsCalculated = 1 THEN 1 ELSE 0 END)
    FROM doc.CellValue cv WITH (NOLOCK)
    JOIN #col k ON k.ColumnDefId = cv.ColumnDefId AND k.grp = @g
    JOIN doc.TableRow tr WITH (NOLOCK) ON tr.Id = cv.TableRowId AND tr.PeriodKey = cv.PeriodKey AND tr.IsDeleted = 0
    JOIN doc.TableInstance ti WITH (NOLOCK) ON ti.Id = tr.TableInstanceId AND ti.PeriodKey = tr.PeriodKey AND ti.DocumentId = @doc AND ti.TableDefId = @td
    WHERE cv.TableDefId = @td AND cv.PeriodKey BETWEEN @pfrom AND @pto;
    PRINT 'WX|iso_' + @g + '_cells|' + CAST(ISNULL(@c,0) AS varchar(20));
    PRINT 'WX|iso_' + @g + '_empty|' + CAST(ISNULL(@e,0) AS varchar(20));
    PRINT 'WX|iso_' + @g + '_entered|' + CAST(ISNULL(@en,0) AS varchar(20));
    PRINT 'WX|iso_' + @g + '_calculated|' + CAST(ISNULL(@ca,0) AS varchar(20));
    FETCH NEXT FROM gc INTO @g;
END
CLOSE gc; DEALLOCATE gc;

PRINT N'== a4. Колонки KEY4: комірок ==';
SELECT k.Code, COUNT_BIG(*) AS cells, SUM(CASE WHEN cv.IsEmpty = 0 THEN 1 ELSE 0 END) AS non_empty
FROM doc.CellValue cv WITH (NOLOCK) JOIN #col k ON k.ColumnDefId = cv.ColumnDefId AND k.grp = 'KEY4'
JOIN doc.TableRow tr WITH (NOLOCK) ON tr.Id = cv.TableRowId AND tr.PeriodKey = cv.PeriodKey AND tr.IsDeleted = 0
JOIN doc.TableInstance ti WITH (NOLOCK) ON ti.Id = tr.TableInstanceId AND ti.PeriodKey = tr.PeriodKey AND ti.DocumentId = @doc AND ti.TableDefId = @td
WHERE cv.TableDefId = @td AND cv.PeriodKey BETWEEN @pfrom AND @pto GROUP BY k.Code ORDER BY k.Code;
SELECT @n = MIN(x.c) FROM (SELECT COUNT_BIG(*) AS c
  FROM doc.CellValue cv WITH (NOLOCK) JOIN #col k ON k.ColumnDefId = cv.ColumnDefId AND k.grp = 'KEY4'
  JOIN doc.TableRow tr WITH (NOLOCK) ON tr.Id = cv.TableRowId AND tr.PeriodKey = cv.PeriodKey AND tr.IsDeleted = 0
  JOIN doc.TableInstance ti WITH (NOLOCK) ON ti.Id = tr.TableInstanceId AND ti.PeriodKey = tr.PeriodKey AND ti.DocumentId = @doc AND ti.TableDefId = @td
  WHERE cv.TableDefId = @td AND cv.PeriodKey BETWEEN @pfrom AND @pto GROUP BY k.Code) x;
PRINT 'WX|iso_key4_min_cells|' + CAST(ISNULL(@n,0) AS varchar(20));

/* ===== (б) aud.CellChange після since: Origin x група ===== */
PRINT N'== b1. Правки комірок документа ізоляції після since: Origin x група ==';
SELECT cc.Origin, k.grp, COUNT_BIG(*) AS changes, COUNT(DISTINCT cc.TableRowId) AS rows_n, COUNT(DISTINCT cc.PeriodKey) AS periods
FROM aud.CellChange cc WITH (NOLOCK) JOIN #col k ON k.ColumnDefId = cc.ColumnDefId
WHERE cc.DocumentId = @doc AND cc.ChangedAt >= @since
GROUP BY cc.Origin, k.grp ORDER BY cc.Origin, k.grp;
SELECT @n = COUNT(*) FROM (
  SELECT DISTINCT cc.PeriodKey, cc.TableRowId FROM aud.CellChange cc WITH (NOLOCK) JOIN #col k ON k.ColumnDefId = cc.ColumnDefId AND k.grp = 'G20'
  WHERE cc.DocumentId = @doc AND cc.ChangedAt >= @since) g20r
  WHERE EXISTS (SELECT 1 FROM aud.CellChange c2 WITH (NOLOCK) JOIN #col k2 ON k2.ColumnDefId = c2.ColumnDefId AND k2.grp IN ('KgH','Kg')
                WHERE c2.DocumentId = @doc AND c2.ChangedAt >= @since AND c2.PeriodKey = g20r.PeriodKey AND c2.TableRowId = g20r.TableRowId AND c2.Origin <> 'Recalculation');
PRINT 'WX|ch_rows_g20_and_kgh_kg_nonrecalc|' + CAST(ISNULL(@n,0) AS varchar(20));
DECLARE @og varchar(8);
DECLARE gc2 CURSOR LOCAL FAST_FORWARD FOR SELECT grp FROM @grps;
OPEN gc2; FETCH NEXT FROM gc2 INTO @og;
WHILE @@FETCH_STATUS = 0
BEGIN
    SELECT @c = COUNT_BIG(*), @e = SUM(CASE WHEN cc.Origin = 'Recalculation' THEN 1 ELSE 0 END)
    FROM aud.CellChange cc WITH (NOLOCK) JOIN #col k ON k.ColumnDefId = cc.ColumnDefId AND k.grp = @og
    WHERE cc.DocumentId = @doc AND cc.ChangedAt >= @since;
    PRINT 'WX|ch_' + @og + '_total|' + CAST(ISNULL(@c,0) AS varchar(20));
    PRINT 'WX|ch_' + @og + '_recalc|' + CAST(ISNULL(@e,0) AS varchar(20));
    FETCH NEXT FROM gc2 INTO @og;
END
CLOSE gc2; DEALLOCATE gc2;

/* ===== (в) результати заданих методологій ===== */
PRINT N'== c1. Current по методологіях wmeth ==';
SELECT m.Code, COUNT_BIG(*) AS current_results, COUNT(DISTINCT r.MethodologyVersionId) AS versions_used, MAX(r.MethodologyVersionId) AS max_mv_id,
       SUM(CASE WHEN r.Value IS NULL THEN 1 ELSE 0 END) AS null_values, COUNT(DISTINCT r.DocumentId) AS docs, COUNT(DISTINCT r.PeriodKey) AS periods
FROM calc.CalculationResult r WITH (NOLOCK)
JOIN calc.CalculationRun run WITH (NOLOCK) ON run.Id = r.CalculationRunId AND run.Status = 'Current'
JOIN calc.MethodologyVersion mv WITH (NOLOCK) ON mv.Id = r.MethodologyVersionId
JOIN calc.Methodology m WITH (NOLOCK) ON m.Id = mv.MethodologyId
WHERE m.Code IN (SELECT code FROM #wm)
GROUP BY m.Code ORDER BY m.Code;
DECLARE @wc varchar(60), @wb bigint;
DECLARE wcur CURSOR LOCAL FAST_FORWARD FOR SELECT code, base FROM #wm ORDER BY code;
OPEN wcur; FETCH NEXT FROM wcur INTO @wc, @wb;
WHILE @@FETCH_STATUS = 0
BEGIN
    SELECT @n = COUNT_BIG(*), @n2 = COUNT(DISTINCT r.MethodologyVersionId), @n3 = SUM(CASE WHEN r.Value IS NULL THEN 1 ELSE 0 END)
    FROM calc.CalculationResult r WITH (NOLOCK)
    JOIN calc.CalculationRun run WITH (NOLOCK) ON run.Id = r.CalculationRunId AND run.Status = 'Current'
    JOIN calc.MethodologyVersion mv WITH (NOLOCK) ON mv.Id = r.MethodologyVersionId
    JOIN calc.Methodology m WITH (NOLOCK) ON m.Id = mv.MethodologyId AND m.Code = @wc;
    PRINT 'WX|m_' + @wc + '_current|' + CAST(ISNULL(@n,0) AS varchar(20));
    PRINT 'WX|m_' + @wc + '_versions_used|' + CAST(ISNULL(@n2,0) AS varchar(20));
    PRINT 'WX|m_' + @wc + '_null_values|' + CAST(ISNULL(@n3,0) AS varchar(20));
    PRINT 'WX|m_' + @wc + '_base|' + ISNULL(CAST(@wb AS varchar(20)), 'none');
    FETCH NEXT FROM wcur INTO @wc, @wb;
END
CLOSE wcur; DEALLOCATE wcur;
SELECT @n = COUNT(*) FROM (SELECT r.DocumentId, r.PeriodKey, r.MethodologyVersionId, r.OutputCode, r.SourceRowKey
       FROM calc.CalculationResult r WITH (NOLOCK) JOIN calc.CalculationRun run WITH (NOLOCK) ON run.Id = r.CalculationRunId AND run.Status = 'Current'
       JOIN calc.MethodologyVersion mv WITH (NOLOCK) ON mv.Id = r.MethodologyVersionId
       JOIN calc.Methodology m WITH (NOLOCK) ON m.Id = mv.MethodologyId AND m.Code IN (SELECT code FROM #wm)
       GROUP BY r.DocumentId, r.PeriodKey, r.MethodologyVersionId, r.OutputCode, r.SourceRowKey HAVING COUNT(DISTINCT r.CalculationRunId) > 1) d;
PRINT 'WX|dup_current_result_runs|' + CAST(@n AS varchar(20));

/* ===== (г) правила, прив'язки ===== */
SELECT @n = COUNT(*) FROM itg.JobProgress WITH (NOLOCK) WHERE COALESCE(UpdatedAt, CreatedAt) >= @since AND Message LIKE '%recalcCategoryRuleRejected%';
PRINT 'WX|jobs_category_rule_rejected|' + CAST(@n AS varchar(20));
SELECT @n = COUNT(*) FROM itg.JobProgress WITH (NOLOCK) WHERE COALESCE(UpdatedAt, CreatedAt) >= @since AND Message LIKE '%recalcNoMatchingRule%';
PRINT 'WX|jobs_no_matching_rule|' + CAST(@n AS varchar(20));
SELECT @n = SUM(CASE WHEN IsActive = 1 THEN 1 ELSE 0 END), @n2 = SUM(CASE WHEN IsActive = 0 THEN 1 ELSE 0 END) FROM cfg.CalculationBinding WITH (NOLOCK);
PRINT 'WX|bindings_active|' + CAST(ISNULL(@n,0) AS varchar(20));
PRINT 'WX|bindings_inactive|' + CAST(ISNULL(@n2,0) AS varchar(20));

/* ===== (д) TableInstanceId NULL у нових результатах/Step/Input ===== */
PRINT N'== e. TableInstanceId NULL у нових (StartedAt >= since) ==';
DECLARE @tb TABLE (k varchar(12), tbl nvarchar(200));
INSERT @tb VALUES ('result','calc.CalculationResult'),('step','calc.CalculationStep'),('input','calc.CalculationInput');
DECLARE @tk varchar(12), @tt nvarchar(200), @sql nvarchar(1000);
DECLARE tc CURSOR LOCAL FAST_FORWARD FOR SELECT k, tbl FROM @tb;
OPEN tc; FETCH NEXT FROM tc INTO @tk, @tt;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF COL_LENGTH(@tt, 'TableInstanceId') IS NULL
        PRINT 'WX|tii_' + @tk + '_new|' + N'відсутня';
    ELSE
    BEGIN
        SET @sql = N'SELECT @a = COUNT_BIG(*), @b = SUM(CASE WHEN x.TableInstanceId IS NULL THEN 1 ELSE 0 END) FROM ' + @tt
                 + N' x WITH (NOLOCK) JOIN calc.CalculationRun run WITH (NOLOCK) ON run.Id = x.CalculationRunId WHERE run.StartedAt >= @s;';
        EXEC sp_executesql @sql, N'@s datetime2, @a bigint OUTPUT, @b bigint OUTPUT', @s = @since, @a = @n OUTPUT, @b = @n2 OUTPUT;
        PRINT 'WX|tii_' + @tk + '_new|' + CAST(ISNULL(@n,0) AS varchar(20));
        PRINT 'WX|tii_' + @tk + '_null|' + CAST(ISNULL(@n2,0) AS varchar(20));
    END
    FETCH NEXT FROM tc INTO @tk, @tt;
END
CLOSE tc; DEALLOCATE tc;
DROP TABLE #col;
DROP TABLE #wm;
PRINT 'WX|done|1';
