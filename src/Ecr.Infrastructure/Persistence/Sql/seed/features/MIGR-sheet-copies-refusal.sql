-- MIGR:sheet-copies-refusal ── ru/kz: D-6 (аудит 2026-11), перенос версії відмовляє, коли аркуш без відповідника має копії; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-SCHM-0422.migrateSheetCopiesNotMapped', N'ru', N'В новой версии нет листа, у которого в документах проекта есть копии. Сначала удалите эти копии листа, затем переносите проект.'),
    (N'err.ECR-SCHM-0422.migrateSheetCopiesNotMapped', N'kz', N'Жаңа нұсқада жоба құжаттарында көшірмелері бар парақ жоқ. Алдымен парақтың осы көшірмелерін жойыңыз, содан кейін жобаны көшіріңіз.'),
    (N'documents.migrateRefusalSheetCopiesNotMapped', N'ru', N'У удаляемого листа есть копии в документах'),
    (N'documents.migrateRefusalSheetCopiesNotMapped', N'kz', N'Жойылатын парақтың құжаттарда көшірмелері бар')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- MIGR:sheet-copies-refusal ── кінець секції ──

-- MIGR:sheet-copies-refusal ── D-6: аркуш, якого нова версія не має, з копіями (RS1) — відмова на етапі плану, до маркера; ru/kz — порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-SCHM-0422.migrateSheetCopiesNotMapped', N'en', N'The new version has no sheet that has copies in this project''s documents. Delete those sheet copies first, then move the project.', 1),
    (N'documents.migrateRefusalSheetCopiesNotMapped', N'en', N'A removed sheet has copies in documents', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- MIGR:sheet-copies-refusal ── кінець секції ──
