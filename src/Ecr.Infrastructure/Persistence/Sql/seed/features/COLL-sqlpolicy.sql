-- COLL:sqlpolicy ── ru/kz політики адреси PiSqlClient; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0422.dataSourceEndpointSqlScheme', N'ru', N'Адрес источника PiSqlClient должен быть именем сервера (server, server\instance, server,port или host:port), а не URL со схемой.'),
    (N'err.ECR-REQ-0422.dataSourceEndpointSqlScheme', N'kz', N'PiSqlClient көзінің мекенжайы схемасы бар URL емес, сервер аты болуы тиіс (server, server\instance, server,port немесе host:port).'),
    (N'err.ECR-REQ-0422.dataSourceEndpointSqlLinkLocal', N'ru', N'Этот адрес сервера не допускается: link-local и облачные metadata-адреса (169.254.0.0/16, fe80::/10) отклоняются.'),
    (N'err.ECR-REQ-0422.dataSourceEndpointSqlLinkLocal', N'kz', N'Бұл сервер мекенжайы рұқсат етілмейді: link-local және бұлттық metadata мекенжайлары (169.254.0.0/16, fe80::/10) қабылданбайды.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:sqlpolicy ── кінець секції ──

-- COLL:sqlpolicy ── Політика адреси джерела PiSqlClient: ім'я сервера, а не URL; link-local заборонено ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REQ-0422.dataSourceEndpointSqlScheme',    N'en', N'A PiSqlClient source address must be a server name (server, server\instance, server,port or host:port), not a URL with a scheme.', 1),
    (N'err.ECR-REQ-0422.dataSourceEndpointSqlLinkLocal', N'en', N'This server address is not allowed: link-local and cloud metadata addresses (169.254.0.0/16, fe80::/10) are refused.', 1)
    -- ru/kz — окремою порцією `COLL:sqlpolicy` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:sqlpolicy ── кінець секції ──
