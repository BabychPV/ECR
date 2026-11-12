-- INSP:locked-rows ── ru/kz 12.11 (людина, doc 96 T3_00): закриті місяці в Issues — одне речення на рядок, місяці назвами ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'inspector.attention.lockedRowOne', N'ru', N'Строка {row}: закрытый месяц ({months}) — эти столбцы здесь не редактируются.'),
    (N'inspector.attention.lockedRowOne', N'kz', N'{row} жолы: жабық ай ({months}) — бұл бағандарды мұнда өңдеуге болмайды.'),
    (N'inspector.attention.lockedRowMany', N'ru', N'Строка {row}: закрытые месяцы ({months}) — эти столбцы здесь не редактируются.'),
    (N'inspector.attention.lockedRowMany', N'kz', N'{row} жолы: жабық айлар ({months}) — бұл бағандарды мұнда өңдеуге болмайды.'),
    (N'inspector.attention.monthList', N'ru', N'{list} и {last}'),
    (N'inspector.attention.monthList', N'kz', N'{list} және {last}'),
    (N'inspector.attention.lockedColumns', N'ru', N'Закрытые столбцы'),
    (N'inspector.attention.lockedColumns', N'kz', N'Жабық бағандар')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- INSP:locked-rows ── кінець секції ──
-- INSP:locked-rows ── en 12.11; ru/kz — порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'inspector.attention.lockedRowOne', N'en', N'Row {row}: the {months} columns are in a closed month and can''t be edited here.', 1),
    (N'inspector.attention.lockedRowMany', N'en', N'Row {row}: the {months} columns are in closed months and can''t be edited here.', 1),
    (N'inspector.attention.monthList', N'en', N'{list} and {last}', 1),
    (N'inspector.attention.lockedColumns', N'en', N'Locked columns', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- INSP:locked-rows ── кінець секції ──
