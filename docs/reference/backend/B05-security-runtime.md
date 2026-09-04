# B05 — Безпека в runtime: автентифікація, рішення про доступ, кеш

> Реалізація моделі з [11-security-model.md](../design/11-security-model.md).
> Тут — те, що в ній не описано: як це працює на гарячому шляху і як не зробити
> з налаштовуваного RBAC вузьке місце.
> Закриває частину §11 [13] п. **8** (стратегія кешу).

---

## 1. Дві схеми, одна ідентичність

### 1.1 Конвеєр

```
/login/windows   [Authorize(AuthenticationSchemes = "Negotiate")]
      │  HttpContext.User.Identity.Name → DOMAIN\user, SID
      │  sec.User пошук за SID (створення при першому вході — за налаштуванням)
      ▼
/login/local     форма → PasswordHasher.VerifyHashedPassword
      │  lockout, MustChangePassword, PasswordExpiresAt, 2FA
      ▼
   ОДИН метод IssueAppIdentityAsync(sec.User user)
      │  claims: sub = sec.User.Id  (НЕ SID)
      │          name, provider, security_stamp
      │  cookie "ecr.auth", HttpOnly + Secure + SameSite=Lax, 8 год sliding
      ▼
   Решта API: [Authorize] за замовчуванням, схема Cookie
```

**Ключове:** `sub` — це внутрішній `sec.User.Id`, а не SID. Локальні користувачі
SID не мають; уся авторизація, аудит і зовнішні ключі посилаються на `Id`
(це узгоджується з [07] §6 і B04 §5.2).

### 1.2 `SecurityStamp` — перевірка на кожен запит

Пастка §13.5 п.11 ТЗ: cookie сама себе не інвалідує. Тому
`CookieAuthenticationEvents.OnValidatePrincipal`:

```
1. взяти security_stamp із claims
2. порівняти з sec.User.SecurityStamp
     — читання з IMemoryCache, ключ user:{id}:stamp, TTL 30 с
     — при зміні ролей/пароля/блокування застосунок оновлює і кеш, і БД
3. розбіжність → RejectPrincipal() + SignOut → 401
4. User.Status != Active → те саме
```

TTL 30 с — компроміс: миттєвість «блокування діє негайно» проти запиту до БД
на кожен HTTP-виклик. Для критичних дій (зміна ролей, `Impersonate`) —
примусова перевірка без кешу.

### 1.3 Чого немає в першій черзі

Зовнішні провайдери (Entra ID / OIDC / SAML) — [11] §1.6. Архітектурно це
«ще одна схема, що викликає `IssueAppIdentityAsync`». Важливо лише не зашити
припущення «провайдерів рівно два» — тому вибір провайдера живе в
`sec.User.AuthProvider` як `tinyint`, а не як `bool IsLocal`.

---

## 2. `IAccessDecisionService` — єдина точка

Контракт із [13] §6 + реалізація.

```csharp
public interface IAccessDecisionService
{
    Task<AccessProfile> BuildProfileAsync(int userId, int templateVersionId, CancellationToken ct);
    AccessLevel  GetLevel(AccessProfile profile, ResourceRef resource);
    EditDecision CanEdit(AccessProfile profile, CellRef cell, DocumentState doc, PeriodState period);
}
```

### 2.1 `AccessProfile` — розгорнута мапа, а не набір правил

```csharp
public sealed class AccessProfile
{
    public int    UserId          { get; init; }
    public string SecurityStamp   { get; init; }
    public int    TemplateVersionId { get; init; }

    // Функціональні права — плоский набір кодів
    public FrozenSet<string> Permissions { get; init; }

    // Ресурсні гранти, ВЖЕ розгорнуті по ієрархії Project→Sheet→Table→Column
    public FrozenDictionary<ResourceRef, AccessLevel> Levels { get; init; }

    // Рядкові фільтри: колонка → скомпільований предикат
    public FrozenDictionary<int, CompiledPredicate> RowFilters { get; init; }

    public int ExtraGraceDays { get; init; }
}
```

**Розгортання при побудові, а не при перевірці.** Алгоритм [11] §5.1 (зібрати
призначення → відфільтрувати за областю → пройти ланцюжок ресурсу → `IsDeny` →
`MAX`) виконується **один раз** для всіх ресурсів версії шаблону. Далі
`GetLevel` — це пошук у словнику.

Розмір: ~90 таблиць × ~40 колонок ≈ 3 600 ресурсів + аркуші + проєкт ≈ 4 000
записів. `FrozenDictionary` на 4 000 елементів — це ~200 КБ і мікросекунди на пошук.
Для 100 користувачів — 20 МБ пам'яті. Прийнятно.

### 2.2 `CanEdit` — композиція п'яти джерел

