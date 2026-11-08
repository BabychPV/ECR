-- COLL:d14unmatched ── ru/kz D14: рядки без правила розрахунку; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'document.unmatchedRows.title', N'ru', N'Часть строк не вошла в расчёт'),
    (N'document.unmatchedRows.title', N'kz', N'Кейбір жолдар есептеуге кірмеді'),
    (N'document.unmatchedRows.hint', N'ru', N'Это информация, исправлять ничего не нужно: ни одно правило методологии не подходит к кодам этих строк, поэтому они не рассчитываются. Чтобы включить строку, откройте её, укажите код, который покрывает методология, и нажмите F9 (строки не в статусе Running здесь не показаны).'),
    (N'document.unmatchedRows.hint', N'kz', N'Бұл ақпарат, ештеңені түзетудің қажеті жоқ: әдістеменің бірде-бір ережесі осы жолдардың кодтарына сәйкес келмейді, сондықтан олар есептелмейді. Жолды қосу үшін оны ашып, әдістеме қамтитын кодты енгізіп, F9 пернесін басыңыз (мәртебесі Running емес жолдар мұнда көрсетілмейді).'),
    (N'document.unmatchedRows.summary', N'ru', N'Строк: {count} · таблиц: {tables}'),
    (N'document.unmatchedRows.summary', N'kz', N'Жолдар: {count} · кестелер: {tables}'),
    (N'document.unmatchedRows.group', N'ru', N'{table} — строк: {count}'),
    (N'document.unmatchedRows.group', N'kz', N'{table} — жолдар: {count}'),
    (N'document.unmatchedRows.row', N'ru', N'Строка {row}'),
    (N'document.unmatchedRows.row', N'kz', N'{row}-жол'),
    (N'document.unmatchedRows.more', N'ru', N'и ещё {count}'),
    (N'document.unmatchedRows.more', N'kz', N'тағы {count}'),
    (N'document.unmatchedRows.lastRun', N'ru', N'последний расчёт: {time}'),
    (N'document.unmatchedRows.lastRun', N'kz', N'соңғы есептеу: {time}')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:d14unmatched ── кінець секції ──
-- COLL:d14unmatched ── D14: рядки без правила розрахунку - інформація на аркуші; ru/kz - порцією COLL:d14unmatched нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'document.unmatchedRows.title', N'en', N'Some rows are not included in the calculation', 1),
    (N'document.unmatchedRows.hint', N'en', N'Information, nothing to fix: no methodology rule matches the codes in these rows, so they are not calculated. To include a row, open it, enter a code the methodology covers and press F9 (rows not Running are not listed).', 1),
    (N'document.unmatchedRows.summary', N'en', N'Rows: {count} · tables: {tables}', 1),
    (N'document.unmatchedRows.group', N'en', N'{table} — rows: {count}', 1),
    (N'document.unmatchedRows.row', N'en', N'Row {row}', 1),
    (N'document.unmatchedRows.more', N'en', N'and {count} more', 1),
    (N'document.unmatchedRows.lastRun', N'en', N'last calculation: {time}', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:d14unmatched ── кінець секції ──
-- COLL:d14unmatched ── змінені тексти: підказка банера без переліку рядків, з поясненням «чому і як» (UAT 08.11 b3) ──
-- Нове значення вже стоїть вище (#UiEn — en, #I18N — ru/kz); епілог оновлює, лише поки в базі старе.
-- ⚠ Стара фраза «наприклад, бо статус не Running» хибна з D-425: рядки зі статусом не Running відсіює фільтр
-- активності, і в перелік «без правила» вони не потрапляють.
INSERT INTO #UiChanged ([Key], Lang, OldVal, NewVal)
SELECT v.[Key], v.Lang, v.OldVal, v.NewVal
  FROM (VALUES
    (N'document.unmatchedRows.hint', N'en',
     N'{count} row(s) matched no calculation rule and were skipped, for example because the status is not Running. This is information, not an error. Row: {rows}',
     N'Information, nothing to fix: no methodology rule matches the codes in these rows, so they are not calculated. To include a row, open it, enter a code the methodology covers and press F9 (rows not Running are not listed).'),
    (N'document.unmatchedRows.hint', N'ru',
     N'Строк, к которым не подошло ни одно правило расчёта: {count}; они пропущены, например из-за статуса, отличного от Running. Это информация, а не ошибка. Строки: {rows}',
     N'Это информация, исправлять ничего не нужно: ни одно правило методологии не подходит к кодам этих строк, поэтому они не рассчитываются. Чтобы включить строку, откройте её, укажите код, который покрывает методология, и нажмите F9 (строки не в статусе Running здесь не показаны).'),
    (N'document.unmatchedRows.hint', N'kz',
     N'Ешқандай есептеу ережесіне сәйкес келмеген жолдар: {count}; олар өткізіп жіберілді, мысалы мәртебесі Running емес болғандықтан. Бұл қате емес, ақпарат. Жолдар: {rows}',
     N'Бұл ақпарат, ештеңені түзетудің қажеті жоқ: әдістеменің бірде-бір ережесі осы жолдардың кодтарына сәйкес келмейді, сондықтан олар есептелмейді. Жолды қосу үшін оны ашып, әдістеме қамтитын кодты енгізіп, F9 пернесін басыңыз (мәртебесі Running емес жолдар мұнда көрсетілмейді).')
       ) AS v ([Key], Lang, OldVal, NewVal)
OPTION (RECOMPILE);
GO
-- COLL:d14unmatched ── кінець секції ──
