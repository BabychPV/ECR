-- COLL:an128 ── ru/kz D-441 (AN-128): подання ще не відкритого періоду; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'deny.PeriodNotOpenYet.Submit', N'ru', N'Период ещё не открыт: подать лист можно только за период в состоянии «Открыт» или «Льготный период».'),
    (N'workflow.submitPeriod', N'ru', N'Подать: {period}'),
    (N'workflow.pickSubmitPeriod', N'ru', N'Выбрать период для подачи'),
    (N'workflow.submitNextPeriod', N'ru', N'Следующий к подаче — {period}'),
    (N'workflow.submitPeriod', N'kz', N'Тапсыру: {period}'),
    (N'workflow.pickSubmitPeriod', N'kz', N'Тапсыру кезеңін таңдау'),
    (N'workflow.submitNextPeriod', N'kz', N'Келесі тапсырылатын кезең — {period}'),
    (N'deny.PeriodNotOpenYet.Submit', N'kz', N'Кезең әлі ашылмаған: парақты тек «Ашық» немесе «Жеңілдік кезеңі» күйіндегі кезең үшін тапсыруға болады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an128 ── кінець секції ──
-- COLL:an128 ── D-441 (AN-128): подання ще не відкритого (Scheduled) періоду — окремий текст відмови (reasonKey deny.PeriodNotOpenYet.Submit); ru/kz — порцією COLL:an128 у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'deny.PeriodNotOpenYet.Submit', N'en', N'This period is not open yet: a sheet can be submitted only for a period that is Open or in the Grace period.', 1),
    (N'workflow.submitPeriod', N'en', N'Submit: {period}', 1),
    (N'workflow.pickSubmitPeriod', N'en', N'Choose the period to submit', 1),
    (N'workflow.submitNextPeriod', N'en', N'Next to submit: {period}', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an128 ── кінець секції ──
