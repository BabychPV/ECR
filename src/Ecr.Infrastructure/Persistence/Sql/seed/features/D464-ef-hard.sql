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
