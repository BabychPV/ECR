-- PERIOD:clarity-ui ── ru/kz: Period clarity (квіз людини 07.11) — сторінка періодів лише блокує місяці; налаштування проєкту; перелік документів за рік ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'periods.columnMonth', N'ru', N'Месяц'),
    (N'periods.locksOn', N'ru', N'Блокируется'),
    (N'periods.locksOnHint', N'ru', N'В этот момент данные месяца блокируются (конец периода + {hardClose} дн., политика {code}); переоткрытый месяц блокируется по окончании переоткрытия. Начиная с {grace} дн. после конца периода правки ещё принимаются, но отмечаются как запоздалые. Время — в часовом поясе площадки.'),
    (N'periods.keyTooltip', N'ru', N'Ключ периода {key} · порядковый номер {sequence}'),
    (N'periods.lockOnlyNote', N'ru', N'Период лишь блокирует данные месяца; документы охватывают весь год.'),
    (N'projectSettings.title', N'ru', N'Настройки проекта'),
    (N'projectSettings.project', N'ru', N'Проект'),
    (N'projectSettings.workflow', N'ru', N'Согласование и правила периодов'),
    (N'projectSettings.currentPeriod', N'ru', N'Текущий период'),
    (N'projectSettings.currentIs', N'ru', N'Текущий период: {period}'),
    (N'projectSettings.modeAuto', N'ru', N'Автоматически по дате'),
    (N'projectSettings.modePinned', N'ru', N'Закреплён: {period}'),
    (N'projectSettings.pinnedReason', N'ru', N'Причина: {reason}'),
    (N'projectSettings.automaticWouldBe', N'ru', N'По дате это был бы {period}.'),
    (N'projectSettings.pin', N'ru', N'Закрепить…'),
    (N'projectSettings.backToAuto', N'ru', N'Вернуть автоматический'),
    (N'projectSettings.backedToAuto', N'ru', N'Текущий период снова выбирается по дате.'),
    (N'projectSettings.pinPeriod', N'ru', N'Месяц для закрепления'),
    (N'projectSettings.noCurrent', N'ru', N'нет'),
    (N'nav.projectSettings', N'ru', N'Настройки проекта'),
    (N'nav.projectSettings.description', N'ru', N'Настройки всего проекта: маршрут согласования, политики периодов, текущий период, часовой пояс, клонирование и архивирование.'),
    (N'documents.year', N'ru', N'Год'),
    (N'documents.month', N'ru', N'Месяц'),
    (N'documents.allMonths', N'ru', N'Все месяцы'),
    (N'documents.months', N'ru', N'Месяцы {year}'),
    (N'documents.oneDocumentNote', N'ru', N'Один документ на объект; все месяцы — внутри него.'),
    (N'documents.yearStripLabel', N'ru', N'{key}: месяцы {year}'),
    (N'documents.monthState', N'ru', N'{month} — {state}'),
    (N'documents.monthNoState', N'ru', N'{month} — нет данных'),
    (N'periods.columnMonth', N'kz', N'Ай'),
    (N'periods.locksOn', N'kz', N'Бұғатталады'),
    (N'periods.locksOnHint', N'kz', N'Осы сәтте айдың деректері бұғатталады (кезеңнің соңы + {hardClose} күн, {code} саясаты); қайта ашылған ай қайта ашу аяқталғанда бұғатталады. Кезең аяқталғаннан кейін {grace} күннен бастап түзетулер әлі қабылданады, бірақ кешіккен деп белгіленеді. Уақыт — алаңның уақыт белдеуінде.'),
    (N'periods.keyTooltip', N'kz', N'Кезең кілті {key} · реттік нөмір {sequence}'),
    (N'periods.lockOnlyNote', N'kz', N'Кезең тек айдың деректерін бұғаттайды; құжаттар бүкіл жылды қамтиды.'),
    (N'projectSettings.title', N'kz', N'Жоба баптаулары'),
    (N'projectSettings.project', N'kz', N'Жоба'),
    (N'projectSettings.workflow', N'kz', N'Келісу және кезең ережелері'),
    (N'projectSettings.currentPeriod', N'kz', N'Ағымдағы кезең'),
    (N'projectSettings.currentIs', N'kz', N'Ағымдағы кезең: {period}'),
    (N'projectSettings.modeAuto', N'kz', N'Күн бойынша автоматты'),
    (N'projectSettings.modePinned', N'kz', N'Бекітілген: {period}'),
    (N'projectSettings.pinnedReason', N'kz', N'Себебі: {reason}'),
    (N'projectSettings.automaticWouldBe', N'kz', N'Күн бойынша бұл {period} болар еді.'),
    (N'projectSettings.pin', N'kz', N'Бекіту…'),
    (N'projectSettings.backToAuto', N'kz', N'Автоматтыға қайтару'),
    (N'projectSettings.backedToAuto', N'kz', N'Ағымдағы кезең қайтадан күн бойынша таңдалады.'),
    (N'projectSettings.pinPeriod', N'kz', N'Бекітілетін ай'),
    (N'projectSettings.noCurrent', N'kz', N'жоқ'),
    (N'nav.projectSettings', N'kz', N'Жоба баптаулары'),
    (N'nav.projectSettings.description', N'kz', N'Бүкіл жобаның баптаулары: келісу маршруты, кезең саясаттары, ағымдағы кезең, уақыт белдеуі, көшіру және мұрағаттау.'),
    (N'documents.year', N'kz', N'Жыл'),
    (N'documents.month', N'kz', N'Ай'),
    (N'documents.allMonths', N'kz', N'Барлық айлар'),
    (N'documents.months', N'kz', N'{year} жылдың айлары'),
    (N'documents.oneDocumentNote', N'kz', N'Бір нысанға бір құжат; барлық айлар оның ішінде.'),
    (N'documents.yearStripLabel', N'kz', N'{key}: {year} жылдың айлары'),
    (N'documents.monthState', N'kz', N'{month} — {state}'),
    (N'documents.monthNoState', N'kz', N'{month} — деректер жоқ')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERIOD:clarity-ui ── кінець секції ──
