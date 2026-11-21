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
    (N'err.ECR-CALC-0409.scriptSwitchDeprecated', N'kz', N'{version} нұсқасы қолданыстан шығарылған: ол Script-ке ауыстырылмайды.'),
    (N'methodologyScript.preview.budgetExhausted.title', N'ru', N'Достигнут предел предпросмотра'),
    (N'methodologyScript.preview.budgetExhausted.title', N'kz', N'Алдын ала қарау шегіне жетті'),
    (N'methodologyScript.preview.budgetExhausted', N'ru', N'Предпросмотр исчерпал бюджет вычислений (20 млн шагов на запуск). Строки с #BUDGET остановил предел предпросмотра, а не ошибка в скрипте: при пересчёте каждая строка проверяется отдельно. Выберите одну таблицу, чтобы увидеть их.'),
    (N'methodologyScript.preview.budgetExhausted', N'kz', N'Алдын ала қарау есептеу бюджетін таусты (бір іске қосуға 20 млн қадам). #BUDGET бар жолдарды скрипттегі қате емес, алдын ала қарау шегі тоқтатты: қайта есептеуде әр жол бөлек тексеріледі. Оларды көру үшін бір кестені таңдаңыз.'),
    (N'methodologyScript.preview.valuesTruncated', N'ru', N'Предпросмотр показывает не более 50 000 значений: в строках с пометкой «значения обрезаны» показаны не все. Выберите одну таблицу, чтобы увидеть все значения.'),
    (N'methodologyScript.preview.valuesTruncated', N'kz', N'Алдын ала қарау 50 000-нан аспайтын мән көрсетеді: «мәндер қысқартылған» белгісі бар жолдарда барлығы көрсетілмеген. Барлық мәндерді көру үшін бір кестені таңдаңыз.'),
    (N'methodologyScript.preview.rowTruncated', N'ru', N'значения обрезаны'),
    (N'methodologyScript.preview.rowTruncated', N'kz', N'мәндер қысқартылған'),
    (N'methodologyScript.checksPaused', N'ru', N'Проверки на паузе: слишком много за короткое время. Повторная проверка через {seconds} с.'),
    (N'methodologyScript.checksPaused', N'kz', N'Тексерулер кідіртілді: қысқа уақытта тым көп. Қайта тексеру {seconds} с кейін.')
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
    (N'err.ECR-CALC-0409.scriptSwitchDeprecated', N'en', N'Version {version} is deprecated: it is not switched to Script.', 1),
    (N'methodologyScript.preview.budgetExhausted.title', N'en', N'Preview limit reached', 1),
    (N'methodologyScript.preview.budgetExhausted', N'en', N'This preview used up its calculation budget (20 million steps per run). Rows with #BUDGET were stopped by the preview limit, not by an error in the script: recalculation checks every row separately. Pick a single table to see them.', 1),
    (N'methodologyScript.preview.valuesTruncated', N'en', N'The preview shows at most 50,000 values: rows marked "values cut off" do not show all of theirs. Pick a single table to see every value.', 1),
    (N'methodologyScript.preview.rowTruncated', N'en', N'values cut off', 1),
    (N'methodologyScript.checksPaused', N'en', N'Checks paused: too many in a short time. Checking again in {seconds} s.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- MS:pss-script-security ── кінець секції ──
