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

