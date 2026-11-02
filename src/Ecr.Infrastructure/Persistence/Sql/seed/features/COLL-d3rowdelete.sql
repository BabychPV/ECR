-- COLL:d3rowdelete ── ru/kz D3-A: видалення динамічного рядка документа; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'grid.deleteRow', N'ru', N'Удалить строку'),
    (N'grid.deleteRow', N'kz', N'Жолды жою'),
    (N'grid.deleteRowHint', N'ru', N'Выберите ячейку строки, которую вы добавили, чтобы удалить её.'),
    (N'grid.deleteRowHint', N'kz', N'Жолды жою үшін өзіңіз қосқан жолдың ұяшығын таңдаңыз.'),
    (N'grid.deleteRowTitle', N'ru', N'Удалить строку «{row}»?'),
    (N'grid.deleteRowTitle', N'kz', N'«{row}» жолын жою керек пе?'),
    (N'grid.deleteRowConsequence', N'ru', N'Строка исчезнет из таблицы, итогов и отчётов. Её значения сохранятся в журнале аудита.'),
    (N'grid.deleteRowConsequence', N'kz', N'Жол кестеден, қорытындылардан және есептерден жоғалады. Оның мәндері аудит журналында сақталады.'),
    (N'grid.deleteRowNote', N'ru', N'Отменить это действие нельзя. Удалить можно только добавленную вами строку и только пока лист в черновике или отклонён.'),
    (N'grid.deleteRowNote', N'kz', N'Бұл әрекетті болдырмау мүмкін емес. Тек өзіңіз қосқан жолды және тек парақ жоба немесе қабылданбаған күйде болғанда жоюға болады.'),
    (N'grid.deleteRowVerb', N'ru', N'Удалить строку'),
    (N'grid.deleteRowVerb', N'kz', N'Жолды жою'),
    (N'grid.deleteRowOrdinalLabel', N'ru', N'строка №{n}'),
    (N'grid.deleteRowOrdinalLabel', N'kz', N'№{n} жол'),
    (N'err.ECR-ACCS-0403.deleteRowDenied', N'ru', N'Из этой таблицы нельзя удалить строку: {reason}.'),
    (N'err.ECR-ACCS-0403.deleteRowDenied', N'kz', N'Бұл кестеден жолды жоюға болмайды: {reason}.'),
    (N'err.ECR-ACCS-0403.deleteRowValuesDenied', N'ru', N'Строку нельзя удалить: в ней есть значения, которые вам нельзя стирать.'),
    (N'err.ECR-ACCS-0403.deleteRowValuesDenied', N'kz', N'Бұл жолды жоюға болмайды: онда сізге өшіруге рұқсат етілмеген мәндер бар.'),
    (N'err.ECR-ACCS-0403.deleteRowOutOfWindow', N'ru', N'Строку нельзя удалить: часть её значений вне окна доступа к периоду.'),
    (N'err.ECR-ACCS-0403.deleteRowOutOfWindow', N'kz', N'Бұл жолды жоюға болмайды: оның кейбір мәндері кезеңге қол жеткізу терезесінен тыс.'),
    (N'err.ECR-ROW-0404.rowNotFound', N'ru', N'Строка в этой таблице не найдена. Возможно, она уже удалена.'),
    (N'err.ECR-ROW-0404.rowNotFound', N'kz', N'Бұл кестеде жол табылмады. Ол бұрын жойылған болуы мүмкін.'),
    (N'err.ECR-ROW-0409.templateRowNotDeletable', N'ru', N'Эта строка берётся из шаблона, её нельзя удалить.'),
    (N'err.ECR-ROW-0409.templateRowNotDeletable', N'kz', N'Бұл жол үлгіден алынған, оны жоюға болмайды.'),
    (N'err.ECR-ROW-0409.sourceRowNotDeletable', N'ru', N'В этой строке данные, загруженные интеграцией, импортом или сбором (или строку ведёт событие источника): удалить её вручную нельзя.'),
    (N'err.ECR-ROW-0409.sourceRowNotDeletable', N'kz', N'Бұл жолда интеграция, импорт немесе жинау арқылы жүктелген деректер бар (немесе оны дереккөз оқиғасы жүргізеді): оны қолмен жоюға болмайды.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:d3rowdelete ── кінець секції ──
