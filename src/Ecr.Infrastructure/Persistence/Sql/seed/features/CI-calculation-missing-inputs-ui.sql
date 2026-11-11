-- CI:calculation-missing-inputs-ui ── ru/kz: рядки, закриті правилом, але без входів формул (AN-164, doc 96 LTD-A41) — банер над сіткою; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'document.missingInputRows.hint', N'ru', N'Для этих строк правило методологии подошло, но формулам не хватает исходных данных, и результата нет. Заполните указанные ячейки и нажмите F9 (Пересчитать).'),
    (N'document.missingInputRows.hint', N'kz', N'Бұл жолдарға әдістеме ережесі сәйкес келді, бірақ формулаларға бастапқы деректер жетпейді, нәтиже жоқ. Көрсетілген ұяшықтарды толтырып, F9 (Қайта есептеу) басыңыз.'),
    (N'document.missingInputRows.group', N'ru', N'{table} — {rows}: не хватает исходных данных'),
    (N'document.missingInputRows.group', N'kz', N'{table} — {rows}: бастапқы деректер жетпейді'),
    (N'jobs.recalcMissingInputs', N'ru', N'Правило методологии подошло, но формулам не хватает исходных данных, строк: {count} (номера: {rows}). Эти строки не рассчитаны, остальные рассчитаны; ячейки для заполнения указаны над таблицей документа.'),
    (N'jobs.recalcMissingInputs', N'kz', N'Әдістеме ережесі сәйкес келді, бірақ формулаларға бастапқы деректер жетпейді, жолдар: {count} (нөмірлері: {rows}). Бұл жолдар есептелмеді, қалғандары есептелді; толтырылатын ұяшықтар құжат кестесінің үстінде көрсетілген.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- CI:calculation-missing-inputs-ui ── кінець секції ──

-- CI:calculation-missing-inputs-ui ── en; ru/kz - порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'document.missingInputRows.hint', N'en', N'These rows matched a methodology rule, but the formulas lack input data, so there is no result. Fill the named cells and press F9 (Recalculate).', 1),
    (N'document.missingInputRows.group', N'en', N'{table} — {rows}: input data missing', 1),
    (N'jobs.recalcMissingInputs', N'en', N'A methodology rule matched {count} row(s) (rows: {rows}), but the formulas lack input data. These rows were not calculated; the other rows were. The cells to fill are listed above the document table.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- CI:calculation-missing-inputs-ui ── кінець секції ──
