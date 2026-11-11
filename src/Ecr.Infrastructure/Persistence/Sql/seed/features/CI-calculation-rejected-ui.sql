-- CI:calculation-rejected-ui ── ru/kz: рядки, відхилені правилом категорії (doc 95, 10.11) — банер над сіткою й панель «Calculation results»; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'document.rejectedRows.hint', N'ru', N'Для этих строк правило методологии подошло, но категорию по строке определить не удалось. Заполните указанные ячейки и нажмите F9 (Пересчитать).'),
    (N'document.rejectedRows.hint', N'kz', N'Бұл жолдарға әдістеме ережесі сәйкес келді, бірақ жол бойынша санатты анықтау мүмкін болмады. Көрсетілген ұяшықтарды толтырып, F9 (Қайта есептеу) басыңыз.'),
    (N'document.rejectedRows.group', N'ru', N'{table} — {rows}: категория не определена'),
    (N'document.rejectedRows.group', N'kz', N'{table} — {rows}: санат анықталмады'),
    (N'document.rejectedRows.row', N'ru', N'Строка {row} · правило {rule}'),
    (N'document.rejectedRows.row', N'kz', N'{row}-жол · {rule} ережесі'),
    (N'document.rejectedRows.rowFill', N'ru', N'Строка {row} · правило {rule}: заполните {columns}'),
    (N'document.rejectedRows.rowFill', N'kz', N'{row}-жол · {rule} ережесі: толтырыңыз {columns}'),
    (N'document.rejectedRows.ruleUnknown', N'ru', N'неизвестно'),
    (N'document.rejectedRows.ruleUnknown', N'kz', N'белгісіз'),
    (N'documents.calculationResultsNotCalculated', N'ru', N'не рассчитано строк: {count}'),
    (N'documents.calculationResultsNotCalculated', N'kz', N'есептелмеген жолдар: {count}'),
    (N'documents.calculationResultsNotCalculatedHint', N'ru', N'Строк, не вошедших в расчёт: {count}.'),
    (N'documents.calculationResultsNotCalculatedHint', N'kz', N'Есептеуге кірмеген жолдар: {count}.'),
    (N'documents.calculationResultsNotCalculatedShow', N'ru', N'Показать строки и ячейки для заполнения'),
    (N'documents.calculationResultsNotCalculatedShow', N'kz', N'Жолдар мен толтырылатын ұяшықтарды көрсету')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- CI:calculation-rejected-ui ── кінець секції ──

-- CI:calculation-rejected-ui ── en; ru/kz - порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'document.rejectedRows.hint', N'en', N'These rows matched a methodology rule, but the category could not be determined from the row. Fill the named cells and press F9 (Recalculate).', 1),
    (N'document.rejectedRows.group', N'en', N'{table} — {rows}: category not determined', 1),
    (N'document.rejectedRows.row', N'en', N'Row {row} · rule {rule}', 1),
    (N'document.rejectedRows.rowFill', N'en', N'Row {row} · rule {rule}: fill {columns}', 1),
    (N'document.rejectedRows.ruleUnknown', N'en', N'unknown', 1),
    (N'documents.calculationResultsNotCalculated', N'en', N'rows not calculated: {count}', 1),
    (N'documents.calculationResultsNotCalculatedHint', N'en', N'Rows not included in the calculation: {count}.', 1),
    (N'documents.calculationResultsNotCalculatedShow', N'en', N'Show the rows and the cells to fill', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- CI:calculation-rejected-ui ── кінець секції ──
