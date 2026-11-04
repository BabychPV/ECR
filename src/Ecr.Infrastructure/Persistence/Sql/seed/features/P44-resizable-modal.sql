-- P44:resizable-modal ── ru/kz: кнопки вікна формул і методологій (на весь екран, ручка розміру); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'common.modal.fullScreen', N'ru', N'Во весь экран'),
    (N'common.modal.fullScreen', N'kz', N'Толық экран'),
    (N'common.modal.expand', N'ru', N'Развернуть во весь экран'),
    (N'common.modal.expand', N'kz', N'Толық экранға жаю'),
    (N'common.modal.restore', N'ru', N'Свернуть до обычного размера'),
    (N'common.modal.restore', N'kz', N'Қалыпты өлшемге қайтару'),
    (N'common.modal.resize', N'ru', N'Изменить размер окна'),
    (N'common.modal.resize', N'kz', N'Терезе өлшемін өзгерту'),
    (N'common.modal.resizeHint', N'ru', N'Потяните за угол или используйте стрелки; с Shift шаг больше.'),
    (N'common.modal.resizeHint', N'kz', N'Бұрышынан тартыңыз немесе бағыт пернелерін басыңыз; Shift басылса, қадам үлкенірек.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- P44:resizable-modal ── кінець секції ──
-- P44:resizable-modal ── кнопки вікна формул і методологій: на весь екран, ручка розміру; власна порція ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'common.modal.fullScreen', N'en', N'Full screen', 1),
    (N'common.modal.expand', N'en', N'Expand to full screen', 1),
    (N'common.modal.restore', N'en', N'Restore size', 1),
    (N'common.modal.resize', N'en', N'Resize window', 1),
    (N'common.modal.resizeHint', N'en', N'Drag the corner or use the arrow keys; hold Shift for bigger steps.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- P44:resizable-modal ── кінець секції ──
