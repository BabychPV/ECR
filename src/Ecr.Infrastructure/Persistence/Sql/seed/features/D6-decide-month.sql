-- D-6 ── ru/kz: рішення (Approve / Reject / Return for edits / Recall) над місяцем річного аркуша; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'workflow.approvePeriod', N'ru', N'Утвердить: {period}'),
    (N'workflow.rejectPeriod', N'ru', N'Вернуть в работу: {period}'),
    (N'workflow.reopenPeriod', N'ru', N'Переоткрыть для правок: {period}'),
    (N'workflow.recallPeriod', N'ru', N'Отозвать: {period}'),
    (N'workflow.pickApprovePeriod', N'ru', N'Выбрать период для утверждения'),
    (N'workflow.pickReopenPeriod', N'ru', N'Выбрать период для переоткрытия'),
    (N'workflow.approvePeriodTitle', N'ru', N'Утвердить {period}?'),
    (N'workflow.rejectPeriodTitle', N'ru', N'Вернуть {period} в работу'),
    (N'workflow.reopenPeriodTitle', N'ru', N'Переоткрыть {period} для правок'),
    (N'workflow.recallPeriodTitle', N'ru', N'Отозвать подачу за {period}'),
    (N'workflow.approvePeriod', N'kz', N'Бекіту: {period}'),
    (N'workflow.rejectPeriod', N'kz', N'Жұмысқа қайтару: {period}'),
    (N'workflow.reopenPeriod', N'kz', N'Түзету үшін қайта ашу: {period}'),
    (N'workflow.recallPeriod', N'kz', N'Кері алу: {period}'),
    (N'workflow.pickApprovePeriod', N'kz', N'Бекіту кезеңін таңдау'),
    (N'workflow.pickReopenPeriod', N'kz', N'Қайта ашу кезеңін таңдау'),
    (N'workflow.approvePeriodTitle', N'kz', N'{period} бекіту керек пе?'),
    (N'workflow.rejectPeriodTitle', N'kz', N'{period} жұмысқа қайтару'),
    (N'workflow.reopenPeriodTitle', N'kz', N'{period} түзету үшін қайта ашу'),
    (N'workflow.recallPeriodTitle', N'kz', N'{period} үшін тапсыруды кері алу'),
    (N'grid.monthMakeDecision', N'ru', N'Сделать {period} месяцем решения'),
    (N'grid.monthMakeDecision', N'kz', N'{period} шешім айы ету')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- D-6 ── кінець секції ──
-- D-6 ── en: рішення над місяцем річного аркуша (кнопка називає місяць, який вирішить, як «Submit: <місяць>») ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'workflow.approvePeriod', N'en', N'Approve: {period}', 1),
    (N'workflow.rejectPeriod', N'en', N'Reject: {period}', 1),
    (N'workflow.reopenPeriod', N'en', N'Return for edits: {period}', 1),
    (N'workflow.recallPeriod', N'en', N'Recall: {period}', 1),
    (N'workflow.pickApprovePeriod', N'en', N'Choose the period to approve', 1),
    (N'workflow.pickReopenPeriod', N'en', N'Choose the period to return for edits', 1),
    (N'workflow.approvePeriodTitle', N'en', N'Approve {period}?', 1),
    (N'workflow.rejectPeriodTitle', N'en', N'Reject {period}', 1),
    (N'workflow.reopenPeriodTitle', N'en', N'Return {period} for edits', 1),
    (N'workflow.recallPeriodTitle', N'en', N'Recall the submission for {period}', 1),
    (N'grid.monthMakeDecision', N'en', N'Make {period} the decision month', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- D-6 ── кінець секції ──
