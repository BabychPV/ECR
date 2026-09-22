# Посібник адміністратора ECR Web

Для адміністратора застосунку, тобто того, хто видає права й веде довідники.
Встановлення описано в `docs/build/11-install-guide.md`, експлуатація сервера — в
`docs/admin/operations-runbook.md`.

Кожне твердження тут перевірено по коду. Джерело вказано в дужках. Позначка
**⚠ потрібне рішення замовника** означає, що код цього факту не знає.

## 1. Ролі й права

Каталог прав і вбудовані ролі засіває `src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql`.
Сід лише **додає** права й ролі, наявних роздач не чіпає.

### 1.1. Вбудовані ролі

| Роль | Що має за замовчуванням |
|---|---|
| `SystemAdministrator` | усі права (`%`), **крім небезпечних** |
| `TemplateAdministrator` | `Template.*`, `Registry.View`, `Calculation.View`, `Document.View`, `System.ViewHealth` |
| `PeriodAdministrator` | `Period.*` (крім небезпечного `Period.Reopen`), `Project.Manage`, `Document.View`, `System.ViewHealth` |
| `DataEntry` | `Document.View/Create/Import/Export`, `Template.View`, `Registry.View`, `Calculation.View`, `Report.Export` |
| `Approver` | `Document.View/Export/Reopen`, `Registry.View`, `Calculation.View`, `Report.*` (крім небезпечних) |
| `Viewer` | `Document.View`, `Registry.View`, `Calculation.View`, `Report.ViewRegulatory`, `Report.Export` |
| `Auditor` | `Document.View`, `Registry.View`, `Calculation.View`, `Template.View`, `Integration.View`, `Report.ViewRegulatory`, `Security.ViewAudit`, `System.ViewHealth` |
| `BootstrapAdministrator` | лише `Security.ManageUsers`, `Security.ManageRoles` |

⛔ Сід не видає **небезпечних** прав (`IsDangerous = 1`) жодній ролі, крім
`BootstrapAdministrator`. Фільтр `WHERE p.IsDangerous = 0` стоїть у самому
`MERGE`. Ці права адміністратор видає окремою дією, і в журналі безпеки видно, хто
це зробив (`DangerousPermissionsGranted`).

### 1.2. Каталог прав

«Небезп.» — `IsDangerous = 1`. Колонка «Ролі» показує стан одразу після сіду.

| Право | Небезп. | Що відкриває | Ролі |
|---|---|---|---|
| `Template.View` | | перегляд шаблонів і версій | TemplateAdm, DataEntry, Auditor, SysAdm |
| `Template.Edit` | | редагування шаблонів, екран `/admin/templates` | TemplateAdm, SysAdm |
| `Template.Publish` | | публікація версії шаблону | TemplateAdm, SysAdm |
| `Registry.View` | | довідники, `/admin/registries` | усі, крім PeriodAdm |
| `Registry.EditData` | | записи довідників | SysAdm |
| `Registry.EditDefinition` | | структура довідника (конструктор) | SysAdm |
| `Registry.Publish` | | публікація довідника | SysAdm |
| `Document.View` | | перегляд документів | усі, крім Bootstrap |
| `Document.Create` | | створення документа | DataEntry, SysAdm |
| `Document.Delete` | | видалення документа | SysAdm |
| `Document.Import` / `Document.Export` | | імпорт / експорт Excel | DataEntry (обидва), Approver (Export), SysAdm |
| `Document.Reopen` | | повторне відкриття поданого | Approver, SysAdm |
| `Document.ChangeKey` | так | зміна бізнес-ключа (номера справи) документа | — |
| `Project.Manage` | | проєкти | PeriodAdm, SysAdm |
| `Period.Configure` | | календар періодів, `/admin/periods` | PeriodAdm, SysAdm |
| `Period.Reopen` | так | повторне відкриття закритого періоду | — |
| `Calculation.View` | | методики, вирази, одиниці (`/admin/methodologies`, `/admin/expressions`, `/admin/units`) | усі, крім PeriodAdm |
| `Calculation.EditFormula` / `EditConstant` / `EditRule` | | формули, константи, правила | SysAdm |
| `Calculation.ManageRequiredInputs` | | обов'язкові входи | SysAdm |
| `Calculation.Recalculate` | | перерахунок | SysAdm |
| `Calculation.Publish` | так | публікація версії методики | — |
| `Report.ViewRegulatory` | | регуляторні зрізи, `/admin/snapshots` | Approver, Viewer, Auditor, SysAdm |
| `Report.BuildSnapshot` | | побудова зрізу | Approver, SysAdm |
| `Report.Export` | | вивантаження звіту | DataEntry, Approver, Viewer, SysAdm |
| `Report.EditDefinition` | так | авторство державної форми | — |
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
| `System.ManageNotifications` | так | канали й правила сповіщень, `/admin/notifications`; керує адресатами й секретами | — |

