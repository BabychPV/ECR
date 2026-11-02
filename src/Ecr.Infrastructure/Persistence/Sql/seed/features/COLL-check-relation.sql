-- COLL:check-relation ── ru/kz підказка MapJson для виду Check; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'tables.mapJsonCheckHint', N'ru', N'Обязательно для «Проверки»: JSON-объект с полями "left" (столбец источника) и "right" (столбец приёмника); необязательны "tolerance", "toleranceKind" (abs или rel) и "severity" (Warn, Block или Info).'),
    (N'tables.mapJsonCheckHint', N'kz', N'«Тексеру» үшін міндетті: "left" (көз бағаны) және "right" (қабылдағыш бағаны) өрістері бар JSON-нысан; "tolerance", "toleranceKind" (abs немесе rel) және "severity" (Warn, Block немесе Info) міндетті емес.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:check-relation ── кінець секції ──

-- COLL:check-relation ── Підказка MapJson для виду зв'язку Check: поле обов'язкове (RelationSpecParser.ParseCheck) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'tables.mapJsonCheckHint', N'en', N'Required for Check: a JSON object with "left" (source column) and "right" (target column); optional "tolerance", "toleranceKind" (abs or rel) and "severity" (Warn, Block or Info).', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:check-relation ── кінець секції ──
