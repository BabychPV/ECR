-- PERMITS: midmonth-split ── ru/kz: фіча (j) 09.11 — історія дозволів рядка/шапки «з дати», розрив між дозволами, зміна шапки посеред місяця; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CELL-4223.permitGap', N'ru', N'Не сохранено: {days} нет действующего разрешения для этой строки — {permit} действует {validity}, {next} действует {nextValidity}. Перенесите дату начала на {suggestedFrom} или добавьте разрешение на эти дни.'),
    (N'err.ECR-CELL-4223.permitGap', N'kz', N'Сақталмады: {days} аралығында бұл жол үшін жарамды рұқсат жоқ — {permit} {validity} аралығында жарамды, {next} {nextValidity} аралығында жарамды. Басталу күнін {suggestedFrom} күніне ауыстырыңыз немесе осы күндерге рұқсат қосыңыз.'),
    (N'err.ECR-HDR-4223.permitGap', N'ru', N'Не сохранено: {days} нет действующего разрешения в поле шапки «{headerFieldCode}» — {permit} действует {validity}, {next} действует {nextValidity}. Перенесите дату начала на {suggestedFrom} или добавьте разрешение на эти дни.'),
    (N'err.ECR-HDR-4223.permitGap', N'kz', N'Сақталмады: {days} аралығында «{headerFieldCode}» тақырып өрісінде жарамды рұқсат жоқ — {permit} {validity} аралығында жарамды, {next} {nextValidity} аралығында жарамды. Басталу күнін {suggestedFrom} күніне ауыстырыңыз немесе осы күндерге рұқсат қосыңыз.'),
    (N'err.ECR-HDR-4223.permitChangeMidMonth', N'ru', N'Не сохранено: разрешение в поле шапки «{headerFieldCode}» можно сменить только с 1-го числа месяца. Чтобы сменить его с {date}, добавьте новое разрешение в строках, которые используют разрешение шапки и имеют данные в этом месяце ({rowCount}): {rows}.'),
    (N'err.ECR-HDR-4223.permitChangeMidMonth', N'kz', N'Сақталмады: «{headerFieldCode}» тақырып өрісіндегі рұқсатты тек айдың 1-күнінен ауыстыруға болады. Оны {date} күнінен ауыстыру үшін тақырып рұқсатын пайдаланатын және осы айда деректері бар жолдарға жаңа рұқсат қосыңыз ({rowCount}): {rows}.'),
    (N'err.ECR-REQ-0422.permitHistoryDate', N'ru', N'Дата {date} не подходит: новое разрешение должно действовать с даты после {after} и не позднее {until}.'),
    (N'err.ECR-REQ-0422.permitHistoryDate', N'kz', N'{date} күні сәйкес келмейді: жаңа рұқсат {after} күнінен кейінгі және {until} күнінен кешіктірмейтін күннен бастап әрекет етуі керек.'),
    (N'err.ECR-REQ-0422.permitHistoryTarget', N'ru', N'Добавить разрешение с даты можно только в колонку разрешения строки, где разрешение уже выбрано (или в поле разрешения шапки), и только другое разрешение.'),
    (N'err.ECR-REQ-0422.permitHistoryTarget', N'kz', N'Күннен бастап рұқсатты тек рұқсат таңдалған жолдың рұқсат бағанына (немесе тақырыптың рұқсат өрісіне) және тек басқа рұқсатты қосуға болады.'),
    (N'err.ECR-REQ-0422.permitHistoryNothingToUndo', N'ru', N'Отменять нечего: добавленного разрешения нет, или его месяцы уже разделены — сначала очистите части.'),
    (N'err.ECR-REQ-0422.permitHistoryNothingToUndo', N'kz', N'Болдырмайтын ештеңе жоқ: қосылған рұқсат жоқ немесе оның айлары бөлініп қойған — алдымен бөліктерді тазалаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: midmonth-split ── кінець секції ──
-- PERMITS: midmonth-split ── en: фіча (j) 09.11 — історія дозволів «з дати»; ru/kz — порцією PERMITS: midmonth-split вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CELL-4223.permitGap', N'en', N'Not saved: {days} have no valid permit for this row — {permit} is valid {validity}, {next} is valid {nextValidity}. Move the start date to {suggestedFrom} or add a permit for those days.', 1),
    (N'err.ECR-HDR-4223.permitGap', N'en', N'Not saved: {days} have no valid permit in header field "{headerFieldCode}" — {permit} is valid {validity}, {next} is valid {nextValidity}. Move the start date to {suggestedFrom} or add a permit for those days.', 1),
    (N'err.ECR-HDR-4223.permitChangeMidMonth', N'en', N'Not saved: the permit in header field "{headerFieldCode}" can change only from the 1st of a month. To change it from {date}, add the new permit in the rows that use the header permit and have data in that month ({rowCount}): {rows}.', 1),
    (N'err.ECR-REQ-0422.permitHistoryDate', N'en', N'The date {date} does not fit: the new permit must be in force from a date after {after} and no later than {until}.', 1),
    (N'err.ECR-REQ-0422.permitHistoryTarget', N'en', N'A permit from a date can be added only to a row''s permit column that already has a permit (or to a header permit field), and only a different permit.', 1),
    (N'err.ECR-REQ-0422.permitHistoryNothingToUndo', N'en', N'Nothing to undo: no permit was added, or its months are already split — clear the parts first.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERMITS: midmonth-split ── кінець секції ──
