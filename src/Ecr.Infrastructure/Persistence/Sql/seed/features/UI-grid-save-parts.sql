-- COLL:ui-grid-save-parts ── ru/kz: PSS-17 — пакет правок понад стелю сервера (50 000 комірок) зберігається частинами: хід, підсумок, часткове збереження; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'grid.saveParts.progress', N'ru', N'Изменения сохраняются частями: сохранено {saved} из {total} ячеек…'),
    (N'grid.saveParts.progress', N'kz', N'Өзгерістер бөліктермен сақталуда: {total} ұяшықтың {saved} сақталды…'),
    (N'grid.saveParts.done', N'ru', N'Все ячейки сохранены: {total} (частей: {parts}).'),
    (N'grid.saveParts.done', N'kz', N'Барлық ұяшықтар сақталды: {total} (бөліктер саны: {parts}).'),
    (N'grid.saveParts.partial', N'ru', N'Сохранено {saved} из {total} ячеек. Остальные ({rest}) ещё не сохранены — они остаются в таблице с отметкой «не сохранено»; причина — в сообщении об ошибке сохранения.'),
    (N'grid.saveParts.partial', N'kz', N'{total} ұяшықтың {saved} сақталды. Қалған {rest} ұяшық әлі сақталмаған — олар кестеде «сақталмаған» белгісімен қалады; себебі сақтау қатесі туралы хабарламада.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui-grid-save-parts ── кінець секції ──

-- COLL:ui-grid-save-parts ── en; ru/kz - порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'grid.saveParts.progress', N'en', N'Saving changes in parts: {saved} of {total} cells saved…', 1),
    (N'grid.saveParts.done', N'en', N'All cells saved: {total} (in {parts} parts).', 1),
    (N'grid.saveParts.partial', N'en', N'Saved {saved} of {total} cells. The other {rest} are not saved yet — they stay in the table marked as unsaved; the save error says why.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui-grid-save-parts ── кінець секції ──
