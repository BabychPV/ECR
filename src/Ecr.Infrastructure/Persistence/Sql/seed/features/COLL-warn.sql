-- COLL:warn ── ru/kz позначки «поза вікном» в журналі змін (ФВ-2.16, D-239); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'audit.outOfWindow', N'ru', N'вне окна'),
    (N'audit.outOfWindow', N'kz', N'терезеден тыс')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:warn ── кінець секції ──

