-- COLL:r7y5-01-layout-changed ── ru/kz: R7-Y5 / Y5-01: рядки чи колонки таблиці в Excel відсортовано, вставлено чи видалено — відмова таблиці, а не значення в чужих рядках; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-IMP-0422.importLayoutChanged', N'ru', N'Строки или столбцы этой таблицы в Excel после экспорта отсортировали, вставили, удалили или переименовали, поэтому значения больше не совпадают со своими строками. Таблица не импортирована. Выгрузите книгу заново и правьте значения на месте, не сортируя и не вставляя строки и столбцы.'),
    (N'err.ECR-IMP-0422.importLayoutChanged', N'kz', N'Бұл кестенің жолдары немесе бағандары экспорттан кейін Excel-де сұрыпталған, қосылған, жойылған немесе қайта аталған, сондықтан мәндер өз жолдарына сәйкес келмейді. Кесте импортталмады. Кітапты қайта жүктеп алып, жолдар мен бағандарды сұрыптамай және қоспай, мәндерді орнында түзетіңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:r7y5-01-layout-changed ── кінець секції ──

-- COLL:r7y5-01-layout-changed ── R7-Y5 / Y5-01: рядки чи колонки таблиці в Excel відсортовано, вставлено чи видалено — відмова таблиці, а не значення в чужих рядках; ru/kz — порцією COLL:r7y5-01-layout-changed нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-IMP-0422.importLayoutChanged', N'en', N'Rows or columns of this table were sorted, inserted, deleted or relabelled in Excel after export, so the values no longer match their rows. The table was not imported. Download the workbook again and edit values in place without sorting or inserting rows and columns.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:r7y5-01-layout-changed ── кінець секції ──
