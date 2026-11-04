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
-- PERMITS: type-usage ── en: PM-03b2, кількості дозволів і рядків у діалозі «Remove table»; ru/kz — порцією PERMITS: type-usage нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'permits.editor.deleteTable.checking', N'en', N'Checking what this table holds…', 1),
    (N'permits.editor.deleteTable.usagePublished', N'en', N'Permits of this type: {permits}. Rows in this published table: {rows}. They stay untouched: a published table cannot be removed.', 1),
    (N'permits.editor.deleteTable.usageDraftOnly', N'en', N'This table is not published yet: none of the {permits} permits of this type holds rows in it.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERMITS: type-usage ── кінець секції ──
