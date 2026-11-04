-- COLL:r5-i1-row-window-stale ── ru/kz: R5-I1 / I1-03: у комірці лишилося число попереднього вікна чи джерела рядка; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'coverageEvents.rowWindowStale', N'ru', N'Строка {rowKey} (сопоставление по окну строки {mapId}): окно или источник строки изменились, но нового значения нет ({status}). Ячейка не обновлена и содержит значение для прежнего окна или источника. Проверьте строку, прежде чем полагаться на число.'),
    (N'coverageEvents.rowWindowStale', N'kz', N'{rowKey} жолы (жол терезесі бойынша {mapId} сәйкестендіру): жолдың терезесі немесе дереккөзі өзгерді, бірақ жаңа мән алынбады ({status}). Ұяшық жаңартылмады және бұрынғы терезе немесе дереккөз үшін мәнді сақтайды. Санға сенбес бұрын жолды тексеріңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:r5-i1-row-window-stale ── кінець секції ──

-- COLL:r5-i1-row-window-stale ── R5-I1 / I1-03: вікно чи джерело рядка змінилося, а нового значення немає — у комірці число попереднього; ru/kz — порцією COLL:r5-i1-row-window-stale нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'coverageEvents.rowWindowStale', N'en', N'Row {rowKey} (row-window mapping {mapId}): the window or the source of the row changed, but no new value was obtained ({status}). The cell was not updated and still holds the value for the previous window or source. Check the row before relying on the number.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:r5-i1-row-window-stale ── кінець секції ──
