-- COLL:rc15c-recalc ── ru/kz: перерахунок вимкнений лише коли подані ВСІ аркуші; власна порція
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'workflow.recalculateAllSubmitted', N'ru', N'Все листы периода {period} поданы или утверждены: пересчитывать нечего. Сначала верните лист на правки (действие листа «Переоткрыть для правок»).'),
    (N'workflow.recalculateAllSubmitted', N'kz', N'{period} кезеңінің барлық парақтары тапсырылған немесе бекітілген: қайта есептейтін ештеңе жоқ. Алдымен парақты түзетуге қайтарыңыз (парақтың «Түзету үшін қайта ашу» әрекеті).')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:rc15c-recalc ── кінець секції ──
-- COLL:rc15c-recalc ── перерахунок вимкнений лише коли подані ВСІ аркуші; ru/kz — порцією COLL:rc15c-recalc нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'workflow.recalculateAllSubmitted', N'en', N'All sheets of period {period} are submitted or approved: there is nothing to recalculate. Return a sheet for edits first (the sheet''s «Return for edits» action).', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:rc15c-recalc ── кінець секції ──
