-- COLL:smtp-probe ── ru/kz проби SMTP-каналу без адресатів (D-263); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'notifications.test.smtpNoRecipients', N'ru', N'Нет адресатов: в канале нет явных адресов, а среди пользователей его ролей нет ни одного активного с адресом электронной почты.'),
    (N'notifications.test.smtpNoRecipients', N'kz', N'Алушылар жоқ: арнада нақты мекенжайлар жоқ, ал оның рөлдерінде электрондық пошта мекенжайы бар бірде-бір белсенді пайдаланушы жоқ.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:smtp-probe ── кінець секції ──

-- COLL:smtp-probe ── Проба SMTP: адресат і канал за ролями (D-263) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'notifications.test.smtpNoRecipients', N'en', N'No recipients: the channel has no explicit addresses, and its roles resolve to no active user with an email address.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:smtp-probe ── кінець секції ──
