-- MS:pss-script-security ── ru/kz: PSS-21 межі прогонів і перевірок скрипта (частота, одночасність, час); PSS-23 ворота перемикання на Script ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0429.tooManyScriptRuns', N'ru', N'Слишком много запусков предпросмотра скрипта за короткое время. Подождите немного и повторите попытку.'),
    (N'err.ECR-REQ-0429.tooManyScriptRuns', N'kz', N'Қысқа уақыт ішінде скриптті алдын ала қарау іске қосулары тым көп. Біраз күтіп, қайталап көріңіз.'),
    (N'err.ECR-REQ-0429.tooManyScriptChecks', N'ru', N'Слишком много проверок скрипта за короткое время. Подождите немного и повторите попытку.'),
    (N'err.ECR-REQ-0429.tooManyScriptChecks', N'kz', N'Қысқа уақыт ішінде скрипт тексерулері тым көп. Біраз күтіп, қайталап көріңіз.'),
    (N'err.ECR-REQ-0429.scriptRunBusy', N'ru', N'Сервер занят другими запусками скриптов. Повторите попытку через минуту.'),
    (N'err.ECR-REQ-0429.scriptRunBusy', N'kz', N'Сервер басқа скрипт іске қосуларымен бос емес. Бір минуттан кейін қайталаңыз.'),
    (N'err.ECR-CALC-0422.scriptPreviewTimeout', N'ru', N'Предпросмотр скрипта не уложился в {seconds} с. Уменьшите число строк или выберите одну таблицу.'),
    (N'err.ECR-CALC-0422.scriptPreviewTimeout', N'kz', N'Скриптті алдын ала қарау {seconds} с ішінде аяқталмады. Жолдар санын азайтыңыз немесе бір кестені таңдаңыз.'),
    (N'err.ECR-CALC-0422.switchGoldenSetEmpty', N'ru', N'Версию {version} нельзя переключить на Script: в её эталонном наборе нет случаев, поэтому ничего не проверено.'),
    (N'err.ECR-CALC-0422.switchGoldenSetEmpty', N'kz', N'{version} нұсқасын Script-ке ауыстыруға болмайды: оның эталондық жиынтығында жағдайлар жоқ, сондықтан ештеңе тексерілмеді.'),
    (N'err.ECR-CALC-0422.switchGoldenNoExpectations', N'ru', N'Версию {version} нельзя переключить на Script: у случаев ({tests}) нет ожидаемых значений, поэтому ничего не проверено.'),
    (N'err.ECR-CALC-0422.switchGoldenNoExpectations', N'kz', N'{version} нұсқасын Script-ке ауыстыруға болмайды: ({tests}) жағдайларында күтілетін мәндер жоқ, сондықтан ештеңе тексерілмеді.'),
    (N'err.ECR-CALC-0422.switchGoldenSetDiverged', N'ru', N'Версию {version} нельзя переключить на Script: на эталонном наборе разошлись значения (значений: {count}; тесты: {tests}).'),
    (N'err.ECR-CALC-0422.switchGoldenSetDiverged', N'kz', N'{version} нұсқасын Script-ке ауыстыруға болмайды: эталондық жиынтықта {count} мән сәйкес келмеді (тесттер: {tests}).'),
    (N'err.ECR-CALC-0409.scriptSwitchDeprecated', N'ru', N'Версия {version} выведена из употребления: на Script она не переключается.'),
    (N'err.ECR-CALC-0409.scriptSwitchDeprecated', N'kz', N'{version} нұсқасы қолданыстан шығарылған: ол Script-ке ауыстырылмайды.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- MS:pss-script-security ── кінець секції ──

-- MS:pss-script-security ── en: PSS-21 межі прогонів і перевірок скрипта; PSS-23 ворота перемикання на Script; ru/kz — порцією MS:pss-script-security вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REQ-0429.tooManyScriptRuns', N'en', N'Too many script preview runs in a short time. Wait a moment and try again.', 1),
    (N'err.ECR-REQ-0429.tooManyScriptChecks', N'en', N'Too many script checks in a short time. Wait a moment and try again.', 1),
    (N'err.ECR-REQ-0429.scriptRunBusy', N'en', N'The server is busy running other script previews. Try again in a minute.', 1),
    (N'err.ECR-CALC-0422.scriptPreviewTimeout', N'en', N'The script preview did not finish within {seconds} s. Reduce the number of rows or pick a single table.', 1),
    (N'err.ECR-CALC-0422.switchGoldenSetEmpty', N'en', N'Version {version} cannot be switched to Script: its golden set has no cases, so nothing was checked.', 1),
    (N'err.ECR-CALC-0422.switchGoldenNoExpectations', N'en', N'Version {version} cannot be switched to Script: the cases ({tests}) have no expected values, so nothing was checked.', 1),
    (N'err.ECR-CALC-0422.switchGoldenSetDiverged', N'en', N'Version {version} cannot be switched to Script: {count} value(s) diverged on the golden set (tests: {tests}).', 1),
    (N'err.ECR-CALC-0409.scriptSwitchDeprecated', N'en', N'Version {version} is deprecated: it is not switched to Script.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- MS:pss-script-security ── кінець секції ──
