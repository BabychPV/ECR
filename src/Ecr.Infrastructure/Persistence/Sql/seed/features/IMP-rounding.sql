-- IMP:rounding ── ru/kz округлення імпорту .xlsx до Scale колонки (ФВ-9.16b); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'import.rounded', N'ru', N'Округлено: {count}'),
    (N'import.rounded', N'kz', N'Дөңгелектелді: {count}'),
    (N'import.roundedMark', N'ru', N'округлено из {value}'),
    (N'import.roundedMark', N'kz', N'{value} мәнінен дөңгелектелді'),
    (N'import.roundedTitle', N'ru', N'Числа округлены до знаков после запятой столбца'),
    (N'import.roundedTitle', N'kz', N'Сандар бағанның үтірден кейінгі таңбаларына дейін дөңгелектелді'),
    (N'import.roundedHint', N'ru', N'В этих числах из файла больше знаков после запятой, чем допускает столбец. Они округлены (половина — от нуля), и сохранено будет именно округлённое значение. Сверьте их с файлом перед применением.'),
    (N'import.roundedHint', N'kz', N'Файлдағы бұл сандарда баған рұқсат ететіннен көп үтірден кейінгі таңба бар. Олар дөңгелектелді (жартысы — нөлден алысқа), және дәл дөңгелектелген мән сақталады. Қолданар алдында оларды файлмен салыстырыңыз.'),
    (N'import.inFile', N'ru', N'В файле'),
    (N'import.inFile', N'kz', N'Файлда'),
    (N'err.ECR-CELL-0422.importPrecision', N'ru', N'В числе больше цифр, чем допускает столбец, даже после округления до его знаков после запятой.'),
    (N'err.ECR-CELL-0422.importPrecision', N'kz', N'Бағанның үтірден кейінгі таңбаларына дейін дөңгелектегеннен кейін де санда баған рұқсат ететіннен көп цифр бар.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- IMP:rounding ── кінець секції ──

-- IMP:rounding ── ФВ-9.16b: імпорт .xlsx округлює до Scale колонки; позначка й перелік у перегляді (`ImportPanel`), точність після округлення (`ImportDiffBuilder`).
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'import.rounded', N'en', N'{count} rounded', 1),
    (N'import.roundedMark', N'en', N'rounded from {value}', 1),
    (N'import.roundedTitle', N'en', N'Numbers rounded to the column''s decimal places', 1),
    (N'import.roundedHint', N'en', N'These numbers in the file have more decimal places than the column allows. They are rounded half away from zero, and the rounded value is the one that will be saved. Check them against the file before applying.', 1),
    (N'import.inFile', N'en', N'In the file', 1),
    (N'err.ECR-CELL-0422.importPrecision', N'en', N'The number has more digits than the column allows, even after rounding to the column''s decimal places.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- IMP:rounding: кінець секції
