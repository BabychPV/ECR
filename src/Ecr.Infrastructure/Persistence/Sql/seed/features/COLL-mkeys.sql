-- COLL:mkeys ── ru/kz D1: відмова переносу, коли правила/обов'язкові входи методології посилаються на колонки іншої версії шаблону; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-SCHM-0422.migrateMethodologyKeysNotMapped', N'ru', N'Правила или обязательные входы методологии, привязанной к целевой версии, ссылаются на листы, таблицы или колонки, которых нет в целевой версии (код утрачен или переименован; см. methodologyKeys). После переноса эти правила не совпали бы ни с одной строкой — ничего не считалось бы, а блокирующий обязательный вход нельзя было бы удовлетворить. Восстановите или переименуйте недостающий элемент в целевой версии либо опубликуйте версию методологии, указывающую на имеющиеся в ней элементы, затем переносите проект.'),
    (N'err.ECR-SCHM-0422.migrateMethodologyKeysNotMapped', N'kz', N'Мақсатты нұсқаға байланған әдістеменің ережелері немесе міндетті кірістері мақсатты нұсқада жоқ парақтарға, кестелерге немесе бағандарға сілтейді (коды жоғалған немесе өзгертілген; methodologyKeys қараңыз). Көшіруден кейін бұл ережелер бірде-бір жолға сәйкес келмес еді — ештеңе есептелмес еді, ал бұғаттаушы міндетті кірісті қанағаттандыру мүмкін болмас еді. Мақсатты нұсқада жетіспейтін элементті қалпына келтіріңіз немесе атауын өзгертіңіз не онда бар элементтерді көрсететін әдістеме нұсқасын жариялаңыз, содан кейін жобаны көшіріңіз.'),
    (N'documents.migrateRefusalMethodologyKeysNotMapped', N'ru', N'Правила или обязательные входы методологии ссылаются на листы, таблицы или колонки, которых нет в целевой версии шаблона (утрачены или переименованы); после переноса они ничему не соответствовали бы.'),
    (N'documents.migrateRefusalMethodologyKeysNotMapped', N'kz', N'Әдістеме ережелері немесе міндетті кірістері үлгінің мақсатты нұсқасында жоқ парақтарға, кестелерге немесе бағандарға сілтейді (жоғалған немесе өзгертілген); көшіруден кейін олар ешнәрсеге сәйкес келмес еді.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:mkeys ── кінець секції ──
-- COLL:mkeys ── D1: перенос проєкту відмовляє, коли правила/обов'язкові входи методології посилаються на колонки іншої версії шаблону; ru/kz — порцією COLL:mkeys нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-SCHM-0422.migrateMethodologyKeysNotMapped', N'en', N'Rules or required inputs of a methodology bound to the target version refer to sheets, tables or columns that the target version does not have (the code was lost or renamed; see methodologyKeys). After the move those rules would match no rows, so nothing would be calculated, and a blocking required input could never be satisfied. Restore or rename the missing item in the target version, or publish a methodology version that points to the items the target version has, then move the project.', 1),
    (N'documents.migrateRefusalMethodologyKeysNotMapped', N'en', N'Methodology rules or required inputs refer to sheets, tables or columns missing from the target template version (lost or renamed); after the move they would match nothing.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:mkeys ── кінець секції ──
