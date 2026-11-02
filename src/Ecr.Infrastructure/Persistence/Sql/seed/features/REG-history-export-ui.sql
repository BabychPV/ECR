-- REG:history-export-ui ── ru/kz журналу змін запису і експорту довідника (RT-15/RT-16, клієнт); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'registries.entryHistory.title', N'ru', N'Журнал изменений'),
    (N'registries.entryHistory.when', N'ru', N'Когда'),
    (N'registries.entryHistory.author', N'ru', N'Автор'),
    (N'registries.entryHistory.change', N'ru', N'Что изменено'),
    (N'registries.entryHistory.before', N'ru', N'Было'),
    (N'registries.entryHistory.after', N'ru', N'Стало'),
    (N'registries.entryHistory.unknownAuthor', N'ru', N'Неизвестен (фоновая задача)'),
    (N'registries.entryHistory.empty', N'ru', N'Изменения этой записи не зафиксированы.'),
    (N'registries.entryHistory.loadMore', N'ru', N'Показать более ранние изменения'),
    (N'registries.entryHistory.openEnded', N'ru', N'без ограничения'),
    (N'registries.entryHistory.yes', N'ru', N'Да'),
    (N'registries.entryHistory.no', N'ru', N'Нет'),
    (N'registries.entryHistory.kind.created', N'ru', N'Запись создана'),
    (N'registries.entryHistory.kind.name', N'ru', N'Название'),
    (N'registries.entryHistory.kind.validity', N'ru', N'Срок действия'),
    (N'registries.entryHistory.kind.active', N'ru', N'Активна'),
    (N'registries.entryHistory.kind.deleted', N'ru', N'Запись удалена'),
    (N'registries.export.button', N'ru', N'Экспорт'),
    (N'registries.export.csv', N'ru', N'CSV (можно импортировать обратно)'),
    (N'registries.export.xlsx', N'ru', N'Книга Excel (XLSX)'),
    (N'registries.export.asOfHint', N'ru', N'Записи, действующие на {date}.'),
    (N'registries.export.includeChildren', N'ru', N'С дочерними частями (композиция)'),
    (N'registries.export.includeChildrenUnavailable', N'ru', N'Пока недоступно: сервер ещё не экспортирует части композиции.'),
    (N'registries.export.failed', N'ru', N'Экспорт не выполнен'),
    (N'registries.export.forbidden', N'ru', N'У вас нет права чтения этого справочника, поэтому экспорт недоступен. Попросите администратора безопасности выдать право чтения.'),
    (N'registries.export.tooLarge', N'ru', N'В справочнике больше записей, чем допускает один экспорт.'),
    (N'registries.export.tooLargeHint', N'ru', N'Файл не обрезается. Попросите администратора поднять предел экспорта (Registries:ExportMaxRows).'),
    (N'registries.entryHistory.title', N'kz', N'Өзгерістер журналы'),
    (N'registries.entryHistory.when', N'kz', N'Қашан'),
    (N'registries.entryHistory.author', N'kz', N'Автор'),
    (N'registries.entryHistory.change', N'kz', N'Не өзгертілді'),
    (N'registries.entryHistory.before', N'kz', N'Бұрын'),
    (N'registries.entryHistory.after', N'kz', N'Кейін'),
    (N'registries.entryHistory.unknownAuthor', N'kz', N'Белгісіз (фондық тапсырма)'),
    (N'registries.entryHistory.empty', N'kz', N'Бұл жазбаның өзгерістері тіркелмеген.'),
    (N'registries.entryHistory.loadMore', N'kz', N'Ертеректегі өзгерістерді көрсету'),
    (N'registries.entryHistory.openEnded', N'kz', N'шектеусіз'),
    (N'registries.entryHistory.yes', N'kz', N'Иә'),
    (N'registries.entryHistory.no', N'kz', N'Жоқ'),
    (N'registries.entryHistory.kind.created', N'kz', N'Жазба жасалды'),
    (N'registries.entryHistory.kind.name', N'kz', N'Атауы'),
    (N'registries.entryHistory.kind.validity', N'kz', N'Қолданылу мерзімі'),
    (N'registries.entryHistory.kind.active', N'kz', N'Белсенді'),
    (N'registries.entryHistory.kind.deleted', N'kz', N'Жазба жойылды'),
    (N'registries.export.button', N'kz', N'Экспорт'),
    (N'registries.export.csv', N'kz', N'CSV (қайта импорттауға болады)'),
    (N'registries.export.xlsx', N'kz', N'Excel кітабы (XLSX)'),
    (N'registries.export.asOfHint', N'kz', N'{date} күні қолданыстағы жазбалар.'),
    (N'registries.export.includeChildren', N'kz', N'Еншілес бөліктерімен (композиция)'),
    (N'registries.export.includeChildrenUnavailable', N'kz', N'Әзірге қолжетімсіз: сервер композиция бөліктерін әлі экспорттамайды.'),
    (N'registries.export.failed', N'kz', N'Экспорт орындалмады'),
    (N'registries.export.forbidden', N'kz', N'Сізде бұл анықтамалықты оқу құқығы жоқ, сондықтан экспорт қолжетімсіз. Қауіпсіздік әкімшісінен оқу құқығын сұраңыз.'),
    (N'registries.export.tooLarge', N'kz', N'Анықтамалықта бір экспортқа рұқсат етілгеннен көп жазба бар.'),
    (N'registries.export.tooLargeHint', N'kz', N'Файл қысқартылмайды. Әкімшіден экспорт шегін (Registries:ExportMaxRows) көтеруді сұраңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- REG:history-export-ui ── кінець секції ──
-- REG:history-export-ui ── журнал змін запису і експорт довідника (RT-15/RT-16, клієнт) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'registries.entryHistory.title', N'en', N'Change log', 1),
    (N'registries.entryHistory.when', N'en', N'When', 1),
    (N'registries.entryHistory.author', N'en', N'Author', 1),
    (N'registries.entryHistory.change', N'en', N'What changed', 1),
    (N'registries.entryHistory.before', N'en', N'Before', 1),
    (N'registries.entryHistory.after', N'en', N'After', 1),
    (N'registries.entryHistory.unknownAuthor', N'en', N'Unknown (background job)', 1),
    (N'registries.entryHistory.empty', N'en', N'No changes recorded for this entry.', 1),
    (N'registries.entryHistory.loadMore', N'en', N'Show earlier changes', 1),
    (N'registries.entryHistory.openEnded', N'en', N'open', 1),
    (N'registries.entryHistory.yes', N'en', N'Yes', 1),
    (N'registries.entryHistory.no', N'en', N'No', 1),
    (N'registries.entryHistory.kind.created', N'en', N'Entry created', 1),
    (N'registries.entryHistory.kind.name', N'en', N'Name', 1),
    (N'registries.entryHistory.kind.validity', N'en', N'Validity', 1),
    (N'registries.entryHistory.kind.active', N'en', N'Active', 1),
    (N'registries.entryHistory.kind.deleted', N'en', N'Entry deleted', 1),
    (N'registries.export.button', N'en', N'Export', 1),
    (N'registries.export.csv', N'en', N'CSV (can be imported back)', 1),
    (N'registries.export.xlsx', N'en', N'Excel workbook (XLSX)', 1),
    (N'registries.export.asOfHint', N'en', N'Entries effective on {date}.', 1),
    (N'registries.export.includeChildren', N'en', N'With child parts (composition)', 1),
    (N'registries.export.includeChildrenUnavailable', N'en', N'Not available yet: the server does not export composition parts.', 1),
    (N'registries.export.failed', N'en', N'Export failed', 1),
    (N'registries.export.forbidden', N'en', N'You do not have read access to this registry, so it cannot be exported. Ask the security administrator for read access.', 1),
    (N'registries.export.tooLarge', N'en', N'The registry has more entries than one export allows.', 1),
    (N'registries.export.tooLargeHint', N'en', N'The file is not cut short. Ask the administrator to raise the export limit (Registries:ExportMaxRows).', 1)
    -- ru/kz — окремою порцією `I18N` (REG:history-export-ui).
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- REG:history-export-ui ── кінець секції ──
