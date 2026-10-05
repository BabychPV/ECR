# Runbook експлуатації ECR Web

Для того, хто обслуговує сервер застосунку й базу. Встановлення описано в
`docs/build/11-install-guide.md`, права й користувачі — в `docs/admin/admin-guide.md`.

Кожне твердження тут перевірено по коду. **⚠ потрібне рішення замовника**
позначає факти, яких код не знає.

## 1. Запуск і зупинка

Застосунок працює як служба Windows **`EcrApi`** (`UseWindowsService()`). Службу
реєструє `tools/deploy-ecr.ps1`.

```powershell
Get-Service EcrApi
Stop-Service EcrApi
Start-Service EcrApi
Invoke-WebRequest http://localhost:5000/health/live -UseBasicParsing   # 200 = процес живий
Invoke-WebRequest http://localhost:5000/health/ready -UseBasicParsing  # 200 = готовий; 503 = див. п. 3.1
```

Порт і схему (`http`/`https`) задає `ASPNETCORE_URLS` у середовищі служби (дефолт
`-AppPort 5000`). ✎ 2026-09-30: з HTTPS (`deploy-ecr.ps1 -HttpsThumbprint`) це
`https://+:<AppPort>` — перевіряти `https://<ім'я з сертифіката>/health/live`; по
`https://localhost` `Invoke-WebRequest` скаржиться на ім'я сертифіката (це не збій).
Транспорт і сертифікат — п. 11.

Послідовність старту (`StartupSequence.cs`):

1. Очікування БД (до 10 спроб по 3 с).
2. Проба редакції SQL Server (`Database:EditionMode`).
3. Перевірка схеми (`Schema:StartupMode`, `SchemaValidator`).
4. Сід `09-seed.sql`. Його виконує **сам застосунок** на кожному старті, а не скрипти
   розгортання.
5. Генерація вʼюх `rpt.v_*` (збій — лише Warning і картка `reportviews`, п. 12).
6. Запис `bootstrap` (`admin-guide.md` §2.2).
7. Закриття покинутих фонових задач цієї машини й ролі (п. 1.1).
8. Прогрів кешу метаданих.

Розклади реєструє окрема фонова служба вже після старту (`RecurringScheduleService`, п. 4).
Службу зупиняють збої кроків 1–4 і 6; збої кроків 5 і 8 старт не зупиняють. Причину шукайте
в лозі (п. 3.2) і в журналі подій Windows.

### 1.1. Одна служба кожної ролі на базу на одному хості

⚠ На **одному** хості не запускайте одночасно дві служби ECR **однієї ролі**
(дві Api, або IIS-пул із перекритим рециклом, overlapped recycle; чи два
воркери) на **ту саму** базу. Старт другого процесу закриває як покинуті всі
задачі `Running`/`Queued` попереднього процесу цієї машини й цієї ролі — навіть
якщо той ще живий: власника задачі визначає `itg.JobProgress.InstanceId` =
`{MachineName}/{роль}/{GUID процесу}` (роль — `api` або `wrk`; ім'я машини
до 27 символів — як є, довше — перші 18 символів, `~` і 8 hex SHA-256 повного
імені, тож хости ферми зі спільним початком імені не збігаються; разом рівно
64 — межа стовпця), а на старті
закриваються всі рядки з тим самим `MachineName`, тією самою роллю й чужим GUID.
Дві служби на **різні** бази — безпечно.

Api й воркер на **одному** хості — різні ролі й задачі одне одного **не**
закривають (P3, ФВ-9.8). Рядки старого формату `{MachineName}/{GUID}` (до P3)
вважаються рядками Api: їх закриває перший старт нової Api на цій машині.
Рядки черги задач у базі (`Lane` заданий) старт не чіпає взагалі — їх
повертає в чергу або закриває лише прострочена оренда.

Відоме обмеження: у мультиінстансному розгортанні зміна прив'язки вікна рядків (RowWindow) на одному вузлі API діє на інших не одразу — до 60 с (TTL знімка колонок). Після цього правки Початку/Кінця й імпорт ставлять підтягування вікон.

## 2. Конфігурація

Джерела в порядку пріоритету (кожне наступне перекриває попереднє):

1. `appsettings.json` поруч з `Ecr.Api.exe`. Перезаписується при оновленні.
2. `%ProgramData%\ECR\config\appsettings.Production.json`. Інсталятор створює
   його один раз і **не перезаписує** при оновленнях. Файл перечитується на льоту.
   Налаштування майданчика редагуйте тут.
3. Змінні оточення з префіксом `ECR_`, `:` → `__`. Вони лежать у
   `HKLM:\SYSTEM\CurrentControlSet\Services\EcrApi\Environment`.

⛔ **Секрети (рядок підключення, паролі, `Secrets:*`) задаються лише змінними
оточення служби**, ніколи у файлі.

### 2.1. Ключі

Колонка «Дефолт» — значення з `appsettings.json`. Запасний дефолт у коді
(коли ключа у файлі немає) з ним збігається — це стереже
`ConfigurationKeysTests`. Єдиний свідомий виняток —
`Notifications:WebhookAllowedHostSuffixes`: без ключа код не дозволяє жодного
хоста. Значення в дужках — дефолт коду для ключів, яких у файлі немає.

| Ключ | Дефолт | Значення |
|---|---|---|
| `ConnectionStrings:Ecr` | порожньо | рядок підключення до SQL Server. **Секрет**: `ECR_ConnectionStrings__Ecr` |
| `Schema:StartupMode` | `Validate` | `Validate` — не стартувати, якщо є незастосовані міграції EF. `Migrate` — застосувати незастосовані міграції EF на старті (лише dev/test: у проді обліковий запис служби не має DDL-прав, `D-66`; скрипти `Sql/*.sql` цей режим не виконує) |
| `Database:EditionMode` | `Auto` | режим редакції SQL Server (`Standard` / `Enterprise`). `Auto` — визначити самостійно на старті. `deploy-ecr.ps1` записує визначене при установці значення в `ECR_Database__EditionMode`, якщо його не задано явно (`docs/build/11-install-guide.md` §2.5) |
| `Database:CommandTimeoutSeconds` | 60 | таймаут команди SQL, с |
| `Database:BulkBatchSize` | 50000 | розмір пачки масового запису |
| `Cache:SchemaName` / `Cache:TableName` | `dbo` / `Cache` | таблиця розподіленого кешу |
| `Cache:MetadataSlidingMinutes` | 240 | кеш метаданих, хв |
| `Cache:AccessProfileSlidingMinutes` | 60 | кеш профілю доступу, хв |
| `Auth:CookieName` | `ecr.auth` | ім'я cookie сесії. Зміна розлогінює всіх відкритих користувачів |
| `Auth:SlidingHours` | 8 | ковзний строк сесії, год |
| `Auth:RequireHttps` | `true` | cookie лише через HTTPS (`Secure`). `false` пише лише `deploy-ecr.ps1 -AllowHttp` (стенд): у Production `transport` на `/health/ready` — Degraded (п. 11) |
| `Transport:Https:CertificateThumbprint` | порожньо | відбиток сертифіката HTTPS у `LocalMachine\My`; Kestrel віддає його для кожної `https://`-адреси з `ASPNETCORE_URLS`. Пише `deploy-ecr.ps1 -HttpsThumbprint`. Заданий, а сертифіката немає чи він без закритого ключа — служба не стартує (п. 11) |
| `Transport:Https:Port` | `0` | порт HTTPS для перенаправлення `http` → `https` (308); `0` — без перенаправлення. Пише `deploy-ecr.ps1 -HttpRedirectPort` |
| `Auth:EnableNegotiate` | `true` | вхід Windows (Negotiate) |
| `Auth:StampCacheSeconds` | 5 | як швидко блокування чи зміна ролей діє на відкриті сесії, с |
| `Auth:DataProtection:CertificateThumbprint` | немає | відбиток сертифіката з `LocalMachine\My` для захисту ключів Data Protection (п. 6.2). ⛔ **З 2026-09-29 (S11) у Production обов'язковий**: без нього служба не стартує. Один і той самий сертифікат (із закритим ключем, з правом читання для облікового запису служби) — на всіх вузлах |
| `Auth:DataProtection:PreviousCertificateThumbprints` | немає | відбитки попередніх сертифікатів Data Protection (масив або список через `;`/`,`), сертифікати мають лишатися в `LocalMachine\My`. Пише `deploy-ecr.ps1 -PreviousDataProtectionCertificateThumbprints`; ненайдений — Warning у журналі. Заміна сертифіката — `https-certificate.md` §9.2–9.3 |
| `Auth:DataProtection:AllowUnprotectedKeys` | `false` | лише для одноразових стендів (`smoke.ps1`, `e2e-stand.ps1`, `setup-dev-db.ps1`): дозволяє старт у Production без сертифіката. На майданчику не вмикати: старт пише Critical у журнал подій (джерело `ECR`), `db` — Degraded з причиною. `deploy-ecr.ps1` його не ставить ніколи |
| `Security:RateLimit:LoginPermitPerMinute` | 60 | спроб входу за хвилину з однієї IP-адреси (`/api/v1/login/*`) |
| `Security:RateLimit:ChangePasswordPermitPerMinute` | 10 | змін пароля за хвилину на користувача |
| `Security:RateLimit:TrustForwardedFor` | `false` | брати IP із `X-Forwarded-For`. Вмикати лише за довіреним проксі |
| `Security:RateLimit:SearchPermit` / `SearchWindowSeconds` | 30 / 10 | обмеження пошуку |
| `Security:RateLimit:CspReportPermitPerMinute` | 120 | звітів про порушення CSP за хвилину з однієї адреси (`POST /api/v1/csp-report`); понад межу — `429` без тіла |
| `Security:RateLimit:RecalculatePermitPerMinute` | 6 | запитів `POST /api/v1/documents/{id}/recalculate` на користувача й документ за хвилину (спільно для власників права `Calculation.Recalculate` і читачів); понад межу — `429` (`ECR-REQ-0429`, `Retry-After`). Лічильник у пам'яті процесу: перезапуск скидає, на кількох вузлах межа діє на кожному окремо. Запити, відхилені `4xx` (`403`/`404`/`422`), межу не витрачають; `5xx` і скасований клієнтом запит — витрачають (робота могла вже статися; ent7 P3-1). Мінімум — 1 |
| `Security:RateLimit:RecalculateUserPermitPerMinute` | 30 | запитів `POST /api/v1/documents/{id}/recalculate` на КОРИСТУВАЧА (усі документи разом) за хвилину; діє поруч із межею на пару «користувач + документ» (ent7 P3-2). Мінімум — 1 |
| `Security:RateLimit:SmtpTestPermitPerMinute` / `SmtpTestSystemPermitPerHour` | 5 / 30 | проб поштового транспорту (`POST /api/v1/notifications/smtp/test` і `POST /api/v1/notifications/channels/{id}/test`): на користувача за хвилину і на всю систему за годину; понад межу — `429` (`ECR-REQ-0429`, `Retry-After`). Системна межа списується за проби, ПРИЙНЯТІ межею користувача й перевіркою права `System.ManageNotifications`; запити, відхилені цими двома перевірками (`429`, `403`), її не витрачають, а квота рахує лише проби, що пройшли валідацію й пошук каналу: запити, що завершилися будь-яким статусом `4xx` пізніше (`422`, `404` та ін., зокрема винятком), токен повертають, `5xx` — ні (рев'ю ent6 S6). Межа діє і для проби каналу Teams. Рахуються проби, а не листи: проба каналу шле до 20 адресатів РАЗОМ (явні адреси, потім ролі; D-263, ent6 S3); канал зберігає не більше 50 явних адрес. Квоти живуть у пам'яті кожного процесу: перезапуск скидає, а на кількох вузлах межа діє на кожному окремо. Мінімум для обох — 1 (`notifications-runbook.md` п. 2.5) |
| `Security:Csp:ReportOnly` | `true` | віддавати сувору політику заголовком `Content-Security-Policy-Report-Only` (лише звіти, сторінки не блокуються). Порушення — рядки журналу `CSP violation: …` і лічильник `ecr.csp.violations` (тег `directive`) |
| `Security:Csp:ReportUri` | `/api/v1/csp-report` | куди браузер шле звіти (`report-uri`, а на HTTPS ще й `report-to`). Порожньо — без звітування. Без `;`, пробілів і ком |
| `Security:Csp:Enforce` | `false` | ⛔ лише задел: `true` робить повну політику примусовою (звітний заголовок зникає). Не вмикати, доки e2e-набір не пройшов під нею, а `ecr.csp.violations` не порожній |
| `Jobs:NightlyRecalculation:Enabled` | `false` | нічний перерахунок о 03:30. Вмикається лише рядком `true` |
| `Jobs:ShutdownTimeoutSeconds` | 120 | скільки чекати завершення фонових задач при зупинці служби, с |
| `Jobs:Queue:Mode` | `Quartz` | `Database` — черга задач у БД (пише `deploy-ecr.ps1` разом з `EcrWorker`, п. 10). ⚠ Недійсне значення мовчки = `Quartz` |
| `Jobs:Recalculation:Executor` | `InProcess` | `Worker` — перерахунок у службі `EcrWorker` (лише з `Queue:Mode=Database`). ⚠ Недійсне значення мовчки = `InProcess` |
| `Database:SheetLockTimeoutSeconds` | 30 | очікування блокування аркуша, с |
| `Health:RawDataPointWarnRows` | 20000000 | поріг перегляду R2 для картки `db` (п. 3.1); `0` — вимкнено. ⚠ Нечислове значення робить `db` Unhealthy «Database is unavailable» (перевірки на старті немає) |
| `Calculations:FullYearWarnSeconds` | 600 | бюджет річного перерахунку (ПРД-13); перевищення — `recalcOverBudget` у картці `jobs` |
| `Calculations:MaxParallelism` | 4 | паралелізм розрахунку |
| `Audit:ExportMaxRows` | 100000 | межа експорту аудиту CSV |
| `Campaign:AtRiskDays` | 3 | за скільки днів до терміну проєкт вважається «під загрозою» |
| `Notifications:WebhookAllowedHostSuffixes` | `.webhook.office.com;.logic.azure.com;.powerplatform.com` | дозволені хости вебхуків (`;` або `,`) |
| `Smtp:Host`, `Smtp:From` | немає | **запасний** транспорт пошти: діє, лише поки налаштування SMTP у застосунку (`/admin/notifications`, `D-263`) вимкнені чи неповні. Без обох пошта не йде (`notifications-runbook.md`) |
| `Smtp:Port` | (587) | |
| `Smtp:UseStartTls` | (`true`) | |
| `Smtp:AllowedPorts` | `[]` (порожньо) | ✎ 2026-10-02 (ent6 S4): порти SMTP, **додаткові** до стандартних 25/465/587/2525 (список цілих 1–65535: `"Smtp": { "AllowedPorts": [ 1025 ] }` або `ECR_Smtp__AllowedPorts__0`). Діє на збереження налаштувань, пробу й кожне відправлення (і БД, і запасний `Smtp:*`). Нечисловий чи поза 1–65535 елемент зупиняє старт з іменем ключа; **скалярне значення без індексу (ECR_Smtp__AllowedPorts=2526) теж зупиняє старт** — потрібен ECR_Smtp__AllowedPorts__0. Потрібен лише якщо поштовий сервер замовника слухає нестандартний порт; після зміни — перезапуск `EcrApi`. ⚠ Loopback (`localhost`, `127.x`), link-local і metadata-адреси для SMTP заборонені завжди й цим ключем НЕ відкриваються (раніше loopback був дозволений: dev-приймач на `localhost:1025` більше не працює) |
| `Smtp:User`, `Smtp:SecretName` | немає | автентифікація. Пароль — секрет `Secrets:<SecretName>` |
| `Secrets:<ім'я>` | немає | секрети джерел і каналів. **Лише змінні оточення** |
| `PiSqlClient:CatalogQuery` / `TemplateQuery` / `ValueQuery` | немає (вбудовані) | перевизначення запитів адаптера PI SQL Client. `InterpolatedQuery`, `SummaryQuery`, `CurrentValueQuery`, `ElementListQuery`, `EventQuery`, `EventTemplateQuery` — без вбудованого тексту |
| `PiSqlClient:<код джерела>:<Query>` | немає | запит для ОДНОГО джерела (інша база AF на тому ж сервері), перекриває спільний `PiSqlClient:<Query>` — для будь-якого із запитів вище; напр. `PiSqlClient:AIR:ElementListQuery`. ⚠ Усі `PiSqlClient:*` адаптер читає через канал секретів, тобто фізично це `Secrets:PiSqlClient:…` — змінна `ECR_Secrets__PiSqlClient__AIR__ElementListQuery` |
| `Sql:CatalogQuery` / `Sql:ValueQuery` | немає (вбудовані) | те саме для SQL-джерела |
| `Integration:AfTimeZoneId` | (порожньо = UTC) | ✎ 2026-09-30, D-212 PR-7: пояс, у якому AF віддає дати дії записів довідника без поясу (Windows або IANA, напр. `Asia/Atyrau`). Діє лише для синку темпорального довідника з атрибутами дат у політиці. Невідомий пояс зупиняє старт з ім'ям ключа. ⚠ Пояс серверів AF замовника — відкрите питання PI-адміністратору |
| `PiWebApi:AllowedHosts` | `[]` (порожньо) | ✎ 2026-10-01: перелік хостів PI Web API, до яких дозволено підключати джерела. Елемент — точне ім'я хоста або `*.domain` (лише піддомени; сам `domain` не збігається); без урахування регістра. **Порожньо = без обмеження, окрім блок-листа** (loopback, link-local і хмарний metadata 169.254.x, unspecified, multicast — діє завжди, для всіх режимів автентифікації; перевіряється по кожній розв'язаній A/AAAA-адресі, у момент підключення — отже DNS rebinding не обійде). Приватні IP-літерали (`10.x`, `172.16–31.x`, `192.168.x`, `fc00::/7`) заборонені лише для джерел з Negotiate (потрібне ім'я хоста для Kerberos SPN). Адресу Negotiate-джерела можна змінити лише з явним `confirmEndpointChange: true` у запиті (в інтерфейсі — діалог підтвердження). Відповідь PI понад 50 МБ відхиляється. Масив задається так: у `appsettings.Production.json` — `"PiWebApi": { "AllowedHosts": [ "<хост PI Web API>", "*.<домен замовника>" ] }`, або змінними служби `ECR_PiWebApi__AllowedHosts__0`, `ECR_PiWebApi__AllowedHosts__1`, … ⚠ Інсталятор і `deploy-ecr.ps1` цього ключа не пишуть — задає адміністратор замовника вручну; імена хостів PI — дані замовника, у продукті їх немає (дефолт порожній; сторож `PiWebApiAllowedHostsDefaultTests`). Для вашого середовища кандидати allowlist: `ncatdevv08`, `ncatuatv12`, `ncatappv0154`; задає адміністратор замовника через `appsettings.Production.json` / `ECR_PiWebApi__AllowedHosts__0`. ⚠ Negotiate-джерело без списку: Warning у журнал і картка `sources` Degraded (`health.sources.negotiateNoAllowlist`, п. 3.1) — заповніть список. Після зміни — перезапуск `EcrApi` |
| `Bootstrap:Password` | немає | запасний пароль `bootstrap`. Основний шлях — файл `bootstrap.secret` |
| `Telemetry:Enabled` | `false` | експорт метрик по OTLP (п. 3.4). Вимкнено — не реєструється нічого з OpenTelemetry, навантаження нуль. Вмикається лише рядком `true` |
| `Telemetry:OtlpEndpoint` | порожньо | адреса OTLP-колектора, напр. `http://collector:4317` (gRPC) чи `http://collector:4318` (HTTP). **Обов'язкова**, коли `Telemetry:Enabled=true`: без неї або з недійсною адресою служба не стартує. Задана при вимкненому експорті — ігнорується, старт пише попередження |
| `Telemetry:OtlpProtocol` | `Grpc` | `Grpc` (порт колектора 4317) або `HttpProtobuf` (4318). Інше значення зупиняє старт |
| `Telemetry:ExportIntervalSeconds` | 60 | як часто відсилати метрики, с. Не менше 5 |
| `Telemetry:ServiceName` | `ecr-api` | `service.name` у ресурсі OTLP — під цим іменем служба видна в колекторі |
| `Logging:LogLevel:*` | `Information`, `Microsoft.AspNetCore` = `Warning`, `Microsoft.EntityFrameworkCore.Database.Command` = `Warning` | рівні логування |
| `Logging:File:Directory` | `%ProgramData%\ECR\logs` | тека логів. Порожньо — без файлового логу |
| `Logging:File:RetainedFiles` | 30 | скільки файлів зберігати |
| `Logging:File:FileSizeLimitMb` | 100 | розмір файлу, після якого починається новий |
| `Logging:File:Json` | `true` | поруч писати `ecr-yyyyMMdd.json` — рядок JSON на запис (п. 3.2) |
| `AllowedHosts` | `*` | |

Ключі з переліку `EcrConfigurationValidation.cs` (зокрема `Schema:StartupMode`,
`Database:*`, `Auth:SlidingHours`, `Security:*`, `Logging:File:*`,
`Calculations:MaxParallelism`) і `Telemetry:*` перевіряються на старті: недійсне значення
зупиняє службу з назвою ключа (п. 5). ⚠ **Не** перевіряються й мовчки замінюються дефолтом:
`Jobs:Queue:Mode`, `Jobs:Recalculation:Executor`, `Smtp:Port`, `Smtp:UseStartTls`,
`Calculations:FullYearWarnSeconds`; про `Health:RawDataPointWarnRows` — рядок таблиці вище.
Порожнє значення — «не задано», тобто дефолт.

⚠ **потрібне рішення замовника:** OTLP-колектор (і чи вмикати експорт метрик), адреси джерел PI. Дефолти
коду — «вимкнено» або порожньо. ✎ 2026-10-01 (рішення людини): SMTP і правила сповіщень налаштовуються в
самій системі (`D-263`); сертифікат — один із SAN для HTTPS і Data Protection (`D-267`, п. 11);
`PiWebApi:AllowedHosts` за замовчуванням порожній, хости інтранету не вносяться (`D-260`); DBA, резервне
копіювання й обслуговування БД — на замовнику (`D-264`).

### 2.2. SQL-джерело: місцевий час у колонці `Ts` без поясу

У налаштуваннях SQL-джерела немає поля часового поясу (`ext.DataSource`), і
текст запиту значень (`Sql:ValueQuery`) адаптер бере як є. Колонку `Ts` він
читає так: `datetimeoffset` **конвертується** в UTC; `datetime`, `datetime2`,
`smalldatetime`, `date` (значення без поясу) **вважаються** UTC без жодної
конвертації. Якщо джерело фактично пише місцевий час (наприклад, Атирау,
UTC+5), увесь ряд буде зсунутий на зсув поясу — дані самі по собі виглядають
правильними, але зміщеними в часі.

Лагодиться це в тексті запиту `Sql:ValueQuery`: переведіть колонку `Ts` через
`AT TIME ZONE` у `datetimeoffset` — тоді адаптер конвертує її в UTC коректно.
Ім'я поясу Windows перевіряйте по `sys.time_zone_info`; для Атирау (UTC+5) це
`West Asia Standard Time`:

```sql
SELECT
    [Ts] AT TIME ZONE 'West Asia Standard Time' AS [Ts],
    [Val], [Uom], [Quality]
FROM dbo.Readings
WHERE [EntityPath] = @path AND [Ts] >= @from AND [Ts] < @to
ORDER BY [Ts];
```

Пояс у прикладі — лише ілюстрація: у якому поясі пишуть мітки ваші джерела,
треба з'ясувати в їхніх власників; підставте свій пояс (список імен —
`SELECT name, current_utc_offset FROM sys.time_zone_info`).

