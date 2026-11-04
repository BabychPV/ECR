-- CI:calculation-issues-ui ── ru/kz: рядки без правила методології (AN-69) — позначка в дереві таблиць і згортна панель над таблицями; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'calculationIssues.badgeLabel', N'ru', N'Строки без правила расчёта: {count}'),
    (N'calculationIssues.badgeLabel', N'kz', N'Есептеу ережесі жоқ жолдар: {count}'),
    (N'calculationIssues.title', N'ru', N'Строки без подходящего правила расчёта: {count}'),
    (N'calculationIssues.title', N'kz', N'Сәйкес есептеу ережесі жоқ жолдар: {count}'),
    (N'calculationIssues.hint', N'ru', N'Пересчёт нашёл эти строки, но для них в методологии нет правила, поэтому они не вошли в результаты.'),
    (N'calculationIssues.hint', N'kz', N'Қайта есептеу бұл жолдарды тапты, бірақ әдістемеде оларға ереже жоқ, сондықтан олар нәтижеге кірмеді.'),
    (N'calculationIssues.show', N'ru', N'Показать строки'),
    (N'calculationIssues.show', N'kz', N'Жолдарды көрсету'),
    (N'calculationIssues.hide', N'ru', N'Скрыть строки'),
    (N'calculationIssues.hide', N'kz', N'Жолдарды жасыру'),
    (N'calculationIssues.listLabel', N'ru', N'Строки без правила расчёта'),
    (N'calculationIssues.listLabel', N'kz', N'Есептеу ережесі жоқ жолдар'),
    (N'calculationIssues.item', N'ru', N'{table} · строка {row} · {methodology}'),
    (N'calculationIssues.item', N'kz', N'{table} · {row}-жол · {methodology}'),
    (N'calculationIssues.methodologyUnknown', N'ru', N'методология неизвестна'),
    (N'calculationIssues.methodologyUnknown', N'kz', N'әдістеме белгісіз')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- CI:calculation-issues-ui ── кінець секції ──

-- CI:calculation-issues-ui ── рядки без правила методології: позначка в дереві й панель; ru/kz — порцією CI:calculation-issues-ui у блоці I18N вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'calculationIssues.badgeLabel', N'en', N'Rows without a calculation rule: {count}', 1),
    (N'calculationIssues.title', N'en', N'Rows with no matching calculation rule: {count}', 1),
    (N'calculationIssues.hint', N'en', N'The recalculation found these rows, but the methodology has no rule for them, so they are left out of the results.', 1),
    (N'calculationIssues.show', N'en', N'Show rows', 1),
    (N'calculationIssues.hide', N'en', N'Hide rows', 1),
    (N'calculationIssues.listLabel', N'en', N'Rows without a calculation rule', 1),
    (N'calculationIssues.item', N'en', N'{table} · row {row} · {methodology}', 1),
    (N'calculationIssues.methodologyUnknown', N'en', N'methodology unknown', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- CI:calculation-issues-ui ── кінець секції ──