Реалізує [11] §6 дослівно, і повертає **причину**, а не `bool`:

```csharp
public EditDecision CanEdit(AccessProfile p, CellRef cell, DocumentState doc, PeriodState period)
{
    if (p.GetLevel(cell.Column) < AccessLevel.Write)  return Deny(NoPermission);
    if (cell.Column.IsReadOnly || cell.Row.IsReadOnly) return Deny(StructurallyReadOnly);
    if (cell.Column.IsCalculated)                      return Deny(CalculatedCell);

    switch (period.State)
    {
        case Open:                                     break;
        case Grace when p.Has("Document.EditInGrace"): break;
        case _ when period.ReopenedUntil > _clock.UtcNow: break;
        default: return Deny(PeriodClosed);
    }

    if (doc.Status == Approved && !p.Has("Document.Reopen")) return Deny(DocumentApproved);
    if (!PermitWindowCovers(cell, period))                   return Deny(OutsidePermitWindow);
    if (!PassesRowFilter(p, cell))                           return Deny(RowFiltered);

    return EditDecision.Allow;
}
```

Порядок перевірок — **від дешевого до дорогого** і від загального до конкретного:
причина, яку побачить користувач, має бути найзмістовнішою. «Період закрито»
корисніше, ніж «немає прав», якщо вірні обидві.

### 2.3 Правило доступу до періодів → `SourceWindow`

`PermitWindowCovers` — це реалізація `cfg.PeriodAccessRuleDef` з
`RuleKind = SourceWindow` ([09] §6a), тобто узагальнення чинного
`ApplyPermitMonthLocks` (алгоритм 8 із ТЗ §7):

```
Parameters = { source: "Registry:Permit", keyColumn: "PermitId",
               from: "StartDate", to: "ActualEndDate",
               onOutOfWindow: "LockAndWarn" }

місяць редагується, якщо [StartDate, ActualEndDate] ПЕРЕТИНАЄТЬСЯ з [PeriodStart, PeriodEnd]
fail-open: дозвіл не резолвиться → місяць лишається відкритим
```

**`fail-open` зберігається свідомо** — так поводиться чинна система ([04] §5),
і зміна цієї поведінки на `fail-closed` заблокувала б роботу при будь-якій
проблемі з довідником. Але кожен `fail-open` пишеться в лог із рівнем `Warning`
і потрапляє в `aud.ConsistencyIssue` — щоб «тихо працює неправильно» стало видимим.

⚠ `onOutOfWindow = LockAndClear` (чинна поведінка Excel — **очищення даних**)
реалізується як окрема явна операція з підтвердженням користувача (ФВ-2.16),
а не як побічний ефект перевірки доступу.

---

## 3. Мапа прав для клієнта

Grid не має питати сервер про кожну комірку. При відкритті таблиці API віддає:

```jsonc
"permissions": {
  "table": "Write",
  "columns": { "MonthValue": "Write", "Cost": "None", "GroupTotal": "Read" },
  "rows":    { "7001003": { "editable": false, "reason": "OutsidePermitWindow" } },
  "period":  { "state": "Grace", "daysLeft": 6, "requiresReason": true }
}
```

Колонки з рівнем `None` **не потрапляють у відповідь взагалі** — ані метаданих,
ані значень ([11] §10, сценарій 7: «`None` → аркуш відсутній у відповіді API,
не видимий-але-заблокований»). Це різниця між приховуванням і безпекою.

---

## 4. Продуктивність: три правила

| # | Правило | Чому |
|---|---------|------|
| 1 | Права **ніколи** не резолвляться на комірку з БД | 30 000 перевірок на одне відкриття таблиці ([14] §8) |
| 2 | `AccessProfile` будується раз на сесію | побудова — це 5–7 запитів і розгортання ієрархії, ~50 мс |
| 3 | `RowFilterExpression` компілюється в **SQL-предикат**, а не фільтрується в пам'яті | інакше «прочитати 500 рядків, показати 12» — і бюджет читання зруйновано |

Правило 3 варте уточнення: `RowFilterExpression` (B03, контекст `RowFilter`)
навмисно обмежений — без агрегатів і крос-періодних посилань — саме щоб його
можна було транслювати в `WHERE EXISTS (SELECT 1 FROM doc.CellValue …)`.
Це обмеження мови заради можливості виконати фільтр у базі.

---

## 5. Кеш (BR-15) — закриває §11 [13] п. 8

### 5.1 Три рівні, різні за природою