⛔ `ORDER BY [Ts]` у тексті запиту обов'язковий незалежно від поясу: мітки
мають іти неспадно, інакше адаптер відмовляє помилкою `ECR-INT-0422`
(`timestampsOutOfOrder`) замість мовчазного сортування в пам'яті.

## 3. Моніторинг і логи

### 3.1. Health-ендпоінти

| Ендпоінт | Доступ | Що перевіряє |
|---|---|---|
| `/health/live` | анонімно | нічого: процес відповідає |
| `/health/ready` | анонімно | `db`, `jobs`, `sources`, `worker` (п. 10), `reportviews` (п. 12), `tzdata` (п. 13), `transport` (п. 11) — ✎ 2026-09-30, порядок як у `Program.cs`. Подробиці `db` приховано |
| `/health/db` | будь-який користувач після входу (право `System.ViewHealth` **не** потрібне) | редакція, RCSI, файлові групи, запас партицій `pf_ByPeriodKey`, обмеження режиму, кільце ключів Data Protection |

HTTP-код: `Healthy` і `Degraded` дають **200**, `Unhealthy` — **503**. Моніторинг
має читати поле `status` у JSON, а не лише код відповіді.

| Перевірка | Degraded | Unhealthy |
|---|---|---|
| `db` | попереду менше 2 меж `pf_ByPeriodKey` (`health.db.partitionsLow`; межі аудиту `pf_AuditByMonth` не перевіряються — п. 7.4); `ext.RawDataPoint` ≥ 80 % порога `Health:RawDataPointWarnRows` (типово 20 000 000 рядків, поріг перегляду R2; кількість з `sys.partitions`, кеш 10 хв; `health.collection.rawPointsApproaching`/`rawPointsOverThreshold`) ; у Production ключі Data Protection не захищені сертифікатом (`Auth:DataProtection:AllowUnprotectedKeys`); у кільці є ключі, зашифровані сертифікатом, якого служба не має (`unreadableKeyCertificates` — `https-certificate.md` §9.2) | RCSI вимкнено; немає файлової групи `DATA_HOT`, `DATA_ARCHIVE`, `AUDIT` або `INDEXES`; БД недоступна |
| `jobs` | планувальник зупинений (`schedulerStopped: true`); немає жодного тригера; є задачі без биття серця (`staleJobs`; `cleanupStalled: true` — прибирання їх не закриває); за 24 год є задачі, що вичерпали стелю відкладень (`deferralExhausted`), або перерахунки понад бюджет `Calculations:FullYearWarnSeconds` (`recalcOverBudget`); останнє зведення збоїв нікому не доставлено (`notificationsUndelivered`: збої були, відправлено 0 — перевірте SMTP, канали сповіщень і адресатів алертів; гасне наступним зведенням, що дійшло) | планувальник не зареєстрований або перевірка кинула помилку |
| `sources` | джерело ще не запускалось, є прогалина покриття, або є активне джерело з Windows-автентифікацією (Negotiate) при порожньому `PiWebApi:AllowedHosts` (`health.sources.negotiateNoAllowlist`, п. 2.1) | останній прогін будь-якої активної **сутності** збору — `Failed` (сутність, що ще не бігала, — прогалина, Degraded). Якщо активних сутностей немає — Healthy |
| `worker` | лише коли Api на `Jobs:Queue:Mode=Database` і `Jobs:Recalculation:Executor=Worker`: служби `EcrWorker` немає, вона `Disabled`, задачі перерахунку чекають понад 5 хв без жодної живої оренди, або чергу не вдалося прочитати (п. 10) | — |
| `reportviews` | вʼюхи `rpt.v_*` не створено для якоїсь опублікованої версії шаблону (п. 12) | ніколи |
| `tzdata` | база часових поясів ОС не знає, що Казахстан з 2024-03-01 на UTC+5, або перевірка сама не вдалась (п. 13) | ніколи |
| `transport` | Production із `Auth:RequireHttps = false` (`-AllowHttp`); сертифікат HTTPS спливає менш ніж за 30 днів або прострочений | ніколи (перевірка не виводить Api з ротації) |

### 3.2. Логи

- Тека: `%ProgramData%\ECR\logs`. Файли `ecr-yyyyMMdd.log`, новий щодня. Коли
  файл перевищує ліміт розміру, починається `…_001.log`. Зберігається 30 файлів.
- Якщо тека недоступна для запису, файловий лог вимикається, а служба все одно
  стартує. Перевіряйте права облікового запису служби.
- Формат рядка:
  `2026-09-22 10:00:00.000 +03:00 [ERR] [<CorrelationId>] [uid:<UserId>] [<машина>] <джерело>: <повідомлення>`
- Поруч — `ecr-yyyyMMdd.json` (вимикається `Logging:File:Json=false`): той самий
  потік записів, рядок JSON на запис, для SIEM. Поля: `@t` (UTC), `@l` (рівень),
  `@mt` (шаблон), `@m` (повідомлення), `@x` (виняток), далі властивості запису —
  `CorrelationId`, `UserId`, `MachineName`, `SourceContext`, `Code` (код помилки
  відмови, напр. `ECR-AUTH-…`). Ротація й строк зберігання — ті самі.
- Журнал подій Windows: канал `Application`, джерело **`ECR`** (його реєструє MSI),
  рівень `Warning` і вище. Сюди ж лягає недійсна конфігурація на старті.

### 3.3. Як знайти запит або задачу

Кожна відповідь API містить заголовок **`X-Correlation-Id`**. Вхідне значення
береться, якщо воно складається з `[A-Za-z0-9-_]` і не довше 64 символів. Інакше
генерується GUID без дефісів. Той самий ідентифікатор стоїть у кожному рядку логу
цього запиту.

```powershell
Select-String -Path "$env:ProgramData\ECR\logs\ecr-*.log" -Pattern '<correlationId>'
```

Фонова задача: стан і помилку можна отримати через `GET /api/v1/jobs/{jobId}`
або з таблиці `itg.JobProgress`. Далі шукайте
`jobId` у лозі.

### 3.4. Метрики

Служба пише власні метрики в лічильник `Meter "Ecr"` (`ecr.cells.read`,
`ecr.cells.write`, `ecr.formula.evaluate`, `ecr.job.duration`,
`ecr.job.start_latency`, `ecr.conflict.count`, `ecr.consistency.issues`,
`ecr.csp.violations` (порушення CSP за звітами браузерів, тег `directive`),
`ecr.budget.count` тощо — перелік у `EcrMetrics.cs`). Прочитати їх можна двома
способами.

**На сервері, без налаштувань** — `dotnet-counters`:

```powershell
dotnet-counters monitor --counters Ecr -n Ecr.Api
```

**Експорт по OTLP у колектор** (Prometheus, Grafana, Azure Monitor — через
OpenTelemetry Collector). За замовчуванням **вимкнено**: вимкнений експорт не
реєструє нічого з OpenTelemetry й не навантажує сервер. Увімкнути — у
`%ProgramData%\ECR\config\appsettings.Production.json`:

```json
{
  "Telemetry": {
    "Enabled": true,
    "OtlpEndpoint": "http://collector:4317",
    "OtlpProtocol": "Grpc",
    "ExportIntervalSeconds": 60
  }
}
```

або змінними оточення служби `ECR_Telemetry__Enabled=true`,
`ECR_Telemetry__OtlpEndpoint=http://collector:4317`, далі `Restart-Service EcrApi`.

- Протокол — `Telemetry:OtlpProtocol`: **`Grpc`** за замовчуванням (порт колектора
  4317) або `HttpProtobuf` (4318). Для `HttpProtobuf` адреса без шляху
  (`http://collector:4318`) доповнюється до `…/v1/metrics`; адреса зі шляхом
  береться як є. Експортуються лише метрики
  `Meter "Ecr"`; трас, логів і метрик ASP.NET/HTTP/рантайму через OTLP немає.
- `Enabled=true` без `OtlpEndpoint` або з адресою не `http://`/`https://` —
  служба **не стартує**, причина з назвою ключа — у журналі подій `ECR` і в
  `ecr-*.log` (п. 3.2, п. 5).
- `OtlpEndpoint` заданий, а `Enabled` ≠ `true` — служба стартує, у журналі
  попередження «експорт метрик OTLP вимкнено, значення ігнорується».

**Як перевірити, що дійшло:** через `ExportIntervalSeconds` після старту в
колекторі з'являються метрики `ecr.*` з ресурсом `service.name` =
`Telemetry:ServiceName` (типово `ecr-api`). Для колектора з
`debug`-експортером — рядки `ecr.` у його виводі. Колектор недоступний —
служба працює далі, експорт за цей інтервал може загубитися (на диск служба
метрики не накопичує).

