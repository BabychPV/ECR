-- CAT:l2ui ── ru/kz RC14-F: панель правила категорії; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'methodologies.categoryRule', N'ru', N'Правило категории'),
    (N'methodologies.categoryRule', N'kz', N'Санат ережесі'),
    (N'methodologies.categoryRuleSet', N'ru', N'Задать правило'),
    (N'methodologies.categoryRuleSet', N'kz', N'Ереже орнату'),
    (N'methodologies.categoryRuleEdit', N'ru', N'Изменить правило'),
    (N'methodologies.categoryRuleEdit', N'kz', N'Ережені өзгерту'),
    (N'methodologies.categoryRuleDelete', N'ru', N'Удалить правило'),
    (N'methodologies.categoryRuleDelete', N'kz', N'Ережені жою'),
    (N'methodologies.categoryRuleDeleteHint', N'ru', N'Без правила константы с несколькими категориями становятся неоднозначными, и строки отклоняются при расчёте. Удалить правило?'),
    (N'methodologies.categoryRuleDeleteHint', N'kz', N'Ережесіз бірнеше санаты бар тұрақтылар түсініксіз болады да, есептеу кезінде жолдар қабылданбайды. Ережені жою керек пе?'),
    (N'methodologies.categoryRuleNone', N'ru', N'У этой версии нет правила категории'),
    (N'methodologies.categoryRuleNone', N'kz', N'Бұл нұсқада санат ережесі жоқ'),
    (N'methodologies.categoryRuleNoneHint', N'ru', N'Правило выбирает категорию (например, Diesel) каждой строки документа, и константы с несколькими категориями получают одно значение.'),
    (N'methodologies.categoryRuleNoneHint', N'kz', N'Ереже құжаттың әр жолының санатын (мысалы, Diesel) таңдайды, сондықтан бірнеше санаты бар тұрақтылар бір мәнге шешіледі.'),
    (N'methodologies.categoryRuleExpression', N'ru', N'Выражение'),
    (N'methodologies.categoryRuleExpression', N'kz', N'Өрнек'),
    (N'methodologies.categoryRuleExpressionHint', N'ru', N'Должно возвращать текст: ключ категории. Доступны @Аргумент, !RowФормула, CST.Константа, if(...) и in(...).'),
    (N'methodologies.categoryRuleErrorHint', N'ru', N'Текстовые значения пишите в одинарных кавычках, например ''Diesel''.'),
    (N'methodologies.categoryRuleExpressionHint', N'kz', N'Мәтін қайтаруы тиіс: санат кілті. @Аргумент, !RowФормула, CST.Тұрақты, if(...) және in(...) қолдануға болады.'),
    (N'methodologies.categoryRuleErrorHint', N'kz', N'Мәтін мәндерін жалғыз тырнақшаға алыңыз, мысалы ''Diesel''.'),
    (N'methodologies.categoryRuleUpdatedAt', N'ru', N'Последнее изменение'),
    (N'methodologies.categoryRuleUpdatedAt', N'kz', N'Соңғы өзгеріс'),
    (N'methodologies.categoryRuleSaved', N'ru', N'Правило категории сохранено.'),
    (N'methodologies.categoryRuleSaved', N'kz', N'Санат ережесі сақталды.'),
    (N'methodologies.categoryRuleDeleted', N'ru', N'Правило категории удалено.'),
    (N'methodologies.categoryRuleDeleted', N'kz', N'Санат ережесі жойылды.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- CAT:l2ui ── кінець секції ──
-- CAT:l2ui ── RC14-F: панель правила категорії в конфігураторі; ru/kz — порцією CAT:l2ui нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'methodologies.categoryRule', N'en', N'Category rule', 1),
    (N'methodologies.categoryRuleSet', N'en', N'Set rule', 1),
    (N'methodologies.categoryRuleEdit', N'en', N'Edit rule', 1),
    (N'methodologies.categoryRuleDelete', N'en', N'Delete rule', 1),
    (N'methodologies.categoryRuleDeleteHint', N'en', N'Without the rule, constants that have several categories become ambiguous and rows are refused at calculation time. Delete the rule?', 1),
    (N'methodologies.categoryRuleNone', N'en', N'This version has no category rule', 1),
    (N'methodologies.categoryRuleNoneHint', N'en', N'The rule picks the category (for example Diesel) of each document row, so constants with several categories resolve to one value.', 1),
    (N'methodologies.categoryRuleExpression', N'en', N'Expression', 1),
    (N'methodologies.categoryRuleExpressionHint', N'en', N'Must return text: the category key. You can use @Argument, !RowFormula, CST.Constant, if(...) and in(...).', 1),
    (N'methodologies.categoryRuleErrorHint', N'en', N'Put text values in single quotes, for example ''Diesel''.', 1),
    (N'methodologies.categoryRuleUpdatedAt', N'en', N'Last changed', 1),
    (N'methodologies.categoryRuleSaved', N'en', N'The category rule has been saved.', 1),
    (N'methodologies.categoryRuleDeleted', N'en', N'The category rule has been deleted.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- CAT:l2ui ── кінець секції ──
