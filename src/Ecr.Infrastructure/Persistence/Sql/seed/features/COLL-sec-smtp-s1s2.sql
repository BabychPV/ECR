-- COLL:sec-smtp-s1s2 ── ru/kz відмов S1 налаштувань SMTP; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0422.smtpPasswordReentryRequired', N'ru', N'Сервер, порт, шифрование или логин изменены, поэтому сохранённый пароль не переносится: введите пароль заново.'),
    (N'err.ECR-REQ-0422.smtpPasswordReentryRequired', N'kz', N'Сервер, порт, шифрлау немесе логин өзгертілді, сондықтан сақталған пароль көшірілмейді: парольді қайта енгізіңіз.'),
    (N'err.ECR-REQ-0422.smtpPasswordNeedsTls', N'ru', N'Аутентификация по паролю требует шифрования: выберите STARTTLS или отключите аутентификацию для внутреннего релея.'),
    (N'err.ECR-REQ-0422.smtpPasswordNeedsTls', N'kz', N'Пароль арқылы аутентификация шифрлауды қажет етеді: STARTTLS таңдаңыз немесе ішкі релей үшін аутентификацияны өшіріңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:sec-smtp-s1s2 ── кінець секції ──

