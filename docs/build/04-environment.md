# 04 — Середовище і залежності

> **Статус версій: ГІПОТЕЗА.** Пакети і їхні версії підібрані на ПК-1 **без
> можливості виконати `restore`**. Розбіжність — очікувана і нормальна.
> Порядок дій на ПК-2: спробувати як написано → якщо не резолвиться, взяти
> найближчу стабільну версію тієї самої мажорної лінії → **записати в
> `questions.md` як `BOOTSTRAP-FIX`**. Мажорні лінії міняти не можна без запису
> в `questions.md` з поясненням.

---

## 1. Передумови

| Компонент | Версія | Перевірка |
|---|---|---|
| **.NET SDK** | **10.0.x** | `dotnet --list-sdks` |
| **SQL Server** | 2019+ (Developer/Standard/Express) | `SELECT @@VERSION` |
| **Docker** | будь-яка актуальна | `docker ps` — потрібен для інтеграційних тестів |
| **Node.js** | **22.x LTS** | `node -v` |
| **npm** | 10.x (іде з Node 22) | `npm -v` |
| **git** | будь-яка | `git --version` |
| Інтернет | доступ до nuget.org і registry.npmjs.org | |

**Якщо чогось немає** — не обходь, а зафіксуй у `questions.md` і працюй у
деградованому режимі за §5.

## 2. Обов'язкова перевірка редакції SQL Server

Архітектура працює на **Standard 2016 SP1+**; Enterprise вмикається автоматично
(рішення `D-28`, ТЗ АРХ-7). **Блокуючий випадок — Standard до 2016 SP1**: там
немає партиціонування, columnstore і компресії, тобто модель архівації не працює
в принципі.

```sql
SELECT SERVERPROPERTY('Edition')             AS Edition,
       SERVERPROPERTY('EngineEdition')       AS EngineEdition,   -- 3 = Enterprise/Developer
       SERVERPROPERTY('ProductMajorVersion') AS MajorVersion,    -- >= 13
       SERVERPROPERTY('ProductLevel')        AS ProductLevel,
       DATABASEPROPERTYEX(DB_NAME(),'IsReadCommittedSnapshotOn') AS Rcsi;
```

`Developer Edition` повідомляє `EngineEdition = 3`, тобто виглядає як Enterprise —
для розробки це прийнятно, але **навантажувальні заміри виконуються в режимі
`Standard`** (`Database:EditionMode = "Standard"`), бо бюджет продуктивності має
витримуватися на базовій редакції.

### 2.1 Фактичне середовище — заміряно 2026-09-04 (`D-101`)

Перевіряти заново не треба; перевіряти **розбіжність** — треба (`ФВ-7.9`).

| Сервер | Роль | Версія | Редакція |
|---|---|---|---|
| `NCATDEVV08` | DEV | 15.0.4480.2 · SQL 2019 RTM · CU32 | Enterprise Core-based, `EngineEdition = 3` |
| `NCATUATV12` | UAT | 15.0.4480.2 · SQL 2019 RTM · CU32 | те саме |
| `NCATSQLV92\PI_ECR` | PROD | 15.0.4480.2 · SQL 2019 RTM · CU32 | те саме |

Три висновки, кожен із наслідком для коду:

1. **Блокера немає.** Підлога `Standard 2016 SP1` (`MajorVersion ≥ 13`)
   виконана із запасом: 2019 — це `MajorVersion = 15`. Партиціонування,
   columnstore і компресія доступні.
2. **Це справжній Enterprise, не Developer.** `Edition` містить
   `Enterprise Edition: Core-based Licensing`, тому пастка з `EngineEdition = 3`
   тут не спрацьовує. Але перевірку рядка `Edition` **не прибирати** — вона
   потрібна на будь-якій іншій машині.
3. **Фізичного Standard-інстансу в контурі немає.** Отже `D-64` («бюджет
   вимірюється в Standard») виконується **лише** примусовим
   `Database:EditionMode = "Standard"` під час замірів. Без цього кроку заміри
   покажуть Enterprise-цифри, а прод колись поїде на Standard і бюджет не
   витримається.

