-- COLL:flare-ef-frequency ── ru/kz: частота синку подій PI (Q-2) і пропуск добової копії _Day (Q-1); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'sourceEvents.frequency.label', N'ru', N'Частота синхронизации событий'),
    (N'sourceEvents.frequency.label', N'kz', N'Оқиғаларды синхрондау жиілігі'),
    (N'sourceEvents.frequency.hint', N'ru', N'Как часто читать события шаблона из PI с полной сверкой за период.'),
    (N'sourceEvents.frequency.hint', N'kz', N'Үлгі оқиғаларын PI-дан кезең бойынша толық салыстырумен қаншалықты жиі оқу керек.'),
    (N'sourceEvents.frequency.save', N'ru', N'Сохранить частоту'),
    (N'sourceEvents.frequency.save', N'kz', N'Жиілікті сақтау'),
    (N'sourceEvents.frequency.none', N'ru', N'Расписания ещё нет: события синхронизируются только вручную. Сохраните, чтобы синхронизировать по расписанию (по умолчанию — каждый час).'),
    (N'sourceEvents.frequency.none', N'kz', N'Кесте әлі жоқ: оқиғалар тек қолмен синхрондалады. Кесте бойынша синхрондау үшін сақтаңыз (әдепкі — сағат сайын).'),
    (N'sourceEvents.frequency.custom', N'ru', N'Другое ({cron})'),
    (N'sourceEvents.frequency.custom', N'kz', N'Басқа ({cron})'),
    (N'sourceEvents.frequency.m5', N'ru', N'Каждые 5 минут'),
    (N'sourceEvents.frequency.m5', N'kz', N'Әр 5 минут сайын'),
    (N'sourceEvents.frequency.m15', N'ru', N'Каждые 15 минут'),
    (N'sourceEvents.frequency.m15', N'kz', N'Әр 15 минут сайын'),
    (N'sourceEvents.frequency.m30', N'ru', N'Каждые 30 минут'),
    (N'sourceEvents.frequency.m30', N'kz', N'Әр 30 минут сайын'),
    (N'sourceEvents.frequency.h1', N'ru', N'Каждый час'),
    (N'sourceEvents.frequency.h1', N'kz', N'Сағат сайын'),
    (N'sourceEvents.frequency.h2', N'ru', N'Каждые 2 часа'),
    (N'sourceEvents.frequency.h2', N'kz', N'Әр 2 сағат сайын'),
    (N'sourceEvents.frequency.h6', N'ru', N'Каждые 6 часов'),
    (N'sourceEvents.frequency.h6', N'kz', N'Әр 6 сағат сайын'),
    (N'sourceEvents.frequency.h12', N'ru', N'Каждые 12 часов'),
    (N'sourceEvents.frequency.h12', N'kz', N'Әр 12 сағат сайын'),
    (N'sourceEvents.frequency.d1', N'ru', N'Раз в сутки'),
    (N'sourceEvents.frequency.d1', N'kz', N'Тәулігіне бір рет'),
    (N'jobs.sourceEventsDayCopySkipped', N'ru', N'Шаблон _Day — суточная копия: события берутся только из корневых _Auto/_Manual'),
    (N'jobs.sourceEventsDayCopySkipped', N'kz', N'_Day үлгісі — тәуліктік көшірме: оқиғалар тек түбірлік _Auto/_Manual үлгілерінен алынады')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:flare-ef-frequency ── кінець секції ──

-- COLL:flare-ef-frequency ── en; ru/kz - порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'sourceEvents.frequency.label', N'en', N'Event sync frequency', 1),
    (N'sourceEvents.frequency.hint', N'en', N'How often to read the template''s events from PI with a full reconcile of the period.', 1),
    (N'sourceEvents.frequency.save', N'en', N'Save frequency', 1),
    (N'sourceEvents.frequency.none', N'en', N'No schedule yet: events are synced only on demand. Save to sync on a schedule (hourly by default).', 1),
    (N'sourceEvents.frequency.custom', N'en', N'Custom ({cron})', 1),
    (N'sourceEvents.frequency.m5', N'en', N'Every 5 minutes', 1),
    (N'sourceEvents.frequency.m15', N'en', N'Every 15 minutes', 1),
    (N'sourceEvents.frequency.m30', N'en', N'Every 30 minutes', 1),
    (N'sourceEvents.frequency.h1', N'en', N'Every hour', 1),
    (N'sourceEvents.frequency.h2', N'en', N'Every 2 hours', 1),
    (N'sourceEvents.frequency.h6', N'en', N'Every 6 hours', 1),
    (N'sourceEvents.frequency.h12', N'en', N'Every 12 hours', 1),
    (N'sourceEvents.frequency.d1', N'en', N'Once a day', 1),
    (N'jobs.sourceEventsDayCopySkipped', N'en', N'The _Day template is a daily copy: events are taken only from the root _Auto/_Manual templates', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:flare-ef-frequency ── кінець секції ──
