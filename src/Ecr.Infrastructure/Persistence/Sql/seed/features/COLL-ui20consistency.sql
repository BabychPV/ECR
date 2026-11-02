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
