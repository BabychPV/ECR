-- B75:row-lock ── ru/kz: право Document.RowLock, відмова RowLocked, помилки блокувань рядків і режиму без погодження (B75); власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'permission.Document.RowLock', N'ru', N'Блокировать и разблокировать строки документа'),
    (N'permission.Document.RowLock', N'kz', N'Құжат жолдарын бұғаттау және бұғаттан шығару'),
    (N'deny.RowLocked', N'ru', N'Строка заблокирована на время: менять её может только тот, кто её заблокировал. Блокировка снимется сама после даты окончания или её снимет владелец.'),
    (N'deny.RowLocked', N'kz', N'Жол уақытша бұғатталған: оны тек бұғаттаған адам ғана өзгерте алады. Бұғат аяқталу күнінен кейін өзі алынады немесе оны иесі алады.'),
    (N'err.ECR-ACCS-0403.rowLockDenied', N'ru', N'Заблокировать или разблокировать строки нельзя.'),
    (N'err.ECR-ACCS-0403.rowLockDenied', N'kz', N'Жолдарды бұғаттауға немесе бұғаттан шығаруға болмайды.'),
    (N'err.ECR-REQ-0422.lockRangeInvalid', N'ru', N'Период блокировки пуст, уже прошёл или слишком длинный: дата окончания должна быть не раньше даты начала, не в прошлом и не дальше 10 лет.'),
    (N'err.ECR-REQ-0422.lockRangeInvalid', N'kz', N'Бұғаттау кезеңі бос, өтіп кеткен немесе тым ұзақ: аяқталу күні басталу күнінен ерте, өткен уақытта және 10 жылдан әрі болмауы керек.'),
    (N'err.ECR-REQ-0422.lockNoteTooLong', N'ru', N'Примечание к блокировке длиннее {max} символов.'),
    (N'err.ECR-REQ-0422.lockNoteTooLong', N'kz', N'Бұғаттау ескертпесі {max} таңбадан ұзын.'),
    (N'err.ECR-REQ-0422.lockRowsInvalid', N'ru', N'Укажите лист, таблицу и от 1 до {max} строк.'),
    (N'err.ECR-REQ-0422.lockRowsInvalid', N'kz', N'Парақты, кестені және 1-ден {max}-ге дейін жолды көрсетіңіз.'),
    (N'err.ECR-ROW-0409.rowAlreadyLocked', N'ru', N'Строки уже заблокированы на это время: {rowKeys}.'),
    (N'err.ECR-ROW-0409.rowAlreadyLocked', N'kz', N'Жолдар осы уақытқа бұғатталған: {rowKeys}.'),
    (N'err.ECR-ROW-0404.lockRowsMissing', N'ru', N'В таблице нет строк: {rowKeys}.'),
    (N'err.ECR-ROW-0404.lockRowsMissing', N'kz', N'Кестеде мына жолдар жоқ: {rowKeys}.'),
    (N'err.ECR-ROW-0404.rowLock', N'ru', N'Блокировки {lockId} в документе нет.'),
    (N'err.ECR-ROW-0404.rowLock', N'kz', N'Құжатта {lockId} бұғаты жоқ.'),
    (N'err.ECR-DOC-0404.tableNotInDocument', N'ru', N'Такой таблицы нет в документе.'),
    (N'err.ECR-DOC-0404.tableNotInDocument', N'kz', N'Құжатта мұндай кесте жоқ.'),
    (N'err.ECR-SUB-4221.workflowDisabled', N'ru', N'Шаблон документа работает без подачи и утверждения: значения сразу доступны отчётности.'),
    (N'err.ECR-SUB-4221.workflowDisabled', N'kz', N'Құжат үлгісі тапсырусыз және бекітусіз жұмыс істейді: мәндер есептілікке бірден қолжетімді.'),
    (N'err.ECR-TMPL-0422.workflowModeUnknown', N'ru', N'Неизвестный режим согласования версии шаблона {versionId}.'),
    (N'err.ECR-TMPL-0422.workflowModeUnknown', N'kz', N'{versionId} үлгі нұсқасының келісу режимі белгісіз.'),
    (N'err.ECR-TMPL-0422.workflowModeHasRoute', N'ru', N'У версии {versionId} есть собственный маршрут согласования; без согласования он ничего не значит. Отключите маршрут.'),
    (N'err.ECR-TMPL-0422.workflowModeHasRoute', N'kz', N'{versionId} нұсқасының өз келісу бағыты бар; келісусіз ол ештеңе білдірмейді. Бағытты өшіріңіз.'),
    (N'err.ECR-SCHM-0409.workflowModeBreaking', N'ru', N'Режим согласования нельзя изменить: в версии {versionId} уже есть документы. Клонируйте версию.'),
    (N'err.ECR-SCHM-0409.workflowModeBreaking', N'kz', N'Келісу режимін өзгертуге болмайды: {versionId} нұсқасында құжаттар бар. Нұсқаны клондаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- B75:row-lock ── кінець секції ──

-- B75:row-lock ── en: право Document.RowLock, відмова RowLocked, помилки блокувань рядків і режиму без погодження; ru/kz — порцією B75:row-lock вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'permission.Document.RowLock', N'en', N'Lock and unlock document rows', 1),
    (N'deny.RowLocked', N'en', N'The row is locked for a period: only the person who locked it can change it. The lock ends by itself after its end date, or its owner removes it.', 1),
    (N'err.ECR-ACCS-0403.rowLockDenied', N'en', N'You cannot lock or unlock these rows.', 1),
    (N'err.ECR-REQ-0422.lockRangeInvalid', N'en', N'The lock period is empty, already over or too long: the end date must not be before the start date, in the past or more than 10 years ahead.', 1),
    (N'err.ECR-REQ-0422.lockNoteTooLong', N'en', N'The lock note is longer than {max} characters.', 1),
    (N'err.ECR-REQ-0422.lockRowsInvalid', N'en', N'Specify a sheet, a table and 1 to {max} rows.', 1),
    (N'err.ECR-ROW-0409.rowAlreadyLocked', N'en', N'These rows are already locked for this time: {rowKeys}.', 1),
    (N'err.ECR-ROW-0404.lockRowsMissing', N'en', N'The table has no such rows: {rowKeys}.', 1),
    (N'err.ECR-ROW-0404.rowLock', N'en', N'There is no lock {lockId} in this document.', 1),
    (N'err.ECR-DOC-0404.tableNotInDocument', N'en', N'There is no such table in this document.', 1),
    (N'err.ECR-SUB-4221.workflowDisabled', N'en', N'This document template works without submission and approval: values are available to reporting right away.', 1),
    (N'err.ECR-TMPL-0422.workflowModeUnknown', N'en', N'Unknown approval mode for template version {versionId}.', 1),
    (N'err.ECR-TMPL-0422.workflowModeHasRoute', N'en', N'Version {versionId} has its own approval route; without approval it means nothing. Turn the route off.', 1),
    (N'err.ECR-SCHM-0409.workflowModeBreaking', N'en', N'The approval mode cannot be changed: version {versionId} already has documents. Clone the version.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- B75:row-lock ── кінець секції ──