**Дочірній воркер перерахунку** (`Ecr.Worker --child`, пул під наглядачем
`EcrWorker`) виконує задачі перерахунку, тож `ecr.job.failed` цих задач,
`ecr.job.start_latency` лейна `recalc`, `ecr.cache.hit/miss` і
`ecr.access.profile.build` у цьому режимі емітяться **ним**, а не Api.
Він експортує їх сам, тим самим Meter `Ecr` і з тими самими ключами `Telemetry:*`
(`Enabled`, `OtlpEndpoint`, `OtlpProtocol`, `ExportIntervalSeconds`), але читає їх
**не з `appsettings.json` Api**, а з `worker.settings.json` поруч з `Ecr.Worker.exe`
або зі змінних оточення служби наглядача `ECR_Telemetry__*` (дочірній їх
успадковує). Тож для режиму Worker експорт вмикають там теж; у колекторі ці метрики
мають `service.name` = `ecr-worker`. `deploy-ecr.ps1 -TelemetryOtlpEndpoint http://collector:4317`
(необов'язково `-TelemetryOtlpProtocol Grpc|HttpProtobuf`) пише ці змінні одразу в `Environment`
`EcrApi` і `EcrWorker`; без параметра телеметрію не чіпає. Типовий інтервал дочірнього — 15 с, а перед
завершенням процесу буфер скидається; процес, який убив Job Object за ліміт пам'яті,
останній буфер втрачає. Недійсна адреса дочірній не зупиняє: експорт тоді тихо
вимкнено (Api у такому разі не стартує).

**Глибина черги задач:** `ecr.jobs.queue_depth` (gauge, теги `lane` = `default`/`recalc`, `state` =
`Queued`/`Running`) — кількість задач черги в базі (режим `Jobs:Queue:Mode = Database`). Значення —
кеш, який Api оновлює раз на `Jobs:QueueDepth:RefreshSeconds` (типово 15, не менше 5) одним агрегатом;
експортує лише Api (`service.name` = `ecr-api`) — дочірній воркер його не рахує (число глобальне).

## 4. Розклади

Планувальник Quartz, розклади реєструє `RecurringScheduleService`. Hangfire у продукті
немає.

Час — локальний час ОС сервера застосунку (`EcrApi`), **не** пояс проєкту (`Asia/Atyrau`,
UTC+5) і не UTC (тригери Quartz без `InTimeZone`). Якщо на сервері виставлено UTC, «02:15»
настає о 07:15 за Атирау. Це стосується і вбудованих задач, і cron розкладів збору. Задачі SQL
Server Agent ідуть за локальним часом сервера SQL. Тики, що припали на час, коли служба
стояла, після старту не наздоганяються (сховище Quartz у пам'яті); виняток — `PeriodStateJob`,
він виконується один раз на старті.

| Коли | Задачі |
|---|---|
| щоночі 02:15 | `PartitionCheckJob`, `ConsistencyCheckJob`, `OrphanScanJob`, `ReportRetentionJob`, `ReportSnapshotFormatJob` |
| щогодини, хх:05 | `PeriodStateJob` (також один раз на старті), `NotificationJob`, `RowWindowRefetchJob` (повторне підтягування вікон рядків за пізніми даними PI) |
| щоночі 03:30 | нічний перерахунок, лише якщо `Jobs:NightlyRecalculation:Enabled=true` |
| за cron розкладу сутності | збір даних (`ext.CollectionSchedule`, по одному розкладу на сутність джерела; формат і залежності — `admin-guide.md` §5.1–5.2) |
| щохвилини / щогодини | прибирання покинутих задач і прогонів збору (`AbandonedWorkSweeper`) / видалення завершених записів `itg.JobProgress` (ретенція) — цикл `RecurringScheduleService`, не Quartz |

Нічний перерахунок ставиться окремо на кожен проєкт, **активний на момент старту** (усі
документи, увесь рік). Проєкт, активований пізніше, потрапить у нічний перерахунок лише після
перезапуску `EcrApi`.

`PartitionCheckJob` (`partition-check`) щоночі: 1) `arc.usp_EnsureAuditPartitions
@MonthsAhead = 12` — межі аудиту `pf_AuditByMonth` на 12 місяців уперед (`D-246`, п. 7.4);
2) `arc.usp_ArchiveAudit @OlderThanMonths = 24` (п. 7.3); 3) перевіряє запас `pf_ByPeriodKey`
(≥ 2 межі попереду). Межі `pf_ByPeriodKey` задача **не** додає — це робить Agent-задача
нижче або DBA. Числа 12 і 24 — константи в коді, ключа конфігурації немає.

SQL Server Agent (`14-agent-jobs.sql`) ставиться лише з `deploy-ecr.ps1
-FirstDeployment`. На Express скрипт задач не створює (друкує «SQL Server Express: SQL
Server Agent немає — завдання обслуговування НЕ створено», розгортання не падає) —
обслуговування тоді виконують вручну (п. 5, п. 7.4):

| Задача Agent | Коли | Що робить |
|---|---|---|
| `ECR: Partitions ahead` | 1-го числа, 02:40 | `arc.usp_EnsurePartitions @MonthsAhead = 6`: межі `pf_ByPeriodKey` на 6 міс. уперед; усередині — `arc.usp_EnsureAuditPartitions @MonthsAhead = 12` |
| `ECR: Physical checks` | щодня 03:10 | недовірені/вимкнені FK (50041), невирівняні індекси (50042) |

⚠ **потрібне рішення замовника:** вікна обслуговування. Код їх не знає. Розклади
вище зашиті в коді, тож резервне копіювання ставте поза ними.

## 5. Типові збої

