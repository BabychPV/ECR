-- COLL:optional-argument ── ru/kz: D-OA, необязательный аргумент `Name?`; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'publish.warning.optionalArgumentNoColumn', N'ru', N'Необязательный аргумент {name} не имеет столбца ни в одной привязанной таблице ({tables}) — он всегда null. Чаще всего это опечатка в имени аргумента.'),
    (N'publish.warning.optionalArgumentNoColumn', N'kz', N'{name} міндетті емес аргументінің байланысқан кестелердің ешқайсысында бағаны жоқ ({tables}) — ол әрдайым null. Көбінесе бұл аргумент атауындағы қате.'),
    (N'methodologies.argumentsOptionalHint', N'ru', N'Необязательный аргумент помечается «?» в конце (например, Flow;OperatingTime?): отсутствующая ячейка считается пустой, а не ошибкой.'),
    (N'methodologies.argumentsOptionalHint', N'kz', N'Міндетті емес аргументтің соңына «?» қойылады (мысалы, Flow;OperatingTime?): ұяшық болмаса, ол қате емес, бос деп саналады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:optional-argument ── кінець секції ──
