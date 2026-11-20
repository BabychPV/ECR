-- SEC:login-audit-events ── ru/kz: події входу в журналі безпеки і строки зберігання (PSS-24); власна порція ──
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
    (N'err.ECR-REQ-0422.technicalLogRetentionOutOfRange', N'ru', N'Срок хранения технических журналов: от {min} до {max} дней.'),
    (N'err.ECR-REQ-0422.technicalLogRetentionOutOfRange', N'kz', N'Техникалық журналдарды сақтау мерзімі: {min} мен {max} күн аралығында.'),
    (N'err.ECR-REQ-0422.auditRetentionOutOfRange', N'ru', N'Глубина аудита: от {min} до {max} месяцев.'),
    (N'err.ECR-REQ-0422.auditRetentionOutOfRange', N'kz', N'Аудиттің тереңдігі: {min} мен {max} ай аралығында.'),
    (N'security.session.retentionLabel', N'ru', N'Срок хранения технических журналов'),
    (N'security.session.retentionLabel', N'kz', N'Техникалық журналдарды сақтау мерзімі'),
    (N'security.session.retentionHint', N'ru', N'От {min} до {max} дней, по умолчанию {default}.'),
    (N'security.session.retentionHint', N'kz', N'{min} мен {max} күн аралығында, әдепкі {default}.'),
    (N'security.session.retentionExplain', N'ru', N'Технические журналы (попытки входа, очередь и доставки уведомлений, прогоны обслуживания) очищаются автоматически по этому сроку; новое значение действует со следующей очистки (не позже чем через час), без перезапуска.'),
    (N'security.session.retentionExplain', N'kz', N'Техникалық журналдар (кіру әрекеттері, хабарландыру кезегі мен жеткізулер, қызмет көрсету іске қосулары) осы мерзім бойынша автоматты түрде тазартылады; жаңа мән келесі тазалаудан бастап (бір сағаттан кешіктірмей) қайта іске қоспай-ақ күшіне енеді.'),
    (N'security.session.daysSuffix', N'ru', N'дн.'),
    (N'security.session.daysSuffix', N'kz', N'күн'),
    (N'security.session.auditLabel', N'ru', N'Минимальная глубина аудита'),
    (N'security.session.auditLabel', N'kz', N'Аудиттің ең аз тереңдігі'),
    (N'security.session.auditHint', N'ru', N'От {min} до {max} месяцев, по умолчанию {default}.'),
    (N'security.session.auditHint', N'kz', N'{min} мен {max} ай аралығында, әдепкі {default}.'),
    (N'security.session.auditExplain', N'ru', N'Неизменяемый журнал аудита (в том числе успешные входы, неудачные попытки и блокировки) не архивируется и не удаляется раньше этого срока. Меньше 120 месяцев задать нельзя.'),
    (N'security.session.auditExplain', N'kz', N'Өзгермейтін аудит журналы (сәтті кірулер, сәтсіз әрекеттер мен бұғаттауларды қоса) осы мерзімнен бұрын мұрағатталмайды және жойылмайды. 120 айдан аз мән беруге болмайды.'),
    (N'security.session.monthsSuffix', N'ru', N'мес.'),
    (N'security.session.monthsSuffix', N'kz', N'ай'),
    (N'security.session.savedAll', N'ru', N'Настройки сеанса сохранены.'),
    (N'security.session.savedAll', N'kz', N'Сеанс баптаулары сақталды.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- SEC:login-audit-events ── кінець секції ──
-- SEC:login-audit-events ── PSS-24: успішний вхід, невдала спроба й блокування в незмінному журналі безпеки; строки зберігання; en ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'audit.eventLabel.LoginSucceeded', N'en', N'Successful sign-in', 1),
    (N'audit.eventLabel.LoginFailed', N'en', N'Failed sign-in attempt', 1),
    (N'audit.eventLabel.AccountLocked', N'en', N'Account locked', 1),
    (N'audit.anonymousActor', N'en', N'Not signed in', 1),
    (N'err.ECR-REQ-0422.technicalLogRetentionOutOfRange', N'en', N'Technical logs retention: from {min} to {max} days.', 1),
    (N'err.ECR-REQ-0422.auditRetentionOutOfRange', N'en', N'Audit depth: from {min} to {max} months.', 1),
    (N'security.session.retentionLabel', N'en', N'Technical logs retention', 1),
    (N'security.session.retentionHint', N'en', N'From {min} to {max} days, default {default}.', 1),
    (N'security.session.retentionExplain', N'en', N'Technical logs (sign-in attempts, the notification queue and deliveries, maintenance runs) are cleaned up automatically after this period; a new value applies from the next cleanup (within an hour), without a restart.', 1),
    (N'security.session.daysSuffix', N'en', N'days', 1),
    (N'security.session.auditLabel', N'en', N'Minimum audit depth', 1),
    (N'security.session.auditHint', N'en', N'From {min} to {max} months, default {default}.', 1),
    (N'security.session.auditExplain', N'en', N'The immutable audit journal (including successful sign-ins, failed attempts and lockouts) is not archived or deleted before this period. Less than 120 months cannot be set.', 1),
    (N'security.session.monthsSuffix', N'en', N'months', 1),
    (N'security.session.savedAll', N'en', N'Session settings saved.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- SEC:login-audit-events ── кінець секції ──
