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
