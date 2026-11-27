-- COLL:nosource-hints ── ru/kz підказки порожнього списку з'єднань; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'sources.notConfiguredHint', N'ru', N'Чтобы подключить источник данных, администратор добавляет здесь подключение: PI Web API, PI SQL Client или SQL — с адресом и учётными данными. До этого данные вводятся вручную. Подробности — в руководстве администратора.'),
    (N'sources.notConfiguredHint', N'kz', N'Дереккөзді қосу үшін әкімші осы жерде қосылым қосады: PI Web API, PI SQL Client немесе SQL — мекенжайы мен тіркелгі деректерімен. Ол уақытқа дейін деректер қолмен енгізіледі. Толығырақ — әкімші нұсқаулығында.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:nosource-hints ── кінець секції ──

-- COLL:nosource-hints ── порожній стан списку з'єднань: що робити й де в документації (UI, DataSourcesTable) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'sources.notConfiguredHint', N'en', N'To connect a data source, an administrator adds a connection here: PI Web API, PI SQL Client or SQL, with its address and credentials. Until then data is entered by hand. Details are in the administrator guide.', 1)
    -- ru/kz — окремою порцією `I18N` (COLL:nosource-hints).
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:nosource-hints ── кінець секції ──
