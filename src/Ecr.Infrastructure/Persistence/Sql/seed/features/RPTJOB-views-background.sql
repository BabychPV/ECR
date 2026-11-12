-- RPTJOB:views-background ── ru/kz P-4b (D-479): вʼюхи SSRS після публікації — фоновою задачею; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'health.reportviews.pending', N'ru', N'Представления отчётов (rpt.v_*) обновляются в фоне после публикации шаблона (шаблоны: {templates}); SSRS увидит новую версию, когда обновление закончится.'),
    (N'health.reportviews.pending', N'kz', N'Есеп көріністері (rpt.v_*) үлгі жарияланғаннан кейін фонда жаңартылуда (үлгілер: {templates}); жаңарту аяқталғанда SSRS жаңа нұсқаны көреді.'),
    (N'jobs.kind.reportViews', N'ru', N'Обновление представлений отчётов'),
    (N'jobs.kind.reportViews', N'kz', N'Есеп көріністерін жаңарту')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- RPTJOB:views-background ── кінець секції ──
-- RPTJOB:views-background ── en P-4b (D-479); ru/kz — порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'health.reportviews.pending', N'en', N'Report views (rpt.v_*) are being updated in the background after a template publication (templates: {templates}); SSRS sees the new version once this finishes.', 1),
    (N'jobs.kind.reportViews', N'en', N'Update report views', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- RPTJOB:views-background ── кінець секції ──
