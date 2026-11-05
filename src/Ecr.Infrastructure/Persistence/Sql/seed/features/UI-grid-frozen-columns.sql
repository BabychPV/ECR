-- COLL:ui-grid-frozen-columns ── ru/kz: закріплені ключові колонки сітки документа (вибір користувача на таблицю); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'grid.frozenColumns.label', N'ru', N'Закрепить столбцы'),
    (N'grid.frozenColumns.label', N'kz', N'Бағандарды бекіту'),
    (N'grid.frozenColumns.default', N'ru', N'По умолчанию'),
    (N'grid.frozenColumns.default', N'kz', N'Әдепкі'),
    (N'grid.frozenColumns.none', N'ru', N'Не закреплять'),
    (N'grid.frozenColumns.none', N'kz', N'Бекітпеу'),
    (N'grid.frozenColumns.upTo', N'ru', N'До столбца «{column}»'),
    (N'grid.frozenColumns.upTo', N'kz', N'«{column}» бағанына дейін')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui-grid-frozen-columns ── кінець секції ──

-- COLL:ui-grid-frozen-columns ── en; ru/kz - порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'grid.frozenColumns.label', N'en', N'Freeze columns', 1),
    (N'grid.frozenColumns.default', N'en', N'Default', 1),
    (N'grid.frozenColumns.none', N'en', N'None', 1),
    (N'grid.frozenColumns.upTo', N'en', N'Up to {column}', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui-grid-frozen-columns ── кінець секції ──
