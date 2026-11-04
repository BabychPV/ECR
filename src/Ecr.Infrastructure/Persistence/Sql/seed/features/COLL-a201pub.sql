-- COLL:a201pub ── ru/kz A2-01: перелік проблем публікації шаблону; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-TMPL-4224.severityConflict', N'ru', N'Правила {ruleCode} ({severity}) и {otherRuleCode} ({otherSeverity}) действуют на одну и ту же область таблицы {tableCode} с разными уровнями. Задайте им один уровень или сузьте область действия одного из них.'),
    (N'err.ECR-TMPL-4224.severityConflict', N'kz', N'{ruleCode} ({severity}) және {otherRuleCode} ({otherSeverity}) ережелері {tableCode} кестесінің бір аймағына әртүрлі деңгеймен әрекет етеді. Оларға бірдей деңгей беріңіз немесе біреуінің қолдану аймағын тарылтыңыз.'),
    (N'err.ECR-TMPL-4225.requiredNotCovered', N'ru', N'Столбец {tableCode}.{columnCode} обязательный, но его не проверяет ни одно правило и не заполняет ни одна формула: пустое значение останется незамеченным. Добавьте для него правило или сделайте столбец необязательным.'),
    (N'err.ECR-TMPL-4225.requiredNotCovered', N'kz', N'{tableCode}.{columnCode} бағаны міндетті, бірақ оны ешбір ереже тексермейді және ешбір формула толтырмайды: бос мән байқалмай қалады. Оған ереже қосыңыз немесе бағанды міндетті емес етіңіз.'),
    (N'version.publishProblems', N'ru', N'Версия не опубликована. Проблем, которые нужно исправить: {count}'),
    (N'version.publishProblems', N'kz', N'Нұсқа жарияланбады. Түзету керек мәселелер: {count}')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:a201pub ── кінець секції ──
-- COLL:a201pub ── A2-01: перелік проблем публікації шаблону читабельний (ключі діагностик 4224/4225 + заголовок переліку); ru/kz — порцією COLL:a201pub у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-TMPL-4224.severityConflict', N'en', N'Rules {ruleCode} ({severity}) and {otherRuleCode} ({otherSeverity}) apply to the same area of table {tableCode} with different severity levels. Give them the same level or narrow the scope of one of them.', 1),
    (N'err.ECR-TMPL-4225.requiredNotCovered', N'en', N'Column {tableCode}.{columnCode} is required, but no rule checks it and no formula fills it: an empty value would go unnoticed. Add a rule for it or make the column optional.', 1),
    (N'version.publishProblems', N'en', N'The version was not published. Problems to fix: {count}', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:a201pub ── кінець секції ──
