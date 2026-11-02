-- COLL:mockb4b ── ru/kz b4b: сторінки 403/404, «← Back»; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'nav.accessDenied.crumb', N'ru', N'Доступ запрещён'),
    (N'nav.accessDenied.crumb', N'kz', N'Қолжетімділік жоқ'),
    (N'nav.accessDenied.title', N'ru', N'У вас нет доступа к этой странице'),
    (N'nav.accessDenied.title', N'kz', N'Бұл бетке қолжетімділігіңіз жоқ'),
    (N'nav.accessDenied.text', N'ru', N'Ничего не изменено. Попросите системного администратора назначить вам роль с этим правом.'),
    (N'nav.accessDenied.text', N'kz', N'Ештеңе өзгертілмеді. Жүйе әкімшісінен осы құқығы бар рөлді беруін сұраңыз.'),
    (N'nav.accessDenied.copy', N'ru', N'Скопировать данные для запроса'),
    (N'nav.accessDenied.copy', N'kz', N'Сұрау деректерін көшіру'),
    (N'nav.accessDenied.copied', N'ru', N'Данные скопированы — вставьте их в сообщение'),
    (N'nav.accessDenied.copied', N'kz', N'Деректер көшірілді — оларды хабарламаңызға қойыңыз'),
    (N'nav.accessDenied.myAccess', N'ru', N'Мой доступ'),
    (N'nav.accessDenied.myAccess', N'kz', N'Менің қолжетімділігім'),
    (N'nav.backToDocuments', N'ru', N'Назад к документам'),
    (N'nav.backToDocuments', N'kz', N'Құжаттарға оралу'),
    (N'nav.backToTemplates', N'ru', N'Назад к шаблонам'),
    (N'nav.backToTemplates', N'kz', N'Үлгілерге оралу'),
    (N'nav.goToDocuments', N'ru', N'Перейти к документам'),
    (N'nav.goToDocuments', N'kz', N'Құжаттарға өту'),
    (N'nav.notFound.didYouMean', N'ru', N'Возможно, вы искали:'),
    (N'nav.notFound.didYouMean', N'kz', N'Мүмкін, сіз іздеген:'),
    (N'nav.notFound.search', N'ru', N'Поиск экранов и документов'),
    (N'nav.notFound.search', N'kz', N'Экрандар мен құжаттарды іздеу'),
    (N'templates.continueDraft', N'ru', N'Продолжить черновик {version}'),
    (N'templates.continueDraft', N'kz', N'{version} жобасын жалғастыру')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:mockb4b ── кінець секції ──
