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

-- COLL:an80-carryover ── AN-80 / N2-04: перенос результатів поданих аркушів більший за стелю; ru/kz — порцією COLL:an80-carryover нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CALC-0422.carryOverTooLarge', N'en', N'Carrying over the results of the submitted sheets of document {documentId} for period {periodKey} would copy {count} rows, more than the limit of {max}. The run was not applied and the previous results stay current: narrow the binding to the right table.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an80-carryover ── кінець секції ──
