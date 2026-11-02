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
-- COLL:d3rowdelete ── D3-A: видалення динамічного рядка документа (кнопка, підтвердження, відмови); ru/kz — порцією COLL:d3rowdelete нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'grid.deleteRow', N'en', N'Delete row', 1),
    (N'grid.deleteRowHint', N'en', N'Select a cell of a row you added to delete it.', 1),
    (N'grid.deleteRowTitle', N'en', N'Delete row “{row}”?', 1),
    (N'grid.deleteRowConsequence', N'en', N'The row disappears from the table, totals and reports. Its values are kept in the audit trail.', 1),
    (N'grid.deleteRowNote', N'en', N'There is no undo for this action. Only a row you added can be deleted, and only while the sheet is a draft or rejected.', 1),
    (N'grid.deleteRowVerb', N'en', N'Delete row', 1),
    (N'grid.deleteRowOrdinalLabel', N'en', N'row #{n}', 1),
    (N'err.ECR-ACCS-0403.deleteRowDenied', N'en', N'A row cannot be deleted from this table: {reason}.', 1),
    (N'err.ECR-ACCS-0403.deleteRowValuesDenied', N'en', N'This row cannot be deleted: it holds values you are not allowed to clear.', 1),
    (N'err.ECR-ACCS-0403.deleteRowOutOfWindow', N'en', N'This row cannot be deleted: some of its values are outside the period access window.', 1),
    (N'err.ECR-ROW-0404.rowNotFound', N'en', N'The row was not found in this table. It may have been deleted already.', 1),
    (N'err.ECR-ROW-0409.templateRowNotDeletable', N'en', N'This row comes from the template and cannot be deleted.', 1),
    (N'err.ECR-ROW-0409.sourceRowNotDeletable', N'en', N'This row holds data loaded by an integration, import or collection (or is maintained by a source event) and cannot be deleted by hand.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:d3rowdelete ── кінець секції ──
