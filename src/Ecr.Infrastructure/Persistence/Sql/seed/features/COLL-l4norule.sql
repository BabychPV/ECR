-- COLL:l4norule ── ru/kz L-4: рядок без жодного правила; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'jobs.recalcNoMatchingRule', N'ru', N'Нет подходящего правила для строк: {count} (номера: {rows}). Эти строки не рассчитаны, остальные рассчитаны.'),
    (N'jobs.recalcNoMatchingRule', N'kz', N'{count} жол үшін сәйкес ереже жоқ (нөмірлері: {rows}). Бұл жолдар есептелмеді, қалғандары есептелді.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:l4norule ── кінець секції ──
