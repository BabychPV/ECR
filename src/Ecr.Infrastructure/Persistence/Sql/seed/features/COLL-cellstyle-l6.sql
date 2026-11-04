-- COLL:cellstyle-l6 ── ru/kz: CELL-STYLE L6 (12.10), правила условного форматирования по строкам/тексту; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CFG-0422.condFormatScope', N'ru', N'Правило условного форматирования {index} (столбец {columnCode}): область должна быть «column» или «row».'),
    (N'err.ECR-CFG-0422.condFormatScope', N'kz', N'Шартты пішімдеу ережесі {index} ({columnCode} бағаны): аймақ «column» немесе «row» болуы керек.'),
    (N'err.ECR-CFG-0422.condFormatTable', N'ru', N'Правило условного форматирования {index} (столбец {columnCode}): код таблицы не задан или такой таблицы нет в этой версии шаблона. Область «строки» и столбец «все столбцы» требуют таблицу.'),
    (N'err.ECR-CFG-0422.condFormatTable', N'kz', N'Шартты пішімдеу ережесі {index} ({columnCode} бағаны): кесте коды көрсетілмеген немесе үлгінің бұл нұсқасында мұндай кесте жоқ. «Жолдар» аймағы мен «барлық бағандар» бағаны кестені талап етеді.'),
    (N'err.ECR-CFG-0422.condFormatRowKeys', N'ru', N'Правило условного форматирования {index} (столбец {columnCode}): область «строки» требует от 1 до 500 различных ключей строк, существующих в таблице; область «column» их не имеет.'),
    (N'err.ECR-CFG-0422.condFormatRowKeys', N'kz', N'Шартты пішімдеу ережесі {index} ({columnCode} бағаны): «жолдар» аймағына кестеде бар 1-ден 500-ге дейін әртүрлі жол кілті қажет; «column» аймағында олар болмауы керек.'),
    (N'err.ECR-CFG-0422.condFormatRef', N'ru', N'Правило условного форматирования {index} (столбец {columnCode}): столбца, значение которого проверяется, нет в этой версии шаблона или в таблице.'),
    (N'err.ECR-CFG-0422.condFormatRef', N'kz', N'Шартты пішімдеу ережесі {index} ({columnCode} бағаны): мәні тексерілетін баған үлгінің бұл нұсқасында немесе кестеде жоқ.'),
    (N'err.ECR-CFG-0422.condFormatPattern', N'ru', N'Правило условного форматирования {index} (столбец {columnCode}): штриховка должна быть «hatch» или пустой.'),
    (N'err.ECR-CFG-0422.condFormatPattern', N'kz', N'Шартты пішімдеу ережесі {index} ({columnCode} бағаны): штрихтау «hatch» немесе бос болуы керек.'),
    (N'err.ECR-CFG-0422.condFormatTextOperand', N'ru', N'Правило условного форматирования {index} (столбец {columnCode}): оператору {operator} нужно непустое текстовое значение до 64 символов.'),
    (N'err.ECR-CFG-0422.condFormatTextOperand', N'kz', N'Шартты пішімдеу ережесі {index} ({columnCode} бағаны): {operator} операторына 64 таңбаға дейінгі бос емес мәтіндік мән қажет.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:cellstyle-l6 ── кінець секції ──
-- COLL:cellstyle-l6 ── CELL-STYLE L6 (12.10): умовне форматування за областю рядків / текстом / статусом рядка; ru/kz — порцією COLL:cellstyle-l6 нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CFG-0422.condFormatScope', N'en', N'Conditional format rule {index} (column {columnCode}): the scope must be "column" or "row".', 1),
    (N'err.ECR-CFG-0422.condFormatTable', N'en', N'Conditional format rule {index} (column {columnCode}): the table code is missing or this template version has no such table. A row scope and the "all columns" column both need a table.', 1),
    (N'err.ECR-CFG-0422.condFormatRowKeys', N'en', N'Conditional format rule {index} (column {columnCode}): a row scope needs 1 to 500 distinct row keys that exist in the table; a column scope must have none.', 1),
    (N'err.ECR-CFG-0422.condFormatRef', N'en', N'Conditional format rule {index} (column {columnCode}): the column whose value is tested does not exist in this template version or in the table.', 1),
    (N'err.ECR-CFG-0422.condFormatPattern', N'en', N'Conditional format rule {index} (column {columnCode}): the pattern must be "hatch" or empty.', 1),
    (N'err.ECR-CFG-0422.condFormatTextOperand', N'en', N'Conditional format rule {index} (column {columnCode}): operator {operator} needs a non-empty text value of up to 64 characters.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:cellstyle-l6 ── кінець секції ──
