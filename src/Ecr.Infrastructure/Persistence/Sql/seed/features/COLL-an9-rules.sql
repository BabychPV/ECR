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

