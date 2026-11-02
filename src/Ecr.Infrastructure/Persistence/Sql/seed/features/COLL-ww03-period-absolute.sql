-- COLL:ww03-period-absolute ── ru/kz: WW-03: [Period:#k] — абсолютний період року; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'expr.periodAbsoluteRange', N'ru', N'Абсолютный период «[Period:#k]» — число от 1 до 99.'),
    (N'expr.periodAbsoluteRange', N'kz', N'«[Period:#k]» абсолютті кезеңі — 1-ден 99-ға дейінгі сан болуы тиіс.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ww03-period-absolute ── кінець секції ──
