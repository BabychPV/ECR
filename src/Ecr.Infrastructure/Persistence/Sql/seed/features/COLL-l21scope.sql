-- COLL:l21scope ── ru/kz: область формули (scope) у PUT формули й порівнянні версій; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-0422.formulaScopeInvalid', N'ru', N'Область «{scope}» формулы «{code}» недопустима: допустимы Substance (на каждое вещество) и Row (один раз на строку).'),
    (N'err.ECR-CALC-0422.formulaScopeInvalid', N'kz', N'«{code}» формуласының «{scope}» аймағы жарамсыз: Substance (әр зат үшін) немесе Row (әр жол үшін бір рет) қолданылады.'),
    (N'methodologyDiffField.scope', N'ru', N'Область'),
    (N'methodologyDiffField.scope', N'kz', N'Қолдану аймағы')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:l21scope ── кінець секції ──
