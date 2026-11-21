-- PERF:recalc-scope ── PERF-01/02 (D-564 (а)/(б)): кнопка року річного документа рахує лише змінені місяці (en) ──
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
