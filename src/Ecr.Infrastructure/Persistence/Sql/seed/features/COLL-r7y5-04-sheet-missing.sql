-- COLL:r7y5-04-sheet-missing ── ru/kz: R7-Y5 / Y5-04: аркуш таблиці перейменовано або видалено в Excel — відмова таблиці в перегляді імпорту, а не 500; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-IMP-0422.importSheetMissing', N'ru', N'Листа этой таблицы нет в файле: его переименовали или удалили в Excel после экспорта. Верните имя листа или выгрузите книгу заново.'),
    (N'err.ECR-IMP-0422.importSheetMissing', N'kz', N'Бұл кестенің парағы файлда жоқ: оны экспорттан кейін Excel-де қайта атаған немесе жойған. Парақ атауын қайтарыңыз немесе кітапты қайта жүктеп алыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:r7y5-04-sheet-missing ── кінець секції ──

-- COLL:r7y5-04-sheet-missing ── R7-Y5 / Y5-04: аркуш таблиці перейменовано або видалено в Excel — відмова таблиці в перегляді імпорту, а не 500; ru/kz — порцією COLL:r7y5-04-sheet-missing нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-IMP-0422.importSheetMissing', N'en', N'The sheet of this table is missing in the file: it was renamed or deleted in Excel after export. Restore the sheet name or download the workbook again.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:r7y5-04-sheet-missing ── кінець секції ──
