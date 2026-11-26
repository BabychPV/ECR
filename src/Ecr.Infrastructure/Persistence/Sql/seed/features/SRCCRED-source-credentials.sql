-- SRCCRED:keys ── ru/kz P-1126-SRCCRED: облікові дані джерела (логін і пароль для PiSqlClient/Sql), стани «пароль не читається» / «вхід відхилено», відмови збереження й перевірки з форми; власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'sources.authMode', N'ru', N'Аутентификация'),
    (N'sources.authModeHint', N'ru', N'Как ECR входит в источник. Windows — под служебной учётной записью ECR. Имя пользователя и пароль — только для PI SQL Client и Sql; пароль хранится зашифрованным и никогда не показывается.'),
    (N'sources.authMode.Windows', N'ru', N'Windows (служебная учётная запись ECR)'),
    (N'sources.authMode.UserPassword', N'ru', N'Имя пользователя и пароль'),
    (N'sources.userName', N'ru', N'Имя пользователя'),
    (N'sources.userNameHint', N'ru', N'Учётная запись источника, до 256 символов. Пример: DOMAIN\ecr_reader'),
    (N'sources.password', N'ru', N'Пароль'),
    (N'sources.passwordHint', N'ru', N'До 256 символов, пробелы сохраняются. После сохранения пароль не показывается; его можно только заменить или удалить.'),
    (N'sources.passwordSet', N'ru', N'•••• задан'),
    (N'sources.changePassword', N'ru', N'Изменить пароль'),
    (N'sources.clearPassword', N'ru', N'Удалить пароль'),
    (N'sources.switchToWindowsConfirm', N'ru', N'При переходе на Windows сохранённые имя пользователя и пароль будут удалены. Продолжить?'),
    (N'sources.credentialsRejected', N'ru', N'Источник отклонил имя пользователя и пароль; сбор и проверка остановлены. Введите пароль заново.'),
    (N'sources.credentialsUnreadable', N'ru', N'Сохранённый пароль не удаётся прочитать (например, база восстановлена из резервной копии). Введите пароль заново.'),
    (N'integration.test.failed.credentialsUnreadable', N'ru', N'Сохранённый пароль источника не удаётся прочитать. Введите пароль заново.'),
    (N'integration.test.failed.credentialsRejected', N'ru', N'Источник уже отклонил сохранённые имя пользователя и пароль; до замены пароля подключение не выполняется, чтобы не заблокировать учётную запись.'),
    (N'err.ECR-INT-0502.credentialsUnreadable', N'ru', N'Сохранённый пароль источника не удаётся прочитать. Введите пароль заново.'),
    (N'err.ECR-INT-0502.credentialsRejected', N'ru', N'Источник отклонил сохранённые имя пользователя и пароль; до замены пароля подключение не выполняется.'),
    (N'err.ECR-REQ-0422.dataSourcePasswordReentryRequired', N'ru', N'Адрес, транспорт или имя пользователя изменились: введите пароль заново. Сохранённый пароль отправляется только туда, куда его ввели.'),
    (N'err.ECR-REQ-0422.dataSourceCredentialsInvalid', N'ru', N'Для входа по имени пользователя и паролю нужны оба значения: имя до 256 символов без управляющих символов, пароль до 256 символов. В строке подключения при этом не должно быть Integrated Security, Trusted_Connection или учётных данных.'),
    (N'err.ECR-REQ-0422.dataSourceCredentialsTransport', N'ru', N'Имя пользователя и пароль поддерживаются только для PI SQL Client и Sql. Для PI Web API выберите Windows.'),
    (N'err.ECR-REQ-0422.dataSourceTestTransport', N'ru', N'Проверка несохранённых значений формы доступна только для PI SQL Client и Sql. Сохраните подключение PI Web API и проверьте его.'),
    (N'err.ECR-REQ-0429.tooManySourceTests', N'ru', N'Слишком много проверок подключения. Подождите минуту и повторите.'),
    (N'err.ECR-REQ-0429.sourceTestTargetPaused', N'ru', N'Источник несколько раз подряд отклонил вход. Проверки этого адреса приостановлены на 15 минут, чтобы не заблокировать учётную запись.'),
    (N'audit.eventLabel.DataSourceCredentialChanged', N'ru', N'Изменены учётные данные источника'),
    (N'sources.authMode', N'kz', N'Аутентификация'),
    (N'sources.authModeHint', N'kz', N'ECR көзге қалай кіреді. Windows — ECR қызметтік тіркелгісі атынан. Пайдаланушы аты мен құпиясөз — тек PI SQL Client және Sql үшін; құпиясөз шифрланып сақталады және ешқашан көрсетілмейді.'),
    (N'sources.authMode.Windows', N'kz', N'Windows (ECR қызметтік тіркелгісі)'),
    (N'sources.authMode.UserPassword', N'kz', N'Пайдаланушы аты және құпиясөз'),
    (N'sources.userName', N'kz', N'Пайдаланушы аты'),
    (N'sources.userNameHint', N'kz', N'Көздің тіркелгісі, 256 таңбаға дейін. Мысал: DOMAIN\ecr_reader'),
    (N'sources.password', N'kz', N'Құпиясөз'),
    (N'sources.passwordHint', N'kz', N'256 таңбаға дейін, бос орындар сақталады. Сақтағаннан кейін құпиясөз көрсетілмейді; оны тек ауыстыруға немесе жоюға болады.'),
    (N'sources.passwordSet', N'kz', N'•••• орнатылған'),
    (N'sources.changePassword', N'kz', N'Құпиясөзді өзгерту'),
    (N'sources.clearPassword', N'kz', N'Құпиясөзді жою'),
    (N'sources.switchToWindowsConfirm', N'kz', N'Windows-қа ауысқанда сақталған пайдаланушы аты мен құпиясөз жойылады. Жалғастыру керек пе?'),
    (N'sources.credentialsRejected', N'kz', N'Көз пайдаланушы аты мен құпиясөзді қабылдамады; жинау және тексеру тоқтатылды. Құпиясөзді қайта енгізіңіз.'),
    (N'sources.credentialsUnreadable', N'kz', N'Сақталған құпиясөзді оқу мүмкін емес (мысалы, дерекқор сақтық көшірмеден қалпына келтірілген). Құпиясөзді қайта енгізіңіз.'),
    (N'integration.test.failed.credentialsUnreadable', N'kz', N'Көздің сақталған құпиясөзін оқу мүмкін емес. Құпиясөзді қайта енгізіңіз.'),
    (N'integration.test.failed.credentialsRejected', N'kz', N'Көз сақталған пайдаланушы аты мен құпиясөзді қабылдамаған; тіркелгі бұғатталмауы үшін құпиясөз ауыстырылғанша қосылым орындалмайды.'),
    (N'err.ECR-INT-0502.credentialsUnreadable', N'kz', N'Көздің сақталған құпиясөзін оқу мүмкін емес. Құпиясөзді қайта енгізіңіз.'),
    (N'err.ECR-INT-0502.credentialsRejected', N'kz', N'Көз сақталған пайдаланушы аты мен құпиясөзді қабылдамады; құпиясөз ауыстырылғанша қосылым орындалмайды.'),
    (N'err.ECR-REQ-0422.dataSourcePasswordReentryRequired', N'kz', N'Мекенжай, көлік немесе пайдаланушы аты өзгерді: құпиясөзді қайта енгізіңіз. Сақталған құпиясөз тек ол енгізілген жерге жіберіледі.'),
    (N'err.ECR-REQ-0422.dataSourceCredentialsInvalid', N'kz', N'Пайдаланушы аты мен құпиясөз арқылы кіру үшін екеуі де қажет: басқарушы таңбаларсыз 256 таңбаға дейінгі атау және 256 таңбаға дейінгі құпиясөз. Бұл кезде қосылым жолында Integrated Security, Trusted_Connection немесе тіркелгі деректері болмауы тиіс.'),
    (N'err.ECR-REQ-0422.dataSourceCredentialsTransport', N'kz', N'Пайдаланушы аты мен құпиясөз тек PI SQL Client және Sql үшін қолданылады. PI Web API үшін Windows таңдаңыз.'),
    (N'err.ECR-REQ-0422.dataSourceTestTransport', N'kz', N'Пішіннің сақталмаған мәндерін тексеру тек PI SQL Client және Sql үшін қолжетімді. PI Web API қосылымын сақтап, содан кейін тексеріңіз.'),
    (N'err.ECR-REQ-0429.tooManySourceTests', N'kz', N'Қосылым тексерулері тым көп. Бір минут күтіп, қайталаңыз.'),
    (N'err.ECR-REQ-0429.sourceTestTargetPaused', N'kz', N'Көз кіруден бірнеше рет қатарынан бас тартты. Тіркелгі бұғатталмауы үшін осы мекенжайды тексеру 15 минутқа тоқтатылды.'),
    (N'audit.eventLabel.DataSourceCredentialChanged', N'kz', N'Көздің тіркелгі деректері өзгертілді')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- SRCCRED:keys ── кінець секції ──

