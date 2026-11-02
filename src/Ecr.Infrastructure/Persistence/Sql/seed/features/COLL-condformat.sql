-- COLL:condformat ── ru/kz відмов PUT …/conditional-formats (ФВ-2.6/2.7); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CFG-0422.condFormatOperator', N'ru', N'Правило условного форматирования {index} (столбец {columnCode}): неизвестный оператор {operator}.'),
    (N'err.ECR-CFG-0422.condFormatOperator', N'kz', N'Шартты пішімдеу ережесі {index} ({columnCode} бағаны): {operator} операторы белгісіз.'),
    (N'err.ECR-CFG-0422.condFormatOperand', N'ru', N'Правило условного форматирования {index} (столбец {columnCode}): оператору {operator} нужны числовые значения до 64 символов.'),
    (N'err.ECR-CFG-0422.condFormatOperand', N'kz', N'Шартты пішімдеу ережесі {index} ({columnCode} бағаны): {operator} операторына 64 таңбаға дейінгі сандық мәндер қажет.'),
    (N'err.ECR-CFG-0422.condFormatColor', N'ru', N'Правило условного форматирования {index} (столбец {columnCode}): цвет должен быть #rrggbb.'),
    (N'err.ECR-CFG-0422.condFormatColor', N'kz', N'Шартты пішімдеу ережесі {index} ({columnCode} бағаны): түс #rrggbb форматында болуы керек.'),
    (N'err.ECR-CFG-0422.condFormatColumn', N'ru', N'Правило условного форматирования {index}: столбца {columnCode} нет в этой версии шаблона.'),
    (N'err.ECR-CFG-0422.condFormatColumn', N'kz', N'Шартты пішімдеу ережесі {index}: {columnCode} бағаны осы үлгі нұсқасында жоқ.'),
    (N'err.ECR-CFG-0422.condFormatLimit', N'ru', N'Слишком много правил условного форматирования: не более {max} на версию шаблона.'),
    (N'err.ECR-CFG-0422.condFormatLimit', N'kz', N'Шартты пішімдеу ережелері тым көп: үлгі нұсқасына {max}-тен көп емес.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:condformat ── кінець секції ──
