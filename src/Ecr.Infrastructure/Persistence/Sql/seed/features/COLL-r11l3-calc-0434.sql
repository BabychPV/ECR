-- COLL:r11l3-calc-0434 ── ru/kz: R11-L3: заголовок коду ECR-CALC-0434; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-0434', N'ru', N'Константа методологии не может использоваться в формуле'),
    (N'err.ECR-CALC-0434', N'kz', N'Әдістеме тұрақтысын формулада қолдануға болмайды')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:r11l3-calc-0434 ── кінець секції ──

-- COLL:r11l3-calc-0434 ── R11-L3: заголовок коду ECR-CALC-0434; ru/kz — порцією COLL:r11l3-calc-0434 нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CALC-0434', N'en', N'Methodology constant cannot be used in a formula', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:r11l3-calc-0434 ── кінець секції ──
