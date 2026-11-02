-- COLL:fv1315-ui ── ru/kz залежності розкладу збору: select і подія пропуску (ФВ-13.15, клієнт); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'schedule.dependsOn', N'ru', N'Зависит от расписания'),
    (N'schedule.dependsOn', N'kz', N'Қай жоспарға тәуелді'),
    (N'schedule.dependsOnHint', N'ru', N'Плановые запуски ждут успешного прогона выбранного расписания того же подключения. Ручной сбор и добор не блокируются.'),
    (N'schedule.dependsOnHint', N'kz', N'Жоспарлы іске қосулар сол қосылымның таңдалған жоспарының сәтті орындалуын күтеді. Қолмен жинау мен толықтыру бұғатталмайды.'),
    (N'schedule.dependsOnNone', N'ru', N'Без зависимости'),
    (N'schedule.dependsOnNone', N'kz', N'Тәуелділіксіз'),
    (N'status.coverage.SkippedDependency', N'ru', N'Ожидание зависимости'),
    (N'status.coverage.SkippedDependency', N'kz', N'Тәуелділік күтілуде'),
    (N'coverageEvents.skippedDependency', N'ru', N'Сущность {sourceEntityId}: плановый запуск пропущен, ожидается успешный прогон расписания {dependsOn}. Данные задержаны, не потеряны: их соберёт следующий запуск или добор.'),
    (N'coverageEvents.skippedDependency', N'kz', N'{sourceEntityId} нысаны: жоспарлы іске қосу өткізілді, {dependsOn} жоспарының сәтті орындалуы күтілуде. Деректер кешіктірілді, жоғалған жоқ: оларды келесі іске қосу немесе толықтыру жинайды.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:fv1315-ui ── кінець секції ──

