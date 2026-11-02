-- L4:cellcolor ── ru/kz: колір комірки; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'grid.cellColor.group', N'ru', N'Цвет ячеек'),
    (N'grid.cellColor.group', N'kz', N'Ұяшық түсі'),
    (N'grid.cellColor.fill', N'ru', N'Заливка'),
    (N'grid.cellColor.fill', N'kz', N'Бояу'),
    (N'grid.cellColor.text', N'ru', N'Цвет текста'),
    (N'grid.cellColor.text', N'kz', N'Мәтін түсі'),
    (N'grid.cellColor.noFill', N'ru', N'Без заливки'),
    (N'grid.cellColor.noFill', N'kz', N'Бояусыз'),
    (N'grid.cellColor.autoText', N'ru', N'Автоматический'),
    (N'grid.cellColor.autoText', N'kz', N'Автоматты'),
    (N'grid.cellColor.theme', N'ru', N'Цвета темы'),
    (N'grid.cellColor.theme', N'kz', N'Тақырып түстері'),
    (N'grid.cellColor.standard', N'ru', N'Стандартные цвета'),
    (N'grid.cellColor.standard', N'kz', N'Стандартты түстер'),
    (N'grid.cellColor.more', N'ru', N'Другие цвета…'),
    (N'grid.cellColor.more', N'kz', N'Басқа түстер…'),
    (N'grid.cellColor.noSelection', N'ru', N'Выделите ячейки, которые можно менять, чтобы закрасить их'),
    (N'grid.cellColor.noSelection', N'kz', N'Бояу үшін өзгертуге болатын ұяшықтарды белгілеңіз'),
    (N'grid.cellColor.readOnly', N'ru', N'Лист закрыт для правки, цвет менять нельзя'),
    (N'grid.cellColor.readOnly', N'kz', N'Парақ өңдеуге жабық, түсті өзгертуге болмайды'),
    (N'grid.cellColor.conflict', N'ru', N'Таблица изменилась, пока вы красили ячейки. Показано актуальное состояние, повторите окраску.'),
    (N'grid.cellColor.conflict', N'kz', N'Сіз бояған кезде кесте өзгерді. Ағымдағы күй көрсетілді, бояуды қайталаңыз.'),
    (N'grid.cellColor.scopeLabel', N'ru', N'Применить к'),
    (N'grid.cellColor.scopeLabel', N'kz', N'Қолдану аймағы'),
    (N'grid.cellColor.scope.selection', N'ru', N'Выделение'),
    (N'grid.cellColor.scope.selection', N'kz', N'Белгіленген'),
    (N'grid.cellColor.scope.row', N'ru', N'Вся строка'),
    (N'grid.cellColor.scope.row', N'kz', N'Бүкіл жол'),
    (N'grid.cellColor.scope.column', N'ru', N'Весь столбец'),
    (N'grid.cellColor.scope.column', N'kz', N'Бүкіл баған'),
    (N'grid.cellColor.scope.table', N'ru', N'Вся таблица'),
    (N'grid.cellColor.scope.table', N'kz', N'Бүкіл кесте'),
    (N'grid.cellColor.partial', N'ru', N'Не изменено ячеек: {count} (причина: {reasons}); остальные окрашены.'),
    (N'grid.cellColor.partial', N'kz', N'Өзгертілмеген ұяшық: {count} (себебі: {reasons}); қалғандары боялды.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- L4:cellcolor ── кінець секції ──

