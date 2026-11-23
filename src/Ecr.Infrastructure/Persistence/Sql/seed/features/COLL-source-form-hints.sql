-- COLL:source-form-hints ── ru/kz підказки полів діалогу «Нове підключення» (/admin/sources): що вводити, формат, приклад; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'sources.codeHint', N'ru', N'Короткий уникальный код: латинские буквы, цифры и подчёркивание, первый символ — буква, до 64 символов. После сохранения изменить нельзя. Пример: PI_MAIN'),
    (N'sources.nameHint', N'ru', N'Название в списках, на текущем языке интерфейса. Пример: Основной сервер PI'),
    (N'sources.namePlaceholder', N'ru', N'Основной сервер PI'),
    (N'sources.transportHint.PiSqlClient', N'ru', N'PI SQL Client (ODBC, RTQP): основной канал чтения данных PI AF.'),
    (N'sources.transportHint.PiWebApi', N'ru', N'PI Web API: чтение PI AF по HTTP или HTTPS.'),
    (N'sources.transportHint.Sql', N'ru', N'Обычная база SQL Server, читается через SqlClient.'),
    (N'sources.endpointHint.PiSqlClient', N'ru', N'Строка подключения ODBC драйвера PI SQL Client: Driver={PI SQL Client};AF Server=<сервер>;AF Database=<база>;Integrated Security=SSPI. Имя пользователя и пароль не вводите: сервер их отклонит. До 400 символов.'),
    (N'sources.endpointHint.PiWebApi', N'ru', N'Базовый URL PI Web API (http или https) без имени пользователя и пароля; localhost и link-local адреса отклоняются. До 400 символов.'),
    (N'sources.endpointHint.Sql', N'ru', N'Строка подключения SQL Server: Server=<сервер>;Database=<база>;Integrated Security=true. Пароль не вводите: сервер его отклонит. До 400 символов.'),
    (N'sources.secondaryEndpointHint.PiSqlClient', N'ru', N'Необязательный резервный адрес в том же формате, что и «Адрес». Используется до конца запуска сбора, если основной адрес недоступен; при отказе во входе не используется.'),
    (N'sources.secondaryEndpointHint.PiWebApi', N'ru', N'Необязательный резервный адрес в том же формате, что и «Адрес». Используется до конца запуска сбора, если основной адрес недоступен; при отказе во входе не используется.'),
    (N'sources.secondaryEndpointHint.Sql', N'ru', N'Для транспорта Sql не используется: оставьте пустым.'),
    (N'sources.catalogHint.PiSqlClient', N'ru', N'Для PI SQL Client не используется: укажите AF Database в строке подключения.'),
    (N'sources.catalogHint.PiWebApi', N'ru', N'Имя базы PI AF, элементы которой просматриваются. До 200 символов. Пример: ECR'),
    (N'sources.catalogHint.Sql', N'ru', N'Необязательное имя базы данных; заменяет Database / Initial Catalog из строки подключения. До 200 символов. Пример: Reports'),
    (N'sources.maxParallelHint', N'ru', N'Целое число от 1 до 32; пусто — значение по умолчанию, 4.'),
    (N'sources.codeHint', N'kz', N'Қысқа бірегей код: латын әріптері, цифрлар және асты сызу, бірінші таңба — әріп, 64 таңбаға дейін. Сақтағаннан кейін өзгертуге болмайды. Мысал: PI_MAIN'),
    (N'sources.nameHint', N'kz', N'Тізімдердегі атау, интерфейстің ағымдағы тілінде. Мысал: Негізгі PI сервері'),
    (N'sources.namePlaceholder', N'kz', N'Негізгі PI сервері'),
    (N'sources.transportHint.PiSqlClient', N'kz', N'PI SQL Client (ODBC, RTQP): PI AF деректерін оқудың негізгі арнасы.'),
    (N'sources.transportHint.PiWebApi', N'kz', N'PI Web API: PI AF деректерін HTTP немесе HTTPS арқылы оқу.'),
    (N'sources.transportHint.Sql', N'kz', N'Кәдімгі SQL Server дерекқоры, SqlClient арқылы оқылады.'),
    (N'sources.endpointHint.PiSqlClient', N'kz', N'PI SQL Client драйверінің ODBC қосылым жолы: Driver={PI SQL Client};AF Server=<сервер>;AF Database=<база>;Integrated Security=SSPI. Пайдаланушы атын мен құпиясөзді енгізбеңіз: сервер оларды қабылдамайды. 400 таңбаға дейін.'),
    (N'sources.endpointHint.PiWebApi', N'kz', N'PI Web API базалық URL мекенжайы (http немесе https), пайдаланушы атынсыз және құпиясөзсіз; localhost және link-local мекенжайлары қабылданбайды. 400 таңбаға дейін.'),
    (N'sources.endpointHint.Sql', N'kz', N'SQL Server қосылым жолы: Server=<сервер>;Database=<база>;Integrated Security=true. Құпиясөзді енгізбеңіз: сервер оны қабылдамайды. 400 таңбаға дейін.'),
    (N'sources.secondaryEndpointHint.PiSqlClient', N'kz', N'Міндетті емес резервтік мекенжай, «Мекенжай» өрісімен бірдей пішімде. Негізгі мекенжай қолжетімсіз болса, жинау іске қосылымының соңына дейін қолданылады; кіруден бас тартылғанда қолданылмайды.'),
    (N'sources.secondaryEndpointHint.PiWebApi', N'kz', N'Міндетті емес резервтік мекенжай, «Мекенжай» өрісімен бірдей пішімде. Негізгі мекенжай қолжетімсіз болса, жинау іске қосылымының соңына дейін қолданылады; кіруден бас тартылғанда қолданылмайды.'),
    (N'sources.secondaryEndpointHint.Sql', N'kz', N'Sql көлігі үшін қолданылмайды: бос қалдырыңыз.'),
    (N'sources.catalogHint.PiSqlClient', N'kz', N'PI SQL Client үшін қолданылмайды: AF Database мәнін қосылым жолында көрсетіңіз.'),
    (N'sources.catalogHint.PiWebApi', N'kz', N'Элементтері қаралатын PI AF дерекқорының атауы. 200 таңбаға дейін. Мысал: ECR'),
    (N'sources.catalogHint.Sql', N'kz', N'Міндетті емес дерекқор атауы; қосылым жолындағы Database / Initial Catalog мәнін ауыстырады. 200 таңбаға дейін. Мысал: Reports'),
    (N'sources.maxParallelHint', N'kz', N'1-ден 32-ге дейінгі бүтін сан; бос болса — әдепкі мән, 4.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:source-form-hints ── кінець секції ──

-- COLL:source-form-hints ── en підказки полів діалогу «New connection» (UI, DataSourceFormModal); зміст звірено з DataSourceHandlers/DataSourceEndpointPolicy/адаптерами ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'sources.codeHint', N'en', N'A short unique code: Latin letters, digits and underscore, starting with a letter, up to 64 characters. It cannot be changed after saving. Example: PI_MAIN', 1),
    (N'sources.nameHint', N'en', N'The name shown in lists, in the current interface language. Example: Main PI server', 1),
    (N'sources.namePlaceholder', N'en', N'Main PI server', 1),
    (N'sources.transportHint.PiSqlClient', N'en', N'PI SQL Client (ODBC, RTQP): the main channel for reading PI AF data.', 1),
    (N'sources.transportHint.PiWebApi', N'en', N'PI Web API: reads PI AF over HTTP or HTTPS.', 1),
    (N'sources.transportHint.Sql', N'en', N'A regular SQL Server database, read with SqlClient.', 1),
    (N'sources.endpointHint.PiSqlClient', N'en', N'ODBC connection string of the PI SQL Client driver: Driver={PI SQL Client};AF Server=<server>;AF Database=<database>;Integrated Security=SSPI. Do not enter a user name or password: the server rejects them. Up to 400 characters.', 1),
    (N'sources.endpointHint.PiWebApi', N'en', N'Base URL of PI Web API (http or https) without a user name or password; localhost and link-local addresses are rejected. Up to 400 characters.', 1),
    (N'sources.endpointHint.Sql', N'en', N'SQL Server connection string: Server=<server>;Database=<database>;Integrated Security=true. Do not enter a password: the server rejects it. Up to 400 characters.', 1),
    (N'sources.secondaryEndpointHint.PiSqlClient', N'en', N'An optional backup address in the same format as Endpoint. It is used for the rest of a collection run when the main address cannot be reached; it is not used when the login is refused.', 1),
    (N'sources.secondaryEndpointHint.PiWebApi', N'en', N'An optional backup address in the same format as Endpoint. It is used for the rest of a collection run when the main address cannot be reached; it is not used when the login is refused.', 1),
    (N'sources.secondaryEndpointHint.Sql', N'en', N'Not used by the Sql transport: leave it empty.', 1),
    (N'sources.catalogHint.PiSqlClient', N'en', N'Not used by PI SQL Client: put AF Database into the connection string instead.', 1),
    (N'sources.catalogHint.PiWebApi', N'en', N'The name of the PI AF database whose elements are browsed. Up to 200 characters. Example: ECR', 1),
    (N'sources.catalogHint.Sql', N'en', N'An optional database name; it replaces Database / Initial Catalog from the connection string. Up to 200 characters. Example: Reports', 1),
    (N'sources.maxParallelHint', N'en', N'A whole number from 1 to 32; leave it empty for the default, 4.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:source-form-hints ── кінець секції ──
