-- COLL:an124-rule-refs ── ru/kz: AN-124 / A1-02, A1-03: посилання правил доступу до періоду й валідації — на свою версію/таблицю; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-TMPL-0422.tableNotInSheet', N'ru', N'Таблица {tableDefId} не находится на листе {sheetDefId}: правило с такой парой не охватило бы ни одной ячейки.'),
    (N'err.ECR-TMPL-0422.tableNotInSheet', N'kz', N'{tableDefId} кестесі {sheetDefId} парағында жоқ: мұндай жұбы бар ереже бірде-бір ұяшықты қамтымас еді.'),
    (N'err.ECR-TMPL-0422.sourceColumnNotInRuleTarget', N'ru', N'Столбец-источник {sourceColumnDefId} не является справочным столбцом таблицы правила в версии шаблона {versionId}. Такое правило никогда не нашло бы окно и ничего бы не блокировало.'),
    (N'err.ECR-TMPL-0422.sourceColumnNotInRuleTarget', N'kz', N'{sourceColumnDefId} дереккөз бағаны {versionId} үлгі нұсқасындағы ереже кестесінің анықтамалық бағаны емес. Мұндай ереже ешқашан терезені таппас еді және ештеңені бұғаттамас еді.'),
    (N'err.ECR-TMPL-0422.cloneAccessRuleForeignRef', N'ru', N'Правило доступа к периоду {ruleId} версии {versionId} ссылается на лист, таблицу или столбец вне этой версии. Версию нельзя клонировать, пока правило не исправлено.'),
    (N'err.ECR-TMPL-0422.cloneAccessRuleForeignRef', N'kz', N'{versionId} нұсқасының {ruleId} кезеңге қол жеткізу ережесі осы нұсқадан тыс параққа, кестеге немесе бағанға сілтейді. Ереже түзетілмейінше нұсқаны көшіруге болмайды.'),
    (N'err.ECR-TMPL-0422.validationColumnNotInTable', N'ru', N'Столбца {columnDefId} нет в таблице {tableCode}. Выберите столбец этой таблицы.'),
    (N'err.ECR-TMPL-0422.validationColumnNotInTable', N'kz', N'{tableCode} кестесінде {columnDefId} бағаны жоқ. Осы кестенің бағанын таңдаңыз.'),
    (N'err.ECR-TMPL-0422.cloneValidationRuleForeignColumn', N'ru', N'Правило проверки {ruleCode} таблицы {tableCode} ссылается на столбец {columnDefId} вне этой таблицы; клон применил бы его ко всем столбцам. Версию нельзя клонировать, пока правило не исправлено.'),
    (N'err.ECR-TMPL-0422.cloneValidationRuleForeignColumn', N'kz', N'{tableCode} кестесінің {ruleCode} тексеру ережесі осы кестеден тыс {columnDefId} бағанына сілтейді; көшірме оны барлық бағандарға қолданар еді. Ереже түзетілмейінше нұсқаны көшіруге болмайды.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an124-rule-refs ── кінець секції ──
-- COLL:an124-rule-refs ── AN-124 / A1-02, A1-03: посилання правил доступу до періоду й валідації — на свою версію/таблицю; ru/kz — порцією COLL:an124-rule-refs нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-TMPL-0422.tableNotInSheet', N'en', N'Table {tableDefId} is not on sheet {sheetDefId}: a rule with this pair would not cover a single cell.', 1),
    (N'err.ECR-TMPL-0422.sourceColumnNotInRuleTarget', N'en', N'Source column {sourceColumnDefId} is not a lookup column of the rule''s table in template version {versionId}. Such a rule would never find a window and would block nothing.', 1),
    (N'err.ECR-TMPL-0422.cloneAccessRuleForeignRef', N'en', N'Period access rule {ruleId} of version {versionId} refers to a sheet, table or column outside that version. The version cannot be cloned until the rule is fixed.', 1),
    (N'err.ECR-TMPL-0422.validationColumnNotInTable', N'en', N'Column {columnDefId} is not in table {tableCode}. Choose a column of this table.', 1),
    (N'err.ECR-TMPL-0422.cloneValidationRuleForeignColumn', N'en', N'Validation rule {ruleCode} of table {tableCode} refers to column {columnDefId} outside that table; the clone would apply it to all columns. The version cannot be cloned until the rule is fixed.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an124-rule-refs ── кінець секції ──
