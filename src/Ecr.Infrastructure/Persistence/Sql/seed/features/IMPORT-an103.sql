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