**RCSI на чинній базі `ECR` вимкнено** (`AllowSnapshotIsolation` теж). Нас це
не зачіпає: ми створюємо **власну** базу `Ecr`, а `06-rcsi.sql` вмикає
`READ_COMMITTED_SNAPSHOT` при створенні. **Чинну базу `ECR` не чіпати** — це
продуктивна база старої системи.

## 3. NuGet-пакети

### 3.1 Наскрізні (`Directory.Packages.props`, central package management)

| Пакет | Версія | Ліцензія | Навіщо |
|---|---|---|---|
| `Microsoft.EntityFrameworkCore` | 10.0.11 | MIT | ORM (`D-02`) |
| `Microsoft.EntityFrameworkCore.SqlServer` | 10.0.11 | MIT | провайдер |
| `Microsoft.EntityFrameworkCore.Design` | 10.0.11 | MIT | `dotnet ef migrations` |
| `Microsoft.EntityFrameworkCore.Relational` | 10.0.11 | MIT | транзитивно, фіксуємо явно |
| `Microsoft.Data.SqlClient` | 6.1.6 | MIT | `SqlBulkCopy`, raw SQL |
| `Microsoft.Extensions.Hosting` | 10.0.11 | MIT | host, DI, конфіг |
| `Microsoft.Extensions.Caching.Memory` | 10.0.11 | MIT | `IMemoryCache` |
| `Microsoft.Extensions.Caching.SqlServer` | 10.0.11 | MIT | `IDistributedCache` без Redis (`D-06`) |
| `Microsoft.AspNetCore.OpenApi` | 10.0.11 | MIT | генерація OpenAPI (`D-07`) |
| `Scalar.AspNetCore` | 2.0.0 | MIT | UI документації (`D-07`) |
| `Microsoft.AspNetCore.Authentication.Negotiate` | 10.0.11 | MIT | Windows-автентифікація |
| `NCalcSync` | **6.1.1** | MIT | обчислювач виразів (`D-19`) — **не парсер нашої мови**. Мажор 5→6 за `Q-004`: у 5.x немає виправлення `GHSA-3w5p-95mh-gq75` |
| `ClosedXML` | 0.104.2 | MIT | експорт/імпорт `.xlsx` |
| `Quartz` | 3.13.1 | Apache-2.0 | планувальник — **дефолтна реалізація** `IBackgroundJobScheduler` |
| `Quartz.Extensions.Hosting` | 3.13.1 | Apache-2.0 | інтеграція з host |
| ~~`Microsoft.CodeAnalysis.CSharp.Scripting`~~ | — | MIT | **не підключається** (`D-105`, `D-113`): рівень 2 у першому релізі не будується, залежність під нереалізовану функцію суперечить `D-12` |
| `System.IdentityModel.Tokens.Jwt` | — | — | ⛔ **не використовуємо** — автентифікація на cookie |
| `Microsoft.Extensions.Hosting.WindowsServices` | 10.0.11 | MIT | `UseWindowsService()` — без нього процес не відповідає на старт/стоп SCM (`docs/build/10-installer.md`) |

> **Чому Quartz, а не Hangfire.** `D-09` дозволяє обидва **за портом
> `IBackgroundJobScheduler`**. Hangfire — LGPL, і його допустимість — відкрите
> питання до ІБ (`L-1`). Quartz — Apache-2.0, питання не виникає. Порт
> дозволяє замінити реалізацію за день, якщо ІБ дасть добро на Hangfire.

### 3.2 Тести

| Пакет | Версія | Ліцензія |
|---|---|---|
| `Microsoft.NET.Test.Sdk` | 17.12.0 | MIT |
| `xunit` | 2.9.2 | Apache-2.0 |
| `xunit.runner.visualstudio` | 2.8.2 | Apache-2.0 |
| `NSubstitute` | 5.3.0 | BSD-3-Clause |
| `Testcontainers.MsSql` | 4.14.0 | MIT |
| `Microsoft.EntityFrameworkCore.Sqlite` | 10.0.11 | MIT |
| `NetArchTest.Rules` | 1.3.2 | MIT |
| `coverlet.collector` | 6.0.2 | MIT |

> ⛔ **`FluentAssertions` заборонений.** З версії 8 (січень 2025) він під
> комерційною ліцензією Xceed для комерційного використання. Асерти —
> **тільки вбудовані `Assert.*` з xUnit**. Це не стильова вподоба, а
> ліцензійна вимога (`D-12`).

