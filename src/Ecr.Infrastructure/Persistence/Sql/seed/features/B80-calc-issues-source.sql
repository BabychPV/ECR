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
-- B80:source-changed-any ── ru/kz F5-R (D-570, P-1122-4): «джерело змінено після затвердження» і за зв'язки Any — панель і перелік; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'document.calcIssues.source.changedData.title', N'ru', N'Данные HSE400 {months} изменены после утверждения этого месяца — числа месяца могут быть устаревшими'),
    (N'document.calcIssues.source.changedData.title', N'kz', N'HSE400 {months} деректері осы ай бекітілгеннен кейін өзгерді — айдың сандары ескірген болуы мүмкін'),
    (N'document.calcIssues.source.sourceChangedAt', N'ru', N'Источник изменён:'),
    (N'document.calcIssues.source.sourceChangedAt', N'kz', N'Дереккөз өзгерді:'),
    (N'document.calcIssues.source.changed.hint', N'ru', N'Утверждённый месяц автоматически не пересчитывается. Проверьте числа; чтобы применить данные источника, верните месяц в работу и пересчитайте его или попросите администратора пересчитать зависимые документы вместе с утверждёнными.'),
    (N'document.calcIssues.source.changed.hint', N'kz', N'Бекітілген ай автоматты түрде қайта есептелмейді. Сандарды тексеріңіз; дереккөз деректерін қолдану үшін айды жұмысқа қайтарып, қайта есептеңіз немесе әкімшіден тәуелді құжаттарды бекітілгендерімен бірге қайта есептеуді сұраңыз.'),
    (N'documents.sourceChanged', N'ru', N'Источник изменён'),
    (N'documents.sourceChanged', N'kz', N'Дереккөз өзгерді'),
    (N'documents.sourceChangedHint', N'ru', N'Документ-источник (HSE400) изменился после утверждения: {months}. Утверждённые месяцы автоматически не пересчитываются — проверьте их; верните в работу и пересчитайте или пересчитайте зависимые вместе с утверждёнными.'),
    (N'documents.sourceChangedHint', N'kz', N'Бастапқы құжат (HSE400) бекітуден кейін өзгерді: {months}. Бекітілген айлар автоматты түрде қайта есептелмейді — оларды тексеріңіз; жұмысқа қайтарып қайта есептеңіз немесе тәуелділерді бекітілгендерімен бірге қайта есептеңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- B80:source-changed-any ── кінець секції ──
-- B80:source-changed-any ── F5-R (D-570): en; ru/kz — порцією B80:source-changed-any вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'document.calcIssues.source.changedData.title', N'en', N'HSE400 {months} data changed after this month was approved — the numbers of this month may be outdated', 1),
    (N'document.calcIssues.source.sourceChangedAt', N'en', N'Source changed:', 1),
    (N'document.calcIssues.source.changed.hint', N'en', N'An approved month is not recalculated automatically. Review the numbers; to apply the source data, reopen this month and recalculate it, or ask an administrator to recalculate the dependent documents including approved ones.', 1),
    (N'documents.sourceChanged', N'en', N'Source changed', 1),
    (N'documents.sourceChangedHint', N'en', N'The source document (HSE400) changed after approval: {months}. Approved months are not recalculated automatically — review them; reopen and recalculate, or recalculate the dependents including approved ones.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- B80:source-changed-any ── кінець секції ──