| Симптом | Причина | Дія |
|---|---|---|
| `/health/ready` 503, `db` Unhealthy | БД недоступна або змінився рядок підключення | перевірити SQL Server і `ECR_ConnectionStrings__Ecr` у реєстрі служби, перезапустити `EcrApi` |
| служба не стартує, у лозі незастосовані міграції | оновили код без схеми, а `StartupMode=Validate` | застосувати схему (п. 8) і запустити службу |
| служба не стартує: «Production: ключі кільця DataProtection … не захищені» | не задано `Auth:DataProtection:CertificateThumbprint` (S11) | встановити сертифікат із закритим ключем у `LocalMachine\My` на кожному вузлі, дати права облікового запису служби, задати `ECR_Auth__DataProtection__CertificateThumbprint` (`deploy-ecr.ps1 -DataProtectionThumbprint`). Старі відкриті ключі в таблиці лишаються чинними до кінця строку — після ввімкнення захисту ротація, п. 6.4 |
| служба не стартує після зміни відбитка | немає сертифіката `Auth:DataProtection:CertificateThumbprint` у `LocalMachine\My` | встановити сертифікат із закритим ключем і дати права облікового запису служби |
| вхід «вдався», далі `401` на кожен запит; з сервера працює | HTTP при `Auth:RequireHttps = true`: `Secure`-cookie по HTTP не відсилається | п. 11: HTTPS (`deploy-ecr.ps1 -HttpsThumbprint`), проксі (`-BehindHttpsProxy`) або на стенді `-AllowHttp` |
| служба не стартує: «Transport:Https:CertificateThumbprint: …» | сертифіката HTTPS немає в `LocalMachine\My`, він без закритого ключа або відбиток не 40 hex | п. 11: поставити сертифікат із закритим ключем, повторити `deploy-ecr.ps1` |
| служба не стартує: «Certificate … cannot be used as an SSL server certificate» | у сертифіката HTTPS розширене використання ключа без Server Authentication | видати сертифікат із EKU Server Authentication |
| `transport` Degraded | `-AllowHttp` у Production, або сертифікат HTTPS спливає / прострочений | п. 11 |
| `db` Degraded: менше 2 партицій попереду | не працює Agent-задача (Express) | `EXEC arc.usp_EnsurePartitions @MonthsAhead = 6;` або скрипт `GET /api/v1/health/partitions/script` (заодно продовжує межі аудиту на 12 міс.). Виконує DBA: процедура без `EXECUTE AS`, потрібні права `ALTER` на функцію й схему партиціонування |
| `partition-check` `Failed` щоночі, у помилці — відмова в `EXECUTE` на `usp_EnsureAuditPartitions` | службовому акаунту не видано `EXECUTE` (`deploy-ecr.ps1` `GRANT` не робить) | DBA: `GRANT EXECUTE` (п. 7.4). Поки не видано, не виконуються ні архівація аудиту, ні перевірка запасу партицій |
| `db` Unhealthy: RCSI | базу відновили або створили без `06-rcsi.sql` | виконати `06-rcsi.sql`. Перезапуск не потрібен: перевірка читає RCSI щоразу, а не з проби старту |
| служба не стартує: «Недійсна конфігурація — служба не стартує» | значення ключа не того типу чи поза межами (`"60s"` замість `60`, друкарська помилка в `Database:EditionMode`) | виправити названий ключ у `appsettings.Production.json` або в `ECR_…` змінній служби. Той самий текст — у журналі подій (джерело `ECR`) і в лозі |
| `sources` Unhealthy | PI/SQL-джерело недоступне або змінився секрет | стан на `/admin/sources`, помилка в `GET /api/v1/jobs/{id}`, секрет `ECR_Secrets__<ім'я>` |
| `jobs` Degraded, `schedulerStopped: true` (✎ 2026-09-28, U7: було Unhealthy; `/health/ready` більше не 503 — тло не виводить інстанс із ротації) | планувальник зупинився: API працює, фонові задачі — ні | лог за `Quartz`, перезапуск служби |
| `jobs` Degraded, `staleJobs` / `cleanupStalled: true` | задачі без биття серця (процес зник); `cleanupStalled` — прибирання їх не закриває | `/admin/jobs`; якщо `cleanupStalled` тримається — лог за `RecurringScheduleService` (прохід прибирання раз на хвилину) |
| розгортання: `01-filegroups.sql`, `Msg 5149 … error 112` | немає місця на диску даних | звільнити місце. Файлові групи займають ~14 ГБ на повній редакції (п. 6.1) |
| збірка чи оновлення: `The file is locked by: "Ecr.Api (<pid>)"` | DLL тримає запущена служба | `Stop-Service EcrApi`, потім оновлення |
| оновлення: `Msg 50148 … Передперевірка D148` на `migration.sql` | у базі до 2026-09-20 є значення з модулем ≥ 1e12 | п. 8.1 |
| оновлення: `Msg 50301 … Передперевірка U1` на `migration.sql` | колонка шаблону чи поле довідника посилається на видалену одиницю | п. 8.2 |
| `404` на `GET /api/v1/jobs/…` | задачі немає (видалена ретенцією) або ідентифікатор старого формату з `#` | нові ідентифікатори (`Тип-guid`, `Тип~ціль~guid`, `Тип:відбиток`) кодування не потребують; старий `#` кодувати `%23` |
| розклад збору є, але збір за ним не йде, у журналі покриття «Очікує залежності» (`SkippedDependency`) | розклад-залежність не відпрацював успішно після останнього прогону цього розкладу | `/admin/sources` → журнал прогонів залежності: якщо `Failed` — усунути причину (часто `401/403`) або зняти залежність. Після 48 год без успіху залежності збір піде сам (`admin-guide.md` §5.2) |
| вкладка «Розклад»: «не поставлено» (`LastError`) | на старті cron виявився недійсним (рядок записано в обхід API), сутність — власна форма ECR, або планувальник відмовив | виправити cron чи вимкнути розклад; текст причини — у вкладці й у лозі (`RecurringScheduleService`) |
| пошта не йде | не налаштовано SMTP у застосунку (і немає запасних `Smtp:Host`/`Smtp:From`) | `/admin/notifications` → «SMTP (outgoing mail)», проба «Send test message» (`notifications-runbook.md` п. 2) |
| проба пошти: «DNS» / «з'єднання» / «TLS» / «логін» / «relay» / «тайм-аут» | проба називає категорію відмови (`notifications.test.smtp.*`): ім'я сервера, порт/брандмауер, сертифікат чи режим STARTTLS, логін і пароль, адреса відправника/адресати — у формі SMTP або, на запасному шляху, `Smtp:*` | виправити названий параметр і повторити пробу (`notifications-runbook.md` п. 2.4) |
| проба пошти: `429` | перевищено межу проб: 5 за хвилину на користувача, 30 за годину на всю систему (відомий вектор: адміністратор із правом може вичерпати системну квоту для всіх — до 30 валідних проб за ~6 хв, відхилені валідацією квоти не їдять; до 600 листів на годину; відновлюється протягом години; лічильник у пам'яті вузла) (`Security:RateLimit:SmtpTestPermitPerMinute`, `SmtpTestSystemPermitPerHour`) | почекати `Retry-After` і повторити; межу не знімати |
| проба каналу шле багато листів | явні адреси каналу **не обмежені** `RecipientLimit`: проба шле на ВСІ явні адреси каналу; адреси, розкриті з ролей, — не більше 20 (`ProbeRecipientLimit`) | тримати перелік явних адрес каналу коротким; проба — не розсилка, її частоту обмежує межа вище |
| проба пошти: відмова без категорії | нерозпізнану відмову транспорту проба називає загальним `notifications.testFailed`, тексту сервера немає | текст відмови клієнту не віддається (безпека); причину шукати в журналі поштового сервера |
| не приходить нагадування «період відкрито» | правило події `PeriodOpened` не вимкнене за замовчуванням, а **відсутнє**: матриця `/admin/notifications` → подія «Reporting period opened» (ru «Открыт отчётный период») × канал, межа серйозності `Info`; нагадує лише про перехід `Scheduled → Open`, раз на період | увімкнути клітинку; текст листа — ключі `notifications.periodOpened.subject/body` (`/admin/ui-strings` або панель «Message templates»); для «термін минув» — подія «Reporting period entered its grace window», ключі `notifications.periodGraceStarted.*` |

## 6. Резервне копіювання і відновлення

### 6.1. Що бекапити

| Що | Де | Чому |
|---|---|---|
| **база ECR** (усі файлові групи: `PRIMARY`, `DATA_HOT`, `DATA_ARCHIVE`, `AUDIT`, `INDEXES` і журнал) | SQL Server | усі дані, аудит, архів. Бекапити **повною базою**. Часткове відновлення файлових груп не перевірялось |
| **ключі Data Protection** | таблиця `sec.DataProtectionKey` **в тій самій БД** | потрапляють у бекап бази. Без них недійсні всі сесії й **не розшифровуються секрети каналів сповіщень і пароль SMTP**, заданий у `/admin/notifications` (`D-263`) |
| **сертифікат** `Auth:DataProtection:CertificateThumbprint` (з закритим ключем) | `LocalMachine\My` | якщо ключі захищені сертифікатом, без нього бекап бази не відкриє їх. Експортуйте PFX окремо, одразу після імпорту (`https-certificate.md` §7) |
| **попередні** сертифікати Data Protection (`Auth:DataProtection:PreviousCertificateThumbprints`) | `LocalMachine\My` | ключі кільця, зашифровані ними, без них не читаються (`db` Degraded, `unreadableKeyCertificates`). PFX кожного зберігати, доки його ключі в кільці **або** в будь-якому бекапі бази |
| задачі SQL Agent (`ECR: Partitions ahead`, `ECR: Physical checks`) | `msdb`, **не** в базі ECR | при відновленні на інший інстанс повторити `14-agent-jobs.sql` (або `deploy-ecr.ps1 -FirstDeployment`) |
| конфіг майданчика | `%ProgramData%\ECR\config\appsettings.Production.json` | налаштування майданчика |
| змінні оточення служби | `HKLM:\SYSTEM\CurrentControlSet\Services\EcrApi\Environment` | рядок підключення, секрети. Зберігайте в сховищі секретів, не поруч із бекапом |

Сховища експорту немає. Файли Excel створюються тимчасово в `%TEMP%`
(`ecr-export-*.xlsx`, `ecr-snapshot-*.xlsx`), тож бекапити їх не потрібно. Логи —
за бажанням.

Розміри файлів у `01-filegroups.sql`:

| Логічне ім'я | Група | Початковий / приріст |
|---|---|---|
| `Ecr_hot` | `DATA_HOT` | 4096 / 1024 МБ |
| `Ecr_archive` | `DATA_ARCHIVE` | 4096 / 4096 МБ |
| `Ecr_audit` | `AUDIT` | 4096 / 2048 МБ |
| `Ecr_idx` | `INDEXES` | 2048 / 1024 МБ |

На Express (і з розширеною властивістю `Ecr_SmallFiles`) усі розміри — 64 МБ. Окремої
архівної бази немає: архів — файлова група `DATA_ARCHIVE` тієї самої бази.

### 6.2. Політика

⚠ **потрібне рішення замовника: RPO/RTO і політика бекапу.** `docs/tz/08-nfr.md`
відсилає до політики замовника. Рішення 17 (`docs/build/DIRECTIVE-15-DECISIONS.md`)
дає лише **тимчасову пропозицію, а не політику**:

| | Пропозиція |
|---|---|
| повна копія | щоночі |
| журнал транзакцій | щогодини |
| RPO | 1 год |
| RTO | 4 год |

`tools/deploy-ecr.ps1` бекапу **не робить**. Модель відновлення бази код не
задає. Для щогодинного журналу база має бути в `FULL`.

```sql
ALTER DATABASE [Ecr] SET RECOVERY FULL;
BACKUP DATABASE [Ecr] TO DISK = N'<шлях>\Ecr_full.bak' WITH CHECKSUM, COMPRESSION;
BACKUP LOG      [Ecr] TO DISK = N'<шлях>\Ecr_log.trn'  WITH CHECKSUM, COMPRESSION;
```

(`COMPRESSION` на Express недоступна. `<шлях>` і ім'я бази — майданчика.)

### 6.3. Відновлення

1. `Stop-Service EcrApi`.
2. Відновити повну копію й журнали: `RESTORE … WITH NORECOVERY`, останній —
   `WITH RECOVERY`.
3. Перевірити RCSI (`06-rcsi.sql`). Сертифікати Data Protection: у `LocalMachine\My` кожного
   вузла мають бути **поточний і всі, якими зашифровані ключі у відновленій базі** (бекап міг
   бути зроблений до заміни сертифіката). Після старту — `/health/db`: якщо
   `unreadableKeyCertificates` не порожній — поставити ці PFX і передати їхні відбитки в
   `-PreviousDataProtectionCertificateThumbprints`; PFX немає — секрети каналів і пароль SMTP
   ввести наново (`https-certificate.md` §9).
4. Після відновлення **на інший сервер**: обліковий запис служби мусить мати логін на
   інстансі й користувача в базі (користувачі бази відновлюються, логіни — ні); членство
   `ecr_viewer` — `deploy-ecr.ps1 -ViewerAccount` (п. 14); задачі Agent — `14-agent-jobs.sql`;
   `Environment` служб — повтор `deploy-ecr.ps1 -SkipSchema` з тими самими параметрами.
5. `Start-Service EcrApi` (і `EcrWorker`), потім `/health/ready` і `/health/db`.

⚠ Відновлена база стартує лише з тією версією застосунку, чиї міграції їй відповідають:
незастосовані міграції в режимі `Validate` чи база, новіша за збірку, зупиняють старт
(`ECR-SYS-5031`).

**Доступ `bootstrap` втрачено.** Запис `bootstrap` створюється лише раз і ніколи не
видаляється, тож на наявній базі новий `-BootstrapPassword` ігнорується. Новий запис
`bootstrap` дає лише розгортання в **нову порожню базу** (`-FirstDeployment
-BootstrapPassword`); на наявній базі вхід відновлює інший адміністратор із
`Security.ManageUsers` (скидання пароля, `admin-guide.md` §2.2–2.3).

⚠ Процедура відновлення **на стенді не перевірялась**. Перевірте її до
приймання.

### 6.4. Ротація відкритих ключів Data Protection (S11)

**Коли:** один раз — після першого розгортання з сертифікатом
(`deploy-ecr.ps1 -DataProtectionThumbprint`) на майданчику, де служба
раніше працювала без нього.

**Чому:** з сертифікатом **нові** ключі кільця пишуться в
`sec.DataProtectionKey` зашифрованими, але **старий** відкритий ключ лишається
ключем за замовчуванням до свого спливу (типово 90 днів), а потім не зникає з
таблиці й **безстроково** приймається для розшифрування. Доки він у таблиці (і
в будь-якому бекапі) — будь-хто з доступом на читання може підробити cookie
сеансу будь-якого користувача. Чекати спливу строку марно: потрібне видалення
(кроки нижче).

⚠ Цей запит знаходить лише **відкриті** ключі. Ключі, зашифровані старим
сертифікатом (після заміни сертифіката), прибирають інакше —
`https-certificate.md` §9.3.

⛔ **Попередження — наслідки для користувачів:**

- **усі користувачі вийдуть із системи**: сеанси, підписані старими ключами,
  стануть недійсними;
- **секрети каналів сповіщень і пароль SMTP доведеться ввести наново**: вони
  зашифровані тими самими ключами і після ротації не розшифровуються. Перелік —
  `/admin/notifications` (панель «SMTP (outgoing mail)» і канали); перед ротацією
  підготуйте їх.

Узгодьте вікно з користувачами.

**Кроки** (на всіх вузлах одночасно):

1. Переконатися, що служба вже стартувала з сертифікатом: у реєстрі служби є
   `ECR_Auth__DataProtection__CertificateThumbprint`, `/health/db` не містить
   обмеження «Session keys are stored unencrypted».
2. Повний бекап бази (п. 6.2).
3. Зупинити службу на **кожному** вузлі: `Stop-Service EcrApi`.
4. Подивитися, які ключі відкриті:

   ```sql
   SELECT Id, FriendlyName FROM sec.DataProtectionKey
   WHERE Xml NOT LIKE N'%encryptedSecret%';
   ```

5. Видалити відкриті ключі:

   ```sql
   DELETE FROM sec.DataProtectionKey WHERE Xml NOT LIKE N'%encryptedSecret%';
   ```

   Якщо після цього в таблиці не лишилося жодного ключа — це нормально:
   застосунок створить новий, уже зашифрований, під час першого старту.
6. `Start-Service EcrApi` на всіх вузлах, потім `/health/ready` і `/health/db`.
7. Увійти в систему, ввести наново пароль SMTP (панель «SMTP (outgoing mail)»,
   проба «Send test message») і секрети каналів (`/admin/notifications`, перевірка —
   `POST /api/v1/notifications/channels/{id}/test`).
8. **Бекапи, зроблені до кроку 5, містять відкриті ключі.** Обмежте доступ до
   них або знищіть їх відповідно до політики зберігання: для них ротація
   нічого не змінює.

⚠ Процедура **на стенді не перевірялась**. Перевірте її до приймання.

## 7. Архівація років

`arc.usp_ArchiveYear @ProjectId, @FromPeriodKey, @ToPeriodKey, @BatchSize = 500000`
(`03-archive-proc.sql`) переносить дані з `DATA_HOT` в `DATA_ARCHIVE`.
Копія в `arc.*` іде пакетами по `@BatchSize` рядків (аудит L10-14; застосунок
передає 500 000 на Standard і 2 000 000 на Enterprise — `ArchiveBatchSize` на
`/health/db`); звірка сум — по всьому періоду після копії.

**Права.** `usp_ArchiveYear`, `usp_RestoreYear` і `usp_RestoreArchiveConstraints` —
`WITH EXECUTE AS OWNER` (аудит L10-08): зняття й повернення ключів і
`TRUNCATE … WITH (PARTITIONS)` ідуть від імені власника, викликачу потрібне лише
`EXECUTE` на процедуру. Перевірено тестом під користувачем без логіна з єдиним
правом `EXECUTE` (`ArchiveLeastPrivilegeTests`).

| Помилка | Значення |
|---|---|
| 50012 | у діапазоні є періоди проєктів, які ще не архівовано (`Status <> 4`) |
| 50013 | немає меж партицій для діапазону |
| 50010 | не збіглася контрольна сума, перенесення скасовано |
| 50014 | прогалина в діапазоні |
| 50015 | `@BatchSize` не додатний (нічого не змінено) |

Повернення: `arc.usp_RestoreYear` (50011 — не збіглася кількість рядків).

### 7.1. Що робить `usp_RestoreYear`

- Повертає період **цілком — для всіх проєктів діапазону**, а не лише для
  `@ProjectId`. Журнал `itg.ArchiveRun` отримує запис `FromArchive` на
  **кожен** повернутий проєкт; у сусідніх — примітка, заради якого проєкту
  запускали. Фільтра за проєктом немає навмисно: наступна архівація очищає
  `arc.*` на весь діапазон і знищила б архів сусіда.
- Працює **однією транзакцією**. Збій (зокрема 50011) — відкат: у гарячій
  схемі нічого не з'являється, архів лишається на місці, запис журналу —
  `Failed` із текстом помилки; для 50011 додатково знахідка
  `RESTORE_CHECKSUM` на `/admin/consistency`. Виправте причину й запустіть
  процедуру ще раз.
- Після успішної звірки (кількість екземплярів, рядків, комірок і сума)
  **прибирає `arc.*` діапазону**. Тому повтор безпечний: другий виклик
  бачить порожній архів і завершується `Completed` з приміткою «Архів
  діапазону порожній: період уже повернуто або не архівувався».

### 7.2. Позначка «осиротілий рядок» у роках, заархівованих до D4

До виправлення D4 (коміт `21d7c4f1`) архів не зберігав позначку
`IsOrphaned` (рядок посилається на запис довідника, нечинний у своєму
періоді; такі рядки блокують подання, `ECR-SUB-4221`). Колонку
`arc.TableRow.IsOrphaned` додає `12-archive-tables.sql` зі значенням **0**
для всього, що вже лежить в архіві. Роки, заархівовані **після**
оновлення, відновлюються зі справжньою позначкою.

Для **старого** року це означає: після `usp_RestoreYear` усі рядки мають
`IsOrphaned = 0`, і осиротілі рядки **не блокують подання**, доки позначку
не перерахують.

Хто її ставить — `IOrphanScanner`, його викликають дві задачі:

- нічні `OrphanScanJob` (`orphan-scan`) і `ConsistencyCheckJob`
  (`consistency-check`), щоночі о 02:15 (п. 4);
- вручну — `POST /api/v1/consistency/run` (право `System.RunJob`) або
  кнопка запуску на `/admin/consistency`: ставить `ConsistencyCheckJob`,
  яка серед іншого робить той самий перерахунок.

⚠ Перерахунок чіпає лише періоди в стані **Open** або **Grace**. Старий
рік зазвичай `Closed` — там позначка не ставиться, але й подати в
закритий період нічого не можна, тож ризику немає. Ризик з'являється,
коли такий період **перевідкривають** (`Reopen` → `Grace`): тоді

1. після перевідкриття запустіть перевірку вручну (або дочекайтесь
   нічного прогону) **до того**, як користувачі почнуть подавати;
2. перевірте в журналі задачі `orphan-scan`/`consistency-check`, що обхід
   **замкнувся** (параметр `cycles`, повідомлення «обхід набору
   ЗАМКНУВСЯ»): прогін має бюджет рядків і часу, і великий рік може
   потребувати кількох прогонів.

Незалежно від позначки `ConsistencyCheckJob` записує знахідки
`ORPHANED_CELL` і для закритих періодів — їх видно на `/admin/consistency`.

⛔ Архівація **не замінює** резервного копіювання (`08-nfr.md`). Перед архівацією
зробіть повний бекап.

⚠ **потрібне рішення замовника:** за скільки років дані лишаються «гарячими».

### 7.3. Архівація аудиту `arc.usp_ArchiveAudit` (✎ 2026-10-01, `D-247`, `D-236`, `НФ-8.4b`)

**Що робить.** Партиції `aud.CellChange`, `aud.StructureChange`, `aud.SecurityEvent`,
`aud.PublicationEvent`, **повністю** старші за `@OlderThanMonths` (дефолт **24**; NULL або < 1 мовчки стає 1),
перемикає (`ALTER TABLE … SWITCH PARTITION`) у
дзеркала `arc.AuditCellChange`, `arc.AuditStructureChange`, `arc.AuditSecurityEvent`,
`arc.AuditPublicationEvent` (`12-archive-tables.sql`; та сама `ps_AuditByMonth`, ті самі
індекси). Рядки не копіюються і не видаляються — це метаданкова операція; `Id` зберігаються.
Це **не** `arc.CellChange` (columnstore на `DATA_ARCHIVE`): SWITCH у нього неможливий.

Межа відсічення — «перше число поточного місяця (UTC) мінус `@OlderThanMonths` місяців»;
переносяться партиції, що **цілком** лежать раніше за неї (`04-partition-maintenance.sql`,
`arc.usp_ArchiveAudit`). Поточний місяць не переноситься ніколи. Перша партиція (усе до
2026-01-01) теж підпадає, у журналі для неї `periodStart = null`. Параметр `@Today` (дата
«сьогодні», за замовчуванням поточна UTC) — лише для тестів.

**Незмінність.** Тригери `aud.TR_*_Immutable` (`THROW 50060` на `UPDATE`/`DELETE`) **не
вимикаються**: `SWITCH` — DDL, DML-тригери не запускає. Архівні дзеркала мають такі самі
тригери (`arc.TR_Audit*_Immutable`).

**Хто й коли запускає.** Нічна задача `partition-check` (`PartitionCheckJob`, щоночі 02:15
за часом сервера) спершу продовжує межі аудиту (`arc.usp_EnsureAuditPartitions
@MonthsAhead = 12`, п. 7.4), потім викликає `arc.usp_ArchiveAudit @OlderThanMonths = 24`.
Поріг — константа `PartitionCheckJob.AuditArchiveOlderThanMonths`, **ключа конфігурації
немає**; місяць рахується за UTC. Ідемпотентно, у C# DDL немає.

- Збій **архівації** (помилка SQL) не ховає перевірку запасу партицій: прогін
  `partition-check` стає `Degraded` з `"auditArchiveFailed":true` у `DetailsJson`, подробиці —
  у рядку `audit-archive`/`Failed`.
- Збій **продовження меж** (немає права `EXECUTE`, таймаут блокування) робить увесь прогін
  `Failed`: тієї ночі не виконуються ні архівація, ні перевірка запасу
  (`PartitionCheckJob.cs`, виклик без перехоплення). Повтор — наступної ночі.

**Права.** Обидві процедури `WITH EXECUTE AS OWNER`, тож викликачу потрібне лише `EXECUTE`.
Службовому акаунту застосунку його видає **DBA** (`D-246`, `D-247`, `D-264`;
`deploy-ecr.ps1` `GRANT` не робить) — команди в п. 7.4.

**Вручну:**

```sql
DECLARE @p int, @r bigint;
EXEC arc.usp_ArchiveAudit @OlderThanMonths = 24, @PartitionsSwitched = @p OUTPUT, @RowsSwitched = @r OUTPUT;
SELECT @p AS Partitions, @r AS [Rows];
```

**Журнал** — `itg.MaintenanceRun`, `JobCode = 'audit-archive'`, рядок на кожну
(таблиця, партиція): `Succeeded` — `DetailsJson` = таблиця, номер партиції, `periodStart`
(початок місяця), `rows`, `by` (`ORIGINAL_LOGIN()`), `olderThanMonths`; `FinishedAt` — коли.
Перенос і запис — одна транзакція. `Failed` — відкат + текст помилки (процедура кидає її
далі). `Degraded` — у цілі вже є рядки цієї партиції (пізній запис у вже заархівований
місяць): партицію **не перенесено**, потрібна ручна розв'язка. Порожні/вже перенесені
партиції — без запису. Процедура зупиняється на першому `Failed`: решта партицій чекає
наступного прогону. `Degraded` пишеться **щоночі заново**, доки конфлікт не розв'язано, і
щоночі потрапляє у зведення збоїв сповіщень (`NotificationJob` вважає збоєм кожен рядок
`itg.MaintenanceRun` зі `Status <> 'Succeeded'`).

**Розв'язати `Degraded`.** Поки ціль `arc.Audit*` у цій партиції непорожня, `SWITCH`
неможливий, а тригери незмінності забороняють `DELETE`/`UPDATE` в обох таблицях. Готового
рецепта злиття в коді немає — це рішення DBA: або лишити як є (рядки пізнього запису лишаються
в `aud.*`, `Degraded` повторюється щоночі), або повернути архівну партицію в `aud.*`
(«Відновити» нижче) — чи вміщує тоді партиція `aud.*` обидві частини, **не перевірено**.

**Відновити** (SWITCH назад; ціль `aud.*` у тій партиції має бути порожньою). Виконує DBA:
ручний `ALTER TABLE … SWITCH` іде без `EXECUTE AS OWNER` і потребує прав `ALTER` на обидві
таблиці.

```sql
DECLARE @p int = $PARTITION.pf_AuditByMonth('2026-03-01');
DECLARE @sql nvarchar(400) = N'ALTER TABLE arc.AuditSecurityEvent SWITCH PARTITION ' + CAST(@p AS nvarchar(10))
    + N' TO aud.SecurityEvent PARTITION ' + CAST(@p AS nvarchar(10));
EXEC sp_executesql @sql;
```

⚠ Нічна задача завжди викликає процедуру з порогом 24 (константа в коді). Тому відновлена
партиція, старша за 24 місяці, буде знову заархівована найближчої ночі о 02:15. Для аналізу
краще не відновлювати, а читати архів прямо (UI і експорт CSV архіву не бачать):

```sql
SELECT * FROM arc.AuditSecurityEvent WHERE ChangedAt >= '2024-03-01' AND ChangedAt < '2024-04-01';
```

**Ризики.**
- Читачі (`AuditReader`, історія комірки) дивляться лише в `aud.*`: заархівований період
  зникає з UI історії. Тому дефолт 24 міс. — **припущення**, не вказане в `D-236`; потрібне
  рішення замовника про глибину «гарячого» аудиту.
- Дані лишаються у файловій групі `AUDIT`: переміщення на дешевший диск — окрема операція
  DBA над файлами групи; SWITCH місця не звільняє.
- SWITCH бере `SCH-M` на мить; `LOCK_TIMEOUT` 30 с — за довгою транзакцією прогін падає
  (`Failed`), повторить наступна ніч.
- Додаєш колонку чи індекс до `aud.*` — додай і до `arc.Audit*`: інакше SWITCH падає
  (Msg 4943/4904/4912/4913 — за коментарем у `12-archive-tables.sql`, на сервері не
  відтворено). Інтеграційний тест `AuditArchiveSwitchTests` проганяє SWITCH, окремої звірки
  колонок у ньому немає.
- Бекап до архівації обов'язковий (див. вище).

**Моніторинг:** `SELECT * FROM itg.MaintenanceRun WHERE JobCode = 'audit-archive' AND Status <> 'Succeeded' ORDER BY Id DESC;`
і нічний прогін:

```sql
SELECT TOP (10) Id, Status, FinishedAt, DetailsJson
FROM itg.MaintenanceRun WHERE JobCode = 'partition-check' ORDER BY Id DESC;
```

Поля `DetailsJson`: `boundariesAhead`, `minimum` (запас `pf_ByPeriodKey`), `auditBoundariesAdded`
(скільки меж аудиту додано тієї ночі), `auditArchivedPartitions`, `auditArchivedRows`,
`auditArchiveFailed`. `Status = 'Failed'` означає, що й межі аудиту не продовжено.

### 7.4. Межі партицій аудиту `pf_AuditByMonth` (`D-246`)

Таблиці `aud.CellChange`, `aud.StructureChange`, `aud.SecurityEvent`, `aud.PublicationEvent`
(і архівні `arc.Audit*`) партиціоновано помісячно за `ChangedAt` (`pf_AuditByMonth`,
`RANGE RIGHT`, усі партиції — файлова група `AUDIT`). Початкові межі — 2026-01-01…2027-06-01
(`02-partitions.sql`).

**Хто продовжує.** `arc.usp_EnsureAuditPartitions @MonthsAhead = 12, @Today = NULL, @Added OUTPUT`
(`04-partition-maintenance.sql`, `WITH EXECUTE AS OWNER`, `LOCK_TIMEOUT` 30 с) додає відсутні
межі — перші числа місяців від **наступного** до +12 від поточного (UTC). Ідемпотентна;
`SPLIT` порожньої крайньої партиції даних не переміщує. Якщо функції `pf_AuditByMonth` немає —
нічого не робить (`@Added = 0`). Викликають:

- нічна задача `partition-check` (щоночі 02:15, п. 4);
- Agent-задача `ECR: Partitions ahead` через `arc.usp_EnsurePartitions` (1-го числа, 02:40;
  на Express Agent немає).

Розгортання (`deploy-ecr.ps1`) процедури лише створює, не викликає.

**Права.** Службовому акаунту застосунку потрібне `EXECUTE` (видає DBA, `D-264`):

```sql
GRANT EXECUTE ON OBJECT::arc.usp_EnsureAuditPartitions TO [<користувач БД служби>];
GRANT EXECUTE ON OBJECT::arc.usp_ArchiveAudit         TO [<користувач БД служби>];
```

Без першого права прогін `partition-check` щоночі `Failed` (сповіщення «Background job
failed»), і не виконуються ні архівація аудиту, ні перевірка запасу партицій.

**Перевірити горизонт вручну** (⚠ `/health/db` і картка стану горизонт аудиту **не**
показують — лише `pf_ByPeriodKey`):

```sql
SELECT MAX(CAST(rv.value AS datetime2(3))) AS LastBoundary,
       DATEDIFF(MONTH, DATEFROMPARTS(YEAR(GETUTCDATE()), MONTH(GETUTCDATE()), 1),
                MAX(CAST(rv.value AS datetime2(3)))) AS MonthsAhead
FROM sys.partition_range_values rv
JOIN sys.partition_functions pf ON pf.function_id = rv.function_id
WHERE pf.name = N'pf_AuditByMonth';
```

Норма — `MonthsAhead` = 12 (щонайменше 11 перед нічним прогоном).

**Продовжити вручну** (DBA або акаунт із `EXECUTE`):

```sql
DECLARE @a int; EXEC arc.usp_EnsureAuditPartitions @MonthsAhead = 12, @Added = @a OUTPUT; SELECT @a AS Added;
```

**Якщо межі закінчились.** Записи аудиту не губляться: усе після останньої межі лягає в
крайню праву партицію, і вона росте (висновок із `RANGE RIGHT`, на стенді не відтворено).
Наслідки: архівація переносить цю партицію лише цілком і лише коли вона вся старша за поріг;
процедура додає межі від наступного місяця, тож поточний і пропущені місяці лишаються в одній
партиції; `SPLIT` непорожньої партиції переміщує дані з блокуванням. Що робити: продовжити
межі якнайшвидше, у вікно обслуговування, з бекапом.

## 8. Оновлення версії

Схема має **два джерела**: міграції EF (основні таблиці) і `Sql/*.sql`
(файлові групи, партиції, аудит, архів, тригери, RCSI, Agent). Порядок —
як у `deploy-ecr.ps1`, крок 2/7:

```
01-filegroups → 02-partitions → міграція EF (migration.sql, idempotent)
→ 11-audit-tables → 07-partition-tables → 08-system-tables → 12-archive-tables
→ 13-cache-table → 03-archive-proc → 04-partition-maintenance → 05-rpt-views
→ 15-cell-tvp → 10-triggers → 06-rcsi   [+ 14-agent-jobs лише з -FirstDeployment]
```

`09-seed.sql` виконує застосунок на старті. Скрипти запускати не треба.

✎ 2026-10-01: **вікно обслуговування для великих баз.** Міграція `PerfFixJobsStaleHealth`
додає до `itg.JobProgress` збережену обчислювану колонку `FanOutParentJobId` (з `Payload`) і індекси
`IX_JobProgress_FanOutParent`, `IX_JobProgress_State_UpdatedAt`, а також `IX_CalculationRun_Project_FinishedAt`
(`calc.CalculationRun`). Додавання збереженої колонки переписує таблицю: на вимірюваному стенді
≈ 20 с на 500 тис. рядків `itg.JobProgress`, на цей час таблиця заблокована. Якщо `itg.JobProgress` велика
(`SELECT COUNT(*) FROM itg.JobProgress`), оновлюйте у вікно без активних задач (зупиніть `EcrWorker` і
`EcrApi`) і заздалегідь перевірте місце під журнал транзакцій. Репетиція на копії бази:
`powershell -File tools\setup-dev-db.ps1 -Server <сервер> -Database <копія> -Upgrade` (оновлює лише наявну
базу, нову не створює; `-Upgrade` без `-RequireFreeGb` місце не перевіряє).

⛔ **Перед оновленням на цю версію (S11, 2026-09-29) — один раз.** Служба в
Production більше не стартує, доки ключі кільця Data Protection не захищені
сертифікатом (п. 2.1). До оновлення:

1. Імпортувати **один** сертифікат **із закритим ключем** (PFX) у
   `Cert:\LocalMachine\My` на **кожному** вузлі — той самий на всіх.
2. Дати обліковому запису служби право **читати закритий ключ**
   (`certlm.msc` → сертифікат → «Усі завдання» → «Керування закритими
   ключами…» → «Читання»).
3. Передати відбиток: `deploy-ecr.ps1 -DataProtectionThumbprint '<відбиток>'`
   або крок «Сертифікат Data Protection» у майстрі `tools/Ecr.Setup`.

`deploy-ecr.ps1` без відбитка (або з відбитком сертифіката, якого немає чи
який без закритого ключа) зупиняється на кроці 1/7 «передумови» — **до**
схеми й MSI, тож працююча служба не зупиняється. Ризик лише в **ручному
оновленні MSI** без `deploy-ecr.ps1` і без відбитка в реєстрі служби: нова
версія встановиться, а служба не стартне (п. 5, «ключі кільця
DataProtection … не захищені»). ⛔ ✎ 2026-09-30: `Environment` служби
(відбиток, рядок підключення, режим Api) оновлення MSI **стирає** — джоб
`msi-install` це перевірив (п. 10.3), тож ручне оновлення без
`deploy-ecr.ps1` лишає службу й без рядка підключення. ⚠ Згоду
`Auth:DataProtection:AllowUnprotectedKeys` на майданчику **не вмикати**: ключі
лишаються відкритими в базі й бекапах, старт пише `Critical`, `db` постійно
`Degraded` — вона лише для одноразових стендів. Після першого старту з
сертифікатом — ротація старих відкритих ключів, п. 6.4.

Процедура:

1. Повний бекап (п. 6.2) і PFX сертифіката(ів) Data Protection (п. 6.1).
2. **Зупинити застосунок:** `Stop-Service EcrWorker` (якщо є), потім `Stop-Service EcrApi`.
   ⚠ `deploy-ecr.ps1` служб перед схемою **не зупиняє**: без цього кроку схема
   застосовується, поки стара версія працює, `06-rcsi.sql` (на базі без RCSI) обриває
   сеанси (`ROLLBACK IMMEDIATE`), міграції можуть блокувати таблиці, а між кроками 2 і 6
   скрипта стара версія працює на новій схемі без нового сіду.
3. Розгортання:

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools\deploy-ecr.ps1 `
     -SqlInstance <сервер> -Database <база> -MsiPath <шлях до .msi> `
     -ServiceAccount '<DOMAIN\ecr-svc$>' -ConnectionString $cs `
     -DataProtectionThumbprint <відбиток> `
     -HttpsThumbprint <відбиток HTTPS> -AppPort 443 -WhatIf
   ```

   Без `-BootstrapPassword` і без `-FirstDeployment`. На **кожному** оновленні (MSI
   стирає `Environment` служб): транспорт — рівно один із `-HttpsThumbprint` /
   `-BehindHttpsProxy` / `-AllowHttp` (інакше зупинка на кроці 1 «Транспорт не обрано»),
   `-ConnectionString`, `-ServiceAccount`, `-PreviousDataProtectionCertificateThumbprints`
   (якщо був) і `-DisableWorker` (якщо воркера не має бути).

   Спершу запустіть із `-WhatIf`, потім без нього. Кроки скрипта: передумови,
   схема, MSI (`msiexec /qn`), змінні служби, конфіг (лише якщо ще заглушка),
   запуск служб (лише з `-ServiceAccount`; без нього служба не стартує, і крок 7 не
   дочекається `/health/live`), перевірка `GET /health/live`, потім очікування
   `GET /health/ready` до `Healthy`/`Degraded` (не довше `-ReadyTimeoutSeconds`,
   дефолт 180 с). Перевірки, що не `Healthy`, скрипт друкує. `Unhealthy` лише
   через `sources` — попередження (зовнішнє джерело, ручне введення працює);
   будь-яка інша `Unhealthy` після тайм-ауту — розгортання провалене, «Готово»
   не друкується.
4. Перевірити `/health/ready` і `/health/db`; виставити вручну змінні, яких скрипт не пише
   (`ECR_Jobs__Workers__*`, `ECR_Secrets__*`, `ECR_PiWebApi__AllowedHosts__*`), і
   перезапустити служби, якщо щось змінили.

⛔ **Будь-який `msiexec` поза скриптом** (ремонт, ручне оновлення, перевстановлення) стирає
`Environment` служб — після нього **знову виконайте `deploy-ecr.ps1`** з тими самими
параметрами, при зупиненому застосунку (кроки 2–4 вище).

⛔ ✎ 2026-09-30: **`Environment` служб (`EcrApi`, `EcrWorker`) не переживає
оновлення MSI** (`MajorUpgrade` перевстановлює службу; перевірено CI-джобом
`msi-install (windows)`, D3). Тому оновлення — це **завжди** `deploy-ecr.ps1`
(він пише `Environment` кроками 4–5 щоразу, разом із `-ConnectionString`),
а не голий `msiexec`. Змінні, яких скрипт не пише (`ECR_Jobs__Workers__*`,
п. 10, будь-які ваші), після оновлення виставте знову.

Графічний майстер `tools/Ecr.Setup` запускає той самий `deploy-ecr.ps1`.

### 8.1. Помилка 50148: «Передперевірка D148»

**Кого стосується.** Лише бази, розгорнуті до 2026-09-20, тобто до міграції
`20260920223149_D148CellValueScale16`. Нові бази й бази, де D148 уже
застосовано, цю перевірку не проходять узагалі.

**Що сталося.** Три міграції `D148*Scale16` переводять десять стовпців із
`decimal(28,10)` у `decimal(28,16)`. Ціла частина на цьому кроці скорочується з
18 розрядів до 12. Наступні `D148*Precision34` повертають 18 розрядів
(`decimal(34,16)`). Тобто межа 1e12 **не є межею домену**: кінцевий тип
вміщує значення до 1e18. Це обмеження лише проміжного кроку. Приклад такого
значення — 1 ТДж у джоулях.

Без перевірки SQL Server зупинив би `ALTER COLUMN` помилкою
`Msg 8115 Arithmetic overflow`. Вона не називає ні стовпця, ні рядків.
Перевірка йде першою командою в кожній із трьох міграцій, тобто **до зміни
схеми**. Вона перелічує стовпці, кількість рядків і максимум за модулем:

```
Msg 50148 … Передперевірка D148: оновлення зупинено ДО зміни схеми. …
Поза межею:
  doc.CellValue.ValueNumeric: рядків 1, max |x| = 1000000000000.0000000000
Схему й дані не змінено. …
```

Після цієї помилки `deploy-ecr.ps1` зупиняється на кроці 2/7. MSI не
ставиться, стара версія лишається робочою, схема й дані — без змін.

**Що робити.** Суть процедури: тимчасово відкласти ці значення, пройти всю
серію D148 і повернути їх. Служба весь цей час зупинена.

1. `Stop-Service EcrApi`, повний бекап (п. 6.2).
2. Для **кожного** стовпця з повідомлення відкласти значення в таблицю
   `dbo.D148Hold_<схема>_<таблиця>` за первинним ключем і поставити 0.
   Приклад для `doc.CellValue`:

   ```sql
   SET XACT_ABORT ON;
   BEGIN TRANSACTION;
   SELECT PeriodKey, TableRowId, ColumnDefId, ValueNumeric AS OldValue
   INTO dbo.D148Hold_doc_CellValue
   FROM doc.CellValue
   WHERE ValueNumeric >= 1000000000000 OR ValueNumeric <= -1000000000000;

   UPDATE t SET ValueNumeric = 0
   FROM doc.CellValue AS t
   JOIN dbo.D148Hold_doc_CellValue AS h
     ON h.PeriodKey = t.PeriodKey AND h.TableRowId = t.TableRowId AND h.ColumnDefId = t.ColumnDefId;
   COMMIT;
   ```

   ⚠ Межу пишіть цілим літералом `1000000000000`, не `1e12`. Літерал `1e12` у
   T-SQL має тип `float`, і значення біля межі (`999999999999.9999999999`)
   округлюються до нього, тобто відкладаються зайві рядки.

   Ключі решти стовпців:

   | Стовпець | Первинний ключ |
   |---|---|
   | `doc.CellValue.ValueNumeric` | `PeriodKey, TableRowId, ColumnDefId` |
   | `doc.DocumentIndexValue.ValueNumeric` | `Id` |
   | `rpt.ReportRow.ValueNumeric` | `SnapshotId, RowNo, ColumnCode` |
   | `ext.RawDataPoint.ValueNumeric` | `Id` |
   | `dic.RegistryValue.ValueNumeric` | `Id` |
   | `calc.CalculationResult.Value` | `PeriodKey, Id` |
   | `calc.CalculationInput.Value` | `PeriodKey, Id` |
   | `calc.CalculationStep.Value` | `PeriodKey, Id` |
   | `calc.MethodologyConstant.Value` | `Id` |
   | `calc.TestCase.Tolerance` | `Id` |

3. Застосувати `01-filegroups.sql`, `02-partitions.sql` і `migration.sql`.
   Прапорці ті самі, що в `deploy-ecr.ps1`:
   `sqlcmd -S <сервер> -E -C -b -I -d <база> -i <файл>`. Тепер `migration.sql`
   проходить до кінця, стовпці стають `decimal(34,16)`.
4. Повернути значення за ключем:
   `UPDATE t SET ValueNumeric = h.OldValue FROM … JOIN dbo.D148Hold_… AS h ON …`.
   Потім перевірити, що розбіжностей немає:
   `SELECT COUNT(*) … WHERE t.ValueNumeric <> h.OldValue` → `0`.
5. Видалити таблиці `dbo.D148Hold_*`.
6. Звичайне розгортання (п. 8, крок 2). `migration.sql` ідемпотентний, тож
   застосовані міграції він пропускає.

✎ 2026-09-28: процедуру перевірено на стенді для `doc.CellValue` (`1e12`) і
`calc.CalculationResult` (`-2.5e13`, `NOT NULL`). Шлях: `sqlcmd` з прапорцями
`deploy-ecr.ps1` → 50148 → відкладення → `migration.sql` → повернення. Значення
повернулися без змін, тип `34,16`, розбіжностей 0. Решту восьми стовпців
вручну не проходили. Передперевірку для всіх десяти тримає тест
`D148ScalePrecheckTests`.

⛔ Не редагуйте міграції й не видаляйте рядки, щоб «пройти» перевірку. Це
справжні дані, і кінцевий тип їх вміщує.

### 8.2. Міграція U1: помилка 50301 «Передперевірка U1» і вікно для індексу

**Кого стосується.** Бази, розгорнуті до міграції
`20260928224103_U1UnitForeignKeys`. Нові бази цю перевірку проходять порожньою.

**Що робить міграція.** Три речі:

- `FK_ColumnDef_Unit`: одиниця колонки шаблону (`cfg.ColumnDef.UnitId`) → `uom.Unit`;
- `FK_RegField_Unit`: одиниця поля довідника (`cfg.RegistryFieldDef.UnitId`) → `uom.Unit`;
- індекс `IX_CalculationResult_UnitId` під наявний `FK_CRes_Unit`.

До U1 ці дві одиниці ключа не мали. Тому видалення одиниці могло лишити колонку
чи поле з номером одиниці, якої вже немає. Такий рядок SQL Server відхилив би на
`ADD CONSTRAINT` помилкою `547` без переліку. Перевірка йде **першою командою
міграції, до зміни схеми**, і перелічує все, що висить:

```
Msg 50301 … Передперевірка U1: оновлення зупинено ДО зміни схеми. …
Висячі посилання:
  cfg.ColumnDef.UnitId: рядків 2; одиниць немає: 57, 58; перші рядки: Id 1204 CO2 (TableDefId 88, UnitId 57), …
  cfg.RegistryFieldDef.UnitId: рядків 1; одиниць немає: 57; перші рядки: Id 31 LIMIT (RegistryDefId 4, UnitId 57)
Схему й дані не змінено. …
```

Показано до десяти одиниць і до десяти рядків на таблицю. Повний перелік дає
запит із кроку 2. Після помилки `deploy-ecr.ps1` зупиняється на кроці 2/7. MSI не
ставиться, стара версія лишається робочою, схема й дані не змінені.

**Чому перевірка не обнуляє сама.** Значення в документах вводилися й
рахувалися в тій одиниці, на яку посилається колонка. Яка одиниця правильна,
вирішує методолог, а не міграція. Тихе `UnitId = NULL` зробило б числа
безрозмірними, і перерахунок чи конвертація дали б інший результат без жодної
помилки.

**Що робити.**

1. `Stop-Service EcrApi`, повний бекап (п. 6.2).
2. Повний перелік висячих посилань:

   ```sql
   SELECT N'cfg.ColumnDef' AS [Table], c.Id, c.Code, c.TableDefId AS OwnerId, c.UnitId
   FROM cfg.ColumnDef AS c
   WHERE c.UnitId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM uom.Unit AS u WHERE u.Id = c.UnitId)
   UNION ALL
   SELECT N'cfg.RegistryFieldDef', f.Id, f.Code, f.RegistryDefId, f.UnitId
   FROM cfg.RegistryFieldDef AS f
   WHERE f.UnitId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM uom.Unit AS u WHERE u.Id = f.UnitId)
   ORDER BY 1, 2;
   ```

3. Для **кожного** відсутнього `UnitId` методолог обирає одне з трьох:

   - **Повернути одиницю з тим самим `Id`.** Це основний варіант: дані лишаються
     в тій одиниці, в якій їх вводили. Код, розмірність, множник і зсув беруть із
     бекапу, зробленого до видалення одиниці:

     ```sql
     SET IDENTITY_INSERT uom.Unit ON;
     INSERT INTO uom.Unit (Id, Code, SymbolL10n, NameL10n, DimensionId, IsBase, FactorToBase, OffsetToBase)
     VALUES (57, N'<код>', N'<символ JSON>', N'<назва JSON>', <розмірність>, 0, <множник>, <зсув>);
     SET IDENTITY_INSERT uom.Unit OFF;
     ```

     Якщо код тим часом зайняла нова одиниця (`UQ_Unit_Code`), дайте повернутій
     інший код. Посилання тримаються за `Id`, не за кодом.

   - **Перевести на іншу наявну одиницю.** Лише якщо методолог підтвердив, що
     введені значення насправді в ній:
     `UPDATE cfg.RegistryFieldDef SET UnitId = <нова> WHERE UnitId = <висяча>;`
     Для `cfg.ColumnDef` **опублікованої** версії шаблону тригер
     `TR_ColumnDef_Immutable` таку зміну відхилить (`50001`), і так і має бути:
     опублікована структура незмінна (ФВ-7.1). Для таких колонок лишається
     перший варіант.
   - **Зняти одиницю (`NULL`)** — лише для колонки чи поля, де одиниця
     справді не потрібна (текст, дата), і лише в чернетці шаблону.

4. Повторити запит із кроку 2 → порожньо.
5. Звичайне розгортання (п. 8, крок 2). `migration.sql` ідемпотентний, тож
   застосовані міграції пропускає.

⛔ Не редагуйте міграцію й не видаляйте рядки, щоб «пройти» перевірку.

**Вікно обслуговування для індексу.** `IX_CalculationResult_UnitId` будується на
всій `calc.CalculationResult`, тобто на мільйонах рядків за рік розрахунків.
Міграція сама визначає редакцію сервера (`SERVERPROPERTY('EngineEdition')`):

| `EngineEdition` | Редакція | Як будується |
|---|---|---|
| 3 | Enterprise, Developer, Evaluation | `WITH (ONLINE = ON)`: таблиця доступна весь час |
| 5, 8 | Azure SQL Database, Managed Instance | `WITH (ONLINE = ON)` |
| 2, 4 | Standard, Express | офлайн: на час побудови таблиця заблокована |

На Standard і Express запис і читання результатів розрахунку чекають, доки
індекс не збудується. `deploy-ecr.ps1` застосовує схему, поки стара служба ще
працює, тому таке оновлення проводьте **у вікні обслуговування**, коли не йде
перерахунок і ніхто не відкриває звітів. Тривалість на конкретній базі
заздалегідь невідома. Її дає пробний прогін на копії з бекапу. Прод-сервери
замовника — Enterprise (`D-101`), тож там вікно не потрібне.

✎ 2026-09-29: передперевірку й обидва шляхи застосування (`MigrateAsync` і
`migration.sql --idempotent`) тримає тест `U1UnitForeignKeysMigrationTests`.
Гілку індексу без `ONLINE` тест виконує наживо на Developer, підставляючи
редакцію 4. Справжнього Standard чи Express у перевірці не було.

### 8.3. Міграція Q222: помилка 50222 «Передперевірка Q222» (дублі під унікальні індекси)

**Кого стосується.** Бази, розгорнуті до міграції
`20260910231342_Q222MissingForeignKeysAndConstraints` (10.09.2026) і ще не
оновлені. Нові бази й бази, де Q222 уже застосовано, цю перевірку не бачать.

**Що робить міграція (щодо унікальності).** Три унікальні індекси на наявних
таблицях:

| Індекс | Таблиця | Ключ |
|---|---|---|
| `UQ_RoleAssignment_Sid` (фільтр `PrincipalSid IS NOT NULL`) | `sec.RoleAssignment` | `(PrincipalSid, RoleId)` |
| `UQ_RoleAssignment_User` (фільтр `UserId IS NOT NULL`) | `sec.RoleAssignment` | `(UserId, RoleId)` |
| `UQ_MethodologyConstant` | `calc.MethodologyConstant` | `(MethodologyVersionId, Code, ISNULL(Category, ''), ISNULL(ValidFrom, 1900-01-01))` |

До Q222 база цих ключів не тримала. Дубль SQL Server відхилив би на
`CREATE UNIQUE INDEX` помилкою `1505` без переліку. Перевірка йде **першою
командою міграції, до зміни схеми**:

```
Msg 50222 … Передперевірка Q222: оновлення зупинено ДО зміни схеми. …
Дублі (кількість груп; перші групи):
  UQ_RoleAssignment_Sid (sec.RoleAssignment): 1 груп; (SID S-1-5-21-…, роль 3) x2
  UQ_MethodologyConstant (calc.MethodologyConstant): 1 груп; (версія методології 7, код K1, категорія , діє з 1900-01-01) x2
