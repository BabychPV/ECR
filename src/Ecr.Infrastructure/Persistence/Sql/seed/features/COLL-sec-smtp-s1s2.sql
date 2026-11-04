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

-- COLL:sec-smtp-s1s2 ── Збережений пароль SMTP не переноситься на іншу адресу; пароль без шифрування заборонено (ent6 S1, `SaveSmtpSettingsHandler`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REQ-0422.smtpPasswordReentryRequired', N'en', N'The server, port, encryption or login was changed, so the saved password is not carried over: enter the password again.', 1),
    (N'err.ECR-REQ-0422.smtpPasswordNeedsTls', N'en', N'Password authentication needs encryption: choose STARTTLS, or switch authentication off for an internal relay.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:sec-smtp-s1s2 ── кінець секції ──
