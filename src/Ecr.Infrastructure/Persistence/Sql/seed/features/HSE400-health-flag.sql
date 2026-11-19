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
    (N'health.hse400CompositionOff.text', N'ru', N'Чтение состава газа из документа выключено: бит ReadFromDocument связи источника HSE400 → Land (HSE400_LAND_COMPOSITION) равен 0 или связи ещё нет, а опубликованный шаблон HSE400 и документ HSE400 за текущий год существуют. Установите ReadFromDocument = 1 в связи (DBA, таблица cfg.DocumentSourceBinding): действует без перезапуска. Отсутствующая связь создаётся при следующем запуске службы.'),
    (N'health.hse400CompositionOff.text', N'kz', N'Газ құрамын құжаттан оқу өшірулі: HSE400 → Land дереккөз байланысының (HSE400_LAND_COMPOSITION) ReadFromDocument биті 0-ге тең немесе байланыс әлі жоқ, ал жарияланған HSE400 үлгісі мен ағымдағы жылдың HSE400 құжаты бар. Байланыста ReadFromDocument = 1 орнатыңыз (DBA, cfg.DocumentSourceBinding кестесі): қызметті қайта іске қосу қажет емес. Жоқ байланыс қызмет келесі рет іске қосылғанда жасалады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- HSE400:health-flag ── кінець секції ──
-- HSE400:health-flag ── B105 → P-0118-31/33: біт зв'язки ReadFromDocument у фактах і попередження (ключ виведено); ru/kz — порцією HSE400:health-flag вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'health.facts.hse400Composition', N'en', N'Land: HSE400 gas composition from document', 1),
    (N'health.facts.hse400Composition.on', N'en', N'On', 1),
    (N'health.facts.hse400Composition.off', N'en', N'Off', 1),
    (N'health.hse400CompositionOff.title', N'en', N'Land reads the gas composition from the registry, not from the HSE400 document', 1),
    (N'health.hse400CompositionOff.text', N'en', N'Reading the gas composition from the document is off: the ReadFromDocument bit of the HSE400 → Land source binding (HSE400_LAND_COMPOSITION) is 0, or the binding does not exist yet, while a published HSE400 template and an HSE400 document for the current year exist. Set ReadFromDocument = 1 on the binding (DBA, table cfg.DocumentSourceBinding): it takes effect without a restart. A missing binding is created on the next service start.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- HSE400:health-flag ── кінець секції ──
-- HSE400:health-flag-text ── B183 (P-0118-31/33): ключ Calculations:Hse400CompositionFromDocument виведено, текст попередження — про біт зв'язки ReadFromDocument ──
-- Змінені тексти наявних ключів: нові бази беруть нове значення з порцій HSE400:health-flag вище; епілог оновлює наявні
-- бази, лише поки там стоїть САМЕ старе значення (правку адміністратора з /admin/ui-strings не затирає).
INSERT INTO #UiChanged ([Key], Lang, OldVal, NewVal)
SELECT v.[Key], v.Lang, v.OldVal, v.NewVal
  FROM (VALUES
    (N'health.hse400CompositionOff.text', N'en', N'The key Calculations:Hse400CompositionFromDocument is off, but a published HSE400 template and an HSE400 document for the current year exist. Set the key persistently in the site configuration and restart the service.', N'Reading the gas composition from the document is off: the ReadFromDocument bit of the HSE400 → Land source binding (HSE400_LAND_COMPOSITION) is 0, or the binding does not exist yet, while a published HSE400 template and an HSE400 document for the current year exist. Set ReadFromDocument = 1 on the binding (DBA, table cfg.DocumentSourceBinding): it takes effect without a restart. A missing binding is created on the next service start.'),
    (N'health.hse400CompositionOff.text', N'ru', N'Ключ Calculations:Hse400CompositionFromDocument выключен, но опубликованный шаблон HSE400 и документ HSE400 за текущий год существуют. Задайте ключ постоянно в конфигурации площадки и перезапустите службу.', N'Чтение состава газа из документа выключено: бит ReadFromDocument связи источника HSE400 → Land (HSE400_LAND_COMPOSITION) равен 0 или связи ещё нет, а опубликованный шаблон HSE400 и документ HSE400 за текущий год существуют. Установите ReadFromDocument = 1 в связи (DBA, таблица cfg.DocumentSourceBinding): действует без перезапуска. Отсутствующая связь создаётся при следующем запуске службы.'),
    (N'health.hse400CompositionOff.text', N'kz', N'Calculations:Hse400CompositionFromDocument кілті өшірулі, бірақ жарияланған HSE400 үлгісі мен ағымдағы жылдың HSE400 құжаты бар. Кілтті алаң конфигурациясында тұрақты түрде орнатып, қызметті қайта іске қосыңыз.', N'Газ құрамын құжаттан оқу өшірулі: HSE400 → Land дереккөз байланысының (HSE400_LAND_COMPOSITION) ReadFromDocument биті 0-ге тең немесе байланыс әлі жоқ, ал жарияланған HSE400 үлгісі мен ағымдағы жылдың HSE400 құжаты бар. Байланыста ReadFromDocument = 1 орнатыңыз (DBA, cfg.DocumentSourceBinding кестесі): қызметті қайта іске қосу қажет емес. Жоқ байланыс қызмет келесі рет іске қосылғанда жасалады.')
       ) AS v ([Key], Lang, OldVal, NewVal)
OPTION (RECOMPILE);
GO
-- HSE400:health-flag-text ── кінець секції ──
