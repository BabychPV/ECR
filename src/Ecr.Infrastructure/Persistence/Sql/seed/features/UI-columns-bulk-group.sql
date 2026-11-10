-- COLL:ui-columns-bulk-group ── ru/kz: вкладка Columns — вибір колонок і «Edit selected…» (група заголовка, все або нічого; PERMITES §3); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'columns.bulkToolbar', N'ru', N'Выбранные колонки'),
    (N'columns.bulkToolbar', N'kz', N'Таңдалған бағандар'),
    (N'columns.selectedCount', N'ru', N'Выбрано: {count}'),
    (N'columns.selectedCount', N'kz', N'Таңдалды: {count}'),
    (N'columns.editSelected', N'ru', N'Изменить выбранные…'),
    (N'columns.editSelected', N'kz', N'Таңдалғандарды өзгерту…'),
    (N'columns.clearSelection', N'ru', N'Снять выбор'),
    (N'columns.clearSelection', N'kz', N'Таңдауды алып тастау'),
    (N'columns.selectAll', N'ru', N'Выбрать все колонки'),
    (N'columns.selectAll', N'kz', N'Барлық бағандарды таңдау'),
    (N'columns.selectOne', N'ru', N'Выбрать колонку {name}'),
    (N'columns.selectOne', N'kz', N'{name} бағанын таңдау'),
    (N'columns.bulkTitle', N'ru', N'Изменить колонки: {count}'),
    (N'columns.bulkTitle', N'kz', N'Бағандарды өзгерту: {count}'),
    (N'columns.bulkAppliesTo', N'ru', N'Применяется к: {names}. Меняются все вместе или ни одна.'),
    (N'columns.bulkAppliesTo', N'kz', N'Қолданылады: {names}. Барлығы бірге өзгереді немесе ешқайсысы өзгермейді.'),
    (N'columns.bulkMixed', N'ru', N'Сейчас выбранные колонки в разных группах.'),
    (N'columns.bulkMixed', N'kz', N'Қазір таңдалған бағандар әртүрлі топтарда.'),
    (N'columns.bulkNoGroup', N'ru', N'— нет — (убрать из группы)'),
    (N'columns.bulkNoGroup', N'kz', N'— жоқ — (топтан шығару)'),
    (N'columns.bulkApply', N'ru', N'Применить'),
    (N'columns.bulkApply', N'kz', N'Қолдану'),
    (N'columns.bulkDone', N'ru', N'Группа обновлена у колонок: {count} (ревизия {revision}).'),
    (N'columns.bulkDone', N'kz', N'Топ жаңартылған бағандар: {count} (ревизия {revision}).'),
    (N'columns.bulkStale', N'ru', N'Оформление этой таблицы только что изменил кто-то другой. Ничего не сохранено; таблица перечитана — проверьте колонки и примените ещё раз.'),
    (N'columns.bulkStale', N'kz', N'Бұл кестенің безендіруін жаңа ғана басқа біреу өзгертті. Ештеңе сақталмады; кесте қайта оқылды — бағандарды тексеріп, қайта қолданыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui-columns-bulk-group ── кінець секції ──

-- COLL:ui-columns-bulk-group ── en; ru/kz - порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'columns.bulkToolbar', N'en', N'Selected columns', 1),
    (N'columns.selectedCount', N'en', N'{count} selected', 1),
    (N'columns.editSelected', N'en', N'Edit selected…', 1),
    (N'columns.clearSelection', N'en', N'Clear selection', 1),
    (N'columns.selectAll', N'en', N'Select all columns', 1),
    (N'columns.selectOne', N'en', N'Select column {name}', 1),
    (N'columns.bulkTitle', N'en', N'Edit columns: {count}', 1),
    (N'columns.bulkAppliesTo', N'en', N'Applies to: {names}. All of them change together, or none does.', 1),
    (N'columns.bulkMixed', N'en', N'The selected columns are in different groups now.', 1),
    (N'columns.bulkNoGroup', N'en', N'— none — (remove from group)', 1),
    (N'columns.bulkApply', N'en', N'Apply', 1),
    (N'columns.bulkDone', N'en', N'Group updated for {count} column(s) (revision {revision}).', 1),
    (N'columns.bulkStale', N'en', N'Someone else changed this table’s appearance a moment ago. Nothing was saved; the table has been reloaded — check the columns and apply again.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui-columns-bulk-group ── кінець секції ──
