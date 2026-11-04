-- COLL:exprbudget ── ru/kz RC5: видима відмова за межею вкладеності й кроків; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'expr.tooDeep', N'ru', N'Формула слишком сложная: глубина вложенности {depth}, допустимо {max}. Разбейте её на несколько вычисляемых колонок.'),
    (N'expr.tooDeep', N'kz', N'Формула тым күрделі: ену тереңдігі {depth}, рұқсат етілгені {max}. Оны бірнеше есептелетін бағанға бөліңіз.'),
    (N'publish.problem.formulaTooDeep', N'ru', N'Формула {formula} слишком сложная: глубина вложенности {depth}, допустимо {max}. Разбейте её на несколько вычисляемых колонок.'),
    (N'publish.problem.formulaTooDeep', N'kz', N'{formula} формуласы тым күрделі: ену тереңдігі {depth}, рұқсат етілгені {max}. Оны бірнеше есептелетін бағанға бөліңіз.'),
    (N'validation.rule.budget', N'ru', N'Правило ''{rule}'' не удалось вычислить: формула слишком велика для одного расчёта (больше 20 000 шагов или 96 уровней вложенности). Разбейте её на несколько вычисляемых колонок.'),
    (N'validation.rule.budget', N'kz', N'''{rule}'' ережесін есептеу мүмкін болмады: формула бір есептеу үшін тым үлкен (20 000 қадамнан немесе 96 ену деңгейінен артық). Оны бірнеше есептелетін бағанға бөліңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:exprbudget ── кінець секції ──
-- COLL:exprbudget ── RC5: видима відмова за межею вкладеності (96) і кроків (20 000) замість мовчазного #BUDGET: публікація/редактор (expr.tooDeep, publish.problem.formulaTooDeep) і експлуатація (validation.rule.budget); ru/kz — порцією COLL:exprbudget у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'expr.tooDeep', N'en', N'The formula is too complex: nesting depth {depth}, allowed {max}. Split it into several calculated columns.', 1),
    (N'publish.problem.formulaTooDeep', N'en', N'Formula {formula} is too complex: nesting depth {depth}, allowed {max}. Split it into several calculated columns.', 1),
    (N'validation.rule.budget', N'en', N'Rule ''{rule}'' could not be evaluated: the formula is too large for one calculation (more than 20,000 steps or 96 nesting levels). Split it into several calculated columns.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:exprbudget ── кінець секції ──
