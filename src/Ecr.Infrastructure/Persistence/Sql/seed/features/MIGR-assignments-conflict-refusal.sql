-- MIGR:assignments-conflict-refusal ── ru/kz: D-10-b (аудит 2026-11), перенос версії відмовляє, коли перейменування таблиці зіткнуло б передачу зрізу з наявною; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-SCHM-0422.migrateAssignmentsConflict', N'ru', N'После переименования таблицы передача среза совпала бы с уже существующей передачей в документе проекта. Сначала отзовите одну из двух передач, затем переносите проект.'),
    (N'err.ECR-SCHM-0422.migrateAssignmentsConflict', N'kz', N'Кестенің атауы өзгергеннен кейін кесінді тапсыруы жоба құжатындағы бар тапсырумен сәйкес келер еді. Алдымен екі тапсырудың бірін алып тастаңыз, содан кейін жобаны көшіріңіз.'),
    (N'documents.migrateRefusalAssignmentsConflict', N'ru', N'Передача переименованной таблицы совпадает с существующей'),
    (N'documents.migrateRefusalAssignmentsConflict', N'kz', N'Атауы өзгерген кестенің тапсыруы бар тапсырумен сәйкес келеді')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- MIGR:assignments-conflict-refusal ── кінець секції ──

-- MIGR:assignments-conflict-refusal ── D-10-b: передача зрізу таблиці X після перейменування X → Y збіглася б із наявною під Y — відмова на етапі плану, до маркера; ru/kz — порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-SCHM-0422.migrateAssignmentsConflict', N'en', N'After the table rename, a hand-over would match an existing hand-over in a document of this project. Remove one of the two hand-overs first, then move the project.', 1),
    (N'documents.migrateRefusalAssignmentsConflict', N'en', N'A renamed table''s hand-over matches an existing one', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- MIGR:assignments-conflict-refusal ── кінець секції ──
