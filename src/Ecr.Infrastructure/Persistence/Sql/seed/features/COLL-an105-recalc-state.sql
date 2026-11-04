-- COLL:an105-recalc-state ── ru/kz: AN-105 / D2-03: стан аркуша чи періоду змінився під час перерахунку; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-0409.recalcStateChanged', N'ru', N'Пока документ {documentId} пересчитывался, лист был подан или период {periodKey} закрыт. Новые результаты не применены, актуальными остаются прежние; пересчёт повторится с новым состоянием.'),
    (N'err.ECR-CALC-0409.recalcStateChanged', N'kz', N'{documentId} құжаты қайта есептеліп жатқанда парақ тапсырылды немесе {periodKey} кезеңі жабылды. Жаңа нәтижелер қолданылмады, алдыңғылары өзекті болып қалады; қайта есептеу жаңа күймен қайталанады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an105-recalc-state ── кінець секції ──

-- COLL:an105-recalc-state ── AN-105 / D2-03: стан аркуша чи періоду змінився під час перерахунку; ru/kz — порцією COLL:an105-recalc-state нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CALC-0409.recalcStateChanged', N'en', N'While document {documentId} was being recalculated, a sheet was submitted or period {periodKey} was closed. The new results were not applied and the previous ones stay current; the recalculation repeats with the new state.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an105-recalc-state ── кінець секції ──
