-- COLL:a2chk ── ru/kz A2-04: знахідка Check називає рядок джерела; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'validation.check.mismatchRow', N'ru', N'Сверка: {left} = {leftValue} (строка источника {sourceRow}) не сходится с {right} = {rightValue}: отклонение {deviation}, допустимо {allowed} ({kind}).'),
    (N'validation.check.mismatchRow', N'kz', N'Салыстыру: {left} = {leftValue} (дереккөз жолы {sourceRow}) мәні {right} = {rightValue} мәніне сәйкес келмейді: ауытқу {deviation}, рұқсат етілгені {allowed} ({kind}).')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:a2chk ── кінець секції ──
-- COLL:a2chk ── A2-04: знахідка Check називає рядок джерела (кілька рядків джерела проти одного рядка приймача); ru/kz — порцією COLL:a2chk у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'validation.check.mismatchRow', N'en', N'Check: {left} = {leftValue} (source row {sourceRow}) does not match {right} = {rightValue}: deviation {deviation}, allowed {allowed} ({kind}).', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:a2chk ── кінець секції ──
