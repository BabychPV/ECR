-- COLL:l26rowreject ── ru/kz L2-6: правило категорії відмовило рядкам; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'jobs.recalcCategoryRuleRejected', N'ru', N'Правило категории отклонило строки: {count} (номера: {rows}): ключ категории для них не получен. Эти строки не рассчитаны, остальные рассчитаны.'),
    (N'jobs.recalcCategoryRuleRejected', N'kz', N'Санат ережесі {count} жолды қабылдамады (нөмірлері: {rows}): олар үшін санат кілті алынбады. Бұл жолдар есептелмеді, қалғандары есептелді.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:l26rowreject ── кінець секції ──

-- COLL:l26rowreject ── L2-6: помилка правила категорії відмовляє лише своєму рядку; ru/kz — порцією COLL:l26rowreject нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'jobs.recalcCategoryRuleRejected', N'en', N'The category rule rejected {count} row(s) (rows: {rows}): it gave no category key for them. These rows were not calculated; the other rows were.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:l26rowreject ── кінець секції ──
