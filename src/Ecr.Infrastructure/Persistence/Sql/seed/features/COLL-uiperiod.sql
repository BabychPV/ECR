-- COLL:uiperiod ── ru/kz: UI-13: PeriodPicker за макетом (сітка періодів, стан, «closes in»); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'period.group', N'ru', N'Отчётный период'),
    (N'period.group', N'kz', N'Есепті кезең'),
    (N'period.choose', N'ru', N'Выбрать период'),
    (N'period.choose', N'kz', N'Кезеңді таңдау'),
    (N'period.gridTitle', N'ru', N'Отчётный период · {year}'),
    (N'period.gridTitle', N'kz', N'Есепті кезең · {year}'),
    (N'period.closesIn.one', N'ru', N'закрывается через {count} день'),
    (N'period.closesIn.one', N'kz', N'{count} күннен кейін жабылады'),
    (N'period.closesIn.few', N'ru', N'закрывается через {count} дня'),
    (N'period.closesIn.few', N'kz', N'{count} күннен кейін жабылады'),
    (N'period.closesIn.many', N'ru', N'закрывается через {count} дней'),
    (N'period.closesIn.many', N'kz', N'{count} күннен кейін жабылады'),
    (N'period.closesIn.other', N'ru', N'закрывается через {count} дня'),
    (N'period.closesIn.other', N'kz', N'{count} күннен кейін жабылады'),
    (N'period.stateInProjects', N'ru', N'{state} в {count} из {total} проектов'),
    (N'period.stateInProjects', N'kz', N'{total} жобаның {count} жобасында: {state}')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:uiperiod ── кінець секції ──

-- COLL:uiperiod ── UI-13: PeriodPicker за макетом (сітка періодів, стан, «closes in»); ru/kz — порцією COLL:uiperiod у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'period.group', N'en', N'Reporting period', 1),
    (N'period.choose', N'en', N'Choose a period', 1),
    (N'period.gridTitle', N'en', N'Reporting period · {year}', 1),
    (N'period.closesIn.one', N'en', N'closes in {count} day', 1),
    (N'period.closesIn.few', N'en', N'closes in {count} days', 1),
    (N'period.closesIn.many', N'en', N'closes in {count} days', 1),
    (N'period.closesIn.other', N'en', N'closes in {count} days', 1),
    (N'period.stateInProjects', N'en', N'{state} in {count} of {total} projects', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:uiperiod ── кінець секції ──
