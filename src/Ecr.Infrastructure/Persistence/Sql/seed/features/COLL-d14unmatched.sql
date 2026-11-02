-- COLL:d14unmatched ── ru/kz D14: рядки без правила розрахунку; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'document.unmatchedRows.title', N'ru', N'Часть строк не вошла в расчёт'),
    (N'document.unmatchedRows.title', N'kz', N'Кейбір жолдар есептеуге кірмеді'),
    (N'document.unmatchedRows.hint', N'ru', N'Строк, к которым не подошло ни одно правило расчёта: {count}; они пропущены, например из-за статуса, отличного от Running. Это информация, а не ошибка. Строки: {rows}'),
    (N'document.unmatchedRows.hint', N'kz', N'Ешқандай есептеу ережесіне сәйкес келмеген жолдар: {count}; олар өткізіп жіберілді, мысалы мәртебесі Running емес болғандықтан. Бұл қате емес, ақпарат. Жолдар: {rows}')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:d14unmatched ── кінець секції ──
-- COLL:d14unmatched ── D14: рядки без правила розрахунку - інформація на аркуші; ru/kz - порцією COLL:d14unmatched нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'document.unmatchedRows.title', N'en', N'Some rows are not included in the calculation', 1),
    (N'document.unmatchedRows.hint', N'en', N'{count} row(s) matched no calculation rule and were skipped, for example because the status is not Running. This is information, not an error. Row: {rows}', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:d14unmatched ── кінець секції ──
