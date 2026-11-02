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

