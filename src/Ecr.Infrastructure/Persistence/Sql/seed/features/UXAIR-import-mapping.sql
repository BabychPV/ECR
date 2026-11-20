-- COLL:ui-air-import ── ru/kz: UX 20.11, крок «Відповідність» майстра «Import parameters…»: таблиця полів зі станом і прикладами значень, вердикт, збереження профілю лише з назвою; власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'permits.air.import.fields.title', N'ru', N'Поля импорта'),
    (N'permits.air.import.fields.title', N'kz', N'Импорт өрістері'),
    (N'permits.air.import.fields.hint', N'ru', N'Каждое поле читается из одной колонки файла. Колонки найдены по тексту заголовка; примеры — значения из вашего файла.'),
    (N'permits.air.import.fields.hint', N'kz', N'Әр өріс файлдың бір бағанынан оқылады. Бағандар тақырып мәтіні бойынша табылды; мысалдар — файлыңыздағы мәндер.'),
    (N'permits.air.import.fields.field', N'ru', N'Поле'),
    (N'permits.air.import.fields.field', N'kz', N'Өріс'),
    (N'permits.air.import.fields.needed', N'ru', N'Нужно'),
    (N'permits.air.import.fields.needed', N'kz', N'Қажеттілігі'),
    (N'permits.air.import.fields.required', N'ru', N'Обязательное'),
    (N'permits.air.import.fields.required', N'kz', N'Міндетті'),
    (N'permits.air.import.fields.optional', N'ru', N'Необязательное'),
    (N'permits.air.import.fields.optional', N'kz', N'Міндетті емес'),
    (N'permits.air.import.fields.column', N'ru', N'Колонка в вашем файле'),
    (N'permits.air.import.fields.column', N'kz', N'Файлыңыздағы баған'),
    (N'permits.air.import.fields.examples', N'ru', N'Примеры из файла'),
    (N'permits.air.import.fields.examples', N'kz', N'Файлдан мысалдар'),
    (N'permits.air.import.fields.status', N'ru', N'Состояние'),
    (N'permits.air.import.fields.status', N'kz', N'Күйі'),
    (N'permits.air.import.fields.found', N'ru', N'Найдено'),
    (N'permits.air.import.fields.found', N'kz', N'Табылды'),
    (N'permits.air.import.fields.missing', N'ru', N'Не найдено — выберите колонку'),
    (N'permits.air.import.fields.missing', N'kz', N'Табылмады — бағанды таңдаңыз'),
    (N'permits.air.import.fields.notInFile', N'ru', N'Нет в этом файле'),
    (N'permits.air.import.fields.notInFile', N'kz', N'Бұл файлда жоқ'),
    (N'permits.air.import.fields.pickFor', N'ru', N'Колонка файла для поля «{field}»'),
    (N'permits.air.import.fields.pickFor', N'kz', N'«{field}» өрісіне арналған файл бағаны'),
    (N'permits.air.import.fields.pick', N'ru', N'Выберите колонку…'),
    (N'permits.air.import.fields.pick', N'kz', N'Бағанды таңдаңыз…'),
    (N'permits.air.import.fields.noColumns', N'ru', N'Таких колонок в файле нет'),
    (N'permits.air.import.fields.noColumns', N'kz', N'Файлда мұндай баған жоқ'),
    (N'permits.air.import.fields.usedBy', N'ru', N'сейчас читается в «{field}»'),
    (N'permits.air.import.fields.usedBy', N'kz', N'қазір «{field}» өрісіне оқылады'),
    (N'permits.air.import.fields.change', N'ru', N'Изменить'),
    (N'permits.air.import.fields.change', N'kz', N'Өзгерту'),
    (N'permits.air.import.fields.changeFor', N'ru', N'Изменить колонку поля «{field}»'),
    (N'permits.air.import.fields.changeFor', N'kz', N'«{field}» өрісінің бағанын өзгерту'),
    (N'permits.air.import.fields.keepFor', N'ru', N'Отмена — оставить колонку поля «{field}» как есть'),
    (N'permits.air.import.fields.keepFor', N'kz', N'Болдырмау — «{field}» өрісінің бағанын өзгертпеу'),
    (N'permits.air.import.fields.absent', N'ru', N'Необязательные поля, которых нет в этом файле: {count}'),
    (N'permits.air.import.fields.absent', N'kz', N'Бұл файлда жоқ міндетті емес өрістер: {count}'),
    (N'permits.air.import.fields.absentHint', N'ru', N'Делать ничего не нужно. Если такие данные в файле есть под другим заголовком — выберите их колонку.'),
    (N'permits.air.import.fields.absentHint', N'kz', N'Ештеңе істеу қажет емес. Егер мұндай деректер файлда басқа тақырыппен болса — олардың бағанын таңдаңыз.'),
    (N'permits.air.import.fields.absentAria', N'ru', N'Необязательные поля, которых нет в файле'),
    (N'permits.air.import.fields.absentAria', N'kz', N'Файлда жоқ міндетті емес өрістер'),
    (N'permits.air.import.verdict.readyTitle', N'ru', N'Все обязательные колонки найдены: из файла читается {found} полей из {total}'),
    (N'permits.air.import.verdict.readyTitle', N'kz', N'Барлық міндетті бағандар табылды: файлдан {total} өрістің {found} өрісі оқылады'),
    (N'permits.air.import.verdict.readyText', N'ru', N'Сверьте примеры значений и переходите к просмотру.'),
    (N'permits.air.import.verdict.readyText', N'kz', N'Мән мысалдарын салыстырып, қарап шығуға өтіңіз.'),
    (N'permits.air.import.verdict.missingTitle', N'ru', N'Не найдено обязательных колонок: {count}'),
    (N'permits.air.import.verdict.missingTitle', N'kz', N'Табылмаған міндетті бағандар: {count}'),
    (N'permits.air.import.verdict.missingText', N'ru', N'Для каждого поля с отметкой «Не найдено» выберите колонку в таблице ниже — файл перечитается сам.'),
    (N'permits.air.import.verdict.missingText', N'kz', N'«Табылмады» белгісі бар әр өріс үшін төмендегі кестеден бағанды таңдаңыз — файл өзі қайта оқылады.'),
    (N'permits.air.import.verdict.blockedTitle', N'ru', N'Файл пока нельзя импортировать'),
    (N'permits.air.import.verdict.blockedTitle', N'kz', N'Файлды әзірге импорттауға болмайды'),
    (N'permits.air.import.verdict.blockedText', N'ru', N'Замечания внизу страницы говорят, что исправить — в файле или в настройках.'),
    (N'permits.air.import.verdict.blockedText', N'kz', N'Беттің төменгі жағындағы ескертпелер файлда немесе баптауларда нені түзету керектігін айтады.'),
    (N'permits.air.import.map.blockedHint', N'ru', N'Исправьте отмеченное выше, чтобы продолжить.'),
    (N'permits.air.import.map.blockedHint', N'kz', N'Жалғастыру үшін жоғарыда белгіленгенді түзетіңіз.'),
    (N'permits.air.import.map.samples', N'ru', N'например: {values}'),
    (N'permits.air.import.map.samples', N'kz', N'мысалы: {values}'),
    (N'permits.air.import.rows.sheetRead', N'ru', N'лист «{sheet}»'),
    (N'permits.air.import.rows.sheetRead', N'kz', N'«{sheet}» парағы'),
    (N'permits.air.import.advancedHint', N'ru', N'Для администраторов: по какому тексту заголовка искать колонку, к чему относится значение, тип значения, новые поля.'),
    (N'permits.air.import.advancedHint', N'kz', N'Әкімшілер үшін: бағанды қандай тақырып мәтіні бойынша іздеу, мән неге қатысты, мән түрі, жаңа өрістер.'),
    (N'permits.air.import.parser.code', N'ru', N'Код'),
    (N'permits.air.import.parser.code', N'kz', N'Код'),
    (N'permits.air.import.parser.choice', N'ru', N'Одно из списка'),
    (N'permits.air.import.parser.choice', N'kz', N'Тізімнен біреуі'),
    (N'permits.air.import.parser.unit', N'ru', N'Единица измерения'),
    (N'permits.air.import.parser.unit', N'kz', N'Өлшем бірлігі'),
    (N'permits.air.import.save.codeShown', N'ru', N'Будет сохранён с кодом {code}'),
    (N'permits.air.import.save.codeShown', N'kz', N'{code} кодымен сақталады'),
    (N'permits.air.import.save.codeLater', N'ru', N'Код будет создан из названия'),
    (N'permits.air.import.save.codeLater', N'kz', N'Код атаудан жасалады'),
    (N'permits.air.import.save.changeCode', N'ru', N'Изменить код'),
    (N'permits.air.import.save.changeCode', N'kz', N'Кодты өзгерту'),
    (N'permits.air.import.fields.clearFor', N'ru', N'Снова искать колонку поля «{field}» по тексту заголовка'),
    (N'permits.air.import.fields.clearFor', N'kz', N'«{field}» өрісінің бағанын қайтадан тақырып мәтіні бойынша іздеу'),
    (N'permits.air.import.fields.perSource', N'ru', N'одно значение на источник'),
    (N'permits.air.import.fields.perSource', N'kz', N'әр көзге бір мән'),
    (N'permits.air.import.fields.perRow', N'ru', N'одно значение на строку вещества'),
    (N'permits.air.import.fields.perRow', N'kz', N'әр зат жолына бір мән'),
    (N'permits.air.import.fields.moveTitle', N'ru', N'Колонку {column} сейчас читает поле «{owner}»'),
    (N'permits.air.import.fields.moveTitle', N'kz', N'{column} бағанын қазір «{owner}» өрісі оқиды'),
    (N'permits.air.import.fields.moveText', N'ru', N'Перенести колонку {column} в «{field}»? У поля «{owner}» тогда не будет колонки — ему нужно будет выбрать другую.'),
    (N'permits.air.import.fields.moveText', N'kz', N'{column} бағанын «{field}» өрісіне ауыстыру керек пе? Сонда «{owner}» өрісінің бағаны болмайды — оған басқасын таңдау керек.'),
    (N'permits.air.import.fields.moveRequired', N'ru', N'«{owner}» — обязательное поле: импорт не продолжится, пока у него нет колонки.'),
    (N'permits.air.import.fields.moveRequired', N'kz', N'«{owner}» — міндетті өріс: оның бағаны болмайынша импорт жалғаспайды.'),
    (N'permits.air.import.fields.move', N'ru', N'Перенести колонку'),
    (N'permits.air.import.fields.move', N'kz', N'Бағанды ауыстыру'),
    (N'permits.air.import.fields.moveCancel', N'ru', N'Отмена — оставить колонку {column} полю «{owner}»'),
    (N'permits.air.import.fields.moveCancel', N'kz', N'Болдырмау — {column} бағанын «{owner}» өрісінде қалдыру'),
    (N'permits.air.import.verdict.pendingTitle', N'ru', N'Соответствие изменено'),
    (N'permits.air.import.verdict.pendingTitle', N'kz', N'Сәйкестік өзгертілді'),
    (N'permits.air.import.verdict.pendingText', N'ru', N'Ниже — результат прошлого чтения файла. Новое соответствие будет проверено, когда файл перечитается.'),
    (N'permits.air.import.verdict.pendingText', N'kz', N'Төменде — файлды алдыңғы оқудың нәтижесі. Жаңа сәйкестік файл қайта оқылғанда тексеріледі.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui-air-import ── кінець секції ──

-- COLL:ui-air-import ── en: крок «Відповідність» (область 1); ru/kz — порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'permits.air.import.fields.title', N'en', N'Fields to import', 1),
    (N'permits.air.import.fields.hint', N'en', N'Each field is read from one column of the file. The columns were found by their header text; the examples are values from your file.', 1),
    (N'permits.air.import.fields.field', N'en', N'Field', 1),
    (N'permits.air.import.fields.needed', N'en', N'Needed', 1),
    (N'permits.air.import.fields.required', N'en', N'Required', 1),
    (N'permits.air.import.fields.optional', N'en', N'Optional', 1),
    (N'permits.air.import.fields.column', N'en', N'Column in your file', 1),
    (N'permits.air.import.fields.examples', N'en', N'Examples from the file', 1),
    (N'permits.air.import.fields.status', N'en', N'Status', 1),
    (N'permits.air.import.fields.found', N'en', N'Found', 1),
    (N'permits.air.import.fields.missing', N'en', N'Not found — choose a column', 1),
    (N'permits.air.import.fields.notInFile', N'en', N'Not in this file', 1),
    (N'permits.air.import.fields.pickFor', N'en', N'File column for «{field}»', 1),
    (N'permits.air.import.fields.pick', N'en', N'Choose a column…', 1),
    (N'permits.air.import.fields.noColumns', N'en', N'No such column in the file', 1),
    (N'permits.air.import.fields.usedBy', N'en', N'now read into «{field}»', 1),
    (N'permits.air.import.fields.change', N'en', N'Change', 1),
    (N'permits.air.import.fields.changeFor', N'en', N'Change the column of «{field}»', 1),
    (N'permits.air.import.fields.keepFor', N'en', N'Cancel — keep the column of «{field}» as it is', 1),
    (N'permits.air.import.fields.absent', N'en', N'Optional fields this file does not have: {count}', 1),
    (N'permits.air.import.fields.absentHint', N'en', N'Nothing to do. If the file has such data under another header, choose its column.', 1),
    (N'permits.air.import.fields.absentAria', N'en', N'Optional fields the file does not have', 1),
    (N'permits.air.import.verdict.readyTitle', N'en', N'All required columns were found: {found} of {total} fields are read from the file', 1),
    (N'permits.air.import.verdict.readyText', N'en', N'Check the example values and go on to the preview.', 1),
    (N'permits.air.import.verdict.missingTitle', N'en', N'Required columns not found: {count}', 1),
    (N'permits.air.import.verdict.missingText', N'en', N'For each field marked «Not found», choose its column in the table below; the file is read again by itself.', 1),
    (N'permits.air.import.verdict.blockedTitle', N'en', N'The file cannot be imported yet', 1),
    (N'permits.air.import.verdict.blockedText', N'en', N'The findings at the bottom of the page say what to fix, in the file or in the settings.', 1),
    (N'permits.air.import.map.blockedHint', N'en', N'Fix what is marked above to continue.', 1),
    (N'permits.air.import.map.samples', N'en', N'e.g. {values}', 1),
    (N'permits.air.import.rows.sheetRead', N'en', N'sheet «{sheet}»', 1),
    (N'permits.air.import.advancedHint', N'en', N'For administrators: which header text finds a column, what a value belongs to, the value type, new fields.', 1),
    (N'permits.air.import.parser.code', N'en', N'Code', 1),
    (N'permits.air.import.parser.choice', N'en', N'One of a list', 1),
    (N'permits.air.import.parser.unit', N'en', N'Unit of measure', 1),
    (N'permits.air.import.save.codeShown', N'en', N'Will be saved under the code {code}', 1),
    (N'permits.air.import.save.codeLater', N'en', N'The code is made from the name', 1),
    (N'permits.air.import.save.changeCode', N'en', N'Change code', 1),
    (N'permits.air.import.fields.clearFor', N'en', N'Find the column of «{field}» by header text again', 1),
    (N'permits.air.import.fields.perSource', N'en', N'one value per source', 1),
    (N'permits.air.import.fields.perRow', N'en', N'one value per substance row', 1),
    (N'permits.air.import.fields.moveTitle', N'en', N'Column {column} is read by «{owner}» now', 1),
    (N'permits.air.import.fields.moveText', N'en', N'Move column {column} to «{field}»? «{owner}» will then have no column; choose another one for it.', 1),
    (N'permits.air.import.fields.moveRequired', N'en', N'«{owner}» is required: the import does not go on until it has a column.', 1),
    (N'permits.air.import.fields.move', N'en', N'Move column', 1),
    (N'permits.air.import.fields.moveCancel', N'en', N'Cancel — keep column {column} in «{owner}»', 1),
    (N'permits.air.import.verdict.pendingTitle', N'en', N'The mapping was changed', 1),
    (N'permits.air.import.verdict.pendingText', N'en', N'What is shown below is from the previous reading of the file. The new mapping is checked when the file is read again.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui-air-import ── кінець секції ──

-- UXAIR:import-mapping ── UX 20.11: простіші підписи кроку «Відповідність» (редактор правил — простими словами) ──
-- Змінені тексти наявних ключів: нові бази беруть нове значення з COLL-ui-air-import.sql; епілог оновлює наявні бази, лише
-- поки там стоїть САМЕ старе значення (правку адміністратора з /admin/ui-strings не затирає).
INSERT INTO #UiChanged ([Key], Lang, OldVal, NewVal)
SELECT v.[Key], v.Lang, v.OldVal, v.NewVal
  FROM (VALUES
    (N'permits.air.import.hint.mapping', N'en', N'Check how the columns of the file are matched to fields. Nothing is saved yet.', N'Check that the columns of the file were recognised, and choose a column only where you are asked to. Nothing is saved yet.'),
    (N'permits.air.import.hint.mapping', N'ru', N'Проверьте, как колонки файла сопоставлены полям. Пока ничего не сохраняется.', N'Проверьте, что колонки файла распознаны, и выберите колонку только там, где это попросят. Пока ничего не сохраняется.'),
    (N'permits.air.import.hint.mapping', N'kz', N'Файл бағандары өрістерге қалай сәйкестендірілгенін тексеріңіз. Әзірге ештеңе сақталмайды.', N'Файл бағандары танылғанын тексеріңіз және бағанды тек сұралған жерде таңдаңыз. Әзірге ештеңе сақталмайды.'),
    (N'permits.air.import.map.level', N'en', N'Level', N'One value per'),
    (N'permits.air.import.map.level', N'ru', N'Уровень', N'Одно значение на'),
    (N'permits.air.import.map.level', N'kz', N'Деңгей', N'Әрқайсысына бір мән'),
    (N'permits.air.import.add.level', N'kz', N'Бір мән', N'Әрқайсысына бір мән'),
    (N'permits.air.import.map.parser', N'en', N'Parser', N'Value type'),
    (N'permits.air.import.map.parser', N'ru', N'Разбор', N'Тип значения'),
    (N'permits.air.import.map.parser', N'kz', N'Талдау', N'Мән түрі'),
    (N'permits.air.import.map.required', N'en', N'Required', N'Must be in the file'),
    (N'permits.air.import.map.required', N'ru', N'Обязательное', N'Должно быть в файле'),
    (N'permits.air.import.map.required', N'kz', N'Міндетті', N'Файлда болуы керек'),
    (N'permits.air.import.map.patterns', N'en', N'Header patterns (variants separated by |)', N'Header texts to look for (variants separated by |)'),
    (N'permits.air.import.map.patterns', N'ru', N'Шаблоны заголовка (варианты через |)', N'Искомый текст заголовка (варианты через |)'),
    (N'permits.air.import.map.patterns', N'kz', N'Тақырып үлгілері (нұсқалар | арқылы)', N'Ізделетін тақырып мәтіні (нұсқалар | арқылы)'),
    (N'permits.air.import.map.unmapped', N'en', N'Unmapped columns: {count}', N'File columns not imported: {count}'),
    (N'permits.air.import.map.unmapped', N'ru', N'Колонок без соответствия: {count}', N'Колонки файла, которые не импортируются: {count}'),
    (N'permits.air.import.map.unmapped', N'kz', N'Сәйкестігі жоқ бағандар: {count}', N'Импортталмайтын файл бағандары: {count}')
       ) AS v ([Key], Lang, OldVal, NewVal)
OPTION (RECOMPILE);
GO
-- UXAIR:import-mapping ── кінець секції ──

-- UXAIR:import-mapping ── прибрані ключі: таблиця «Matched columns» стала таблицею полів (колонка файлу — з літерою й заголовком) ──
INSERT INTO #UiRemoved ([Key], Lang, OldVal)
SELECT v.[Key], v.Lang, v.OldVal
  FROM (VALUES
    (N'permits.air.import.map.headerText', N'en', N'Header in file'),
    (N'permits.air.import.map.headerText', N'ru', N'Заголовок в файле'),
    (N'permits.air.import.map.headerText', N'kz', N'Файлдағы тақырып')
       ) AS v ([Key], Lang, OldVal)
OPTION (RECOMPILE);
GO
-- UXAIR:import-mapping ── кінець секції ──
