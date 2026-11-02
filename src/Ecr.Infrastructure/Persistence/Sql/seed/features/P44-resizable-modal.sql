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
