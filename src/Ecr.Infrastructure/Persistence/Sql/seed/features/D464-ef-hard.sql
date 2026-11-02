-- D464:ef-hard ── ru/kz: синк подій PI для річних періодів (D-464); власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'sourceEvents.removalBlockedTitle', N'ru', N'Пропавшие в PI события не удалены'),
    (N'sourceEvents.removalBlockedTitle', N'kz', N'PI-де жоғалған оқиғалар жойылмады'),
    (N'sourceEvents.removalBlockedText', N'ru', N'В PI пропало событий: {count} (лимит без подтверждения: {limit}; связанных строк в окне: {linked}). Ничего не удалено. Проверьте источник; если события действительно удалены в PI, подтвердите удаление.'),
    (N'sourceEvents.removalBlockedText', N'kz', N'PI-де жоғалған оқиғалар: {count} (растаусыз шек: {limit}; терезедегі байланысты жолдар: {linked}). Ештеңе жойылмады. Көзді тексеріңіз; оқиғалар PI-де шынымен жойылса, жоюды растаңыз.'),
    (N'sourceEvents.removalConfirmAction', N'ru', N'Подтвердить удаление ({count})'),
    (N'sourceEvents.removalConfirmAction', N'kz', N'Жоюды растау ({count})'),
    (N'sourceEvents.removalConfirmTitle', N'ru', N'Удалить строки событий, пропавших в PI: {count}?'),
    (N'sourceEvents.removalConfirmTitle', N'kz', N'PI-де жоғалған оқиғалардың {count} жолын жою керек пе?'),
    (N'sourceEvents.removalConfirmText', N'ru', N'Следующая синхронизация удалит строки документов для событий, которых больше нет в PI, вместе со значениями. Строки с ручными правками, закрытые периоды и поданные листы не затрагиваются. Каждое удаление записывается в журнал изменений.'),
    (N'sourceEvents.removalConfirmText', N'kz', N'Келесі синхрондау PI-де енді жоқ оқиғалардың құжат жолдарын мәндерімен бірге жояды. Қолмен түзетілген жолдар, жабық кезеңдер және тапсырылған парақтар қозғалмайды. Әр жою өзгерістер журналына жазылады.'),
    (N'sourceEvents.removalConfirmVerb', N'ru', N'Удалить пропавшие события'),
    (N'sourceEvents.removalConfirmVerb', N'kz', N'Жоғалған оқиғаларды жою'),
    (N'sourceEvents.removalConfirmQueued', N'ru', N'Удаление подтверждено; синхронизация поставлена в очередь: {job}.'),
    (N'sourceEvents.removalConfirmQueued', N'kz', N'Жою расталды; синхрондау кезекке қойылды: {job}.'),
    (N'coverageEvents.eventsTruncatedCap', N'ru', N'В окне источника событий больше, чем потолок чтения {cap} ({maxPages} страниц по {pageSize}): прочитано {count} за {pages} страниц, события после {after} не синхронизированы, удаление пропавших отключено. Увеличьте Collection:MaxEventPages или задайте PiSqlClient:EventQueryChunk = Month.'),
    (N'coverageEvents.eventsTruncatedCap', N'kz', N'Көз терезесінде оқу шегінен {cap} ({pageSize} бойынша {maxPages} бет) көп оқиға бар: {pages} бетте {count} оқылды, {after} кейінгі оқиғалар синхрондалмады, жоғалғандарды жою өшірілді. Collection:MaxEventPages мәнін арттырыңыз немесе PiSqlClient:EventQueryChunk = Month орнатыңыз.'),
    (N'coverageEvents.eventSheetLocked', N'ru', N'Лист в состоянии {status}: в этом периоде не записано событий: {writes}, не удалено пропавших: {removals}. Они синхронизируются сразу после возврата, отзыва или переоткрытия листа.'),
    (N'coverageEvents.eventSheetLocked', N'kz', N'Парақ {status} күйінде: бұл кезеңде {writes} оқиға жазылмады және {removals} жоғалған оқиға жойылмады. Олар парақ қайтарылғаннан, кері шақырылғаннан немесе қайта ашылғаннан кейін бірден синхрондалады.'),
    (N'coverageEvents.eventOpenAtPeriodClose', N'ru', N'Событий этого закрытого периода, всё ещё открытых в PI (без времени окончания): {count}; их строки так и не записаны. Закройте события в PI и переоткройте период или внесите строки вручную.'),
    (N'coverageEvents.eventOpenAtPeriodClose', N'kz', N'Бұл жабық кезеңнің PI-де әлі ашық (аяқталу уақытысыз) оқиғалары: {count}; олардың жолдары ешқашан жазылмаған. Оқиғаларды PI-де жауып, кезеңді қайта ашыңыз немесе жолдарды қолмен енгізіңіз.'),
    (N'coverageEvents.eventPeriodChanged', N'ru', N'Событие {eventId} теперь начинается в другом периоде ({to}); его строка {rowKey} осталась в периоде {from} и не перенесена. Проверьте событие в PI и поправьте строки вручную.'),
    (N'coverageEvents.eventPeriodChanged', N'kz', N'{eventId} оқиғасы енді басқа кезеңде басталады ({to}); оның {rowKey} жолы {from} кезеңінде қалды және көшірілмеді. Оқиғаны PI-де тексеріп, жолдарды қолмен түзетіңіз.'),
    (N'integrationAudit.sourceEventRemovalConfirmed', N'ru', N'Удаление событий, пропавших в PI, подтверждено вручную для сущности «{entity}» (задача {job}).'),
    (N'integrationAudit.sourceEventRemovalConfirmed', N'kz', N'«{entity}» нысаны үшін PI-де жоғалған оқиғаларды жою қолмен расталды ({job} тапсырмасы).')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- D464:ef-hard ── кінець секції ──
