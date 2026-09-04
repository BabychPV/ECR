# Модель безпеки: автентифікація, налаштовувані ролі та права

> Вимоги:
> * адміністратор створює ролі з доступом до певного функціоналу (аркуш, таблиця тощо)
>   і призначає роль користувачу; має бути поняття «тільки для читання»;
> * окрім Windows-автентифікації — **внутрішня**: створили користувача з паролем,
>   дали ролі, він працює.

Ключові принципи:
1. **Ролі — це дані, а не enum у коді.**
2. **Автентифікація і авторизація розділені.** Як користувач увійшов —
   не впливає на те, що він може робити.

---

## 1. Автентифікація: два провайдери ⭐

### 1.1 Схема

```
                    ┌──────────────────────────┐
                    │   Сторінка входу         │
                    └────────┬─────────────────┘
              ┌──────────────┴───────────────┐
              ▼                              ▼
   ┌──────────────────────┐      ┌───────────────────────────┐
   │ «Увійти через Windows»│      │ Логін + пароль            │
   │ Negotiate / Kerberos  │      │ (локальний обліковий запис)│
   │ SSO без форми         │      │                           │
   └──────────┬───────────┘      └───────────┬───────────────┘
              │                              │
              └──────────────┬───────────────┘
                             ▼
              ┌──────────────────────────────────┐
              │  ЄДИНА ідентичність застосунку   │
              │  (cookie / JWT з тими самими      │
              │   claims: sid, roles, scopes)     │
              └──────────────┬───────────────────┘
                             ▼
                    Уся авторизація нижче
                    працює однаково
```

**Ключове рішення:** обидва шляхи входу випускають **один і той самий**
токен застосунку. Уся авторизація нижче не знає і не має знати,
яким провайдером користувач автентифікувався.

### 1.2 Реалізація в ASP.NET Core

```csharp
services.AddAuthentication(options =>
{
    options.DefaultScheme          = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.Cookie.HttpOnly     = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite     = SameSiteMode.Lax;
    options.ExpireTimeSpan      = TimeSpan.FromHours(8);
    options.SlidingExpiration   = true;
    options.LoginPath           = "/login";
})
.AddNegotiate();   // Kerberos / NTLM — вмикається лише на endpoint /login/windows
```

* `/login/windows` — вимагає схему `Negotiate`; після успіху шукає `sec.User`
  за SID, створює claims і **підписує ту саму cookie**;
* `/login/local` — форма; перевіряє пароль, ті самі claims, та сама cookie;
* усі інші endpoint'и вимагають лише cookie і не знають про провайдера.

### 1.3 Локальні облікові записи — вимоги

**АУТ-1** Адміністратор (право `Security.ManageUsers`) створює локального
користувача: логін, ПІБ, email, початковий пароль або запрошення.

**АУТ-2** Політика паролів (налаштовується в UI, не в коді):
мінімальна довжина, вимоги до складності, історія (заборона повторення N останніх),
строк дії, обов'язкова зміна при першому вході.

**АУТ-3** Зберігання пароля — **ASP.NET Core `PasswordHasher<T>`**
(PBKDF2-HMAC-SHA256, ≥100 000 ітерацій, унікальна сіль) або **Argon2id**.
Пароль ніколи не логується, не потрапляє в аудит і не повертається API.

**АУТ-4** Захист від підбору: блокування після N невдалих спроб
(`LockoutFailedCount`, `LockoutEndsAt`), експоненційна затримка,
однакова відповідь на «немає такого користувача» і «невірний пароль»
(щоб не давати перелічувати облікові записи).

**АУТ-5** Життєвий цикл: `Invited` -> `Active` -> `Locked` / `Disabled`.
Видалення — тільки `Disabled` (SID/Id залишається, бо на нього посилається аудит).

