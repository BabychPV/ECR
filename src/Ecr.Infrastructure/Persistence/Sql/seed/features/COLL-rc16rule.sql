-- COLL:rc16rule ── ru/kz: відмова збереження правила категорії з невідомою константою чи не-Row формулою; власна порція
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-0422.categoryRuleUnknownConstant', N'ru', N'Правило категории ссылается на константу CST.{code}, которой в этой версии нет.'),
    (N'err.ECR-CALC-0422.categoryRuleUnknownConstant', N'kz', N'Санат ережесі осы нұсқада жоқ CST.{code} тұрақтысына сілтейді.'),
    (N'err.ECR-CALC-0422.categoryRuleBadFormula', N'ru', N'Правило категории ссылается на !{formula}: оно видит только Row-формулы версии (считается один раз на строку, до цикла веществ).'),
    (N'err.ECR-CALC-0422.categoryRuleBadFormula', N'kz', N'Санат ережесі !{formula} формуласына сілтейді: ол нұсқаның тек Row-формулаларын көреді (жолға бір рет, заттар циклінен бұрын есептеледі).')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:rc16rule ── кінець секції ──
-- COLL:rc16rule ── RC16-2: правило категорії перевіряє посилання вже при збереженні; ru/kz — порцією COLL:rc16rule нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CALC-0422.categoryRuleUnknownConstant', N'en', N'The category rule references constant CST.{code}, which this version does not define.', 1),
    (N'err.ECR-CALC-0422.categoryRuleBadFormula', N'en', N'The category rule references !{formula}: it can only read Row formulas of the version (it runs once per row, before the substance loop).', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:rc16rule ── кінець секції ──
