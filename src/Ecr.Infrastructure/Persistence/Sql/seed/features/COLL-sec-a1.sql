-- COLL:sec-a1 ── ru/kz відмови переносу версії при заборонах без відповідника; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-SCHM-0422.migrateGrantsNotMapped', N'ru', N'В целевой версии нет листа, таблицы или столбца с кодом ресурса, на котором стоит право доступа (запрет или ограничивающее чтение), поэтому право не перенести, а доступ мог бы расшириться. Снимите или пересоздайте это право осознанно до переноса проекта.'),
    (N'err.ECR-SCHM-0422.migrateGrantsNotMapped', N'kz', N'Мақсатты нұсқада қол жеткізу құқығы (тыйым немесе шектейтін оқу) қойылған ресурстың кодымен парақ, кесте немесе баған жоқ, сондықтан құқықты көшіру мүмкін емес, ал қолжетімділік кеңеюі мүмкін. Жобаны көшірместен бұрын бұл құқықты саналы түрде алып тастаңыз немесе қайта жасаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:sec-a1 ── кінець секції ──

-- COLL:sec-a1 ── Перенос версії: заборона на аркуші/таблиці/колонці, якої нема в новій версії (`MigrateDocumentVersionHandler`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-SCHM-0422.migrateGrantsNotMapped', N'en', N'The target version has no sheet, table or column with the code of a resource that has an access grant (deny or a restricting Read), so the grant cannot be carried over and access could widen. Remove or re-create that grant deliberately before moving the project.', 1)
    -- ru/kz — окремою порцією `COLL:sec-a1` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:sec-a1 ── кінець секції ──
