-- COLL:ui32crumbs ── ru/kz UI-32: крихти у верхній смузі; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'nav.breadcrumb', N'ru', N'Навигационная цепочка'),
    (N'nav.breadcrumb', N'kz', N'Навигация жолы')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui32crumbs ── кінець секції ──