Схему й дані не змінено, автоматичного видалення немає. …
```

Показано до десяти груп на індекс. Після помилки `deploy-ecr.ps1` зупиняється на
кроці 2/7. MSI не ставиться, стара версія лишається робочою.

**Чому перевірка не видаляє дублі сама.** Яка з двох констант чи призначень
правильна, залежить від змісту: у константах різні `Value`, `UnitId` й
`TextValue` дають різні розрахунки. Тихе видалення змінило б результати без
помилки.

**Що робити.**

1. `Stop-Service EcrApi`, повний бекап (п. 6.2).
2. Повний перелік дублів (кожен запит має повернути порожньо, коли все чисто):

   ```sql
   -- призначення ролі групі AD
   SELECT PrincipalSid, RoleId, COUNT(*) AS Cnt, MIN(Id) AS FirstId, MAX(Id) AS LastId
   FROM sec.RoleAssignment WHERE PrincipalSid IS NOT NULL
   GROUP BY PrincipalSid, RoleId HAVING COUNT(*) > 1;

   -- призначення ролі особі
   SELECT UserId, RoleId, COUNT(*) AS Cnt, MIN(Id) AS FirstId, MAX(Id) AS LastId
   FROM sec.RoleAssignment WHERE UserId IS NOT NULL
   GROUP BY UserId, RoleId HAVING COUNT(*) > 1;

   -- константи методології (Category і ValidFrom необов'язкові: NULL = порожньо / 1900-01-01)
   SELECT MethodologyVersionId, Code, ISNULL(Category, N'') AS Category,
          ISNULL(ValidFrom, CONVERT(date, '19000101', 112)) AS ValidFrom, COUNT(*) AS Cnt
   FROM calc.MethodologyConstant
   GROUP BY MethodologyVersionId, Code, ISNULL(Category, N''), ISNULL(ValidFrom, CONVERT(date, '19000101', 112))
   HAVING COUNT(*) > 1;
   ```

3. Для кожної групи подивіться всі її рядки (`SELECT * FROM … WHERE <ключ>`) і
   лишіть один.
   - `sec.RoleAssignment`: рядки з однаковою особою чи групою й роллю
     рівнозначні, якщо збігаються `ScopeJson`, `ValidFrom` і `ValidTo`. Тоді видаліть
     зайві: `DELETE FROM sec.RoleAssignment WHERE Id IN (…)`. Якщо ці стовпці
     різні, це не дубль за змістом: обговоріть із власником ролі, перш ніж
     видаляти.
   - `calc.MethodologyConstant`: порівняйте `Value`, `TextValue`, `UnitId`, `ValidTo`.
     Лишіть той, який застосовує розрахунок (`calc.CalculationInput`/`CalculationStep`
     посилаються на константи за кодом); якщо значення різні, рішення за методологом.
     Ідентичні рядки видаляйте.
4. Повторіть запити з кроку 2: порожньо.
5. Звичайне розгортання (п. 8, крок 2). `migration.sql` ідемпотентний.

⛔ Не редагуйте міграцію й не знімайте індекс, щоб «пройти» перевірку.

✎ 2026-09-30: передперевірку й обидва шляхи застосування (`MigrateAsync` і
`migration.sql --idempotent`) тримає тест `Q222UniquePrecheckTests`.

### 8.4. Міграція AN34: помилка 50401 «Передперевірка AN-34 L4-01» (дві сутності на один довідник)

**Кого стосується.** Бази, розгорнуті до міграції `AN34SourceEntityRegistryUnique`
(05.10.2026) і ще не оновлені. Нові бази й бази, де вона вже застосована, цю
перевірку не бачать.

**Що робить міграція.** Фільтрований унікальний індекс `UQ_SourceEntity_Registry`
на `ext.SourceEntity (DataSourceId, RegistryDefId) WHERE RegistryDefId IS NOT NULL`:
одну сутність збору одного з'єднання можна прив'язати до довідника, лише якщо
жодна інша сутність цього з'єднання (навіть вимкнена) його не тримає. Зв'язки синку
довідника (`dic.RegistryExternalKey`) беруться за парою з'єднання-довідник без
сутності, тож дві сутності на один довідник щопрогону вимикали б записи одна одної.
Перевірка йде **першою командою міграції, до зміни схеми**:

```
Msg 50401 … Передперевірка AN-34 L4-01: оновлення зупинено ДО зміни схеми. …
Груп (з'єднання, довідник): 1; перші: (з'єднання 1, довідник 5, сутності: 12 PlantA, 14 PlantB (вимкнена))
Схему й дані не змінено, автоматичного відв'язування немає. …
```

**Що робити.**

1. `Stop-Service EcrApi`, повний бекап (п. 6.2).
2. Повний перелік (порожньо, коли все чисто):

   ```sql
   SELECT DataSourceId, RegistryDefId, COUNT(*) AS Cnt, MIN(Id) AS FirstId, MAX(Id) AS LastId
   FROM ext.SourceEntity WHERE RegistryDefId IS NOT NULL
   GROUP BY DataSourceId, RegistryDefId HAVING COUNT(*) > 1;
   ```

3. Для кожної групи вирішіть, яка сутність власник довідника (звичайно та, чиї
   записи вже прив'язані: `dic.RegistryExternalKey` + мапінги `ext.EntityFieldMap`).
   Решту відв'яжіть: в інтерфейсі «Інтеграція, сутності збору» або
   `PUT /api/v1/sources/{id}/registry` з `registryDefId = null`. Мапінги полів
   відв'язаної сутності на довідник перегляньте окремо: вони більше не діють.
   Автоматичного вибору немає: який з двох варіантів правильний, залежить від
   того, чиї зв'язки записів чинні.

   ⚠ Індекс `UQ_SourceEntity_Registry` **не фільтрує за `IsActive`**. Тож
   **вимкнену** сутність теж треба відв'язати перед міграцією, якщо вона ділить
   довідник з іншою: «вимкнена» не означає «не рахується». У переліку
   передперевірки такі сутності позначені «(вимкнена)». Вимкнена сутність, яку
   лишили прив'язаною, блокує і міграцію, і пізніший вибір довідника для
   активної (помилка унікальності).
4. Повторіть запит з кроку 2: порожньо.
5. Звичайне розгортання (п. 8, крок 2). `migration.sql` ідемпотентний.

⛔ Не редагуйте міграцію й не знімайте індекс, щоб «пройти» перевірку.

Тест: `AN34SourceEntityRegistryUniqueMigrationTests`.

### 8.5. Міграція AN34: індекс подій синку `IX_CollectionCoverage_RegistryEvents` (час, розмір, ONLINE/офлайн)

**Кого стосується.** Бази, розгорнуті до міграції `AN34CollectionCoverageDedupIndex`
(05.10.2026) і ще не оновлені. Передперевірки немає: міграція даних не змінює,
дублів у не унікальному індексі не буває. Помилок оновлення вона не дає, але
**будує індекс на журналі покриття**, тож на великому журналі це помітний крок.

**Що робить міграція.** Індекс
`itg.CollectionCoverage (SourceEntityId, Id) INCLUDE (Status, PeriodKey, Details)
WHERE [Status] IS NOT NULL AND [PeriodKey] IS NULL`. Дедуп подій синку довідника
(`RegistrySyncJob.DedupJournal`) читає події однієї сутності; єдиний інший індекс
за сутністю відфільтрований `Status IS NULL` і подій не містить, тож до цієї
міграції кожен прогін синку сканував **весь** журнал. Тепер: `Index Seek` по
сутності (тест `RegistrySyncDedupPlanTests`).

**ONLINE чи офлайн.** Міграція сама визначає редакцію сервера
(`SERVERPROPERTY('EngineEdition')`), як U1 (п. 8.2):

| `EngineEdition` | Редакція | Як будується |
|---|---|---|
| 3 | Enterprise, Developer, Evaluation | `WITH (ONLINE = ON)`: запис у журнал не блокується |
| 5, 8 | Azure SQL Database, Managed Instance | `WITH (ONLINE = ON)` |
| 2, 4 | Standard, Express | офлайн: на час побудови запис у `itg.CollectionCoverage` чекає (читання лишається) |

Рекомендація: **ONLINE на Enterprise (замовник, `D-101`), офлайн-гілка лише як
запасна для Standard/Express**. Заміри нижче показують, що ONLINE тривав
порівнянно з офлайн (від ×1 до ×2), а за визначенням ONLINE-побудова не блокує
запис (одночасний запис під час побудови заміром не перевірявся), тож для
Enterprise вікно обслуговування не потрібне. На Standard/Express запис (збір, синк, матеріалізація)
чекатиме тривалість побудови: проводьте оновлення, коли збір не йде.

**Розмір і час.** Заміри 05.10.2026 на локальному SQL Server Developer
(EngineEdition 3), диск `H:`, база зі штучним журналом; INCLUDE `Details`
(довжина 200-1000 символів) робить індекс майже таким самим за обсягом, як
сама таблиця подій:

| Рядків у `itg.CollectionCoverage` | З них у фільтрі індексу | Розмір індексу | Офлайн | ONLINE |
|---|---|---|---|---|
| 1 000 000 | 500 000 | ≈ 428 МБ | 6.5-9.5 с | 9.5-22 с |
| 2 000 000 | 1 000 000 | ≈ 856 МБ | ≈ 42 с | ≈ 36 с |

⚠ Це верхня оцінка: у замовника подій синку (`Status IS NOT NULL`, без періоду)
зазвичай значно менше за рядки покриття (`Status IS NULL`), а в індекс потрапляють
лише вони. Орієнтир: ≈ 0.86 КБ індексу на подію, час лінійний за обсягом
подій. Місце під індекс і журнал транзакцій передбачте з запасом (розмір
індексу плюс журнал побудови, а під ONLINE ще й версії рядків у `tempdb`).

**Що робити.** Звичайне розгортання (п. 8, крок 2); `migration.sql` ідемпотентний,
а повторний прогін нічого не змінює (індекс створюється лише коли його немає).
Перевірити:

```sql
SELECT name, has_filter, filter_definition FROM sys.indexes
WHERE object_id = OBJECT_ID(N'itg.CollectionCoverage') AND name = N'IX_CollectionCoverage_RegistryEvents';
```

Тест: `AN34CollectionCoverageDedupIndexMigrationTests` (база з журналом, Down/Up,
ідемпотентний скрипт двічі, гілка без `ONLINE` наживо з підставленою редакцією 4).
Справжнього Standard чи Express у перевірці не було.

### 8.6. Міграція AN37: помилка 50708 «Передперевірка AN-37 L7-08» (дві опубліковані версії методології на одну дату)

**Кого стосується.** Бази, розгорнуті до міграції `AN37MethodologyEffectiveUnique`
(05.10.2026) і ще не оновлені. Нові бази й бази, де вона вже застосована, цю
перевірку не бачать.

**Що робить міграція.** Фільтрований унікальний індекс `UQ_MV_Effective` на
`calc.MethodologyVersion (MethodologyId, EffectiveFrom) WHERE Status = 1`: у методології
не може бути двох **опублікованих** версій, чинних від однієї дати (ФВ-13.3). Раніше
це перевіряв лише код у пам'яті, і дві паралельні публікації двох чернеток на ту саму
дату обидві проходили; вибір версії на період тоді вирішував номер версії, а не
дата. Застарілі (`Status = 2`) і чернетки (`Status = 0`) індексом не зачеплені.
Перевірка йде **першою командою міграції, до зміни схеми**:

```
Msg 50708 … Передперевірка AN-37 L7-08: оновлення зупинено ДО зміни схеми. …
Груп (методологія, дата): 1; перші: (методологія 1, чинна від 2026-01-01, версії: 11 1.0, 12 1.1)
Схему й дані не змінено, автоматичного виправлення немає. …
```

**Що робити.**

1. `Stop-Service EcrApi`, повний бекап (п. 6.2).
2. Повний перелік (порожньо, коли все чисто):

   ```sql
   SELECT MethodologyId, EffectiveFrom, COUNT(*) AS Cnt, MIN(Id) AS FirstId, MAX(Id) AS LastId
   FROM calc.MethodologyVersion WHERE Status = 1
   GROUP BY MethodologyId, EffectiveFrom HAVING COUNT(*) > 1;
   ```

3. Для кожної групи вирішіть, яка версія чинна від цієї дати. Звичайно це та, якою
   вже рахували періоди (`calc.CalculationResult` за `MethodologyVersionId`) або з
   більшим номером, але автоматичного вибору немає. Решту виведіть з обігу
   (`UPDATE calc.MethodologyVersion SET Status = 2 WHERE Id = …`, застаріла версія
   лишається в історії) або, якщо вона мала діяти від іншої дати, змініть їй
   `EffectiveFrom` на справжню. ⚠ Версії, якими вже рахували подані періоди, не
   переносьте на іншу дату: це змінить, якою версією рахується звітність.
4. Повторіть запит з кроку 2: порожньо.
5. Звичайне розгортання (п. 8, крок 2). `migration.sql` ідемпотентний.

⚠ Після оновлення друга публікація на зайняту дату дає `409 ECR-CALC-0409`
(«версія вже чинна від цієї дати»): і за перевіркою в коді, і за індексом у разі
гонки двох публікацій.

⛔ Не редагуйте міграцію й не знімайте індекс, щоб «пройти» перевірку.

Тести: `AN37MethodologyEffectiveUniqueMigrationTests` (база з дублями, Down/Up, скрипт
двічі), `MethodologyEffectiveDateClashErrorTests` (409 замість 500).

## 9. Відкат

Окремого механізму відкату в коді **немає**. Міграції EF назад не застосовуються,
і `deploy-ecr.ps1` відкату не робить.

1. `Stop-Service EcrApi` (і `Stop-Service EcrWorker`, якщо воркер увімкнено, п. 10).
2. Відновити базу з бекапу, зробленого перед оновленням (п. 6.3).
3. Встановити попередній MSI (з `WORKER_ENABLED=1`, якщо воркер був і
   попередня версія його має; до I2-2 типове там було `0`).
   ⛔ ✎ 2026-09-30: `Environment` служб (рядок підключення, режим Api) ні
   оновлення, ні відкат MSI не зберігає, тож після встановлення попередньої
   MSI **повторіть `deploy-ecr.ps1`** (`-SkipSchema`, з тим самим
   `-ConnectionString` і `-DisableWorker`, якщо попередня версія без
   воркера) — інакше Api стартує без рядка підключення. Якщо Environment
   пишете вручну: попередня версія без воркера — `Executor=InProcess` (і
   `Mode=Quartz`) у `Services\EcrApi\Environment`, інакше перерахунок
   лишиться без виконавця (п. 10.1).
4. `Start-Service EcrApi` і перевірити health.

Дані, введені після оновлення, при такому відкаті втрачаються.

## 10. Воркер перерахунку (служба `EcrWorker`)

Друга служба — наглядач пулу процесів перерахунку (ФВ-9.8, `D-206`):
`Ecr.Worker.exe --supervisor` у теці застосунку тримає дочірні процеси під
Windows Job Object з межами пам'яті. ✎ 2026-09-30 (I2-2): **типово
встановлюється** — і MSI (`WORKER_ENABLED` типово `1`), і `deploy-ecr.ps1`
(крім `-DisableWorker` і SQL Server Express), `docs/build/11-install-guide.md`
§2.6. Той самий обліковий запис, що `EcrApi`; рядок підключення — у
`HKLM:\SYSTEM\CurrentControlSet\Services\EcrWorker\Environment`.

⛔ **Зв'язка з `EcrApi`.** Разом зі службою `deploy-ecr.ps1` пише в
`Services\EcrApi\Environment` `ECR_Jobs__Queue__Mode=Database` і
`ECR_Jobs__Recalculation__Executor=Worker`. У цьому режимі Api лейн
перерахунку **не бере**: зупинений чи знятий воркер = перерахунок стоїть у
черзі. Тому вимкнення воркера (10.1) — завжди разом із перемиканням Api на
`InProcess`. Стан видно на `/health/ready`, перевірка `worker`: `Degraded` з
поясненням, якщо Api на `Worker`, а служби немає, вона `Disabled` чи задачі
чекають понад 5 хв без жодної виконуваної. Поточний режим — поля `queueMode`
і `executor` цієї перевірки.

```powershell
Get-Service EcrWorker
Get-CimInstance Win32_Service -Filter "Name='EcrWorker'" | Select-Object State, StartMode, StartName, PathName
Get-CimInstance Win32_Process -Filter "Name='Ecr.Worker.exe'" | Select-Object ProcessId, ParentProcessId, CommandLine
```

Норма: один процес `--supervisor` і `Jobs:Workers:Count` дочірніх `--child`.

**Налаштування пулу** — змінні `ECR_Jobs__Workers__Count`, `__MemoryLimitMb`,
`__JobMemoryLimitMb`, `__MaxDuration` у `Environment` служби (перекривають
`worker.settings.json` поруч з exe, який оновлення перезаписує). Недійсне
значення — служба не стартує (код виходу 3, перелік недійсних ключів — у
stderr і журналі); після зміни — `Restart-Service EcrWorker`. Типові значення
(`worker.settings.json`): `Count` 10, `MemoryLimitMb` 2048, `JobMemoryLimitMb` 22528,
`MaxDuration` 00:30:00.

⚠ Задачі **за розкладом** (усі з п. 4, зокрема нічний перерахунок і збір) виконує Quartz
усередині `EcrApi` навіть у режимі `Queue:Mode=Database` + `Executor=Worker`: воркер бере з
черги лише разово поставлені задачі лейна перерахунку. Тому зупинений воркер не зупиняє
розклади, а нічний перерахунок навантажує процес Api і межі Job Object на нього не діють
(висновок із коду, вимірів немає). Тик розкладу бере міжінстансний SQL-лок
`Ecr.Job.<ідентифікатор>`: якщо той самий тик уже виконує інший вузол, цей його пропускає.
⛔ ✎ 2026-09-30: ці змінні **не переживають оновлення MSI** — воно стирає
`Environment` служби (п. 10.3), а `deploy-ecr.ps1` їх не пише. Після кожного
оновлення виставте їх знову й зробіть `Restart-Service EcrWorker`.

### 10.1. Вимкнути воркер

Найпростіше — повторити `deploy-ecr.ps1 … -DisableWorker -SkipSchema` з тим
самим `-ConnectionString`: MSI з `WORKER_ENABLED=0` і Api на `InProcess` —
разом.

Швидко, без MSI, — служба лишається зареєстрованою, але не стартує, зокрема
після перезавантаження; ⛔ і ОДРАЗУ перемкнути Api на перерахунок у власному
процесі (Executor — у `Environment` служби EcrApi, той самий прийом злиття,
що в `docs/build/11-install-guide.md` §9):

```powershell
Stop-Service EcrWorker
Set-Service EcrWorker -StartupType Disabled

$key = 'HKLM:\SYSTEM\CurrentControlSet\Services\EcrApi'
$vars = @((Get-ItemProperty $key -Name Environment).Environment |
    Where-Object { $_ -notlike 'ECR_Jobs__Recalculation__Executor=*' -and $_ -notlike 'ECR_Jobs__Queue__Mode=*' })
Set-ItemProperty $key -Name Environment -Type MultiString -Value ([string[]] ($vars + 'ECR_Jobs__Recalculation__Executor=InProcess' + 'ECR_Jobs__Queue__Mode=Quartz'))
Restart-Service EcrApi
```

Дочірні процеси закриваються разом із наглядачем (Job Object з
`KILL_ON_JOB_CLOSE`), окремо їх зупиняти не треба — перевірка: запит
`Win32_Process` вище порожній.

Прибрати службу зовсім — тим самим MSI, що встановлено (компоненти воркера
транзитивні, `docs/build/10-installer.md` §1.6):

```powershell
msiexec /i Ecr.msi /qn /l*v worker-off.log REINSTALL=ALL REINSTALLMODE=vomus WORKER_ENABLED=0 SERVICE_ACCOUNT=DOMAIN\ecr-svc$
```

⚠ `SERVICE_ACCOUNT` — той самий, що при установці: властивості MSI не
запам'ятовуються, і REINSTALL без нього перереєструє службу під `LocalSystem`.

### 10.2. Увімкнути назад

Якщо вимикали через `Set-Service` — служба назад, потім Api на пул (зворотне
до 10.1: `ECR_Jobs__Queue__Mode=Database`, `ECR_Jobs__Recalculation__Executor=Worker`
і `Restart-Service EcrApi`):

```powershell
Set-Service EcrWorker -StartupType Automatic
Start-Service EcrWorker
```

Якщо службу прибирали (чи ніколи не ставили) — найпростіше повторити
`deploy-ecr.ps1 … -SkipSchema` (без `-DisableWorker`) з тим самим
`-ConnectionString`: MSI з `WORKER_ENABLED=1`, рядок підключення в
`Environment`, режим Api, перезапуск і перевірка — разом. Вручну — той самий
MSI, що в 10.1, з `WORKER_ENABLED=1`, потім рядок підключення
(`docs/build/11-install-guide.md` §9, служба `EcrWorker`),
`Restart-Service EcrWorker` і режим Api, як вище.

⚠ Той самий MSI-файл, що вже встановлено, без `REINSTALL=ALL
REINSTALLMODE=vomus` нічого не перемикає: це режим обслуговування, умови
компонентів не переобчислюються.

### 10.3. Відкат воркера

Воркер не змінює схему бази й не має власних даних, тож його відкат — це
вимкнення (10.1): `EcrApi` працює й без нього — **за `Executor=InProcess`**.

1. Блок «швидко, без MSI» з 10.1 — службу вимкнути й Api перемкнути
   на `InProcess` разом, негайно.
2. Причина: журнал подій Application; ручний прогін
   `& "C:\Program Files\ECR\Api\Ecr.Worker.exe" --supervisor` від
   адміністратора — вивід у консоль, Ctrl+C зупиняє разом із дочірніми.
3. Коли причину усунуто — 10.2. Прибрати службу зовсім —
   `deploy-ecr.ps1 -DisableWorker` (10.1), і наступні оновлення — теж
   з `-DisableWorker`.

⚠ ✎ I2-2: оновлення продукту **без** `-DisableWorker` / `WORKER_ENABLED=0`
ставить службу воркера назад (типове — так), а `deploy-ecr.ps1` ще й
перемикає Api на `Worker`. Вимкнений воркер — прапорець на кожному
оновленні. Прямий `msiexec` без властивості службу поставить, але режим Api
не виставить (це робить лише `deploy-ecr.ps1`).

⛔ ✎ 2026-09-30: **`Environment` служби `EcrApi` не переживає оновлення MSI.**
`MajorUpgrade` перевстановлює службу, і змінні оточення (режим Api
`ECR_Jobs__Queue__Mode` / `ECR_Jobs__Recalculation__Executor`, рядок
підключення `ECR_ConnectionStrings__Ecr`, `ECR_Database__EditionMode`,
відбиток DataProtection, ваші власні) зникають. Перевірено джобом
`msi-install (windows)` (крок D3, run 36676739674): тестовий маркер у
`Environment` після оновлення відсутній. Для `EcrWorker` окремого виміру
немає, але механізм той самий — вважайте `Environment` втраченим і там.
Наслідок після голого `msiexec`: Api без рядка підключення й на типовому
Quartz/`InProcess`, а служба `EcrWorker` (MSI її зберігає) стоїть без роботи —
безпечно, але режим D-216 (Api на `Worker`) не діє. Тому **після кожного
оновлення MSI повторюйте** `deploy-ecr.ps1` (10.2, той самий
`-ConnectionString`, без `-DisableWorker`, якщо воркер потрібен):

```powershell
powershell -ExecutionPolicy Bypass -File tools\deploy-ecr.ps1 `
  -SqlInstance <сервер> -Database <база> -MsiPath <шлях до .msi> -SkipSchema `
  -ConnectionString $cs -DataProtectionThumbprint <відбиток> -HttpsThumbprint <відбиток HTTPS>
```

(`-SkipSchema` — якщо схему вже застосовано; повний виклик — п. 8.) Те саме
для відкату (п. 9, крок 3). Змінні, яких скрипт не пише
(`ECR_Jobs__Workers__*`), виставте знову вручну. ✎ 2026-09-30: параметр транспорту
(`-HttpsThumbprint`, або `-BehindHttpsProxy`, або на стенді `-AllowHttp`) обов'язковий і тут — без
нього скрипт зупиняється (п. 11).

## 11. HTTPS і сертифікат (✎ 2026-09-30, `D14-08`)

Повний опис — `docs/build/11-install-guide.md` §2.7; тут — те, що потрібно в експлуатації.
Покроково для одного сертифіката замовника з SAN (право на ключ, перенаправлення порту, Data Protection,
перевірка після встановлення, заміна, відомі розбіжності) — [`https-certificate.md`](https-certificate.md).

**Три транспорти, рівно один** (`deploy-ecr.ps1`; на кожному оновленні — бо `Environment` стирає
оновлення MSI, п. 10.3): `-HttpsThumbprint '<відбиток>'` (HTTPS; порт `-AppPort`, для `https://сервер/`
— 443; необов'язково `-HttpRedirectPort 80`), `-BehindHttpsProxy` (TLS на проксі перед застосунком;
`Auth:RequireHttps` лишається `true`), `-AllowHttp` (лише стенд: `Auth:RequireHttps=false`).
Жодного — скрипт зупиняється на кроці 1. Причина вибору: cookie сеансу `Secure`, а по HTTP його
браузер не відсилає — вхід з інших машин не працює (симптом «вдався, далі 401»).

