-- P44C:sheet-mail ── ru/kz: листи «Аркуш подано» / «Аркуш повернуто» (D-451); власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'notifications.event.SheetSubmitted', N'ru', N'Лист подан на согласование'),
    (N'notifications.event.SheetSubmitted', N'kz', N'Парақ келісуге жіберілді'),
    (N'notifications.event.SheetReturned', N'ru', N'Лист возвращён на доработку'),
    (N'notifications.event.SheetReturned', N'kz', N'Парақ пысықтауға қайтарылды'),
    (N'notifications.sheetSubmitted.subject', N'ru', N'ECR: {document}, лист {sheet}, период {period} подан на согласование'),
    (N'notifications.sheetSubmitted.subject', N'kz', N'ECR: {document}, {sheet} парағы, {period} кезеңі келісуге жіберілді'),
    (N'notifications.sheetSubmitted.body', N'ru', N'{by} подал(а) лист {sheet} документа {document} за период {period} ({at}). Лист ожидает вашего согласования. Открыть лист: {link}'),
    (N'notifications.sheetSubmitted.body', N'kz', N'{by} {document} құжатының {sheet} парағын {period} кезеңі үшін жіберді ({at}). Парақ сіздің келісуіңізді күтуде. Парақты ашу: {link}'),
    (N'notifications.sheetReturned.subject', N'ru', N'ECR: {document}, лист {sheet}, период {period} возвращён'),
    (N'notifications.sheetReturned.subject', N'kz', N'ECR: {document}, {sheet} парағы, {period} кезеңі қайтарылды'),
    (N'notifications.sheetReturned.body', N'ru', N'{by} вернул(а) лист {sheet} документа {document} за период {period} ({at}). Причина: {reason}. Открыть лист: {link}'),
    (N'notifications.sheetReturned.body', N'kz', N'{by} {document} құжатының {sheet} парағын {period} кезеңі үшін қайтарды ({at}). Себебі: {reason}. Парақты ашу: {link}'),
    (N'notificationTemplates.recipientsSheetSubmitted', N'ru', N'Получают те, кто может согласовать лист на текущем шаге маршрута и видит его. Подавший лист письма не получает.'),
    (N'notificationTemplates.recipientsSheetSubmitted', N'kz', N'Парақты маршруттың ағымдағы қадамында келісе алатын және оны көретіндер алады. Парақты жіберген адам хат алмайды.'),
    (N'notificationTemplates.recipientsSheetReturned', N'ru', N'Получают владелец документа и участники, которые заполняют или подают лист, если видят его. Вернувший лист письма не получает.'),
    (N'notificationTemplates.recipientsSheetReturned', N'kz', N'Құжат иесі және парақты толтыратын немесе жіберетін қатысушылар, егер оны көрсе, алады. Парақты қайтарған адам хат алмайды.'),
    (N'notificationTemplates.recipientsTransportOnly', N'ru', N'включает почту; его собственные адреса и роли не используются'),
    (N'notificationTemplates.recipientsTransportOnly', N'kz', N'поштаны қосады; оның өз мекенжайлары мен рөлдері қолданылмайды'),
    (N'notificationTemplates.recipientsTeamsIgnored', N'ru', N'не используется: это письмо идёт только по почте'),
    (N'notificationTemplates.recipientsTeamsIgnored', N'kz', N'қолданылмайды: бұл хат тек поштамен жіберіледі'),
    (N'jobs.kind.outboxFlush', N'ru', N'Отправка уведомлений по листам'),
    (N'jobs.kind.outboxFlush', N'kz', N'Парақтар бойынша хабарламаларды жіберу')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- P44C:sheet-mail ── кінець секції ──
