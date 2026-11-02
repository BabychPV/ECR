-- COLL:an25 ── ru/kz меж виразу (L7-01); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0422.expressionTooLong', N'ru', N'Выражение слишком длинное: {length} символов, допускается не более {max}. Сократите его или разделите на несколько.'),
    (N'err.ECR-REQ-0422.expressionTooLong', N'kz', N'Өрнек тым ұзын: {length} таңба, ең көбі {max} рұқсат етіледі. Оны қысқартыңыз немесе бірнешеге бөліңіз.'),
    (N'expr.chainTooLong', N'ru', N'В выражении больше {max} операторов подряд. Разбейте его на несколько формул.'),
    (N'expr.chainTooLong', N'kz', N'Өрнекте қатарынан {max} оператордан артық. Оны бірнеше формулаға бөліңіз.'),
    (N'expr.tooComplex', N'ru', N'Выражение слишком сложное для проверки. Разбейте его на несколько формул.'),
    (N'expr.tooComplex', N'kz', N'Өрнек тексеру үшін тым күрделі. Оны бірнеше формулаға бөліңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an25 ── кінець секції ──
