-- COLL:stale-mytasks ── ru/kz блока «Потребують перерахунку» у My tasks; власна порція
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'document.staleResults.leaveReminder', N'ru', N'В документе №{id} результаты расчёта всё ещё устарели. Пересчитайте его или найдите позже в «Моих задачах» в блоке «Требуют пересчёта».'),
    (N'document.staleResults.leaveReminder', N'kz', N'№{id} құжатында есептеу нәтижелері әлі де ескірген. Оны қайта есептеңіз немесе кейін «Менің тапсырмаларым» ішіндегі «Қайта есептеуді қажет етеді» блогынан табыңыз.'),
    (N'jobs.staleDocuments.title', N'ru', N'Требуют пересчёта'),
    (N'jobs.staleDocuments.title', N'kz', N'Қайта есептеуді қажет етеді'),
    (N'jobs.staleDocuments.hint', N'ru', N'Входные данные этих документов изменены после последнего расчёта. Откройте документ и нажмите «Пересчитать».'),
    (N'jobs.staleDocuments.hint', N'kz', N'Бұл құжаттардың кіріс деректері соңғы есептеуден кейін өзгерген. Құжатты ашып, «Қайта есептеу» түймесін басыңыз.'),
    (N'jobs.staleDocuments.since', N'ru', N'Изменено {date}'),
    (N'jobs.staleDocuments.since', N'kz', N'Өзгертілген күні: {date}'),
    (N'jobs.staleDocuments.open', N'ru', N'Открыть'),
    (N'jobs.staleDocuments.open', N'kz', N'Ашу'),
    (N'jobs.staleDocuments.error', N'ru', N'Не удалось загрузить документы, требующие пересчёта.'),
    (N'jobs.staleDocuments.error', N'kz', N'Қайта есептеуді қажет ететін құжаттарды жүктеу мүмкін болмады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:stale-mytasks ── кінець секції ──
-- COLL:stale-mytasks ── блок «Needs recalculation» у My tasks і нагадування при виході з документа; ru/kz — порцією COLL:stale-mytasks нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'document.staleResults.leaveReminder', N'en', N'Document #{id} still has out-of-date results. Recalculate it, or find it later in My tasks under "Needs recalculation".', 1),
    (N'jobs.staleDocuments.title', N'en', N'Needs recalculation', 1),
    (N'jobs.staleDocuments.hint', N'en', N'Input data in these documents changed after the last calculation. Open a document and press Recalculate.', 1),
    (N'jobs.staleDocuments.since', N'en', N'Changed {date}', 1),
    (N'jobs.staleDocuments.open', N'en', N'Open', 1),
    (N'jobs.staleDocuments.error', N'en', N'Could not load the documents that need recalculation.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:stale-mytasks ── кінець секції ──