### 3.3 Заборонені пакети (`D-12`, `docs/15-verified-stack.md`)

`EFCore.BulkExtensions` · `HyperFormula` · `EPPlus` 5+ · `Handsontable` ·
`NBomber` 5+ · `FluentAssertions` 8+ · будь-який Redis-сервер ·
`AG Grid Enterprise` · усе під GPL / AGPL / SSPL / Polyform / cFOSS / noncommercial.

### 3.4 Інсталятор (`installer/`, `docs/build/10-installer.md`)

Не входить у `Ecr.sln` (WiX не збирається на Linux, а `build`/`test`/`server`
CI — виключно ubuntu-latest). Версії — у `Directory.Packages.props` разом із
рештою реєстру, окрім самого SDK (`Sdk="WixToolset.Sdk/5.0.2"` — атрибут
проєкту, не `PackageReference`).

| Пакет | Версія | Ліцензія | Навіщо |
|---|---|---|---|
| `WixToolset.Sdk` | 5.0.2 | MS-RL | MSBuild SDK інсталятора |
| `WixToolset.Util.wixext` | 5.0.2 | MS-RL | ACL, служба |
| `WixToolset.Firewall.wixext` | 5.0.2 | MS-RL | правило брандмауера |
| `WixToolset.UI.wixext` | 5.0.2 | MS-RL | стандартні діалоги |

> ⛔ **Не 6.x і новіші.** З `6.0.0` (квітень 2025) FireGiant вимагає Open
> Source Maintenance Fee за комерційне використання офіційних збірок понад
> $10k доходу — сам код лишається MS-RL, але це вже платне зобов'язання, не
> технічне рішення. `5.0.2` — останній випуск до цієї моделі, той самий
> формат `.wxs`. Платити за `6.x`/новіше чи лишатися на `5.x` — рішення не
> моє (`Q-210`, `docs/build/questions.md`).

**Нова залежність додається лише так:** запис у `questions.md` → перевірка
ліцензії за першоджерелом → додавання в `Directory.Packages.props`. CI має
перевірку ліцензій, вона впаде на забороненій.

## 4. npm-пакети (`src/Ecr.Web`)

| Пакет | Версія | Ліцензія | Навіщо |
|---|---|---|---|
| `react` / `react-dom` | 19.0.0 | MIT | |
| `typescript` | 5.7.2 | Apache-2.0 | |
| `vite` | 6.0.7 | MIT | збірка |
| `@vitejs/plugin-react` | 4.3.4 | MIT | |
| `@mantine/core` / `@mantine/hooks` | 7.15.2 | MIT | UI-кіт (`D-04`) |
| `@mantine/dates` / `@mantine/form` / `@mantine/notifications` | 7.15.2 | MIT | |
| `@revolist/react-datagrid` | 4.11.0 | MIT | grid (`FQ-1`, підтвердити прототипом) |
| `@tanstack/react-query` | 5.62.11 | MIT | серверний стан |
| `zustand` | 5.0.2 | MIT | клієнтський стан |
| `react-hook-form` | 7.54.2 | MIT | форми |
| `zod` | 3.24.1 | MIT | схеми |
| `react-router-dom` | 7.1.1 | MIT | маршрутизація |
| `@formulajs/formulajs` | 4.4.9 | MIT | клієнтські формули — **лише підказка** (`D-20`) |
| `openapi-typescript` | 7.5.0 | MIT | типи з нашого OpenAPI |
| `vitest` | 2.1.8 | MIT | тести |
| `@testing-library/react` | 16.1.0 | MIT | тести |
| `jsdom` | 25.0.1 | MIT | середовище тестів |
| `monaco-editor` | 0.56.0 | MIT | редактор виразів (`ФВ-9.15a`, `D-113`, `П-32`) |

