-- NAE:n4-import ── ru/kz загального рушія імпорту довідників з Excel (N4); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-IMP-0422.regSpecInvalid', N'ru', N'Профиль импорта недопустим ({where}): {reason}.'),
    (N'err.ECR-IMP-0422.regSpecInvalid', N'kz', N'Импорт профилі жарамсыз ({where}): {reason}.'),
    (N'err.ECR-IMP-0422.regHeaderNotFound', N'ru', N'Заголовок таблицы в файле не найден. Укажите строки заголовка в профиле импорта.'),
    (N'err.ECR-IMP-0422.regHeaderNotFound', N'kz', N'Файлдан кесте тақырыбы табылмады. Импорт профилінде тақырып жолдарын көрсетіңіз.'),
    (N'err.ECR-IMP-0422.regRejected', N'ru', N'В файле ошибок: {errorCount}. Ничего не импортировано: исправьте их и загрузите файл заново.'),
    (N'err.ECR-IMP-0422.regRejected', N'kz', N'Файлда қателер бар: {errorCount}. Ештеңе импортталмады: оларды түзетіп, файлды қайта жүктеңіз.'),
    (N'err.ECR-IMP-0422.regReplaceNotConfirmed', N'ru', N'Импорт заменит строки других разрешений ({owners}). Подтвердите замену, чтобы продолжить.'),
    (N'err.ECR-IMP-0422.regReplaceNotConfirmed', N'kz', N'Импорт басқа рұқсаттардың жолдарын ауыстырады ({owners}). Жалғастыру үшін ауыстыруды растаңыз.'),
    (N'err.ECR-IMP-0422.regPlanChanged', N'ru', N'Данные изменились после предпросмотра. Ничего не импортировано: постройте предпросмотр заново.'),
    (N'err.ECR-IMP-0422.regPlanChanged', N'kz', N'Алдын ала қарағаннан кейін деректер өзгерді. Ештеңе импортталмады: алдын ала қарауды қайта жасаңыз.'),
    (N'err.ECR-IMP-0422.regColumnMissing', N'ru', N'Столбец «{field}» не найден в заголовке файла.'),
    (N'err.ECR-IMP-0422.regColumnMissing', N'kz', N'«{field}» бағаны файл тақырыбынан табылмады.'),
    (N'err.ECR-IMP-0422.regColumnAmbiguous', N'ru', N'Полю «{field}» соответствует несколько столбцов: {columns}. Уточните шаблон заголовка.'),
    (N'err.ECR-IMP-0422.regColumnAmbiguous', N'kz', N'«{field}» өрісіне бірнеше баған сәйкес келеді: {columns}. Тақырып үлгісін нақтылаңыз.'),
    (N'err.ECR-IMP-0422.regColumnClaimed', N'ru', N'Столбец {column} уже занят полем «{other}» и не может быть полем «{field}».'),
    (N'err.ECR-IMP-0422.regColumnClaimed', N'kz', N'{column} бағанын «{other}» өрісі алып қойған, ол «{field}» бола алмайды.'),
    (N'err.ECR-IMP-0422.regValueInvalid', N'ru', N'«{field}»: значение «{value}» не читается.'),
    (N'err.ECR-IMP-0422.regValueInvalid', N'kz', N'«{field}»: «{value}» мәні оқылмайды.'),
    (N'err.ECR-IMP-0422.regValueRequired', N'ru', N'«{field}» не может быть пустым.'),
    (N'err.ECR-IMP-0422.regValueRequired', N'kz', N'«{field}» бос болмауы керек.'),
    (N'err.ECR-IMP-0422.regValueNegative', N'ru', N'«{field}» не может быть отрицательным («{value}»).'),
    (N'err.ECR-IMP-0422.regValueNegative', N'kz', N'«{field}» теріс болмауы керек («{value}»).'),
    (N'err.ECR-IMP-0422.regValueAmbiguous', N'ru', N'«{field}»: «{value}» прочитано с запятой как десятичным разделителем. Убедитесь, что это не разделитель тысяч.'),
    (N'err.ECR-IMP-0422.regValueAmbiguous', N'kz', N'«{field}»: «{value}» үтір ондық бөлгіш ретінде оқылды. Бұл мыңдық бөлгіш емес екенін тексеріңіз.'),
    (N'err.ECR-IMP-0422.regDuplicateParent', N'ru', N'Источник {key} встречается дважды: строки {firstRow} и {row}.'),
    (N'err.ECR-IMP-0422.regDuplicateParent', N'kz', N'{key} көзі екі рет кездеседі: {firstRow} және {row} жолдары.'),
    (N'err.ECR-IMP-0422.regDuplicateChild', N'ru', N'Вещество {key} встречается дважды у источника {parent}.'),
    (N'err.ECR-IMP-0422.regDuplicateChild', N'kz', N'{key} заты {parent} көзінде екі рет кездеседі.'),
    (N'err.ECR-IMP-0422.regReferenceMissing', N'ru', N'{kind} {key} отсутствует в системе. Создайте его или пропустите.'),
    (N'err.ECR-IMP-0422.regReferenceMissing', N'kz', N'{kind} {key} жүйеде жоқ. Оны жасаңыз немесе өткізіп жіберіңіз.'),
    (N'err.ECR-IMP-0422.regReferenceUnchecked', N'ru', N'Коды вида {kind} не проверены по системе.'),
    (N'err.ECR-IMP-0422.regReferenceUnchecked', N'kz', N'{kind} түріндегі кодтар жүйе бойынша тексерілмеді.'),
    (N'err.ECR-IMP-0422.regChildWithoutParent', N'ru', N'Строка {row}: у строки вещества нет источника выше.'),
    (N'err.ECR-IMP-0422.regChildWithoutParent', N'kz', N'{row}-жол: заттың жолының үстінде көз жоқ.'),
    (N'err.ECR-IMP-0422.regParentWithoutChildren', N'ru', N'У источника {key} нет строк веществ.'),
    (N'err.ECR-IMP-0422.regParentWithoutChildren', N'kz', N'{key} көзінде заттардың жолдары жоқ.'),
    (N'err.ECR-IMP-0422.regNoRows', N'ru', N'В файле нет строк данных.'),
    (N'err.ECR-IMP-0422.regNoRows', N'kz', N'Файлда деректер жолдары жоқ.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- NAE:n4-import ── кінець секції ──
-- NAE:n4-import ── generic registry import engine (Excel with a header block, groups, two levels); ru/kz in the section NAE:n4-import below.
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-IMP-0422.regSpecInvalid',        N'en', N'The import profile is not valid ({where}): {reason}.', 1),
    (N'err.ECR-IMP-0422.regHeaderNotFound',     N'en', N'The header of the table was not found in the file. Set the header rows in the import profile.', 1),
    (N'err.ECR-IMP-0422.regRejected',           N'en', N'The file has {errorCount} error(s). Nothing was imported: fix them and upload the file again.', 1),
    (N'err.ECR-IMP-0422.regReplaceNotConfirmed', N'en', N'The import replaces rows of other permits ({owners}). Confirm the replacement to continue.', 1),
    (N'err.ECR-IMP-0422.regPlanChanged',        N'en', N'The data changed since the preview. Nothing was imported: build the preview again.', 1),
    (N'err.ECR-IMP-0422.regColumnMissing',      N'en', N'The column for "{field}" was not found in the file header.', 1),
    (N'err.ECR-IMP-0422.regColumnAmbiguous',    N'en', N'Several columns match "{field}": {columns}. Make the header pattern more specific.', 1),
    (N'err.ECR-IMP-0422.regColumnClaimed',      N'en', N'Column {column} is already used by "{other}" and cannot also be "{field}".', 1),
    (N'err.ECR-IMP-0422.regValueInvalid',       N'en', N'"{field}": the value "{value}" cannot be read.', 1),
    (N'err.ECR-IMP-0422.regValueRequired',      N'en', N'"{field}" must not be empty.', 1),
    (N'err.ECR-IMP-0422.regValueNegative',      N'en', N'"{field}" must not be negative ("{value}").', 1),
    (N'err.ECR-IMP-0422.regValueAmbiguous',     N'en', N'"{field}": "{value}" was read with the comma as the decimal separator. Check that it is not a thousands separator.', 1),
    (N'err.ECR-IMP-0422.regDuplicateParent',    N'en', N'Source {key} appears twice: rows {firstRow} and {row}.', 1),
    (N'err.ECR-IMP-0422.regDuplicateChild',     N'en', N'Substance {key} appears twice under source {parent}.', 1),
    (N'err.ECR-IMP-0422.regReferenceMissing',   N'en', N'{kind} {key} does not exist in the system. Create it or skip it.', 1),
    (N'err.ECR-IMP-0422.regReferenceUnchecked', N'en', N'Codes of kind {kind} were not checked against the system.', 1),
    (N'err.ECR-IMP-0422.regChildWithoutParent', N'en', N'Row {row}: a substance row has no source above it.', 1),
    (N'err.ECR-IMP-0422.regParentWithoutChildren', N'en', N'Source {key} has no substance rows.', 1),
    (N'err.ECR-IMP-0422.regNoRows',             N'en', N'The file has no data rows.', 1),
    (N'err.ECR-IMP-0422.regValueEmpty',         N'en', N'"{field}" is empty. The row is imported without it.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- NAE:n4-import ── кінець секції ──
