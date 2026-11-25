-- REQIN:server-texts ── ru/kz: тексти сервера за ключем (✎ 25.11 «чому помилки українською»): ECR-CALC-0437 — одне повідомлення на колонку з переліком методологій і назвою колонки; пояснення правил доступу до періоду (діалог підтвердження сітки, матриця доступу); власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'validation.methodology.requiredInput', N'ru', N'Колонка «{column}» обязательна для методологии {methodologies}.'),
    (N'validation.methodology.requiredInput', N'kz', N'«{column}» бағаны {methodologies} әдістемесі үшін міндетті.'),
    (N'validation.methodology.requiredInputMany', N'ru', N'Колонка «{column}» обязательна для методологий {methodologies}.'),
    (N'validation.methodology.requiredInputMany', N'kz', N'«{column}» бағаны {methodologies} әдістемелері үшін міндетті.'),
    (N'validation.methodology.requiredInputUnmapped', N'ru', N'Обязательной колонки методологии нет в этой версии шаблона; проверка пропущена.'),
    (N'validation.methodology.requiredInputUnmapped', N'kz', N'Әдістеменің міндетті бағаны шаблонның бұл нұсқасында жоқ; тексеру өткізіп жіберілді.'),
    (N'validation.access.readOnly', N'ru', N'Правило доступа: только чтение.'),
    (N'validation.access.readOnly', N'kz', N'Қол жеткізу ережесі: тек оқу.'),
    (N'validation.access.headerRow', N'ru', N'Строка-заголовок не заполняется.'),
    (N'validation.access.headerRow', N'kz', N'Тақырып жолы толтырылмайды.'),
    (N'validation.access.periodOutsideWindow', N'ru', N'Период {period} вне окна ввода.'),
    (N'validation.access.periodOutsideWindow', N'kz', N'{period} кезеңі енгізу терезесінен тыс.'),
    (N'validation.access.periodTooFar', N'ru', N'Период {period} дальше, чем на {offset} от текущего {current}.'),
    (N'validation.access.periodTooFar', N'kz', N'{period} кезеңі ағымдағы {current} кезеңінен {offset} кезеңнен артық қашықта.'),
    (N'validation.access.monthOutsideValidity', N'ru', N'Месяц {month} вне срока действия ({from} — {to}).'),
    (N'validation.access.monthOutsideValidity', N'kz', N'{month} айы жарамдылық мерзімінен тыс ({from} — {to}).'),
    (N'validation.access.expression', N'ru', N'Правило доступа: {expression}.'),
    (N'validation.access.expression', N'kz', N'Қол жеткізу ережесі: {expression}.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- REQIN:server-texts ── кінець секції ──

-- REQIN:server-texts ── en: тексти сервера за ключем (ValidationMessageTemplates); ru/kz — порцією REQIN:server-texts вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'validation.methodology.requiredInput', N'en', N'Column "{column}" is required for methodology {methodologies}.', 1),
    (N'validation.methodology.requiredInputMany', N'en', N'Column "{column}" is required for methodologies {methodologies}.', 1),
    (N'validation.methodology.requiredInputUnmapped', N'en', N'A required column of the methodology is not in this template version; the check was skipped.', 1),
    (N'validation.access.readOnly', N'en', N'Access rule: read only.', 1),
    (N'validation.access.headerRow', N'en', N'A header row is not filled in.', 1),
    (N'validation.access.periodOutsideWindow', N'en', N'Period {period} is outside the data entry window.', 1),
    (N'validation.access.periodTooFar', N'en', N'Period {period} is more than {offset} away from the current period {current}.', 1),
    (N'validation.access.monthOutsideValidity', N'en', N'Month {month} is outside the validity period ({from} — {to}).', 1),
    (N'validation.access.expression', N'en', N'Access rule: {expression}.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- REQIN:server-texts ── кінець секції ──
