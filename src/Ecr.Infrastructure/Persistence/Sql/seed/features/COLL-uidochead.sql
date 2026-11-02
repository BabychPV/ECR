-- COLL:uidochead ── ru/kz UI-14/15/16: шапка й панель дій документа; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'document.saveState.allSaved', N'ru', N'Все изменения сохранены'),
    (N'document.saveState.allSaved', N'kz', N'Барлық өзгерістер сақталды'),
    (N'document.saveState.savedAt', N'ru', N'Сохранено в {time}'),
    (N'document.saveState.savedAt', N'kz', N'{time} сақталды'),
    (N'document.saveState.readOnly', N'ru', N'Только чтение'),
    (N'document.saveState.readOnly', N'kz', N'Тек оқу'),
    (N'document.saveState.notSaved', N'ru', N'не сохранено'),
    (N'document.saveState.notSaved', N'kz', N'сақталмады'),
    (N'document.saveState.show', N'ru', N'Показать'),
    (N'document.saveState.show', N'kz', N'Көрсету'),
    (N'document.saveState.unsaved.one', N'ru', N'{count} несохранённое изменение'),
    (N'document.saveState.unsaved.one', N'kz', N'{count} сақталмаған өзгеріс'),
    (N'document.saveState.unsaved.few', N'ru', N'{count} несохранённых изменения'),
    (N'document.saveState.unsaved.few', N'kz', N'{count} сақталмаған өзгеріс'),
    (N'document.saveState.unsaved.many', N'ru', N'{count} несохранённых изменений'),
    (N'document.saveState.unsaved.many', N'kz', N'{count} сақталмаған өзгеріс'),
    (N'document.saveState.unsaved.other', N'ru', N'{count} несохранённого изменения'),
    (N'document.saveState.unsaved.other', N'kz', N'{count} сақталмаған өзгеріс'),
    (N'document.sheetStateLabel', N'ru', N'Лист:'),
    (N'document.sheetStateLabel', N'kz', N'Парақ:'),
    (N'document.noIssues', N'ru', N'Замечаний нет'),
    (N'document.noIssues', N'kz', N'Ескертпелер жоқ'),
    (N'document.issuesCount.one', N'ru', N'{count} замечание'),
    (N'document.issuesCount.one', N'kz', N'{count} ескертпе'),
    (N'document.issuesCount.few', N'ru', N'{count} замечания'),
    (N'document.issuesCount.few', N'kz', N'{count} ескертпе'),
    (N'document.issuesCount.many', N'ru', N'{count} замечаний'),
    (N'document.issuesCount.many', N'kz', N'{count} ескертпе'),
    (N'document.issuesCount.other', N'ru', N'{count} замечания'),
    (N'document.issuesCount.other', N'kz', N'{count} ескертпе')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:uidochead ── кінець секції ──
