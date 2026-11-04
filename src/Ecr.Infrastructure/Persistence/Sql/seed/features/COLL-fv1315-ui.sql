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

-- COLL:fv1315-ui ── Залежність розкладу збору: select у формі, подія покриття пропуску (ФВ-13.15, клієнт) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'schedule.dependsOn', N'en', N'Depends on schedule', 1),
    (N'schedule.dependsOnHint', N'en', N'Scheduled runs wait for a successful run of the chosen schedule of the same connection. Manual and catch-up runs are not blocked.', 1),
    (N'schedule.dependsOnNone', N'en', N'No dependency', 1),
    (N'status.coverage.SkippedDependency', N'en', N'Waiting for dependency', 1),
    (N'coverageEvents.skippedDependency', N'en', N'Entity {sourceEntityId}: the scheduled run was skipped, waiting for a successful run of schedule {dependsOn}. The data is delayed, not lost: the next run or the catch-up collects it.', 1)
    -- ru/kz — окремою порцією `I18N` (COLL:fv1315-ui).
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:fv1315-ui ── кінець секції ──
