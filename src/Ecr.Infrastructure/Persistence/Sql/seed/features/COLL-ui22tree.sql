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
