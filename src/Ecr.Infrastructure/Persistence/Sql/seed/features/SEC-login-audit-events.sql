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
    (N'security.session.retentionExplain', N'ru', N'Технические журналы старше этого срока могут автоматически удаляться фоновой очисткой; новое значение применяется при следующем проходе очистки, без перезапуска.'),
    (N'security.session.retentionExplain', N'kz', N'Осы мерзімнен ескі техникалық журналдарды фондық тазалау автоматты түрде жоюы мүмкін; жаңа мән тазалаудың келесі өтуінде қайта іске қоспай-ақ қолданылады.'),
    (N'security.session.daysSuffix', N'ru', N'дн.'),
    (N'security.session.daysSuffix', N'kz', N'күн'),
    (N'security.session.auditLabel', N'ru', N'Минимальная глубина аудита'),
    (N'security.session.auditLabel', N'kz', N'Аудиттің ең аз тереңдігі'),
    (N'security.session.auditHint', N'ru', N'От {min} до {max} месяцев, по умолчанию {default}.'),
    (N'security.session.auditHint', N'kz', N'{min} мен {max} ай аралығында, әдепкі {default}.'),
    (N'security.session.auditExplain', N'ru', N'Записи аудита (в том числе успешные входы, неудачные попытки и блокировки) хранятся не меньше этого срока и не удаляются раньше. Меньше 120 месяцев задать нельзя.'),
    (N'security.session.auditExplain', N'kz', N'Аудит жазбалары (сәтті кірулер, сәтсіз әрекеттер мен бұғаттауларды қоса) кемінде осы мерзім сақталады және одан бұрын жойылмайды. 120 айдан аз мән беруге болмайды.'),
    (N'security.session.monthsSuffix', N'ru', N'мес.'),
    (N'security.session.monthsSuffix', N'kz', N'ай'),
    (N'security.session.retentionSection', N'ru', N'Сроки хранения данных'),
    (N'security.session.retentionSection', N'kz', N'Деректерді сақтау мерзімдері'),
    (N'security.session.retentionWarning', N'ru', N'Сокращение срока хранения необратимо удаляет более старые записи при следующей очистке.'),
    (N'security.session.retentionWarning', N'kz', N'Сақтау мерзімін қысқарту келесі тазалау кезінде ескі жазбаларды қайтарымсыз жояды.'),
    (N'security.session.retentionSaved', N'ru', N'Сроки хранения данных сохранены. Новые значения применятся при следующей очистке.'),
    (N'security.session.retentionSaved', N'kz', N'Деректерді сақтау мерзімдері сақталды. Жаңа мәндер келесі тазалау кезінде қолданылады.'),
    (N'audit.eventLabel.LoginFailedSuppressed', N'ru', N'Пропущено неудачных попыток входа (сводка)'),
    (N'audit.eventLabel.LoginFailedSuppressed', N'kz', N'Өткізіп жіберілген сәтсіз кіру әрекеттері (жиынтық)'),
    (N'audit.eventLabel.LoginAuditOverflow', N'ru', N'Журнал входов переполнен: часть попыток не записана'),
    (N'audit.eventLabel.LoginAuditOverflow', N'kz', N'Кіру журналы толып кетті: кейбір әрекеттер жазылмады')
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
    (N'security.session.retentionExplain', N'en', N'Technical logs older than this period may be removed automatically by background cleanup; a new value applies at the next cleanup pass, without a restart.', 1),
    (N'security.session.daysSuffix', N'en', N'days', 1),
    (N'security.session.auditLabel', N'en', N'Minimum audit depth', 1),
    (N'security.session.auditHint', N'en', N'From {min} to {max} months, default {default}.', 1),
    (N'security.session.auditExplain', N'en', N'Audit records (including successful sign-ins, failed attempts and lockouts) are kept for at least this period and are not deleted earlier. Less than 120 months cannot be set.', 1),
    (N'security.session.monthsSuffix', N'en', N'months', 1),
    (N'security.session.retentionSection', N'en', N'Data retention', 1),
    (N'security.session.retentionWarning', N'en', N'Shortening a retention period permanently deletes older records at the next cleanup.', 1),
    (N'security.session.retentionSaved', N'en', N'Data retention saved. The new values apply at the next cleanup.', 1),
    (N'audit.eventLabel.LoginFailedSuppressed', N'en', N'Failed sign-in attempts not recorded (summary)', 1),
    (N'audit.eventLabel.LoginAuditOverflow', N'en', N'Sign-in journal overloaded: some attempts not recorded', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- SEC:login-audit-events ── кінець секції ──
