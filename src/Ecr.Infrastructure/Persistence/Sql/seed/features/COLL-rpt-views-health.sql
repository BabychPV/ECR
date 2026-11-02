-- COLL:rpt-views-health ── Перевірка `reportviews` на /health/ready: вʼюхи rpt.v_* для SSRS не створено (`ReportViewsHealthCheck`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'health.check.reportviews',               N'en', N'Report views (SSRS)', 1),
    (N'health.reportviews.ok',                  N'en', N'Report views (rpt.v_*) are generated for all published template versions.', 1),
    (N'health.reportviews.failed',              N'en', N'Report views rpt.v_* were not created for some published template versions; SSRS does not see them: {details}. Publication is not affected. Fix the template (at most 250 columns per table; unique template/sheet/table codes) and restart the application or run EXEC rpt.usp_GenerateTemplateViews.', 1)
    -- ru/kz — окремою порцією `I18N` (COLL:rpt-views-health).
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:rpt-views-health ── кінець секції ──
