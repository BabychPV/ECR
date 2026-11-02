-- COLL:result-table ── ru/kz: D-401, колонка «Таблиця» панелі результатів; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'documents.resultTable', N'ru', N'Таблица'),
    (N'documents.resultTable', N'kz', N'Кесте')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:result-table ── кінець секції ──

