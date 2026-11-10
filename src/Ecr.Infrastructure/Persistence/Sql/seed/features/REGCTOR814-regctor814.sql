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
    (N'registries.entryUsage.columnsHint', N'ru', N'Эти столбцы могут содержать эту запись; документы, которые её действительно содержат, перечислены выше.'),
    (N'registries.entryUsage.truncated', N'ru', N'Список уровня справочника обрезан сервером: часть ссылающихся полей, колонок или веществ может здесь отсутствовать.'),
    (N'registries.entryUsage.notListed', N'ru', N'Здесь для отдельной записи не перечисляются: константы методик и связи каскада. Удаление записи сообщает, сколько их, по видам.'),
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
    (N'registries.entryUsage.columnsHint', N'kz', N'Бұл бағандарда осы жазба болуы мүмкін; оны нақты қамтитын құжаттар жоғарыда тізілген.'),
    (N'registries.entryUsage.truncated', N'kz', N'Анықтамалық деңгейіндегі тізімді сервер қысқартты: сілтеме жасайтын өрістердің, бағандардың немесе заттардың бір бөлігі мұнда болмауы мүмкін.'),
    (N'registries.entryUsage.notListed', N'kz', N'Мұнда жеке жазба үшін тізілмейді: әдістеме константалары және каскад байланыстары. Жазбаны жою олардың қанша екенін түрлері бойынша хабарлайды.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- REGCTOR814 ── кінець секції ──
-- REGCTOR814 ── ФВ-8.14: «Де використовується» запис довідника (`features/registries/rc814`).
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'registries.entryUsage.column', N'en', N'Usage', 1),
    (N'registries.entryUsage.action', N'en', N'Where used', 1),
    (N'registries.entryUsage.actionFor', N'en', N'Where entry {code} is used', 1),
    (N'registries.entryUsage.title', N'en', N'Where entry "{code}" is used', 1),
    (N'registries.entryUsage.noneNamed', N'en', N'No references to this entry were found by name. The kinds listed at the bottom are not checked here.', 1),
    (N'registries.entryUsage.fieldsTitle', N'en', N'Registry entries that refer to it', 1),
    (N'registries.entryUsage.asOfNote', N'en', N'Entries are read as of {date}: valid, active and not deleted on that day.', 1),
    (N'registries.entryUsage.noFields', N'en', N'No registry field refers to this registry.', 1),
    (N'registries.entryUsage.fieldCount', N'en', N'Entries: {count}', 1),
    (N'registries.entryUsage.fieldShown', N'en', N'Shown {shown} of {total}', 1),
    (N'registries.entryUsage.none', N'en', N'None found', 1),
    (N'registries.entryUsage.childrenTitle', N'en', N'Child entries', 1),
    (N'registries.entryUsage.substancesTitle', N'en', N'Methodologies that declare it as a substance', 1),
    (N'registries.entryUsage.substanceLink', N'en', N'Methodology versions', 1),
    (N'registries.entryUsage.columnsTitle', N'en', N'Template columns that take values from this registry', 1),
    (N'registries.entryUsage.columnsHint', N'en', N'These columns can hold this entry; the documents that actually hold it are listed above.', 1),
    (N'registries.entryUsage.truncated', N'en', N'The registry-level list is cut short by the server: some referring fields, columns or substances may be missing here.', 1),
    (N'registries.entryUsage.notListed', N'en', N'Not listed here per entry: methodology constants and cascade links. Deleting the entry reports how many of them there are, by kind.', 1)
    -- ru/kz — окремою порцією `REGCTOR814` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- REGCTOR814 ── кінець секції ──
