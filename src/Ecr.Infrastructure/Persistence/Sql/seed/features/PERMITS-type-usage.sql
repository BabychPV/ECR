-- PERMITS: type-usage ── ru/kz: PM-03b2, кількості дозволів і рядків у діалозі «Remove table»; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'permits.editor.deleteTable.checking', N'ru', N'Проверяем, что хранит эта таблица…'),
    (N'permits.editor.deleteTable.checking', N'kz', N'Бұл кесте нені сақтайтыны тексерілуде…'),
    (N'permits.editor.deleteTable.usagePublished', N'ru', N'Разрешений этого типа: {permits}. Строк в этой опубликованной таблице: {rows}. Они остаются нетронутыми: опубликованную таблицу удалить нельзя.'),
    (N'permits.editor.deleteTable.usagePublished', N'kz', N'Осы типтегі рұқсаттар: {permits}. Осы жарияланған кестедегі жолдар: {rows}. Олар қозғалмайды: жарияланған кестені жоюға болмайды.'),
    (N'permits.editor.deleteTable.usageDraftOnly', N'ru', N'Эта таблица ещё не опубликована: ни в одном из {permits} разрешений этого типа в ней нет строк.'),
    (N'permits.editor.deleteTable.usageDraftOnly', N'kz', N'Бұл кесте әлі жарияланбаған: осы типтегі {permits} рұқсаттың ешқайсысында онда жол жоқ.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: type-usage ── кінець секції ──
