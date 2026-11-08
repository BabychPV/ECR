-- COLL:d14unmatched ── ru/kz D14: рядки без правила розрахунку; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'document.unmatchedRows.title', N'ru', N'Часть строк не вошла в расчёт'),
    (N'document.unmatchedRows.title', N'kz', N'Кейбір жолдар есептеуге кірмеді'),
    (N'document.unmatchedRows.hint', N'ru', N'Информация: эти строки не рассчитываются, потому что ни одно правило методологии не подходит к их кодам. Если строку нужно рассчитать, замените её код на одно из значений, указанных для её таблицы, и нажмите F9 (Пересчитать).'),
    (N'document.unmatchedRows.hint', N'kz', N'Ақпарат: бұл жолдар есептелмейді, себебі әдістеменің бірде-бір ережесі олардың кодтарына сәйкес келмейді. Жолды есептеу керек болса, оның кодын кестесі үшін көрсетілген мәндердің біріне ауыстырып, F9 (Қайта есептеу) басыңыз.'),
    (N'document.unmatchedRows.hintNarrow', N'ru', N'Информация: эти строки не рассчитываются, потому что ни одно правило методологии не подходит к их кодам. Чтобы изменить код, откройте документ на более широком экране.'),
    (N'document.unmatchedRows.hintNarrow', N'kz', N'Ақпарат: бұл жолдар есептелмейді, себебі әдістеменің бірде-бір ережесі олардың кодтарына сәйкес келмейді. Кодты өзгерту үшін құжатты кеңірек экранда ашыңыз.'),
    (N'document.unmatchedRows.summary', N'ru', N'Не рассчитываются: {rows} в {tables}'),
    (N'document.unmatchedRows.summary', N'kz', N'Есептелмейді: {tables} {rows}'),
    (N'document.unmatchedRows.rows.one', N'ru', N'{count} строка'),
    (N'document.unmatchedRows.rows.one', N'kz', N'{count} жол'),
    (N'document.unmatchedRows.rows.few', N'ru', N'{count} строки'),
    (N'document.unmatchedRows.rows.few', N'kz', N'{count} жол'),
    (N'document.unmatchedRows.rows.other', N'ru', N'{count} строк'),
    (N'document.unmatchedRows.rows.other', N'kz', N'{count} жол'),
    (N'document.unmatchedRows.tables.one', N'ru', N'{count} таблице'),
    (N'document.unmatchedRows.tables.one', N'kz', N'{count} кестеде'),
    (N'document.unmatchedRows.tables.few', N'ru', N'{count} таблицах'),
    (N'document.unmatchedRows.tables.few', N'kz', N'{count} кестеде'),
    (N'document.unmatchedRows.tables.other', N'ru', N'{count} таблицах'),
    (N'document.unmatchedRows.tables.other', N'kz', N'{count} кестеде'),
    (N'document.unmatchedRows.group', N'ru', N'{table} — {rows}'),
    (N'document.unmatchedRows.group', N'kz', N'{table} — {rows}'),
    (N'document.unmatchedRows.row', N'ru', N'Строка {row}'),
    (N'document.unmatchedRows.row', N'kz', N'{row}-жол'),
    (N'document.unmatchedRows.more', N'ru', N'и ещё {count}'),
    (N'document.unmatchedRows.more', N'kz', N'тағы {count}'),
    (N'document.unmatchedRows.lastRun', N'ru', N'последний расчёт: {time}'),
    (N'document.unmatchedRows.lastRun', N'kz', N'соңғы есептеу: {time}'),
    (N'document.unmatchedRows.allowed', N'ru', N'Рассчитывается только для «{column}»: {values}'),
    (N'document.unmatchedRows.allowed', N'kz', N'Тек «{column}» мына мәндері үшін есептеледі: {values}'),
    (N'document.unmatchedRows.allowedMore', N'ru', N'+ещё {count}'),
    (N'document.unmatchedRows.allowedMore', N'kz', N'+тағы {count}'),
    (N'document.unmatchedRows.rowValue', N'ru', N'Строка {row}: «{value}»'),
    (N'document.unmatchedRows.rowValue', N'kz', N'{row}-жол: «{value}»'),
    (N'document.unmatchedRows.emptyValue', N'ru', N'пусто'),
    (N'document.unmatchedRows.emptyValue', N'kz', N'бос')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:d14unmatched ── кінець секції ──
