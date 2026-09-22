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
```

Порт задає `ASPNETCORE_URLS` у середовищі служби (дефолт `-AppPort 5000`).

Послідовність старту (`StartupSequence.cs`):

1. Перевірка схеми (`Schema:StartupMode`).
2. Сід `09-seed.sql`. Його виконує **сам застосунок**, а не скрипти розгортання.
3. Створення запису `bootstrap`.
4. Реєстрація розкладів.

Якщо з цих кроків падає будь-який, служба не стартує. Причину шукайте в лозі
(п. 3.2) і в журналі подій Windows.

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

Колонка «Дефолт» — значення з `appsettings.json`. Якщо код має інший запасний
дефолт, він указаний у дужках.

| Ключ | Дефолт | Значення |
|---|---|---|
| `ConnectionStrings:Ecr` | порожньо | рядок підключення до SQL Server. **Секрет**: `ECR_ConnectionStrings__Ecr` |
| `Schema:StartupMode` | `Validate` | `Validate` — не стартувати, якщо є незастосовані міграції EF. `Migrate` — застосувати їх на старті |
| `Database:EditionMode` | `Auto` | режим редакції SQL Server (Express / повна). `Auto` — визначити самостійно |
| `Database:CommandTimeoutSeconds` | 60 | таймаут команди SQL, с |
| `Database:BulkBatchSize` | 50000 (код: 5000) | розмір пачки масового запису |
| `Cache:SchemaName` / `Cache:TableName` | `dbo` / `Cache` | таблиця розподіленого кешу |
| `Cache:MetadataSlidingMinutes` | 240 | кеш метаданих, хв |
| `Cache:AccessProfileSlidingMinutes` | 60 | кеш профілю доступу, хв |
| `Auth:CookieName` | `ecr.auth` (код: `ecr.session`) | ім'я cookie сесії |
| `Auth:SlidingHours` | 8 | ковзний строк сесії, год |
| `Auth:RequireHttps` | `true` | cookie лише через HTTPS |
| `Auth:EnableNegotiate` | `true` | вхід Windows (Negotiate) |
| `Auth:StampCacheSeconds` | 5 | як швидко блокування чи зміна ролей діє на відкриті сесії, с |
| `Auth:DataProtection:CertificateThumbprint` | немає | відбиток сертифіката з `LocalMachine\My` для захисту ключів Data Protection (п. 6.2) |
| `Security:RateLimit:LoginPermitPerMinute` | 60 | спроб входу за хвилину |
| `Security:RateLimit:TrustForwardedFor` | `false` | брати IP із `X-Forwarded-For`. Вмикати лише за довіреним проксі |
| `Security:RateLimit:SearchPermit` / `SearchWindowSeconds` | 30 / 10 | обмеження пошуку |
| `Jobs:NightlyRecalculation:Enabled` | `false` | нічний перерахунок о 03:30. Вмикається лише рядком `true` |
| `Audit:ExportMaxRows` | 100000 | межа експорту аудиту CSV |
| `Campaign:AtRiskDays` | 3 | за скільки днів до терміну проєкт вважається «під загрозою» |
| `Notifications:WebhookAllowedHostSuffixes` | `.webhook.office.com;.logic.azure.com;.powerplatform.com` | дозволені хости вебхуків (`;` або `,`) |
| `Smtp:Host`, `Smtp:From` | немає | транспорт пошти. Без них пошта не йде |
| `Smtp:Port` | (587) | |
| `Smtp:UseStartTls` | (`true`) | |
| `Smtp:User`, `Smtp:SecretName` | немає | автентифікація. Пароль — секрет `Secrets:<SecretName>` |
| `Secrets:<ім'я>` | немає | секрети джерел і каналів. **Лише змінні оточення** |
| `PiSqlClient:CatalogQuery` / `TemplateQuery` / `ValueQuery` | немає (вбудовані) | перевизначення запитів адаптера PI SQL Client |
| `Sql:CatalogQuery` / `Sql:ValueQuery` | немає (вбудовані) | те саме для SQL-джерела |
| `Bootstrap:Password` | немає | запасний пароль `bootstrap`. Основний шлях — файл `bootstrap.secret` |
| `Telemetry:ServiceName` | `ecr-api` | ім'я служби в телеметрії |
| `Telemetry:OtlpEndpoint` | порожньо | OTLP-колектор. Порожньо — не експортувати |
| `Logging:LogLevel:*` | `Information`, `Microsoft.AspNetCore` = `Warning` | рівні логування |
| `Logging:File:Directory` | `%ProgramData%\ECR\logs` | тека логів. Порожньо — без файлового логу |
| `Logging:File:RetainedFiles` | 30 | скільки файлів зберігати |
| `Logging:File:FileSizeLimitMb` | 100 | розмір файлу, після якого починається новий |
| `AllowedHosts` | `*` | |

⚠ **потрібне рішення замовника:** SMTP, OTLP-колектор, сертифікат для Data
Protection і HTTPS, адреси джерел PI. Дефолти коду — «вимкнено» або порожньо.