> ⛔ **`monaco-editor` вантажиться ЛИШЕ динамічним імпортом.** Це не стиль, а
> умова бюджету `D-132`: 818 КБ gzip у спільному вхідному чанку зламали б межу
> 250 КБ **одразу для всіх маршрутів**. Підключаються поіменні внески
> (`suggest`, `hover`, `bracketMatching`, `parameterHints`), а не
> `editor.main` — той тягне ще вісім десятків чужих мов і важить 1 121 КБ.
> Стереже це `npm run budget`.
>
> ⚠ Пакет тягне `dompurify` із відомою вразливістю **low/moderate**
> (`GHSA-c2j3-45gr-mqc4`, обхід `CUSTOM_ELEMENT_HANDLING`). Гейт
> `npm audit --omit=dev --audit-level=high` її не блокує, і це правильно: шлях
> до неї — рендеринг markdown у підказках, а наші підказки віддаються
> **простим рядком**, не `IMarkdownString`. Перевірити при оновленні Monaco.

> ⚠ **npm-екосистема дрейфує швидше за NuGet.** Очікуй більше `BOOTSTRAP-FIX`
> саме тут. Правило те саме: мажорну лінію не міняти, мінорну — вільно, кожну
> зміну записати.

## 5. Деградований режим (якщо чогось немає)

| Немає | Що робити | Наслідок |
|---|---|---|
| **Docker** | задати `ECR_TEST_SQL` — фікстура піде на локальний SQL Server замість контейнера | нічого не втрачається: інтеграційні тести виконуються повністю |
| **Docker і SQL Server** | додати `--filter "Category!=Integration"` **вручну** до команди `dotnet test` — сам собою жоден крок цього не робить (`09-commands.md` §2) | партиціонування, `TRUNCATE PARTITIONS`, складені FK і RCSI **не перевірені** — записати в `questions.md`. Без цього фільтра `SqlServerFixture` спробує підняти контейнер і **впаде** — тести не «не запустяться», а почервоніють |
| **SQL Server** | те саме + `Ecr.Api` не стартує | unit-тести і тести виразів працюють; решта — ні |
| **Node.js** | Етап 6 (frontend) не виконується | backend не залежить від frontend |
| **Інтернет** | `restore` неможливий | зупинка, запис у `questions.md` |

### 5.1 Два рівноправні шляхи до SQL для інтеграційних тестів

`SqlServerFixture` обирає сама, і жоден шлях не є «запасним» (`Q-062`):

| `ECR_TEST_SQL` | Що робить фікстура |
|---|---|
| задана (рядок підключення до **сервера**) | працює на цьому інстансі, створюючи власну базу |
| не задана | піднімає `mcr.microsoft.com/mssql/server:2022-latest` через Testcontainers |

Розробник не зобов'язаний тримати Docker, CI не зобов'язаний мати SQL Server.
Ім'я бази з `ECR_TEST_SQL` **ігнорується**: фікстура створює свою
(`EcrTest_<збірка>`) і перестворює її з нуля при кожному прогоні, тож на чужий
інстанс вона не впливає. Зіставлення задається явно —
`Latin1_General_100_CI_AS_SC` (`02a` §1.0), а не успадковується від інстансу.

⚠ **Виправлено (Q-207, було неточно сформульовано).** Раніше тут стояло, що
`dotnet test` за замовчуванням виключає інтеграційні тести (трейтом
`Category=Integration`) і тому зелений без Docker. Це не відповідає тому, як
насправді працює канонічний прогін: `tools/verify-all.ps1` (той самий скрипт,
що й CI, крок «Тести .NET») кличе `dotnet test` **без фільтра `Category`** —
виключений явно лише `Ecr.Scenarios.Tests` (директива №09). Отже інтеграційні
тести (кількасот тестів із трейтом `[Trait("Category","Integration")]` у
`Ecr.Api.Tests`, `Ecr.Application.Tests`, `Ecr.Infrastructure.Tests` та інших)
виконуються в цьому кроці **за замовчуванням** — і тому CI піднімає реальний
SQL Server у контейнері (`.github/workflows/ci.yml`, job `server`) саме для
нього, а не лише для гейта розгортання.

**Що це означає для `dotnet test` без параметрів.** Без `ECR_TEST_SQL` і без
Docker `SqlServerFixture` не «пропускає» інтеграційні тести — вона намагається
підняти контейнер через Testcontainers і **падає**, і разом з нею падають усі
тести, що нею користуються. Зелений прогін без Docker вимагає **явного**
фільтра `--filter "Category!=Integration"` — це зручність для розробника без
Docker, не поведінка за замовчуванням (команда — у `09-commands.md` §2).

