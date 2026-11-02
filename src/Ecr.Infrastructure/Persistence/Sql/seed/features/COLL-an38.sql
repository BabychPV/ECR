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
