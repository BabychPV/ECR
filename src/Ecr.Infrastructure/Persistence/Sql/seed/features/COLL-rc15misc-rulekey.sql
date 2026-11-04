-- COLL:rc15misc-rulekey ── ru/kz: ключ предиката правила методології - Id колонки, не код; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-0422.ruleKeyNotColumnId', N'ru', N'Ключ «{key}» в условии отбора правила «{code}» не является Id столбца: условие сопоставляется по Id столбца (ColumnDefId), а не по его коду, поэтому с кодом правило не совпадает ни с одной строкой. Выберите столбец в форме правила или укажите его числовой Id.'),
    (N'err.ECR-CALC-0422.ruleKeyNotColumnId', N'kz', N'«{code}» ережесінің сәйкестік шартындағы «{key}» кілті бағанның Id-і емес: шарт бағанның кодымен емес, Id-імен (ColumnDefId) салыстырылады, сондықтан кодпен ереже бірде-бір жолға сәйкес келмейді. Ереже пішінінде бағанды таңдаңыз немесе оның сандық Id-ін көрсетіңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:rc15misc-rulekey ── кінець секції ──

-- COLL:rc15misc-rulekey ── RC14B P3-3: ключ предиката правила методології - Id колонки, не код; ru/kz — порцією COLL:rc15misc-rulekey нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CALC-0422.ruleKeyNotColumnId', N'en', N'Key "{key}" in the predicate of rule "{code}" is not a column Id: the predicate is matched by column Id (ColumnDefId), not by column code, so a code never matches any row. Pick the column in the rule form or use its numeric Id.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:rc15misc-rulekey ── кінець секції ──
