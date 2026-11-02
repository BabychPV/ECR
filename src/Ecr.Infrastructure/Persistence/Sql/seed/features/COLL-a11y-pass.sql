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
