-- REG:rt25-client ── ru/kz сторінки впливу довідника і банера застарілості (RT-25, клієнт); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'registries.impact.open', N'ru', N'Затронутые документы'),
    (N'registries.impact.title', N'ru', N'Влияние справочника'),
    (N'registries.impact.hint', N'ru', N'Документы открытых периодов, результаты которых посчитаны методикой, читающей этот справочник. Закрытые периоды не пересчитываются и не показываются.'),
    (N'registries.impact.empty', N'ru', N'Нет затронутых документов'),
    (N'registries.impact.emptyHint', N'ru', N'Результаты открытых периодов не зависят от этого справочника.'),
    (N'registries.impact.count', N'ru', N'Показано {shown} из {total}'),
    (N'registries.impact.truncated', N'ru', N'Список достиг предела сервера: затронутых документов больше, чем показано.'),
    (N'registries.impact.select', N'ru', N'Выбор'),
    (N'registries.impact.selectDocument', N'ru', N'Выбрать {document}'),
    (N'registries.impact.document', N'ru', N'Документ'),
    (N'registries.impact.period', N'ru', N'Период'),
    (N'registries.impact.via', N'ru', N'Методики'),
    (N'registries.impact.recalculateAll', N'ru', N'Пересчитать затронутые'),
    (N'registries.impact.recalculateSelected', N'ru', N'Пересчитать выбранные ({count})'),
    (N'registries.impact.recalculateTitle', N'ru', N'Пересчёт затронутых документов'),
    (N'registries.impact.recalculateHint', N'ru', N'Пересчёт ставится в очередь; каждый документ пересчитывает своя задача.'),
    (N'registries.impact.recalculateConfirm', N'ru', N'Поставить в очередь'),
    (N'registries.impact.jobQueued', N'ru', N'Задача пересчёта {jobId}'),
    (N'registries.impact.jobUnreadable', N'ru', N'Состояние задачи недоступно с вашими правами.'),
    (N'registries.impact.progress', N'ru', N'Ход пересчёта'),
    (N'registries.impact.openJob', N'ru', N'Открыть в очереди задач'),
    (N'calculation.staleRegistry', N'ru', N'Справочник "{name}" изменён после расчёта'),
    (N'registries.impact.open', N'kz', N'Әсер еткен құжаттар'),
    (N'registries.impact.title', N'kz', N'Анықтамалықтың әсері'),
    (N'registries.impact.hint', N'kz', N'Нәтижелері осы анықтамалықты оқитын әдістемемен есептелген ашық кезеңдердің құжаттары. Жабық кезеңдер қайта есептелмейді және көрсетілмейді.'),
    (N'registries.impact.empty', N'kz', N'Әсер еткен құжаттар жоқ'),
    (N'registries.impact.emptyHint', N'kz', N'Ашық кезеңдердің нәтижелері бұл анықтамалыққа тәуелді емес.'),
    (N'registries.impact.count', N'kz', N'{total} ішінен {shown} көрсетілген'),
    (N'registries.impact.truncated', N'kz', N'Тізім сервер шегіне жетті: әсер еткен құжаттар көрсетілгеннен көп.'),
    (N'registries.impact.select', N'kz', N'Таңдау'),
    (N'registries.impact.selectDocument', N'kz', N'{document} таңдау'),
    (N'registries.impact.document', N'kz', N'Құжат'),
    (N'registries.impact.period', N'kz', N'Кезең'),
    (N'registries.impact.via', N'kz', N'Әдістемелер'),
    (N'registries.impact.recalculateAll', N'kz', N'Әсер еткендерді қайта есептеу'),
    (N'registries.impact.recalculateSelected', N'kz', N'Таңдалғандарды қайта есептеу ({count})'),
    (N'registries.impact.recalculateTitle', N'kz', N'Әсер еткен құжаттарды қайта есептеу'),
    (N'registries.impact.recalculateHint', N'kz', N'Қайта есептеу кезекке қойылады; әр құжатты өз тапсырмасы қайта есептейді.'),
    (N'registries.impact.recalculateConfirm', N'kz', N'Кезекке қою'),
    (N'registries.impact.jobQueued', N'kz', N'{jobId} қайта есептеу тапсырмасы'),
    (N'registries.impact.jobUnreadable', N'kz', N'Тапсырма күйін сіздің құқықтарыңызбен оқу мүмкін емес.'),
    (N'registries.impact.progress', N'kz', N'Қайта есептеу барысы'),
    (N'registries.impact.openJob', N'kz', N'Тапсырмалар кезегінде ашу'),
    (N'calculation.staleRegistry', N'kz', N'"{name}" анықтамалығы есептеуден кейін өзгертілді')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- REG:rt25-client ── кінець секції ──

-- REG:rt25-client ── сторінка впливу довідника і банер застарілості (RT-25, клієнт) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'registries.impact.open', N'en', N'Affected documents', 1),
    (N'registries.impact.title', N'en', N'Registry impact', 1),
    (N'registries.impact.hint', N'en', N'Documents in open periods whose results were calculated by a methodology that reads this registry. Closed periods are never recalculated and are not listed.', 1),
    (N'registries.impact.empty', N'en', N'No affected documents', 1),
    (N'registries.impact.emptyHint', N'en', N'No open-period results depend on this registry.', 1),
    (N'registries.impact.count', N'en', N'Shown {shown} of {total}', 1),
    (N'registries.impact.truncated', N'en', N'The list reached the server limit: more documents are affected than shown.', 1),
    (N'registries.impact.select', N'en', N'Select', 1),
    (N'registries.impact.selectDocument', N'en', N'Select {document}', 1),
    (N'registries.impact.document', N'en', N'Document', 1),
    (N'registries.impact.period', N'en', N'Period', 1),
    (N'registries.impact.via', N'en', N'Methodologies', 1),
    (N'registries.impact.recalculateAll', N'en', N'Recalculate affected', 1),
    (N'registries.impact.recalculateSelected', N'en', N'Recalculate selected ({count})', 1),
    (N'registries.impact.recalculateTitle', N'en', N'Recalculate affected documents', 1),
    (N'registries.impact.recalculateHint', N'en', N'The recalculation is queued; each document is recalculated by its own job.', 1),
    (N'registries.impact.recalculateConfirm', N'en', N'Queue recalculation', 1),
    (N'registries.impact.jobQueued', N'en', N'Recalculation job {jobId}', 1),
    (N'registries.impact.jobUnreadable', N'en', N'The job state cannot be read with your rights.', 1),
    (N'registries.impact.progress', N'en', N'Recalculation progress', 1),
    (N'registries.impact.openJob', N'en', N'Open in the job queue', 1),
    (N'calculation.staleRegistry', N'en', N'Registry "{name}" was changed after the calculation', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- REG:rt25-client ── кінець секції ──
