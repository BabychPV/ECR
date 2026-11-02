-- COLL:sec-s4 ── ru/kz напряму пошти (рев'ю ent6 S4); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0422.smtpPortNotAllowed', N'ru', N'Этот порт SMTP не разрешён. Используйте 25, 465, 587 или 2525 либо попросите администратора добавить порт в ключ конфигурации Smtp:AllowedPorts.'),
    (N'err.ECR-REQ-0422.smtpPortNotAllowed', N'kz', N'Бұл SMTP порты рұқсат етілмеген. 25, 465, 587 немесе 2525 пайдаланыңыз не әкімшіден портты Smtp:AllowedPorts конфигурация кілтіне қосуын сұраңыз.'),
    (N'err.ECR-REQ-0422.smtpHostForbidden', N'ru', N'Этот SMTP-сервер не разрешён. Проверьте, что имя сервера разрешается в DNS; адреса loopback, link-local и метаданных облака (в том числе имена, которые на них разрешаются) запрещены.'),
    (N'err.ECR-REQ-0422.smtpHostForbidden', N'kz', N'Бұл SMTP сервері рұқсат етілмеген. Сервер атауының DNS-те шешілетінін тексеріңіз; loopback, link-local және бұлт метадеректерінің мекенжайлары (оларға шешілетін атаулар да) тыйым салынған.'),
    (N'notifications.test.smtp.probeFailed', N'ru', N'Проверка SMTP не удалась: сервер недоступен или не ответил. Проверьте имя сервера, порт и межсетевой экран.'),
    (N'notifications.test.smtp.probeFailed', N'kz', N'SMTP тексеруі сәтсіз: сервер қолжетімсіз немесе жауап бермеді. Сервер атауын, портты және брандмауэрді тексеріңіз.'),
    (N'notifications.test.smtp.endpointForbidden', N'ru', N'Проверка SMTP отклонена: порт или сервер не разрешён. Разрешены порты 25, 465, 587, 2525 и из Smtp:AllowedPorts; серверы loopback, link-local и метаданных облака запрещены.'),
    (N'notifications.test.smtp.endpointForbidden', N'kz', N'SMTP тексеруі қабылданбады: порт немесе сервер рұқсат етілмеген. 25, 465, 587, 2525 және Smtp:AllowedPorts ішіндегі порттар рұқсат; loopback, link-local және бұлт метадеректері серверлеріне тыйым салынған.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:sec-s4 ── кінець секції ──
