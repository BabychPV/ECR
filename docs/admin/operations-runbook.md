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

1. Перевірка схеми (`Schema:StartupMode`).
2. Сід `09-seed.sql`. Його виконує **сам застосунок**, а не скрипти розгортання.
3. Створення запису `bootstrap`.
4. Реєстрація розкладів.

Якщо з цих кроків падає будь-який, служба не стартує. Причину шукайте в лозі
(п. 3.2) і в журналі подій Windows.

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
| `Schema:StartupMode` | `Validate` | `Validate` — не стартувати, якщо є незастосовані міграції EF. `Migrate` — застосувати їх на старті |
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
| `Auth:DataProtection:AllowUnprotectedKeys` | `false` | лише для одноразових стендів (`smoke.ps1`, `e2e-stand.ps1`, `setup-dev-db.ps1`): дозволяє старт у Production без сертифіката. На майданчику не вмикати: старт пише Critical у журнал подій (джерело `ECR`), `db` — Degraded з причиною. `deploy-ecr.ps1` його не ставить ніколи |
| `Security:RateLimit:LoginPermitPerMinute` | 60 | спроб входу за хвилину |
| `Security:RateLimit:TrustForwardedFor` | `false` | брати IP із `X-Forwarded-For`. Вмикати лише за довіреним проксі |
| `Security:RateLimit:SearchPermit` / `SearchWindowSeconds` | 30 / 10 | обмеження пошуку |
| `Security:RateLimit:CspReportPermitPerMinute` | 120 | звітів про порушення CSP за хвилину з однієї адреси (`POST /api/v1/csp-report`); понад межу — `429` без тіла |
| `Security:Csp:ReportOnly` | `true` | віддавати сувору політику заголовком `Content-Security-Policy-Report-Only` (лише звіти, сторінки не блокуються). Порушення — рядки журналу `CSP violation: …` і лічильник `ecr.csp.violations` (тег `directive`) |
| `Security:Csp:ReportUri` | `/api/v1/csp-report` | куди браузер шле звіти (`report-uri`, а на HTTPS ще й `report-to`). Порожньо — без звітування. Без `;`, пробілів і ком |
| `Security:Csp:Enforce` | `false` | ⛔ лише задел: `true` робить повну політику примусовою (звітний заголовок зникає). Не вмикати, доки e2e-набір не пройшов під нею, а `ecr.csp.violations` не порожній |
| `Jobs:NightlyRecalculation:Enabled` | `false` | нічний перерахунок о 03:30. Вмикається лише рядком `true` |
| `Audit:ExportMaxRows` | 100000 | межа експорту аудиту CSV |
| `Campaign:AtRiskDays` | 3 | за скільки днів до терміну проєкт вважається «під загрозою» |
| `Notifications:WebhookAllowedHostSuffixes` | `.webhook.office.com;.logic.azure.com;.powerplatform.com` | дозволені хости вебхуків (`;` або `,`) |
| `Smtp:Host`, `Smtp:From` | немає | транспорт пошти. Без них пошта не йде |
| `Smtp:Port` | (587) | |
| `Smtp:UseStartTls` | (`true`) | |
| `Smtp:User`, `Smtp:SecretName` | немає | автентифікація. Пароль — секрет `Secrets:<SecretName>` |
| `Secrets:<ім'я>` | немає | секрети джерел і каналів. **Лише змінні оточення** |
| `PiSqlClient:CatalogQuery` / `TemplateQuery` / `ValueQuery` | немає (вбудовані) | перевизначення запитів адаптера PI SQL Client. `InterpolatedQuery`, `SummaryQuery`, `CurrentValueQuery`, `ElementListQuery`, `EventQuery`, `EventTemplateQuery` — без вбудованого тексту |
| `PiSqlClient:<код джерела>:<Query>` | немає | запит для ОДНОГО джерела (інша база AF на тому ж сервері), перекриває спільний `PiSqlClient:<Query>` — для будь-якого із запитів вище; напр. `PiSqlClient:AIR:ElementListQuery`. ⚠ Усі `PiSqlClient:*` адаптер читає через канал секретів, тобто фізично це `Secrets:PiSqlClient:…` — змінна `ECR_Secrets__PiSqlClient__AIR__ElementListQuery` |
| `Sql:CatalogQuery` / `Sql:ValueQuery` | немає (вбудовані) | те саме для SQL-джерела |
| `Integration:AfTimeZoneId` | (порожньо = UTC) | ✎ 2026-09-30, D-212 PR-7: пояс, у якому AF віддає дати дії записів довідника без поясу (Windows або IANA, напр. `Asia/Atyrau`). Діє лише для синку темпорального довідника з атрибутами дат у політиці. Невідомий пояс зупиняє старт з ім'ям ключа. ⚠ Пояс серверів AF замовника — відкрите питання PI-адміністратору |
| `Bootstrap:Password` | немає | запасний пароль `bootstrap`. Основний шлях — файл `bootstrap.secret` |
| `Telemetry:Enabled` | `false` | експорт метрик по OTLP (п. 3.4). Вимкнено — не реєструється нічого з OpenTelemetry, навантаження нуль. Вмикається лише рядком `true` |
| `Telemetry:OtlpEndpoint` | порожньо | адреса OTLP-колектора, напр. `http://collector:4317` (gRPC) чи `http://collector:4318` (HTTP). **Обов'язкова**, коли `Telemetry:Enabled=true`: без неї або з недійсною адресою служба не стартує. Задана при вимкненому експорті — ігнорується, старт пише попередження |
| `Telemetry:OtlpProtocol` | `Grpc` | `Grpc` (порт колектора 4317) або `HttpProtobuf` (4318). Інше значення зупиняє старт |
| `Telemetry:ExportIntervalSeconds` | 60 | як часто відсилати метрики, с. Не менше 5 |
| `Telemetry:ServiceName` | `ecr-api` | `service.name` у ресурсі OTLP — під цим іменем служба видна в колекторі |
| `Logging:LogLevel:*` | `Information`, `Microsoft.AspNetCore` = `Warning` | рівні логування |
| `Logging:File:Directory` | `%ProgramData%\ECR\logs` | тека логів. Порожньо — без файлового логу |
| `Logging:File:RetainedFiles` | 30 | скільки файлів зберігати |
| `Logging:File:FileSizeLimitMb` | 100 | розмір файлу, після якого починається новий |
| `Logging:File:Json` | `true` | поруч писати `ecr-yyyyMMdd.json` — рядок JSON на запис (п. 3.2) |
| `AllowedHosts` | `*` | |

