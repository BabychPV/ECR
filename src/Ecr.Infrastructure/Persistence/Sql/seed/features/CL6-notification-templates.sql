-- CL6:notification-templates ── ru/kz шаблонів повідомлень (CL-6); власна порція #I18N ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'notificationTemplates.title', N'ru', N'Шаблоны сообщений'),
    (N'notificationTemplates.title', N'kz', N'Хабарлама үлгілері'),
    (N'notificationTemplates.hint', N'ru', N'Что отправляется по каждому событию и кому. Каждый получатель получает сообщение на своём языке; пустой перевод заменяется языком по умолчанию.'),
    (N'notificationTemplates.hint', N'kz', N'Әр оқиға бойынша не және кімге жіберіледі. Әр алушы хабарламаны өз тілінде алады; бос аударманың орнына әдепкі тіл қолданылады.'),
    (N'notificationTemplates.noPermission', N'ru', N'Для изменения шаблонов сообщений нужно право управления строками интерфейса.'),
    (N'notificationTemplates.noPermission', N'kz', N'Хабарлама үлгілерін өзгерту үшін интерфейс жолдарын басқару құқығы қажет.'),
    (N'notificationTemplates.noLanguages', N'ru', N'Не зарегистрировано ни одного языка интерфейса.'),
    (N'notificationTemplates.noLanguages', N'kz', N'Интерфейстің бірде-бір тілі тіркелмеген.'),
    (N'notificationTemplates.notInCatalog', N'ru', N'Шаблона этого события нет в каталоге строк. Повторите развёртывание базы данных, чтобы его добавил сид.'),
    (N'notificationTemplates.notInCatalog', N'kz', N'Бұл оқиғаның үлгісі жолдар каталогында жоқ. Сид оны қосуы үшін дерекқорды қайта орналастырыңыз.'),
    (N'notificationTemplates.event', N'ru', N'Событие'),
    (N'notificationTemplates.event', N'kz', N'Оқиға'),
    (N'notificationTemplates.language', N'ru', N'Язык'),
    (N'notificationTemplates.language', N'kz', N'Тіл'),
    (N'notificationTemplates.subject', N'ru', N'Тема'),
    (N'notificationTemplates.subject', N'kz', N'Тақырып'),
    (N'notificationTemplates.body', N'ru', N'Текст сообщения'),
    (N'notificationTemplates.body', N'kz', N'Хабарлама мәтіні'),
    (N'notificationTemplates.reference', N'ru', N'Язык по умолчанию: {text}'),
    (N'notificationTemplates.reference', N'kz', N'Әдепкі тіл: {text}'),
    (N'notificationTemplates.untranslated', N'ru', N'Перевода на этот язык пока нет: получатели получают текст на языке по умолчанию.'),
    (N'notificationTemplates.untranslated', N'kz', N'Бұл тілге аударма әлі жоқ: алушылар әдепкі тілдегі мәтінді алады.'),
    (N'notificationTemplates.placeholders', N'ru', N'Подстановки, которые заполняет система: {names}.'),
    (N'notificationTemplates.placeholders', N'kz', N'Жүйе толтыратын толтырғыштар: {names}.'),
    (N'notificationTemplates.problemEmpty', N'ru', N'Текст на языке по умолчанию не может быть пустым.'),
    (N'notificationTemplates.problemEmpty', N'kz', N'Әдепкі тілдегі мәтін бос болмауы керек.'),
    (N'notificationTemplates.problemUnknownPlaceholder', N'ru', N'Неизвестные подстановки: {names}. Система заполняет только {allowed}.'),
    (N'notificationTemplates.problemUnknownPlaceholder', N'kz', N'Белгісіз толтырғыштар: {names}. Жүйе тек {allowed} толтырады.'),
    (N'notificationTemplates.save', N'ru', N'Сохранить шаблон'),
    (N'notificationTemplates.save', N'kz', N'Үлгіні сақтау'),
    (N'notificationTemplates.saved', N'ru', N'Шаблон сообщения сохранён'),
    (N'notificationTemplates.saved', N'kz', N'Хабарлама үлгісі сақталды'),
    (N'notificationTemplates.unsaved', N'ru', N'Изменения шаблона не сохранены'),
    (N'notificationTemplates.unsaved', N'kz', N'Үлгідегі өзгерістер сақталмаған'),
    (N'notificationTemplates.recipients', N'ru', N'Кто получит'),
    (N'notificationTemplates.recipients', N'kz', N'Кім алады'),
    (N'notificationTemplates.recipientsNone', N'ru', N'Для этого события не включено ни одно правило: сообщение никто не получит. Включите его в матрице правил выше.'),
    (N'notificationTemplates.recipientsNone', N'kz', N'Бұл оқиға үшін бірде-бір ереже қосылмаған: хабарламаны ешкім алмайды. Оны жоғарыдағы ережелер матрицасында қосыңыз.'),
    (N'notificationTemplates.recipientsRoles', N'ru', N'роли: {roles}'),
    (N'notificationTemplates.recipientsRoles', N'kz', N'рөлдер: {roles}'),
    (N'notificationTemplates.recipientsRoleCount', N'ru', N'ролей-получателей: {count}'),
    (N'notificationTemplates.recipientsRoleCount', N'kz', N'алушы рөлдер: {count}'),
    (N'notificationTemplates.recipientsNoRoles', N'ru', N'нет ролей-получателей'),
    (N'notificationTemplates.recipientsNoRoles', N'kz', N'алушы рөлдер жоқ'),
    (N'notificationTemplates.recipientsFiltered', N'ru', N'порог серьёзности выше Info, это событие не проходит'),
    (N'notificationTemplates.recipientsFiltered', N'kz', N'маңыздылық шегі Info-дан жоғары, бұл оқиға өтпейді'),
    (N'notificationTemplates.recipientsNoTransport', N'ru', N'у канала не настроена доставка'),
    (N'notificationTemplates.recipientsNoTransport', N'kz', N'арнада жеткізу бапталмаған')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- CL6:notification-templates ── кінець секції ──

