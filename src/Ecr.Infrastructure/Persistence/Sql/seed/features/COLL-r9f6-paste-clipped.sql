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