**АУТ-6** Скидання пароля: адміністратором (видає тимчасовий пароль
з обов'язковою зміною) і самостійно через email — **якщо в контурі є SMTP**;
інакше лише адміністратором.

**АУТ-7** Опційно — **2FA (TOTP)** для локальних облікових записів.
Рекомендовано зробити обов'язковою для ролей з правами групи `Security`.

**АУТ-8** `SecurityStamp`: зміна пароля, ролей або блокування
**негайно інвалідує всі активні сесії** користувача.

**АУТ-9** Аудит подій входу: успіх, невдача (з причиною), блокування,
зміна пароля, скидання, вихід. Окрема таблиця, append-only.

**АУТ-10** Сесії: перелік активних сесій користувача, примусове завершення
адміністратором.

### 1.4 Порівняння провайдерів

| | Windows (Negotiate) | Локальний |
|---|---|---|
| Для кого | співробітники в домені | підрядники, зовнішні, сервісні облікові записи |
| Вхід | SSO, без форми | логін + пароль |
| Джерело груп | AD | тільки прямі призначення ролей |
| Керування паролем | AD | у системі |
| 2FA | політиками домену | вбудована (TOTP) |
| **Делегування до PI AF** | можливе (Kerberos S4U2Proxy) | **неможливе** |

### 1.5 ⚠ Наслідок для інтеграції з PI AF

Локальний користувач **не має Windows-ідентичності**, тому запит до PI Web API
від його імені за Kerberos неможливий у принципі.

Отже:
* **варіант B з ТЗ ФВ-6.2 (Kerberos constrained delegation) працюватиме лише
  для частини користувачів** — це вже не цілісна модель;
* **робочим варіантом стає A: сервісний обліковий запис** із правами запису в AF,
  а реальний користувач фіксується в аудиті ECR і в атрибутах даних.

> Це змінює баланс у відкритому питанні §12 п.4 ТЗ на користь сервісного
> облікового запису. Остаточно підтвердити з ІБ.

### 1.6 Чого не робимо (перша черга)

Зовнішні провайдери (Entra ID / OIDC / SAML). Архітектура це передбачає —
достатньо додати ще одну схему автентифікації, яка випускає ту саму cookie, —
але в обсяг першої черги не входить.

---

## 2. Авторизація: загальна схема

```
┌─────────────────────────────────────────────────────────────────┐
│  КАТАЛОГ ПРАВ (системний, задає розробник)                      │
│  sec.Permission: Template.Publish, Security.ManageUsers…         │
└──────────────────────────┬──────────────────────────────────────┘
                           │ адміністратор набирає в роль
┌──────────────────────────▼──────────────────────────────────────┐
│  РОЛЬ (створює адміністратор)                                   │
│  «Оператор води Мангістау»                                      │
│                                                                 │
│  Функціональні права        Ресурсні гранти                     │
│  ├ Document.Create          ├ Sheet "7. Water Report"  → Write   │
│  ├ Document.Submit          ├ Sheet "7a"               → Write   │
│  └ Data.Export              ├ Sheet "9. Utility"       → Read    │
│                             ├ Column "Cost"            → None    │
│                             └ решта                    → None    │
└──────────────────────────┬──────────────────────────────────────┘
                           │ призначення (з областю дії)
┌──────────────────────────▼──────────────────────────────────────┐
│  ПРИЗНАЧЕННЯ                                                    │
│  Роль → AD-група "ECR_Water_Mangistau"  АБО  локальний користувач│
│  Область: Project=2026, Region=Mangistau, Contractor=AGS        │
│  IsReadOnly=false, ValidTo=2027-01-31, ExtraGraceDays=+10        │
└─────────────────────────────────────────────────────────────────┘
```

Ролі призначаються **однаково** на AD-групу, на доменного користувача
і на локального користувача.

---

## 3. Каталог прав (`sec.Permission`)

Системний, поповнюється розробниками з релізами. Адміністратор **не створює**
нові права — він набирає їх у ролі.

| Група | Права |
|-------|-------|
| **Template** | `Template.View`, `Template.Edit`, `Template.Publish`, `Template.Clone`, `Template.Deprecate`, `Template.EditStyles`, `Template.EditRelations`, `Template.EditValidation` |
| **Project** | `Project.View`, `Project.Create`, `Project.Edit`, `Project.Close`, `Project.Archive` |
| **Period** | `Period.View`, `Period.Open`, `Period.Close`, `Period.Reopen`, `Period.EditPolicy` |
| **Document** | `Document.View`, `Document.Create`, `Document.Edit`, `Document.Delete`, `Document.Submit`, `Document.Approve`, `Document.Reject`, `Document.Reopen`, `Document.MigrateVersion`, `Document.EditInGrace` |
| **Data** | `Data.Export`, `Data.Import`, `Data.BulkEdit` |
| **Dictionary** | `Dictionary.View`, `Dictionary.Edit`, `Dictionary.Sync` |
| **Integration** | `Integration.ViewLogs`, `Integration.RunCollection`, `Integration.EditSources`, `Integration.EditMappings` |
| **Security** | `Security.ViewRoles`, `Security.EditRoles`, `Security.AssignRoles`, `Security.ManageUsers`, `Security.ResetPassword`, `Security.Impersonate` |
| **Audit** | `Audit.View`, `Audit.Export` |
| **System** | `System.ViewHealth`, `System.EditSettings` |

`sec.Permission`: `Code` (UQ), `GroupCode`, `NameEn/Ru/Kz`, `Description`,
`IsDangerous` (потребує підтвердження при видачі).

---

## 4. Ресурсні гранти (`sec.RoleResourceGrant`)

### 4.1 Рівні доступу

Упорядкований enum — вищий включає нижчий:

| # | Рівень | Що дозволяє |
|---|--------|-------------|
| 0 | `None` | ресурс не видно взагалі (немає у списку, API повертає 404) |
| 1 | **`Read`** | видно, редагування заблоковане |
| 2 | `Write` | введення і зміна значень |
| 3 | `Submit` | + подання на затвердження |
| 4 | `Approve` | + затвердження / відхилення |
| 5 | `Manage` | + зміна структури цього ресурсу |

### 4.2 Ієрархія ресурсів

```
ResourceType.Global        усі проєкти
  └ Project                конкретний проєкт
      └ Sheet (SheetDef)   аркуш
          └ Table (TableDef)
              └ Column (ColumnDef)
```

Грант видається на будь-якому рівні і **успадковується вниз**.

### 4.3 Приклади

```
Роль «Аудитор» (тільки читання по всьому):
  Global → Read

Роль «Оператор води»:
  Global                  → None    ← за замовчуванням нічого
  Sheet "7. Water Report" → Write
  Sheet "7a"              → Write
  Sheet "7b"              → Write
  Sheet "7.0"             → Read    ← rollup, редагувати нема чого

Роль «Підрядник» (не бачить собівартість):
  Global             → Write
  Column "UnitCost"  → None   ← більш конкретний грант перекриває
  Column "TotalCost" → None

Роль «Контролер якості»:
  Global                  → Read
  Sheet "8. Waste Report" → Approve
```

---

## 5. Розв'язання ефективних прав

Найважливіший алгоритм у моделі. Реалізується в **одному місці**
(`IAccessDecisionService`) і покривається тестами.

### 5.1 Алгоритм

```
EffectiveLevel(user, resource, scopeContext):

1. Зібрати всі активні призначення ролей користувача:
     • персональні (доменні і локальні) + через усі AD-групи
     • ValidFrom <= now <= ValidTo (або NULL)
     • Role.IsActive = 1, User.Status = Active

2. Відфільтрувати ті, чия ОБЛАСТЬ не збігається з контекстом
     (інший проєкт, інший регіон, інший підрядник)

3. Для кожного призначення, що лишилось:
     а) знайти гранти ролі по ланцюжку ресурсу
        (Column → Table → Sheet → Project → Global)
        перший знайдений (найконкретніший) = рівень цієї ролі
     б) якщо Assignment.IsReadOnly → min(рівень, Read)

4. ЯВНА ЗАБОРОНА ВИГРАЄ ЗАВЖДИ:
     якщо будь-який застосовний грант має IsDeny = 1 → None

5. Інакше: EffectiveLevel = MAX(рівнів усіх ролей)
     (ролі складаються, не перетинаються)

6. Накласти системні обмеження (див. §6)
```

### 5.2 Чому саме так

| Рішення | Обґрунтування |
|---------|---------------|
| Ролі складаються (`MAX`) | користувач із двома ролями має об'єднання прав — очікувана поведінка |
| Найконкретніший грант виграє в межах ролі | інакше «Global → Write» неможливо було б звузити для однієї колонки |
| `IsDeny` виграє над усім | єдиний надійний спосіб гарантовано щось закрити |
| `IsReadOnly` на призначенні, а не на ролі | одну роль можна видати комусь у повному обсязі, а комусь — тільки на читання |

### 5.3 Продуктивність

Розв'язання прав на кожну комірку неприйнятне. Тому:

* при вході будується **`AccessProfile`** — розгорнута мапа
  `resourceKey -> level` для всіх ресурсів поточної версії шаблону;
* кешується в `IMemoryCache`, ключ включає `userId + templateVersionId + securityStamp`;
* інвалідується зміною `SecurityStamp` (зміна ролей, грантів, пароля, блокування);
* API повертає клієнту готову мапу прав для відкритого документа —
  grid одразу знає, які колонки read-only, а які приховати.

---

## 6. Системні обмеження понад RBAC

Ефективний рівень — **не остаточне рішення**. Фінальна перевірка
«чи можна редагувати цю комірку» — композиція п'яти джерел:

```
CanEdit(user, cell) =
      RBAC:        EffectiveLevel >= Write
  AND Період:      Period.State ∈ {Open}
                   OR (Period.State = Grace AND має Document.EditInGrace)
                   OR Period.ReopenedUntil > now
  AND Документ:    Status = Draft
                   OR (Status = Approved AND має Document.Reopen)
  AND Структура:   ColumnDef.IsReadOnly = 0
                   AND RowDef.IsReadOnly = 0
                   AND ColumnDef.DataType != Formula
  AND Бізнес:      permit-вікно покриває цей місяць (ApplyPermitMonthLocks)
                   AND рядок проходить RowFilterExpression гранта
```

**Це має бути один метод.** Розмазування цієї логіки по контролерах і UI —
найпоширеніший спосіб отримати діру в правах.

UI отримує від API не лише `canEdit: false`, а й **причину**
(`reason: "PeriodClosed"` / `"NoWritePermission"` / `"PermitNotValid"`) —
щоб користувач розумів, чому поле сіре.

---

## 7. Вбудовані ролі (`IsSystem = 1`)

Постачаються з системою, **не видаляються**, але можна копіювати і змінювати копію.

| Роль | Функціональні права | Ресурсні гранти |
|------|--------------------|-----------------|
| `Viewer` | `*.View`, `Data.Export` | `Global → Read` |
| `DataEntry` | + `Document.Create/Edit/Submit`, `Data.Import` | `Global → Submit` |
| `Approver` | + `Document.Approve/Reject` | `Global → Approve` |
| `PeriodAdministrator` | + `Period.*`, `Document.Reopen`, `Document.EditInGrace` | `Global → Approve` |
| `TemplateAdministrator` | + `Template.*`, `Dictionary.Edit` | `Global → Manage` |
| `SystemAdministrator` | усі | `Global → Manage` |

**Захист від самоблокування:** система не дозволяє прибрати останнє призначення
ролі з правом `Security.EditRoles`, а також заблокувати останній активний
обліковий запис із цим правом.

**Первинне налаштування:** при першому запуску створюється локальний
обліковий запис `admin` із згенерованим паролем, який показується один раз
і потребує зміни при першому вході. Це розв'язує проблему «як увійти,
поки AD-групи ще не налаштовані».

---

## 8. Схема БД (`sec`)

### sec.User
| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` | `int` PK | внутрішній ідентифікатор — на нього посилається аудит |
| **`AuthProvider`** | `tinyint` | `Windows` = 0, `Local` = 1 |
| `UserName` | `nvarchar(200)` UQ | `DOMAIN\user` або локальний логін |
| `Sid` | `nvarchar(200)` NULL UQ | лише для `Windows` |
| `DisplayName`, `Email` | `nvarchar(200)` | |
| `Status` | `tinyint` | `Invited`/`Active`/`Locked`/`Disabled` |
| **`PasswordHash`** | `nvarchar(400)` NULL | лише для `Local` |
| `PasswordChangedAt` | `datetime2` NULL | |
| `MustChangePassword` | `bit` | |
| `PasswordExpiresAt` | `datetime2` NULL | |
| `SecurityStamp` | `nvarchar(64)` | зміна -> усі сесії недійсні |
| `LockoutFailedCount` | `int` | |
| `LockoutEndsAt` | `datetime2` NULL | |
| `TwoFactorEnabled` | `bit` | |
| `TwoFactorSecret` | `nvarchar(200)` NULL | шифрується |
| `CachedGroupsJson`, `GroupsCachedAt` | | членство в AD, TTL ~15 хв |
| `LastLoginAt`, `CreatedAt/BySid` | | |

**CHECK:** `AuthProvider = 1` -> `PasswordHash IS NOT NULL`;
`AuthProvider = 0` -> `Sid IS NOT NULL AND PasswordHash IS NULL`.

### sec.PasswordHistory
`Id`, `UserId` FK, `PasswordHash`, `CreatedAt` — для заборони повторення N останніх.

### sec.PasswordPolicy
`Id`, `MinLength`, `RequireUpper`, `RequireLower`, `RequireDigit`, `RequireSpecial`,
`HistoryDepth`, `ExpiryDays`, `MaxFailedAttempts`, `LockoutMinutes`,
`RequireTwoFactorForSecurityRoles`

> Політика — **дані**, редагується адміністратором у UI, а не в `appsettings.json`.

### sec.UserSession
`Id`, `UserId` FK, `SessionId`, `AuthProvider`, `IssuedAt`, `ExpiresAt`,
`ClientIp`, `UserAgent`, `RevokedAt`, `RevokedBySid`

### sec.Role
| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` | `int` PK | |
| `Code` | `nvarchar(50)` UQ | |
| `NameEn/Ru/Kz` | `nvarchar(200)` | |
| `Description` | `nvarchar(1000)` | |
| `IsSystem` | `bit` | вбудована, не видаляється |
| `IsActive` | `bit` | |
| `ClonedFromRoleId` | `int` FK NULL | |
| `CreatedAt/BySid`, `UpdatedAt/BySid` | | |

### sec.RolePermission
`RoleId` FK, `PermissionId` FK — PK `(RoleId, PermissionId)`

### sec.RoleResourceGrant
| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` | `int` PK | |
| `RoleId` | `int` FK | |
| `ResourceType` | `tinyint` | `Global`/`Project`/`Sheet`/`Table`/`Column` |
| `ResourceId` | `int` NULL | `NULL` для `Global` |
| `AccessLevel` | `tinyint` | `None`…`Manage` |
| `IsDeny` | `bit` | явна заборона — виграє над усім |
| `RowFilterExpression` | `nvarchar(1000)` NULL | рядковий фільтр |

**UQ:** `(RoleId, ResourceType, ResourceId)`

### sec.RoleAssignment
| Колонка | Тип | Примітка |
|---------|-----|----------|
| `Id` | `int` PK | |
| `RoleId` | `int` FK | |
| `PrincipalType` | `tinyint` | `User` / `AdGroup` |
| `PrincipalUserId` | `int` FK NULL | для `User` (доменного або локального) |
| `PrincipalSid` | `nvarchar(200)` NULL | для `AdGroup` |
| `PrincipalDisplayName` | `nvarchar(200)` | кеш для UI |
| `ProjectId` | `int` FK NULL | `NULL` = усі проєкти |
| `ScopeJson` | `nvarchar(1000)` NULL | `{"RegionIds":[1,2],"ContractorIds":[5]}` |
| **`IsReadOnly`** | `bit` | обмежує ефективний рівень до `Read` |
| `ExtraGraceDays` | `int` DEFAULT 0 | додаткові дні редагування |
| `ValidFrom`, `ValidTo` | `datetime2` NULL | тимчасовий доступ |
| `AssignedAt/BySid`, `RevokedAt/BySid` | | |

**IX:** `(PrincipalUserId, ProjectId)`, `(PrincipalSid, ProjectId)`, `(RoleId)`

### sec.AuthAuditEntry
`Id`, `EventType` (`LoginSuccess`/`LoginFailed`/`Lockout`/`Logout`/
`PasswordChanged`/`PasswordReset`/`TwoFactorEnabled`/`SessionRevoked`),
`UserId` NULL, `AttemptedUserName`, `AuthProvider`, `FailureReason`,
`ClientIp`, `UserAgent`, `OccurredAt`

> Пароль і його хеш сюди **ніколи** не потрапляють.

### sec.AccessAuditEntry
`Id`, `EventType` (`RoleCreated`/`GrantChanged`/`RoleAssigned`/`RoleRevoked`/
`UserCreated`/`ImpersonationStarted`), `ActorSid`, `TargetUserId` NULL,
`RoleId` NULL, `BeforeJson`, `AfterJson`, `OccurredAt`, `ClientIp`

---

## 9. UI адміністратора

### Екран «Користувачі»
* фільтр за провайдером (Windows / локальні);
* **створення локального користувача**: логін, ПІБ, email, ролі, спосіб
  видачі пароля (тимчасовий / запрошення), строк дії;
* пошук доменних користувачів в AD і призначення їм ролей;
* дії: блокувати, розблокувати, скинути пароль, завершити сесії, вимкнути;
* **кнопка «Показати ефективні права»** — по кожному ресурсу підсумковий рівень
  **і пояснення**, який грант якої ролі його дав;
* `Impersonate (read-only)` — подивитися систему очима користувача, із записом в аудит.

### Екран «Ролі»
* список ролей із кількістю призначень;
* створення / копіювання ролі;
* вкладка **«Функціональні права»** — дерево груп із чекбоксами;
  небезпечні права позначені і потребують підтвердження;
* вкладка **«Доступ до структури»** — дерево `Project -> Sheet -> Table -> Column`,
  на кожному вузлі випадний список рівня; успадковані значення сірі, явні — чорні;
* вкладка **«Призначення»** — кому видана роль, з якою областю і строком.

### Екран «Матриця доступу»
Зведена таблиця `Роль × Аркуш` з рівнями — щоб бачити картину прав цілком.

### Екран «Політика паролів і безпека»
Довжина, складність, історія, строк дії, блокування, 2FA, TTL сесії.

---

## 10. Тести, які обов'язкові

### Авторизація
| # | Сценарій |
|---|----------|
| 1 | Дві ролі з різними рівнями -> ефективний = `MAX` |
| 2 | `IsDeny` в одній ролі перекриває `Manage` в іншій |
| 3 | Грант на колонку перекриває грант на аркуш |
| 4 | `Assignment.IsReadOnly` знижує `Approve` до `Read` |
| 5 | Прострочене призначення (`ValidTo` в минулому) не діє |
| 6 | Область не збігається -> призначення не застосовується |
| 7 | `None` на аркуш -> аркуш відсутній у відповіді API, не «видимий, але заблокований» |
| 8 | Закритий період -> редагування заблоковане навіть для `Manage` |
| 9 | `Grace` + `Document.EditInGrace` -> дозволено, `IsLateEdit = 1` |
| 10 | Не можна прибрати останнє призначення `Security.EditRoles` |
| 11 | Зміна ролі інвалідує `AccessProfile` усіх її носіїв |
| 12 | Видалення `SheetDef` (soft) не залишає «висячих» грантів |

### Автентифікація
| # | Сценарій |
|---|----------|
| 13 | Локальний і доменний користувачі з однаковими ролями мають **ідентичні** права |
| 14 | N невдалих спроб -> блокування; відповідь однакова для неіснуючого логіну |
| 15 | Зміна пароля / ролей інвалідує всі активні сесії (`SecurityStamp`) |
| 16 | `MustChangePassword` блокує доступ до всього, крім зміни пароля |
| 17 | Пароль не потрапляє в логи, аудит і відповіді API |
| 18 | Заблокований / вимкнений користувач не проходить автентифікацію |
| 19 | Прострочений пароль -> примусова зміна |
| 20 | Не можна створити локального користувача з логіном, що збігається з доменним |