## 6. Змінні оточення і конфігурація

Секрети **ніколи** не потрапляють у `appsettings.json` — лише імена секретів
(`D-11`, ФВ-6.11). Для локальної розробки — `dotnet user-secrets`.

| Змінна | Приклад | Навіщо |
|---|---|---|
| `ECR_ConnectionStrings__Ecr` | `Server=localhost;Database=Ecr;Trusted_Connection=True;TrustServerCertificate=True` | основна БД |
| `ECR_Database__EditionMode` | `Auto` \| `Standard` \| `Enterprise` | АРХ-7. Прод — завжди `Standard` явно (Developer/Evaluation зовні невідрізнювані від Enterprise, `SqlCapabilitiesProbe`) |
| `ECR_Schema__StartupMode` | `Validate` (прод) \| `Migrate` (dev/test) | `B01` §6.3 |
| `ECR_Auth__CookieName` | `ecr.auth` | |
| `ECR_Auth__SlidingHours` | `8` | |
| `ECR_Cache__SchemaName` | `dbo` | схема таблиці розподіленого кешу (`AddDistributedSqlServerCache`) |
| `ECR_Cache__TableName` | `Cache` | назва тієї самої таблиці — має збігатися з `13-cache-table.sql` |
| `ECR_Secrets__<ім'я>` | `ECR_Secrets__pi-af-service-account` = `<пароль>` | секрет джерела (PI AF, SMTP, …). `<ім'я>` — значення колонки `SecretName` конкретного джерела (`ext.DataSource.SecretName`, наприклад `pi-af-service-account`) чи налаштування сповіщень, НЕ фіксований суфікс шляху — `ConfigurationSecretProvider.Find` читає `Secrets:<ім'я>` за тим самим механізмом для ВСІХ джерел, PiAf і SMTP разом (`ConfigurationSecretProvider.cs`, `SmtpNotificationSender.cs`) |
| `ECR_Bootstrap__Password` | одноразовий пароль | **лише перший старт** (`D-115`): застосунок створює локального адміністратора з `MustChangePassword = 1`. Якщо запис уже існує — змінна ігнорується. Джерело — не сама змінна оточення (`Q-215`, `Q-222` аудит): застосунок читає й одразу видаляє одноразовий файл `%ProgramData%\ECR\config\bootstrap.secret`, куди `deploy-ecr.ps1 -BootstrapPassword` записує значення. Сам bootstrap-обліковий запис деактивується не власним входом, а коли БУДЬ-ЯКИЙ домен-користувач отримує `Security.ManageUsers` (`DisableBootstrapAdminHandler`) |
| `ECR_TEST_SQL` | `Server=localhost\SQLEXPRESS;Integrated Security=true;TrustServerCertificate=true` | рядок підключення до **сервера** для інтеграційних тестів замість Testcontainers. ⚠ Саме рядок, а не `1`: фікстура підключається за ним, а базу створює свою |
| `ECR_TEST_DB` | `EcrTest_Infrastructure` | перевизначає ім'я тестової бази. За замовчуванням — своє на кожну збірку тестів (`Q-055`) |

