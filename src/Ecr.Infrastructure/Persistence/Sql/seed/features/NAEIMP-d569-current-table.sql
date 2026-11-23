-- NAEIMP:d569 ── ru/kz: D-569 (P-1123-1) — поле специфікації, що зберігається в ДОДАНУ колонку («Цех»), але не прочитане, блокує імпорт; власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-IMP-0422.airAddedColumnUnread', N'ru', N'«{field}» сохраняется в добавленную колонку «{column}», но колонка файла для него не прочитана (не найдена по заголовку или занята другим полем). В эту колонку ничего не сохранилось бы: выберите колонку файла для поля или укажите текст заголовка из файла.'),
    (N'err.ECR-IMP-0422.airAddedColumnUnread', N'kz', N'«{field}» қосылған «{column}» бағанына сақталады, бірақ ол үшін файл бағаны оқылмады (тақырыбы бойынша табылмады немесе басқа өріс алған). Бұл бағанға ештеңе сақталмас еді: өріске файл бағанын таңдаңыз немесе файлдағы тақырып мәтінін көрсетіңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- NAEIMP:d569 ── кінець секції ──

-- NAEIMP:d569 ── en: D-569 — an unread field of an added column blocks the import; ru/kz in the section above ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-IMP-0422.airAddedColumnUnread', N'en', N'"{field}" goes into the added column "{column}", but no file column was read for it (not found by its header, or used by another field). Nothing would be saved into that column: choose the file column for the field, or give it the header text from the file.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- NAEIMP:d569 ── кінець секції ──

-- COLL:ui-air-import ── ru/kz: D-569 (P-1123-1) — перегляд «що замінює нова таблиця» (рядки за НАЕ/речовиною, колонки, яких немає у файлі) і «поточна таблиця» картки; власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'permits.air.currentTable', N'ru', N'Текущая таблица: {file}, {date}, {user}'),
    (N'permits.air.currentTable', N'kz', N'Ағымдағы кесте: {file}, {date}, {user}'),
    (N'permits.air.currentTableNoName', N'ru', N'имя файла не записано'),
    (N'permits.air.currentTableNoName', N'kz', N'файл атауы жазылмаған'),
    (N'permits.air.import.changes.title', N'ru', N'Что заменяет новая таблица'),
    (N'permits.air.import.changes.title', N'kz', N'Жаңа кесте нені ауыстырады'),
    (N'permits.air.import.changes.hint', N'ru', N'Актуальна только последняя загруженная таблица: все строки этого разрешения во всех регионах и участках заменяются строками этого файла. Строки, которых нет в файле, удаляются; значения, которых нет в файле, становятся пустыми.'),
    (N'permits.air.import.changes.hint', N'kz', N'Тек соңғы жүктелген кесте өзекті: осы рұқсаттың барлық аймақтар мен учаскелердегі барлық жолдары осы файлдың жолдарымен ауыстырылады. Файлда жоқ жолдар жойылады; файлда жоқ мәндер бос болады.'),
    (N'permits.air.import.changes.sources', N'ru', N'Источники: добавлено {added}, изменено {changed}, удалено {removed}'),
    (N'permits.air.import.changes.sources', N'kz', N'Көздер: қосылды {added}, өзгертілді {changed}, жойылды {removed}'),
    (N'permits.air.import.changes.substances', N'ru', N'Строки веществ: добавлено {added}, изменено {changed}, удалено {removed}'),
    (N'permits.air.import.changes.substances', N'kz', N'Заттар жолдары: қосылды {added}, өзгертілді {changed}, жойылды {removed}'),
    (N'permits.air.import.changes.kind', N'ru', N'Изменение'),
    (N'permits.air.import.changes.kind', N'kz', N'Өзгеріс'),
    (N'permits.air.import.changes.nae', N'ru', N'НАЕ'),
    (N'permits.air.import.changes.nae', N'kz', N'НАЕ'),
    (N'permits.air.import.changes.substance', N'ru', N'Вещество'),
    (N'permits.air.import.changes.substance', N'kz', N'Зат'),
    (N'permits.air.import.changes.what', N'ru', N'Что меняется'),
    (N'permits.air.import.changes.what', N'kz', N'Не өзгереді'),
    (N'permits.air.import.changes.added', N'ru', N'Добавлено'),
    (N'permits.air.import.changes.added', N'kz', N'Қосылды'),
    (N'permits.air.import.changes.changed', N'ru', N'Изменено'),
    (N'permits.air.import.changes.changed', N'kz', N'Өзгертілді'),
    (N'permits.air.import.changes.removed', N'ru', N'Удалено'),
    (N'permits.air.import.changes.removed', N'kz', N'Жойылды'),
    (N'permits.air.import.changes.empty', N'ru', N'(пусто)'),
    (N'permits.air.import.changes.empty', N'kz', N'(бос)'),
    (N'permits.air.import.changes.field', N'ru', N'{field}: {old} → {new}'),
    (N'permits.air.import.changes.field', N'kz', N'{field}: {old} → {new}'),
    (N'permits.air.import.changes.otherScope', N'ru', N'(другой регион / участок: {scope})'),
    (N'permits.air.import.changes.otherScope', N'kz', N'(басқа аймақ / учаске: {scope})'),
    (N'permits.air.import.changes.otherPermit', N'ru', N'(разрешение {permit}, замена с вашего подтверждения)'),
    (N'permits.air.import.changes.otherPermit', N'kz', N'({permit} рұқсаты, сіздің растауыңызбен ауыстырылады)'),
    (N'permits.air.import.changes.wholeSource', N'ru', N'весь источник'),
    (N'permits.air.import.changes.wholeSource', N'kz', N'бүкіл көз'),
    (N'permits.air.import.changes.none', N'ru', N'Изменений строк нет: в разрешении уже ровно эти строки.'),
    (N'permits.air.import.changes.none', N'kz', N'Жолдар өзгермейді: рұқсатта дәл осы жолдар бар.'),
    (N'permits.air.import.changes.dropped', N'ru', N'Ещё изменений не показано: {count}.'),
    (N'permits.air.import.changes.dropped', N'kz', N'Тағы {count} өзгеріс көрсетілмеген.'),
    (N'permits.air.import.changes.aria', N'ru', N'Изменения строк импорта'),
    (N'permits.air.import.changes.aria', N'kz', N'Импорт жолдарының өзгерістері'),
    (N'permits.air.import.changes.emptiedTitle', N'ru', N'Колонки, которых нет в этом файле'),
    (N'permits.air.import.changes.emptiedTitle', N'kz', N'Бұл файлда жоқ бағандар'),
    (N'permits.air.import.changes.emptiedText', N'ru', N'Эти колонки не читаются из этого файла. После импорта они пусты во всех строках разрешения: {columns}.'),
    (N'permits.air.import.changes.emptiedText', N'kz', N'Бұл бағандар осы файлдан оқылмайды. Импорттан кейін олар рұқсаттың барлық жолдарында бос: {columns}.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui-air-import ── кінець секції ──

-- COLL:ui-air-import ── en: D-569 (P-1123-1) — preview «what the new table replaces» and the card's «current table»; ru/kz in the section above ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'permits.air.currentTable', N'en', N'Current table: {file}, {date}, {user}', 1),
    (N'permits.air.currentTableNoName', N'en', N'file name not recorded', 1),
    (N'permits.air.import.changes.title', N'en', N'What the new table replaces', 1),
    (N'permits.air.import.changes.hint', N'en', N'The last uploaded table is the only current one: all rows of this permit, in every region and area, are replaced by the rows of this file. Rows that are not in the file are removed; values the file does not have become empty.', 1),
    (N'permits.air.import.changes.sources', N'en', N'Sources: {added} added, {changed} changed, {removed} removed', 1),
    (N'permits.air.import.changes.substances', N'en', N'Substance rows: {added} added, {changed} changed, {removed} removed', 1),
    (N'permits.air.import.changes.kind', N'en', N'Change', 1),
    (N'permits.air.import.changes.nae', N'en', N'NAE', 1),
    (N'permits.air.import.changes.substance', N'en', N'Substance', 1),
    (N'permits.air.import.changes.what', N'en', N'What changes', 1),
    (N'permits.air.import.changes.added', N'en', N'Added', 1),
    (N'permits.air.import.changes.changed', N'en', N'Changed', 1),
    (N'permits.air.import.changes.removed', N'en', N'Removed', 1),
    (N'permits.air.import.changes.empty', N'en', N'(empty)', 1),
    (N'permits.air.import.changes.field', N'en', N'{field}: {old} → {new}', 1),
    (N'permits.air.import.changes.otherScope', N'en', N'(other region / area: {scope})', 1),
    (N'permits.air.import.changes.otherPermit', N'en', N'(permit {permit}, replaced with your confirmation)', 1),
    (N'permits.air.import.changes.wholeSource', N'en', N'whole source', 1),
    (N'permits.air.import.changes.none', N'en', N'No row changes: the permit already holds exactly these rows.', 1),
    (N'permits.air.import.changes.dropped', N'en', N'{count} more changes are not listed.', 1),
    (N'permits.air.import.changes.aria', N'en', N'Row changes of the import', 1),
    (N'permits.air.import.changes.emptiedTitle', N'en', N'Columns not in this file', 1),
    (N'permits.air.import.changes.emptiedText', N'en', N'These columns are not read from this file. After the import they are empty in every row of the permit: {columns}.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui-air-import ── кінець секції ──

-- NAEIMP:d569 ── D-569: the import replaces ALL rows of the permit (every region / area), not only the chosen scope ──
-- Змінені тексти наявних ключів: нові бази беруть нове значення з COLL-ui-air-import.sql; епілог оновлює наявні бази, лише
-- поки там стоїть САМЕ старе значення (правку адміністратора з /admin/ui-strings не затирає).
INSERT INTO #UiChanged ([Key], Lang, OldVal, NewVal)
SELECT v.[Key], v.Lang, v.OldVal, v.NewVal
  FROM (VALUES
    (N'permits.air.import.scopeNote', N'en', N'The import replaces the rows of this permit for the chosen region and area, and the rows of other permits for the same NAE (with your confirmation).', N'The import replaces ALL rows of this permit (every region and area: the last uploaded table is the only current one), and the rows of other permits for the same NAE (with your confirmation).'),
    (N'permits.air.import.scopeNote', N'ru', N'Импорт заменяет строки этого разрешения по выбранным региону и участку, а также строки других разрешений по тем же НАЕ (с вашего подтверждения).', N'Импорт заменяет ВСЕ строки этого разрешения (во всех регионах и участках: актуальна только последняя загруженная таблица), а также строки других разрешений по тем же НАЕ (с вашего подтверждения).'),
    (N'permits.air.import.scopeNote', N'kz', N'Импорт осы рұқсаттың таңдалған аймақ пен учаске бойынша жолдарын, сондай-ақ сол НАЕ бойынша басқа рұқсаттардың жолдарын (сіздің растауыңызбен) ауыстырады.', N'Импорт осы рұқсаттың БАРЛЫҚ жолдарын (барлық аймақтар мен учаскелерде: тек соңғы жүктелген кесте өзекті), сондай-ақ сол НАЕ бойынша басқа рұқсаттардың жолдарын (сіздің растауыңызбен) ауыстырады.')
       ) AS v ([Key], Lang, OldVal, NewVal)
OPTION (RECOMPILE);
GO
-- NAEIMP:d569 ── кінець секції ──
