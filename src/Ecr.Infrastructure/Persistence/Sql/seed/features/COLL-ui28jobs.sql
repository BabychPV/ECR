-- COLL:ui28jobs ── ru/kz екран задач на шаблоні переліку (UI-28); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'jobs.description', N'ru', N'Экспорт, импорт, пересчёты и ночные проверки выполняются в фоне. Проваленная задача объясняет причину, и её можно перезапустить.'),
    (N'jobs.description', N'kz', N'Экспорт, импорт, қайта есептеу және түнгі тексерулер фонда орындалады. Сәтсіз тапсырма себебін көрсетеді, оны қайта іске қосуға болады.'),
    (N'jobs.statsLabel', N'ru', N'Задачи кратко'),
    (N'jobs.statsLabel', N'kz', N'Тапсырмалар қысқаша'),
    (N'jobs.statRunning', N'ru', N'выполняются'),
    (N'jobs.statRunning', N'kz', N'орындалуда'),
    (N'jobs.statQueued', N'ru', N'в очереди'),
    (N'jobs.statQueued', N'kz', N'кезекте'),
    (N'jobs.statFailed', N'ru', N'с ошибкой за 24 ч'),
    (N'jobs.statFailed', N'kz', N'24 сағатта сәтсіз'),
    (N'jobs.statLatency', N'ru', N'Средняя задержка запуска за последние 24 часа: {seconds} с'),
    (N'jobs.statLatency', N'kz', N'Соңғы 24 сағаттағы іске қосудың орташа кідірісі: {seconds} с'),
    (N'jobs.search', N'ru', N'Поиск'),
    (N'jobs.search', N'kz', N'Іздеу'),
    (N'jobs.searchPlaceholder', N'ru', N'Задача, документ или человек'),
    (N'jobs.searchPlaceholder', N'kz', N'Тапсырма, құжат немесе адам'),
    (N'jobs.filterType', N'ru', N'Типы'),
    (N'jobs.filterType', N'kz', N'Түрлері'),
    (N'jobs.filterState', N'ru', N'Состояния'),
    (N'jobs.filterState', N'kz', N'Күйлері'),
    (N'jobs.noMatch', N'ru', N'Нет задач, подходящих под фильтры.'),
    (N'jobs.noMatch', N'kz', N'Сүзгілерге сәйкес тапсырма жоқ.'),
    (N'jobs.progress', N'ru', N'Ход выполнения'),
    (N'jobs.progress', N'kz', N'Орындалу барысы'),
    (N'jobs.stoppedAt', N'ru', N'остановлена на {percent} %'),
    (N'jobs.stoppedAt', N'kz', N'{percent} % кезінде тоқтады'),
    (N'jobs.refresh', N'ru', N'Обновить'),
    (N'jobs.refresh', N'kz', N'Жаңарту'),
    (N'jobs.findById', N'ru', N'Найти задачу по идентификатору'),
    (N'jobs.findById', N'kz', N'Тапсырманы идентификатор бойынша табу'),
    (N'jobs.whyFailed', N'ru', N'Почему не выполнена'),
    (N'jobs.whyFailed', N'kz', N'Неге сәтсіз аяқталды'),
    (N'jobs.attempts', N'ru', N'Попытки'),
    (N'jobs.attempts', N'kz', N'Әрекеттер')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui28jobs ── кінець секції ──
-- COLL:ui28jobs ── екран задач на шаблоні переліку (UI-28); ru/kz — порцією COLL:ui28jobs у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'jobs.description', N'en', N'Exports, imports, recalculations and nightly checks run in the background. A failed job says why and can be restarted.', 1),
    (N'jobs.statsLabel', N'en', N'Jobs at a glance', 1),
    (N'jobs.statRunning', N'en', N'running', 1),
    (N'jobs.statQueued', N'en', N'queued', 1),
    (N'jobs.statFailed', N'en', N'failed in 24 h', 1),
    (N'jobs.statLatency', N'en', N'Average start delay over the last 24 hours: {seconds} s', 1),
    (N'jobs.search', N'en', N'Search', 1),
    (N'jobs.searchPlaceholder', N'en', N'Job, document or person', 1),
    (N'jobs.filterType', N'en', N'Types', 1),
    (N'jobs.filterState', N'en', N'States', 1),
    (N'jobs.noMatch', N'en', N'No jobs match the filters.', 1),
    (N'jobs.progress', N'en', N'Progress', 1),
    (N'jobs.stoppedAt', N'en', N'stopped at {percent} %', 1),
    (N'jobs.refresh', N'en', N'Refresh', 1),
    (N'jobs.findById', N'en', N'Find a job by id', 1),
    (N'jobs.whyFailed', N'en', N'Why it failed', 1),
    (N'jobs.attempts', N'en', N'Attempts', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui28jobs ── кінець секції ──
