-- COLL:health-dpkeys ── ru/kz обмеження БД «ключі сеансів без шифрування»; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'health.db.limitation.dataProtectionKeys', N'ru', N'Ключи сеансов хранятся в sec.DataProtectionKey без шифрования: сертификат не настроен (Auth:DataProtection:CertificateThumbprint). Ограничьте доступ к таблице учётной записью службы, запретив (DENY) всем остальным.'),
    (N'health.db.limitation.dataProtectionKeys', N'kz', N'Сеанс кілттері sec.DataProtectionKey кестесінде шифрланбай сақталады: сертификат бапталмаған (Auth:DataProtection:CertificateThumbprint). Кестеге қолжетімділікті қызмет тіркелгісімен шектеңіз, қалғандарының бәріне DENY қойыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:health-dpkeys ── кінець секції ──

-- COLL:health-dpkeys ── Обмеження БД: ключі сеансів без сертифіката (`DatabaseHealthCheck`, запасний текст `UnprotectedKeysFallback`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'health.db.limitation.dataProtectionKeys', N'en', N'Session keys are stored unencrypted in sec.DataProtectionKey: no certificate is configured (Auth:DataProtection:CertificateThumbprint). Restrict the table to the service account with DENY for everyone else.', 1)
    -- ru/kz — окремою порцією `COLL:health-dpkeys` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:health-dpkeys ── кінець секції ──