> ✎ **Q-223 (аудит) закрито 2026-09-11 — людина доручила вирішити, а не
> лише задокументувати.** Три знахідки, три різні долі:
>
> - `Database:EditionMode` був справжнім пропуском коду — ключ існував в
>   `appsettings.json` (з іншим значенням у Development!), а
>   `StartupSequence.cs` його ніколи не читав, завжди передаючи `Auto`.
>   **Виправлено**: старт тепер читає цей ключ і передає його в
>   `SqlCapabilitiesProbe.ProbeAsync`.
> - `Cache:DistributedProvider` виявився не пропуском, а розбіжністю назв:
>   код завжди читав `Cache:SchemaName`/`Cache:TableName`, яких у
>   `appsettings.json` не було взагалі (там були `DistributedProvider`/
>   `DistributedSchemaName`/`DistributedTableName` — інші імена, що
>   ніколи не застосовувались). **Виправлено**: `appsettings.json`
>   перейменовано на реальні ключі, зі значеннями, що вже й так діяли
>   мовчки за замовчуванням (`dbo`/`Cache` — збігається з
>   `13-cache-table.sql`), тобто без зміни поведінки.
> - `Jobs:Provider`/`Jobs:WorkerCount` — підтверджено мертві (Quartz
>   реєструється безумовно, `DependencyInjection.cs`; заміна провайдера —
>   через порт `IBackgroundJobScheduler`, D-09, не конфігурацію).
>   **Прибрано з `appsettings.json`.** Той самий огляд секції `Jobs`
>   заразом показав, що вона мертва ЦІЛКОМ: `PeriodStateCron`/
>   `ConsistencyCheckCron`/`PartitionCheckCron`/`MaxParallelRecalculation`
>   теж ніде не читаються — `RecurringScheduleService` розкладає
>   нічні/погодинні задачі двома захардкодженими константами
>   (`NightlyCron`/`HourlyCron`), не per-job значеннями з конфігурації.
>   Секцію `Jobs` прибрано з `appsettings.json` цілком, а не лише два
>   названі в Q-223 ключі — лишати непрочитані ключі поруч із реальними
>   вводило б в оману саме тому, чому цей запис існує.
>
>   **Не зроблено свідомо, і це не той самий пропуск:** реальна вимога за
>   лаштунками цих ключів — щоб два інстанси застосунку (D-32 вимагає
>   ≥2 за балансувальником) не виконували той самий нічний/погодинний
>   job двічі одночасно — досі не має захисту. `RecurringScheduleService`
>   лише РЕЄСТРУЄ розклад у Quartz (безпечно дублювати), а сам запуск
>   job'и на кожному інстансі нічим не координований між інстансами.
>   Патерн для фіксу вже є в цьому самому дереві —
>   `StartupSequence.ApplySchemaModeAsync` бере `sp_getapplock` навколо
>   міграції з тієї самої причини (два інстанси, що стартують одночасно).
>   Перенести той самий патерн на шість job-класів — окрема, за розміром
>   самостійна робота (потрібна семантика "не чекай — пропусти цей
>   запуск", а не "почекай і виконай", як у міграції), і вона свідомо НЕ
>   зроблена в цьому проході.
>
> **`ECR_Bootstrap__Password` не має значення за замовчуванням і не потрапляє
> нікуди, крім пам'яті процесу**: ні в seed, ні в `appsettings`, ні в лог
> (`ФВ-6.11`). Якщо змінної немає і bootstrap-адміністратора теж немає,
> застосунок стартує і **пише попередження** — це не помилка конфігурації, а
> нормальний стан контуру, де вхід уже переведено на домен.

Префікс `ECR_` і роздільник `__` — стандартна конвенція
`Microsoft.Extensions.Configuration.EnvironmentVariables`.

## 7. Що НЕ конфігурується файлами

Прямо заборонено виносити в `appsettings.json` (`B01` §6.1, ФВ-2.14):
політику паролів (→ `sec.PasswordPolicy`), offsets періодів (→ `doc.PeriodPolicy`),
ролі й права (→ `sec.*`), мапінги і адреси джерел (→ `ext.DataSource`).
Причина проста: інакше DEV/TEST/PROD розповзаються, і «чому на тесті інша
поведінка» стає щоденним питанням.

## 8. Дані для перевірки

На ПК-2 доступні реальні артефакти: `.xlsm`, VBA, PI AF, дампи SQL, золотий набір.
**Але звертатися до них треба рідко і в чітко визначених точках** — усе, що
потрібно для щоденної розробки, **вбудоване в markdown**:

| Що | Де | Коли йти до реального джерела |
|---|---|---|
| Структура шаблону, приклади рядків, формули | `06-tests.md`, фікстури в `02-contracts.md` | ніколи під час розробки |
| Синтетичний набір даних для навантаження | генератор `tools/Ecr.DataGen` | ніколи |
| Bootstrap із реального `.xlsm` | Етап 5, задача `bootstrap-verify` | **один раз** на етапі, за чек-листом |
| Звірка з еталоном | Етап 5, задача `golden-compare` | **один раз** на етапі |

Правило: **якщо задача не названа в `07-checkpoints.md` як така, що працює з
реальними даними, — не відкривай реальні дані.**
