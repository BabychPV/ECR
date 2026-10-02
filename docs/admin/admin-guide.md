# Посібник адміністратора ECR Web

Для адміністратора застосунку, тобто того, хто видає права й веде довідники.
Встановлення описано в `docs/build/11-install-guide.md`, експлуатація сервера — в
`docs/admin/operations-runbook.md`.

Кожне твердження тут перевірено по коду. Джерело вказано в дужках. Позначка
**⚠ потрібне рішення замовника** означає, що код цього факту не знає.

## 1. Ролі й права

Каталог прав і вбудовані ролі засіває `src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql`.
Сід виконується на **кожному** старті застосунку (`SeedRunner`, `StartupSequence.cs`) і лише
додає відсутні права, ролі й пари «роль–право». ⚠ Тому зняти право з вбудованої ролі не
вийде — на наступному старті воно повернеться; потрібен інший набір — створіть власну роль.
Виняток: прибрані з каталогу права (`Template.Migrate`, `Calculation.EditScript`,
`Report.MarkSubmitted`) сід видаляє разом із роздачами.

### 1.1. Вбудовані ролі

| Роль | Що має за замовчуванням |
|---|---|
| `SystemAdministrator` | усі права (`%`), **крім небезпечних** |
| `TemplateAdministrator` | `Template.*`, `Registry.View`, `Calculation.View`, `Document.View`, `System.ViewHealth` |
| `PeriodAdministrator` | `Period.*` (крім небезпечного `Period.Reopen`), `Project.Manage`, `Document.View`, `System.ViewHealth` |
| `DataEntry` | `Document.View/Create/Import/Export`, `Template.View`, `Registry.View`, `Calculation.View`, `Report.Export` |
| `Approver` | `Document.View/Export/Reopen`, `Registry.View`, `Calculation.View`, `Report.*` без небезпечних (`ViewRegulatory`, `ViewSnapshot`, `BuildSnapshot`, `Export`) і окремим рядком — небезпечне `Report.EditDefinition` (`D-203`) |
| `Viewer` | `Document.View`, `Registry.View`, `Calculation.View`, `Report.ViewRegulatory`, `Report.Export` |
| `Auditor` | `Document.View`, `Registry.View`, `Calculation.View`, `Template.View`, `Integration.View`, `Report.ViewRegulatory`, `Report.ViewSnapshot` (без `Report.Export`), `Security.ViewAudit`, `System.ViewHealth` |
| `ReportViewer` | `Report.ViewRegulatory`, `Report.ViewSnapshot`, `Report.Export` (секція `SEC:RPT` сіду). Без ресурсного гранта Read на проєкт не бачить нічого |
| `BootstrapAdministrator` | лише `Security.ManageUsers`, `Security.ManageRoles` |

⛔ Шаблонні роздачі сіду фільтруються `WHERE p.IsDangerous = 0` у самому `MERGE`.
Поіменні винятки — окремими `MERGE`: `BootstrapAdministrator` (`Security.ManageUsers`,
`Security.ManageRoles`) і `Approver` (`Report.EditDefinition`, `D-203`). Решту небезпечних
прав адміністратор дає, **створюючи роль** із ними (п. 2.3); у журналі безпеки — подія
`DangerousPermissionsGranted`.

### 1.2. Каталог прав

«Небезп.» — `IsDangerous = 1`. Колонка «Ролі» показує стан одразу після сіду.

