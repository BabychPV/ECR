-- COLL:stale-ui ── ru/kz банера "результати застаріли" у картці документа; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'document.staleResults.title', N'ru', N'Результаты расчёта устарели'),
    (N'document.staleResults.title', N'kz', N'Есептеу нәтижелері ескірген'),
    (N'document.staleResults.hint', N'ru', N'Входные данные изменены после последнего расчёта. Пересчитайте, чтобы обновить результаты.'),
    (N'document.staleResults.hint', N'kz', N'Кіріс деректер соңғы есептеуден кейін өзгерген. Нәтижелерді жаңарту үшін қайта есептеңіз.'),
    (N'document.staleResults.hintSince', N'ru', N'Входные данные изменены {date}, после последнего расчёта. Пересчитайте, чтобы обновить результаты.'),
    (N'document.staleResults.hintSince', N'kz', N'Кіріс деректер {date} күні, соңғы есептеуден кейін өзгерген. Нәтижелерді жаңарту үшін қайта есептеңіз.'),
    (N'documents.filterStale', N'ru', N'Требуют пересчёта ({count})'),
    (N'documents.filterStale', N'kz', N'Қайта есептеуді қажет етеді ({count})'),
    (N'documents.filterStaleNoCount', N'ru', N'Требуют пересчёта'),
    (N'documents.filterStaleNoCount', N'kz', N'Қайта есептеуді қажет етеді'),
    (N'nav.documents.staleCount', N'ru', N'{count} требуют пересчёта'),
    (N'nav.documents.staleCount', N'kz', N'{count} қайта есептеуді қажет етеді')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:stale-ui ── кінець секції ──
