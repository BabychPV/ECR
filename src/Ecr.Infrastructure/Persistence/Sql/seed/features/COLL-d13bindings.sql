-- COLL:d13bindings ── ru/kz D-13: відмова переносу, коли прив'язка методології не має відповідника в цільовій версії; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-SCHM-0422.migrateBindingsNotMapped', N'ru', N'В целевой версии нет активной привязки выхода методологии на соответствующей колонке (или самой колонки), поэтому после переноса эта колонка осталась бы пустой без ошибки. Создайте версию заново клоном переносимой (клон теперь копирует привязки) или осознанно отключите привязку, затем переносите проект.'),
    (N'err.ECR-SCHM-0422.migrateBindingsNotMapped', N'kz', N'Мақсатты нұсқада сәйкес бағанда әдістеменің шығысына белсенді байланыс жоқ (немесе бағанның өзі жоқ), сондықтан көшіруден кейін бұл баған қатесіз бос қалар еді. Нұсқаны көшірілетін нұсқадан қайта клондаңыз (клон енді байланыстарды көшіреді) немесе байланысты саналы түрде өшіріңіз, содан кейін жобаны көшіріңіз.'),
    (N'documents.migrateRefusalBindingsNotMapped', N'ru', N'Выходы методологий привязаны к колонкам, для которых в целевой версии нет привязки; эти колонки остались бы пустыми.'),
    (N'documents.migrateRefusalBindingsNotMapped', N'kz', N'Әдістеме шығыстары мақсатты нұсқада байланысы жоқ бағандарға байланған; бұл бағандар бос қалар еді.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:d13bindings ── кінець секції ──
-- COLL:d13bindings ── D-13: перенос проєкту на версію шаблону відмовляє, якщо активна прив'язка методології не має відповідника в цільовій; ru/kz — порцією COLL:d13bindings нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-SCHM-0422.migrateBindingsNotMapped', N'en', N'The target version has no active binding of a methodology output on the matching column (or the column itself is missing), so after the move that column would stay empty without any error. Clone the version again from the one being moved from (a clone now carries bindings), or switch the binding off deliberately, then move the project.', 1),
    (N'documents.migrateRefusalBindingsNotMapped', N'en', N'Methodology outputs are bound to columns that have no matching binding in the target version; those columns would be left empty.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:d13bindings ── кінець секції ──
