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
