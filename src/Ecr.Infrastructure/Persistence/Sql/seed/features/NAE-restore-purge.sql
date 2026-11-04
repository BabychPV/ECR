-- NAE:restore-purge ── ECR-REG-0409 ── ru/kz: повернення закритого вузла і остаточне видалення невживаного (людина 04.11); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REG-0409.parentRetired', N'ru', N'Узел находится под закрытыми предками ({count}: {ancestorCodes}). Сначала восстановите их или восстановите вместе с ними.'),
    (N'err.ECR-REG-0409.parentRetired', N'kz', N'Түйін жабылған ата-түйіндердің астында ({count}: {ancestorCodes}). Алдымен оларды қалпына келтіріңіз немесе олармен бірге қалпына келтіріңіз.'),
    (N'err.ECR-REG-0409.purgeActive', N'ru', N'Запись «{code}» действует: сначала закройте её, затем удалите окончательно.'),
    (N'err.ECR-REG-0409.purgeActive', N'kz', N'«{code}» жазбасы әрекет етеді: алдымен оны жабыңыз, содан кейін біржола жойыңыз.'),
    (N'err.ECR-REG-0409.entryUsed', N'ru', N'Запись «{code}» нельзя удалить окончательно: она используется ({referenceCount}) — в документах, поданных срезах, архиве, других записях или настройках.'),
    (N'err.ECR-REG-0409.entryUsed', N'kz', N'«{code}» жазбасын біржола жоюға болмайды: ол қолданылады ({referenceCount}) — құжаттарда, берілген кесінділерде, мұрағатта, басқа жазбаларда немесе баптауларда.'),
    (N'registryAudit.entryRestored', N'ru', N'Восстановлено записей: {count}.'),
    (N'registryAudit.entryRestored', N'kz', N'Қалпына келтірілген жазбалар: {count}.'),
    (N'registryAudit.entryPurged', N'ru', N'Удалено окончательно записей: {count}.'),
    (N'registryAudit.entryPurged', N'kz', N'Біржола жойылған жазбалар: {count}.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- NAE:restore-purge ── кінець секції ──
-- NAE:restore-purge ── ECR-REG-0409 ── en: повернення вузла і остаточне видалення; ru/kz — порція вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REG-0409.parentRetired', N'en', N'The node sits under retired ancestors ({count}: {ancestorCodes}). Restore them first, or restore together with them.', 1),
    (N'err.ECR-REG-0409.purgeActive', N'en', N'Entry «{code}» is active: retire it first, then delete it for good.', 1),
    (N'err.ECR-REG-0409.entryUsed', N'en', N'Entry «{code}» cannot be deleted for good: it is used ({referenceCount}) — in documents, submitted snapshots, the archive, other entries or settings.', 1),
    (N'registryAudit.entryRestored', N'en', N'Entries restored: {count}.', 1),
    (N'registryAudit.entryPurged', N'en', N'Entries deleted for good: {count}.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- NAE:restore-purge ── кінець секції ──
