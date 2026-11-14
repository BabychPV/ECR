-- HSE400:health-flag ── ru/kz B105: прапор складу HSE400 на сторінці «Здоров'я системи»; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'health.facts.hse400Composition', N'ru', N'Land: состав газа из документа HSE400'),
    (N'health.facts.hse400Composition', N'kz', N'Land: газ құрамы HSE400 құжатынан'),
    (N'health.facts.hse400Composition.on', N'ru', N'Включено'),
    (N'health.facts.hse400Composition.on', N'kz', N'Қосулы'),
    (N'health.facts.hse400Composition.off', N'ru', N'Выключено'),
    (N'health.facts.hse400Composition.off', N'kz', N'Өшірулі'),
    (N'health.hse400CompositionOff.title', N'ru', N'Land читает состав газа из справочника, а не из документа HSE400'),
    (N'health.hse400CompositionOff.title', N'kz', N'Land газ құрамын HSE400 құжатынан емес, анықтамалықтан оқиды'),
    (N'health.hse400CompositionOff.text', N'ru', N'Ключ Calculations:Hse400CompositionFromDocument выключен, но опубликованный шаблон HSE400 и документ HSE400 за текущий год существуют. Задайте ключ постоянно в конфигурации площадки и перезапустите службу.'),
    (N'health.hse400CompositionOff.text', N'kz', N'Calculations:Hse400CompositionFromDocument кілті өшірулі, бірақ жарияланған HSE400 үлгісі мен ағымдағы жылдың HSE400 құжаты бар. Кілтті алаң конфигурациясында тұрақты түрде орнатып, қызметті қайта іске қосыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- HSE400:health-flag ── кінець секції ──
-- HSE400:health-flag ── B105: прапор Calculations:Hse400CompositionFromDocument у фактах і попередження; ru/kz — порцією HSE400:health-flag вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'health.facts.hse400Composition', N'en', N'Land: HSE400 gas composition from document', 1),
    (N'health.facts.hse400Composition.on', N'en', N'On', 1),
    (N'health.facts.hse400Composition.off', N'en', N'Off', 1),
    (N'health.hse400CompositionOff.title', N'en', N'Land reads the gas composition from the registry, not from the HSE400 document', 1),
    (N'health.hse400CompositionOff.text', N'en', N'The key Calculations:Hse400CompositionFromDocument is off, but a published HSE400 template and an HSE400 document for the current year exist. Set the key persistently in the site configuration and restart the service.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- HSE400:health-flag ── кінець секції ──