**Змінні служби `EcrApi`** (пише `deploy-ecr.ps1`): `ASPNETCORE_URLS`,
`ECR_Transport__Https__CertificateThumbprint`, `ECR_Transport__Https__Port` (лише з перенаправленням),
`ECR_Auth__RequireHttps` (завжди явно), `ECR_Auth__DataProtection__CertificateThumbprint` і, при заміні
сертифіката DP, `ECR_Auth__DataProtection__PreviousCertificateThumbprints`.

**Що бачить оператор:**

- Старт пише в журнал подій (джерело `ECR`) і файловий журнал: Warning «HTTP без HTTPS — cookie сеансу НЕ Secure»
  (Production з `RequireHttps=false`); Information «HTTPS, сертифікат завантажено, діє до …»; Warning
  «сертифікат HTTPS спливає через N дн.»; Error «сертифікат HTTPS ПРОСТРОЧЕНИЙ». Відбиток у журнал не пишеться.
- `/health/ready` і `/admin/health`, картка «Transport (HTTPS)» — `transport`: `Degraded` (не 503),
  коли Production працює з `RequireHttps=false`, або сертифікат спливає менш ніж за 30 днів чи прострочений.
  Дані: `requireHttps`, `httpsCertificate`, `certificateNotAfter`, `certificateDaysLeft`.