-- D464:ef-hard ── en: синк подій PI для річних періодів (D-464): підтвердження масового видалення, стеля сторінок, поданий аркуш, відкриті на закриття періоду, переїзд початку події; ru/kz — порцією D464:ef-hard нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'sourceEvents.removalBlockedTitle', N'en', N'Events missing from PI were not removed', 1),
    (N'sourceEvents.removalBlockedText', N'en', N'{count} events are missing from PI (limit without confirmation: {limit}; linked rows in the window: {linked}). Nothing was removed. Check the source; if the events were really deleted in PI, confirm the removal.', 1),
    (N'sourceEvents.removalConfirmAction', N'en', N'Confirm removal ({count})', 1),
    (N'sourceEvents.removalConfirmTitle', N'en', N'Remove {count} event rows missing from PI?', 1),
    (N'sourceEvents.removalConfirmText', N'en', N'The next synchronization deletes the document rows of events that are no longer in PI, together with their values. Rows with manual edits, closed periods and submitted sheets are not touched. Each removal is recorded in the change log.', 1),
    (N'sourceEvents.removalConfirmVerb', N'en', N'Remove missing events', 1),
    (N'sourceEvents.removalConfirmQueued', N'en', N'Removal confirmed; synchronization queued: {job}.', 1),
    (N'coverageEvents.eventsTruncatedCap', N'en', N'The source has more events in the window than the read cap of {cap} ({maxPages} pages of {pageSize}): {count} read in {pages} pages, events after {after} were not synchronized, and removal of missing events is off. Raise Collection:MaxEventPages or set PiSqlClient:EventQueryChunk to Month.', 1),
    (N'coverageEvents.eventSheetLocked', N'en', N'The sheet is {status}: in this period {writes} events were not written and {removals} missing events were not removed. They are synchronized right after the sheet is returned, recalled or reopened.', 1),
    (N'coverageEvents.eventOpenAtPeriodClose', N'en', N'{count} events of this closed period are still open in PI (no end time): their rows were never written. Close the events in PI and reopen the period, or enter the rows manually.', 1),
    (N'coverageEvents.eventPeriodChanged', N'en', N'Event {eventId} now starts in another period ({to}); its row {rowKey} stays in period {from} and was not moved. Check the event in PI and correct the rows manually.', 1),
    (N'integrationAudit.sourceEventRemovalConfirmed', N'en', N'Removal of events missing from PI confirmed manually for entity "{entity}" (job {job}).', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- D464:ef-hard ── кінець секції ──
