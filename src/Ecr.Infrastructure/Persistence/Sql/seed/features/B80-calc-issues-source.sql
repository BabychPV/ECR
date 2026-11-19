-- B80:calc-issues-source ── ru/kz B80 N6: причини джерела HSE400 у панелі «Проблеми розрахунку» (sourceIssues, N4); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'document.calcIssues.months.one', N'ru', N'{count} месяц'),
    (N'document.calcIssues.months.one', N'kz', N'{count} ай'),
    (N'document.calcIssues.months.few', N'ru', N'{count} месяца'),
    (N'document.calcIssues.months.few', N'kz', N'{count} ай'),
    (N'document.calcIssues.months.other', N'ru', N'{count} месяцев'),
    (N'document.calcIssues.months.other', N'kz', N'{count} ай'),
    (N'document.calcIssues.source.notApproved.title', N'ru', N'HSE400 {months} не утверждены — у Land нет состава газа за эти месяцы'),
    (N'document.calcIssues.source.notApproved.title', N'kz', N'HSE400 {months} бекітілмеген — Land-та бұл айлар үшін газ құрамы жоқ'),
    (N'document.calcIssues.source.changed.title', N'ru', N'HSE400 {months} изменены после утверждения этого месяца (утверждены повторно или больше не утверждены) — верните месяц в работу и пересчитайте его'),
    (N'document.calcIssues.source.changed.title', N'kz', N'HSE400 {months} осы ай бекітілгеннен кейін өзгерді (қайта бекітілді немесе енді бекітілмеген) — айды жұмысқа қайтарып, қайта есептеңіз'),
    (N'document.calcIssues.source.other.title', N'ru', N'Проблема с исходными данными HSE400 за {months} — проверьте документ-источник'),
    (N'document.calcIssues.source.other.title', N'kz', N'{months} бойынша HSE400 бастапқы деректерінде мәселе бар — бастапқы құжатты тексеріңіз'),
    (N'document.calcIssues.source.unavailable', N'ru', N'Документ-источник вам недоступен'),
    (N'document.calcIssues.source.unavailable', N'kz', N'Бастапқы құжат сізге қолжетімсіз'),
    (N'document.calcIssues.source.sourceApprovedAt', N'ru', N'Источник утверждён:'),
    (N'document.calcIssues.source.sourceApprovedAt', N'kz', N'Дереккөз бекітілді:'),
    (N'document.calcIssues.source.approvedAt', N'ru', N'Этот месяц утверждён:'),
    (N'document.calcIssues.source.approvedAt', N'kz', N'Бұл ай бекітілді:')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- B80:calc-issues-source ── кінець секції ──
-- B80:calc-issues-source ── B80 N6: причини джерела HSE400 у панелі «Проблеми розрахунку»; ru/kz — порцією B80:calc-issues-source вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'document.calcIssues.months.one', N'en', N'{count} month', 1),
    (N'document.calcIssues.months.few', N'en', N'{count} months', 1),
    (N'document.calcIssues.months.other', N'en', N'{count} months', 1),
    (N'document.calcIssues.source.notApproved.title', N'en', N'HSE400 {months} not approved — Land has no gas composition for these months', 1),
    (N'document.calcIssues.source.changed.title', N'en', N'HSE400 {months} changed after this month was approved (approved again or no longer approved) — reopen and recalculate this month', 1),
    (N'document.calcIssues.source.other.title', N'en', N'There is a problem with the HSE400 source data for {months} — check the source document', 1),
    (N'document.calcIssues.source.unavailable', N'en', N'The source document is not available to you', 1),
    (N'document.calcIssues.source.sourceApprovedAt', N'en', N'Source approved:', 1),
    (N'document.calcIssues.source.approvedAt', N'en', N'This month approved:', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- B80:calc-issues-source ── кінець секції ──
