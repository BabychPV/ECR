-- B66:calc-step-errors ── ru/kz: причини нуля результатів — помилка кроку методики (StepError, одна проблема на рядок) і рядки, відсіяні фільтром активності D-425 (RowInactive, один підсумок на таблицю); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'document.stepErrorRows.hint', N'ru', N'В этих строках шаг методики не рассчитался (ошибка формулы), поэтому его результата нет. Проверьте значения строки; если они верны — сообщите методологу код шага и ошибки.'),
    (N'document.stepErrorRows.hint', N'kz', N'Бұл жолдарда әдістеме қадамы есептелмеді (формула қатесі), сондықтан оның нәтижесі жоқ. Жол мәндерін тексеріңіз; дұрыс болса — қадам мен қате кодын әдіскерге хабарлаңыз.'),
    (N'document.stepErrorRows.group', N'ru', N'{table} — {rows}: ошибка шага методики'),
    (N'document.stepErrorRows.group', N'kz', N'{table} — {rows}: әдістеме қадамының қатесі'),
    (N'document.stepErrorRows.row', N'ru', N'Строка {row} · шаг «{step}»: {error}'),
    (N'document.stepErrorRows.row', N'kz', N'{row}-жол · «{step}» қадамы: {error}'),
    (N'document.stepErrorRows.rowMore', N'ru', N'Строка {row} · шаг «{step}»: {error} (ещё шагов с ошибкой: {more})'),
    (N'document.stepErrorRows.rowMore', N'kz', N'{row}-жол · «{step}» қадамы: {error} (қатесі бар тағы қадамдар: {more})'),
    (N'document.inactiveRows.hint', N'ru', N'Строки с неактивным статусом исключены из расчёта фильтром активности методики — это ожидаемо. Если строку нужно рассчитать, смените её статус и нажмите F9 (Пересчитать).'),
    (N'document.inactiveRows.hint', N'kz', N'Белсенді емес мәртебедегі жолдар әдістеменің белсенділік сүзгісімен есептеуден шығарылды — бұл күтілетін жағдай. Жолды есептеу қажет болса, оның мәртебесін өзгертіп, F9 (Қайта есептеу) басыңыз.'),
    (N'document.inactiveRows.group', N'ru', N'{table} — {rows}: неактивны, исключены из расчёта'),
    (N'document.inactiveRows.group', N'kz', N'{table} — {rows}: белсенді емес, есептеуден шығарылды'),
    (N'document.calcIssues.stepError.title', N'ru', N'Не рассчитано ({rows}): ошибка шага методики'),
    (N'document.calcIssues.stepError.title', N'kz', N'Есептелмеді ({rows}): әдістеме қадамының қатесі'),
    (N'document.calcIssues.inactive.title', N'ru', N'Исключено ({rows}): неактивный статус строки'),
    (N'document.calcIssues.inactive.title', N'kz', N'Шығарылды ({rows}): жолдың мәртебесі белсенді емес')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- B66:calc-step-errors ── кінець секції ──

-- B66:calc-step-errors ── en-порція ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'document.stepErrorRows.hint', N'en', N'In these rows a methodology step failed (formula error), so it has no result. Check the row values; if they are correct, give the methodologist the step and error codes.', 1),
    (N'document.stepErrorRows.group', N'en', N'{table} — {rows}: methodology step error', 1),
    (N'document.stepErrorRows.row', N'en', N'Row {row} · step «{step}»: {error}', 1),
    (N'document.stepErrorRows.rowMore', N'en', N'Row {row} · step «{step}»: {error} (more failed steps: {more})', 1),
    (N'document.inactiveRows.hint', N'en', N'Rows with an inactive status are excluded from the calculation by the methodology activity filter — this is expected. To calculate a row, change its status and press F9 (Recalculate).', 1),
    (N'document.inactiveRows.group', N'en', N'{table} — {rows}: inactive, excluded from the calculation', 1),
    (N'document.calcIssues.stepError.title', N'en', N'{rows} not calculated: a methodology step failed', 1),
    (N'document.calcIssues.inactive.title', N'en', N'{rows} excluded: the row status is inactive', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- B66:calc-step-errors ── кінець секції ──
