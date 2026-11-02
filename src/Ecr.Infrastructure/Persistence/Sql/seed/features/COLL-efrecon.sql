-- COLL:efrecon ── ru/kz «повної звірки за період» подій джерела; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'jobs.sourceEventsRemoved', N'ru', N'События, пропавшие из источника: удалено {removed}, удаление пропущено {skipped}'),
    (N'jobs.sourceEventsRemoved', N'kz', N'Көзден жоғалған оқиғалар: жойылды {removed}, жою өткізілді {skipped}'),
    (N'coverageEvents.eventRemovalSourceEmpty', N'ru', N'Источник не вернул ни одного события, хотя связанных строк: {count}. Удаление пропущено: источник похож на недоступный.'),
    (N'coverageEvents.eventRemovalSourceEmpty', N'kz', N'Көз ешбір оқиға қайтармады, ал байланысты жолдар: {count}. Жою өткізілді: көз қолжетімсіз сияқты.'),
    (N'coverageEvents.eventRemovalKeptManual', N'ru', N'События {eventId} больше нет в источнике, но в строке {rowKey} есть ручная правка: строка не удалена.'),
    (N'coverageEvents.eventRemovalKeptManual', N'kz', N'{eventId} оқиғасы көзде жоқ, бірақ {rowKey} жолында қолмен түзету бар: жол жойылмады.'),
    (N'coverageEvents.eventTemplateOverlap', N'ru', N'Событие {eventId} есть в шаблонах {template} и {other} с разными значениями атрибутов; берётся первый шаблон в порядке Auto, Auto_Day, Manual, Manual_Day.'),
    (N'coverageEvents.eventTemplateOverlap', N'kz', N'{eventId} оқиғасы {template} және {other} үлгілерінде атрибут мәндері әртүрлі күйде бар; Auto, Auto_Day, Manual, Manual_Day реті бойынша бірінші үлгі алынады.'),
    (N'coverageEvents.eventRemovalLimit', N'ru', N'В источнике пропало событий: {count} (связанных строк в окне: {linked}; лимит {limit}). Ничего не удалено. Проверьте источник и подтвердите удаление вручную.'),
    (N'coverageEvents.eventRemovalLimit', N'kz', N'Көзде оқиғалар жоғалды: {count} (терезедегі байланысты жолдар: {linked}; шек {limit}). Ештеңе жойылмады. Көзді тексеріп, жоюды қолмен растаңыз.'),
    (N'coverageEvents.eventRemovalSheetSubmitted', N'ru', N'События {eventId} больше нет в источнике, но лист строки {rowKey} в состоянии {status}: строка не удалена.'),
    (N'coverageEvents.eventRemovalSheetSubmitted', N'kz', N'{eventId} оқиғасы көзде жоқ, бірақ {rowKey} жолының парағы {status} күйінде: жол жойылмады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:efrecon ── кінець секції ──

