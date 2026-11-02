-- COLL:an80-carryover ── ru/kz: AN-80 / N2-04: перенос результатов поданных листов больше потолка; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-0422.carryOverTooLarge', N'ru', N'Перенос результатов поданных листов документа {documentId} за период {periodKey} скопировал бы {count} строк, что больше предела {max}. Прогон не применён, прежние результаты остаются актуальными: сузьте привязку до нужной таблицы.'),
    (N'err.ECR-CALC-0422.carryOverTooLarge', N'kz', N'{documentId} құжатының {periodKey} кезеңіндегі тапсырылған парақтардың нәтижелерін көшіру {count} жолды көшірер еді, бұл {max} шегінен артық. Есептеу қолданылған жоқ, алдыңғы нәтижелер өзекті болып қалады: байланыстыруды қажетті кестеге тарылтыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an80-carryover ── кінець секції ──

