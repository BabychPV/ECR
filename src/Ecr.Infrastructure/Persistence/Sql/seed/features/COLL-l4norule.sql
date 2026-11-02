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
-- COLL:l4norule ── L-4 Land Demo: рядок без жодного правила не рахується мовчки; ru/kz — порцією COLL:l4norule нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'jobs.recalcNoMatchingRule', N'en', N'No matching rule for {count} row(s) (rows: {rows}). These rows were not calculated; the other rows were.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:l4norule ── кінець секції ──
