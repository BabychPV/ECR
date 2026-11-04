-- P44B:owner-filter ── ru/kz: колонка «Власник» і фільтр переліку за людиною (D-450); власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'documents.owner', N'ru', N'Владелец'),
    (N'documents.owner', N'kz', N'Иесі'),
    (N'documents.ownerOf', N'ru', N'Владелец: {name}'),
    (N'documents.ownerOf', N'kz', N'Иесі: {name}'),
    (N'documents.ownerUnknown', N'ru', N'Владелец не показан'),
    (N'documents.ownerUnknown', N'kz', N'Иесі көрсетілмейді'),
    (N'documents.ownerSearch', N'ru', N'Найти человека…'),
    (N'documents.ownerSearch', N'kz', N'Адамды табу…'),
    (N'documents.ownerSelected', N'ru', N'Выбранный человек'),
    (N'documents.ownerSelected', N'kz', N'Таңдалған адам'),
    (N'documents.ownerCount', N'ru', N'{count} док.'),
    (N'documents.ownerCount', N'kz', N'{count} құж.'),
    (N'documents.ownerNothingFound', N'ru', N'Среди доступных вам документов никого не найдено'),
    (N'documents.ownerNothingFound', N'kz', N'Сізге қолжетімді құжаттардан ешкім табылмады'),
    (N'documents.ownerLoading', N'ru', N'Загружаем людей…'),
    (N'documents.ownerLoading', N'kz', N'Адамдар жүктелуде…'),
    (N'documents.ownerLoadFailed', N'ru', N'Не удалось загрузить список людей'),
    (N'documents.ownerLoadFailed', N'kz', N'Адамдар тізімін жүктеу мүмкін болмады'),
    (N'documents.ownerPeople', N'ru', N'Люди'),
    (N'documents.ownerPeople', N'kz', N'Адамдар'),
    (N'err.ECR-REQ-0422.ownerUserId', N'ru', N'Для фильтра по владельцу нужен существующий человек.'),
    (N'err.ECR-REQ-0422.ownerUserId', N'kz', N'Иесі бойынша сүзгіге нақты адам қажет.'),
    (N'err.ECR-REQ-0422.ownerWithMine', N'ru', N'Выберите либо «Мои», либо конкретного человека, но не оба сразу.'),
    (N'err.ECR-REQ-0422.ownerWithMine', N'kz', N'«Менікі» немесе нақты адамды таңдаңыз, екеуін бірге емес.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- P44B:owner-filter ── кінець секції ──
-- P44B:owner-filter ── en: колонка «Власник» і фільтр переліку за конкретною людиною (D-450); ru/kz — порцією P44B:owner-filter нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'documents.owner', N'en', N'Owner', 1),
    (N'documents.ownerOf', N'en', N'Owner: {name}', 1),
    (N'documents.ownerUnknown', N'en', N'Owner not shown', 1),
    (N'documents.ownerSearch', N'en', N'Find a person…', 1),
    (N'documents.ownerSelected', N'en', N'Selected person', 1),
    (N'documents.ownerCount', N'en', N'{count} doc.', 1),
    (N'documents.ownerNothingFound', N'en', N'No one found among the documents you can see', 1),
    (N'documents.ownerLoading', N'en', N'Loading people…', 1),
    (N'documents.ownerLoadFailed', N'en', N'Could not load the list of people', 1),
    (N'documents.ownerPeople', N'en', N'People', 1),
    (N'err.ECR-REQ-0422.ownerUserId', N'en', N'The owner filter needs a valid person.', 1),
    (N'err.ECR-REQ-0422.ownerWithMine', N'en', N'Choose either «Mine» or a specific person, not both.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- P44B:owner-filter ── кінець секції ──
