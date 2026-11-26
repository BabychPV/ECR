# DESIGN: source-credentials (P-1126-SRCCRED), R3 — REV 2

> **Rev 2 (після незалежного огляду `REVIEW-source-credentials.md`, 24 знахідки).** Де текст §0–§11 суперечить §12–§16, **чинні §12–§16** (Review resolution, фінальні AC, нарізка, відкриті питання). Застарілі місця в §0–§11 позначено «⟶ rev2».

Картка: `.sync-local\ready\integ-source-credentials.md`. Гілка: `lane/integ-source-credentials`.
Код читано з worktree `C:\ecr-wt\audit-followup` (SHA 6260abdb8 = live). Жодного коду, коміту чи дії на live.
Шляхи нижче відносні до кореня репо; `файл:рядок` — з цього SHA.

## 0. Головні висновки (прочитати першими)

1. **Картка не повністю точна щодо «сховища секретів немає».** Секрет-за-іменем УЖЕ є: `ISecretProvider.Find(SecretName)`
   (`src/Ecr.Application/Ports/ISecretProvider.cs:21-31`, значення з середовища процесу), і адаптери його вже вставляють:
   PiSqlClient `PWD` (`src/Ecr.Adapters.PiAf/PiSqlClientDataSource.cs:1199-1202`), Sql `builder.Password`
   (`src/Ecr.Adapters.Sql/SqlDataSource.cs:560-563`). Нового немає лише: (а) логіна (UID/User ID не вставляється нігде),
   (б) зберігання пароля в БД. Новий шлях має ПОСУНУТИ, а не зламати env-секрет (див. §5, питання Q-1).
2. **Прецедент SMTP копіюється майже дослівно** (§2), але DataSource — не singleton, має `RowVersion`/`If-Match` і вже має
   S3-захист «секрет іде лише на ту адресу, яку підтвердив той, хто його знає» (`SecretConfirmation`) — треба узгодити (§4).
3. **DataProtection на live**: ключі в БД (`sec.DataProtectionKey`, `PersistKeysToDbContext`) → переживають рестарт
   служби. Але live-env прочитати неможливо (див. §6): за артефактами `.sync-local` стенди ганяли з
   `AllowUnprotectedKeys=true`; чи так на live — НЕ ПІДТВЕРДЖЕНО. Це умова зупинки картки (див. §9).
4. Збір і Test connection проходять через ТОЙ САМИЙ `OpenAsync` адаптера → одна точка підстановки облікових даних на транспорт.

## 1. Поточний стан (AC-4, AC-5, AC-R)

| Що | Де |
|---|---|
| Заборона облікових даних в адресі | `DataSourceHandlers.cs:694` `RequireNoCredentials`; маркери `:672-675` = `password= pwd= secret= token= apikey= api-key= accesskey= sig=` + `scheme://user:pass@`(`HasUserInfo :722`). Викликається для `endpoint` і `secondaryEndpoint` у `ValidateAsync :502-503`. Відмова не повторює вміст: `messageKey err.ECR-REQ-0422.dataSourceEndpointCarriesSecret`, `field` |
| Маркерів логіна НЕМАЄ | `uid=`, `user id=`, `user=` не в списку → `UID=Administrator` в адресі сьогодні ПРОХОДИТЬ (див. Q-2) |
| Адреса PiSqlClient/Sql | `Endpoint` (nvarchar(400)) = рядок з'єднання ODBC (PiSqlClient) / SqlClient (Sql). Валідація: `RequireSqlServerAddressAsync :553-617` (SSRF-політика, голе ім'я заборонене `:588`). Sql: `CheckSqlClientConnectionString` (`DataSourceEndpointPolicy.cs:139`); `Authentication=` лише порожнє/`SqlPassword` (`:322-323`) — тобто SQL-логін політика вже допускає (HU-11 Q9) |
| Секрет-за-іменем | `SecretName = "DataSource." + code` (`DataSourceHandlers.cs:195, 289`), колонка `SecretName nvarchar(100) NOT NULL` (`ExternalConfiguration.cs:29`). `HasSecret` у DTO = `secrets.Find(...) is not null` (`DataSourceHandlers.cs:103`) |
| S3 (секрет не їде на нову адресу) | `RequireSecretConfirmation :759-783`, `IsNegotiate :785`; для джерел без env-секрету — `ConfirmEndpointChange` (`:374-376`, ключ `EndpointChangeUnconfirmedKey`) |
| Test connection | `POST /api/v1/data-sources/{id}/test` (`DataSourcesController.cs:198-210`) → `TestDataSourceConnectionHandler.HandleAsync :1041` → `ProbeAsync :1097` → `adapter.DiscoverAsync(source.Id)` (`:1111`); без каталогу — `IExternalConnectionCheck.CheckConnectionAsync (:1121)`. Лише збережене джерело (по `id`), форма НЕ тестує незбережені значення. Право `Integration.Manage`, причина обов'язкова, аудит `DataSourceConnectionTested` без адреси (`:1086-1091`); у тілі — лише категорія (`FailureCategories :1019`, `auth` тощо) |
| PiSqlClient з'єднання | `PiSqlClientDataSource.OpenAsync :1156` → `Failover.RunAsync` (`EndpointFailover.cs:51-81`: основна, при `connectFailed` — `SecondaryEndpoint`) → `OpenEndpointAsync :1169`: `new OdbcConnectionStringBuilder(endpoint)` (`:1176`), політика адреси `:1197`, `builder["PWD"]=secret` (`:1201`), `new OdbcConnection(builder.ConnectionString)` (`:1204`). Windows-вхід = відсутність UID/PWD у рядку (як в endpoint FLERT) |
| Sql з'єднання | `SqlDataSource.OpenAsync :521`: `SqlConnectionStringBuilder(source.Endpoint)` (`:528`), політика `:550`, `Catalog` перекриває (`:555-558`), `builder.Password=secret` (`:562`). Запасної адреси Sql НЕ читає (`RequireReadByTransport :230-254`, `FieldNotReadKey`) |
| Secondary endpoint | Лише PiWebApi і PiSqlClient (`EndpointFailover`); те саме `secretName` → ті самі облікові дані; `OpenEndpointAsync` викликається з кожною адресою, тож новий код підстановки в ньому покриває обидві автоматично (AC-3) |
| Аудит конфігурації | `IntegrationConfigAudit.Snapshot(DataSource) :55-65` — код, транспорт, адреси, паралелізм, активність; без секрету (навіть імені). Подія `DataSourceSaved` (`DataSourceHandlers.cs:444-459`) — адреси без секрету |
| UI | `src/Ecr.Web/src/features/integration/DataSourceFormModal.tsx` (поля `secretConfirmation` `:119,192,471`), `DataSourceDrawer.tsx:58` (`hasSecret`), `dataSourceApi.ts:12` («дії замінити секрет не існує»); DTO `DataSourceView.HasSecret` (`DataSourceHandlers.cs:37-50`) |

## 2. Прецедент: SMTP (AC-2)

- **Сутність** `src/Ecr.Domain/Entities/Notifications/SmtpSettings.cs`: `PasswordProtected byte[]?` (`:68`), `HasPassword => PasswordProtected is {Length:>0}` (`:78`);
  `Update(...)` не чіпає пароль, але при `AuthMode.None` обнуляє його (`:123-126`); `ReplacePassword(byte[]? protected, utcNow, byUserId)` (`:132-136`; `null`/порожній → `null`); `Touch` ставить `UpdatedAt/UpdatedByUserId`.
