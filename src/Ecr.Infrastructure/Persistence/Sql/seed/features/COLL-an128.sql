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