-- SRCCRED:keys ── en: P-1126-SRCCRED; ru/kz — порцією SRCCRED:keys вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'sources.authMode', N'en', N'Authentication', 1),
    (N'sources.authModeHint', N'en', N'How ECR signs in to the source. Windows uses the ECR service account. User name and password is for PI SQL Client and Sql only; the password is stored encrypted and is never shown.', 1),
    (N'sources.authMode.Windows', N'en', N'Windows (ECR service account)', 1),
    (N'sources.authMode.UserPassword', N'en', N'User name and password', 1),
    (N'sources.userName', N'en', N'User name', 1),
    (N'sources.userNameHint', N'en', N'The account on the source, up to 256 characters. Example: DOMAIN\ecr_reader', 1),
    (N'sources.password', N'en', N'Password', 1),
    (N'sources.passwordHint', N'en', N'Up to 256 characters; spaces are kept. After saving, the password is never shown; you can only replace or clear it.', 1),
    (N'sources.passwordSet', N'en', N'•••• set', 1),
    (N'sources.changePassword', N'en', N'Change password', 1),
    (N'sources.clearPassword', N'en', N'Clear password', 1),
    (N'sources.switchToWindowsConfirm', N'en', N'Switching to Windows deletes the saved user name and password. Continue?', 1),
    (N'sources.credentialsRejected', N'en', N'The source refused the user name and password; collection and tests are stopped. Enter the password again.', 1),
    (N'sources.credentialsUnreadable', N'en', N'The saved password cannot be read (for example, the database was restored from a backup). Enter the password again.', 1),
    (N'integration.test.failed.credentialsUnreadable', N'en', N'The saved password of the source cannot be read. Enter the password again.', 1),
    (N'integration.test.failed.credentialsRejected', N'en', N'The source has already refused the saved user name and password; no connection is made until the password is replaced, so that the account is not locked.', 1),
    (N'err.ECR-INT-0502.credentialsUnreadable', N'en', N'The saved password of the source cannot be read. Enter the password again.', 1),
    (N'err.ECR-INT-0502.credentialsRejected', N'en', N'The source refused the saved user name and password; no connection is made until the password is replaced.', 1),
    (N'err.ECR-REQ-0422.dataSourcePasswordReentryRequired', N'en', N'The address, transport or user name changed: enter the password again. A saved password is only sent where it was entered for.', 1),
    (N'err.ECR-REQ-0422.dataSourceCredentialsInvalid', N'en', N'Signing in with a user name and password needs both: a user name of up to 256 characters without control characters and a password of up to 256 characters. The connection string must then not contain Integrated Security, Trusted_Connection or credentials.', 1),
    (N'err.ECR-REQ-0422.dataSourceCredentialsTransport', N'en', N'A user name and password are supported for PI SQL Client and Sql only. Choose Windows for PI Web API.', 1),
    (N'err.ECR-REQ-0422.dataSourceTestTransport', N'en', N'Testing unsaved form values is available for PI SQL Client and Sql only. Save the PI Web API connection and test it.', 1),
    (N'err.ECR-REQ-0429.tooManySourceTests', N'en', N'Too many connection tests. Wait a minute and try again.', 1),
    (N'err.ECR-REQ-0429.sourceTestTargetPaused', N'en', N'The source refused the sign-in several times in a row. Tests of this address are paused for 15 minutes so that the account is not locked.', 1),
    (N'audit.eventLabel.DataSourceCredentialChanged', N'en', N'Source credentials changed', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- SRCCRED:keys ── кінець секції ──

-- SRCCRED:changed ── наявні тексти, що вели «облікові дані — ніколи» (R-19): тепер ведуть до полів «Authentication» ──
-- Змінені тексти наявних ключів: нові бази беруть нове значення з 00-prelude.sql / CATALOG-en-base.sql /
-- COLL-source-form-hints.sql; епілог оновлює наявні бази, лише поки там стоїть САМЕ старе значення.
INSERT INTO #UiChanged ([Key], Lang, OldVal, NewVal)
SELECT v.[Key], v.Lang, v.OldVal, v.NewVal
  FROM (VALUES
    (N'sources.endpointHint.PiSqlClient', N'en', N'ODBC connection string of the PI SQL Client driver: Driver={PI SQL Client};AF Server=<server>;AF Database=<database>;Integrated Security=SSPI. Do not enter a user name or password: the server rejects them. Up to 400 characters.', N'ODBC connection string of the PI SQL Client driver: Driver={PI SQL Client};AF Server=<server>;AF Database=<database>;Integrated Security=SSPI. Do not put a user name or password here: choose User name and password under Authentication instead and remove Integrated Security. Up to 400 characters.'),
    (N'sources.endpointHint.PiSqlClient', N'ru', N'Строка подключения ODBC драйвера PI SQL Client: Driver={PI SQL Client};AF Server=<сервер>;AF Database=<база>;Integrated Security=SSPI. Имя пользователя и пароль не вводите: сервер их отклонит. До 400 символов.', N'Строка подключения ODBC драйвера PI SQL Client: Driver={PI SQL Client};AF Server=<сервер>;AF Database=<база>;Integrated Security=SSPI. Имя пользователя и пароль сюда не вводите: выберите «Имя пользователя и пароль» в поле «Аутентификация» и уберите Integrated Security. До 400 символов.'),
    (N'sources.endpointHint.PiSqlClient', N'kz', N'PI SQL Client драйверінің ODBC қосылым жолы: Driver={PI SQL Client};AF Server=<сервер>;AF Database=<база>;Integrated Security=SSPI. Пайдаланушы атын мен құпиясөзді енгізбеңіз: сервер оларды қабылдамайды. 400 таңбаға дейін.', N'PI SQL Client драйверінің ODBC қосылым жолы: Driver={PI SQL Client};AF Server=<сервер>;AF Database=<база>;Integrated Security=SSPI. Пайдаланушы аты мен құпиясөзді мұнда енгізбеңіз: «Аутентификация» өрісінде «Пайдаланушы аты және құпиясөз» тармағын таңдап, Integrated Security параметрін алып тастаңыз. 400 таңбаға дейін.'),
    (N'sources.endpointHint.Sql', N'en', N'SQL Server connection string: Server=<server>;Database=<database>;Integrated Security=true. Do not enter a password: the server rejects it. Up to 400 characters.', N'SQL Server connection string: Server=<server>;Database=<database>;Integrated Security=true. Do not put a user name or password here: choose User name and password under Authentication instead and remove Integrated Security. Up to 400 characters.'),
    (N'sources.endpointHint.Sql', N'ru', N'Строка подключения SQL Server: Server=<сервер>;Database=<база>;Integrated Security=true. Пароль не вводите: сервер его отклонит. До 400 символов.', N'Строка подключения SQL Server: Server=<сервер>;Database=<база>;Integrated Security=true. Имя пользователя и пароль сюда не вводите: выберите «Имя пользователя и пароль» в поле «Аутентификация» и уберите Integrated Security. До 400 символов.'),
    (N'sources.endpointHint.Sql', N'kz', N'SQL Server қосылым жолы: Server=<сервер>;Database=<база>;Integrated Security=true. Құпиясөзді енгізбеңіз: сервер оны қабылдамайды. 400 таңбаға дейін.', N'SQL Server қосылым жолы: Server=<сервер>;Database=<база>;Integrated Security=true. Пайдаланушы аты мен құпиясөзді мұнда енгізбеңіз: «Аутентификация» өрісінде «Пайдаланушы аты және құпиясөз» тармағын таңдап, Integrated Security параметрін алып тастаңыз. 400 таңбаға дейін.'),
    (N'err.ECR-REQ-0422.dataSourceEndpointCarriesSecret', N'en', N'The address of a data source must not carry credentials: sources connect under the service account.', N'The address of a data source must not carry credentials. Enter the user name and password in the Authentication fields instead (PI SQL Client and Sql).'),
    (N'err.ECR-REQ-0422.dataSourceEndpointCarriesSecret', N'ru', N'Адрес источника данных не должен содержать учётные данные: источники подключаются под служебной учётной записью.', N'Адрес источника данных не должен содержать учётные данные. Имя пользователя и пароль вводятся в полях «Аутентификация» (PI SQL Client и Sql).'),
    (N'err.ECR-REQ-0422.dataSourceEndpointCarriesSecret', N'kz', N'Деректер көзінің мекенжайында тіркелгі деректері болмауы тиіс: көздер қызметтік тіркелгі атынан қосылады.', N'Деректер көзінің мекенжайында тіркелгі деректері болмауы тиіс. Пайдаланушы аты мен құпиясөз «Аутентификация» өрістерінде енгізіледі (PI SQL Client және Sql).'),
    (N'integration.test.failed.auth', N'en', N'The source refused the credentials of the service account.', N'The source refused the credentials (the service account, or the user name and password of the connection).'),
    (N'integration.test.failed.auth', N'ru', N'Источник отклонил учётные данные служебной учётной записи.', N'Источник отклонил учётные данные (служебной учётной записи или имя пользователя и пароль подключения).'),
    (N'integration.test.failed.auth', N'kz', N'Көз қызметтік тіркелгінің тіркелгі деректерін қабылдамады.', N'Көз тіркелгі деректерін қабылдамады (қызметтік тіркелгі немесе қосылымның пайдаланушы аты мен құпиясөзі).')
       ) AS v ([Key], Lang, OldVal, NewVal)
OPTION (RECOMPILE);
GO
-- SRCCRED:changed ── кінець секції ──
