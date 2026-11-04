-- COLL:transport-health ── ru/kz перевірки transport на /health/ready (D14-08); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'health.check.transport', N'ru', N'Транспорт (HTTPS)'),
    (N'health.transport.secure', N'ru', N'Cookie сеанса требует HTTPS (Auth:RequireHttps = true).'),
    (N'health.transport.notProduction', N'ru', N'Auth:RequireHttps = false вне Production (разработка или тест): cookie сеанса не Secure.'),
    (N'health.transport.httpNoSecureCookie', N'ru', N'Транспорт: HTTP — cookie сеанса не Secure (Auth:RequireHttps = false). Допустимо только на стенде; на рабочей площадке включите HTTPS (руководство по установке, раздел «HTTPS»).'),
    (N'health.transport.certificateExpiresSoon', N'ru', N'Сертификат HTTPS истекает через {days} дн. ({date}). Установите обновлённый сертификат и повторите deploy-ecr.ps1 с новым -HttpsThumbprint.'),
    (N'health.transport.certificateExpired', N'ru', N'Сертификат HTTPS истёк {date}: браузеры откажутся открывать приложение. Установите обновлённый сертификат и повторите deploy-ecr.ps1 с новым -HttpsThumbprint.'),
    (N'health.check.transport', N'kz', N'Тасымал (HTTPS)'),
    (N'health.transport.secure', N'kz', N'Сеанс cookie-ісі HTTPS талап етеді (Auth:RequireHttps = true).'),
    (N'health.transport.notProduction', N'kz', N'Auth:RequireHttps = false Production-нан тыс (әзірлеу немесе сынақ): сеанс cookie-і Secure емес.'),
    (N'health.transport.httpNoSecureCookie', N'kz', N'Тасымал: HTTP — сеанс cookie-і Secure емес (Auth:RequireHttps = false). Тек стендте рұқсат етіледі; жұмыс алаңында HTTPS қосыңыз (орнату нұсқаулығы, «HTTPS» бөлімі).'),
    (N'health.transport.certificateExpiresSoon', N'kz', N'HTTPS сертификатының мерзімі {days} күннен кейін аяқталады ({date}). Жаңартылған сертификатты орнатып, deploy-ecr.ps1 файлын жаңа -HttpsThumbprint арқылы қайталаңыз.'),
    (N'health.transport.certificateExpired', N'kz', N'HTTPS сертификатының мерзімі {date} күні аяқталды: браузерлер қолданбаны ашудан бас тартады. Жаңартылған сертификатты орнатып, deploy-ecr.ps1 файлын жаңа -HttpsThumbprint арқылы қайталаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:transport-health ── кінець секції ──

-- COLL:transport-health ── Перевірка `transport` на /health/ready (D14-08, `TransportHealthCheck`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'health.check.transport',                 N'en', N'Transport (HTTPS)', 1),
    (N'health.transport.secure',                N'en', N'The session cookie requires HTTPS (Auth:RequireHttps = true).', 1),
    (N'health.transport.notProduction',         N'en', N'Auth:RequireHttps = false outside Production (development or test): the session cookie is not Secure.', 1),
    (N'health.transport.httpNoSecureCookie',    N'en', N'Transport: HTTP - the session cookie is not Secure (Auth:RequireHttps = false). Use this only on a stand; on a production site enable HTTPS (install guide, section HTTPS).', 1),
    (N'health.transport.certificateExpiresSoon', N'en', N'The HTTPS certificate expires in {days} day(s) ({date}). Install a renewed certificate and repeat deploy-ecr.ps1 with the new -HttpsThumbprint.', 1),
    (N'health.transport.certificateExpired',    N'en', N'The HTTPS certificate expired on {date}: browsers refuse to open the application. Install a renewed certificate and repeat deploy-ecr.ps1 with the new -HttpsThumbprint.', 1)
    -- ru/kz — окремою порцією `COLL:transport-health` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:transport-health ── кінець секції ──
