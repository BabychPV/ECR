-- COLL:ui-colgroups-polish ── ru/kz: діалог груп колонок, «Other languages»; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'colgroups.otherLanguages', N'ru', N'Другие языки'),
    (N'colgroups.otherLanguages', N'kz', N'Басқа тілдер')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui-colgroups-polish ── кінець секції ──
-- COLL:ui-colgroups-polish ── діалог груп колонок: поле поточної мови, решта мов згорнуто; ru/kz — порцією COLL:ui-colgroups-polish нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'colgroups.otherLanguages', N'en', N'Other languages', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui-colgroups-polish ── кінець секції ──
