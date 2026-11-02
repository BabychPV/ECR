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
-- COLL:condformat ── Правила умовного форматування версії шаблону: відмови PUT …/conditional-formats (ФВ-2.6/2.7, `ConditionalFormatRule`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CFG-0422.condFormatOperator', N'en', N'Conditional format rule {index} (column {columnCode}): unknown operator {operator}.', 1),
    (N'err.ECR-CFG-0422.condFormatOperand', N'en', N'Conditional format rule {index} (column {columnCode}): operator {operator} needs numeric value(s) up to 64 characters.', 1),
    (N'err.ECR-CFG-0422.condFormatColor', N'en', N'Conditional format rule {index} (column {columnCode}): colour must be #rrggbb.', 1),
    (N'err.ECR-CFG-0422.condFormatColumn', N'en', N'Conditional format rule {index}: column {columnCode} does not exist in this template version.', 1),
    (N'err.ECR-CFG-0422.condFormatLimit', N'en', N'Too many conditional format rules: at most {max} per template version.', 1)
    -- ru/kz — окремою порцією `COLL:condformat` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:condformat ── кінець секції ──
