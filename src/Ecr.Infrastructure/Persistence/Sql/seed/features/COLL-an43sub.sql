-- COLL:an43sub ── ru/kz D-285: підпис права Document.Submit; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'permission.Document.Submit', N'ru', N'Подача документов (при уровне «Запись»)'),
    (N'permission.Document.Submit', N'kz', N'Құжаттарды тапсыру («Жазу» деңгейінде)')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an43sub ── кінець секції ──
