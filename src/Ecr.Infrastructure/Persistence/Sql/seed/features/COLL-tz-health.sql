-- COLL:tz-health ── ru/kz перевірки tzdata на /health/ready (F-4); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'health.check.tzdata', N'ru', N'База часовых поясов'),
    (N'health.check.tzdata', N'kz', N'Уақыт белдеулерінің базасы'),
    (N'health.tzdata.ok', N'ru', N'База часовых поясов знает, что Казахстан с 2024-03-01 на UTC+5.'),
    (N'health.tzdata.ok', N'kz', N'Уақыт белдеулері базасы Қазақстанның 2024-03-01 бастап UTC+5 бойынша тұратынын біледі.'),
    (N'health.tzdata.stale', N'ru', N'База часовых поясов операционной системы устарела: {zones} (ожидалось +05:00 с 2024-03-01). Границы периодов и отметки о запоздалых правках проектов в этих поясах смещены. Обновите tzdata (Linux) или установите обновление часовых поясов Windows; ничего не блокируется, сохранённые данные не меняются.'),
    (N'health.tzdata.stale', N'kz', N'Операциялық жүйенің уақыт белдеулері базасы ескірген: {zones} (2024-03-01 бастап +05:00 күтілген). Осы белдеулердегі жобалардың кезең шекаралары мен кешіккен түзету белгілері ығысқан. tzdata (Linux) жаңартыңыз немесе Windows уақыт белдеулерінің жаңартуын орнатыңыз; ештеңе бұғатталмайды, сақталған деректер өзгермейді.'),
    (N'health.tzdata.unavailable', N'ru', N'Не удалось проверить базу часовых поясов.'),
    (N'health.tzdata.unavailable', N'kz', N'Уақыт белдеулерінің базасын тексеру мүмкін болмады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:tz-health ── кінець секції ──

-- COLL:tz-health ── Перевірка `tzdata` на /health/ready: база поясів ОС і перехід Казахстану на UTC+5 (F-4, `TimeZoneDatabaseHealthCheck`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'health.check.tzdata',                    N'en', N'Time zone database', 1),
    (N'health.tzdata.ok',                       N'en', N'The time zone database knows that Kazakhstan has been on UTC+5 since 2024-03-01.', 1),
    (N'health.tzdata.stale',                    N'en', N'The operating system time zone database is out of date: {zones} (expected +05:00 since 2024-03-01). Period boundaries and late-edit marks of projects in these zones are shifted. Update tzdata (Linux) or install the Windows time zone update; nothing is blocked and stored data is not changed.', 1),
    (N'health.tzdata.unavailable',              N'en', N'The time zone database could not be checked.', 1)
    -- ru/kz — окремою порцією `COLL:tz-health` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:tz-health ── кінець секції ──
