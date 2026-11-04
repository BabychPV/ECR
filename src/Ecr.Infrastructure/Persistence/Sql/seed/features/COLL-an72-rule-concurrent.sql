-- COLL:an72-rule-concurrent ── ru/kz: AN-72 / N2-05: два одночасні перші PUT category-rule (UQ_CategoryRule_Version) -> 409; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-0409.categoryRuleConcurrent', N'ru', N'Правило категории этой версии только что создал кто-то другой. Перечитайте версию и сохраните правило ещё раз.'),
    (N'err.ECR-CALC-0409.categoryRuleConcurrent', N'kz', N'Осы нұсқаның санат ережесін жаңа ғана басқа біреу жасады. Нұсқаны қайта жүктеп, ережені қайтадан сақтаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an72-rule-concurrent ── кінець секції ──

-- COLL:an72-rule-concurrent ── AN-72 / N2-05: два одночасні перші PUT category-rule (UQ_CategoryRule_Version) -> 409; ru/kz — порцією COLL:an72-rule-concurrent нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CALC-0409.categoryRuleConcurrent', N'en', N'Someone else has just created the category rule of this version. Reload the version and save the rule again.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an72-rule-concurrent ── кінець секції ──
