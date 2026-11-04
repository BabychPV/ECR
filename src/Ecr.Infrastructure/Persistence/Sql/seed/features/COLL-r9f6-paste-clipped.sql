-- COLL:r9f6-paste-clipped ── ru/kz: R9-F6 / F6-03: вставка, більша за таблицю, — скільки не вмістилося; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'grid.pasteClipped', N'ru', N'Скопированные данные больше таблицы: строк ниже — {rows}, столбцов справа — {columns}; они не поместились и не вставлены. Добавьте строки или вставьте остаток отдельно.'),
    (N'grid.pasteClipped', N'kz', N'Көшірілген деректер кестеден үлкен: төмендегі {rows} жол және оң жақтағы {columns} баған сыймады және қойылмады. Жолдар қосыңыз немесе қалғанын бөлек қойыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:r9f6-paste-clipped ── кінець секції ──

-- COLL:r9f6-paste-clipped ── R9-F6 / F6-03: вставка, більша за таблицю, — скільки рядків і колонок не вмістилося; ru/kz — порцією COLL:r9f6-paste-clipped нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'grid.pasteClipped', N'en', N'The copied data is larger than the table: {rows} row(s) below and {columns} column(s) to the right did not fit and were not pasted. Add rows or paste the rest separately.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:r9f6-paste-clipped ── кінець секції ──
