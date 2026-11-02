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
