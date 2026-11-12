-- FLERT:sync ── ru/kz: стан синку складу газу FLERT і «Синхронізувати зараз» (D-476 F3); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'flert.sync.title', N'ru', N'Состав газа из FLERT'),
    (N'flert.sync.never', N'ru', N'Синхронизации ещё не было.'),
    (N'flert.sync.lastRun', N'ru', N'Последняя синхронизация: {time} — {status}.'),
    (N'flert.sync.counts', N'ru', N'Строк из FLERT: {rows}; добавлено: {added}; изменено: {updated}; отключено: {deactivated}; снова включено: {reactivated}.'),
    (N'flert.sync.disabled', N'ru', N'Ежедневная синхронизация выключена (Flert:Sync:Enabled): запуск лишь отмечается в журнале.'),
    (N'flert.sync.now', N'ru', N'Синхронизировать сейчас'),
    (N'flert.sync.queuing', N'ru', N'Ставим в очередь…'),
    (N'flert.sync.queued', N'ru', N'Синхронизация с FLERT поставлена в очередь.'),
    (N'flert.sync.status.running', N'ru', N'выполняется'),
    (N'flert.sync.status.succeeded', N'ru', N'успешно'),
    (N'flert.sync.status.degraded', N'ru', N'с замечаниями'),
    (N'flert.sync.status.failed', N'ru', N'ошибка'),
    (N'flert.sync.reason.disabled', N'ru', N'Синхронизация выключена — данные не читались.'),
    (N'flert.sync.reason.emptyResult', N'ru', N'FLERT вернул 0 строк — копия не изменена, ничего не удалено.'),
    (N'flert.sync.reason.deactivationLimit', N'ru', N'Снимок отключил бы слишком много кейсов (похоже на неполный результат FLERT) — ничего не записано.'),
    (N'flert.sync.reason.mappingErrors', N'ru', N'Изменилась схема данных FLERT — снимок не применён.'),
    (N'flert.sync.reason.truncated', N'ru', N'Снимок FLERT больше допустимого — не применён.'),
    (N'flert.sync.reason.registryIncompatible', N'ru', N'Справочник с таким кодом уже есть и не подходит для копии FLERT — ничего не записано.'),
    (N'flert.sync.reason.writerRejected', N'ru', N'Часть строк FLERT не принята — подробности в журнале обслуживания.'),
    (N'jobs.kind.flertCompositionSync', N'ru', N'Синхронизация состава газа из FLERT'),
    (N'err.ECR-REG-0409.externalSourceFlert', N'ru', N'Записи этого справочника — копия состава газа из FLERT. Изменяйте их во FLERT.'),
    (N'flert.mirror.pending', N'ru', N'Справочник появится после первой синхронизации с FLERT.'),
    (N'flert.sync.title', N'kz', N'FLERT-тен газ құрамы'),
    (N'flert.sync.never', N'kz', N'Синхрондау әлі болған жоқ.'),
    (N'flert.sync.lastRun', N'kz', N'Соңғы синхрондау: {time} — {status}.'),
    (N'flert.sync.counts', N'kz', N'FLERT жолдары: {rows}; қосылды: {added}; өзгертілді: {updated}; өшірілді: {deactivated}; қайта қосылды: {reactivated}.'),
    (N'flert.sync.disabled', N'kz', N'Күнделікті синхрондау өшірулі (Flert:Sync:Enabled): іске қосу тек журналда белгіленеді.'),
    (N'flert.sync.now', N'kz', N'Қазір синхрондау'),
    (N'flert.sync.queuing', N'kz', N'Кезекке қойылуда…'),
    (N'flert.sync.queued', N'kz', N'FLERT-пен синхрондау кезекке қойылды.'),
    (N'flert.sync.status.running', N'kz', N'орындалуда'),
    (N'flert.sync.status.succeeded', N'kz', N'сәтті'),
    (N'flert.sync.status.degraded', N'kz', N'ескертулермен'),
    (N'flert.sync.status.failed', N'kz', N'қате'),
    (N'flert.sync.reason.disabled', N'kz', N'Синхрондау өшірулі — деректер оқылмады.'),
    (N'flert.sync.reason.emptyResult', N'kz', N'FLERT 0 жол қайтарды — көшірме өзгертілмеді, ештеңе жойылмады.'),
    (N'flert.sync.reason.deactivationLimit', N'kz', N'Сурет тым көп кейсті өшірер еді (FLERT нәтижесі толық емес сияқты) — ештеңе жазылмады.'),
    (N'flert.sync.reason.mappingErrors', N'kz', N'FLERT деректер сұлбасы өзгерді — сурет қолданылмады.'),
    (N'flert.sync.reason.truncated', N'kz', N'FLERT суреті рұқсат етілгеннен үлкен — қолданылмады.'),
    (N'flert.sync.reason.registryIncompatible', N'kz', N'Осындай кодты анықтамалық бар және FLERT көшірмесіне сай емес — ештеңе жазылмады.'),
    (N'flert.sync.reason.writerRejected', N'kz', N'FLERT жолдарының бір бөлігі қабылданбады — толығырақ қызмет көрсету журналында.'),
    (N'jobs.kind.flertCompositionSync', N'kz', N'FLERT-тен газ құрамын синхрондау'),
    (N'err.ECR-REG-0409.externalSourceFlert', N'kz', N'Бұл анықтамалықтың жазбалары — FLERT-тен газ құрамының көшірмесі. Оларды FLERT ішінде өзгертіңіз.'),
    (N'flert.mirror.pending', N'kz', N'Анықтамалық FLERT-пен алғашқы синхрондаудан кейін пайда болады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- FLERT:sync ── кінець секції ──

-- FLERT:sync ── en: стан синку складу газу FLERT і «Синхронізувати зараз» (D-476 F3) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'flert.sync.title', N'en', N'Gas composition from FLERT', 1),
    (N'flert.sync.never', N'en', N'Not synchronized yet.', 1),
    (N'flert.sync.lastRun', N'en', N'Last sync: {time} — {status}.', 1),
    (N'flert.sync.counts', N'en', N'Rows from FLERT: {rows}; added: {added}; changed: {updated}; switched off: {deactivated}; back on: {reactivated}.', 1),
    (N'flert.sync.disabled', N'en', N'Daily sync is off (Flert:Sync:Enabled): a run is only recorded in the log.', 1),
    (N'flert.sync.now', N'en', N'Sync now', 1),
    (N'flert.sync.queuing', N'en', N'Queuing…', 1),
    (N'flert.sync.queued', N'en', N'FLERT sync queued.', 1),
    (N'flert.sync.status.running', N'en', N'running', 1),
    (N'flert.sync.status.succeeded', N'en', N'succeeded', 1),
    (N'flert.sync.status.degraded', N'en', N'with remarks', 1),
    (N'flert.sync.status.failed', N'en', N'failed', 1),
    (N'flert.sync.reason.disabled', N'en', N'Sync is off — nothing was read.', 1),
    (N'flert.sync.reason.emptyResult', N'en', N'FLERT returned 0 rows — the copy is unchanged, nothing was removed.', 1),
    (N'flert.sync.reason.deactivationLimit', N'en', N'The snapshot would switch off too many cases (looks like a partial FLERT result) — nothing was written.', 1),
    (N'flert.sync.reason.mappingErrors', N'en', N'The FLERT data shape changed — the snapshot was not applied.', 1),
    (N'flert.sync.reason.truncated', N'en', N'The FLERT snapshot is larger than allowed — not applied.', 1),
    (N'flert.sync.reason.registryIncompatible', N'en', N'A registry with this code already exists and does not fit the FLERT copy — nothing was written.', 1),
    (N'flert.sync.reason.writerRejected', N'en', N'Some FLERT rows were not accepted — see the maintenance log.', 1),
    (N'jobs.kind.flertCompositionSync', N'en', N'Gas composition sync from FLERT', 1),
    (N'err.ECR-REG-0409.externalSourceFlert', N'en', N'Entries of this registry are a copy of the FLERT gas composition. Change them in FLERT.', 1),
    (N'flert.mirror.pending', N'en', N'The registry appears after the first FLERT sync.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- FLERT:sync ── кінець секції ──
