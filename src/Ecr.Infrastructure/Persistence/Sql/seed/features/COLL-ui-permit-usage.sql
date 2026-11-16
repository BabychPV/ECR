-- COLL:ui-permit-usage ── ru/kz: «Used in» картки дозволу і колонка переліку (PERMIT-USAGE); власна порція; en — порція #UiEn нижче ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'permits.page.tabs.usage', N'ru', N'Где используется'),
    (N'permits.page.tabs.usage', N'kz', N'Қайда қолданылады'),
    (N'permits.usage.title', N'ru', N'Где используется разрешение'),
    (N'permits.usage.title', N'kz', N'Рұқсат қайда қолданылады'),
    (N'permits.usage.note', N'ru', N'Документы, в которых поле шапки или колонка таблицы содержит это разрешение или оно задано в истории разрешений (более позднее «с даты» или часть разделённого месяца). Учитываются только проекты, доступные вам.'),
    (N'permits.usage.note', N'kz', N'Тақырып өрісінде немесе кесте бағанында осы рұқсат көрсетілген немесе ол рұқсаттар тарихында берілген құжаттар (кейінгі «күннен бастап» немесе бөлінген айдың бөлігі). Тек сізге қолжетімді жобалар ескеріледі.'),
    (N'permits.usage.summary', N'ru', N'Документов: {documents} · мест: {links}'),
    (N'permits.usage.summary', N'kz', N'Құжаттар: {documents} · орындар: {links}'),
    (N'permits.usage.period', N'ru', N'Период'),
    (N'permits.usage.period', N'kz', N'Кезең'),
    (N'permits.usage.allPeriods', N'ru', N'Все периоды'),
    (N'permits.usage.allPeriods', N'kz', N'Барлық кезеңдер'),
    (N'permits.usage.truncatedTitle', N'ru', N'Показаны не все места'),
    (N'permits.usage.truncatedTitle', N'kz', N'Барлық орын көрсетілмеген'),
    (N'permits.usage.truncatedText', N'ru', N'В списке первые {shown} из {links} мест. Выберите период, чтобы сузить список.'),
    (N'permits.usage.truncatedText', N'kz', N'Тізімде {links} орынның алғашқы {shown} орны. Тізімді тарылту үшін кезеңді таңдаңыз.'),
    (N'permits.usage.emptyTitle', N'ru', N'Пока не используется ни в одном документе'),
    (N'permits.usage.emptyTitle', N'kz', N'Әзірге ешбір құжатта қолданылмайды'),
    (N'permits.usage.emptyHint', N'ru', N'Выберите это разрешение в поле шапки документа или в колонке таблицы со списком разрешений — место появится здесь.'),
    (N'permits.usage.emptyHint', N'kz', N'Осы рұқсатты құжат тақырыбының өрісінде немесе рұқсаттар тізімі бар кесте бағанында таңдаңыз — орын осында пайда болады.'),
    (N'permits.usage.noMatchTitle', N'ru', N'Не используется в этом периоде'),
    (N'permits.usage.noMatchTitle', N'kz', N'Бұл кезеңде қолданылмайды'),
    (N'permits.usage.noMatchHint', N'ru', N'Выберите другой период или покажите все периоды.'),
    (N'permits.usage.noMatchHint', N'kz', N'Басқа кезеңді таңдаңыз немесе барлық кезеңдерді көрсетіңіз.'),
    (N'permits.usage.showAll', N'ru', N'Показать все периоды'),
    (N'permits.usage.showAll', N'kz', N'Барлық кезеңдерді көрсету'),
    (N'permits.usage.caption', N'ru', N'Места, где используется разрешение'),
    (N'permits.usage.caption', N'kz', N'Рұқсат қолданылатын орындар'),
    (N'permits.usage.col.document', N'ru', N'Документ'),
    (N'permits.usage.col.document', N'kz', N'Құжат'),
    (N'permits.usage.col.period', N'ru', N'Период'),
    (N'permits.usage.col.period', N'kz', N'Кезең'),
    (N'permits.usage.col.where', N'ru', N'Где'),
    (N'permits.usage.col.where', N'kz', N'Қайда'),
    (N'permits.usage.col.field', N'ru', N'Поле или колонка'),
    (N'permits.usage.col.field', N'kz', N'Өріс немесе баған'),
    (N'permits.usage.header', N'ru', N'Шапка документа'),
    (N'permits.usage.header', N'kz', N'Құжаттың тақырып бөлігі'),
    (N'permits.usage.row', N'ru', N'Строка {row}'),
    (N'permits.usage.row', N'kz', N'{row}-жол'),
    (N'permits.usage.listColumn', N'ru', N'Используется в документах'),
    (N'permits.usage.listColumn', N'kz', N'Құжаттарда қолданылады'),
    (N'permits.usage.listColumnHint', N'ru', N'Документы, в шапке, таблице или истории разрешений которых указано это разрешение'),
    (N'permits.usage.listColumnHint', N'kz', N'Тақырыбында, кестесінде немесе рұқсаттар тарихында осы рұқсат бар құжаттар'),
    (N'permits.usage.typeListUsedIn', N'ru', N'Используются в документах: {count}'),
    (N'permits.usage.typeListUsedIn', N'kz', N'Құжаттарда қолданылады: {count}'),
    (N'permits.usage.historyFrom', N'ru', N'в истории с {date}'),
    (N'permits.usage.historyFrom', N'kz', N'{date} бастап тарихта'),
    (N'permits.usage.summaryHistory', N'ru', N'в истории разрешений: {history}'),
    (N'permits.usage.summaryHistory', N'kz', N'рұқсаттар тарихында: {history}')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui-permit-usage ── кінець секції ──
