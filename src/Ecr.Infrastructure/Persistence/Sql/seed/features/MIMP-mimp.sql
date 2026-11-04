-- MIMP ── ru/kz імпорту пакета методологій; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-0422.methodologyImportBlocked', N'ru', N'Пакет не импортирован: найдено блокеров — {count}. Ничего не записано; см. отчёт.'),
    (N'err.ECR-CALC-0422.methodologyImportBlocked', N'kz', N'Пакет импортталмады: бөгеттер табылды — {count}. Ештеңе жазылмады; есепті қараңыз.'),
    (N'err.ECR-CALC-0409.methodologyImportConflict', N'ru', N'Пакет не импортирован: версий, которые уже есть с другим содержимым, — {count}. Ничего не записано; дайте изменённым версиям новый номер.'),
    (N'err.ECR-CALC-0409.methodologyImportConflict', N'kz', N'Пакет импортталмады: басқа мазмұнмен бұрыннан бар нұсқалар — {count}. Ештеңе жазылмады; өзгерген нұсқаларға жаңа нөмір беріңіз.'),
    (N'err.ECR-CALC-0422.methodologyImportTimeZone', N'ru', N'Неизвестный часовой пояс {timeZone}: нужен идентификатор IANA, например Asia/Atyrau.'),
    (N'err.ECR-CALC-0422.methodologyImportTimeZone', N'kz', N'Белгісіз уақыт белдеуі {timeZone}: IANA идентификаторы қажет, мысалы Asia/Atyrau.'),
    (N'methodologies.importPackage', N'ru', N'Импорт пакета'),
    (N'methodologies.importPackage', N'kz', N'Пакетті импорттау'),
    (N'methodologies.importTitle', N'ru', N'Импорт пакета методик'),
    (N'methodologies.importTitle', N'kz', N'Әдістемелер пакетін импорттау'),
    (N'methodologies.importHint', N'ru', N'Сначала проверьте пакет: проверка ничего не записывает. Импорт создаёт только черновые версии; публикация остаётся отдельным шагом другого человека.'),
    (N'methodologies.importHint', N'kz', N'Алдымен пакетті тексеріңіз: тексеру ештеңе жазбайды. Импорт тек нобай нұсқаларын жасайды; жариялау басқа адамның жеке қадамы болып қалады.'),
    (N'methodologies.importFile', N'ru', N'Файл пакета (ecr-methodology-package, JSON)'),
    (N'methodologies.importFile', N'kz', N'Пакет файлы (ecr-methodology-package, JSON)'),
    (N'methodologies.importInvalidFile', N'ru', N'Файл не является корректным JSON.'),
    (N'methodologies.importInvalidFile', N'kz', N'Файл дұрыс JSON емес.'),
    (N'methodologies.importCheck', N'ru', N'Проверить'),
    (N'methodologies.importCheck', N'kz', N'Тексеру'),
    (N'methodologies.importApply', N'ru', N'Импортировать'),
    (N'methodologies.importApply', N'kz', N'Импорттау'),
    (N'methodologies.importOutcomeCreated', N'ru', N'Готово к импорту'),
    (N'methodologies.importOutcomeCreated', N'kz', N'Импортқа дайын'),
    (N'methodologies.importOutcomeUnchanged', N'ru', N'Импортировать нечего: пакет уже в системе'),
    (N'methodologies.importOutcomeUnchanged', N'kz', N'Импорттайтын ештеңе жоқ: пакет жүйеде бар'),
    (N'methodologies.importOutcomeBlocked', N'ru', N'В пакете есть блокеры, импорт невозможен'),
    (N'methodologies.importOutcomeBlocked', N'kz', N'Пакетте бөгеттер бар, импорттау мүмкін емес'),
    (N'methodologies.importOutcomeConflict', N'ru', N'Некоторые версии уже есть с другим содержимым'),
    (N'methodologies.importOutcomeConflict', N'kz', N'Кейбір нұсқалар басқа мазмұнмен бұрыннан бар'),
    (N'methodologies.importTotals', N'ru', N'Новых методик: {methodologies}; новых черновых версий: {versions}; версий без изменений: {unchanged}; формул: {formulas}; значений констант: {constants}; импортов между методиками: {imports}.'),
    (N'methodologies.importTotals', N'kz', N'Жаңа әдістемелер: {methodologies}; жаңа нобай нұсқалары: {versions}; өзгеріссіз нұсқалар: {unchanged}; формулалар: {formulas}; константалар: {constants}; әдістемелер арасындағы импорттар: {imports}.'),
    (N'methodologies.importBlockers', N'ru', N'Блокеры'),
    (N'methodologies.importBlockers', N'kz', N'Бөгеттер'),
    (N'methodologies.importConflicts', N'ru', N'Конфликты'),
    (N'methodologies.importConflicts', N'kz', N'Қайшылықтар'),
    (N'methodologies.importWarnings', N'ru', N'Предупреждения'),
    (N'methodologies.importWarnings', N'kz', N'Ескертулер'),
    (N'methodologies.importDone', N'ru', N'Пакет импортирован: создано черновых версий — {versions}.'),
    (N'methodologies.importDone', N'kz', N'Пакет импортталды: жасалған нобай нұсқалары — {versions}.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- MIMP ── кінець секції ──

-- MIMP: імпорт пакета методологій з AF (`ImportMethodologyPackageHandler`, `MethodologyPackageImport.tsx`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CALC-0422.methodologyImportBlocked', N'en', N'The package was not imported: blockers found — {count}. Nothing was written; see the report.', 1),
    (N'err.ECR-CALC-0409.methodologyImportConflict', N'en', N'The package was not imported: versions that already exist with different content — {count}. Nothing was written; give changed versions a new number.', 1),
    (N'err.ECR-CALC-0422.methodologyImportTimeZone', N'en', N'Unknown time zone {timeZone}: an IANA identifier is required, e.g. Asia/Atyrau.', 1),
    (N'methodologies.importPackage', N'en', N'Import package', 1),
    (N'methodologies.importTitle', N'en', N'Import methodology package', 1),
    (N'methodologies.importHint', N'en', N'Check the package first: checking writes nothing. Import creates draft versions only; publishing stays a separate step by another person.', 1),
    (N'methodologies.importFile', N'en', N'Package file (ecr-methodology-package, JSON)', 1),
    (N'methodologies.importInvalidFile', N'en', N'The file is not valid JSON.', 1),
    (N'methodologies.importCheck', N'en', N'Check', 1),
    (N'methodologies.importApply', N'en', N'Import', 1),
    (N'methodologies.importOutcomeCreated', N'en', N'Ready to import', 1),
    (N'methodologies.importOutcomeUnchanged', N'en', N'Nothing to import: the package is already in the system', 1),
    (N'methodologies.importOutcomeBlocked', N'en', N'The package has blockers and cannot be imported', 1),
    (N'methodologies.importOutcomeConflict', N'en', N'Some versions already exist with different content', 1),
    (N'methodologies.importTotals', N'en', N'New methodologies: {methodologies}; new draft versions: {versions}; unchanged versions: {unchanged}; formulas: {formulas}; constant values: {constants}; imports between methodologies: {imports}.', 1),
    (N'methodologies.importBlockers', N'en', N'Blockers', 1),
    (N'methodologies.importConflicts', N'en', N'Conflicts', 1),
    (N'methodologies.importWarnings', N'en', N'Warnings', 1),
    (N'methodologies.importDone', N'en', N'Package imported: draft versions created — {versions}.', 1)
    -- ru/kz — окремою порцією `MIMP` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- MIMP: кінець секції ──
