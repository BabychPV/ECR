-- COLL:ui22tree ── ru/kz UI-22: дерево таблиць документа; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'grid.tree.label', N'ru', N'Таблицы листа'),
    (N'grid.tree.label', N'kz', N'Парақ кестелері'),
    (N'grid.tree.filter', N'ru', N'Фильтр таблиц'),
    (N'grid.tree.filter', N'kz', N'Кестелерді сүзу'),
    (N'grid.tree.errorsOnly', N'ru', N'Только таблицы с ошибками'),
    (N'grid.tree.errorsOnly', N'kz', N'Тек қателері бар кестелер'),
    (N'grid.tree.tablesFilled', N'ru', N'Заполнено таблиц: {filled} из {total}'),
    (N'grid.tree.tablesFilled', N'kz', N'Толтырылған кестелер: {filled} / {total}'),
    (N'grid.tree.issues', N'ru', N'Замечаний: {count}'),
    (N'grid.tree.issues', N'kz', N'Ескертпелер: {count}'),
    (N'grid.tree.nothingFound', N'ru', N'Нет таблиц, подходящих под фильтр.'),
    (N'grid.tree.nothingFound', N'kz', N'Сүзгіге сәйкес кесте жоқ.'),
    (N'grid.tree.clearFilter', N'ru', N'Сбросить фильтр'),
    (N'grid.tree.clearFilter', N'kz', N'Сүзгіні тазарту'),
    (N'grid.tree.toggle', N'ru', N'Навигатор таблиц'),
    (N'grid.tree.toggle', N'kz', N'Кестелер навигаторы'),
    (N'grid.tree.state.unknown', N'ru', N'Состояние ещё не загружено'),
    (N'grid.tree.state.unknown', N'kz', N'Күйі әлі жүктелмеген'),
    (N'grid.tree.state.none', N'ru', N'Заполнять нечего'),
    (N'grid.tree.state.none', N'kz', N'Толтыратын ештеңе жоқ'),
    (N'grid.tree.state.empty', N'ru', N'Не заполнена'),
    (N'grid.tree.state.empty', N'kz', N'Толтырылмаған'),
    (N'grid.tree.state.partial', N'ru', N'Заполнена частично: {filled} из {total} ячеек'),
    (N'grid.tree.state.partial', N'kz', N'Ішінара толтырылған: {filled} / {total} ұяшық'),
    (N'grid.tree.state.filled', N'ru', N'Заполнена'),
    (N'grid.tree.state.filled', N'kz', N'Толтырылған'),
    (N'grid.tree.state.error', N'ru', N'Ошибок: {count}'),
    (N'grid.tree.state.error', N'kz', N'Қателер: {count}'),
    (N'grid.tree.warnings', N'ru', N'Предупреждений: {count}'),
    (N'grid.tree.warnings', N'kz', N'Ескертулер: {count}')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui22tree ── кінець секції ──
-- COLL:ui22tree ── UI-22: дерево таблиць документа (фільтр, стан кожної таблиці, перемикач); ru/kz — порцією COLL:ui22tree у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'grid.tree.label', N'en', N'Tables of this sheet', 1),
    (N'grid.tree.filter', N'en', N'Filter tables', 1),
    (N'grid.tree.errorsOnly', N'en', N'Show only tables with errors', 1),
    (N'grid.tree.tablesFilled', N'en', N'{filled} of {total} tables filled', 1),
    (N'grid.tree.issues', N'en', N'Issues: {count}', 1),
    (N'grid.tree.nothingFound', N'en', N'No tables match the filter.', 1),
    (N'grid.tree.clearFilter', N'en', N'Clear filter', 1),
    (N'grid.tree.toggle', N'en', N'Table navigator', 1),
    (N'grid.tree.state.unknown', N'en', N'Status not loaded yet', 1),
    (N'grid.tree.state.none', N'en', N'Nothing to fill', 1),
    (N'grid.tree.state.empty', N'en', N'Not filled', 1),
    (N'grid.tree.state.partial', N'en', N'Partially filled: {filled} of {total} cells', 1),
    (N'grid.tree.state.filled', N'en', N'Filled', 1),
    (N'grid.tree.state.error', N'en', N'Errors: {count}', 1),
    (N'grid.tree.warnings', N'en', N'Warnings: {count}', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui22tree ── кінець секції ──
