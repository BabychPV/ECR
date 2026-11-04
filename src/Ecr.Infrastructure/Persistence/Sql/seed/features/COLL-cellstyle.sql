-- COLL:cellstyle ── ru/kz: CELL-STYLE L3a (12.10), ECR-CELL-4224; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CELL-4224', N'ru', N'Недопустимое оформление ячеек'),
    (N'err.ECR-CELL-4224', N'kz', N'Ұяшықтарды форматтау жарамсыз'),
    (N'err.ECR-CELL-4224.tooManyItems', N'ru', N'Слишком много ячеек в одном запросе: {count} (лимит — {max}). Примените оформление частями.'),
    (N'err.ECR-CELL-4224.tooManyItems', N'kz', N'Бір сұрауда ұяшықтар тым көп: {count} (шектеу — {max}). Форматтауды бөліп қолданыңыз.'),
    (N'err.ECR-CELL-4224.unknownCell', N'ru', N'Часть ячеек отсутствует в этой таблице или недоступна вам: {cellCount}. Перезагрузите таблицу и повторите.'),
    (N'err.ECR-CELL-4224.unknownCell', N'kz', N'Кейбір ұяшықтар бұл кестеде жоқ немесе сізге қолжетімді емес: {cellCount}. Кестені қайта жүктеп, қайталаңыз.'),
    (N'err.ECR-CELL-4224.duplicateCell', N'ru', N'Одна и та же ячейка указана дважды: строка {rowKey}, столбец {columnCode}.'),
    (N'err.ECR-CELL-4224.duplicateCell', N'kz', N'Бір ұяшық екі рет көрсетілген: жол {rowKey}, баған {columnCode}.'),
    (N'err.ECR-CELL-4224.invalidColor', N'ru', N'Цвет должен быть записан как #rrggbb (строка {rowKey}, столбец {columnCode}).'),
    (N'err.ECR-CELL-4224.invalidColor', N'kz', N'Түс #rrggbb түрінде жазылуы керек (жол {rowKey}, баған {columnCode}).'),
    (N'err.ECR-CELL-4224.emptyChange', N'ru', N'Для строки {rowKey}, столбца {columnCode} нечего применять: задайте заливку, цвет текста или жирность либо сбросьте оформление.'),
    (N'err.ECR-CELL-4224.emptyChange', N'kz', N'Жол {rowKey}, баған {columnCode} үшін қолданатын ештеңе жоқ: толтыруды, мәтін түсін немесе қалың қаріпті беріңіз не форматтауды тастаңыз.'),
    (N'audit.eventLabel.CellStyleSet', N'ru', N'Оформление ячеек задано'),
    (N'audit.eventLabel.CellStyleSet', N'kz', N'Ұяшық форматтауы белгіленді'),
    (N'audit.eventLabel.CellStyleReset', N'ru', N'Оформление ячеек сброшено'),
    (N'audit.eventLabel.CellStyleReset', N'kz', N'Ұяшық форматтауы тасталды')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:cellstyle ── кінець секції ──
-- COLL:cellstyle ── CELL-STYLE L3a (12.10): користувацька заливка/колір тексту/жирність комірок; ru/kz — порцією COLL:cellstyle нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CELL-4224', N'en', N'Invalid cell formatting', 1),
    (N'err.ECR-CELL-4224.tooManyItems', N'en', N'Too many cells in one request: {count} (the limit is {max}). Apply the formatting in smaller parts.', 1),
    (N'err.ECR-CELL-4224.unknownCell', N'en', N'Some cells are not in this table or are not available to you: {cellCount}. Reload the table and try again.', 1),
    (N'err.ECR-CELL-4224.duplicateCell', N'en', N'The same cell is listed twice: row {rowKey}, column {columnCode}.', 1),
    (N'err.ECR-CELL-4224.invalidColor', N'en', N'The color must be written as #rrggbb (row {rowKey}, column {columnCode}).', 1),
    (N'err.ECR-CELL-4224.emptyChange', N'en', N'Nothing to apply for row {rowKey}, column {columnCode}: set a fill, a text color or bold, or reset the formatting.', 1),
    (N'audit.eventLabel.CellStyleSet', N'en', N'Cell formatting set', 1),
    (N'audit.eventLabel.CellStyleReset', N'en', N'Cell formatting reset', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:cellstyle ── кінець секції ──
