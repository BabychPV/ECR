-- COLL:an36 ── ru/kz AN-36 (L6-02, L6-06); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-DOC-4091.structureChanged', N'ru', N'Документ перевели на другую версию шаблона, пока сохранялись ваши изменения. Ничего не сохранено; обновите страницу и повторите.'),
    (N'err.ECR-DOC-4091.structureChanging', N'ru', N'Документ сейчас переводится на другую версию шаблона. Ваши изменения не сохранены; повторите попытку через мгновение.'),
    (N'err.ECR-DOC-4091.documentBeingEdited', N'ru', N'Документ сейчас редактируется. Версия шаблона не изменена; повторите попытку через мгновение.'),
    (N'err.ECR-DOC-4091.headerBeingEdited', N'ru', N'Шапка документа сейчас сохраняется. Лист не подан; повторите попытку через мгновение.'),
    (N'err.ECR-DOC-4091.structureChanged', N'kz', N'Өзгерістеріңіз сақталып жатқанда құжат үлгінің басқа нұсқасына ауыстырылды. Ештеңе сақталмады; бетті жаңартып, қайталап көріңіз.'),
    (N'err.ECR-DOC-4091.structureChanging', N'kz', N'Құжат дәл қазір үлгінің басқа нұсқасына ауыстырылуда. Өзгерістеріңіз сақталмады; сәлден кейін қайталап көріңіз.'),
    (N'err.ECR-DOC-4091.documentBeingEdited', N'kz', N'Құжат дәл қазір өңделуде. Үлгі нұсқасы өзгертілмеді; сәлден кейін қайталап көріңіз.'),
    (N'err.ECR-DOC-4091.headerBeingEdited', N'kz', N'Құжаттың тақырыптамасы дәл қазір сақталуда. Парақ тапсырылмады; сәлден кейін қайталап көріңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an36 ── кінець секції ──
-- COLL:an36 ── AN-36 (L6-02, L6-06): блокування структури й шапки документа; ru/kz — порцією COLL:an36 у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-DOC-4091.structureChanged',      N'en', N'The document was moved to another template version while your changes were being saved. Nothing was saved; reload the page and try again.', 1),
    (N'err.ECR-DOC-4091.structureChanging',     N'en', N'The document is being moved to another template version right now. Your changes were not saved; try again in a moment.', 1),
    (N'err.ECR-DOC-4091.documentBeingEdited',   N'en', N'The document is being edited right now. The template version was not changed; try again in a moment.', 1),
    (N'err.ECR-DOC-4091.headerBeingEdited',     N'en', N'The document header is being saved right now. The sheet was not submitted; try again in a moment.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an36 ── кінець секції ──
