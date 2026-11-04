-- COLL:smtp ── ru/kz категорій відмови проби SMTP і події «період відкрито»; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'notifications.test.smtp.dns', N'ru', N'Проверка SMTP не удалась: имя сервера не разрешилось (DNS). Проверьте сервер (Smtp:Host, если используется конфигурация процесса).'),
    (N'notifications.test.smtp.dns', N'kz', N'SMTP тексеруі сәтсіз: сервер атауы анықталмады (DNS). Серверді тексеріңіз (процесс конфигурациясы қолданылса — Smtp:Host).'),
    (N'notifications.test.smtp.connect', N'ru', N'Проверка SMTP не удалась: не удалось подключиться к серверу. Проверьте сервер, порт (Smtp:Host и Smtp:Port, если используется конфигурация процесса) и межсетевой экран.'),
    (N'notifications.test.smtp.connect', N'kz', N'SMTP тексеруі сәтсіз: серверге қосылу мүмкін болмады. Серверді, портты (процесс конфигурациясы қолданылса — Smtp:Host және Smtp:Port) және брандмауэрді тексеріңіз.'),
    (N'notifications.test.smtp.tls', N'ru', N'Проверка SMTP не удалась: ошибка TLS/STARTTLS. Проверьте сертификат сервера и шифрование (Smtp:UseStartTls, если используется конфигурация процесса).'),
    (N'notifications.test.smtp.tls', N'kz', N'SMTP тексеруі сәтсіз: TLS/STARTTLS қатесі. Сервер сертификатын және шифрлауды тексеріңіз (процесс конфигурациясы қолданылса — Smtp:UseStartTls).'),
    (N'notifications.test.smtp.auth', N'ru', N'Проверка SMTP не удалась: сервер отклонил логин. Проверьте логин и пароль (Smtp:User и секрет, названный в Smtp:SecretName, если используется конфигурация процесса).'),
    (N'notifications.test.smtp.auth', N'kz', N'SMTP тексеруі сәтсіз: сервер логинді қабылдамады. Логин мен құпиясөзді тексеріңіз (процесс конфигурациясы қолданылса — Smtp:User және Smtp:SecretName ішінде аталған құпия).'),
    (N'notifications.test.smtp.relay', N'ru', N'Проверка SMTP не удалась: сервер отказался пересылать почту для отправителя или получателя. Проверьте адрес отправителя (Smtp:From, если используется конфигурация процесса) и адреса получателей.'),
    (N'notifications.test.smtp.relay', N'kz', N'SMTP тексеруі сәтсіз: сервер жіберушіге немесе алушыға пошта жіберуден бас тартты. Жіберуші мекенжайын (процесс конфигурациясы қолданылса — Smtp:From) және алушылар мекенжайларын тексеріңіз.'),
    (N'notifications.test.smtp.timeout', N'ru', N'Проверка SMTP не удалась: сервер не ответил вовремя.'),
    (N'notifications.test.smtp.timeout', N'kz', N'SMTP тексеруі сәтсіз: сервер уақытында жауап бермеді.'),
    (N'notifications.event.PeriodOpened', N'ru', N'Открыт отчётный период'),
    (N'notifications.event.PeriodOpened', N'kz', N'Есепті кезең ашылды'),
    (N'notifications.periodOpened.subject', N'ru', N'ECR: открыт период {period}, проект {project}'),
    (N'notifications.periodOpened.subject', N'kz', N'ECR: {period} кезеңі ашылды, {project} жобасы'),
    (N'notifications.periodOpened.body', N'ru', N'Для проекта {project} открыт новый отчётный период {period}. Заполните документ за этот период.'),
    (N'notifications.periodOpened.body', N'kz', N'{project} жобасы үшін жаңа есепті кезең {period} ашылды. Осы кезеңге арналған құжатты толтырыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:smtp ── кінець секції ──
-- COLL:smtp ── Категорії відмови проби SMTP (`SmtpFailureClassifier`) і подія «період відкрито» (`PeriodStateJob`, `NotificationEventKind.PeriodOpened`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'notifications.test.smtp.dns',     N'en', N'SMTP test failed: the server name could not be resolved (DNS). Check the server (Smtp:Host when the process configuration is in use).', 1),
    (N'notifications.test.smtp.connect', N'en', N'SMTP test failed: could not connect to the server. Check the server, the port (Smtp:Host and Smtp:Port when the process configuration is in use) and the firewall.', 1),
    (N'notifications.test.smtp.tls',     N'en', N'SMTP test failed: TLS/STARTTLS error. Check the server certificate and the encryption (Smtp:UseStartTls when the process configuration is in use).', 1),
    (N'notifications.test.smtp.auth',    N'en', N'SMTP test failed: the server rejected the login. Check the login and the password (Smtp:User and the secret named in Smtp:SecretName when the process configuration is in use).', 1),
    (N'notifications.test.smtp.relay',   N'en', N'SMTP test failed: the server refused to relay for the sender or a recipient. Check the sender address (Smtp:From when the process configuration is in use) and the recipient addresses.', 1),
    (N'notifications.test.smtp.timeout', N'en', N'SMTP test failed: the server did not respond in time.', 1),
    (N'notifications.event.PeriodOpened', N'en', N'Reporting period opened', 1),
    (N'notifications.periodOpened.subject', N'en', N'ECR: period {period} opened, project {project}', 1),
    (N'notifications.periodOpened.body', N'en', N'A new reporting period {period} has opened for project {project}. Please fill in your document for this period.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:smtp ── кінець секції ──
