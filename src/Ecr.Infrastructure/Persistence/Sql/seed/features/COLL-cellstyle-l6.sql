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
