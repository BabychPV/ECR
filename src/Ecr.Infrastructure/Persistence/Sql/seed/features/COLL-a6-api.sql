-- COLL:a6-api ── ru/kz API подій джерела (HSE301 A6); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-INT-0404.eventMap', N'ru', N'Сопоставление событий {eventMapId} не существует.'),
    (N'err.ECR-INT-0405.registryEntry', N'ru', N'Записи справочника {registryEntryId} не существует, либо она не принадлежит справочнику столбца «{targetColumn}».'),
    (N'err.ECR-INT-0409.eventMapExists', N'ru', N'Сопоставление событий для этой сущности источника, документа и таблицы уже есть: измените его, а не создавайте второе.'),
    (N'err.ECR-INT-0409.eventMapHasLinks', N'ru', N'Через это сопоставление уже синхронизировано событий: {links}. Удаление оставило бы их строки без объяснения. Приостановите сопоставление.'),
    (N'err.ECR-INT-0422.eventMapTableNotInDocument', N'ru', N'Таблица {tableDefId} не принадлежит версии шаблона проекта документа {documentId}, поэтому события в неё записать нельзя.'),
    (N'err.ECR-INT-0422.eventSyncNoMap', N'ru', N'У сущности источника {sourceEntityId} нет активного сопоставления событий: синхронизировать нечего.'),
    (N'err.ECR-REQ-0422.probeEventsInvalid', N'ru', N'Для пробы событий нужны имя шаблона, окно, где начало раньше конца (не более 92 дней), и лимит от 1 до 100 событий.'),
    (N'err.ECR-INT-0404.eventMap', N'kz', N'{eventMapId} оқиғаларды салыстыру бар емес.'),
    (N'err.ECR-INT-0405.registryEntry', N'kz', N'{registryEntryId} анықтамалық жазбасы жоқ немесе ол «{targetColumn}» бағанының анықтамалығына жатпайды.'),
    (N'err.ECR-INT-0409.eventMapExists', N'kz', N'Осы дерек көзі нысаны, құжат және кесте үшін оқиғаларды салыстыру бұрыннан бар: екіншісін жасамай, оны өзгертіңіз.'),
    (N'err.ECR-INT-0409.eventMapHasLinks', N'kz', N'Осы салыстыру арқылы {links} оқиға синхрондалған. Жою олардың жолдарын түсіндірмесіз қалдырар еді. Салыстыруды тоқтата тұрыңыз.'),
    (N'err.ECR-INT-0422.eventMapTableNotInDocument', N'kz', N'{tableDefId} кестесі {documentId} құжатының жобасы үлгісінің нұсқасына жатпайды, сондықтан оқиғаларды оған жазу мүмкін емес.'),
    (N'err.ECR-INT-0422.eventSyncNoMap', N'kz', N'{sourceEntityId} дерек көзі нысанында белсенді оқиғаларды салыстыру жоқ: синхрондайтын ештеңе жоқ.'),
    (N'err.ECR-REQ-0422.probeEventsInvalid', N'kz', N'Оқиғаларды сынау үшін үлгі атауы, басы соңынан бұрын болатын терезе (92 күннен аспайтын) және 1–100 оқиға шегі қажет.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:a6-api ── кінець секції ──

