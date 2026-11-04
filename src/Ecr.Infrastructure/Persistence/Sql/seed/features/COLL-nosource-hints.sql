-- COLL:nosource-hints ── ru/kz підказки порожнього списку з'єднань; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'sources.notConfiguredHint', N'ru', N'Чтобы подключить PI, администратор добавляет здесь подключение: адрес PI Web API, секрет и способ аутентификации. До этого данные вводятся вручную. См. docs/admin/admin-guide.md, раздел 5, и docs/admin/operations-runbook.md, раздел 15.'),
    (N'sources.notConfiguredHint', N'kz', N'PI-ды қосу үшін әкімші осы жерде қосылым қосады: PI Web API мекенжайы, құпия және аутентификация тәсілі. Ол уақытқа дейін деректер қолмен енгізіледі. docs/admin/admin-guide.md құжатының 5-бөлімін және docs/admin/operations-runbook.md құжатының 15-бөлімін қараңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:nosource-hints ── кінець секції ──

-- COLL:nosource-hints ── порожній стан списку з'єднань: що робити й де в документації (UI, DataSourcesTable) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'sources.notConfiguredHint', N'en', N'To connect PI, an administrator adds a connection here: the PI Web API address, the secret and the authentication. Until then data is entered by hand. See docs/admin/admin-guide.md, section 5, and docs/admin/operations-runbook.md, section 15.', 1)
    -- ru/kz — окремою порцією `I18N` (COLL:nosource-hints).
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:nosource-hints ── кінець секції ──