-- COLL:d14unmatched ── D14: рядки без правила розрахунку - інформація на аркуші; ru/kz - порцією COLL:d14unmatched нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'document.unmatchedRows.title', N'en', N'Some rows are not included in the calculation', 1),
    (N'document.unmatchedRows.hint', N'en', N'Information: these rows are not calculated because no methodology rule matches their codes. If a row should be calculated, change its code to one of the values listed for its table and press F9 (Recalculate).', 1),
    (N'document.unmatchedRows.hintNarrow', N'en', N'Information: these rows are not calculated because no methodology rule matches their codes. To change a code, open this document on a wider screen.', 1),
    (N'document.unmatchedRows.summary', N'en', N'Not calculated: {rows} in {tables}', 1),
    (N'document.unmatchedRows.rows.one', N'en', N'{count} row', 1),
    (N'document.unmatchedRows.rows.few', N'en', N'{count} rows', 1),
    (N'document.unmatchedRows.rows.other', N'en', N'{count} rows', 1),
    (N'document.unmatchedRows.tables.one', N'en', N'{count} table', 1),
    (N'document.unmatchedRows.tables.few', N'en', N'{count} tables', 1),
    (N'document.unmatchedRows.tables.other', N'en', N'{count} tables', 1),
    (N'document.unmatchedRows.group', N'en', N'{table} — {rows}', 1),
    (N'document.unmatchedRows.row', N'en', N'Row {row}', 1),
    (N'document.unmatchedRows.more', N'en', N'and {count} more', 1),
    (N'document.unmatchedRows.lastRun', N'en', N'last calculation: {time}', 1),
    (N'document.unmatchedRows.allowed', N'en', N'Calculated only for «{column}»: {values}', 1),
    (N'document.unmatchedRows.allowedMore', N'en', N'+{count} more', 1),
    (N'document.unmatchedRows.rowValue', N'en', N'Row {row}: «{value}»', 1),
    (N'document.unmatchedRows.emptyValue', N'en', N'empty', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:d14unmatched ── кінець секції ──
-- COLL:d14unmatched ── змінені тексти: підказка банера без переліку рядків — чому рядки не рахуються і що зробити (UAT 08.11 b3) ──
-- Нове значення вже стоїть вище (#UiEn — en, #I18N — ru/kz); епілог оновлює, лише поки в базі старе (випущене) значення.
-- ⚠ Стара фраза «наприклад, бо статус не Running» хибна з D-425: рядки зі статусом не Running відсіює фільтр
-- активності, і в перелік «без правила» вони не потрапляють.
INSERT INTO #UiChanged ([Key], Lang, OldVal, NewVal)
SELECT v.[Key], v.Lang, v.OldVal, v.NewVal
  FROM (VALUES
    (N'document.unmatchedRows.hint', N'en',
     N'{count} row(s) matched no calculation rule and were skipped, for example because the status is not Running. This is information, not an error. Row: {rows}',
     N'Information: these rows are not calculated because no methodology rule matches their codes. If a row should be calculated, change its code to one of the values listed for its table and press F9 (Recalculate).'),
    (N'document.unmatchedRows.hint', N'ru',
     N'Строк, к которым не подошло ни одно правило расчёта: {count}; они пропущены, например из-за статуса, отличного от Running. Это информация, а не ошибка. Строки: {rows}',
     N'Информация: эти строки не рассчитываются, потому что ни одно правило методологии не подходит к их кодам. Если строку нужно рассчитать, замените её код на одно из значений, указанных для её таблицы, и нажмите F9 (Пересчитать).'),
    (N'document.unmatchedRows.hint', N'kz',
     N'Ешқандай есептеу ережесіне сәйкес келмеген жолдар: {count}; олар өткізіп жіберілді, мысалы мәртебесі Running емес болғандықтан. Бұл қате емес, ақпарат. Жолдар: {rows}',
     N'Ақпарат: бұл жолдар есептелмейді, себебі әдістеменің бірде-бір ережесі олардың кодтарына сәйкес келмейді. Жолды есептеу керек болса, оның кодын кестесі үшін көрсетілген мәндердің біріне ауыстырып, F9 (Қайта есептеу) басыңыз.')
       ) AS v ([Key], Lang, OldVal, NewVal)
OPTION (RECOMPILE);
GO
-- COLL:d14unmatched ── кінець секції ──
