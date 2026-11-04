-- COLL:ui-sheet-focus ── ru/kz: панель таблиць, таблиця на весь екран, стрілки й список аркушів, згортання результатів; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'grid.tree.hide', N'ru', N'Скрыть'),
    (N'grid.tree.hide', N'kz', N'Жасыру'),
    (N'grid.tree.show', N'ru', N'Показать список таблиц'),
    (N'grid.tree.show', N'kz', N'Кестелер тізімін көрсету'),
    (N'grid.full.enter', N'ru', N'На весь экран'),
    (N'grid.full.enter', N'kz', N'Толық экран'),
    (N'grid.full.exit', N'ru', N'Выйти из полноэкранного режима'),
    (N'grid.full.exit', N'kz', N'Толық экраннан шығу'),
    (N'grid.keys.fullscreen', N'ru', N'Таблица на весь экран: вкл / выкл'),
    (N'grid.keys.fullscreen', N'kz', N'Кестені толық экранға: қосу / өшіру'),
    (N'documents.sheets.prev', N'ru', N'Предыдущий лист'),
    (N'documents.sheets.prev', N'kz', N'Алдыңғы парақ'),
    (N'documents.sheets.next', N'ru', N'Следующий лист'),
    (N'documents.sheets.next', N'kz', N'Келесі парақ'),
    (N'documents.sheets.all', N'ru', N'Все листы'),
    (N'documents.sheets.all', N'kz', N'Барлық парақтар'),
    (N'documents.calculationResultsNone', N'ru', N'нет'),
    (N'documents.calculationResultsNone', N'kz', N'жоқ')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui-sheet-focus ── кінець секції ──
-- COLL:ui-sheet-focus ── панель таблиць (Hide/Show), таблиця на весь екран, стрілки й список аркушів, згортання результатів; ru/kz — порцією COLL:ui-sheet-focus у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'grid.tree.hide', N'en', N'Hide', 1),
    (N'grid.tree.show', N'en', N'Show tables list', 1),
    (N'grid.full.enter', N'en', N'Full screen', 1),
    (N'grid.full.exit', N'en', N'Exit full screen', 1),
    (N'grid.keys.fullscreen', N'en', N'Table full screen on / off', 1),
    (N'documents.sheets.prev', N'en', N'Previous sheet', 1),
    (N'documents.sheets.next', N'en', N'Next sheet', 1),
    (N'documents.sheets.all', N'en', N'All sheets', 1),
    (N'documents.calculationResultsNone', N'en', N'none', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui-sheet-focus ── кінець секції ──
