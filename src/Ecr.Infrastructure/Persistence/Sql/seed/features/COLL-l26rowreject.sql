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

