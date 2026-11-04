-- COLL:rc20rule ── ru/kz R16-4: правило категорії з аргументом без колонки; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-0422.categoryRuleUnknownArgument', N'ru', N'Правило категории использует аргумент(ы) {arguments}, для которых нет столбца в привязанных таблицах (таблиц: {tableCount}): правило считается по строке такой таблицы, и аргумент всегда будет пустым.'),
    (N'err.ECR-CALC-0422.categoryRuleUnknownArgument', N'kz', N'Санат ережесі {arguments} аргументтерін пайдаланады, бірақ байланыстырылған кестелерде (кестелер саны: {tableCount}) сәйкес баған жоқ: ереже сол кестенің жолы бойынша есептеледі, сондықтан аргумент әрқашан бос болады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:rc20rule ── кінець секції ──
-- COLL:rc20rule ── R16-4: правило категорії - єдиний код ECR-CALC-0422 для @Nope, CST.X, !X; ru/kz - порцією COLL:rc20rule нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CALC-0422.categoryRuleUnknownArgument', N'en', N'The category rule uses argument(s) {arguments}, which have no matching column in {tableCount} bound table(s): the rule runs on a row of that table, so the argument would always be empty.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:rc20rule ── кінець секції ──
