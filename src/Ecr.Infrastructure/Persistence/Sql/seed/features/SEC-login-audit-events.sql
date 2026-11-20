-- SEC:login-audit-events ── ru/kz: події входу в журналі безпеки (PSS-24); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'audit.eventLabel.LoginSucceeded', N'ru', N'Успешный вход'),
    (N'audit.eventLabel.LoginSucceeded', N'kz', N'Сәтті кіру'),
    (N'audit.eventLabel.LoginFailed', N'ru', N'Неудачная попытка входа'),
    (N'audit.eventLabel.LoginFailed', N'kz', N'Кіру әрекеті сәтсіз'),
    (N'audit.eventLabel.AccountLocked', N'ru', N'Учётная запись заблокирована'),
    (N'audit.eventLabel.AccountLocked', N'kz', N'Есептік жазба бұғатталды'),
    (N'audit.anonymousActor', N'ru', N'Вход не выполнен'),
    (N'audit.anonymousActor', N'kz', N'Кіру орындалмаған')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- SEC:login-audit-events ── кінець секції ──
-- SEC:login-audit-events ── PSS-24: успішний вхід, невдала спроба й блокування записуються в незмінний журнал безпеки; en ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'audit.eventLabel.LoginSucceeded', N'en', N'Successful sign-in', 1),
    (N'audit.eventLabel.LoginFailed', N'en', N'Failed sign-in attempt', 1),
    (N'audit.eventLabel.AccountLocked', N'en', N'Account locked', 1),
    (N'audit.anonymousActor', N'en', N'Not signed in', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- SEC:login-audit-events ── кінець секції ──
