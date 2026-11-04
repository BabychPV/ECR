-- COLL:reqclose3-tmplkeys ── ru/kz; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-TMPL-4228.fixedTableWithoutRows', N'ru', N'Таблица {tableCode} фиксированная (строки определены в шаблоне), но в ней нет ни одной строки. В опубликованной форме это пустая таблица, в которую оператор не сможет ввести ни одного значения.'),
    (N'err.ECR-TMPL-4228.fixedTableWithoutRows', N'kz', N'{tableCode} кестесі тұрақты (жолдар үлгіде анықталған), бірақ онда бірде-бір жол жоқ. Жарияланған пішінде бұл оператор ешқандай мән енгізе алмайтын бос кесте болады.'),
    (N'err.ECR-TMPL-4229', N'ru', N'Некорректные группы заголовков столбцов'),
    (N'err.ECR-TMPL-4229', N'kz', N'Баған тақырыптарының топтары қате'),
    (N'err.ECR-TMPL-4229.duplicateGroupCode', N'ru', N'Группа заголовков {groupCode} таблицы {tableCode} встречается более одного раза: коды групп должны быть уникальны в пределах таблицы.'),
    (N'err.ECR-TMPL-4229.parentNotFound', N'ru', N'Родительской группы {parentCode} для группы {groupCode} нет в таблице {tableCode}: группу можно вложить только в группу той же таблицы.'),
    (N'err.ECR-TMPL-4229.cycle', N'ru', N'Группа заголовков {groupCode} таблицы {tableCode} входит в цикл: группу нельзя вложить в саму себя.'),
    (N'err.ECR-TMPL-4229.depthExceeded', N'ru', N'Группа заголовков {groupCode} таблицы {tableCode} вложена глубже {maxDepth} уровней.'),
    (N'err.ECR-TMPL-4229.unknownGroup', N'ru', N'Столбец {columnCode} таблицы {tableCode} ссылается на группу заголовков {groupCode}, которой нет в этой таблице.'),
    (N'err.ECR-TMPL-4229.notContiguous', N'ru', N'Столбцы группы заголовков {groupCode} в таблице {tableCode} должны идти подряд: между ними стоит другой столбец. Переставьте столбцы так, чтобы группа была непрерывной.'),
    (N'err.ECR-TMPL-4229.duplicateGroupCode', N'kz', N'{tableCode} кестесінің {groupCode} тақырып тобы бірнеше рет кездеседі: топ кодтары кесте ішінде бірегей болуы керек.'),
    (N'err.ECR-TMPL-4229.parentNotFound', N'kz', N'{groupCode} тобының {parentCode} аталық тобы {tableCode} кестесінде жоқ: топты тек сол кестенің тобына ғана ендіруге болады.'),
    (N'err.ECR-TMPL-4229.cycle', N'kz', N'{tableCode} кестесінің {groupCode} тақырып тобы циклге кіреді: топты өзіне ендіруге болмайды.'),
    (N'err.ECR-TMPL-4229.depthExceeded', N'kz', N'{tableCode} кестесінің {groupCode} тақырып тобы {maxDepth} деңгейден терең ендірілген.'),
    (N'err.ECR-TMPL-4229.unknownGroup', N'kz', N'{tableCode} кестесінің {columnCode} бағаны осы кестеде жоқ {groupCode} тақырып тобына сілтеме жасайды.'),
    (N'err.ECR-TMPL-4229.notContiguous', N'kz', N'{tableCode} кестесіндегі {groupCode} тақырып тобының бағандары қатар тұруы керек: олардың арасында басқа баған бар. Топ үздіксіз болуы үшін бағандардың ретін өзгертіңіз.'),
    (N'err.ECR-TMPL-4226.calculatedWithoutSource', N'ru', N'Столбец {tableCode}.{columnCode} имеет тип {dataType}, то есть должен считаться формулой шаблона, но в этой версии формулы нет. В опубликованной форме это пустая ячейка, которую оператор не вправе заполнить.'),
    (N'err.ECR-TMPL-4226.calculatedWithoutSource', N'kz', N'{tableCode}.{columnCode} бағанының түрі {dataType}, яғни ол үлгі формуласымен есептелуі тиіс, бірақ бұл нұсқада формула жоқ. Жарияланған пішінде бұл оператор толтыруға құқығы жоқ бос ұяша болады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:reqclose3-tmplkeys ── кінець секції ──

-- COLL:reqclose3-tmplkeys ── див. ключі нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-TMPL-4228.fixedTableWithoutRows', N'en', N'Table {tableCode} is fixed (its rows are defined in the template) but has no rows. In the published form it would be an empty table the operator cannot enter anything into.', 1),
    -- CG:colgroups ── групи заголовків колонок (ECR-TMPL-4229); ru/kz — у блоці I18N нижче.
    (N'err.ECR-TMPL-4229',  N'en', N'Invalid column header groups', 1),
    (N'err.ECR-TMPL-4229.duplicateGroupCode', N'en', N'Header group {groupCode} of table {tableCode} appears more than once: group codes must be unique within a table.', 1),
    (N'err.ECR-TMPL-4229.parentNotFound', N'en', N'Parent group {parentCode} of group {groupCode} does not exist in table {tableCode}: a group can only be nested in a group of the same table.', 1),
    (N'err.ECR-TMPL-4229.cycle', N'en', N'Header group {groupCode} of table {tableCode} is part of a cycle: a group cannot be nested in itself.', 1),
    (N'err.ECR-TMPL-4229.depthExceeded', N'en', N'Header group {groupCode} of table {tableCode} is nested deeper than {maxDepth} levels.', 1),
    (N'err.ECR-TMPL-4229.unknownGroup', N'en', N'Column {columnCode} of table {tableCode} refers to header group {groupCode}, which does not exist in this table.', 1),
    (N'err.ECR-TMPL-4229.notContiguous', N'en', N'The columns of header group {groupCode} in table {tableCode} must be adjacent: another column sits between them. Reorder the columns so the group is contiguous.', 1),
    (N'err.ECR-TMPL-4226.calculatedWithoutSource', N'en', N'Column {tableCode}.{columnCode} has type {dataType}, so it must be computed by a template formula, but this version has none. In the published form it would be an empty cell the operator may not fill in.', 1)
    -- ru/kz — окремою порцією $Section у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:reqclose3-tmplkeys ── кінець секції ──
