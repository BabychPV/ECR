-- COLL:r5g1-undo-changed ── ru/kz: R5-G1 / G1-05: Undo/Redo не затирає комірки, змінені після кроку; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'grid.undoChangedSince', N'ru', N'Ячеек, изменённых после этого шага (другим пользователем, импортом или пересчётом): {count}. Отмена/повтор оставили их как есть; при необходимости введите значение вручную.'),
    (N'grid.undoChangedSince', N'kz', N'Осы қадамнан кейін өзгертілген ұяшықтар (басқа пайдаланушы, импорт немесе қайта есептеу): {count}. Болдырмау/қайталау оларды өзгертпеді; қажет болса, мәнді қолмен енгізіңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:r5g1-undo-changed ── кінець секції ──

