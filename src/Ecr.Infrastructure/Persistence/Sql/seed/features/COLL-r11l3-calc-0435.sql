-- COLL:r11l3-calc-0435 ── ru/kz: R11-L3: заголовок коду ECR-CALC-0435; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-0435', N'ru', N'Ссылка на формулу неоднозначна'),
    (N'err.ECR-CALC-0435', N'kz', N'Формулаға сілтеме бір мәнді емес')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:r11l3-calc-0435 ── кінець секції ──

