-- COLL:acc-ow-dialog ── ru/kz: CL-8; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'documents.migrateGuardedOverwriteTitle', N'ru', N'Перезаписать {count} производных значений?'),
    (N'documents.migrateGuardedOverwriteBody', N'ru', N'Целевая версия кладёт новую формулу ячейки на ячейки с уже введёнными значениями. После переноса пересчёт заменит эти значения результатом формулы. Ячейки (введённых значений):'),
    (N'documents.migrateGuardedOverwriteItem', N'ru', N'{sheet} / {table} / {row} / {column}: {values}'),
    (N'documents.migrateGuardedOverwriteMore', N'ru', N'Ещё ячейки не показаны.'),
    (N'documents.migrateGuardedOverwriteConfirm', N'ru', N'Да, перезаписать'),
    (N'documents.migrateGuardedOverwriteCancel', N'ru', N'Нет'),
    (N'documents.migrateGuardedOverwriteAccepted', N'ru', N'Перезапись подтверждена: {count} производных значений заменят формулы.'),
    (N'documents.migrateGuardedOverwriteTitle', N'kz', N'{count} туынды мәнді қайта жазу керек пе?'),
    (N'documents.migrateGuardedOverwriteBody', N'kz', N'Мақсатты нұсқа енгізілген мәндері бар ұяшықтарға жаңа ұяшық формуласын қояды. Көшіруден кейін қайта есептеу бұл мәндерді формула нәтижесімен ауыстырады. Ұяшықтар (енгізілген мәндер):'),
    (N'documents.migrateGuardedOverwriteItem', N'kz', N'{sheet} / {table} / {row} / {column}: {values}'),
    (N'documents.migrateGuardedOverwriteMore', N'kz', N'Тағы ұяшықтар көрсетілмеген.'),
    (N'documents.migrateGuardedOverwriteConfirm', N'kz', N'Иә, қайта жазу'),
    (N'documents.migrateGuardedOverwriteCancel', N'kz', N'Жоқ'),
    (N'documents.migrateGuardedOverwriteAccepted', N'kz', N'Қайта жазу расталды: {count} туынды мәнді формулалар ауыстырады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:acc-ow-dialog ── кінець секції ──
