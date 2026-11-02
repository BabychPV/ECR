-- COLL:an105-results-ceiling ── ru/kz: AN-105 / D2-05: актуальних результатів документа за період більше за стелю читання; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-0422.resultsTooLarge', N'ru', N'У документа {documentId} за период {periodKey} больше {max} результатов расчёта. Частично они не показываются, потому что неполный список дал бы неверные суммы: проверьте привязки методологий (привязка не к той таблице размножает строки).'),
    (N'err.ECR-CALC-0422.resultsTooLarge', N'kz', N'{documentId} құжатында {periodKey} кезеңі үшін {max}-тан астам есептеу нәтижесі бар. Олар ішінара көрсетілмейді, өйткені толық емес тізім қате қосындылар берер еді: әдістемелердің байланыстыруларын тексеріңіз (басқа кестеге байланыстыру жолдарды көбейтеді).')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an105-results-ceiling ── кінець секції ──

-- COLL:an105-results-ceiling ── AN-105 / D2-05: актуальних результатів документа за період більше за стелю читання; ru/kz — порцією COLL:an105-results-ceiling нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CALC-0422.resultsTooLarge', N'en', N'Document {documentId} has more than {max} calculation results for period {periodKey}. They are not shown partly, because a partial list would give wrong totals: check the methodology bindings (a binding on the wrong table multiplies rows).', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an105-results-ceiling ── кінець секції ──
