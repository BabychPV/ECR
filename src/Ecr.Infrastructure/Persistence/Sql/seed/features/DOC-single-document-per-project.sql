-- B151:single-document ── ru/kz: B151 (рішення людини 16.11) — шаблон «один документ на проєкт» (HSE400, HSE401); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    -- B165(1): власний код ECR-DOC-4092 (доти — ECR-DOC-0409 «подано»; старий ключ — у «Прибрані ключі» прологу).
    (N'err.ECR-DOC-4092', N'ru', N'Документ этого шаблона уже есть'),
    (N'err.ECR-DOC-4092', N'kz', N'Осы үлгінің құжаты бар'),
    (N'err.ECR-DOC-4092.documentExistsForTemplate', N'ru', N'В этом проекте документ этого шаблона уже есть: шаблон допускает один документ на проект. Откройте существующий документ.'),
    (N'err.ECR-DOC-4092.documentExistsForTemplate', N'kz', N'Бұл жобада осы үлгінің құжаты бар: үлгі бір жобаға бір құжатқа рұқсат береді. Бар құжатты ашыңыз.'),
    (N'err.ECR-TMPL-0409.singleDocumentDuplicates', N'ru', N'Нельзя включить «один документ на проект»: в некоторых проектах уже несколько документов этого шаблона. Сначала оставьте по одному.'),
    (N'err.ECR-TMPL-0409.singleDocumentDuplicates', N'kz', N'«Бір жобаға бір құжат» қосылмайды: кейбір жобаларда осы үлгінің бірнеше құжаты бар. Алдымен әрқайсысында біреуін қалдырыңыз.'),
    (N'templates.documentsPerProject', N'ru', N'Документов на проект'),
    (N'templates.documentsPerProject', N'kz', N'Жобадағы құжаттар'),
    (N'templates.documentsPerProjectOne', N'ru', N'Один'),
    (N'templates.documentsPerProjectOne', N'kz', N'Біреу'),
    (N'templates.documentsPerProjectMany', N'ru', N'Несколько'),
    (N'templates.documentsPerProjectMany', N'kz', N'Бірнеше'),
    (N'templates.documentsPerProjectHint', N'ru', N'Сколько документов этого шаблона можно создать в одном проекте (году).'),
    (N'templates.documentsPerProjectHint', N'kz', N'Бір жобада (жылда) осы үлгінің неше құжатын құруға болады.'),
    (N'templates.allowOneDocument', N'ru', N'Разрешить один документ на проект'),
    (N'templates.allowOneDocument', N'kz', N'Жобаға бір құжатқа рұқсат беру'),
    (N'templates.allowManyDocuments', N'ru', N'Разрешить несколько документов на проект'),
    (N'templates.allowManyDocuments', N'kz', N'Жобаға бірнеше құжатқа рұқсат беру'),
    (N'templates.documentPolicySaved', N'ru', N'Правило документов шаблона сохранено'),
    (N'templates.documentPolicySaved', N'kz', N'Үлгі құжаттарының ережесі сақталды')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- B151:single-document ── кінець секції ──
-- B151:single-document ── en; ru/kz — порцією B151:single-document вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-DOC-4092', N'en', N'A document of this template already exists', 1),
    (N'err.ECR-DOC-4092.documentExistsForTemplate', N'en', N'This project already has a document of this template: the template allows one document per project. Open the existing document.', 1),
    (N'err.ECR-TMPL-0409.singleDocumentDuplicates', N'en', N'«One document per project» cannot be turned on: some projects already have several documents of this template. Keep one in each first.', 1),
    (N'templates.documentsPerProject', N'en', N'Documents per project', 1),
    (N'templates.documentsPerProjectOne', N'en', N'One', 1),
    (N'templates.documentsPerProjectMany', N'en', N'Several', 1),
    (N'templates.documentsPerProjectHint', N'en', N'How many documents of this template one project (year) can have.', 1),
    (N'templates.allowOneDocument', N'en', N'Allow one document per project', 1),
    (N'templates.allowManyDocuments', N'en', N'Allow several documents per project', 1),
    (N'templates.documentPolicySaved', N'en', N'Template document rule saved', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- B151:single-document ── кінець секції ──
