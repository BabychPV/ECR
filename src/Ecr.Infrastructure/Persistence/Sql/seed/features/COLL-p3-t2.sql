-- COLL:p3-t2 ── ru/kz пакета P3 тестувального проходу №2; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'publish.warning.argumentUnused', N'ru', N'Формула {formula}: аргумент {name} объявлен в списке, но не используется в выражении — чаще всего это опечатка в имени токена.'),
    (N'publish.warning.argumentUnused', N'kz', N'{formula} формуласы: {name} аргументі тізімде жарияланған, бірақ өрнекте қолданылмайды — көбіне бұл токен атауындағы қате.'),
    (N'notifications.delivery.smtpNotConfigured', N'ru', N'Канал «{channel}»: почтовый транспорт процесса не настроен (Smtp:Host, Smtp:From).'),
    (N'notifications.delivery.smtpNotConfigured', N'kz', N'«{channel}» арнасы: процестің пошта транспорты бапталмаған (Smtp:Host, Smtp:From).'),
    (N'notifications.delivery.smtpNoRecipients', N'ru', N'Канал «{channel}»: адресаты не заданы (измените канал) или у ролей-адресатов нет активных пользователей с почтой.'),
    (N'notifications.delivery.smtpNoRecipients', N'kz', N'«{channel}» арнасы: адресаттар көрсетілмеген (арнаны өзгертіңіз) немесе адресат рөлдерінде поштасы бар белсенді пайдаланушылар жоқ.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:p3-t2 ── кінець секції ──