-- COLL:ui-permit-usage ── en: «Used in» картки дозволу і колонка переліку (PERMIT-USAGE, lane/integ-permit-usage-rc); ru/kz — seed/features/COLL-ui-permit-usage.sql ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'permits.page.tabs.usage', N'en', N'Used in', 1),
    (N'permits.usage.title', N'en', N'Where this permit is used', 1),
    (N'permits.usage.note', N'en', N'Documents where a header field or a table column holds this permit, or where it is set in the permit history (a later permit «from date» or part of a split month). Only projects you can open are counted.', 1),
    (N'permits.usage.summary', N'en', N'Documents: {documents} · places: {links}', 1),
    (N'permits.usage.period', N'en', N'Period', 1),
    (N'permits.usage.allPeriods', N'en', N'All periods', 1),
    (N'permits.usage.truncatedTitle', N'en', N'Not every place is listed', 1),
    (N'permits.usage.truncatedText', N'en', N'The list shows the first {shown} of {links} places. Choose a period to narrow it.', 1),
    (N'permits.usage.emptyTitle', N'en', N'Not used in any document yet', 1),
    (N'permits.usage.emptyHint', N'en', N'Pick this permit in a document header field or in a table column that lists permits, and the place appears here.', 1),
    (N'permits.usage.noMatchTitle', N'en', N'Not used in this period', 1),
    (N'permits.usage.noMatchHint', N'en', N'Choose another period or show all periods.', 1),
    (N'permits.usage.showAll', N'en', N'Show all periods', 1),
    (N'permits.usage.caption', N'en', N'Places where the permit is used', 1),
    (N'permits.usage.col.document', N'en', N'Document', 1),
    (N'permits.usage.col.period', N'en', N'Period', 1),
    (N'permits.usage.col.where', N'en', N'Where', 1),
    (N'permits.usage.col.field', N'en', N'Field or column', 1),
    (N'permits.usage.header', N'en', N'Document header', 1),
    (N'permits.usage.row', N'en', N'Row {row}', 1),
    (N'permits.usage.listColumn', N'en', N'Used in documents', 1),
    (N'permits.usage.listColumnHint', N'en', N'Documents whose header, table or permit history holds this permit', 1),
    (N'permits.usage.typeListUsedIn', N'en', N'Used in documents: {count}', 1),
    (N'permits.usage.historyFrom', N'en', N'in history from {date}', 1),
    (N'permits.usage.summaryHistory', N'en', N'in permit history: {history}', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui-permit-usage ── кінець секції ──
-- COLL:ui-permit-usage ── змінені тексти: підказки «Used in» згадують історію дозволів (B56) ──
-- Нове значення вже стоїть вище (#UiEn — en, #I18N — ru/kz); епілог оновлює, лише поки в базі старе (випущене) значення.
INSERT INTO #UiChanged ([Key], Lang, OldVal, NewVal)
SELECT v.[Key], v.Lang, v.OldVal, v.NewVal
  FROM (VALUES
    (N'permits.usage.note',
     N'ru',
     N'Документы, в которых поле шапки или колонка таблицы содержит это разрешение. Учитываются только проекты, доступные вам.',
     N'Документы, в которых поле шапки или колонка таблицы содержит это разрешение или оно задано в истории разрешений (более позднее «с даты» или часть разделённого месяца). Учитываются только проекты, доступные вам.'),
    (N'permits.usage.note',
     N'kz',
     N'Тақырып өрісінде немесе кесте бағанында осы рұқсат көрсетілген құжаттар. Тек сізге қолжетімді жобалар ескеріледі.',
     N'Тақырып өрісінде немесе кесте бағанында осы рұқсат көрсетілген немесе ол рұқсаттар тарихында берілген құжаттар (кейінгі «күннен бастап» немесе бөлінген айдың бөлігі). Тек сізге қолжетімді жобалар ескеріледі.'),
    (N'permits.usage.note',
     N'en',
     N'Documents where a header field or a table column holds this permit. Only projects you can open are counted.',
     N'Documents where a header field or a table column holds this permit, or where it is set in the permit history (a later permit «from date» or part of a split month). Only projects you can open are counted.'),
    (N'permits.usage.listColumnHint',
     N'ru',
     N'Документы, в шапке или таблице которых указано это разрешение',
     N'Документы, в шапке, таблице или истории разрешений которых указано это разрешение'),
    (N'permits.usage.listColumnHint',
     N'kz',
     N'Тақырыбында немесе кестесінде осы рұқсат бар құжаттар',
     N'Тақырыбында, кестесінде немесе рұқсаттар тарихында осы рұқсат бар құжаттар'),
    (N'permits.usage.listColumnHint',
     N'en',
     N'Documents whose header or table holds this permit',
     N'Documents whose header, table or permit history holds this permit')
       ) AS v ([Key], Lang, OldVal, NewVal)
OPTION (RECOMPILE);
GO
-- COLL:ui-permit-usage ── кінець змінених текстів ──
-- COLL:ui-permit-usage ── прибрані ключі: «лише в історії» замінено рядками історії в переліку (B128c) ──
INSERT INTO #UiRemoved ([Key], Lang, OldVal)
SELECT v.[Key], v.Lang, v.OldVal
  FROM (VALUES
    (N'permits.usage.historyOnlyTitle',
     N'ru',
     N'Используется только в истории разрешений'),
    (N'permits.usage.historyOnlyTitle',
     N'kz',
     N'Тек рұқсаттар тарихында қолданылады'),
    (N'permits.usage.historyOnlyHint',
     N'ru',
     N'Разрешение задано в документе как более позднее «с даты» или для части месяца. Откройте документ — история разрешений показана в шапке документа и в инспекторе строки.'),
    (N'permits.usage.historyOnlyHint',
     N'kz',
     N'Рұқсат құжатта кейінгі «күннен бастап» немесе айдың бір бөлігі үшін берілген. Құжатты ашыңыз — рұқсаттар тарихы құжат тақырыбында және жол инспекторында көрсетілген.'),
    (N'permits.usage.historyOnlyTitle',
     N'en',
     N'Used only in permit history'),
    (N'permits.usage.historyOnlyHint',
     N'en',
     N'The document sets this permit as a later permit «from date» or for part of a month. Open the document: the permit history is shown in the document header and in the row inspector.')
       ) AS v ([Key], Lang, OldVal)
OPTION (RECOMPILE);
GO
-- COLL:ui-permit-usage ── кінець прибраних ключів ──
