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