| Право | Небезп. | Що відкриває | Ролі |
|---|---|---|---|
| `Template.View` | | перегляд шаблонів і версій | TemplateAdm, DataEntry, Auditor, SysAdm |
| `Template.Edit` | | редагування шаблонів, екран `/admin/templates` | TemplateAdm, SysAdm |
| `Template.Publish` | | публікація версії шаблону | TemplateAdm, SysAdm |
| `Registry.View` | | довідники, `/admin/registries` | TemplateAdm, DataEntry, Approver, Viewer, Auditor, SysAdm |
| `Registry.EditData` | | записи довідників | SysAdm |
| `Registry.EditDefinition` | | структура довідника (конструктор) | SysAdm |
| `Registry.Publish` | | публікація довідника | SysAdm |
| `Document.View` | | перегляд документів | TemplateAdm, PeriodAdm, DataEntry, Approver, Viewer, Auditor, SysAdm |
| `Document.Create` | | створення документа | DataEntry, SysAdm |
| `Document.Delete` | | видалення документа | SysAdm |
| `Document.Import` / `Document.Export` | | імпорт / експорт Excel | DataEntry (обидва), Approver (Export), SysAdm |
| `Document.Reopen` | | повторне відкриття поданого | Approver, SysAdm |
| `Document.ChangeKey` | так | зміна бізнес-ключа (номера справи) документа | — |
| `Project.Manage` | | проєкти | PeriodAdm, SysAdm |
| `Period.Configure` | | календар періодів, `/admin/periods` | PeriodAdm, SysAdm |
| `Period.Reopen` | так | повторне відкриття закритого періоду | — |
| `Calculation.View` | | методики, вирази, одиниці (`/admin/methodologies`, `/admin/expressions`, `/admin/units`) | TemplateAdm, DataEntry, Approver, Viewer, Auditor, SysAdm |
| `Calculation.EditFormula` / `EditConstant` / `EditRule` | | формули, константи, правила | SysAdm |
| `Calculation.ManageRequiredInputs` | | обов'язкові входи | SysAdm |
| `Calculation.Recalculate` | | проєктний/масовий перерахунок (перерахунок СВОГО документа кнопкою «Recalculate» — за читанням, `Document.View` + видимість документа). Виконавець без `Calculation.Recalculate` ставить перерахунок документа без витіснення: повторні запити зливаються з наявною задачею; витіснення доступне лише власникам `Calculation.Recalculate`. | SysAdm |
| `Calculation.Publish` | так | публікація версії методики | — |
| `Report.ViewRegulatory` | | регуляторні зрізи, `/admin/snapshots` | Approver, Viewer, ReportViewer, Auditor, SysAdm |
| `Report.ViewSnapshot` | | вміст зрізу: «View rows», книга `.xlsx` (разом із `Report.Export`); потрібен ще грант Read на проєкт | Approver, ReportViewer, Auditor, SysAdm |
| `Report.BuildSnapshot` | | побудова зрізу | Approver, SysAdm |
| `Report.Export` | | вивантаження звіту | DataEntry, Approver, Viewer, ReportViewer, SysAdm |
| `Report.EditDefinition` | так | авторство державної форми | Approver (явним рядком seed, `D-203`) |
| `Report.ViewCampaign` | так | огляд кампанії `/admin/campaign`: усі проєкти періоду без меж грантів | — |
| `Integration.View` | | джерела даних (лише перегляд) | Auditor, SysAdm |
| `Integration.Manage` | так | джерела, з'єднання, мапінг (`/admin/sources`, `/admin/mapping`) | — |
| `Integration.EditSchedule` | | розклад збору | SysAdm |
| `Uom.EditCatalog` | | нова одиниця виміру | SysAdm |
| `Security.ManageUsers` | так | користувачі | Bootstrap |
| `Security.ManageRoles` | так | ролі й роздачі, `/admin/security` | Bootstrap |
| `Security.ViewAudit` | | журнал аудиту, `/admin/audit` | Auditor, SysAdm |
| `Security.Simulate` | так | перегляд системи очима іншого користувача | — |
| `System.ViewHealth` | | стан, задачі, узгодженість (`/admin/health`, `/admin/jobs`, `/admin/consistency`) | TemplateAdm, PeriodAdm, Auditor, SysAdm |
| `System.RunJob` | так | ручний запуск системних задач, зокрема перевірки узгодженості | — |
| `System.ManageLocalization` | | рядки інтерфейсу, `/admin/ui-strings` | SysAdm |
| `System.ManageNotifications` | так | SMTP (панель «SMTP (outgoing mail)»), канали, правила й журнал доставок сповіщень, `/admin/notifications`; керує адресатами, паролем SMTP і секретами каналів | — |

