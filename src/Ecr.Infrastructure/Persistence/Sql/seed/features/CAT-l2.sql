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
