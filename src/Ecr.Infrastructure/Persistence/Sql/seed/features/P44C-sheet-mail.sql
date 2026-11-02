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
-- P44C:sheet-mail ── en: листи «Аркуш подано» / «Аркуш повернуто» (D-451, `SheetWorkflowNotifier`, `NotificationEventKind.SheetSubmitted/SheetReturned`); підстановки {document} {sheet} {period} {by} {at} {link} (+ {reason}); ru/kz — порцією P44C:sheet-mail нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'notifications.event.SheetSubmitted', N'en', N'Sheet submitted for approval', 1),
    (N'notifications.event.SheetReturned', N'en', N'Sheet returned for edits', 1),
    (N'notifications.sheetSubmitted.subject', N'en', N'ECR: {document}, sheet {sheet}, period {period} submitted for approval', 1),
    (N'notifications.sheetSubmitted.body', N'en', N'{by} submitted sheet {sheet} of document {document} for period {period} on {at}. It is waiting for your approval. Open the sheet: {link}', 1),
    (N'notifications.sheetReturned.subject', N'en', N'ECR: {document}, sheet {sheet}, period {period} returned', 1),
    (N'notifications.sheetReturned.body', N'en', N'{by} returned sheet {sheet} of document {document} for period {period} on {at}. Reason: {reason}. Open the sheet: {link}', 1),
    (N'notificationTemplates.recipientsSheetSubmitted', N'en', N'Sent to the people who can approve the sheet at its current route step and can see it. The person who submitted it gets nothing.', 1),
    (N'notificationTemplates.recipientsSheetReturned', N'en', N'Sent to the document owner and to participants who fill in or submit the sheet, if they can see it. The person who returned it gets nothing.', 1),
    (N'notificationTemplates.recipientsTransportOnly', N'en', N'turns the email on; its own addresses and roles are not used', 1),
    (N'notificationTemplates.recipientsTeamsIgnored', N'en', N'not used: this message goes only by email', 1),
    (N'jobs.kind.outboxFlush', N'en', N'Send sheet notifications', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- P44C:sheet-mail ── кінець секції ──
