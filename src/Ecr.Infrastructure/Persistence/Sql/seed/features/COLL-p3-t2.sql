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
-- COLL:p3-t2 ── тестувальний прохід №2, пакет P3 (T2-04…); ru/kz — порцією COLL:p3-t2 у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'publish.warning.argumentUnused', N'en', N'Formula {formula}: argument {name} is declared in the list but not used in the expression - most often a typo in the token name.', 1),
    (N'notifications.delivery.smtpNotConfigured', N'en', N'Channel "{channel}": the SMTP transport of the process is not configured (Smtp:Host, Smtp:From).', 1),
    (N'notifications.delivery.smtpNoRecipients', N'en', N'Channel "{channel}": no recipients are set (edit the channel) or the recipient roles have no active users with an email address.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:p3-t2 ── кінець секції ──
