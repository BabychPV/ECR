-- COLL:ui33periods ── ru/kz UI-33: Periods — плитки року й огляд проєктів; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'periods.tile.closesToday', N'ru', N'закрывается сегодня'),
    (N'periods.tile.closesIn.one', N'ru', N'закрывается через {count} день'),
    (N'periods.tile.closesIn.few', N'ru', N'закрывается через {count} дня'),
    (N'periods.tile.closesIn.many', N'ru', N'закрывается через {count} дней'),
    (N'periods.tile.closesIn.other', N'ru', N'закрывается через {count} дня'),
    (N'periods.tile.reopened', N'ru', N'переоткрыт до {date} · правки поздние'),
    (N'periods.tile.grace', N'ru', N'льготный период до {date} · правки поздние'),
    (N'periods.tile.opens', N'ru', N'откроется {date}'),
    (N'periods.tile.readOnly', N'ru', N'только чтение'),
    (N'periods.tile.now', N'ru', N'сейчас'),
    (N'periods.year.title', N'ru', N'{project} · {year}'),
    (N'periods.overview.title', N'ru', N'Все проекты · текущий период'),
    (N'periods.overview.hint', N'ru', N'выберите проект, чтобы увидеть его календарь выше'),
    (N'periods.overview.project', N'ru', N'Проект'),
    (N'periods.overview.current', N'ru', N'Текущий период'),
    (N'periods.overview.state', N'ru', N'Состояние'),
    (N'periods.overview.closes', N'ru', N'Закрывается'),
    (N'periods.overview.inGrace', N'ru', N'за {period}'),
    (N'periods.overview.stats', N'ru', N'Периоды всех проектов'),
    (N'periods.overview.stat.open', N'ru', N'открыто'),
    (N'periods.overview.stat.grace', N'ru', N'в льготном периоде'),
    (N'periods.overview.stat.soon', N'ru', N'закрываются в течение ≤ 3 дней'),
    (N'periods.overview.noMatch', N'ru', N'Нет проектов под этот фильтр'),
    (N'periods.tile.closesToday', N'kz', N'бүгін жабылады'),
    (N'periods.tile.closesIn.one', N'kz', N'{count} күннен кейін жабылады'),
    (N'periods.tile.closesIn.other', N'kz', N'{count} күннен кейін жабылады'),
    (N'periods.tile.closesIn.few', N'kz', N'{count} күннен кейін жабылады'),
    (N'periods.tile.closesIn.many', N'kz', N'{count} күннен кейін жабылады'),
    (N'periods.tile.reopened', N'kz', N'{date} дейін қайта ашылды · түзетулер кешіккен'),
    (N'periods.tile.grace', N'kz', N'жеңілдік кезеңі {date} дейін · түзетулер кешіккен'),
    (N'periods.tile.opens', N'kz', N'{date} ашылады'),
    (N'periods.tile.readOnly', N'kz', N'тек оқу'),
    (N'periods.tile.now', N'kz', N'қазір'),
    (N'periods.year.title', N'kz', N'{project} · {year}'),
    (N'periods.overview.title', N'kz', N'Барлық жобалар · ағымдағы кезең'),
    (N'periods.overview.hint', N'kz', N'күнтізбесін жоғарыда көру үшін жобаны таңдаңыз'),
    (N'periods.overview.project', N'kz', N'Жоба'),
    (N'periods.overview.current', N'kz', N'Ағымдағы кезең'),
    (N'periods.overview.state', N'kz', N'Күйі'),
    (N'periods.overview.closes', N'kz', N'Жабылады'),
    (N'periods.overview.inGrace', N'kz', N'{period} үшін'),
    (N'periods.overview.stats', N'kz', N'Барлық жобалардың кезеңдері'),
    (N'periods.overview.stat.open', N'kz', N'ашық'),
    (N'periods.overview.stat.grace', N'kz', N'жеңілдік кезеңінде'),
    (N'periods.overview.stat.soon', N'kz', N'≤ 3 күнде жабылады'),
    (N'periods.overview.noMatch', N'kz', N'Бұл сүзгіге сай жоба жоқ')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui33periods ── кінець секції ──
-- COLL:ui33periods ── UI-33: Periods — плитки періодів року, «All projects · current period», смуга показників; ru/kz — порцією COLL:ui33periods у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'periods.tile.closesToday', N'en', N'closes today', 1),
    (N'periods.tile.closesIn.one', N'en', N'closes in {count} day', 1),
    (N'periods.tile.closesIn.other', N'en', N'closes in {count} days', 1),
    (N'periods.tile.closesIn.few', N'en', N'closes in {count} days', 1),
    (N'periods.tile.closesIn.many', N'en', N'closes in {count} days', 1),
    (N'periods.tile.reopened', N'en', N'reopened until {date} · edits are late', 1),
    (N'periods.tile.grace', N'en', N'grace period until {date} · edits are late', 1),
    (N'periods.tile.opens', N'en', N'opens {date}', 1),
    (N'periods.tile.readOnly', N'en', N'read-only', 1),
    (N'periods.tile.now', N'en', N'now', 1),
    (N'periods.year.title', N'en', N'{project} · {year}', 1),
    (N'periods.overview.title', N'en', N'All projects · current period', 1),
    (N'periods.overview.hint', N'en', N'select a project to show its calendar above', 1),
    (N'periods.overview.project', N'en', N'Project', 1),
    (N'periods.overview.current', N'en', N'Current period', 1),
    (N'periods.overview.state', N'en', N'State', 1),
    (N'periods.overview.closes', N'en', N'Closes', 1),
    (N'periods.overview.inGrace', N'en', N'for {period}', 1),
    (N'periods.overview.stats', N'en', N'Periods of all projects', 1),
    (N'periods.overview.stat.open', N'en', N'open', 1),
    (N'periods.overview.stat.grace', N'en', N'in grace period', 1),
    (N'periods.overview.stat.soon', N'en', N'closing in ≤ 3 days', 1),
    (N'periods.overview.noMatch', N'en', N'No project matches this filter', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui33periods ── кінець секції ──
