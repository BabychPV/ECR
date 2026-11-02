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

