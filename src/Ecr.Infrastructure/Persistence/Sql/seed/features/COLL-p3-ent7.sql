-- COLL:p3-ent7 ── ru/kz: усі споживачі зв'язку приховані від автора (ФВ-8.12, ent7 P3-7); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'documents.migrateRefusalGrantsNotMapped', N'ru', N'У ролей есть права на листы, таблицы или колонки, которых нет в целевой версии; они были бы потеряны.'),
    (N'documents.migrateRefusalGrantsNotMapped', N'kz', N'Рөлдерде мақсатты нұсқада баламасы жоқ парақтарға, кестелерге немесе бағандарға құқықтар бар; олар жоғалар еді.'),
    (N'err.ECR-REG-0422.lookupRetargetUsedByHidden', N'ru', N'Связь поля «{fieldCode}» нельзя изменить: через неё читают атрибуты правил, формул или версий методологий — {total}, и все {hiddenCount} из показанных в ответе скрыты от вас. Попросите того, у кого есть доступ, изменить или отключить их.'),
    (N'err.ECR-REG-0422.lookupRetargetUsedByHidden', N'kz', N'«{fieldCode}» өрісінің байланысын өзгерту мүмкін емес: ол арқылы атрибуттарды оқитын ережелер, формулалар немесе әдістеме нұсқалары бар — {total}, ал жауапта көрсетілгеннің барлығы ({hiddenCount}) сізден жасырылған. Қолжетімділігі бар адамнан оларды өзгертуді немесе өшіруді сұраңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:p3-ent7 ── кінець секції ──
-- COLL:p3-ent7 ── усі споживачі зв'язку приховані від автора (ФВ-8.12, ent7 P3-7); підпис відмови grantsNotMapped у діалозі переносу (ent7 P3-4) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'documents.migrateRefusalGrantsNotMapped', N'en', N'Roles have permissions on sheets, tables or columns that have no counterpart in the target version; they would be lost.', 1),
    (N'err.ECR-REG-0422.lookupRetargetUsedByHidden', N'en', N'The link of field "{fieldCode}" cannot be changed: {total} rule(s), formula(s) or methodology version(s) read attributes through it, and all {hiddenCount} shown in this response are hidden from you. Ask someone with access to change or disable them first.', 1)
    -- ru/kz — окремою порцією COLL:p3-ent7 у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:p3-ent7 ── кінець секції ──
