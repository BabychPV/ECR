-- COLL:l701depth ── ru/kz L7-01: заголовок коду ECR-EXPR-0422; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-EXPR-0422', N'ru', N'Выражение слишком сложное'),
    (N'err.ECR-EXPR-0422', N'kz', N'Өрнек тым күрделі')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:l701depth ── кінець секції ──