Числові, булеві ключі й ключі з переліком значень (`Schema:StartupMode`,
`Database:EditionMode`) перевіряються на старті: недійсне значення зупиняє
службу з назвою ключа (п. 5), а не мовчки замінюється дефолтом. Порожнє
значення — «не задано», тобто дефолт.

⚠ **потрібне рішення замовника:** SMTP, OTLP-колектор (і чи вмикати експорт метрик), сертифікат для Data
Protection і HTTPS, адреси джерел PI. Дефолти коду — «вимкнено» або порожньо.

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
| `/health/db` | після входу | редакція, RCSI, файлові групи, запас партицій |

HTTP-код: `Healthy` і `Degraded` дають **200**, `Unhealthy` — **503**. Моніторинг
має читати поле `status` у JSON, а не лише код відповіді.

| Перевірка | Degraded | Unhealthy |
|---|---|---|
| `db` | попереду менше 2 партицій | RCSI вимкнено; немає файлової групи `DATA_HOT`, `DATA_ARCHIVE`, `AUDIT` або `INDEXES`; БД недоступна |
| `jobs` | у планувальника немає тригерів | планувальник не зареєстрований, зупинений або кидає помилку |
| `sources` | джерело ще не запускалось або є прогалина покриття | останній запуск будь-якого активного джерела впав. Якщо активних джерел немає — Healthy |
| `worker` | Api на `Executor=Worker`, а служби `EcrWorker` немає, вона `Disabled` або задачі чекають понад 5 хв без жодної виконуваної (п. 10) | — |
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
(`#` кодується як `%23`) або з таблиці `itg.JobProgress`. Далі шукайте
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

## 4. Розклади

Планувальник Quartz, розклади реєструє `RecurringScheduleService`. Час —
локальний час сервера.

| Коли | Задачі |
|---|---|
| щоночі 02:15 | `PartitionCheckJob`, `ConsistencyCheckJob`, `OrphanScanJob`, `ReportRetentionJob`, `ReportSnapshotFormatJob` |
| щогодини, хх:05 | `PeriodStateJob` (також один раз на старті), `NotificationJob` |
| щоночі 03:30 | нічний перерахунок, лише якщо `Jobs:NightlyRecalculation:Enabled=true` |
| за cron джерела | збір даних (`ext.CollectionSchedule`) |

SQL Server Agent (`14-agent-jobs.sql`) ставиться лише з `deploy-ecr.ps1
-FirstDeployment` і не працює на Express (THROW 50040):

