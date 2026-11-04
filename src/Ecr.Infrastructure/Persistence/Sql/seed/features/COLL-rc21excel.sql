-- COLL:rc21excel ── ru/kz RC21 EA-6: імпорт .xlsx виконавця з передачею місяця - «все або нічого»; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-ACCS-0403.importDeniedCells', N'ru', N'Файл отклонён целиком: в нём {deniedCount} ячеек, которые вам нельзя изменять в рамках вашей передачи. Ничего не записано. Уберите эти значения из файла (или попросите владельца изменить передачу) и повторите импорт.'),
    (N'err.ECR-ACCS-0403.importDeniedCells', N'kz', N'Файл толығымен қабылданбады: онда тапсырмаңыз шеңберінде Сізге өзгертуге болмайтын {deniedCount} ұяшық бар. Ештеңе жазылмады. Бұл мәндерді файлдан алып тастаңыз (немесе иесінен тапсырманы өзгертуді сұраңыз) және импортты қайталаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:rc21excel ── кінець секції ──
-- COLL:rc21excel ── RC21 EA-6: імпорт .xlsx виконавця з передачею місяця - «все або нічого», 403; ru/kz - порцією COLL:rc21excel нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-ACCS-0403.importDeniedCells', N'en', N'The file was refused as a whole: it contains {deniedCount} cell(s) you are not allowed to change under your assignment. Nothing was written. Remove those values from the file (or ask the owner to change the assignment) and import again.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:rc21excel ── кінець секції ──