- **Колонка**: `NotificationConfiguration.cs:116` `HasColumnType("varbinary(max)")`; міграція `20261001163355_D256SmtpSettingsAndChannelRoles.cs:102` (`type: "varbinary(max)", nullable: true`).
- **Шифрування** (⟶ rev2, виправлено неточність): порт `ISmtpPasswordProtector` (`src/Ecr.Application/Ports/ISmtpSettingsStore.cs:19-24`) має ЛИШЕ `Protect`; `Unprotect` є тільки на конкретному класі `SmtpPasswordProtector` (`NotificationStore.cs:178`), і sender бере саме конкретний тип (`SmtpNotificationSender.cs:43,60,342`). Тому новий порт `IDataSourceCredentialResolver` (§12 R-23) ДОДАЄ розшифровку в порт, а не «копіює» SMTP-порт. Клас `SmtpPasswordProtector` (`src/Ecr.Infrastructure/Notifications/NotificationStore.cs:168-178`): `Purpose = "Ecr.Smtp.Password.v1"`, `Protect(string)=_protector.Protect(UTF8)`, `Unprotect(byte[])`. Окрема purpose від каналів (`Ecr.Notifications.ChannelSecret.v1`, `:159`), реєстрація singleton `DependencyInjection.cs:571`. Unprotect викликає лише транспорт (`SmtpNotificationSender.cs:339-343`), ніколи API.
- **API-форма** (write-only): `SmtpSettingsInput(... string? Password, bool ClearPassword ...)` (`SmtpSettingsHandlers.cs:42-44`): `Password` null/порожній = «не змінювати»; `ClearPassword` = стерти; відповідь `SmtpSettingsView` містить лише `HasPassword` (`:26-29`). Ні значення, ні довжини.
- **Логіка збереження** (`SaveSmtpSettingsHandler :88-175`): `clears = ClearPassword || AuthMode==None` (`:108`); `willHavePassword` (`:110`); валідація «Password-режим потребує логіна і пароля» (`:235-240`); **S1** «змінено адресу/порт/шифрування/логін, а пароль не введено заново → 422 `smtpPasswordReentryRequired`» (`:133-139`, `EndpointChanged :189-193`) — збережений пароль не переноситься на чужий хост.
- **Аудит**: `AuditAndSaveAsync(... "SmtpSettingsCreated"/"SmtpSettingsUpdated", { ..., userName, passwordChanged = newPassword is not null || (clears && hadPassword) })` (`:155-170`); коментар «у журнал лише факт зміни пароля, не він і не довжина».
- **Право**: `System.ManageNotifications` (`SmtpSettingsHandlers.cs:54, 93`).
- **Тести-прецедент** — знайти за іменем `SmtpSettings*` у `tests/` (не читав; Q-7).

## 3. Дані (AC-R, AC-5)

Нові колонки `ext.DataSource` (сутність `src/Ecr.Domain/Entities/External/DataSource.cs`, конфіг `ExternalConfiguration.cs:17-36`):

| Колонка | Тип | Default / nullable | Примітка |
|---|---|---|---|
| `AuthMode` | `int`/tinyint (enum `SourceAuthMode { Windows=0, UserPassword=1 }`) | NOT NULL, default 0 | наявні рядки = Windows (AC-R) без UPDATE: default у DDL |
| `UserName` | `nvarchar(256)` | NULL | не секрет; може бути `DOMAIN\user` / `WIN-…\Administrator` |
| `PasswordProtected` | `varbinary(max)` | NULL | DataProtection-блоб, як SMTP |

- Домен: `Configure/Update` НЕ чіпають пароль; `ReplaceCredentials(authMode, userName, byte[]? protectedPassword)` і `ClearPassword()`; інваріанти: `Windows` ⇒ `UserName=null`, `PasswordProtected=null`; `UserPassword` ⇒ `UserName` непорожній; `HasPassword` обчислюване. Не писати нове у `Snapshot` значення — лише `authMode`, `userName`, `hasPassword` (без самого пароля й довжини).
- **Міграція**: ОДНА, за токеном міграцій (CLAUDE.md «одна міграція в польоті»), `AddColumn ×3`; `Up` не торкається даних; EF `EcrDbContextModelSnapshot.cs` оновлюється в тому ж коміті. Прецедент форми — D256 (`:102`). Ідемпотентний скрипт двічі через `sqlcmd -I -b` (правило «перевірка оновлення» перед мержем).
- **Rollback (rev2 R-16/R-17)**: у листі готовності — відкат/ff-відкат коду змінює особу, під якою ходять UserPassword-джерела (старий код ігнорує `AuthMode` і ходить як Windows): перед відкатом вимкнути ці джерела. Колонок у rev2 чотири (+`CredentialsRejectedAt`). `Down` = `DropColumn` ×4 (у тексті нижче «×3» читати як ×4). Дані: після відкату паролі втрачаються (джерела з `UserPassword` перестануть працювати — це очікувано, фіксувати в листі готовності). Старий код на новій схемі працює (колонки ігноруються) — зворотна сумісність при ff-пуші зверху вниз безпечна.
- **Сід/UI-тексти**: нові ключі каталогу (en/ru/kz/uk) у `Sql/seed/...` — пам'ять `agents-avoid-new-catalog-keys`: обов'язково перевірити сторожі каталогу (SeedTextUpdate, каталог ключів) і секцію сіду в іменованій секції (`-- SRC:…`); правило «Змінені тексти» не потрібне (нові ключі, не зміна).
- Тест AC-R: на копії БД «до» (наявні рядки FLERT/PI) застосувати міграцію → `AuthMode=0`, `UserName/PasswordProtected IS NULL`, збір працює як раніше; нуль викликів `Unprotect` (лічильник на фейковому захиснику).

## 4. Контракт (AC-1, AC-2, AC-4)

Файли: `DataSourcesController.cs:239-249` (`SaveDataSourceRequest`), `DataSourceHandlers.cs:37-50` (`DataSourceView`), `openapi.snapshot.json`, `src/Ecr.Web/src/api/schema.d.ts` (останній крок, один коміт).