⚠ Отже, **одразу після встановлення** `SystemAdministrator` не може: публікувати
методику, керувати джерелами даних і мапінгом, сповіщеннями, користувачами й
ролями, запускати системні задачі, повторно відкривати періоди. Ці права дає лише нова
роль, створена з ними (п. 2.3).

## 2. Користувачі

### 2.1. Типи облікових записів

| Тип | Вхід | Ендпоінт |
|---|---|---|
| Windows (домен) | Negotiate/Kerberos, без пароля в ECR | `POST /api/v1/login/windows` |
| Локальний | ім'я + пароль, політика паролів ECR | `POST /api/v1/login/local` |

Вхід Windows вимикається ключем `Auth:EnableNegotiate` (дефолт `true`).

### 2.2. Первинне налаштування (bootstrap)

1. На старті застосунок створює запис `bootstrap` (роль `BootstrapAdministrator`, вимога
   змінити пароль при першому вході), лише якщо **одночасно**: запису `bootstrap` ще немає,
   немає активного доменного адміністратора (п. 4) і пароль задано (`BootstrapAdmin.Decide`,
   `EnsureBootstrapAdminHandler`). Наявний запис не змінюється ніколи: повторний пароль його
   не скидає. Без пароля — лише попередження в лозі старту.
2. Пароль береться з файлу `%ProgramData%\ECR\config\bootstrap.secret`. Застосунок
   **читає і видаляє** цей файл на кожному старті, навіть якщо пароль не знадобився. Файл пише `tools/deploy-ecr.ps1 -BootstrapPassword`.
   Запасний шлях — змінна оточення `ECR_Bootstrap__Password`.
3. Увійдіть як `bootstrap` (логін/пароль) і змініть пароль. Створіть роль із
   `Security.ManageUsers`, `Security.ManageRoles` (і потрібними небезпечними правами) —
   `POST /api/v1/roles`. Доменний користувач з'являється в переліку після свого першого входу
   Windows (без ролей) або створюється `POST /api/v1/users`. Призначте йому роль **особисто**
   (`PUT /api/v1/users/{id}/roles`), не через групу.
4. Запис `bootstrap` вимикається (не видаляється; подія `BootstrapAdminDisabled`), щойно
   активному **доменному** (Windows) користувачу **особисто** призначено роль із
   `Security.ManageUsers` через `POST /users` або `PUT /users/{id}/roles`
   (`DisableBootstrapAdminHandler`, `UserStore.HasActiveDomainAdminAsync`). Роль, видана через
   групу AD, і локальний користувач його не вимикають.

⚠ **Пароль `bootstrap` не відновлюється на наявній базі.** Запис створюється лише раз, а
повторний `-BootstrapPassword` його не скидає (п. 1). Якщо доступ до `bootstrap` втрачено, а
доменного адміністратора ще немає, шляхів два: розгорнути **нову порожню базу**
(`deploy-ecr.ps1 -BootstrapPassword …` на новій БД — запис створиться заново) або, якщо дані
вже є, скинути пароль іншим носієм `Security.ManageUsers` (`POST /users/{id}/reset-password`).
Прямих SQL-правок `sec.[User]` не робіть: хеш — PBKDF2-HMAC-SHA512 (п. 2.4).

Технічний запис `svc-integration` створює сід. Його пароль випадковий і нікому не
відомий, ролей і грантів запис не має. Від його імені пише збір даних з AF.
**Не видаляйте і не вмикайте його для входу.**

### 2.3. Операції (екран `/admin/security`, право `Security.ManageUsers`/`ManageRoles`)

| Дія | Ендпоінт |
|---|---|
| Перелік / створення | `GET` / `POST /api/v1/users` |
| Ролі користувача | `GET` / `PUT /api/v1/users/{id}/roles` |
| Ролі: перелік, створення з набором прав, клон, перейменування, видалення | `GET`/`POST /api/v1/roles`, `POST …/roles/{id}/clone`, `PUT …/roles/{id}/code`, `DELETE …/roles/{id}` |
| Ресурсні гранти ролі (проєкт/аркуш/таблиця/колонка/довідник, рівень Read…Manage або заборона) | `GET`/`PUT …/roles/{id}/grants` (PUT — з `If-Match` = `ETag` із GET) |
| Каталог прав | `GET /api/v1/permissions` |
| Пояснення доступу користувача | `GET /api/v1/security/users/{id}/effective-access?resource=Project:{id}`, `GET …/security/users/{id}/groups` |
| Пошта й алерти користувача | `PUT …/users/{id}/email`, `PUT …/users/{id}/alerts` |
| Перегляд очима іншого (`Security.Simulate`) | `POST /api/v1/security/simulation` |
| Скидання пароля (локальний запис) | `POST /api/v1/users/{id}/reset-password` |
| Блокування / розблокування | `POST …/users/{id}/lock`, `…/unlock` |
| Зміна власного пароля | `POST /api/v1/auth/change-password` |

