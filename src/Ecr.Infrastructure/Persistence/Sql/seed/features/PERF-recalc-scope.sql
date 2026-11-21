-- PERF:recalc-scope ── PERF-01/02 (D-564a/b): кнопка року річного документа рахує лише змінені місяці (en) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    -- Підсумок ланцюжка: незмінні місяці лишили свій актуальний прогін (RecalculationJob.SkippedUnchangedKey).
    (N'jobs.recalcSkippedUnchanged',        N'en', N'Recalculated only the months whose inputs changed and the month on screen. Unchanged months kept their current results ({count}): {months}.', 1),
    -- Те саме з підсумком журналу аркушів ({message}).
    (N'jobs.recalcSkippedUnchangedWithLog', N'en', N'Recalculated only the months whose inputs changed and the month on screen. Unchanged months kept their current results ({count}): {months}. {message}', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERF:recalc-scope ── кінець секції ──

-- PERF:recalc-scope ── ru/kz: PERF-01/02 ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'jobs.recalcSkippedUnchanged', N'ru', N'Пересчитаны только месяцы с изменёнными входами и месяц на экране. Неизменённые месяцы сохранили текущие результаты ({count}): {months}.'),
    (N'jobs.recalcSkippedUnchanged', N'kz', N'Тек кірістері өзгерген айлар және экрандағы ай қайта есептелді. Өзгермеген айлар ағымдағы нәтижелерін сақтады ({count}): {months}.'),
    (N'jobs.recalcSkippedUnchangedWithLog', N'ru', N'Пересчитаны только месяцы с изменёнными входами и месяц на экране. Неизменённые месяцы сохранили текущие результаты ({count}): {months}. {message}'),
    (N'jobs.recalcSkippedUnchangedWithLog', N'kz', N'Тек кірістері өзгерген айлар және экрандағы ай қайта есептелді. Өзгермеген айлар ағымдағы нәтижелерін сақтады ({count}): {months}. {message}')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERF:recalc-scope ── кінець секції ──

-- PERF:recalc-scope ── PERF-01/10 (D-564a/c): повний перерахунок + місце в черзі (en) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'workflow.recalculateFull',     N'en', N'Full recalculation', 1),
    (N'workflow.recalculateFullHint', N'en', N'Recomputes all open months of the year. The normal Recalculate recomputes only the months whose inputs changed and the month on screen.', 1),
    (N'jobs.queuePosition',           N'en', N'Queued: position {position}', 1),
    (N'jobs.queueEta',                N'en', N'≈ starts in about {minutes} min (estimate)', 1),
    (N'jobs.queueEtaSoon',            N'en', N'Starts shortly (estimate)', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERF:recalc-scope ── кінець секції ──

-- PERF:recalc-scope ── ru/kz: PERF-01/10 (D-564a/c) ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'workflow.recalculateFull', N'ru', N'Полный пересчёт'),
    (N'workflow.recalculateFull', N'kz', N'Толық қайта есептеу'),
    (N'workflow.recalculateFullHint', N'ru', N'Пересчитывает все открытые месяцы года. Обычный пересчёт обновляет только месяцы с изменёнными входами и месяц на экране.'),
    (N'workflow.recalculateFullHint', N'kz', N'Жылдың барлық ашық айларын қайта есептейді. Қарапайым қайта есептеу тек кірістері өзгерген айларды және экрандағы айды жаңартады.'),
    (N'jobs.queuePosition', N'ru', N'В очереди: позиция {position}'),
    (N'jobs.queuePosition', N'kz', N'Кезекте: орны {position}'),
    (N'jobs.queueEta', N'ru', N'≈ начнётся примерно через {minutes} мин (оценка)'),
    (N'jobs.queueEta', N'kz', N'≈ шамамен {minutes} минуттан кейін басталады (бағалау)'),
    (N'jobs.queueEtaSoon', N'ru', N'Начнётся в ближайшее время (оценка)'),
    (N'jobs.queueEtaSoon', N'kz', N'Жақын арада басталады (бағалау)')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERF:recalc-scope ── кінець секції ──
