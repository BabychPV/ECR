-- COLL:an37m ── ru/kz AN-37 L7-08: гонка двох публікацій на одну дату; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-0409.effectiveDateTakenNoVersion', N'ru', N'Другая версия этой методики опубликована с датой начала {effectiveFrom} в тот же момент. Две опубликованные версии с одинаковой датой начала делают выбор методики неоднозначным: перезагрузите методику и опубликуйте с другой датой.'),
    (N'err.ECR-CALC-0409.effectiveDateTakenNoVersion', N'kz', N'Осы әдістеменің басқа нұсқасы {effectiveFrom} басталу күнімен дәл сол сәтте жарияланды. Басталу күні бірдей екі жарияланған нұсқа әдістемені таңдауды екіұшты етеді: әдістемені қайта жүктеп, басқа күнмен жариялаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an37m ── кінець секції ──
-- COLL:an37m ── AN-37 L7-08: гонка двох публікацій на одну дату (версію-суперника база не називає); ru/kz — порцією COLL:an37m у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CALC-0409.effectiveDateTakenNoVersion', N'en', N'Another version of this methodology was published with effective date {effectiveFrom} at the same moment. Two published versions with the same start date make the methodology choice ambiguous: reload the methodology and publish with another date.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an37m ── кінець секції ──