⚠ Отже, **одразу після встановлення** `SystemAdministrator` не може: публікувати
методику, керувати джерелами даних і мапінгом, сповіщеннями, користувачами й
ролями, запускати системні задачі, повторно відкривати періоди. Ці права треба
видати явно (п. 2.3).

## 2. Користувачі

### 2.1. Типи облікових записів

| Тип | Вхід | Ендпоінт |
|---|---|---|
| Windows (домен) | Negotiate/Kerberos, без пароля в ECR | `POST /api/v1/login/windows` |
| Локальний | ім'я + пароль, політика паролів ECR | `POST /api/v1/login/local` |

Вхід Windows вимикається ключем `Auth:EnableNegotiate` (дефолт `true`).

### 2.2. Первинне налаштування (bootstrap)

1. Під час першого старту застосунок створює запис `bootstrap` із роллю
   `BootstrapAdministrator` (`StartupSequence.cs`, `EnsureBootstrapAdminHandler`).
2. Пароль береться з файлу `%ProgramData%\ECR\config\bootstrap.secret`. Застосунок
   **читає і видаляє** цей файл. Файл пише `tools/deploy-ecr.ps1 -BootstrapPassword`.
   Запасний шлях — змінна оточення `ECR_Bootstrap__Password`.
3. Увійдіть як `bootstrap` і видайте доменному адміністратору ролі й
   `Security.ManageUsers`/`Security.ManageRoles`.
4. Запис `bootstrap` вимикається, щойно доменний користувач отримує
   `Security.ManageUsers` (`deploy-ecr.ps1`).

Технічний запис `svc-integration` створює сід. Його пароль випадковий і нікому не
відомий, ролей і грантів запис не має. Від його імені пише збір даних з AF.
**Не видаляйте і не вмикайте його для входу.**

### 2.3. Операції (екран `/admin/security`, право `Security.ManageUsers`/`ManageRoles`)

| Дія | Ендпоінт |
|---|---|
| Перелік / створення | `GET` / `POST /api/v1/users` |
| Ролі користувача | `GET` / `PUT /api/v1/users/{id}/roles` |
| Ролі: створення, клон, права ролі | `POST /api/v1/roles`, `POST …/roles/{id}/clone`, `GET`/`PUT …/roles/{id}/grants` |
| Перегляд очима іншого (`Security.Simulate`) | `POST /api/v1/security/simulation` |
| Скидання пароля (локальний запис) | `POST /api/v1/users/{id}/reset-password` |
| Блокування / розблокування | `POST …/users/{id}/lock`, `…/unlock` |
| Зміна власного пароля | `POST /api/v1/auth/change-password` |

Обмеження:

- **Останній адміністратор.** Не можна заблокувати чи позбавити ролі останнього
  адміністратора — помилка `ECR-SEC-0409` (`UserAdministrationHandlers.cs`,
  `EnsureNotLastAdministratorAsync`).
- Не можна заблокувати себе чи скинути собі пароль (`cannotTargetSelf`).
- Після скидання пароля користувач мусить змінити пароль при вході (`ECR-PWD-0428`).
  Доки він цього не зробить, інші запити не пройдуть.

### 2.4. Політика паролів і блокування

Одна політика `Default` (`sec.PasswordPolicy`). Поля: `MinLength`,
`MaxFailedAttempts`, `LockoutMinutes`, `RequireUpper`, `RequireDigit`,
`RequireSpecial`. Коли вичерпано `MaxFailedAttempts`, запис блокується на
`LockoutMinutes` (`ECR-AUTH-0423`). Пароль, що не відповідає політиці, дає
`ECR-PWD-0422`.

