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
