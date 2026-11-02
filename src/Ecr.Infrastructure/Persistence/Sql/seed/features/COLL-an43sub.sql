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
-- COLL:an43sub ── D-285: право подання аркуша для рівня Write (підпис у ролі й повідомлення); ru/kz — порцією COLL:an43sub у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'permission.Document.Submit', N'en', N'Submit documents (with the Write level)', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an43sub ── кінець секції ──