Незалежно від політики працює обмеження частоти входу:
`Security:RateLimit:LoginPermitPerMinute` (дефолт 60).

### 2.5. Групи AD

Ролі можна видати доменній групі: `GET`/`POST`/`DELETE
/api/v1/security/group-assignments` (`GroupAssignmentsController.cs`).
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

Документ назавжди лишається на тій версії шаблону, з якою його створили. Права
`Template.Migrate` немає, і сід прибирає його з наявних баз.

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
| `/admin/sources` | `Integration.View` (перегляд) або `Integration.Manage` (дії, таблиця сутностей збору) |
| `/admin/mapping` | `Integration.Manage` |

Розклад збору задається cron-виразом джерела (`ext.CollectionSchedule`, право
`Integration.EditSchedule`). Недійсний cron потрапляє в лог і пропускається, а
джерело не збирає даних. Стан джерел видно в `/health/ready` (runbook, п. 3).

Секрети з'єднань читаються з конфігурації за іменем `Secrets:<ім'я>`, наприклад
`Secrets:PiAf.Primary` (`ConfigurationSecretProvider.cs`). Задавайте їх змінними
оточення служби (`ECR_Secrets__PiAf.Primary`), не у файлі.

⚠ **потрібне рішення замовника:** адреса й спосіб автентифікації PI Web API / PI
AF (C-3), обліковий запис для з'єднання.

## 6. Сповіщення

Екран `/admin/notifications`, право `System.ManageNotifications` (небезп.).
Ендпоінти: `api/v1/notifications/channels` (`PUT`, `DELETE`, `PUT {id}/secret`,
`POST {id}/test`), `…/rules`, `…/deliveries`. Секрети каналів шифруються Data
Protection, тож без ключів (runbook, п. 6) їх не розшифрувати.

| Канал | Що потрібно |
|---|---|
| Пошта | транспорт процесу: `Smtp:Host`, `Smtp:From`, `Smtp:Port` (587), `Smtp:UseStartTls` (true), `Smtp:User` + `Smtp:SecretName`. Без `Smtp:User` — інтегрована або анонімна відправка |
| Teams / вебхук | URL, чий хост закінчується на один із суфіксів `Notifications:WebhookAllowedHostSuffixes` |

Розсилку виконує `NotificationJob` щогодини (о хх:05).

⚠ **потрібне рішення замовника:** SMTP-сервер, адреса відправника, адресати.

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

## 9. Фонові задачі

Екран `/admin/jobs`. Задачі зберігаються в `itg.JobProgress`.

| Дія | Ендпоінт | Хто може |
|---|---|---|
| Перелік | `GET /api/v1/jobs?state&code&mine&limit` | `System.ViewHealth`; із `mine=true` — будь-хто, для своїх |
| Одна задача | `GET /api/v1/jobs/{jobId}` | те саме |
| Повтор | `POST /api/v1/jobs/{jobId}/restart` (202) | `System.ViewHealth` або автор задачі |
| Скасування | `POST /api/v1/jobs/{jobId}/cancel` (202) | те саме |

⚠ Ідентифікатор задачі містить `#` (`IRecalculationJob#42`). У URL його треба
кодувати: `%23`. Інакше буде `404`.

Розклади задач наведено в runbook, п. 4.

## 10. Узгодженість даних

Екран `/admin/consistency`, право `System.ViewHealth`: знахідки
`GET /api/v1/consistency/issues` (таблиця `aud.ConsistencyIssue`). Перевірка
запускається щоночі о 02:15 (`ConsistencyCheckJob`). Вручну —
`POST /api/v1/consistency/run`, право `System.RunJob` (небезп.).

## 11. Стан системи

Екран `/admin/health`, право `System.ViewHealth`. Джерела даних:
`/api/v1/health/facts` і `/health/db` (лише для автентифікованих). Там видно
редакцію SQL Server, RCSI, файлові групи й запас партицій. Скрипт додавання
партицій: `GET /api/v1/health/partitions/script`. Значення станів пояснено в
runbook, п. 3.