- **GET /data-sources (і відповіді POST/PUT)**: `DataSourceView` + `authMode`, `userName`, `hasPassword`. ⛔ Ніколи не пароль, не довжина, не «замаскований» рядок фіксованої довжини. Існуючий `hasSecret` (env-секрет) лишається окремим полем, поведінка без змін.
- **POST/PUT**: у `SaveDataSourceRequest` додати `AuthMode?`, `UserName?`, `Password?` (write-only), `ClearPassword` — точна копія SMTP-семантики: `Password` null/порожній = лишити; `ClearPassword`=стерти; перехід у `Windows` стирає пароль і логін. ПРОПОЗИЦІЯ: окремі дії `POST /data-sources/{id}/credentials/password` (замінити) і `DELETE .../credentials/password` — відповідає AC-1 «Change password / Clear» і дає окремий аудит; але це більший контракт, ніж SMTP. Рішення — Q-3.
- **S1/S3 для нового пароля** (копія `SmtpSettings S1`): якщо змінилися `Endpoint`/`SecondaryEndpoint`/`Transport`/`UserName`, а `Password` не введено заново, при наявному `PasswordProtected` → 422 новий ключ `err.ECR-REQ-0422.dataSourcePasswordReentryRequired` (поле `password`). Інакше адмін з `Integration.Manage` перенаправив би збережений пароль на довільний хост. Існуючий `SecretConfirmation`/`ConfirmEndpointChange` лишаються для env-секрету/Windows.
- **If-Match**: уже є для PUT (`RequireCurrentVersion :117`); окремі дії пароля теж мають вимагати `If-Match` і ротувати `RowVersion`.
- **Валідація**: `UserPassword` без `UserName` або (при створенні/зміні режиму) без пароля → 422 `err.ECR-REQ-0422.dataSourceCredentialsInvalid` (або повторне використання `dataSourceInvalid`); довжини `UserName ≤256`, `Password ≤` узгодити (Q-4); керівні символи в логіні/паролі заборонити (ін'єкція `;`/`}` у рядок драйвера обробляється builder'ом, див. §5).
- **AC-4**: `RequireNoCredentials` лишається. Підказка «ведe на поля логіна» = новий `messageKey` або текст існуючого `dataSourceEndpointCarriesSecret` оновити в сіді (зміна наявного ключа → рядок у секції «Змінені тексти»). Розширити `CredentialMarkers` на `uid=`, `user id=`, `userid=` (Q-2 — ризик: наявне джерело з UID в адресі стане нересейвабельним до очищення).
- **Аудит**: нова подія безпеки `DataSourceCredentialChanged` (через `IAuditWriter.WriteSecurityEventAsync`, як `DataSourceSaved`, `DataSourceHandlers.cs:796-801`): `{id, code, authMode, userName, passwordChanged:bool, passwordCleared:bool}` — БЕЗ значення й БЕЗ довжини; `EventType` константа поряд з `SaveDataSourceHandler.EventType`. Журнал `IntegrationConfigAudit.Snapshot` доповнити `authMode`, `userName`, `hasPassword` (булеве).
- **Коди помилок**: нові лише `messageKey` під існуючим `ECR-REQ-0422` (без нового ErrorCode ⇒ 4-файлова процедура з пам'яті `registering-an-error-code` не потрібна, але ключі каталогу й клієнтський map `DataSourceFormModal.tsx:150` — так).

## 5. Runtime: підстановка лише в пам'яті (AC-3)

Принцип: розшифрування — у момент з'єднання, значення живе лише локальною змінною/`builder`, ніколи в полі, кеші, винятку, логу.

- **Порт**: новий `IDataSourceCredentialProtector { byte[] Protect(string); string Unprotect(byte[]) }` у `Ecr.Application/Ports` (копія `ISmtpPasswordProtector`), реалізація `DataSourceCredentialProtector` в `Ecr.Infrastructure` з `Purpose = "Ecr.DataSource.Credential.v1"` (окремий від SMTP і каналів). Адаптери (`Ecr.Adapters.PiAf/Sql`) НЕ повинні залежати від ASP.NET DataProtection: або вони отримують порт `IDataSourceCredentialResolver.Resolve(DataSource) → (string? User, string? Password)` (реалізація в Infrastructure), або порт-захисник інжектується (Q-5: перевірити архітектурні тести на залежності адаптерів).
- **PiSqlClient**: у `OpenEndpointAsync` після `RequireAllowedAddress(...)` (`:1197`) — політика бачить рядок БЕЗ секрету (важливо: так і зараз, коментар `:1121`); потім: якщо `AuthMode==UserPassword` → `builder["UID"]=user; builder["PWD"]=password` (і ПРИБРАТИ Trusted_Connection/Integrated Security, якщо присутні в `endpoint`, інакше драйвер може проігнорувати UID — Q-6). Env-секрет `secrets.Find` (`:1199`) лишається для `Windows`-режиму/зворотної сумісності лише як є (Q-1: пріоритет: БД-пароль > env).
- **Sql**: `SqlDataSource.OpenAsync` після `:555-558`: `builder.UserID=user; builder.Password=password; builder.IntegratedSecurity=false`; `Authentication` лишається порожнім/SqlPassword (політика `:322`).
- **Secondary**: той самий код у `OpenEndpointAsync` обслуговує обидві адреси (`EndpointFailover.cs:67,78`) — AC-3 «ті самі облікові дані» виконується автоматично, але потрібен тест на запасну адресу.
- **Test connection / DiscoverAsync / collect**: усе йде через `OpenAsync` — один тест на адаптер (AC-3) + один інтеграційний на handler `TestDataSourceConnectionHandler` (категорія `auth` при `SQLSTATE 28000`/`SqlException` auth, `IsAuthenticationFailure`, `PiSqlClientDataSource.cs:1307`; Sql `:585`).
- **Журнали/винятки — ВСІ місця, де можлива рядок з'єднання (перевірити кожне ⇒ тест-сторож)**:
  - `PiSqlClientDataSource.cs:1188` і `SqlDataSource.cs:542` — повідомлення без `ex.Message` (SEC TIER2), лише кореляція; `LogConnectionStringUnreadable(log, code, corr, ex)` (`:1289/:1302`, Sql `:634`) логує ВИНЯТОК `ArgumentException` парсера: його текст може цитувати фрагмент рядка. ⚠ Ризик зростає: якщо парсити РЯДОК ПІСЛЯ підстановки пароля — парсер-помилка потрапила б у лог разом з ним. Тому: парсити `endpoint` ДО підстановки (як зараз), пароль додавати лише в `builder` після успішного розбору, у `try` на відкриття НЕ логувати `builder.ConnectionString`.
  - `LogConnectFailed(log, code, SqlState/Number, corr, error)` (`PiSqlClientDataSource.cs:1278`, `SqlDataSource.cs:623`) логує `OdbcException`/`SqlException` ЦІЛКОМ. Тексти драйверів (ODBC «connection string …») ТЕОРЕТИЧНО можуть містити рядок; перевірити практично на Test connection з неправильним паролем (тестовий драйвер/фейк) — Q-8. Мітигація: логувати `SafeErrorText`/скрабувати `PWD=…`/`Password=…` перед записом (є `SafeErrorText.NewCorrelationId`, шукати його файл).
  - `EndpointFailover.LogFailover` — лише code і тип винятку (`:74,92`) — ОК.
  - `TestDataSourceConnectionHandler.LogProbeFailed(logger, e, code, category)` (`:1140,1151-1154`) логує виняток адаптера — адаптери вже кидають `BusinessRuleException` без рядка (див. вище), але `Classify(e)` може пропустити сирий виняток — перевірити.
  - Аудит: `DataSourceSaved` (`:444`), `DataSourceConnectionTested` (`:1086`), `IntegrationConfigAudit.Snapshot` — додавати лише булеві/логін.
  - Telemetry/OTel: перевірити, що SqlClient/ODBC-інструментація не пише `db.connection_string` (`src/Ecr.Api/Observability/TelemetrySetup.cs`) — Q-9.
  - Серіалізація `DataSource` у API: `DataSourceView` (не віддавати сутність); експорт конфігурації (`docs`/export — перевірити, що `PasswordProtected` не експортується — Q-10).
- **Пам'ять**: `string` пароля не можна зануляти в .NET; обмежити час життя (локальна змінна в `OpenEndpointAsync`), не класти в `Dictionary` Details винятків.

## 6. DataProtection-ключі на live (умова зупинки картки)

Конфігурація (код): `AuthenticationSetup.AddEcrDataProtection :274-341` — `SetApplicationName("Ecr")` (`:278`), `PersistKeysToDbContext<EcrDbContext>()` (`:279`) → таблиця `sec.DataProtectionKey` у БД ECR; захист ключів — `ProtectKeysWithCertificate(current)` лише якщо заданий `Auth:DataProtection:CertificateThumbprint` (`:292-300`), попередні сертифікати — `PreviousCertificateThumbprints` + `UnprotectKeysWithAnyCertificate` (`:309-325`); у Production без сертифіката старт відмовляє (`CheckKeyProtection :417`), крім `Auth:DataProtection:AllowUnprotectedKeys=true` (`:390`, Critical у лог, `/health/db` Degraded). Доки: `docs/admin/https-certificate.md:255-267`, `docs/admin/operations-runbook.md:127-129, 1107-1109`. `AddDataProtection` викликається ЛИШЕ в `src/Ecr.Api` (`https-certificate.md:53`; grep по `src` підтверджує єдине входження); колекція/Test connection ідуть в Api-процесі (адаптери реєструє `Program.cs:68-76`); Worker/Child адаптерів не реєструє (перевірено грепом, Q-11 — підтвердити).

Відповідь на питання «чи переживають ключі рестарт»: **так, за конструкцією** (кільце в БД, не в профілі). Що має триматися, щоб збережений пароль розшифровувався після рестарту:
1. Кільце не знищується: `sec.DataProtectionKey` не чиститься, БД не пересоздається (setup-dev-db DROP, restore старішого бекапу ⇒ ключі «до» + блоби «після» = нерозшифровувані; пам'ять `never-drop-ecr-database`, вікна RESTORE).
2. `SetApplicationName("Ecr")` і purpose не змінюються між збірками (`Ecr.DataSource.Credential.v1` — не перейменовувати).
3. Якщо live має сертифікат (`ECR_Auth__DataProtection__CertificateThumbprint`): сертифікат із закритим ключем лишається в `LocalMachine\My`; при заміні — `PreviousCertificateThumbprints` (інакше ключі нечитабельні, `https-certificate.md:368`). Якщо live без сертифіката (`AllowUnprotectedKeys=true`): ключі лежать відкрито в БД, шифрування пароля захищає лише від читання таблиці `ext.DataSource` без таблиці ключів — слабше, ніж виглядає (бекап БД містить і ключі, і блоби).
4. Ротація ключів DataProtection (90 днів) не ламає: старі ключі лишаються в кільці для розшифрування.

⚠ **НЕ ЗНАЮ (не вигадую)**: фактичний стан live. `%ProgramData%\ECR` містить лише `logs\` (перевірено read-only, жодних конфігів). Стенди з `.sync-local` (`STAND-*`, `K1-DEMO13-UI-PLAN-2026-10-18.md:14`, `RUNBOOK-5094-WINDOW-COMMANDS-2026-10-13.md:52`) запускалися з `ECR_Auth__DataProtection__AllowUnprotectedKeys=true`; стенд `EcrLandUi` :5094 — так само. Чи live (:5094/служба) має сертифікат — перевірити людиною/DBA: змінні середовища процесу `Ecr.Api` (`Get-CimInstance Win32_Process … ` без читання значень секретів) і наявність рядків `sec.DataProtectionKey` з `encryptedSecret` (`SELECT COUNT(*) … Xml LIKE '%encryptedSecret%'`, runbook `:1246`). Картка прямо ставить: «потреба в зміні DataProtection-ключів live» = умова зупинки. Рекомендація: до старту реалізації отримати від людини ТАК/НІ на «live має стабільне кільце в БД, без пересоздання БД між вікнами»; сертифікат — бажаний, не блокер, але ризик названий у листі готовності.

## 7. Права, UI, тести

**Права**: усі нові дії — `Integration.Manage` (`SaveDataSourceHandler.Permission`, `DataSourceHandlers.cs:192`), та сама перевірка, що PUT. Бачення: `Integration.View` бачить `authMode/userName/hasPassword` (не секрет); ніхто не бачить пароль. Питання, чи `userName` вважати чутливим для `Integration.View` (Q-12; за замовчуванням — видно, SMTP теж віддає `UserName`, `SmtpSettingsHandlers.cs:73`).

**UI (AC-1)** — `DataSourceFormModal.tsx` (+ `dataSourceApi.ts`, `DataSourceDrawer.tsx`) для PiSqlClient і Sql: перемикач «Authentication»: «Windows (обліковка служби ECR)» / «User name and password»; поля `User name`, `Password` (`type=password`, `autocomplete="new-password"`, write-only); у режимі редагування при `hasPassword`: показ «•••• set» (статичний, не залежний від довжини), кнопки «Change password» (відкриває порожнє поле) і «Clear»; для Windows поля приховані (стерти логін/пароль при перемиканні — з підтвердженням). Помилка `dataSourcePasswordReentryRequired` → мапа `DataSourceFormModal.tsx:150` на поле `password`. Сід текстів — нові ключі (4 мови). a11y/dark+light гейти (пам'ять `ui-change-needs-a11y-config-and-real-browser`, лише прогін e2e зачеплених екранів на CI, правило «e2e UI-lane»).

**Тести по AC**:
| AC | Тест |
|---|---|
| AC-0 | live-вікно, кроки людини (лише за словом координатора; не мій прогін) |
| AC-1 | vitest форми (перемикач, write-only поле, «•••• set», Change/Clear, відсутність значення пароля в DOM/`value`); e2e `sources*` на CI з `-grep` |
| AC-2 | API-тест: PUT з паролем → GET/list/`DataSourceView` JSON не містить рядка пароля ні довжини (перевірити весь JSON сканом по значенню); БД: `PasswordProtected` не дорівнює UTF8(пароль) і не містить його підрядка; аудит `DataSourceCredentialChanged` без значення; тест журналу: перехопити `ILogger` під час `Test`/`Discover` з неправильним паролем — жодного запису з паролем (маркер-значення `ZZpwd-<guid>`); мутація: покласти пароль у `Snapshot`/лог → тест червоний |
| AC-3 | тест адаптера: фейковий `OdbcConnection`/`SqlConnection` (чи перехоплений `builder.ConnectionString` через шов) → `UID/PWD` / `User ID/Password` у рядку лише в момент з'єднання; запасна адреса ті ж облікові; `Windows` — жодних UID/PWD |
| AC-4 | `RequireNoCredentials` тести: `Password=`, `PWD=`, `UID=`/`User ID=` (якщо додаємо), `://u:p@h` → 422 `dataSourceEndpointCarriesSecret`, відповідь не повторює вхід |
| AC-5 | регресія: наявні `DataSource*`/`PiSqlClient*`/`SqlDataSource*` тести без змін; FLERT Windows: рядок з'єднання ідентичний до і після міграції (знімок `builder.ConnectionString` у тесті) |
| AC-R | тест міграції (§3) + лічильник `Unprotect` = 0 для Windows-джерел |
Тестові дані: ЖОДНИХ реальних логінів/паролів у репо; випадкові `test-<guid>` значення, згенеровані в тесті; для live — облікові дані лише від людини в UI (не в чаті/файлах). Приклад із скрину (`WIN-ABAANR1OQJE\Administrator`) у тестах не використовувати. Правило локального режиму: build + Architecture + вузькі тести; повні — на CI.

## 8. Ризики

1. Пароль у відкритому вигляді у винятку/логу драйвера (§5) — найвища ймовірність; потрібен сторож-тест по логах.
2. Перенаправлення збереженого пароля на чужий хост (S1-аналог) — закрито reentry-правилом (§4); не забути для `SecondaryEndpoint` і `Transport`.
3. Stale/нерозшифровуваний блоб після RESTORE БД або втрати сертифіката: `Unprotect` кидає `CryptographicException` → мапити в категорію `auth`/«потрібно ввести пароль заново», не 500 і не лог блоба; UI показує «пароль потрібно ввести заново».
4. Конфлікт двох шляхів (env-секрет `DataSource.<code>` vs БД) — неоднозначність пріоритету (Q-1).
5. ODBC-драйвер PI SQL Client: точні ключі автентифікації не перевірені (Q-6) — AC-0 це ловить лише на live.
6. Міграція R3 + «одна в польоті»; відкат втрачає паролі.
7. Bakup БД містить ключі DataProtection і блоби разом (при `AllowUnprotectedKeys`) — шифрування є «обфускація проти випадкового читання», не захист від викрадення БД; сказати прямо в листі готовності.
8. Розмір/бюджет: нові поля роблять `DataSourceFormModal` і `SaveDataSourceHandler` більшими — межа diff ≤800 з ≥40% тестів; ймовірно ділити: (1) домен+міграція+протектор+аудит, (2) адаптери, (3) API+контракт, (4) UI — але ОДНА lane за правилом 10 (контракт перед клієнтом, правило 6 CLAUDE «клієнт від запушеного контракту»).

## 9. Умови зупинки (з картки + мої)

- будь-яка відповідь API / журнал / аудит / виняток містить пароль або його довжину → СТОП;
- потреба змінити DataProtection-ключі/сертифікат live → СТОП (питання людині);
- live без постійного кільця (пересоздається БД) → СТОП до рішення;
- адаптери потребують залежності від ASP.NET DataProtection (порушення Architecture-тестів) → повернутися до порта-резолвера;
- ODBC-драйвер не підтримує UID/PWD так, як припускає картка → СТОП, питання людині.

## 10. Мапа розділів на AC

AC-0: §7 (live), §6. AC-1: §4 (DTO), §7 (UI). AC-2: §2, §3, §4, §5 (журнали). AC-3: §5. AC-4: §1, §4. AC-5: §1, §3, §7. AC-R: §3, §7.

## 11. Відкриті питання (⟶ rev2: застаріло, актуальний перелік — §16)

- Q-1: пріоритет БД-пароль vs env-секрет `DataSource.<code>` (і чи прибирати env-шлях, чи лишити для сумісності)?
- Q-2: розширювати `CredentialMarkers` на `uid=`/`user id=`? (ризик для наявних джерел з UID в адресі; перевірити, чи є такі у live: `SELECT Code, Endpoint FROM ext.DataSource WHERE Endpoint LIKE '%uid=%' OR … '%user id=%'` — читання лише людиною/DBA.)
- Q-3: окремі дії «замінити/стерти пароль» (ендпоінти) чи поля в PUT (як SMTP)? Моя рекомендація — як SMTP (поля `Password`/`ClearPassword` у PUT, один аудит), менший контракт.
- Q-4: максимальна довжина пароля/логіна; дозволені символи (`;`, `{`, `}`, `=`).
- Q-5: чи дозволяють Architecture-тести залежність `Ecr.Adapters.*` від DataProtection-порта; де додавати резолвер.
- Q-6: точні ключі ODBC-драйвера PI SQL Client для логіна/пароля й чи треба прибирати `Trusted_Connection`/`Integrated Security` при `UserPassword`.
- Q-7: наявні тести-прецеденти SMTP (не читав).
- Q-8: чи тексти `OdbcException`/`SqlException` можуть цитувати рядок з'єднання (перевірити емпірично тестом з фейковим драйвером).
- Q-9: чи OTel/SqlClient-інструментація пише рядок з'єднання у трейси.
- Q-10: чи є експорт/«знімок конфігурації», що серіалізує `DataSource` цілком.
- Q-11: підтвердити, що Worker/Child не виконують збір (адаптери реєструє лише Api).
- Q-12: чи `userName` показувати з `Integration.View`.
- Q-13: чи live має DataProtection-сертифікат / `AllowUnprotectedKeys` — потрібна відповідь людини/DBA (§6).
- Q-14: чи форма має мати «Test connection» ДО збереження (зараз тест лише для збереженого `id`); AC-0 формулює «New connection → Test connection», що потребує або збереження чернетки, або нового ендпоінта тесту з тіла (більший ризик: пароль у тілі тесту, SSRF).


## 12. Review resolution (rev 2): R-01..R-24 → що змінилось

Рішення координатора враховано: R-01 (тест з тіла, без збереження), R-02 (safe-log + canary), R-03 (дві нові категорії, зупинка збору), R-05 (UserPassword ігнорує env). Решта рекомендацій огляду прийнято.

| ID | Що змінилось у дизайні (розділ) |
|---|---|
| R-01 | Q-14 закрито. Новий ендпоінт `POST /api/v1/data-sources/test-connection` з тіла форми БЕЗ збереження (§13.1): валідація = та сама, що при збереженні (`RequireNoCredentials`/розбір ключів R-04, політика транспорту й адреси, `RequireReadByTransport`); пароль — лише з тіла; збережений пароль підставляється тільки за `sourceId` того ж джерела при НЕЗМІНЕНІЙ цілі (`SameTarget` за ODBC/Sql-парсером, R-10) — інакше 422 `dataSourcePasswordReentryRequired`; право `Integration.Manage` (як збереження); ліміт на користувача 5/хв → 429 (прецедент `SmtpTestRateLimitPolicy`, `SmtpSettingsController.cs:38`); ворота не за id, а за користувачем+ціллю; аудит без пароля й адреси. Лише PiSqlClient і Sql (мій вибір: PiWebApi з тіла не тестується, лишається за id). AC-11a..e |
| R-02 | Обгортка `SafeLogException` (§13.2): тип винятку, SQLSTATE/номер, повідомлення після скрабера (точні підрядки пароля й логіна, відомі в момент з'єднання, + шаблони `PWD=`/`Password=`/`UID=`/`User ID=`), без сирого об'єкта винятку й без `Errors[]`/внутрішніх; застосовується до `LogConnectFailed` (PiSqlClient `:1278`, Sql `:623`), `LogConnectionStringUnreadable` (`:1302`, Sql `:634`), `LogProbeFailed` (`DataSourceHandlers.cs:1140,1151-1154`). AC-0 крок canary (невірний пароль-маркер → grep логів, `itg.CollectionRun.ErrorMessage`, ProblemDetails = 0). AC-2b |
| R-03 | Категорії `credentialsUnreadable` (збій `Unprotect`, NULL-блоб при UserPassword, §13.3) і `credentialsRejected` (після однієї відмови автентифікації 28000/18456 збір і Test за id зупиняються для джерела, доки пароль не замінено). Додається 4-та колонка `CredentialsRejectedAt datetime2 NULL` (скидається `ReplaceCredentials`). Ключі каталогу 4 мов, `FailureCategories` розширено, `Classify` знає обидва. UI: «введіть пароль заново». AC-6a/6b/6c. Інструкція вікна RESTORE: паролі вводити заново |
| R-04 | `RequireNoCredentials` лишається підрядковою (друга лінія, нормалізація всіх `\s`, не лише пробілів), але ПЕРВА лінія — розбір рядка (ODBC-правила для PiSqlClient, `SqlConnectionStringBuilder` для Sql) і відмова за ІМЕНЕМ ключа: `uid, user, user id, userid, username, pwd, password` + наявні secret/token/apikey/sig (§13.4). `guid=`/`superuser=` не відхиляються. Рядок, що не розбирається — відхиляється без повтору вмісту. AC-4. Вимір наявних джерел з UID — питання DBA §16 |
| R-05 | Правило до коду: `UserPassword` ⇒ env-секрет `DataSource.<code>` ІГНОРУЄТЬСЯ повністю (жодної подвійної вставки, S3-перевірка env і `HasSecret` діють лише для Windows). Windows = як сьогодні. Q-1 закрито. AC-5b |
| R-06 | Побудова рядка в режимі UserPassword: видалити ВСІ аліаси логіна/пароля/`Trusted_Connection`/`Integrated Security`/`Persist Security Info`, потім виставити `UID/PWD` (Sql: `UserID/Password`, `IntegratedSecurity=false`, `PersistSecurityInfo=false`); на збереженні endpoint із такими ключами при UserPassword → 422 (§13.4). Тест на кожен аліас. AC-3 |
| R-07 | Рефакторинг у дизайні: чиста `internal static string BuildConnectionString(DataSource, string endpoint, ResolvedCredentials?, string? envSecret)` у кожному адаптері (PiSqlClient: поряд з `:1204`; Sql: поряд з `:565`); побудова ПІСЛЯ політики адреси на рядку без секрету. Вузькі тести на неї; мутація «прибрати присвоєння UID» → червоний. AC-3, AC-5 (Windows = байтова рівність знімку) |
| R-08 | Семантика PUT: `AuthMode`/`UserName`/`Password` = null/відсутні ⇒ «не змінювати» (не «Windows»). Windows→UserPassword — `UserName`+`Password` у ТОМУ Ж запиті; UserPassword→Windows стирає логін і блоб; `ClearPassword` при UserPassword без зміни режиму → 422. Інваріант домену: `UserName` без `AuthMode=UserPassword` не діє. AC-10 (PUT старого клієнта не змінює облікові дані й режим) |
| R-09 | AC-7: PUT зі зміною `Endpoint`, `SecondaryEndpoint`, `Transport`, `UserName` (по одному) без пароля → 422 `dataSourcePasswordReentryRequired`, блоб і адреса не змінені; позитив: та сама ціль (регістр ключів/порядок) без reentry; порядок: reentry раніше S3; мутація «прибрати перевірку» → червоний. Комбінація UserPassword→Windows+нова адреса: блоб стирається в тій самій транзакції, лишається `confirmEndpointChange` |
| R-10 | `DataSourceAddress.SameTarget`/`Canonical` (`DataSourceHandlers.cs:860`) переходить на ODBC-правила для PiSqlClient і `SqlConnectionStringBuilder` для Sql; `UserName` порівнюється окремо, Ordinal. Диференціальний тест: пари, що однаково читає дефолтний парсер і по-різному ODBC (фігурні дужки з `;`) → «змінено». AC-7 |
| R-11 | Інваріант: `AuthMode=UserPassword` лише для PiSqlClient і Sql; зміна транспорту на PiWebApi при UserPassword → 422 `dataSourceCredentialsTransport`; тест. AC-9 |
| R-12 | Пароль у запитах — тип `SecretString` (`ToString()=>"***"`, `PrintMembers` приховано); те саме для вхідного record `test-connection`; сторож: `request.ToString()` без маркера. Параметр handler-а не логується. (SMTP-record має ту саму діру — окремий борг, не в цій lane.) AC-2 |
| R-13 | Ліміти: `UserName` 1..256 (`DOMAIN\user`), без керівних символів (<0x20, 0x7F); `Password` 1..256, БЕЗ `Trim`, без NUL; `Trim()` (`DataSourceHandlers.cs:793`) до пароля не застосовується. Тести ін'єкції: `user;Server=evil`, `user}`, `a=b` — після builder рядок розбирається назад з тими самими UID/PWD і тим самим Server. AC-8. Q-4 закрито |
| R-14 | Подія безпеки `DataSourceCredentialChanged` пишеться ЗАВЖДИ при заміні/стиранні (як SMTP `:154-170`), незалежно від знімка структурного журналу, у тій самій транзакції де можливо; нові ключі `audit.eventLabel.DataSourceCredentialChanged` у 4 мовах з рядками сіду (прецедент `COLL-p3-rc19.sql:39-41,100-102,172-174`); рівно одна подія, без значення й довжини. AC-2a |
| R-15 | Сторож «sentinel»: пароль `ZZpwd-<guid>` і base64 блоба по всіх виходах — відповіді data-sources (GET/list/POST/PUT/test), ProblemDetails, `aud.*`, `itg.CollectionRun`, `itg.NotificationOutbox`, серверний лог (захоплений `ILogger`) після сценаріїв: save, change, невірний пароль у test, збір з невірним паролем, збій `Unprotect`. `DataSourceView` лише явними полями; рефлексійний тест на відсутність властивостей password/protected. AC-2 |
| R-16 | Лист готовності: відкат коду/ff-відкат змінює особу, під якою ходять UserPassword-джерела (старий код ігнорує `AuthMode` ⇒ ходить як Windows); перед відкатом вимкнути ці джерела. Додано в §3 нижче («Rollback») як вимога листа |
| R-17 | Міграція ідемпотентна двічі (`sqlcmd -I -b`), `Up` без `UPDATE` даних, `EcrDbContextModelSnapshot` у тому ж коміті; CI «upgrade main→SHA ×2» покриває. AC-R |
| R-18 | AC-R розширено мутацією «default AuthMode=UserPassword → тест міграції червоний» і інваріантом: рядок з `UserName`, але `AuthMode=Windows` ⇒ жодного UID у рядку. AC-R, AC-10 |
| R-19 | Перелік наявних ключів, що змінюються (форма-підказки джерела `COLL-source-form-hints.sql`, `dataSourceEndpointCarriesSecret`, пояснення Q15-06 «службовий акаунт») + рядки «Змінені тексти наявних ключів» у `09-seed.sql`; сторож `SeedTextUpdateTests`. Нові ключі додаються окремо. AC-14 |
| R-20 | Після `ReplaceCredentials`/Clear — очищення пулів (`SqlConnection.ClearAllPools()` / `OdbcConnection.ReleaseObjectPool()`) через шов `IConnectionPoolReset`; тест на шов (виклик при зміні). Свідомо поза: пули чужих процесів. AC-12 |
| R-21 | Підтверджено: `Integration.View` бачить `authMode/userName/hasPassword`; `Integration.Manage` — заміна/стирання/тест; окремого права немає. Тест прав: View без Manage → 403 на PUT з паролем і на test-connection; тіло 403/422 не повторює пароль. AC-15. Q-12 закрито |
| R-22 | Q-13 закрито рішенням людини (внизу документа, збережено): ключі в БД, без сертифіката. Додано в §6: `setup-dev-db` DROP/CREATE та відтворення `EcrLandUi` стирає ключі разом з паролями (паролі вводити заново), вимога в інструкції вікна RESTORE |
| R-23 | Q-5 закрито: порт `IDataSourceCredentialResolver` (Application.Ports; `Resolve(DataSource)`→`ResolvedCredentials?`, кидає типізований `CredentialsUnreadableException`; `ReportRejectedAsync(sourceId)`; `ReplaceAsync`) → реалізація в Infrastructure на DataProtection (purpose `Ecr.DataSource.Credential.v1`); адаптери (як сьогодні з `ISecretProvider`, `PiSqlClientDataSource.cs:25`, `SqlDataSource.cs:58`) DataProtection не знають |
| R-24 | Політика адреси викликається ДО підстановки/розшифровки; відмова політики не викликає `Unprotect` (тест: лічильник `Unprotect`=0 при відхиленій адресі). AC-13 |

## 13. Рішення rev 2 (деталі)

### 13.1 Test connection з тіла форми (R-01)
- `POST /api/v1/data-sources/test-connection`, тіло: `{ sourceId?, transport, endpoint, secondaryEndpoint?, catalog?, authMode, userName?, password? (SecretString), reason }`. Нічого не зберігає.
- Адаптерам потрібен шов без читання БД: сьогодні `DiscoverAsync(int dataSourceId)` читає джерело з БД (`DataSourceHandlers.cs:1111`). Додається `IExternalConnectionProbe.ProbeAsync(DataSourceProbeSpec spec, ct)`, де `spec` = ефемерна (не прикріплена до контексту) сутність `DataSource` + `ResolvedCredentials` з тіла; `OpenAsync` розділяється на `OpenAsync(source)` (читає облікові дані через резолвер) і `OpenWithAsync(source, credentials)` — друга спільна для збору й тесту (одна точка підстановки лишається).
- Захисти (кожен має AC): (a) та сама валідація, що при збереженні (виклик спільної `ValidateAsync`-частини без запису); (b) пароль з тіла; збережений — лише за `sourceId` при незмінній цілі (SameTarget, R-10), інакше 422; (c) `Integration.Manage`; (d) ліміт 5/хв/користувач → 429 (політика rate-limit за прецедентом `Ecr.Api.Security.SmtpTestRateLimitPolicy`); (e) ворота `SourceProbeGate` за ключем `user+hash(target)`, не за id; (f) аудит `DataSourceConnectionTested` з `unsaved:true`, без адреси і без пароля, причина обов'язкова; (g) відповідь — лише категорія (як сьогодні); (h) відмова автентифікації з тіла НЕ ставить `CredentialsRejectedAt` (нічого не збережено), але лічиться в ліміті.

### 13.2 Safe-log (R-02)
`SafeLogException(Exception e, params string[] secrets)` → `{ Type, SqlState|Number, Message (скраб), Hresult? }`. Скраб: заміна точних значень `secrets` (пароль, логін, відомі в момент з'єднання) і шаблонів `(pwd|password|uid|user id)\s*=\s*[^;]*`; внутрішні винятки/`Errors[]` не серіалізуються. Логується DTO, а не `Exception`. Покриває три точки: `LogConnectFailed` ×2 адаптери, `LogConnectionStringUnreadable` ×2, `LogProbeFailed`. Мутація: повернути `error` у лог → sentinel-тест червоний.

### 13.3 Стани облікових даних (R-03)
- `credentialsUnreadable`: `Unprotect` кинув `CryptographicException`, або `AuthMode=UserPassword` і блоб NULL → `CredentialsUnreadableException` (BusinessRuleException, `messageKey err.ECR-INT-0502.credentialsUnreadable`), у лог лише тип; UI «введіть пароль заново». Не 500, не `other`.
- `credentialsRejected`: після ПЕРШОЇ відмови автентифікації (ODBC SQLSTATE 28000 / SqlException 18456, `IsAuthenticationFailure` `PiSqlClientDataSource.cs:1307`, `SqlDataSource.cs:585`) резолвер ставить `CredentialsRejectedAt`; далі збір за розкладом і Test за id для цього джерела НЕ відкривають з'єднання (повертають `credentialsRejected` без звернення до драйвера) до `ReplaceCredentials` (скидає мітку). Захист від блокування облікового запису AF.
- Залишковий ризик (названо): до `MaxParallel` одночасних спроб уже в польоті в момент першої відмови; поріг блокування облікового запису замовника невідомий (§16).
- Нові значення в `TestDataSourceConnectionHandler.FailureCategories` (`:1019`) і `Classify`; ключі каталогу 4 мов.

### 13.4 Розбір ключів і побудова (R-04, R-06, R-10)
Спільний `SourceConnectionString` (Application): `Parse(transport, text)` → ODBC-правила (`DbConnectionStringBuilder(useOdbcRules:true)`, як `DataSourceEndpointPolicy.cs:347`) для PiSqlClient, `SqlConnectionStringBuilder` для Sql. Використовується в `RequireNoCredentials` (відмова за іменем ключа), `SameTarget`, перевірці «у UserPassword endpoint без ключів логіна/Trusted_Connection/Integrated Security/Persist Security Info», і не залежить від адаптерів.

## 14. Фінальні AC (кожен з одним конкретним тестом/кроком, що падає без виправлення)

| AC | Що спостерігаємо | Доказ, що червоніє без фіксу |
|---|---|---|
| AC-0 | live: New connection (PiSqlClient), UserPassword, AF WIN-ABAANR1OQJE / ECR_01_Air → Test connection з тіла «Connected»; Windows для FLERT «Connected»; **canary**: невірний пароль-маркер → grep логів, `itg.CollectionRun.ErrorMessage`, ProblemDetails = 0 влучень; **крок 0**: ключі ODBC логіна визначено (див. §16 Q-6) | live-кроки; без safe-log canary знаходить маркер |
| AC-1 | форма: перемикач Windows/User name and password; write-only Password; «•••• set», Change, Clear; PiSqlClient і Sql; PiWebApi без перемикача; старий клієнт | vitest + e2e (CI `-grep`); тест «значення пароля не в DOM» червоніє при `value={password}` |
| AC-2 | sentinel-тест по всіх виходах (R-15) + `request.ToString()` без маркера (R-12) + DTO без password-властивостей | мутація «кладемо пароль у Snapshot/лог/ToString» → червоний |
| AC-2a | заміна/стирання → рівно одна подія `DataSourceCredentialChanged` без значення/довжини, навіть при незмінному знімку; підпис у 4 мовах | тест: без запису події червоний |
| AC-2b | safe-log: логуються лише тип/SQLSTATE/скрабоване повідомлення; фейк-драйвер кидає виняток з рядком з паролем → у `ILogger` маркера немає | мутація «логувати error» → червоний |
| AC-3 | `BuildConnectionString` по транспортах: UID/PWD, UserID/Password лише для UserPassword; запасна адреса ті ж облікові; аліаси прибрано; PersistSecurityInfo=false | чисті тести; мутація «прибрати UID» → червоний |
| AC-4 | `RequireNoCredentials`: відмова за іменем ключа (`uid`, `user id`, `userid`, `pwd`, `password`, табуляція/перенос); `guid=` і `superuser=` не відхиляються; відповідь не повторює вхід | тест; без розбору ключів `userid=` проходить |
| AC-5 | Windows: `BuildConnectionString` байтово дорівнює знімку до змін; Windows + env-секрет — як до змін | знімок-тест |
| AC-5b | UserPassword + заданий env-секрет → пароль у рядку з БД, env не впливає; S3/`HasSecret` не вимагають env-секрету | тест; без правила env перекриває |
| AC-R | наявні рядки після міграції = Windows, нуль `Unprotect`; рядок з `UserName`, але Windows → без UID; міграція ідемпотентна ×2, `Up` без UPDATE | тест міграції; мутація «default=UserPassword» → червоний |
| AC-6a | `Unprotect` кидає / блоб NULL при UserPassword → `credentialsUnreadable`, не 500, текст/блоб не в логах | тест з фейковим резолвером |
| AC-6b | після 1 відмови автентифікації друга спроба збору/Test за id не відкриває з'єднання (лічильник фейк-драйвера = 1) | без стану `credentialsRejected` лічильник =2+ |
| AC-6c | `ReplaceCredentials` скидає `CredentialsRejectedAt`; наступний Test працює | тест |
| AC-7 | зміна `Endpoint`/`SecondaryEndpoint`/`Transport`/`UserName` без пароля → 422 `dataSourcePasswordReentryRequired`, блоб і адреса не змінені; та сама ціль → без reentry; диференціал ODBC-парсера | мутація «прибрати перевірку» → червоний |
| AC-8 | ін'єкція в `UserName`/`Password` (`user;Server=evil`, `user}`, `a=b`, NUL, керівні): відмова або builder-розбір назад дає ті самі UID/PWD і той самий Server; пароль без Trim | параметризований тест |
| AC-9 | UserPassword + PiWebApi (створення, зміна транспорту) → 422 | тест |
| AC-10 | PUT старого вигляду (без нових полів) на UserPassword-джерело не змінює облікові дані й режим; `ClearPassword` без зміни режиму → 422 | тест |
| AC-11a | test-connection з тіла: валідація як при збереженні (carriesSecret, політика, RequireReadByTransport) | тест; без спільної валідації проходить |
| AC-11b | пароль лише з тіла; збережений — лише за `sourceId` + SameTarget, інакше 422 | тест |
| AC-11c | `Integration.Manage` обов'язкове (View → 403) | тест прав |
| AC-11d | 6-й запит за хвилину від користувача → 429 | тест rate limit |
| AC-11e | ворота не за id; аудит `unsaved:true` без пароля/адреси; відповідь — лише категорія | тест |
| AC-12 | після Replace/Clear викликано скидання пулів | тест шва `IConnectionPoolReset` |
| AC-13 | відхилення політики адреси → `Unprotect` не викликано; політика перед підстановкою | лічильник |
| AC-14 | наявні підказки/`carriesSecret` оновлено «Змінені тексти», нові ключі 4 мов | `SeedTextUpdateTests` + каталог-сторожі |
| AC-15 | View: бачить `authMode/userName/hasPassword`; без Manage → 403 на PUT і test, тіло без пароля | тест прав |

Жорстка вимога для всіх мутаційних доказів: мутація має збиратися (правило 410/411).

## 15. Пропозиція нарізки (одна lane `lane/integ-source-credentials`, лише лінійні комміти; розміри — оцінка без згенерованих файлів)

1. **Foundation (токен міграцій)** — сутність + конфіг + міграція (4 колонки: `AuthMode`, `UserName`, `PasswordProtected`, `CredentialsRejectedAt`), enum, DTO-поля `DataSourceView`, `SaveDataSourceRequest` (`SecretString`), DTO `test-connection`, регенерація `openapi.snapshot.json` + `schema.d.ts` (останній крок), сід ключів 4 мов і рядки «Змінені тексти». Контракт запушено ДО клієнта (правило 6). Оцінка ~350 рядків + ~250 згенерованих; тести ≥40 % (AC-R, DTO-рефлексія, міграція ×2).
2. **Server A** — `IDataSourceCredentialResolver` + `DataProtection`-реалізація, `SourceConnectionString` розбір (R-04/R-10), `RequireNoCredentials`/`SameTarget`, `Save/Update` семантика (R-08/R-09/R-11/R-13), подія аудиту (R-14). ~600 рядків, тести ≥40 % (AC-2, 2a, 4, 5b, 7, 8, 9, 10, 15).
3. **Server B** — `BuildConnectionString` + шов у обох адаптерах, `SafeLogException`, `credentialsUnreadable/Rejected` і стан (R-03), `test-connection` (ендпоінт, шов `ProbeAsync`, ліміт, ворота), скидання пулів. ~700 рядків, тести ≥40 % (AC-2b, 3, 5, 6a-c, 11a-e, 12, 13).
4. **Client** — форма (перемикач, write-only, Change/Clear, повторне введення, Test на New connection), a11y dark/light, e2e зачеплених екранів на CI. ~450 рядків, тести ≥40 % (AC-1).
Кожна частина ≤800 рядків за межею CLAUDE.md; між частинами — rebase і пуш у lane, CI на `integ-*`. Мутації безпеки (AC-2, 2b, 3, 7) — локально на свій клас; повні набори — CI.

## 16. Залишкові відкриті питання (лише факти людини/замовника)

- **Q-6 (визначає агент, не людина)**: точні ключі ODBC для логіна PI SQL Client. Крок 0 до коду: прочитати документацію драйвера (реєстр `HKLM\SOFTWARE\ODBC\ODBCINST.INI`, ключові слова DSN у `odbcad32`/тест-DSN) і перевірити на AC-0 (Test connection на тестовому AF із записаним `UID/PWD`). Якщо драйвер не приймає `UID/PWD` — СТОП (умова зупинки) і питання людині.
- **Q-15 (замовник)**: політика блокування облікового запису AF (`WIN-ABAANR1OQJE\Administrator` і службові): поріг/вікно невдалих входів — визначає, чи достатньо «зупинка після 1 відмови» (R-03).
- **Q-16 (DBA, лише читання)**: чи є в live `ext.DataSource.Endpoint` значення з `uid=`/`user id=`/`pwd=` (`SELECT Code FROM ext.DataSource WHERE Endpoint LIKE '%uid=%' OR Endpoint LIKE '%user id=%'`); якщо є — шлях міграції до UserPassword до релізу правила R-04.
Закрито в rev 2: Q-1, Q-2, Q-3 (поля в PUT, як SMTP, з R-08), Q-4, Q-5, Q-8 (safe-log + canary), Q-9, Q-10, Q-11, Q-12, Q-13, Q-14.

## Рішення людини 26.11 (Q-13)
Людина 26.11: «Захист ключів шифрування - достатньо, що паролі джерел шифруються ключами» → на live сертифікат для DataProtection НЕ потрібен; ключі в sec.DataProtectionKey (EcrLandUi), AllowUnprotectedKeys=true лишається. Умова: ключі не втрачаються при рестарті (у БД); при відновленні старого бекапу — повторне введення паролів (у інструкцію вікна).


## Q-16 закрито координатором (26.11 09:51)
live EcrLandUi, лише читання: ext.DataSource — 1 рядок, з uid=/user id=/pwd=/password= в Endpoint — 0. Шлях міграції наявних адрес не потрібен.


## Q-15 закрито людиною (26.11 09:52)
Людина: «десь 20 спроб» (поріг блокування облікового запису на AF WIN-ABAANR1OQJE). Стоп після 1 відмови входу (AC-6b) лишається — запас ×20; Test connection обмежено 5/хв на користувача → за 4 хв ручних спроб можна дійти до порогу: rate limit додатково рахує відмови входу на ціль (≤ 3 поспіль → 15 хв пауза для цієї цілі), AC-11 доповнити. Q-6: людина «ок» — агент визначає ключі ODBC на AC-0, при відмові — питання людині.
