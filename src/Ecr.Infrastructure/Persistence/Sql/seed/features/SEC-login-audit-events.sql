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
    (N'audit.anonymousActor', N'kz', N'Кіру орындалмаған'),
    (N'err.ECR-REQ-0422.loginRetentionOutOfRange', N'ru', N'Срок хранения попыток входа: от {min} до {max} дней.'),
    (N'err.ECR-REQ-0422.loginRetentionOutOfRange', N'kz', N'Кіру әрекеттерін сақтау мерзімі: {min} мен {max} күн аралығында.'),
    (N'security.session.retentionLabel', N'ru', N'Срок хранения попыток входа'),
    (N'security.session.retentionLabel', N'kz', N'Кіру әрекеттерін сақтау мерзімі'),
    (N'security.session.retentionHint', N'ru', N'От {min} до {max} дней, по умолчанию {default}.'),
    (N'security.session.retentionHint', N'kz', N'{min} мен {max} күн аралығында, әдепкі {default}.'),
    (N'security.session.retentionExplain', N'ru', N'Оперативный журнал попыток входа очищается автоматически по этому сроку; новое значение действует со следующей очистки (не позже чем через час), без перезапуска. Неизменяемый след входов в журнале безопасности (успешные входы, неудачные попытки, блокировки) хранится не менее 120 месяцев независимо от этого значения.'),
    (N'security.session.retentionExplain', N'kz', N'Кіру әрекеттерінің жедел журналы осы мерзім бойынша автоматты түрде тазартылады; жаңа мән келесі тазалаудан бастап (бір сағаттан кешіктірмей) қайта іске қоспай-ақ күшіне енеді. Қауіпсіздік журналындағы кірулердің өзгермейтін ізі (сәтті кірулер, сәтсіз әрекеттер, бұғаттаулар) осы мәннен тәуелсіз кемінде 120 ай сақталады.'),
    (N'security.session.daysSuffix', N'ru', N'дн.'),
    (N'security.session.daysSuffix', N'kz', N'күн'),
    (N'security.session.savedAll', N'ru', N'Настройки сеанса сохранены.'),
    (N'security.session.savedAll', N'kz', N'Сеанс баптаулары сақталды.')
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
    (N'audit.anonymousActor', N'en', N'Not signed in', 1),
    (N'err.ECR-REQ-0422.loginRetentionOutOfRange', N'en', N'Retention of sign-in attempts: from {min} to {max} days.', 1),
    (N'security.session.retentionLabel', N'en', N'Sign-in attempts retention', 1),
    (N'security.session.retentionHint', N'en', N'From {min} to {max} days, default {default}.', 1),
    (N'security.session.retentionExplain', N'en', N'The operational log of sign-in attempts is cleaned up automatically after this period; a new value applies from the next cleanup (within an hour), without a restart. The immutable trail of sign-ins in the security journal (successful sign-ins, failed attempts, lockouts) is kept for at least 120 months regardless of this value.', 1),
    (N'security.session.daysSuffix', N'en', N'days', 1),
    (N'security.session.savedAll', N'en', N'Session settings saved.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- SEC:login-audit-events ── кінець секції ──
