-- COLL:navcollapse ── ru/kz: бічне меню згортається до іконок; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'nav.collapse', N'ru', N'Свернуть меню'),
    (N'nav.collapse', N'kz', N'Мәзірді жию'),
    (N'nav.expand', N'ru', N'Развернуть меню'),
    (N'nav.expand', N'kz', N'Мәзірді жаю')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:navcollapse ── кінець секції ──
-- COLL:navcollapse ── бічне меню згортається до іконок (кнопка-шеврон); ru/kz — порцією COLL:navcollapse у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'nav.collapse', N'en', N'Collapse menu', 1),
    (N'nav.expand', N'en', N'Expand menu', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:navcollapse ── кінець секції ──
