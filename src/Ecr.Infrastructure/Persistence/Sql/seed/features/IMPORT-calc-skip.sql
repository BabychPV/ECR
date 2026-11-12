-- IMPORT:calc-skip ── ru/kz: змінена/перерахована обчислювана комірка книги — попередження, а не відмова (P1 12.11) ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'import.calculatedSkippedTitle', N'ru', N'Рассчитываемые ячейки не будут изменены'),
    (N'import.calculatedSkippedTitle', N'kz', N'Есептелетін ұяшықтар өзгертілмейді'),
    (N'import.calculatedSkippedHint', N'ru', N'Эти ячейки рассчитывает система, поэтому значения из файла в них не применяются. Остальные изменения книги будут применены.'),
    (N'import.calculatedSkippedHint', N'kz', N'Бұл ұяшықтарды жүйе есептейді, сондықтан файлдағы мәндер оларға қолданылмайды. Кітаптың қалған өзгерістері қолданылады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- IMPORT:calc-skip ── кінець секції ──

-- IMPORT:calc-skip ── en ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'import.calculatedSkippedTitle', N'en', N'Calculated cells will not be changed', 1),
    (N'import.calculatedSkippedHint', N'en', N'The system calculates these cells, so the values from the file are not applied to them. The other changes in the workbook will be applied.', 1)
    -- ru/kz — порцією `IMPORT:calc-skip` у блоці I18N вище.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- IMPORT:calc-skip ── кінець секції ──