| Задача Agent | Коли | Що робить |
|---|---|---|
| `ECR: Partitions ahead` | 1-го числа, 02:40 | `arc.usp_EnsurePartitions @MonthsAhead = 6` |
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
| `db` Degraded: менше 2 партицій попереду | не працює Agent-задача (Express) | `EXEC arc.usp_EnsurePartitions @MonthsAhead = 6;` або скрипт `GET /api/v1/health/partitions/script` |
| `db` Unhealthy: RCSI | базу відновили або створили без `06-rcsi.sql` | виконати `06-rcsi.sql`. Перезапуск не потрібен: перевірка читає RCSI щоразу, а не з проби старту |
| служба не стартує: «Недійсна конфігурація — служба не стартує» | значення ключа не того типу чи поза межами (`"60s"` замість `60`, друкарська помилка в `Database:EditionMode`) | виправити названий ключ у `appsettings.Production.json` або в `ECR_…` змінній служби. Той самий текст — у журналі подій (джерело `ECR`) і в лозі |
| `sources` Unhealthy | PI/SQL-джерело недоступне або змінився секрет | стан на `/admin/sources`, помилка в `GET /api/v1/jobs/{id}`, секрет `ECR_Secrets__<ім'я>` |
| `jobs` Degraded, `schedulerStopped: true` (✎ 2026-09-28, U7: було Unhealthy; `/health/ready` більше не 503 — тло не виводить інстанс із ротації) | планувальник зупинився: API працює, фонові задачі — ні | лог за `Quartz`, перезапуск служби |
| `jobs` Degraded, `staleJobs` / `cleanupStalled: true` | задачі без биття серця (процес зник); `cleanupStalled` — прибирання їх не закриває | `/admin/jobs`; якщо `cleanupStalled` тримається — лог за `RecurringScheduleService` (прохід прибирання раз на хвилину) |
| розгортання: `01-filegroups.sql`, `Msg 5149 … error 112` | немає місця на диску даних | звільнити місце. Файлові групи займають ~14 ГБ на повній редакції (п. 6.1) |
| збірка чи оновлення: `The file is locked by: "Ecr.Api (<pid>)"` | DLL тримає запущена служба | `Stop-Service EcrApi`, потім оновлення |
| оновлення: `Msg 50148 … Передперевірка D148` на `migration.sql` | у базі до 2026-09-20 є значення з модулем ≥ 1e12 | п. 8.1 |
| оновлення: `Msg 50301 … Передперевірка U1` на `migration.sql` | колонка шаблону чи поле довідника посилається на видалену одиницю | п. 8.2 |
| `404` на `GET /api/v1/jobs/…` | `#` в ідентифікаторі не закодовано | кодувати `%23` |
| пошта не йде | не задано `Smtp:Host`/`Smtp:From` | задати й перевірити `POST /api/v1/notifications/channels/{id}/test` |

## 6. Резервне копіювання і відновлення

### 6.1. Що бекапити

| Що | Де | Чому |
|---|---|---|
| **база ECR** (усі файлові групи: `PRIMARY`, `DATA_HOT`, `DATA_ARCHIVE`, `AUDIT`, `INDEXES` і журнал) | SQL Server | усі дані, аудит, архів. Бекапити **повною базою**. Часткове відновлення файлових груп не перевірялось |
| **ключі Data Protection** | таблиця `sec.DataProtectionKey` **в тій самій БД** | потрапляють у бекап бази. Без них недійсні всі сесії й **не розшифровуються секрети каналів сповіщень** |
| **сертифікат** `Auth:DataProtection:CertificateThumbprint` (з закритим ключем) | `LocalMachine\My` | якщо ключі захищені сертифікатом, без нього бекап бази не відкриє їх. Експортуйте PFX окремо |
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

На Express усі розміри — 64 МБ.

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
3. Перевірити RCSI (`06-rcsi.sql`) і наявність сертифіката Data Protection.
4. `Start-Service EcrApi`, потім `/health/ready` і `/health/db`.

⚠ Процедура відновлення **на стенді не перевірялась**. Перевірте її до
приймання.

### 6.4. Ротація відкритих ключів Data Protection (S11)

**Коли:** один раз — після першого розгортання з сертифікатом
(`deploy-ecr.ps1 -DataProtectionThumbprint`) на майданчику, де служба
раніше працювала без нього.

**Чому:** з сертифікатом **нові** ключі кільця пишуться в
`sec.DataProtectionKey` зашифрованими, але **старі**, записані відкрито,
застосунок і далі читає й приймає до кінця їхнього строку (типово 90 днів).
Доки старий ключ у таблиці — будь-хто з доступом на читання до бази або до
будь-якого бекапу, зробленого раніше, може підробити cookie сеансу будь-якого
користувача.

