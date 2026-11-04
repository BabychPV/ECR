-- CAT:l2 ── ru/kz L-2: правило категорії константи; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-0422.categoryRuleEmpty', N'ru', N'Правилу категории нужно выражение; чтобы правила не было, удалите его.'),
    (N'err.ECR-CALC-0422.categoryRuleEmpty', N'kz', N'Санат ережесіне өрнек қажет; ереже болмауы үшін оны жойыңыз.'),
    (N'err.ECR-CALC-0422.categoryRuleFailed', N'ru', N'Правило категории «{expression}» не дало категорию для этой строки: {reason}. Строка не рассчитана.'),
    (N'err.ECR-CALC-0422.categoryRuleFailed', N'kz', N'«{expression}» санат ережесі осы жол үшін санат бермеді: {reason}. Жол есептелмеді.'),
    (N'err.ECR-CALC-0422.categoryRuleInvalid', N'ru', N'Выражение правила категории недопустимо: {reason}.'),
    (N'err.ECR-CALC-0422.categoryRuleInvalid', N'kz', N'Санат ережесінің өрнегі жарамсыз: {reason}.'),
    (N'err.ECR-CALC-0422.categoryRuleNotText', N'ru', N'Правило категории должно возвращать текст (ключ категории, например "Diesel"), а не число.'),
    (N'err.ECR-CALC-0422.categoryRuleNotText', N'kz', N'Санат ережесі мәтін (санат кілті, мысалы "Diesel") қайтаруы тиіс, сан емес.'),
    (N'publish.problem.categoryRuleInvalid', N'ru', N'Правило категории не разбирается: {reason}'),
    (N'publish.problem.categoryRuleInvalid', N'kz', N'Санат ережесі талданбайды: {reason}'),
    (N'publish.problem.categoryRuleNotText', N'ru', N'Правило категории возвращает {type}, а ключ категории должен быть текстом (например "Diesel").'),
    (N'publish.problem.categoryRuleNotText', N'kz', N'Санат ережесі {type} қайтарады, ал санат кілті мәтін болуы тиіс (мысалы "Diesel").'),
    (N'publish.problem.categoryRuleUnknownConstant', N'ru', N'Правило категории ссылается на константу CST.{code}, которой в этой версии нет.'),
    (N'publish.problem.categoryRuleUnknownConstant', N'kz', N'Санат ережесі осы нұсқада жоқ CST.{code} тұрақтысына сілтейді.'),
    (N'publish.problem.categoryRuleBadFormula', N'ru', N'Правило категории ссылается на !{formula}: оно видит только Row-формулы версии (считается один раз на строку, до цикла веществ).'),
    (N'publish.problem.categoryRuleBadFormula', N'kz', N'Санат ережесі !{formula} формуласына сілтейді: ол нұсқаның тек Row-формулаларын көреді (жолға бір рет, заттар циклінен бұрын есептеледі).'),
    (N'publish.warning.categoryRuleMissing', N'ru', N'Констант с несколькими категориями: {count} ({constants}), но у версии нет правила категории: каждая из них неоднозначна при расчёте.'),
    (N'publish.warning.categoryRuleMissing', N'kz', N'Бірнеше санаты бар тұрақтылар: {count} ({constants}), бірақ нұсқада санат ережесі жоқ: олардың әрқайсысы есептеу кезінде екіұшты.'),
    (N'publish.warning.categoryRuleRowConstant', N'ru', N'Row-формула {formula} читает константу {constant} с несколькими категориями. Row-фаза идёт до правила категории, поэтому константа там неоднозначна: сделайте формулу формулой вещества.'),
    (N'publish.warning.categoryRuleRowConstant', N'kz', N'{formula} Row-формуласы бірнеше санаты бар {constant} тұрақтысын оқиды. Row-фаза санат ережесінен бұрын орындалады, сондықтан тұрақты онда екіұшты: формуланы зат формуласына айналдырыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- CAT:l2 ── кінець секції ──
-- CAT:l2 ── L-2: правило категорії константи (calc.CategoryRule); ru/kz — порцією CAT:l2 нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CALC-0422.categoryRuleEmpty', N'en', N'The category rule needs an expression; to have no rule, delete it.', 1),
    (N'err.ECR-CALC-0422.categoryRuleFailed', N'en', N'The category rule "{expression}" gave no category for this row: {reason}. The row is not calculated.', 1),
    (N'err.ECR-CALC-0422.categoryRuleInvalid', N'en', N'The category rule expression is invalid: {reason}.', 1),
    (N'err.ECR-CALC-0422.categoryRuleNotText', N'en', N'The category rule must return text (the category key, for example "Diesel"), not a number.', 1),
    (N'publish.problem.categoryRuleInvalid', N'en', N'The category rule does not parse: {reason}', 1),
    (N'publish.problem.categoryRuleNotText', N'en', N'The category rule returns {type}, but the category key must be text (for example "Diesel").', 1),
    (N'publish.problem.categoryRuleUnknownConstant', N'en', N'The category rule references constant CST.{code}, which this version does not define.', 1),
    (N'publish.problem.categoryRuleBadFormula', N'en', N'The category rule references !{formula}: it can only read Row formulas of the version (it runs once per row, before the substance loop).', 1),
    (N'publish.warning.categoryRuleMissing', N'en', N'{count} constant(s) have more than one category ({constants}), but the version has no category rule: each of them is ambiguous at calculation time.', 1),
    (N'publish.warning.categoryRuleRowConstant', N'en', N'Row formula {formula} reads constant {constant}, which has several categories. The Row phase runs before the category rule, so the constant is ambiguous there: make the formula a substance formula.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- CAT:l2 ── кінець секції ──
