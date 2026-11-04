-- COLL:ent4 ── ru/kz причин змін з'єднання з джерелом (D8); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'integrationAudit.dataSourceCreated', N'ru', N'Подключение «{connection}» создано.'),
    (N'integrationAudit.dataSourceCreated', N'kz', N'«{connection}» қосылымы құрылды.'),
    (N'integrationAudit.dataSourceChanged', N'ru', N'Подключение «{connection}» изменено.'),
    (N'integrationAudit.dataSourceChanged', N'kz', N'«{connection}» қосылымы өзгертілді.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ent4 ── кінець секції ──

-- COLL:ent4 ── Причини змін з'єднання з джерелом (D8) конвертом, а не українською фразою (`SaveDataSourceHandler`, `structureChangeReason.ts`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'integrationAudit.dataSourceCreated', N'en', N'Connection "{connection}" created.', 1),
    (N'integrationAudit.dataSourceChanged', N'en', N'Connection "{connection}" changed.', 1)
    -- ru/kz — окремою порцією `COLL:ent4` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ent4 ── кінець секції ──