| Що | Де | Ключ | Інвалідація |
|----|----|------|-------------|
| Метадані **Published**-версії | `IMemoryCache`, per-instance | `schema:v{id}:r{presentationRevision}` | **не потрібна** |
| Метадані **Draft**-версії | `IMemoryCache` | `schema:draft:{id}:{rowversion}` | ключ змінюється сам |
| `AccessProfile` | `IMemoryCache` | `acl:{userId}:{versionId}:{securityStamp}` | **не потрібна** |
| `SecurityStamp` | `IMemoryCache` | `user:{id}:stamp` | TTL 30 с |
| Списки довідників для листбоксів | `IMemoryCache` + ETag | `reg:{defId}:d{dataRevision}:{asOf}:{lang}` | **не потрібна** |
| Календар періодів | `IMemoryCache` | `periods:{projectId}:{rowversion}` | ключ змінюється сам |
| Дані документів | **не кешуються** | — | — |

### 5.2 Головний принцип: версія в ключі замість інвалідації

Класична проблема «кеш на кількох інстансах» тут **не виникає** — за побудовою.
Структурний шар опублікованої версії незмінний, тому запис під ключем
`schema:v7:r17` не може застаріти: він або актуальний, або більше ніколи не
запитуватиметься.

Щоб це працювало і для довідників, `cfg.RegistryDef` отримує поле
**`DataRevision int`**, яке інкрементується при будь-якій зміні записів реєстру
(CRUD у UI, синхронізація з AF, імпорт). Тоді ключ списку теж самоінвалідується,
і жодних broadcast-повідомлень між інстансами не потрібно.

> Це доповнення до [07] §3.1 — поля `DataRevision` там немає. Винесено в
> [B10](B10-decisions.md) як пропозицію правки; без нього доведеться або
> тримати TTL (і показувати застарілі списки після синхронізації), або будувати
> механізм інвалідації між інстансами — тобто платити за те, чого можна не робити.

### 5.3 Чи потрібен `IDistributedCache`

За замовчуванням — **ні**. Усе перелічене або per-instance-безпечне (бо ключ
містить версію), або дешеве для повторної побудови.

`IDistributedCache` на SQL Server лишається для одного випадку: якщо замір
покаже, що побудова `AccessProfile` (~50 мс) на двох інстансах при 100
користувачах дає помітний трафік до БД. Це перевіряється на Етапі 0
([15] §8 п.7), а не приймається наперед.

Redis виключений політикою (AGPLv3/SSPL); якщо кеш-сервер таки знадобиться —
**Garnet** (MIT), клієнт `StackExchange.Redis`.

---

## 6. Захист від самоблокування і первинне налаштування

| Механізм | Реалізація |
|----------|------------|
| Не можна прибрати останнього носія `Security.EditRoles` | перевірка в команді + `ConsistencyCheckJob` + `MetadataValidator` при старті ([11] §7) |
| Не можна вимкнути останній активний обліковий запис із цим правом | те саме |
| Первинний `admin` | ідемпотентний seed при старті: локальний обліковий запис, згенерований пароль, `MustChangePassword = 1`, пароль показується **один раз** у консолі розгортання і ніде не логується |
| Локальний логін не може збігатися з доменним | унікальність `UserName` + перевірка при створенні ([11] §10, сценарій 20) |

---

## 7. Аудит безпеки

Дві окремі append-only таблиці ([11] §8), обидві з `UserId`, а не SID:

* `sec.AuthAuditEntry` — вхід, невдача, блокування, зміна/скидання пароля, вихід,
  завершення сесії. Пароль і його хеш сюди не потрапляють **ніколи**;
  у коді це забезпечується тим, що DTO логіну має `[JsonIgnore]`-подібне
  маркування і власний `ToString()`.
* `sec.AccessAuditEntry` — створення/зміна ролі, зміна грантів, призначення і
  відкликання, кожна `Impersonate`-сесія, з `BeforeJson`/`AfterJson`.

`Impersonate` — **тільки read-only**: під час імперсонації будь-яка мутація
відхиляється на рівні middleware, незалежно від прав цілі. Інакше «подивитися
очима користувача» стає способом зробити зміну від чужого імені.

---

## 8. Тести (розширення [11] §10)

До 20 обов'язкових сценаріїв додаються п'ять, специфічних для runtime:

| # | Сценарій |
|---|----------|
| 21 | `AccessProfile` кешується під ключем зі `securityStamp`; зміна ролі дає **новий** ключ, старий не використовується |
| 22 | Колонка з рівнем `None` відсутня у відповіді API (не «присутня і заблокована») |
| 23 | `RowFilterExpression` виконується в SQL: план запиту містить предикат, кількість прочитаних рядків = кількості відданих |
| 24 | `Impersonate` + будь-яка мутація → 403, запис в `sec.AccessAuditEntry` |
| 25 | `CanEdit` повертає **найзмістовнішу** причину, коли істинні кілька (закритий період важливіший за «немає прав») |
