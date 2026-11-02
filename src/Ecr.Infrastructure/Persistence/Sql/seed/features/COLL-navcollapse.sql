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
