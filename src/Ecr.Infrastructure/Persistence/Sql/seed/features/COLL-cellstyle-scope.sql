-- COLL:cellstyle-scope ── ru/kz: CELL-STYLE «область», оформление строк × столбцов одним запросом; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CELL-4224.scopeRows', N'ru', N'Оформление по области: передайте либо список ключей строк, либо «все строки» — не оба и не ни одного.'),
    (N'err.ECR-CELL-4224.scopeRows', N'kz', N'Аймақ бойынша форматтау: жол кілттерінің тізімін немесе «барлық жолдарды» беріңіз — екеуін де емес, біреуін де емес.'),
    (N'err.ECR-CELL-4224.scopeColumns', N'ru', N'Оформление по области: передайте либо список кодов столбцов, либо «все столбцы» — не оба и не ни одного.'),
    (N'err.ECR-CELL-4224.scopeColumns', N'kz', N'Аймақ бойынша форматтау: баған кодтарының тізімін немесе «барлық бағандарды» беріңіз — екеуін де емес, біреуін де емес.'),
    (N'err.ECR-CELL-4224.scopeInvalidColor', N'ru', N'Оформление по области: цвет должен быть записан как #rrggbb.'),
    (N'err.ECR-CELL-4224.scopeInvalidColor', N'kz', N'Аймақ бойынша форматтау: түс #rrggbb түрінде жазылуы керек.'),
    (N'err.ECR-CELL-4224.scopeEmptyChange', N'ru', N'Оформление по области: нечего применять. Задайте заливку, цвет текста или жирность либо сбросьте оформление.'),
    (N'err.ECR-CELL-4224.scopeEmptyChange', N'kz', N'Аймақ бойынша форматтау: қолданатын ештеңе жоқ. Толтыруды, мәтін түсін немесе қалың қаріпті беріңіз не форматтауды тастаңыз.'),
    (N'err.ECR-CELL-4224.tooManyCells', N'ru', N'Область охватывает {count} ячеек; одним запросом можно оформить не больше {max}. Оформите таблицу частями.'),
    (N'err.ECR-CELL-4224.tooManyCells', N'kz', N'Аймақ {count} ұяшықты қамтиды; бір сұрауда ең көбі {max} ұяшықты форматтауға болады. Кестені бөліп форматтаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:cellstyle-scope ── кінець секції ──

-- COLL:cellstyle-scope ── CELL-STYLE «область»: фарбування рядків × колонок одним запитом; ru/kz — порцією COLL:cellstyle-scope нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CELL-4224.scopeRows', N'en', N'Formatting by area: pass either a list of row keys or "all rows", not both and not neither.', 1),
    (N'err.ECR-CELL-4224.scopeColumns', N'en', N'Formatting by area: pass either a list of column codes or "all columns", not both and not neither.', 1),
    (N'err.ECR-CELL-4224.scopeInvalidColor', N'en', N'Formatting by area: the color must be written as #rrggbb.', 1),
    (N'err.ECR-CELL-4224.scopeEmptyChange', N'en', N'Formatting by area: nothing to apply. Set a fill, a text color or bold, or reset the formatting.', 1),
    (N'err.ECR-CELL-4224.tooManyCells', N'en', N'The area covers {count} cells; one request can format at most {max}. Format the table in parts.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:cellstyle-scope ── кінець секції ──