⚠ Набір прав наявної ролі не редагується; вбудовані ролі не перейменовуються й не
видаляються (`ECR-SEC-0409`, `roleBuiltIn`). Щоб дати небезпечне право, створіть нову роль
(`POST /roles` або клон і нова роль) з потрібним набором і призначте її. Без ресурсного гранта
роль не відкриває жодного проєкту.

Обмеження:

- **Останній адміністратор** (активний незаблокований носій `Security.ManageUsers`): його не
  можна заблокувати, скинути йому пароль чи зняти/замінити його ролі так, що право зникає
  (`PUT /users/{id}/roles`) — `ECR-SEC-0409` (`lastAdministrator`,
  `UserAdministrationHandlers.cs`). Перевірка іде в тій самій транзакції під замком; bootstrap
  не виняток; групові призначення не рахуються (членство чужих сесій невідоме), а відновлення,
  якщо адміністраторів усе ж не лишилось, — нова порожня БД за рішенням людини. Захищається
  носій права `Security.ManageUsers`, а не роль `SystemAdministrator`. Строкове (обмежене в
  часі) призначення права керування користувачами при захисті останнього адміністратора не
  враховується: у рідкісному разі система відмовить консервативно (409).
- Блокування безстрокове, з обов'язковою причиною; знімається лише `unlock`.
- Не можна заблокувати себе чи скинути собі пароль (`cannotTargetSelf`).
- Після скидання пароля користувач мусить змінити пароль при вході (`ECR-PWD-0428`).
  Доки він цього не зробить, інші запити не пройдуть.

### 2.4. Політика паролів і блокування

Одна політика `Default` (`sec.PasswordPolicy`), лише для локальних записів. Діють
(`PasswordPolicyCheck.cs`): `MinLength` (дефолт 12), стеля 256 символів, заборона містити
ім'я входу (від 3 символів), заборона поширених паролів (вбудований список), новий ≠ чинний.
`RequireUpper`/`RequireDigit`/`RequireSpecial` і `ExpirationDays` у таблиці є, але код їх
**не застосовує** (`P-1`). Відмова — `ECR-PWD-0422` з причиною. Після `MaxFailedAttempts`
(дефолт 5) невдалих входів локальний запис блокується на `LockoutMinutes` (дефолт 15) —
`ECR-AUTH-0423`. Екрана й API для політики немає: змінити її можна лише SQL-оновленням
`sec.PasswordPolicy` (DBA). Хеш — PBKDF2-HMAC-SHA512, 200 000 ітерацій (`D-218`); старіші
хеші перехешовуються при вході.

Обмеження частоти: вхід (`/api/v1/login/*`) — `Security:RateLimit:LoginPermitPerMinute`
(дефолт 60 на IP-адресу); зміна пароля — `Security:RateLimit:ChangePasswordPermitPerMinute`
(дефолт 10 на користувача).

### 2.5. Групи AD

Ролі можна видати доменній групі: `GET`/`POST`/`DELETE
/api/v1/security/group-assignments` (`GroupAssignmentsController.cs`), право
`Security.ManageUsers`. `principal` — SID або `ДОМЕН\Група`; можна задати строк дії
(`validFrom`/`validTo`) і область (проєкти). Роль із небезпечними правами — лише з
`confirmDangerous: true`, інакше `409 ECR-SEC-0409`. Нова група діє для вже залогінених членів
з наступного входу; відкликання — негайно. ⚠ Роль із `Security.ManageUsers`, видана групою,
не вимикає `bootstrap` і не рахується як «останній адміністратор».
Користувач бачить власні групи на `/my-groups`. Цей екран доступний без прав, щоб
людина з порожніми екранами бачила причину.

