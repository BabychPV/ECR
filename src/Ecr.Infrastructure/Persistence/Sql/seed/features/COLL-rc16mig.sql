-- COLL:rc16mig ── ru/kz D-2 RC15B: перенос версії шаблону фоновою задачею; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'jobs.kind.migrateDocumentVersion', N'ru', N'Перенос на версию шаблона'),
    (N'jobs.kind.migrateDocumentVersion', N'kz', N'Үлгі нұсқасына көшіру'),
    (N'jobs.migrateStarted', N'ru', N'Перенос запущен'),
    (N'jobs.migrateStarted', N'kz', N'Көшіру басталды'),
    (N'jobs.migratePlanning', N'ru', N'Проверка проекта и планирование переноса'),
    (N'jobs.migratePlanning', N'kz', N'Жобаны тексеру және көшіруді жоспарлау'),
    (N'jobs.migrateCells', N'ru', N'Перенос значений ячеек и строк (изменено строк: {rows})'),
    (N'jobs.migrateCells', N'kz', N'Ұяшық мәндері мен жолдарды көшіру (өзгертілген жолдар: {rows})'),
    (N'jobs.migrateInstances', N'ru', N'Перенос экземпляров таблиц'),
    (N'jobs.migrateInstances', N'kz', N'Кесте даналарын көшіру'),
    (N'jobs.migrateNewRows', N'ru', N'Добавление новых строк целевой версии (добавлено: {rows})'),
    (N'jobs.migrateNewRows', N'kz', N'Мақсатты нұсқаның жаңа жолдарын қосу (қосылған: {rows})'),
    (N'jobs.migrateHeader', N'ru', N'Перенос шапок документов'),
    (N'jobs.migrateHeader', N'kz', N'Құжат тақырыптарын көшіру'),
    (N'jobs.migrateIndex', N'ru', N'Перестроение индекса поиска (строк: {rows})'),
    (N'jobs.migrateIndex', N'kz', N'Іздеу индексін қайта құру (жолдар: {rows})'),
    (N'jobs.migrateWorkflow', N'ru', N'Перенос состава документов и рабочего процесса'),
    (N'jobs.migrateWorkflow', N'kz', N'Құжат құрамы мен жұмыс үдерісін көшіру'),
    (N'jobs.migrateValidation', N'ru', N'Перенос результатов проверки (строк: {rows})'),
    (N'jobs.migrateValidation', N'kz', N'Тексеру нәтижелерін көшіру (жолдар: {rows})'),
    (N'jobs.migrateFinish', N'ru', N'Переключение проекта на новую версию'),
    (N'jobs.migrateFinish', N'kz', N'Жобаны жаңа нұсқаға ауыстыру'),
    (N'jobs.migrateDone', N'ru', N'Перенос на версию {version} завершён: документов {documents}, перенесено значений {values}'),
    (N'jobs.migrateDone', N'kz', N'{version} нұсқасына көшіру аяқталды: құжаттар {documents}, көшірілген мәндер {values}'),
    (N'err.ECR-JOB-0409.migrationInProgress', N'ru', N'Перенос этого проекта уже выполняется (запущен другим пользователем). Дождитесь завершения.'),
    (N'err.ECR-JOB-0409.migrationInProgress', N'kz', N'Бұл жобаны көшіру қазірдің өзінде орындалуда (басқа пайдаланушы бастаған). Аяқталуын күтіңіз.'),
    (N'documents.migrateRunning', N'ru', N'Перенос выполняется в фоне: {percent}%. Окно можно закрыть; ход переноса остаётся в «Моих задачах».'),
    (N'documents.migrateRunning', N'kz', N'Көшіру фонда орындалуда: {percent}%. Терезені жабуға болады; барысы «Менің тапсырмаларымда» қалады.'),
    (N'documents.migrateJobFailed', N'ru', N'Перенос не завершён, ничего не изменено: {reason}'),
    (N'documents.migrateJobFailed', N'kz', N'Көшіру аяқталмады, ештеңе өзгертілмеді: {reason}')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:rc16mig ── кінець секції ──

-- COLL:rc16mig ── D-2 RC15B: перенос версії шаблону фоновою задачею (прогрес стадій, підсумок, друга людина вже запустила перенос); ru/kz — порцією COLL:rc16mig нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'jobs.kind.migrateDocumentVersion', N'en', N'Template version migration', 1),
    (N'jobs.migrateStarted', N'en', N'Migration started', 1),
    (N'jobs.migratePlanning', N'en', N'Checking the project and planning the transfer', 1),
    (N'jobs.migrateCells', N'en', N'Moving cell values and rows ({rows} changed so far)', 1),
    (N'jobs.migrateInstances', N'en', N'Moving table instances', 1),
    (N'jobs.migrateNewRows', N'en', N'Adding new rows of the target version ({rows} added so far)', 1),
    (N'jobs.migrateHeader', N'en', N'Moving document headers', 1),
    (N'jobs.migrateIndex', N'en', N'Rebuilding the search index ({rows} rows so far)', 1),
    (N'jobs.migrateWorkflow', N'en', N'Moving document composition and workflow', 1),
    (N'jobs.migrateValidation', N'en', N'Moving validation results ({rows} rows so far)', 1),
    (N'jobs.migrateFinish', N'en', N'Switching the project to the new version', 1),
    (N'jobs.migrateDone', N'en', N'Migrated to version {version}: documents {documents}, values moved {values}', 1),
    (N'err.ECR-JOB-0409.migrationInProgress', N'en', N'A migration of this project is already running (started by another user). Wait for it to finish.', 1),
    (N'documents.migrateRunning', N'en', N'The migration runs in the background: {percent}%. You can close this window; progress stays in My tasks.', 1),
    (N'documents.migrateJobFailed', N'en', N'The migration did not finish, nothing was changed: {reason}', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:rc16mig ── кінець секції ──