- Заданий відбиток без сертифіката чи сертифікат без закритого ключа — **служба не стартує** (не відкат до
  HTTP) із назвою ключа `Transport:Https:CertificateThumbprint` і причиною. Прострочений сертифікат старт
  **не** зупиняє (служба, що не піднялась вночі через строк, гірша за сторінку з попередженням) — це
  видно на `transport`.

**Заміна сертифіката** (продовження строку): встановити новий у `LocalMachine\My`, дати обліковому запису служби
право читання закритого ключа, повторити `deploy-ecr.ps1` з новим `-HttpsThumbprint` і решту параметрів як
на оновленні — служба перезапуститься; крок 7 перевірить, що Kestrel віддає саме новий сертифікат. Швидка
заміна лише відбитка без MSI (як у `docs/build/11-install-guide.md` §9): змінити
`ECR_Transport__Https__CertificateThumbprint` у `Environment` служби й `Restart-Service EcrApi`.
⚠ Якщо цей сертифікат — і сертифікат Data Protection, швидкий шлях змінює **три** змінні
(`https-certificate.md` §9.1), а старий сертифікат лишається в сховищі.

⚠ **HSTS:** запити по HTTPS отримують `Strict-Transport-Security: max-age=31536000`; після першого входу браузер
не відкриє це ім'я по `http://` до кінця строку. За проксі застосунок HSTS не віддає (`X-Forwarded-*` не читає):
ставити на проксі.