⚠ **потрібне рішення замовника:** які домен і групи AD відповідають яким ролям.
Дефолту в коді немає.

## 3. Шаблони, довідники, методики

| Об'єкт | Екран | Редагування | Публікація |
|---|---|---|---|
| Шаблони форм | `/admin/templates` | `Template.Edit` | `Template.Publish` |
| Довідники | `/admin/registries` | `Registry.EditData`, `Registry.EditDefinition` | `Registry.Publish` |
| Методики | `/admin/methodologies` | `Calculation.Edit*` | `Calculation.Publish` (небезп.) |
| Вирази | `/admin/expressions` | `Calculation.EditFormula` | — |
| Одиниці | `/admin/units` | `Uom.EditCatalog` | — |

**Чотири очі** діють **лише для версій методик**. Автор версії не може її
опублікувати: помилка `ECR-CALC-0409` (`MethodologyVersion.cs`). Те саме тримає
обмеження БД `CK_MV_FourEyes`. Отже, для публікації методики потрібні двоє людей:
автор і носій `Calculation.Publish`. Для шаблонів і довідників правила чотирьох
очей у коді немає.

Перенос на нову версію шаблону існує (ФВ-7.5): `GET`/`POST`
`/api/v1/documents/{id}/migrate-version`. Окремого права немає — потрібне
`Template.Edit` (`Template.Migrate` прибрано з каталогу, сід видаляє його з
наявних баз). Версія шаблону живе на проєкті, тож переноситься весь проєкт
разом. Режими: `Safe` і `Presentation`; `dryRun` дає сухий прогін зі звітом.
Відмови: `ECR-SCHM-0422` (втрата даних чи заборонена зміна), `ECR-DOC-0409`
(подані/затверджені аркуші або архівний проєкт).

Відоме обмеження переносу версії (`POST /documents/{id}/migrate-version`): перенос проєкту на нову версію шаблону блокується (422 `ECR-SCHM-0422`, `migrateGrantsNotMapped`), якщо на аркуші, таблиці чи колонці вихідної версії стоїть заборона (deny), якої немає за кодом у новій версії: адміністратор спершу знімає заборону. Список скопійованих/не змаплених грантів у dry-run не показується (планується).

## 4. Періоди

Екран `/admin/periods`, право `Period.Configure`. Сід засіває політику
`ECR-Standard`:

| Зсув (днів) | Значення |
|---|---|
| `OpenOffsetDays` | 0 |
| `GraceOffsetDays` | 15 |
| `HardCloseOffsetDays` | 45 |
| `YearGraceOffsetDays` | 45 |

Стан періодів перераховує задача `PeriodStateJob` щогодини (о хх:05) і один раз
на старті. Повторне відкриття закритого періоду потребує `Period.Reopen`
(небезп.).

⚠ **потрібне рішення замовника:** чи відповідають ці зсуви регламенту
підприємства. Дефолт — наведені числа.

## 5. Джерела даних і мапінг

| Екран | Право |
|---|---|
| `/admin/sources` | `Integration.View` або `Integration.Manage` — перегляд з'єднань, сутностей, журналу прогонів і подій покриття; лише `Integration.Manage` — дії (збір, правка, вкладки «Сутності» і «Події з PI»); вкладка «Розклад» — `Integration.EditSchedule` |
| `/admin/mapping` | `Integration.Manage` |

Стан джерел видно в `/health/ready` (runbook, п. 3).

