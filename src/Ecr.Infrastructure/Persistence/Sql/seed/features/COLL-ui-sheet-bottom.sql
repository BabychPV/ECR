-- COLL:ui-sheet-bottom ── ru/kz: нижній рядок під таблицею; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'grid.hscroll.label', N'ru', N'Горизонтальная прокрутка таблицы'),
    (N'grid.hscroll.label', N'kz', N'Кестенің көлденең айналдыруы')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui-sheet-bottom ── кінець секції ──
