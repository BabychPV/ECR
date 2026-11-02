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
-- COLL:ui-sheet-bottom ── нижній рядок під таблицею: підпис горизонтальної прокрутки; ru/kz — порцією COLL:ui-sheet-bottom у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'grid.hscroll.label', N'en', N'Horizontal scroll of the table', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui-sheet-bottom ── кінець секції ──
