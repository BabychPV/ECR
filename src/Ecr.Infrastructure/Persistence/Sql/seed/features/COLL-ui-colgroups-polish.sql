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
