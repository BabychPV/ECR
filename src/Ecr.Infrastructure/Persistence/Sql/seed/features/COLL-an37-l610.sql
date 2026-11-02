-- COLL:an37-l610 ── ru/kz AN-37 (L6-10); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0422.patchDuplicateRowKey', N'ru', N'Изменение содержит строку {rowKeys} больше одного раза. Ничего не сохранено; передайте каждую строку один раз.'),
    (N'err.ECR-REQ-0422.patchDuplicateCell', N'ru', N'Изменение задаёт столбец {columnCodes} строки {rowKey} больше одного раза. Ничего не сохранено; передайте каждую ячейку один раз.'),
    (N'err.ECR-REQ-0422.patchDuplicateRowKey', N'kz', N'Өзгерісте {rowKeys} жолы бірнеше рет кездеседі. Ештеңе сақталмады; әр жолды бір рет жіберіңіз.'),
    (N'err.ECR-REQ-0422.patchDuplicateCell', N'kz', N'Өзгеріс {rowKey} жолының {columnCodes} бағанын бірнеше рет береді. Ештеңе сақталмады; әр ұяшықты бір рет жіберіңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an37-l610 ── кінець секції ──
