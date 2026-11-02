-- COLL:r7y4-digest ── ru/kz: R7-Y4 / Y4-04: тема й рамка зведення збоїв; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'notifications.digest.subject', N'ru', N'ECR: сбоев за период — {count}'),
    (N'notifications.digest.subject', N'kz', N'ECR: кезеңдегі ақаулар — {count}'),
    (N'notifications.digest.coverageLine', N'ru', N'период {period}: событий {count}; {details}'),
    (N'notifications.digest.coverageLine', N'kz', N'кезең {period}: оқиғалар {count}; {details}'),
    (N'notifications.digest.noRecipients', N'ru', N'Алерты никому не отправляются: ни у одного активного пользователя не включено получение алертов и не указана почта.'),
    (N'notifications.digest.noRecipients', N'kz', N'Алерттер ешкімге жіберілмейді: бірде-бір белсенді пайдаланушыда алерт алу қосылмаған және пошта көрсетілмеген.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:r7y4-digest ── кінець секції ──
-- COLL:r7y4-digest ── R7-Y4 / Y4-04: тема й рамка зведення збоїв (NotificationJob) мовою листа; ru/kz — порцією COLL:r7y4-digest у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'notifications.digest.subject', N'en', N'ECR: failures in the period — {count}', 1),
    (N'notifications.digest.coverageLine', N'en', N'period {period}: {count} events; {details}', 1),
    (N'notifications.digest.noRecipients', N'en', N'Alerts are sent to nobody: no active user has alerts turned on and an email address.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:r7y4-digest ── кінець секції ──