⛔ **Попередження — наслідки для користувачів:**

- **усі користувачі вийдуть із системи**: сеанси, підписані старими ключами,
  стануть недійсними;
- **секрети каналів сповіщень доведеться ввести наново**: вони зашифровані
  тими самими ключами і після ротації не розшифровуються. Перелік каналів —
  `/admin/notifications`; перед ротацією підготуйте їхні секрети.

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
7. Увійти в систему й ввести наново секрети каналів сповіщень
   (`/admin/notifications`, перевірка — `POST /api/v1/notifications/channels/{id}/test`).
8. **Бекапи, зроблені до кроку 5, містять відкриті ключі.** Обмежте доступ до
   них або знищіть їх відповідно до політики зберігання: для них ротація
   нічого не змінює.

⚠ Процедура **на стенді не перевірялась**. Перевірте її до приймання.

## 7. Архівація років

`arc.usp_ArchiveYear @ProjectId, @FromPeriodKey, @ToPeriodKey, @BatchSize = 500000`
(`03-archive-proc.sql`) переносить дані з `DATA_HOT` в `DATA_ARCHIVE`.

| Помилка | Значення |
|---|---|
| 50012 | у діапазоні є періоди проєктів, які ще не архівовано (`Status <> 4`) |
| 50013 | немає меж партицій для діапазону |
| 50010 | не збіглася контрольна сума, перенесення скасовано |
| 50014 | прогалина в діапазоні |

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

1. Повний бекап (п. 6.2).
2. Розгортання:

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools\deploy-ecr.ps1 `
     -SqlInstance <сервер> -Database <база> -MsiPath <шлях до .msi> `
     -DataProtectionThumbprint <відбиток> -WhatIf
   ```

   Спершу запустіть із `-WhatIf`, потім без нього. Кроки скрипта: передумови,
   схема, MSI (`msiexec /qn`), змінні служби, конфіг (лише якщо ще заглушка),
   перезапуск служби, перевірка `GET /health/live`, потім очікування
   `GET /health/ready` до `Healthy`/`Degraded` (не довше `-ReadyTimeoutSeconds`,
   дефолт 180 с). Перевірки, що не `Healthy`, скрипт друкує. `Unhealthy` лише
   через `sources` — попередження (зовнішнє джерело, ручне введення працює);
   будь-яка інша `Unhealthy` після тайм-ауту — розгортання провалене, «Готово»
   не друкується.
3. Перевірити `/health/db`.

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
stderr і журналі); після зміни — `Restart-Service EcrWorker`.
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

**Три транспорти, рівно один** (`deploy-ecr.ps1`; на кожному оновленні — бо `Environment` стирає
оновлення MSI, п. 10.3): `-HttpsThumbprint '<відбиток>'` (HTTPS; порт `-AppPort`, для `https://сервер/`
— 443; необов'язково `-HttpRedirectPort 80`), `-BehindHttpsProxy` (TLS на проксі перед застосунком;
`Auth:RequireHttps` лишається `true`), `-AllowHttp` (лише стенд: `Auth:RequireHttps=false`).
Жодного — скрипт зупиняється на кроці 1. Причина вибору: cookie сеансу `Secure`, а по HTTP його
браузер не відсилає — вхід з інших машин не працює (симптом «вдався, далі 401»).

**Змінні служби `EcrApi`** (пише `deploy-ecr.ps1`): `ASPNETCORE_URLS`,
`ECR_Transport__Https__CertificateThumbprint`, `ECR_Transport__Https__Port` (лише з перенаправленням),
`ECR_Auth__RequireHttps` (завжди явно).

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

⚠ **HSTS:** запити по HTTPS отримують `Strict-Transport-Security: max-age=31536000`; після першого входу браузер
не відкриє це ім'я по `http://` до кінця строку. За проксі застосунок HSTS не віддає (`X-Forwarded-*` не читає):
ставити на проксі.

⚠ **Невідомо про майданчик замовника** (у документах проєкту немає; потрібне рішення замовника): чи є
зворотний проксі/балансувальник перед застосунком і хто завершує TLS; ім'я хоста, за яким відкриватимуть
застосунок (SAN сертифіката), і який ЦС його видає; кількість вузлів. Відомо лише рішення людини 2026-09-29: «HTTPS
— сертифікат замовника».

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
- `/health/ready` і `/admin/health` — картка `tzdata`: `Healthy`, або `Degraded` (не 503) з полем
  `staleZones` (напр. `Asia/Almaty=+06:00`; `?` — пояса в базі немає), `expectedOffset`, `checkedZones`.

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
