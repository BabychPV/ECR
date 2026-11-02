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
-- COLL:l21scope ── L2-1: область формули (scope) у PUT формули й порівнянні версій; ru/kz — порцією COLL:l21scope нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CALC-0422.formulaScopeInvalid', N'en', N'The scope "{scope}" of formula "{code}" is not valid: use Substance (once per substance) or Row (once per row).', 1),
    (N'methodologyDiffField.scope', N'en', N'Scope', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:l21scope ── кінець секції ──
