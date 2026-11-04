-- IMPORT:an103 ── ru/kz D1-02: рядок змінено після експорту книги; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CELL-0409.importRowChangedSinceExport', N'ru', N'Кто-то изменил эту строку после выгрузки книги, поэтому значение из файла не применяется. Выгрузите документ заново и повторите изменения в новой книге.'),
    (N'err.ECR-CELL-0409.importRowChangedSinceExport', N'kz', N'Кітап экспортталғаннан кейін біреу бұл жолды өзгертті, сондықтан файлдағы мән қолданылмайды. Құжатты қайта экспорттап, өзгерістерді жаңа кітапта қайталаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- IMPORT:an103 ── кінець секції ──

-- IMPORT:an103 ── D1-02: рядок змінено після експорту книги — конфлікт у перегляді імпорту; ru/kz — порцією IMPORT:an103 у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CELL-0409.importRowChangedSinceExport', N'en', N'Someone changed this row after the workbook was exported, so the value from the file is not applied. Export the document again and repeat your changes in the new workbook.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- IMPORT:an103 ── кінець секції ──
