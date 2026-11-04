-- COLL:an72-rule-length ── ru/kz: AN-72: правило категорії довше за межу формули (MethodologyCategoryRule.Normalize); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-0422.categoryRuleTooLong', N'ru', N'Правило категории длиннее {max} символов ({length}): сократите выражение или вынесите часть логики в Row-формулу.'),
    (N'err.ECR-CALC-0422.categoryRuleTooLong', N'kz', N'Санат ережесі {max} таңбадан ұзын ({length}): өрнекті қысқартыңыз немесе логиканың бір бөлігін Row-формулаға шығарыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an72-rule-length ── кінець секції ──

-- COLL:an72-rule-length ── AN-72: правило категорії довше за межу формули (MethodologyCategoryRule.Normalize); ru/kz — порцією COLL:an72-rule-length нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CALC-0422.categoryRuleTooLong', N'en', N'The category rule is longer than {max} characters ({length}): shorten the expression or move part of the logic into a Row formula.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an72-rule-length ── кінець секції ──
