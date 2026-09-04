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
| `Microsoft.EntityFrameworkCore` | 10.0.0 | MIT | ORM (`D-02`) |
| `Microsoft.EntityFrameworkCore.SqlServer` | 10.0.0 | MIT | провайдер |
| `Microsoft.EntityFrameworkCore.Design` | 10.0.0 | MIT | `dotnet ef migrations` |
| `Microsoft.EntityFrameworkCore.Relational` | 10.0.0 | MIT | транзитивно, фіксуємо явно |
| `Microsoft.Data.SqlClient` | 6.0.1 | MIT | `SqlBulkCopy`, raw SQL |
| `Microsoft.Extensions.Hosting` | 10.0.0 | MIT | host, DI, конфіг |
| `Microsoft.Extensions.Caching.Memory` | 10.0.0 | MIT | `IMemoryCache` |
| `Microsoft.Extensions.Caching.SqlServer` | 10.0.0 | MIT | `IDistributedCache` без Redis (`D-06`) |
| `Microsoft.AspNetCore.OpenApi` | 10.0.0 | MIT | генерація OpenAPI (`D-07`) |
| `Scalar.AspNetCore` | 2.0.0 | MIT | UI документації (`D-07`) |
| `Microsoft.AspNetCore.Authentication.Negotiate` | 10.0.0 | MIT | Windows-автентифікація |
| `NCalcSync` | 5.4.0 | MIT | обчислювач виразів (`D-19`) — **не парсер нашої мови** |
| `ClosedXML` | 0.104.2 | MIT | експорт/імпорт `.xlsx` |
| `Quartz` | 3.13.1 | Apache-2.0 | планувальник — **дефолтна реалізація** `IBackgroundJobScheduler` |
| `Quartz.Extensions.Hosting` | 3.13.1 | Apache-2.0 | інтеграція з host |
| ~~`Microsoft.CodeAnalysis.CSharp.Scripting`~~ | — | MIT | **не підключається** (`D-105`, `D-113`): рівень 2 у першому релізі не будується, залежність під нереалізовану функцію суперечить `D-12` |
| `System.IdentityModel.Tokens.Jwt` | — | — | ⛔ **не використовуємо** — автентифікація на cookie |

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
| `Testcontainers.MsSql` | 4.0.0 | MIT |
| `Microsoft.EntityFrameworkCore.Sqlite` | 10.0.0 | MIT |
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

> ⚠ **npm-екосистема дрейфує швидше за NuGet.** Очікуй більше `BOOTSTRAP-FIX`
> саме тут. Правило те саме: мажорну лінію не міняти, мінорну — вільно, кожну
> зміну записати.

## 5. Деградований режим (якщо чогось немає)

| Немає | Що робити | Наслідок |
|---|---|---|
| **Docker** | інтеграційні тести з трейтом `Category=Integration` не запускаються | партиціонування, `TRUNCATE PARTITIONS`, складені FK і RCSI **не перевірені** — записати в `questions.md` |
| **SQL Server** | те саме + `Ecr.Api` не стартує | unit-тести і тести виразів працюють; решта — ні |
| **Node.js** | Етап 6 (frontend) не виконується | backend не залежить від frontend |
| **Інтернет** | `restore` неможливий | зупинка, запис у `questions.md` |

**Тести за замовчуванням (`dotnet test`) мають бути зеленими без Docker.**
Усе, що потребує реального SQL Server, позначене трейтом
`[Trait("Category","Integration")]` і виключається фільтром за замовчуванням
(див. `09-commands.md`). Це не «пропуск тестів»: інтеграційні тести
запускаються окремою командою і є частиною Definition of Done тих етапів, де
вони оголошені.

## 6. Змінні оточення і конфігурація

Секрети **ніколи** не потрапляють у `appsettings.json` — лише імена секретів
(`D-11`, ФВ-6.11). Для локальної розробки — `dotnet user-secrets`.

| Змінна | Приклад | Навіщо |
|---|---|---|
| `ECR_ConnectionStrings__Ecr` | `Server=localhost;Database=Ecr;Trusted_Connection=True;TrustServerCertificate=True` | основна БД |
| `ECR_Database__EditionMode` | `Auto` \| `Standard` \| `Enterprise` | АРХ-7 |
| `ECR_Schema__StartupMode` | `Validate` (прод) \| `Migrate` (dev/test) | `B01` §6.3 |
| `ECR_Auth__CookieName` | `ecr.auth` | |
| `ECR_Auth__SlidingHours` | `8` | |
| `ECR_Jobs__Provider` | `Quartz` | реалізація порту |
| `ECR_Jobs__WorkerCount` | `4` | |
| `ECR_Cache__DistributedProvider` | `SqlServer` | без Redis |
| `ECR_ExternalSources__PiAf__SecretName` | `pi-af-service-account` | **лише ім'я секрету** |
| `ECR_Bootstrap__Password` | одноразовий пароль | **лише перший старт** (`D-115`): застосунок створює локального адміністратора з `MustChangePassword = 1`. Якщо запис уже існує — змінна ігнорується. Після першого входу її прибирають |
| `ECR_TEST_SQL` | `1` | вмикає інтеграційні тести проти локального SQL замість Testcontainers |

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
