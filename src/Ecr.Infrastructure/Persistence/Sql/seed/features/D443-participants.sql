-- D443:participants ── ru/kz: учасники документа (D-443, AN-130); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-ACCS-0403.editAssignmentLevelAboveOwn', N'ru', N'Нельзя выдать {level}: это выше вашего собственного доступа ({ownLevel}) на части этой области. Выберите уровень ниже или область уже.'),
    (N'err.ECR-ACCS-0403.editAssignmentLevelAboveOwn', N'kz', N'{level} беруге болмайды: бұл аймақтың бір бөлігінде өз қолжетімділігіңізден ({ownLevel}) жоғары. Төменірек деңгейді немесе тарырақ аймақты таңдаңыз.'),
    (N'err.ECR-REQ-0422.editAssignmentLevelSliceInvalid', N'ru', N'Месяц и период задаются только для уровня Write (заполнение месяца).'),
    (N'err.ECR-REQ-0422.editAssignmentLevelSliceInvalid', N'kz', N'Ай мен кезең тек Write деңгейі үшін беріледі (айды толтыру).'),
    (N'err.ECR-ACCS-0403.documentOwnerTransferForbidden', N'ru', N'Передать владение может только владелец документа или менеджер проекта.'),
    (N'err.ECR-ACCS-0403.documentOwnerTransferForbidden', N'kz', N'Иелікті тек құжат иесі немесе жоба менеджері бере алады.'),
    (N'err.ECR-REQ-0422.documentOwnerInvalid', N'ru', N'Этого пользователя нельзя назначить владельцем документа. Новому владельцу нужно право записи в проекте.'),
    (N'err.ECR-REQ-0422.documentOwnerInvalid', N'kz', N'Бұл пайдаланушыны құжат иесі етіп тағайындау мүмкін емес. Жаңа иеге жобада жазу құқығы қажет.'),
    (N'editAssign.addParticipant', N'ru', N'Добавить участника'),
    (N'editAssign.addParticipant', N'kz', N'Қатысушы қосу'),
    (N'editAssign.participant.title', N'ru', N'Добавить участника документа'),
    (N'editAssign.participant.title', N'kz', N'Құжат қатысушысын қосу'),
    (N'editAssign.participant.hint', N'ru', N'Участник видит только предоставленную часть документа, даже без роли в проекте. Запрет роли всё равно сильнее. Выдать уровень выше собственного нельзя.'),
    (N'editAssign.participant.hint', N'kz', N'Қатысушы жобада рөлі болмаса да, құжаттың тек берілген бөлігін көреді. Рөлдің тыйымы бәрібір күштірек. Өз деңгейіңізден жоғары деңгей беру мүмкін емес.'),
    (N'editAssign.participant.who', N'ru', N'Кто'),
    (N'editAssign.participant.who', N'kz', N'Кім'),
    (N'editAssign.participant.change', N'ru', N'Изменить'),
    (N'editAssign.participant.change', N'kz', N'Өзгерту'),
    (N'editAssign.participant.level', N'ru', N'Уровень доступа'),
    (N'editAssign.participant.level', N'kz', N'Қолжетімділік деңгейі'),
    (N'editAssign.participant.sheet', N'ru', N'Лист'),
    (N'editAssign.participant.sheet', N'kz', N'Парақ'),
    (N'editAssign.participant.sheetAll', N'ru', N'Весь документ'),
    (N'editAssign.participant.sheetAll', N'kz', N'Бүкіл құжат'),
    (N'editAssign.participant.table', N'ru', N'Таблица'),
    (N'editAssign.participant.table', N'kz', N'Кесте'),
    (N'editAssign.participant.tableAll', N'ru', N'Все таблицы листа'),
    (N'editAssign.participant.tableAll', N'kz', N'Парақтың барлық кестелері'),
    (N'editAssign.participant.grant', N'ru', N'Предоставить доступ'),
    (N'editAssign.participant.grant', N'kz', N'Қолжетімділік беру'),
    (N'editAssign.level.Read', N'ru', N'Просмотр'),
    (N'editAssign.level.Read', N'kz', N'Қарау'),
    (N'editAssign.level.Write', N'ru', N'Заполнение'),
    (N'editAssign.level.Write', N'kz', N'Толтыру'),
    (N'editAssign.level.Submit', N'ru', N'Заполнение и подача'),
    (N'editAssign.level.Submit', N'kz', N'Толтыру және жіберу'),
    (N'editAssign.level.Approve', N'ru', N'Заполнение, подача и согласование'),
    (N'editAssign.level.Approve', N'kz', N'Толтыру, жіберу және келісу'),
    (N'editAssign.levelLabel', N'ru', N'Доступ: {level}'),
    (N'editAssign.levelLabel', N'kz', N'Қолжетімділік: {level}'),
    (N'editAssign.owner.transfer', N'ru', N'Передать владение'),
    (N'editAssign.owner.transfer', N'kz', N'Иелікті беру'),
    (N'editAssign.owner.title', N'ru', N'Передать владение документом'),
    (N'editAssign.owner.title', N'kz', N'Құжат иелігін беру'),
    (N'editAssign.owner.text', N'ru', N'Новый владелец управляет участниками этого документа. Ему нужно право записи в проекте.'),
    (N'editAssign.owner.text', N'kz', N'Жаңа иесі осы құжаттың қатысушыларын басқарады. Оған жобада жазу құқығы қажет.'),
    (N'editAssign.owner.confirm', N'ru', N'Передать'),
    (N'editAssign.owner.confirm', N'kz', N'Беру'),
    (N'editAssign.done.granted', N'ru', N'Доступ предоставлен: {name}.'),
    (N'editAssign.done.granted', N'kz', N'Қолжетімділік берілді: {name}.'),
    (N'editAssign.done.ownerTransferred', N'ru', N'Владение передано: {name}.'),
    (N'editAssign.done.ownerTransferred', N'kz', N'Иелік берілді: {name}.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- D443:participants ── кінець секції ──
-- D443:participants ── en: учасники документа — рівень/область, стеля «не вище власного», передача власника (D-443, AN-130); ru/kz — порцією D443:participants нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-ACCS-0403.editAssignmentLevelAboveOwn', N'en', N'You cannot grant {level}: it is higher than your own access ({ownLevel}) on part of this scope. Choose a lower level or a narrower scope.', 1),
    (N'err.ECR-REQ-0422.editAssignmentLevelSliceInvalid', N'en', N'A month and a period can be set only for the Write level (filling a month).', 1),
    (N'err.ECR-ACCS-0403.documentOwnerTransferForbidden', N'en', N'Only the document owner or a project manager can transfer ownership.', 1),
    (N'err.ECR-REQ-0422.documentOwnerInvalid', N'en', N'This user cannot become the document owner. The new owner needs write access to the project.', 1),
    (N'editAssign.addParticipant', N'en', N'Add participant', 1),
    (N'editAssign.participant.title', N'en', N'Add a document participant', 1),
    (N'editAssign.participant.hint', N'en', N'A participant sees only the granted part of the document, even without a role on the project. A role denial still wins. You cannot grant a level higher than your own.', 1),
    (N'editAssign.participant.who', N'en', N'Who', 1),
    (N'editAssign.participant.change', N'en', N'Change', 1),
    (N'editAssign.participant.level', N'en', N'Access level', 1),
    (N'editAssign.participant.sheet', N'en', N'Sheet', 1),
    (N'editAssign.participant.sheetAll', N'en', N'Whole document', 1),
    (N'editAssign.participant.table', N'en', N'Table', 1),
    (N'editAssign.participant.tableAll', N'en', N'All tables of the sheet', 1),
    (N'editAssign.participant.grant', N'en', N'Grant access', 1),
    (N'editAssign.level.Read', N'en', N'View', 1),
    (N'editAssign.level.Write', N'en', N'Fill in', 1),
    (N'editAssign.level.Submit', N'en', N'Fill in and submit', 1),
    (N'editAssign.level.Approve', N'en', N'Fill in, submit and approve', 1),
    (N'editAssign.levelLabel', N'en', N'Access: {level}', 1),
    (N'editAssign.owner.transfer', N'en', N'Transfer ownership', 1),
    (N'editAssign.owner.title', N'en', N'Transfer document ownership', 1),
    (N'editAssign.owner.text', N'en', N'The new owner manages the participants of this document. They need write access to the project.', 1),
    (N'editAssign.owner.confirm', N'en', N'Transfer', 1),
    (N'editAssign.done.granted', N'en', N'Access granted: {name}.', 1),
    (N'editAssign.done.ownerTransferred', N'en', N'Ownership transferred to {name}.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- D443:participants ── кінець секції ──
