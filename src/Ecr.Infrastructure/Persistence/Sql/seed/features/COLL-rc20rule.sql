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