**Один сертифікат для HTTPS і Data Protection** (рішення людини 2026-10-01, `D-267`): замовник видає
один сертифікат із SAN. Практично — один відбиток у двох параметрах `deploy-ecr.ps1`
(`-HttpsThumbprint` і `-DataProtectionThumbprint`), сертифікат із закритим ключем у `LocalMachine\My`,
право читання ключа для облікового запису служби. Ім'я, за яким відкривають застосунок, має входити в SAN.
TLS завершує **застосунок (Kestrel)** за замовчуванням; з `-BehindHttpsProxy` сертифікат HTTPS стоїть на
проксі, а на вузлі застосунку той самий сертифікат усе одно потрібен для Data Protection.

⚠ **Data Protection і сертифікат — що відомо з коду (`AuthenticationSetup.cs`):** ключі кільця в
`sec.DataProtectionKey` захищаються лише `ProtectKeysWithCertificate` за відбитком; у Production без відбитка
служба не стартує. `UnprotectKeysWithAnyCertificate` налаштовано з D-267 (коміт `ed0b2393`): відбитки попередніх сертифікатів —
`Auth:DataProtection:PreviousCertificateThumbprints` (`deploy-ecr.ps1 -PreviousDataProtectionCertificateThumbprints`; MSI-оновлення стирає
змінну — передавати параметр на кожному розгортанні, доки старі ключі в таблиці; прибирається Previous тим, що його перестають передавати); при заміні сертифіката старий залишати в `LocalMachine\My`, доки з кільця не видалено ключі, захищені ним (`https-certificate.md` §9.3; запит п. 6.4 для цього не годиться — він знаходить лише відкриті ключі). Поведінку при
заміні перевірено на бібліотеці (`https-certificate.md` §10.1), ризик закрито кодом; `/health/db` попереджає (Degraded), якщо відбиток пропущено.
Продовження строку одного сертифіката зачіпає одночасно HTTPS і Data Protection — виконувати як одну
операцію (`https-certificate.md` §9.2–9.3, бекап PFX — п. 6.1).

⚠ **Невідомо про майданчик замовника** (потрібне уточнення): чи є зворотний проксі/балансувальник перед
застосунком і хто завершує TLS (замовник не називав; діє Kestrel за замовчуванням); який ЦС видає сертифікат;
кількість вузлів. Рішення людини 2026-09-29: «HTTPS — сертифікат замовника»; 2026-10-01 — один сертифікат
із SAN (`D-267`).

## 12. Вʼюхи для SSRS не створено (картка `reportviews`, ✎ 2026-09-30)

Публікація версії шаблону НЕ залежить від вʼюх `rpt.v_*`: якщо `rpt.usp_GenerateTemplateViews` відмовила
(`50422` — таблиця має понад 250 колонок; `50409` — два шаблони/аркуші/таблиці дають одне ім'я вʼюхи), версія
лишається опублікованою, у журналі — Warning із шаблоном, версією, аркушем, таблицею й кодом, а
`/health/ready` показує картку `reportviews` жовтою (`Degraded`, не 503) з тією самою причиною.

Виправлення: зменшити таблицю (нова версія шаблону) або змінити коди шаблону/аркуша/таблиці, щоб вони
відрізнялися не лише розділовими знаками чи регістром. Повтор генерації — **перезапуск застосунку** (старт
перегенеровує вʼюхи по всіх версіях) або вручну від імені DBA:

```sql
EXEC rpt.usp_GenerateTemplateViews;                        -- усі опубліковані версії
EXEC rpt.usp_GenerateTemplateViews @TemplateVersionId = 7; -- одна
```

⚠ Картка живе в памʼяті процесу: ручний `EXEC` вʼюхи створює, але картку очистить лише перезапуск.

## 13. База часових поясів ОС (картка `tzdata`, ✎ 2026-09-30, F-4, `D-217`)

Межі періодів проєкту рахуються з бази часових поясів **операційної системи**
(`TimeZoneInfo.FindSystemTimeZoneById` за ідентифікатором IANA проєкту; Windows — реєстр і
накопичувальні оновлення, Linux — `tzdata`). Фіксованого зсуву в продукті немає. Майданчик —
`Asia/Atyrau` (UTC+5, `D-217`). З 2024-03-01 увесь Казахстан на UTC+5; машина, що про це не знає,
вважає `Asia/Almaty` `+06:00`, і періоди проєктів на такому поясі закриваються на годину пізніше.

**Що перевіряється** (`KazakhstanTimeZoneReference`): зсув `Asia/Atyrau`, `Asia/Aqtau`, `Asia/Almaty` на
2024-06-01 має бути `+05:00`; еталон — константа, а не та сама база. Реально виказує застарілу базу
`Asia/Almaty` — для Атирау й Актау зсув не змінювався.

**Що бачить оператор:**

- Старт пише Warning «База часових поясів ОС застаріла: <пояси>…» (або «Не вдалося перевірити базу
  часових поясів ОС») у журнал. **Старт не зупиняється.**
- `/admin/health` — картка `tzdata` (`Healthy` / `Degraded`, не 503, з переліком поясів у тексті);
  у JSON `/health/ready` — поля `staleZones` (напр. `Asia/Almaty=+06:00`; `?` — пояса в базі немає),
  `expectedOffset`, `checkedZones`.

**Виправлення:** встановити накопичувальне оновлення Windows із часовими поясами (Linux — оновити пакет
`tzdata`), перезапустити `EcrApi` (і `EcrWorker`, п. 10) і переконатися, що картка зелена. Збережені дані
перевірка не змінює; ⚠ чи треба щось перераховувати для періодів, закритих на машині із застарілою
базою, — кодом не визначено (перевірка лише попереджає).

Швидка ручна перевірка на сервері:

```powershell
[System.TimeZoneInfo]::FindSystemTimeZoneById('Asia/Almaty').GetUtcOffset([datetime]'2024-06-01T00:00:00Z')   # очікується 05:00:00
```

⚠ Той самий пояс проєкту — IANA; у полі проєкту Windows-ідентифікатор (`West Asia Standard Time`)
відхиляється `ECR-CFG-4221`. Windows-імена лишаються лише для `AT TIME ZONE` у запитах SQL-джерела (п. 2.2).


## 14. Роль бази «бачить усе» `ecr_viewer` (✎ 2026-10-01, AN-10, `D-265`, НФ-8.5)

Роль `ecr_viewer` створює `Sql/05-rpt-views.sql` на КОЖНОМУ розгортанні й оновленні (ідемпотентно). Вона **порожня за замовчуванням**.

| Роль | Для кого | Права |
|---|---|---|
| `rpt_reader` | обліковий запис SSRS | лише `SELECT` на схему `rpt` |
| `ecr_viewer` | довірений DBA / діагностика | членство в `db_datareader` (`SELECT` на ВСІ схеми бази, зокрема майбутні) + `VIEW DEFINITION`; жодних `ALTER`/`CREATE`/`INSERT`/`UPDATE`/`DELETE`/`EXECUTE` |

⛔ DENY на секрети: `ecr_viewer` НЕ читає `sec.DataProtectionKey` і стовпці `PasswordHash`, `SecurityStamp` у `sec.User` (DENY SELECT у `05-rpt-views.sql`; перекриває `db_datareader`). Скрипти відхиляють лише імена, що містять `EcrApi`/`EcrWorker` або починаються з `NT SERVICE\`. ⚠ Обліковий запис служби на кшталт `DOMAIN\ecr-svc$` ця перевірка **не** розпізнає — не передавайте його в `-ViewerAccount`.

⛔ Членом `ecr_viewer` НЕ робити обліковий запис служби EcrApi/EcrWorker: ця роль лише читає, а службі потрібні власні права запису. Роль бачить усе, зокрема `sys_ecr` і `aud` — членство лише довіреним особам.

Видати членство при розгортанні (необовʼязковий параметр, без нього роль лишається порожньою):

```powershell
powershell -File tools\deploy-ecr.ps1 ... -ViewerAccount 'DOMAIN\dba-ecr'
powershell -File tools\setup-dev-db.ps1 -Server localhost -Database EcrDev -ViewerAccount 'DOMAIN\dba-ecr'
```

Скрипт створює користувача бази `FOR LOGIN`, якщо логін на сервері є (інакше зупиняється з повідомленням), і додає його в роль лише якщо він ще не член; повторний запуск нічого не міняє. Паролів скрипт не друкує. `-ViewerAccount` приймає будь-який логін сервера (Windows або SQL), але логін має існувати до запуску (`CREATE LOGIN` — справа DBA). Вручну: `ALTER ROLE ecr_viewer ADD MEMBER [DOMAIN\dba-ecr];`; зняти членство — `ALTER ROLE ecr_viewer DROP MEMBER [DOMAIN\dba-ecr];`.

З `-SkipSchema` скрипт `05-rpt-views.sql` не виконується: роль має вже існувати (її накотив DBA), інакше скрипт зупиниться з `Role ecr_viewer is missing`.

`rpt_reader` параметром не наповнюється — лише вручну: `ALTER ROLE rpt_reader ADD MEMBER [DOMAIN\svc-ssrs];`.

⚠ Через DENY на стовпці члени `ecr_viewer` отримають відмову на `SELECT * FROM sec.[User]` — перелічуйте стовпці явно (без `PasswordHash`, `SecurityStamp`; висновок із семантики column-DENY SQL Server, тестом не перевірено). Зашифровані секрети каналів сповіщень і пароль SMTP роль читає лише як шифротекст: ключі Data Protection їй закриті.

**Права облікового запису служби** в базі скрипти **не** видають — це робить DBA (`deploy-ecr.ps1` виконує DDL під обліковим записом DBA, не під `-ServiceAccount`; у службі DDL немає, `D-66`). За кодом потрібні: `SELECT/INSERT/UPDATE/DELETE` на таблицях (сід виконується на кожному старті), `UPDATE` на послідовностях, `EXECUTE` на TVP-типах `doc.CellValueTvp`, `aud.CellChangeTvp` і на `arc.usp_ArchiveYear`, `arc.usp_EnsureAuditPartitions`, `arc.usp_ArchiveAudit` (п. 7, 7.3–7.4; усі три — `WITH EXECUTE AS OWNER`, DDL-прав служба не потребує). Чи досить `db_datareader` + `db_datawriter` + `EXECUTE`, не перевірено (відкрите питання DBA, `DB-1`).

Перевірка:

```sql
SELECT m.name FROM sys.database_role_members rm
JOIN sys.database_principals r ON r.principal_id = rm.role_principal_id AND r.name = N'ecr_viewer'
JOIN sys.database_principals m ON m.principal_id = rm.member_principal_id;
-- від імені члена: SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER');  -- очікується 0
```

Тести: `ViewerRoleTests` (читання кожної схеми, відмова на `CREATE TABLE`/`DELETE`), `DeployScriptsRerunTests` (ідемпотентність повторного прогону).

## 15. PI не налаштований або недоступний (✎ 2026-10-02, підказка `sources.notConfiguredHint`)

Тут — що бачить адміністратор, коли джерело PI не підключене чи не відповідає, і що перевірити. Адреси, облікові записи й спосіб автентифікації PI Web API — рішення замовника (див. `admin-guide.md`, п. 5); цей розділ їх не припускає.

**Симптоми**

| Де | Що видно |
|---|---|
| `/admin/sources` | порожній список з'єднань і підказка «що робити» (`sources.notConfiguredHint`); джерел немає — збору немає |
| екрани збору, покриття | порожні таблиці: збирати нічого або остання спроба впала |
| `/health/ready`, перевірка `sources` | Unhealthy, якщо останній запуск активного джерела впав; без активних джерел — Healthy; Degraded — джерело ще не запускалось, є прогалина покриття або Windows-автентифікація (Negotiate) при порожньому `PiWebApi:AllowedHosts` (`health.sources.negotiateNoAllowlist`) |
| журнал фонових задач (`/admin/jobs`) | `jobs.collectionAuthRefused` — джерело відмовило в автентифікації; повтор з тими самими даними нічого не змінить, потрібне втручання адміністратора |
| помилки `err.ECR-INT-0503.*` | джерело недоступне: `piWebApiTimeout`, `piWebApiErrorStatus`, `connectFailed`, `sourceMissing` (джерело видалене чи вимкнене), `catalogUnavailable`, `probeUnavailable` та інші |
| пробa з'єднання | `integration.test.failed.auth` / `.unreachable` / `.tls` / `.timeout` / `.other` — категорія відмови |

**Що перевірити (у такому порядку)**

1. Чи є з'єднання на `/admin/sources` і чи воно **активне** (вимкнене джерело нічого не збирає).
2. Адреса PI Web API в полі `Endpoint` джерела: схема, хост, порт, шлях; з сервера `EcrApi` адреса відкривається (`Invoke-WebRequest` тим самим обліковим записом служби).
3. Секрет: `Secrets:<ім'я>` задано змінною оточення служби `ECR_Secrets__<ім'я>` і служба перезапущена (admin-guide, п. 5). Джерело без секрету вважається Windows-автентифікацією (Negotiate).
4. Автентифікація: відмова `auth` / `jobs.collectionAuthRefused` — обліковий запис чи секрет не прийнято на стороні PI; виправляє власник PI.
5. Захист від SSRF: адреса джерела проходить `DataSourceEndpointPolicy` (заборонені небезпечні адреси); для Negotiate хост має бути в `PiWebApi:AllowedHosts` у конфігурації `EcrApi` (п. 2.1), після зміни — перезапуск `EcrApi`.
6. Мережа, брандмауер, TLS-сертифікат сервера PI (довіра до видавця на сервері ECR), тайм-аут: категорії `unreachable` / `tls` / `timeout`; деталі — у лозі сервера (`integration.test.failed.other`).

**Що робити, поки PI відсутній**

Без PI дані вводяться вручну (у клітинки аркушів). Збір почнеться, коли з'єднання додано, воно активне й проба проходить. Не вимикайте перевірки (`PiWebApi:AllowedHosts`, політику адрес) заради швидкого підключення — це захист від SSRF.
