-- COLL:r11l3-calc-0436 ── ru/kz: R11-L3: заголовок коду ECR-CALC-0436; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-0436', N'ru', N'Тип результата формулы не соответствует приёмнику'),
    (N'err.ECR-CALC-0436', N'kz', N'Формула нәтижесінің түрі қабылдағышқа сәйкес келмейді')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:r11l3-calc-0436 ── кінець секції ──
