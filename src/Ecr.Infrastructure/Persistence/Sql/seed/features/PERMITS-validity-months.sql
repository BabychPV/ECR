-- PERMITS: validity-months ── ru/kz: правило (h) 08.11 — дозвіл має покривати місяці з даними (відмова запису, шапка, Issues); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CELL-4223.permitNotCoveringMonths', N'ru', N'Не сохранено: разрешение {permit} действует {validity} и не покрывает {months} — месяцы, за которые в этой строке есть данные. Выберите разрешение, действующее в эти месяцы, очистите значения за эти месяцы или продлите срок действия разрешения.'),
    (N'err.ECR-CELL-4223.permitNotCoveringMonths', N'kz', N'Сақталмады: {permit} рұқсаты {validity} аралығында жарамды және {months} айларын қамтымайды — бұл жолда осы айлар бойынша деректер бар. Осы айларда жарамды рұқсатты таңдаңыз, осы айлардағы мәндерді тазалаңыз немесе рұқсаттың жарамдылық мерзімін ұзартыңыз.'),
    (N'err.ECR-HDR-4223.permitNotCoveringMonths', N'ru', N'Не сохранено: разрешение {permit} в поле шапки «{headerFieldCode}» действует {validity} и не покрывает {months} — месяцы, за которые в документе есть данные. Выберите разрешение, действующее в эти месяцы, очистите значения за эти месяцы или продлите срок действия разрешения.'),
    (N'err.ECR-HDR-4223.permitNotCoveringMonths', N'kz', N'Сақталмады: «{headerFieldCode}» тақырып өрісіндегі {permit} рұқсаты {validity} аралығында жарамды және {months} айларын қамтымайды — құжатта осы айлар бойынша деректер бар. Осы айларда жарамды рұқсатты таңдаңыз, осы айлардағы мәндерді тазалаңыз немесе рұқсаттың жарамдылық мерзімін ұзартыңыз.'),
    (N'validation.permit.monthsNotCovered', N'ru', N'Разрешение {permit} (колонка «{column}») действует {validity} и не покрывает месяцы с данными: {months}. Выберите разрешение, действующее в эти месяцы, очистите значения за эти месяцы или продлите срок действия разрешения.'),
    (N'validation.permit.monthsNotCovered', N'kz', N'{permit} рұқсаты («{column}» бағаны) {validity} аралығында жарамды және деректері бар айларды қамтымайды: {months}. Осы айларда жарамды рұқсатты таңдаңыз, осы айлардағы мәндерді тазалаңыз немесе рұқсаттың жарамдылық мерзімін ұзартыңыз.'),
    (N'validation.permit.headerMonthsNotCovered', N'ru', N'Поле шапки «{column}»: разрешение {permit} действует {validity} и не покрывает месяцы, за которые в документе есть данные: {months}. Выберите разрешение, действующее в эти месяцы, очистите значения за эти месяцы или продлите срок действия разрешения.'),
    (N'validation.permit.headerMonthsNotCovered', N'kz', N'«{column}» тақырып өрісі: {permit} рұқсаты {validity} аралығында жарамды және құжатта деректері бар айларды қамтымайды: {months}. Осы айларда жарамды рұқсатты таңдаңыз, осы айлардағы мәндерді тазалаңыз немесе рұқсаттың жарамдылық мерзімін ұзартыңыз.'),
    (N'validation.permit.partialMonth', N'ru', N'Разрешение {permit} (колонка «{column}») действует только {days} в месяце {month}: значения этого месяца приняты, но разрешение покрывает только эти дни.'),
    (N'validation.permit.partialMonth', N'kz', N'{permit} рұқсаты («{column}» бағаны) {month} айында тек {days} аралығында жарамды: осы айдың мәндері қабылданды, бірақ рұқсат тек осы күндерді қамтиды.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: validity-months ── кінець секції ──
-- PERMITS: validity-months ── en: правило (h) 08.11 — дозвіл має покривати місяці з даними; ru/kz — порцією PERMITS: validity-months нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CELL-4223.permitNotCoveringMonths', N'en', N'Not saved: permit {permit} is valid {validity} and does not cover {months}, where this row has data. Pick a permit valid in those months, clear the values in those months, or extend the permit''s validity.', 1),
    (N'err.ECR-HDR-4223.permitNotCoveringMonths', N'en', N'Not saved: permit {permit} in header field "{headerFieldCode}" is valid {validity} and does not cover {months}, where the document has data. Pick a permit valid in those months, clear the values in those months, or extend the permit''s validity.', 1),
    (N'validation.permit.monthsNotCovered', N'en', N'Permit {permit} (column "{column}") is valid {validity} and does not cover the months with data: {months}. Pick a permit valid in those months, clear the values in those months, or extend the permit''s validity.', 1),
    (N'validation.permit.headerMonthsNotCovered', N'en', N'Header field "{column}": permit {permit} is valid {validity} and does not cover the months the document holds data in: {months}. Pick a permit valid in those months, clear the values in those months, or extend the permit''s validity.', 1),
    (N'validation.permit.partialMonth', N'en', N'Permit {permit} (column "{column}") is valid only {days} in month {month}: the values of that month are accepted, but the permit covers those days only.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERMITS: validity-months ── кінець секції ──
