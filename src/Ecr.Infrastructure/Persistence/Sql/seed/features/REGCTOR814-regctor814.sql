-- REGCTOR814 ── ru/kz «Де використовується» запису довідника (ФВ-8.14); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'registries.entryUsage.column', N'ru', N'Использование'),
    (N'registries.entryUsage.action', N'ru', N'Где используется'),
    (N'registries.entryUsage.actionFor', N'ru', N'Где используется запись {code}'),
    (N'registries.entryUsage.title', N'ru', N'Где используется запись «{code}»'),
    (N'registries.entryUsage.noneNamed', N'ru', N'Ссылок на эту запись поимённо не найдено. Виды, перечисленные внизу, здесь не проверяются.'),
    (N'registries.entryUsage.fieldsTitle', N'ru', N'Записи справочников, которые на неё ссылаются'),
    (N'registries.entryUsage.asOfNote', N'ru', N'Записи прочитаны на {date}: действующие, активные и не удалённые на этот день.'),
    (N'registries.entryUsage.noFields', N'ru', N'Ни одно поле справочника не ссылается на этот справочник.'),
    (N'registries.entryUsage.fieldCount', N'ru', N'Записей: {count}'),
    (N'registries.entryUsage.fieldShown', N'ru', N'Показано {shown} из {total}'),
    (N'registries.entryUsage.none', N'ru', N'Не найдено'),
    (N'registries.entryUsage.childrenTitle', N'ru', N'Дочерние записи'),
    (N'registries.entryUsage.substancesTitle', N'ru', N'Методики, объявившие её веществом'),
    (N'registries.entryUsage.substanceLink', N'ru', N'Версии методики'),
    (N'registries.entryUsage.columnsTitle', N'ru', N'Столбцы шаблонов, берущие значения из этого справочника'),
    (N'registries.entryUsage.columnsHint', N'ru', N'Эти столбцы могут содержать эту запись; какие документы её действительно содержат, для отдельной записи не перечисляется.'),
    (N'registries.entryUsage.truncated', N'ru', N'Список уровня справочника обрезан сервером: часть ссылающихся полей, колонок или веществ может здесь отсутствовать.'),
    (N'registries.entryUsage.notListed', N'ru', N'Здесь для отдельной записи не перечисляются: ячейки и шапки документов, константы методик и связи каскада. Удаление записи сообщает, сколько их, по видам.'),
    (N'registries.entryUsage.column', N'kz', N'Қолданылуы'),
    (N'registries.entryUsage.action', N'kz', N'Қайда қолданылады'),
    (N'registries.entryUsage.actionFor', N'kz', N'{code} жазбасы қайда қолданылады'),
    (N'registries.entryUsage.title', N'kz', N'«{code}» жазбасы қайда қолданылады'),
    (N'registries.entryUsage.noneNamed', N'kz', N'Бұл жазбаға атаулы сілтемелер табылмады. Төменде аталған түрлер мұнда тексерілмейді.'),
    (N'registries.entryUsage.fieldsTitle', N'kz', N'Оған сілтеме жасайтын анықтамалық жазбалары'),
    (N'registries.entryUsage.asOfNote', N'kz', N'Жазбалар {date} күнге оқылды: сол күні қолданыстағы, белсенді және жойылмаған.'),
    (N'registries.entryUsage.noFields', N'kz', N'Бұл анықтамалыққа бірде-бір анықтамалық өрісі сілтеме жасамайды.'),
    (N'registries.entryUsage.fieldCount', N'kz', N'Жазбалар: {count}'),
    (N'registries.entryUsage.fieldShown', N'kz', N'{total} ішінен {shown} көрсетілді'),
    (N'registries.entryUsage.none', N'kz', N'Табылмады'),
    (N'registries.entryUsage.childrenTitle', N'kz', N'Еншілес жазбалар'),
    (N'registries.entryUsage.substancesTitle', N'kz', N'Оны зат ретінде жариялаған әдістемелер'),
    (N'registries.entryUsage.substanceLink', N'kz', N'Әдістеме нұсқалары'),
    (N'registries.entryUsage.columnsTitle', N'kz', N'Осы анықтамалықтан мән алатын үлгі бағандары'),
    (N'registries.entryUsage.columnsHint', N'kz', N'Бұл бағандарда осы жазба болуы мүмкін; оны нақты қай құжаттар қамтитыны жеке жазба үшін тізілмейді.'),
    (N'registries.entryUsage.truncated', N'kz', N'Анықтамалық деңгейіндегі тізімді сервер қысқартты: сілтеме жасайтын өрістердің, бағандардың немесе заттардың бір бөлігі мұнда болмауы мүмкін.'),
    (N'registries.entryUsage.notListed', N'kz', N'Мұнда жеке жазба үшін тізілмейді: құжат ұяшықтары мен тақырыптары, әдістеме константалары және каскад байланыстары. Жазбаны жою олардың қанша екенін түрлері бойынша хабарлайды.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- REGCTOR814 ── кінець секції ──
