-- COLL:an9-rules ── ru/kz події «пільговий строк періоду»; власна порція #I18N ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'notifications.event.PeriodGraceStarted', N'ru', N'Отчётный период перешёл в льготный срок'),
    (N'notifications.event.PeriodGraceStarted', N'kz', N'Есепті кезең жеңілдік мерзіміне өтті'),
    (N'notifications.periodGraceStarted.subject', N'ru', N'ECR: срок периода {period} истёк, проект {project}'),
    (N'notifications.periodGraceStarted.subject', N'kz', N'ECR: {period} кезеңінің мерзімі өтті, {project} жобасы'),
    (N'notifications.periodGraceStarted.body', N'ru', N'Срок подачи за период {period} по проекту {project} истёк, остался только льготный срок. Завершите и отправьте документ за этот период.'),
    (N'notifications.periodGraceStarted.body', N'kz', N'{project} жобасы бойынша {period} кезеңіне тапсыру мерзімі өтті, тек жеңілдік мерзімі қалды. Осы кезеңге арналған құжатты аяқтап жіберіңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an9-rules ── кінець секції ──

-- COLL:an9-rules ── Подія «період перейшов у пільговий строк» (`PeriodStateJob`, `NotificationEventKind.PeriodGraceStarted`, AN-9) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'notifications.event.PeriodGraceStarted', N'en', N'Reporting period entered its grace window', 1),
    (N'notifications.periodGraceStarted.subject', N'en', N'ECR: period {period} is past its deadline, project {project}', 1),
    (N'notifications.periodGraceStarted.body', N'en', N'The submission deadline of period {period} for project {project} has passed; only the grace window is left. Please finish and submit your document for this period.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an9-rules ── кінець секції ──
