-- COLL:ui-grid-denied-cells ── ru/kz: банер незбережених заборонених комірок (обчислювана колонка тощо) — перелік «колонка · рядок: причина» нижче; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'grid.deniedCellsSummary', N'ru', N'Не сохранено ячеек: {count} — их здесь нельзя изменять:'),
    (N'grid.deniedCellsSummary', N'kz', N'Сақталмаған ұяшықтар: {count} — оларды мұнда өзгертуге болмайды:')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui-grid-denied-cells ── кінець секції ──

-- COLL:ui-grid-denied-cells ── en; ru/kz - порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'grid.deniedCellsSummary', N'en', N'{count} cell(s) not saved — they cannot be edited here:', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui-grid-denied-cells ── кінець секції ──
