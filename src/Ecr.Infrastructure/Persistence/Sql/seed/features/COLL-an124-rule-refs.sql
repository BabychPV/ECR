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
