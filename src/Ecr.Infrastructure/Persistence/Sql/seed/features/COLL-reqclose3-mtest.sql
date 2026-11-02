-- COLL:reqclose3-mtest ── ru/kz; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-0422.testCaseJsonInvalid', N'ru', N'Тест «{testCode}»: поле {field} не является корректным JSON ожидаемой формы ({reason}).'),
    (N'err.ECR-CALC-0422.testCaseJsonInvalid', N'kz', N'«{testCode}» тесі: {field} өрісі күтілетін пішімдегі дұрыс JSON емес ({reason}).')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:reqclose3-mtest ── кінець секції ──

