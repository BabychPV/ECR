-- LANDYEAR:l7ab-client ── ru/kz: LAND-YEAR L7a, Info інспектора документа «рік» — чия комірка (місяць-власник або рік) ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'inspector.info.period', N'ru', N'Период'),
    (N'inspector.info.period', N'kz', N'Кезең')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- LANDYEAR:l7ab-client ── кінець секції ──
-- LANDYEAR:l7ab-client ── LAND-YEAR L7a, Info інспектора документа «рік» (en) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'inspector.info.period', N'en', N'Period', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- LANDYEAR:l7ab-client ── кінець секції ──