-- CL6:notification-templates ── Шаблони повідомлень і адресати події (CL-6, NotificationTemplatesPanel, /admin/notifications) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'notificationTemplates.title', N'en', N'Message templates', 1),
    (N'notificationTemplates.hint', N'en', N'What is sent for each event and to whom. Each recipient gets the message in their own language; an empty translation falls back to the default language.', 1),
    (N'notificationTemplates.noPermission', N'en', N'Editing message templates requires the right to manage interface strings.', 1),
    (N'notificationTemplates.noLanguages', N'en', N'No interface languages are registered.', 1),
    (N'notificationTemplates.notInCatalog', N'en', N'The template of this event is not in the string catalog. Re-run the database deployment so that the seed adds it.', 1),
    (N'notificationTemplates.event', N'en', N'Event', 1),
    (N'notificationTemplates.language', N'en', N'Language', 1),
    (N'notificationTemplates.subject', N'en', N'Subject', 1),
    (N'notificationTemplates.body', N'en', N'Message text', 1),
    (N'notificationTemplates.reference', N'en', N'Default language: {text}', 1),
    (N'notificationTemplates.untranslated', N'en', N'There is no translation in this language yet: recipients get the default-language text.', 1),
    (N'notificationTemplates.placeholders', N'en', N'Placeholders filled in by the system: {names}.', 1),
    (N'notificationTemplates.problemEmpty', N'en', N'The default-language text cannot be empty.', 1),
    (N'notificationTemplates.problemUnknownPlaceholder', N'en', N'Unknown placeholders: {names}. The system fills in only {allowed}.', 1),
    (N'notificationTemplates.save', N'en', N'Save template', 1),
    (N'notificationTemplates.saved', N'en', N'Message template saved', 1),
    (N'notificationTemplates.unsaved', N'en', N'Template changes are not saved', 1),
    (N'notificationTemplates.recipients', N'en', N'Who gets it', 1),
    (N'notificationTemplates.recipientsNone', N'en', N'No rule is enabled for this event: nobody gets the message. Enable it in the rules matrix above.', 1),
    (N'notificationTemplates.recipientsRoles', N'en', N'roles: {roles}', 1),
    (N'notificationTemplates.recipientsRoleCount', N'en', N'recipient roles: {count}', 1),
    (N'notificationTemplates.recipientsNoRoles', N'en', N'no recipient roles', 1),
    (N'notificationTemplates.recipientsFiltered', N'en', N'the severity threshold is above Info, so this event does not pass', 1),
    (N'notificationTemplates.recipientsNoTransport', N'en', N'the channel has no delivery transport configured', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- CL6:notification-templates ── кінець секції ──
