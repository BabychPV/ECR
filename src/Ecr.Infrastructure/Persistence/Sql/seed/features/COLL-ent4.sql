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