Секрети з'єднань читаються з конфігурації за іменем `Secrets:<ім'я>`, наприклад
`Secrets:PiAf.Primary` (`ConfigurationSecretProvider.cs`). Задавайте їх змінними
оточення служби (`ECR_Secrets__PiAf.Primary`), не у файлі.

⚠ **потрібне рішення замовника:** адреса й спосіб автентифікації PI Web API / PI
AF (C-3), обліковий запис для з'єднання.

### 5.1. Розклади збору

Розклад належить **сутності джерела**, не з'єднанню: одна сутність — один розклад
(`ext.CollectionSchedule`, унікальний `SourceEntityId`; другий — `409 ECR-JOB-0409`
`collectionScheduleExists`). Право — `Integration.EditSchedule` (типово SysAdm); воно
потрібне навіть для перегляду переліку.

**Де:** `/admin/sources` → шухляда з'єднання → вкладка «Розклад» (перелік розкладів цього
з'єднання з колонкою «Залежить від» і форма для вибраної сутності). Без
`Integration.EditSchedule` вкладка показує відмову 403.

**API** (`CollectionSchedulesController.cs`): `GET /api/v1/collection-schedules?dataSource=<код>`;
`POST` (`sourceEntityId`, `cron`, `isEnabled`, `lookbackDays`, `dependsOnScheduleId`) — 201;
`PUT /{id}` (`cron`, `isEnabled`, `lookbackDays`, `dependsOnScheduleId`, `clearDependency`) і
`DELETE /{id}` — обидва з обов'язковим `If-Match: <rowVersion>`. Без нього — 422
(`collectionScheduleIfMatch`), із застарілим — `409 ECR-JOB-0409` (`collectionScheduleChanged`).

**Формат cron — Quartz, 6–7 полів:** `секунди хвилини години день-місяця місяць день-тижня [рік]`,
рівно одне з полів дня — `?`. Приклади: `0 0 * * * ?` — щогодини о хх:00; `0 30 1 * * ?` —
щодня о 01:30. П'ятипольний Unix-cron недійсний. Довжина ≤ 100 символів. Час — локальний
час ОС сервера застосунку, не пояс проєкту (runbook, п. 4).

**Вікно перекриття** `lookbackDays` — 1…366, типово 7: кожен прогін перечитує
`[зараз − N діб; зараз]`; ідемпотентність за природним ключем не дає подвоїти точки.
Пропущені інтервали до 45 діб збирає наздоганяння. Один прогін обмежено 15 хв: що не
встигли прочитати, піде в наздоганяння.

**Застосування:** правка й вмикання/вимикання діють одразу, без перезапуску (на вузлі, що
прийняв запит; про кілька вузлів — нижче). Якщо планувальник не прийняв розклад, він усе
одно збережений, а відповідь — 422 `collectionScheduleNotApplied` з причиною в `LastError`.
Недійсний cron з API не зберігається (422 `collectionScheduleCron` / `…CronLength`). Рядок
із недійсним cron, що потрапив у базу в обхід API, на старті пропускається з помилкою в лозі
й у `LastError` (на вкладці — «не поставлено»). Розклад для власної форми ECR
(`SourceKind = Local`) заборонено (422 `scheduleForLocalEntity`). У планувальник ставиться
не більше 1000 увімкнених розкладів, решта — з помилкою в лозі. Усі зміни пишуться в журнал
аудиту інтеграції (старий/новий cron, вмикання, вікно, залежність).

⚠ **Кілька вузлів `EcrApi`:** розклади живуть у пам'яті кожного процесу, тож правка,
вимкнення чи видалення розкладу доходить до інших вузлів лише після їх перезапуску (висновок
із коду, `CollectionScheduleApplier`; на стенді не відтворено). Після зміни розкладів
перезапустіть `EcrApi` на решті вузлів.

**Відмови збору:** недоступне джерело чи перевищення 15 хв — прогін `Degraded`, задача
успішна, діапазон іде в наздоганяння. `401`/`403` — задача `Failed` і негайний алерт.
Транзієнтні відмови (`ECR-INT-0503`) повторюються до 3 разів (30 с, 60 с, 120 с). Журнал
прогонів і події покриття — `/admin/sources` (`GET /api/v1/collection-runs`,
`…/coverage-events`).

### 5.2. Залежності розкладів (ФВ-13.15)

Розклад може залежати від іншого розкладу **того самого з'єднання**: поле «Залежить від
розкладу» у формі, в API — `dependsOnScheduleId` (зняти — `clearDependency: true` або
порожній вибір у формі).

**Перевірки при збереженні** (усі — 422 `ECR-REQ-0422`): розкладу-залежності не існує
(`collectionScheduleDependencyNotFound`); він належить іншому з'єднанню
(`collectionScheduleDependencyOtherSource`); залежність замикає цикл, зокрема сам на себе
(`collectionScheduleDependencyCycle`; ланцюг довший за 50 ланок теж вважається циклом).

**Як працює** (`CollectionSchedule.IsDependencyMet`, `CollectionJob.cs`). Перед **плановим**
запуском (тик cron) збір порівнює час останнього прогону свого розкладу і розкладу-залежності:

- залежність відпрацювала не раніше, ніж цей розклад востаннє, — збір іде;
- інакше запуск **пропускається**: задача в `/admin/jobs` завершується успішно з текстом
  «пропущено, чекає успішного прогону розкладу N», а в журналі подій покриття з'являється
  подія `SkippedDependency` («Очікує залежності», не частіше разу на годину на розклад);
- наступний тик cron перевіряє знову. Пропуск — затримка, не втрата: вікно перекриття й
  наздоганяння доберуть пропущене.

**Не блокує ніколи:** ручний збір кнопкою «Зібрати»; запуск із явним вікном (наздоганяння,
`fromUtc`/`toUtc`); розклад-залежність вимкнений, ще жодного разу не бігав або не бігав
понад **48 годин**; сам цей розклад ще жодного разу не бігав.

**«Успішний прогін» залежності** — будь-який, що завершився без помилки задачі, зокрема
`Degraded` (джерело недоступне чи 15-хвилинний ліміт) і ручний. Прогін `Failed` (відмова
автентифікації `401`/`403`, правило даних, збій сховища, скасування) час останнього прогону
**не** оновлює: залежний розклад чекає, доки залежність не відпрацює, але не довше 48 годин
від її останнього успіху.

**Видалення** розкладу, від якого хтось залежить, дозволене: у залежних залежність
знімається автоматично (без окремого запису в журнал аудиту інтеграції).

**Порада** (висновок із коду, не вимога ТЗ): ставте cron залежного розкладу пізніше за cron
залежності із запасом, не меншим за тривалість її прогону (до 15 хв). Порівнюється **час
старту** прогонів, тож за однакового cron порядок випадковий: залежний може піти зі старими
даними залежності або пропустити тик.

## 6. Сповіщення

Екран `/admin/notifications`, право `System.ManageNotifications` (небезп.).
Покроково — [`notifications-runbook.md`](notifications-runbook.md).
Ендпоінти: `api/v1/notifications/smtp` (`GET`, `PUT`, `POST test`),
`api/v1/notifications/channels` (`GET`, `POST`, `PUT {id}`, `DELETE {id}`, `PUT {id}/secret`,
`POST {id}/test`), `…/rules` (`GET`, `PUT`), `…/deliveries` (`GET`). Пароль SMTP і секрети каналів
шифруються Data Protection, тож без ключів (runbook, п. 6) їх не розшифрувати.

✎ 2026-10-01 (`D-263`): SMTP і правила налаштовуються в застосунку.

| Що | Де задається |
|---|---|
| Пошта (транспорт) | панель «SMTP (outgoing mail)»: сервер, порт (587), STARTTLS або без шифрування, відправник, логін і пароль (write-only). Поки вона вимкнена чи неповна — запасний шлях `Smtp:*` конфігурації процесу |
| Поштовий канал | адресати: явні адреси та/або ролі (активні користувачі ролі з поштою, кожному — його мовою) |
| Teams / вебхук | URL, чий хост закінчується на один із суфіксів `Notifications:WebhookAllowedHostSuffixes` |
| Правила | матриця «подія × канал» з межею серйозності |
| Шаблони листів | панель «Message templates» (право `System.ManageLocalization`) |
| Алерти адміністраторам (`ReceivesAlerts`) | перемикач «Alerts» користувача на `/admin/security` (право `Security.ManageUsers`; лише з поштою, очищення пошти його вимикає) — щогодинне зведення збоїв і алерт про відмову автентифікації джерела (`notifications-runbook.md` п. 6) |
| Межа проб | 5/хв на користувача, 30/год на систему (`Security:RateLimit:SmtpTest*`, `notifications-runbook.md` п. 2.5) |

Розсилку виконує `NotificationJob` щогодини (о хх:05); нагадування про відкриття періоду
(`Scheduled → Open`) і про початок пільгового строку (`Open → Grace`) — `PeriodStateJob`
(щогодини о хх:05 і один раз на старті).

⚠ **потрібне від замовника:** дані поштового сервера (адреса, порт, обліковий
запис, дозвіл релею для хоста застосунку).

## 7. Рядки інтерфейсу й переклади

Екран `/admin/ui-strings`, право `System.ManageLocalization`. API:
`GET /api/v1/ui-strings/coverage`, `GET …/{lang}`, `PUT …/{lang}/{key}`. Рядки
редагуються по одному.

⚠ Імпорту чи експорту CSV для рядків інтерфейсу в коді **немає**. Масова заливка
можлива лише через `09-seed.sql` при оновленні версії.

## 8. Журнал аудиту

Екран `/admin/audit`, право `Security.ViewAudit`. Експорт змін структури у CSV:
`GET /api/v1/audit/structure/export.csv`. Кількість рядків обмежує
`Audit:ExportMaxRows` (100000). Щоб отримати більше, звужуйте фільтр.

⚠ Екран і експорт CSV показують лише «гарячий» аудит (`aud.*`). Записи, старші за 24
місяці, нічна задача переносить в `arc.Audit*` (runbook, п. 7.3): тут їх не видно, читати —
SQL-запитом DBA.

## 9. Фонові задачі

Екран `/admin/jobs`. Задачі зберігаються в `itg.JobProgress`.

| Дія | Ендпоінт | Хто може |
|---|---|---|
| Перелік | `GET /api/v1/jobs?state&code&mine&limit` | `System.ViewHealth`; із `mine=true` — будь-хто, для своїх |
| Одна задача | `GET /api/v1/jobs/{jobId}` | те саме |
| Повтор | `POST /api/v1/jobs/{jobId}/restart` (202) | `System.ViewHealth` або автор задачі |
| Скасування | `POST /api/v1/jobs/{jobId}/cancel` (202) | те саме |

Ідентифікатор задачі має вигляд `IRecalculationJob-<guid>` або, для задачі на конкретну
ціль, `IRecalculationJob~<ціль>~<guid>`; тик розкладу — `<Тип>:<відбиток>`
(`QuartzJobScheduler.cs`). Символи `-`, `~`, `:` у сегменті шляху URL кодування не
потребують. Лише рядки старого формату з `#` треба кодувати як `%23`.