## 3. Моніторинг і логи

### 3.1. Health-ендпоінти

| Ендпоінт | Доступ | Що перевіряє |
|---|---|---|
| `/health/live` | анонімно | нічого: процес відповідає |
| `/health/ready` | анонімно | `db`, `jobs`, `sources`. Подробиці `db` приховано |
| `/health/db` | після входу | редакція, RCSI, файлові групи, запас партицій |

HTTP-код: `Healthy` і `Degraded` дають **200**, `Unhealthy` — **503**. Моніторинг
має читати поле `status` у JSON, а не лише код відповіді.

| Перевірка | Degraded | Unhealthy |
|---|---|---|
| `db` | попереду менше 2 партицій | RCSI вимкнено; немає файлової групи `DATA_HOT`, `DATA_ARCHIVE`, `AUDIT` або `INDEXES`; БД недоступна |
| `jobs` | у планувальника немає тригерів | планувальник не зареєстрований, зупинений або кидає помилку |
| `sources` | джерело ще не запускалось або є прогалина покриття | останній запуск будь-якого активного джерела впав. Якщо активних джерел немає — Healthy |

### 3.2. Логи

- Тека: `%ProgramData%\ECR\logs`. Файли `ecr-yyyyMMdd.log`, новий щодня. Коли
  файл перевищує ліміт розміру, починається `…_001.log`. Зберігається 30 файлів.
- Якщо тека недоступна для запису, файловий лог вимикається, а служба все одно
  стартує. Перевіряйте права облікового запису служби.
- Формат рядка:
  `2026-09-22 10:00:00.000 +03:00 [ERR] [<CorrelationId>] [uid:<UserId>] [<машина>] <джерело>: <повідомлення>`

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
| служба не стартує після зміни відбитка | немає сертифіката `Auth:DataProtection:CertificateThumbprint` у `LocalMachine\My` | встановити сертифікат із закритим ключем і дати права облікового запису служби |
| `db` Degraded: менше 2 партицій попереду | не працює Agent-задача (Express) | `EXEC arc.usp_EnsurePartitions @MonthsAhead = 6;` або скрипт `GET /api/v1/health/partitions/script` |
| `db` Unhealthy: RCSI | базу відновили або створили без `06-rcsi.sql` | виконати `06-rcsi.sql` |
| `sources` Unhealthy | PI/SQL-джерело недоступне або змінився секрет | стан на `/admin/sources`, помилка в `GET /api/v1/jobs/{id}`, секрет `ECR_Secrets__<ім'я>` |
| `jobs` Unhealthy | планувальник зупинився | лог за `Quartz`, перезапуск служби |
| розгортання: `01-filegroups.sql`, `Msg 5149 … error 112` | немає місця на диску даних | звільнити місце. Файлові групи займають ~14 ГБ на повній редакції (п. 6.1) |
| збірка чи оновлення: `The file is locked by: "Ecr.Api (<pid>)"` | DLL тримає запущена служба | `Stop-Service EcrApi`, потім оновлення |
| `404` на `GET /api/v1/jobs/…` | `#` в ідентифікаторі не закодовано | кодувати `%23` |
| пошта не йде | не задано `Smtp:Host`/`Smtp:From` | задати й перевірити `POST /api/v1/notifications/channels/{id}/test` |

## 6. Резервне копіювання і відновлення

### 6.1. Що бекапити

| Що | Де | Чому |
|---|---|---|
| **база ECR** (усі файлові групи: `PRIMARY`, `DATA_HOT`, `DATA_ARCHIVE`, `AUDIT`, `INDEXES` і журнал) | SQL Server | усі дані, аудит, архів. Бекапити **повною базою**. Часткове відновлення файлових груп не перевірялось |
| **ключі Data Protection** | таблиця `dbo.DataProtectionKeys` **в тій самій БД** | потрапляють у бекап бази. Без них недійсні всі сесії й **не розшифровуються секрети каналів сповіщень** |
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

Процедура:

1. Повний бекап (п. 6.2).
2. Розгортання:

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools\deploy-ecr.ps1 `
     -SqlInstance <сервер> -Database <база> -MsiPath <шлях до .msi> -WhatIf
   ```

   Спершу запустіть із `-WhatIf`, потім без нього. Кроки скрипта: передумови,
   схема, MSI (`msiexec /qn`), змінні служби, конфіг (лише якщо ще заглушка),
   перезапуск служби, перевірка `GET /health/live`.
3. Перевірити `/health/ready` і `/health/db`.

Графічний майстер `tools/Ecr.Setup` запускає той самий `deploy-ecr.ps1`.

## 9. Відкат

Окремого механізму відкату в коді **немає**. Міграції EF назад не застосовуються,
і `deploy-ecr.ps1` відкату не робить.

1. `Stop-Service EcrApi`.
2. Відновити базу з бекапу, зробленого перед оновленням (п. 6.3).
3. Встановити попередній MSI.
4. `Start-Service EcrApi` і перевірити health.

Дані, введені після оновлення, при такому відкаті втрачаються.
