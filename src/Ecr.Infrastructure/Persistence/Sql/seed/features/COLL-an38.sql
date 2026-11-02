-- COLL:an38 ── ru/kz розрахунків і методологій (L7); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'expr.ampersandNotConcat', N'ru', N'«&» в диалекте методик не объединяет текст: это побитовое И, и «6 & 3» равно 2. Для логического И используйте «and».'),
    (N'expr.ampersandNotConcat', N'kz', N'Әдістемелер диалектінде «&» мәтінді біріктірмейді: бұл биттік ЖӘНЕ, және «6 & 3» 2-ге тең. Логикалық ЖӘНЕ үшін «and» қолданыңыз.'),
    (N'expr.rangeNotAccepted', N'ru', N'{function} принимает одно значение, а не диапазон строк. Оберните диапазон в SUM, AVERAGE, MIN или MAX.'),
    (N'expr.rangeNotAccepted', N'kz', N'{function} жолдар ауқымын емес, бір мәнді қабылдайды. Ауқымды SUM, AVERAGE, MIN немесе MAX ішіне орналастырыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an38 ── кінець секції ──
-- COLL:an38 ── AN-38 (аудит 2026-10-03, L7): розрахунки й методології; ru/kz — порцією COLL:an38 у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'expr.ampersandNotConcat', N'en', N'"&" in the methodology dialect does not join text: it is bitwise AND, and "6 & 3" equals 2. Use "and" for a logical AND.', 1),
    (N'expr.rangeNotAccepted', N'en', N'{function} takes a single value, not a range of rows. Wrap the range in SUM, AVERAGE, MIN or MAX.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an38 ── кінець секції ──
