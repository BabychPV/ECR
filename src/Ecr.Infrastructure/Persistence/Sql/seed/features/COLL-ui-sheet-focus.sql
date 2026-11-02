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