`state` — `Queued|Running|Succeeded|Failed|Cancelled` (невідомий — 422), `limit` — 1…50;
без `mine=true` і без права — 403, а не порожній перелік. Перезапуск — лише проваленої
задачі, той самий `jobId`.

Розклади задач наведено в runbook, п. 4.

## 10. Узгодженість даних

Екран `/admin/consistency`, право `System.ViewHealth`: знахідки
`GET /api/v1/consistency/issues` (таблиця `aud.ConsistencyIssue`). Перевірка
запускається щоночі о 02:15 (`ConsistencyCheckJob`). Вручну —
`POST /api/v1/consistency/run`, право `System.RunJob` (небезп.).

## 11. Стан системи

Екран `/admin/health`, право `System.ViewHealth`. Джерела даних: `/health/ready` (сім
карток: `db`, `jobs`, `sources`, `worker`, `reportviews`, `tzdata`, `transport`; оновлення
раз на 30 с), `/health/db` (будь-який автентифікований користувач: редакція SQL Server,
режим, RCSI, файлові групи, запас партицій, обмеження режиму) і `/api/v1/health/facts`
(версія, час старту, середовище, транспорт сповіщень, тека журналу; право
`System.ViewHealth`). Скрипт додавання
партицій: `GET /api/v1/health/partitions/script` (виконує DBA; процедура заодно продовжує
межі аудиту на 12 міс., але горизонт аудиту картка не показує — runbook, п. 7.4). Значення станів пояснено в
runbook, п. 3.