-- PERIOD:clarity-ui ── Period clarity (en) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'periods.columnMonth', N'en', N'Month', 1),
    (N'periods.locksOn', N'en', N'Locks on', 1),
    (N'periods.locksOnHint', N'en', N'The month''s data locks at this moment (period end + {hardClose} d, policy {code}); a reopened month locks when its reopening ends. From {grace} d after the period end, edits are still accepted but flagged as late. Times are in the site time zone.', 1),
    (N'periods.keyTooltip', N'en', N'Period key {key} · sequence {sequence}', 1),
    (N'periods.lockOnlyNote', N'en', N'A period only locks a month''s data; documents cover the whole year.', 1),
    (N'projectSettings.title', N'en', N'Project settings', 1),
    (N'projectSettings.project', N'en', N'Project', 1),
    (N'projectSettings.workflow', N'en', N'Approval and period rules', 1),
    (N'projectSettings.currentPeriod', N'en', N'Current period', 1),
    (N'projectSettings.currentIs', N'en', N'Current period: {period}', 1),
    (N'projectSettings.modeAuto', N'en', N'Automatic by date', 1),
    (N'projectSettings.modePinned', N'en', N'Pinned to {period}', 1),
    (N'projectSettings.pinnedReason', N'en', N'Reason: {reason}', 1),
    (N'projectSettings.automaticWouldBe', N'en', N'By date it would be {period}.', 1),
    (N'projectSettings.pin', N'en', N'Pin…', 1),
    (N'projectSettings.backToAuto', N'en', N'Back to automatic', 1),
    (N'projectSettings.backedToAuto', N'en', N'The current period is chosen by date again.', 1),
    (N'projectSettings.pinPeriod', N'en', N'Month to pin', 1),
    (N'projectSettings.noCurrent', N'en', N'none', 1),
    (N'nav.projectSettings', N'en', N'Project settings', 1),
    (N'nav.projectSettings.description', N'en', N'Settings of the whole project: approval route, period policies, current period, time zone, cloning and archiving.', 1),
    (N'documents.year', N'en', N'Year', 1),
    (N'documents.month', N'en', N'Month', 1),
    (N'documents.allMonths', N'en', N'All months', 1),
    (N'documents.months', N'en', N'Months of {year}', 1),
    (N'documents.oneDocumentNote', N'en', N'One document per object; all months are inside it.', 1),
    (N'documents.yearStripLabel', N'en', N'{key}: months of {year}', 1),
    (N'documents.monthState', N'en', N'{month}: {state}', 1),
    (N'documents.monthNoState', N'en', N'{month}: no data', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERIOD:clarity-ui ── кінець секції ──
-- PERIOD:clarity-ui ── змінені тексти наявних ключів: стани місяця Open / Late (flagged) / Locked (людина 07.11; D-70 без змін) ──
-- Нове значення вже стоїть у MERGE (CATALOG-en-base.sql — en, 00-prelude.sql — ru/kz); епілог оновлює, лише поки в базі старе.
INSERT INTO #UiChanged ([Key], Lang, OldVal, NewVal)
SELECT v.[Key], v.Lang, v.OldVal, v.NewVal
  FROM (VALUES
    (N'status.period.Grace', N'en', N'Grace period', N'Late (flagged)'),
    (N'status.period.Grace', N'ru', N'Льготный период', N'С опозданием (отмечается)'),
    (N'status.period.Grace', N'kz', N'Жеңілдік кезеңі', N'Кешіккен (белгіленеді)'),
    (N'status.period.Closed', N'en', N'Closed', N'Locked'),
    (N'status.period.Closed', N'ru', N'Закрыт', N'Заблокирован'),
    (N'status.period.Closed', N'kz', N'Жабық', N'Бұғатталған')
       ) AS v ([Key], Lang, OldVal, NewVal)
OPTION (RECOMPILE);
GO
-- PERIOD:clarity-ui ── кінець секції ──
