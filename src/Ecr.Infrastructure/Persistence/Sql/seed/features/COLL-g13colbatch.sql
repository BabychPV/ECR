-- COLL:g13colbatch ── ru/kz G13: пакетний запис колонок таблиці; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-TMPL-0422.columnBatchTooLarge', N'ru', N'В пачке столбцов: {count}, а один запрос принимает не больше {max}. Разбейте пачку на несколько запросов.'),
    (N'err.ECR-TMPL-0422.columnBatchTooLarge', N'kz', N'Пакетте {count} баған бар, ал бір сұрау ең көбі {max} қабылдайды. Пакетті бірнеше сұрауға бөліңіз.'),
    (N'err.ECR-TMPL-0422.columnBatchDuplicateCode', N'ru', N'Код столбца «{columnCode}» повторяется в пачке: один код - один столбец.'),
    (N'err.ECR-TMPL-0422.columnBatchDuplicateCode', N'kz', N'«{columnCode}» баған коды пакетте қайталанады: бір код - бір баған.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:g13colbatch ── кінець секції ──

-- COLL:g13colbatch ── G13: пакетний запис колонок таблиці; ru/kz — порцією COLL:g13colbatch нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-TMPL-0422.columnBatchTooLarge', N'en', N'The batch has {count} columns; one request accepts at most {max}. Split it into several requests.', 1),
    (N'err.ECR-TMPL-0422.columnBatchDuplicateCode', N'en', N'Column code "{columnCode}" appears more than once in the batch: one code is one column.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:g13colbatch ── кінець секції ──
