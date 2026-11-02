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
-- COLL:ui32crumbs ── UI-32: крихти у верхній смузі (підпис області навігації); ru/kz — порцією COLL:ui32crumbs у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'nav.breadcrumb', N'en', N'Breadcrumb', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui32crumbs ── кінець секції ──
