-- COLL:a11y-pass ── ru/kz: зауваження успішного PATCH; 403 у каскаді гранта; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'grid.patchNoticesTitle', N'ru', N'Сохранено с замечаниями: {count}'),
    (N'grid.patchNoticesTitle', N'kz', N'Ескертпелермен сақталды: {count}')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:a11y-pass ── кінець секції ──
-- COLL:a11y-pass ── зауваження успішного PATCH у гріді; 403 у каскаді гранта (A1-05); ru/kz — порцією COLL:a11y-pass у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'grid.patchNoticesTitle', N'en', N'Saved with remarks: {count}', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:a11y-pass ── кінець секції ──
