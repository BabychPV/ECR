-- SHEETCOPY:sheet-copies ── ECR-DOC-0409/0422/0404 ── ru/kz: копії аркушів документа (людина 03.11); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-DOC-0409.sheetCopyIsOriginal', N'ru', N'Оригинал аркуша удалить нельзя: удаляется только копия.'),
    (N'err.ECR-DOC-0409.sheetCopyIsOriginal', N'kz', N'Парақтың түпнұсқасын жоюға болмайды: тек көшірмесі жойылады.'),
    (N'err.ECR-DOC-0409.sheetCopyHasWorkflow', N'ru', N'Копию нельзя удалить: за период {periodKey} она в состоянии {workflowStatus}. Сначала верните её в черновик.'),
    (N'err.ECR-DOC-0409.sheetCopyHasWorkflow', N'kz', N'Көшірмені жою мүмкін емес: {periodKey} кезеңінде ол {workflowStatus} күйінде. Алдымен оны жобаға қайтарыңыз.'),
    (N'err.ECR-DOC-0409.sheetCopyProjectArchived', N'ru', N'Проект в архиве: состав документа не меняется.'),
    (N'err.ECR-DOC-0409.sheetCopyProjectArchived', N'kz', N'Жоба мұрағатта: құжат құрамы өзгермейді.'),
    (N'err.ECR-DOC-0422.sheetNotCopyable', N'ru', N'Этот аркуш в шаблоне не помечен как копируемый.'),
    (N'err.ECR-DOC-0422.sheetNotCopyable', N'kz', N'Бұл парақ үлгіде көшіруге болатын деп белгіленбеген.'),
    (N'err.ECR-DOC-0422.sheetNameInvalid', N'ru', N'Название группы аркуша — от 1 до {maxLength} символов хотя бы на одном языке.'),
    (N'err.ECR-DOC-0422.sheetNameInvalid', N'kz', N'Парақ тобының атауы — кемінде бір тілде 1-ден {maxLength} таңбаға дейін.'),
    (N'err.ECR-DOC-0404.sheetCopy', N'ru', N'Аркуш или его копия не найдены в составе документа.'),
    (N'err.ECR-DOC-0404.sheetCopy', N'kz', N'Парақ немесе оның көшірмесі құжат құрамында табылмады.'),
    (N'audit.eventLabel.DocumentSheetCopyAdded', N'ru', N'Добавлена копия аркуша'),
    (N'audit.eventLabel.DocumentSheetCopyAdded', N'kz', N'Парақ көшірмесі қосылды'),
    (N'audit.eventLabel.DocumentSheetGroupRenamed', N'ru', N'Переименована группа аркуша'),
    (N'audit.eventLabel.DocumentSheetGroupRenamed', N'kz', N'Парақ тобының атауы өзгертілді'),
    (N'audit.eventLabel.DocumentSheetCopyDeleted', N'ru', N'Удалена копия аркуша'),
    (N'audit.eventLabel.DocumentSheetCopyDeleted', N'kz', N'Парақ көшірмесі жойылды')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- SHEETCOPY:sheet-copies ── кінець секції ──
-- SHEETCOPY:sheet-copies ── ECR-DOC-0409/0422/0404 ── en: копії аркушів документа; ru/kz — порція вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-DOC-0409.sheetCopyIsOriginal', N'en', N'The original sheet cannot be deleted: only a copy can.', 1),
    (N'err.ECR-DOC-0409.sheetCopyHasWorkflow', N'en', N'The copy cannot be deleted: in period {periodKey} it is {workflowStatus}. Return it to draft first.', 1),
    (N'err.ECR-DOC-0409.sheetCopyProjectArchived', N'en', N'The project is archived: the document composition cannot change.', 1),
    (N'err.ECR-DOC-0422.sheetNotCopyable', N'en', N'This sheet is not marked as copyable in the template.', 1),
    (N'err.ECR-DOC-0422.sheetNameInvalid', N'en', N'The sheet group name must be 1 to {maxLength} characters in at least one language.', 1),
    (N'err.ECR-DOC-0404.sheetCopy', N'en', N'The sheet or its copy was not found in the document.', 1),
    (N'audit.eventLabel.DocumentSheetCopyAdded', N'en', N'Sheet copy added', 1),
    (N'audit.eventLabel.DocumentSheetGroupRenamed', N'en', N'Sheet group renamed', 1),
    (N'audit.eventLabel.DocumentSheetCopyDeleted', N'en', N'Sheet copy deleted', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- SHEETCOPY:sheet-copies ── кінець секції ──
