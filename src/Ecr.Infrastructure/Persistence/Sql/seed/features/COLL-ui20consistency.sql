-- COLL:ui20consistency ── ru/kz UI-20: знахідки узгодженості на шаблоні переліку (пояснення, смуга за вагою, шторка); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'consistency.description', N'ru', N'Каждую ночь ECR сверяет числа, которые должны совпадать между документами, таблицами и справочниками. Каждая находка называет нарушенное правило и затронутую запись.'),
    (N'consistency.description', N'kz', N'ECR әр түнде құжаттар, кестелер мен анықтамалықтар арасында сәйкес болуға тиіс сандарды салыстырады. Әр табылған жағдай бұзылған ережені және қозғалған жазбаны атайды.'),
    (N'consistency.statsLabel', N'ru', N'Неразрешённые находки по важности'),
    (N'consistency.statsLabel', N'kz', N'Маңыздылығы бойынша шешілмеген жағдайлар'),
    (N'consistency.statErrors', N'ru', N'ошибок'),
    (N'consistency.statErrors', N'kz', N'қате'),
    (N'consistency.statWarnings', N'ru', N'предупреждений'),
    (N'consistency.statWarnings', N'kz', N'ескерту'),
    (N'consistency.statInfo', N'ru', N'информационных'),
    (N'consistency.statInfo', N'kz', N'ақпараттық'),
    (N'consistency.noMatch', N'ru', N'Нет находок, подходящих под фильтры.'),
    (N'consistency.noMatch', N'kz', N'Сүзгілерге сәйкес жағдайлар жоқ.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui20consistency ── кінець секції ──
-- COLL:ui20consistency ── UI-20: знахідки узгодженості на шаблоні переліку (пояснення, смуга за вагою, шторка); ru/kz — порцією COLL:ui20consistency у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'consistency.description', N'en', N'Every night ECR compares numbers that must agree with each other across documents, tables and registries. Each finding names the rule that failed and the record it affects.', 1),
    (N'consistency.statsLabel', N'en', N'Unresolved findings by severity', 1),
    (N'consistency.statErrors', N'en', N'errors', 1),
    (N'consistency.statWarnings', N'en', N'warnings', 1),
    (N'consistency.statInfo', N'en', N'info', 1),
    (N'consistency.noMatch', N'en', N'No findings match the filters.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui20consistency ── кінець секції ──
