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
-- COLL:uidochead ── UI-14/15/16: стан збереження словами, чип аркуша, число зауважень у шапці документа; ru/kz — порцією COLL:uidochead у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'document.saveState.allSaved', N'en', N'All changes saved', 1),
    (N'document.saveState.savedAt', N'en', N'Saved {time}', 1),
    (N'document.saveState.readOnly', N'en', N'Read-only', 1),
    (N'document.saveState.notSaved', N'en', N'not saved', 1),
    (N'document.saveState.show', N'en', N'Show', 1),
    (N'document.saveState.unsaved.one', N'en', N'{count} unsaved change', 1),
    (N'document.saveState.unsaved.few', N'en', N'{count} unsaved changes', 1),
    (N'document.saveState.unsaved.many', N'en', N'{count} unsaved changes', 1),
    (N'document.saveState.unsaved.other', N'en', N'{count} unsaved changes', 1),
    (N'document.sheetStateLabel', N'en', N'Sheet:', 1),
    (N'document.noIssues', N'en', N'No issues', 1),
    (N'document.issuesCount.one', N'en', N'{count} issue', 1),
    (N'document.issuesCount.few', N'en', N'{count} issues', 1),
    (N'document.issuesCount.many', N'en', N'{count} issues', 1),
    (N'document.issuesCount.other', N'en', N'{count} issues', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:uidochead ── кінець секції ──
