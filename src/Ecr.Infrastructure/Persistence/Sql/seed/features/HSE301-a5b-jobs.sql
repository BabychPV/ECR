-- HSE301:a5b-jobs ── ru/kz прогресу синку подій джерела (SourceEventSyncJob); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'jobs.sourceEventsNoMaps', N'ru', N'Нет активных мапингов событий'),
    (N'jobs.sourceEventsNoMaps', N'kz', N'Оқиғалардың белсенді мапингтері жоқ'),
    (N'jobs.sourceEventsReading', N'ru', N'Чтение событий из источника'),
    (N'jobs.sourceEventsReading', N'kz', N'Оқиғаларды көзден оқу'),
    (N'jobs.sourceEventsWriting', N'ru', N'Запись строк событий'),
    (N'jobs.sourceEventsWriting', N'kz', N'Оқиға жолдарын жазу'),
    (N'jobs.sourceEventsDone', N'ru', N'Создано: {created}; обновлено: {updated}; сохранено ручных значений: {keptManual}; пропало в источнике: {missing}; не завершено: {open}; без соответствия: {unmapped}; период закрыт: {closed}; в ожидании: {pending}'),
    (N'jobs.sourceEventsDone', N'kz', N'Жасалды: {created}; жаңартылды: {updated}; сақталған қолмен енгізілген мәндер: {keptManual}; көзден жоғалды: {missing}; аяқталмаған: {open}; сәйкестігі жоқ: {unmapped}; кезең жабық: {closed}; күтуде: {pending}')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- HSE301:a5b-jobs ── кінець секції ──

