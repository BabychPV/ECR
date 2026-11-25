-- SRCTEST:flert ── ru/kz b14 source-test-flert (людина 25.11, скрин FLERT → Test connection): успіх без каталогу (AC-2), категорії «не налаштовано» / «помилка запиту» (AC-3), відмова запиту каталогу SQL-джерела; власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'integration.test.connectedNoCatalog', N'ru', N'Подключение установлено; каталог сущностей не настроен — источник читается собственными запросами (например, состав газа).'),
    (N'integration.test.connectedNoCatalog', N'kz', N'Қосылым орнатылды; нысандар каталогы бапталмаған — көз өз сұрауларымен оқылады (мысалы, газ құрамы).'),
    (N'integration.test.failed.notConfigured', N'ru', N'Подключение настроено не полностью (строка подключения, политика адресов или запрос источника). Дополните настройки подключения; недостающий параметр назван в журнале сервера.'),
    (N'integration.test.failed.notConfigured', N'kz', N'Қосылым толық бапталмаған (қосылым жолы, мекенжай саясаты немесе көз сұрауы). Қосылым баптауларын толықтырыңыз; жетіспейтін параметр сервер журналында көрсетілген.'),
    (N'integration.test.failed.query', N'ru', N'Подключение установлено, но настроенный запрос источника завершился ошибкой (объект, права или синтаксис). Проверьте запрос; ошибка источника — в журнале сервера.'),
    (N'integration.test.failed.query', N'kz', N'Қосылым орнатылды, бірақ көздің бапталған сұрауы қатемен аяқталды (нысан, құқықтар немесе синтаксис). Сұрауды тексеріңіз; көздің қатесі — сервер журналында.'),
    (N'err.ECR-INT-0422.catalogQueryFailed', N'ru', N'Запрос каталога источника «{dataSource}» завершился ошибкой на стороне источника. Подробности — в журнале сервера.'),
    (N'err.ECR-INT-0422.catalogQueryFailed', N'kz', N'«{dataSource}» көзінің каталог сұрауы көз жағында қатемен аяқталды. Толығырақ — сервер журналында.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- SRCTEST:flert ── кінець секції ──

-- SRCTEST:flert ── en: b14 source-test-flert (AC-2, AC-3); ru/kz — порцією SRCTEST:flert вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'integration.test.connectedNoCatalog', N'en', N'Connected; entity catalog not configured. The source is read by its own queries (for example, gas composition).', 1),
    (N'integration.test.failed.notConfigured', N'en', N'The connection is not fully configured (connection string, address policy or the source query). Complete the connection settings; the server log names the missing setting.', 1),
    (N'integration.test.failed.query', N'en', N'Connected, but the configured query failed on the source (object, permissions or syntax). Check the query; the server log has the source error.', 1),
    (N'err.ECR-INT-0422.catalogQueryFailed', N'en', N'The catalog query of source "{dataSource}" failed on the source side. The server log has the details.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- SRCTEST:flert ── кінець секції ──

-- SRCTEST:flert-title ── заголовок відмови проби: «джерело відхилило підключення» неправда для «не налаштовано» й «помилки запиту» (AC-3) ──
-- Змінені тексти наявних ключів: нові бази беруть нове значення з 00-prelude.sql / CATALOG-en-base.sql; епілог оновлює наявні
-- бази, лише поки там стоїть САМЕ старе значення (правку адміністратора з /admin/ui-strings не затирає).
INSERT INTO #UiChanged ([Key], Lang, OldVal, NewVal)
SELECT v.[Key], v.Lang, v.OldVal, v.NewVal
  FROM (VALUES
    (N'sources.testFailed', N'en', N'The source refused the connection', N'The connection test did not pass'),
    (N'sources.testFailed', N'ru', N'Источник отклонил подключение', N'Проверка подключения не пройдена'),
    (N'sources.testFailed', N'kz', N'Көз қосылымнан бас тартты', N'Қосылымды тексеру өтпеді')
       ) AS v ([Key], Lang, OldVal, NewVal)
OPTION (RECOMPILE);
GO
-- SRCTEST:flert-title ── кінець секції ──
