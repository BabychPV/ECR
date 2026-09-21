# Директива №15 — бекенд: розширення API під новий інтерфейс

**Дата:** 2026-09-19 · **Звірено з `main` @ `745d8f9`** · Частина пакета
[`DIRECTIVE-15.md`](DIRECTIVE-15.md) (читати спершу його).

Цей файл відповідає на одне питання: **чого бракує серверу, щоб кожен елемент
нового інтерфейсу показував правду, а не заглушку.** Джерело — звірка
152 можливостей гібридного макета (`docs/design/hybrid/`) з 115 шляхами
`contracts/openapi.snapshot.json` і кодом контролерів/обробників:
**54 є · 44 частково · 54 немає.**

Позначки достовірності — ті самі, що в №14:

| Знак | Значення |
|---|---|
| ✔ | перевірено мною в коді на `745d8f9` (файл:рядок наведено) |
| ◐ | знахідка саб-агента з файл:рядок, мною вибірково не перечитана — **перевір одним пошуком перед стартом** |
| ⏱ | потребує заміру, а не читання |

---

## 0. Правила, спільні для всіх пунктів

1. **Ендпоінт виходить разом зі споживачем.** Архітектурний сторож
   `EndpointCoverageTests.Кожна_дія_сервера_має_споживача_в_інтерфейсі` падає на
   дії без клієнта. Тому кожен `BE-xx` — це ОДИН PR із чотирма частинами:
   обробник + маршрут → тести сервера → `contracts/openapi.snapshot.json` +
   перегенерований `src/Ecr.Web/src/shared/api/schema.d.ts` → мінімальний
   споживач у клієнті (хук у `features/<x>/api.ts` і його виклик на чинному
   екрані). Споживач може бути скромним (кнопка в наявній таблиці) — новий
   вигляд приїде окремим PR із `DIRECTIVE-15-FRONTEND.md`.
2. **Іменовані записи у відповідях, ніколи анонімні об'єкти** — інакше в
   OpenAPI немає імені типу (`A7-16`, `A7-32`; див. коментар у
   `DocumentsController.cs:129-132` ✔).
3. **Право перевіряє обробник, не контролер.** Помічників у коді **два**, і
   обидва справжні:
   - `PermissionCheck.RequireAsync(access, currentUser, permission, ct)` —
     `Ecr.Application.Security`, `public static`, **54 виклики**, повертає
     `AccessProfile`, щоб не будувати його двічі. З'явився після `A7-53`, коли
     три майже однакові приватні помічники розійшлися. **Для нового коду —
     цей.**
   - `ListTemplatesHandler.RequireAsync(access, currentUser, permission, ct)` —
     `TemplateQueryHandlers.cs:45`, `internal static`, **22 виклики**, зокрема
     весь `IntegrationHandlers.cs`. Старіша форма; у файлі, де її вже вживають
     сусіди, лишай її — другий стиль поруч гірший за старий.

   ~~⛔ **✎ 2026-09-19:** тут стояло `ListTemplatesHandler.RequireAsync` —
   такого методу **не існує**.~~
   ⛔ **✎ 2026-09-19, пізніше: попереднє виправлення було ХИБНЕ, і хибним був
   я.** Метод існує; я записав сюди знахідку саб-агента, не перевіривши її сам
   — тобто зробив рівно те, від чого застерігає позначка ◐ на початку файлу.
   Первісний текст директиви був правильний.
4. **Помилки — кодами** `ECR-<ОБЛАСТЬ>-<HTTP>`; новий код = рядок у сіді
   повідомлень (`messageKey`) + рядок `en` у `09-seed.sql`. Див. пам'ятку
   «покрити звільнену вимогу — це три файли й два сторожі».
5. **Доказ, а не зелений колір.** У кожному PR названо мутацію, від якої новий
   тест падає (яку саме умову прибрали/перевернули), і результат.
   ⚠ .NET: після мутації — `dotnet build` перед `dotnet test`, інакше тестується
   стара DLL (пам'ятка `msbuild-stale-after-copy`).
6. **Міграції — по одній, окремим PR, послідовно** (CLAUDE.md §2). Пункти з
   позначкою 🗄 мають міграцію; вони НЕ паралеляться між собою.
7. ~~**Конфлікт за файли з №14-ARCH.** `PatchCellsHandler.cs`, `AuditReader.cs`,
   `QuartzJobScheduler.cs` зараз активно змінюються (відкритий #347, worktree'ї
   `wr05-period`, `w16-timeouts`). Пункти `BE-05`, `BE-06` стартують лише після
   мержу відкритих PR, що чіпають `PatchCellsHandler.cs`.~~
   ✎ **2026-09-21:** умова знята — `BE-05` і `BE-06` уже змерджено:
   `Dto/PatchCellsResponse.cs:30` (`RecalculationJobId`),
   `PatchCellsHandler.cs:533` (`ReadLastChangesAsync`), `CellConflictDto.cs:51`
   (`TheirOrigin`). Чекати нема на що.

---

## 1. Хвиля B1 — дешеві прогалини (S), без міграцій

Кожен пункт ≤ 300 рядків diff. Між собою **не перетинаються за файлами**, крім
`openapi.snapshot.json`/`schema.d.ts` — тому **мержити по одному**, а
перегенерацію контракту робити останнім комітом гілки після `git merge origin/main`.

### ~~BE-01 · Видалення запису довідника — маршрут до наявного обробника ✔~~

✎ **2026-09-21: зроблено.** `RegistriesController.cs:240`
`[HttpDelete("{code}/entries/{id:long}")]`. «Факт» нижче описує стан до
реалізації — маршруту тепер НЕ бракує.

**Факт.** `DeleteRegistryEntryHandler` існує й зареєстрований
(`RegistryAdminHandlers.cs:286-334` ✔, DI — `DependencyInjection.cs:172` ✔), але
в `RegistriesController.cs` немає жодного `[HttpDelete]` ✔ (пошук по
`Controllers/`). Обробник уже робить усе правильне: право `Registry.EditData`,
`ECR-REG-0404`, `ECR-REG-0409` з `Details["references"]`, `SoftDelete`,
`BumpDataRevision`.

**UI, який на це спирається:** `#/admin/registries/<code>/entries` →
діалог `ent-delete-confirm` і стан `ent-delete-blocked` («на запис посилаються
N комірок — закрийте датою»).

**Код** (`src/Ecr.Api/Controllers/RegistriesController.cs`; обробник додати в
первинний конструктор контролера):

```csharp
/// <summary>
/// Видаляє запис довідника. Право <c>Registry.EditData</c> (ФВ-8.6).
/// </summary>
/// <remarks>
/// ⛔ Запис, на який посилаються дані, не видаляється — <c>409 ECR-REG-0409</c>
/// з кількістю посилань у <c>details.references</c>. Клієнт у відповідь
/// пропонує закрити запис датою (<c>POST …/validity</c>), а не повторює спробу.
/// <para>
/// ⚠ <c>code</c> у шляху перевіряється: запис чужого довідника — 404, а не
/// мовчазне видалення «бо id збігся».
/// </para>
/// </remarks>
[HttpDelete("{code}/entries/{id:long}")]
[ProducesResponseType(StatusCodes.Status204NoContent)]
[ProducesResponseType(StatusCodes.Status404NotFound)]
[ProducesResponseType(StatusCodes.Status409Conflict)]
public async Task<IActionResult> DeleteEntry(string code, long id, CancellationToken ct)
{
    await deleteEntry.HandleAsync(code, id, ct).ConfigureAwait(false);

    return NoContent();
}
```

**Зміна обробника** — додати перевірку належності довіднику (зараз сигнатура
`HandleAsync(long registryEntryId, CancellationToken ct)` ✔ і `code` ніхто не
звіряє):

```csharp
public async Task HandleAsync(string registryCode, long registryEntryId, CancellationToken ct)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(registryCode);
    // … RequireAsync, userId, FindEntryAsync — без змін …

    var definition = await registries.FindDefinitionByIdAsync(entry.RegistryDefId, ct).ConfigureAwait(false);
    if (definition is null || !string.Equals(definition.Code, registryCode, StringComparison.OrdinalIgnoreCase))
    {
        throw new NotFoundException("ECR-REG-0404", $"Запису {registryEntryId} у довіднику «{registryCode}» не існує.");
    }
    // … CountReferencesAsync → 0409, SoftDelete, definition.BumpDataRevision(), SaveChanges …
}
```

⚠ `definition` тепер читається ДО `SoftDelete` — другий виклик
`FindDefinitionByIdAsync` (`:307`) прибрати, а не лишити дублем.

**Тести, які сьогодні падають** (`tests/Ecr.Api.Tests/…Registries…`):

| Тест | Очікування |
|---|---|
| `DELETE` без посилань | `204`; повторний `GET …/entries` запису не містить; `DataRevision` довідника зросла |
| `DELETE` запису з посиланням у `doc.CellValue` | `409`, `code=ECR-REG-0409`, `details.references ≥ 1`; запис лишився |
| `DELETE /registries/FuelTypes/entries/{id запису EmissionSources}` | `404` — **мутаційний доказ**: прибрати перевірку `definition.Code` → тест падає |
| без `Registry.EditData` | `403` |

**Споживач:** `features/registries/api.ts` → `useDeleteRegistryEntry(code)`;
на `409` показати `details.references` і дію «Close validity» замість «Retry».

---

### ~~BE-02 · Скасування фонової задачі ✔~~

✎ **2026-09-21: зроблено.** `JobsController.cs:118`
`[HttpPost("{jobId}/cancel")]`; «три дії» нижче — стан до реалізації.

**Факт.** `IBackgroundJobScheduler.CancelAsync(string jobId, CancellationToken ct)`
існує (`Ports/IBackgroundJobScheduler.cs:63` ✔), `QuartzJobAdapter` уміє
завершити задачу станом `"Cancelled"` (`QuartzJobAdapter.cs:143` ✔), але
`JobsController` має рівно три дії: `List`, `Get`, `Restart` ✔. Двадцятихвилинний
річний перерахунок із інтерфейсу зупинити нічим.

**UI:** `#/admin/jobs` → `jobs-dlg-cancel-job`, панель `jobs-panel-running`.

**Обробник** (`src/Ecr.Application/Integration/IntegrationHandlers.cs`, поруч із
`RestartJobHandler` — дзеркальна форма):

```csharp
/// <summary>
/// Скасування фонової задачі. Право <c>System.ViewHealth</c> — або автор
/// власної задачі (Q-156, та сама межа, що в <see cref="GetJobStatusHandler"/>).
/// </summary>
/// <remarks>
/// ⚠ Скасування — ПРОХАННЯ, не вбивство: задача бачить токен і завершується на
/// найближчій межі батчу. Тому відповідь — 202, а стан «Cancelled» клієнт
/// дочитує тим самим <c>GET /jobs/{jobId}</c>.
/// </remarks>
public sealed class CancelJobHandler(
    IBackgroundJobScheduler jobs,
    IJobProgressStore progress,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Код помилки: скасувати можна лише задачу, що ще не завершилась.</summary>
    public const string NotActiveErrorCode = "ECR-JOB-0409";

    public async Task HandleAsync(string jobId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var status = await jobs.GetStatusAsync(jobId, ct).ConfigureAwait(false);
        if (string.Equals(status.State, "Unknown", StringComparison.Ordinal))
        {
            throw new NotFoundException("ECR-JOB-0404", $"Задачі {jobId} не існує.");
        }

        // Автор скасовує СВОЮ задачу без System.ViewHealth; чужу — лише з правом.
        var ownerId = await progress.GetCreatedByUserIdAsync(jobId, ct).ConfigureAwait(false);
        if (ownerId is null || ownerId != currentUser.UserId)
        {
            // ⚠ Тут годяться обидва помічники (див. §0.3). `IntegrationHandlers.cs`
            // уже вживає ListTemplatesHandler.RequireAsync — у ньому й лишається він.
            await ListTemplatesHandler
                .RequireAsync(access, currentUser, GetJobStatusHandler.Permission, ct)
                .ConfigureAwait(false);
        }

        if (status.State is not ("Queued" or "Running"))
        {
            throw new BusinessRuleException(
                NotActiveErrorCode,
                $"Задача {jobId} у стані «{status.State}» — скасовувати нічого.");
        }

        await jobs.CancelAsync(jobId, ct).ConfigureAwait(false);
    }
}
```

◐ Перевір перед стартом: точні рядки станів «у черзі»/«виконується» в
`QuartzJobScheduler`/`QuartzJobAdapter` (я бачив `"Succeeded"`, `"Cancelled"`,
`"Failed"`, `"Unknown"`; `"Queued"`/`"Running"` — за коментарем
`JobsController.cs:51` «знову у черзі») і сигнатуру
`IJobProgressStore.GetCreatedByUserIdAsync`. ⚠ `RestartJobHandler` уже займає код
`ECR-JOB-0409` для «не Failed» — той самий код із іншим текстом прийнятний (одна
природа: «стан не дозволяє дію»), але `messageKey` має бути окремий.

**Маршрут** (`JobsController.cs`): `[HttpPost("{jobId}/cancel")]` →
`Accepted(new Contracts.JobAcceptedResponse(jobId))`, `404`, `409`.

⚠ `jobId` містить `#` — у шляху він має бути закодований. Саме на цьому
падав `smoke.ps1` на кроці 17 (див. CLAUDE.md). Тест клієнта мусить перевірити
`encodeURIComponent` у новому хуку.

**Тести:** скасування `Running` → `202`, далі `GET` дає `Cancelled`;
`Succeeded` → `409`; чужа задача без права → `403`; **своя** задача без права →
`202` (мутація: прибрати гілку `ownerId` → падає саме цей тест).

---

### ~~BE-03 · Історія однієї комірки й фільтри журналу ✔~~

✎ **2026-09-21: зроблено.** `AuditController.cs:67-68` приймає `rowKey`,
`columnDefId`, `author`, `origin`, `lateOnly`; «лише
`from,to,documentId,limit,cursor`» нижче — стан до реалізації.

**Факт.** `GET /api/v1/audit/cells` приймає лише `from,to,documentId,limit,cursor`
(`AuditController.cs:31-34` ✔); SQL у `AuditReader.cs:32-41` ✔ читає
`RowKey, ColumnDefId, ChangedByUserId, Origin, IsLateEdit`, але фільтрувати за
ними не вміє. Макет: інспектор комірки → вкладка History (історія ОДНІЄЇ
комірки) і фільтри журналу `author / origin / late only`.

**Контракт** — замість росту позиційних параметрів увести запис-фільтр:

```csharp
// src/Ecr.Application/Ports/IAuditReader.cs
/// <summary>Фільтр журналу змін комірок. Вікно часу обов'язкове — воно відсікає партиції.</summary>
public sealed record CellChangeFilter(
    DateTime From,
    DateTime To,
    long? DocumentId = null,
    string? RowKey = null,
    int? ColumnDefId = null,
    int? ChangedByUserId = null,
    string? Origin = null,
    bool LateOnly = false);

public Task<PagedResult<CellChangeView>> ReadCellChangesAsync(
    CellChangeFilter filter, CursorRequest page, CancellationToken ct);
```

**SQL** (`AuditReader.cs`) — умови додаються лише за наявності значення, кожне
**іменованим параметром** (правило «жодного значення в текст запиту» — `:105-108` ✔):

```csharp
var where = new StringBuilder("ChangedAt >= @from AND ChangedAt < @to AND Id > @after");
void And(string clause, string name, object value, SqlDbType type, int size = 0)
{
    where.Append(" AND ").Append(clause);
    var p = command.Parameters.Add(name, type);
    if (size > 0) { p.Size = size; }
    p.Value = value;
}

if (filter.DocumentId is { } d)      And("DocumentId = @documentId", "@documentId", d, SqlDbType.BigInt);
if (filter.RowKey is { } r)          And("RowKey = @rowKey", "@rowKey", r, SqlDbType.NVarChar, 100); // ✎ 2026-09-19: було 200 — помилка, див. нижче
if (filter.ColumnDefId is { } c)     And("ColumnDefId = @columnDefId", "@columnDefId", c, SqlDbType.Int);
if (filter.ChangedByUserId is { } u) And("ChangedByUserId = @userId", "@userId", u, SqlDbType.Int);
if (filter.Origin is { } o)          And("Origin = @origin", "@origin", o, SqlDbType.NVarChar, 32);
if (filter.LateOnly)                 where.Append(" AND IsLateEdit = 1");
```

⛔ **✎ 2026-09-19, виправлення факту.** `RowKey` у схемі — `nvarchar(100)`, не
200 (`11-audit-tables.sql:48`, звірено запитом до живої `EcrDev`). Параметр
завширшки 200 тихо дав би **другий запис у кеші планів** — рівно той дефект
`WR-01`, від якого застерігає абзац вище. Ширина параметра мусить збігатися зі
схемою до символа.

⚠ Типізовані параметри, не `AddWithValue` — той самий урок, що `WR-01` у №14
(нетипізований `nvarchar(4000)` ламає кеш планів). Розмір `RowKey` звір зі
схемою (`sqlcmd … INFORMATION_SCHEMA.COLUMNS`), не з пам'яті.

⏱ **Індекс.** Є `IX_CellChange_Cell (DocumentId, TableRowId, ColumnDefId, ChangedAt DESC)`
(`Sql/11-audit-tables.sql:61-65` ✔) — за `TableRowId`, а клієнт знає `RowKey`.
Перед мержем зніми план запиту «одна комірка за 12 місяців» на базі після
`smoke.ps1`. Якщо йде скан партицій — не латай індексом наосліп: або резолв
`RowKey → TableRowId` одним запитом до `doc.TableRow` перед читанням журналу,
або окремий PR 🗄 з індексом. Рішення запиши в PR.

**Правила обробника** (`GetCellChangesHandler`): `rowKey`/`columnDefId` без
`documentId` → `400 ECR-AUD-0400` (комірка без документа не адреса); для
історії однієї комірки стеля вікна розширюється до 13 місяців (зараз стеля
вікна — в обробнику ◐), бо користувач питає «що було з цим числом», а не «що
сталося в березні».

⚠ **Право.** Зараз увесь ендпоінт — `Security.ViewAudit`. Історію СВОЄЇ комірки
має бачити той, хто бачить документ. Рішення (судження, зафіксоване тут): якщо
задано `documentId + rowKey + columnDefId`, достатньо `Document.View` на цей
документ (через той самий `IAccessDecisionService`, що й читання зрізу); без
них — як було, `Security.ViewAudit`.

**Тести:** фільтр за коміркою повертає лише її рядки (мутація: прибрати
`RowKey = @rowKey` → у видачі чужі рядки → тест падає); `lateOnly`; `rowKey` без
`documentId` → `400`; користувач із `Document.View` без `Security.ViewAudit`
читає історію комірки свого документа й отримує `403` на загальний журнал.

---

### ~~BE-04 · `TableDefId` у знахідках валідації ✔~~

✎ **2026-09-21: зроблено.** Обидва відображення несуть `m.TableDefId`
(`DocumentsController.cs:186`, `:245`), поле в DTO — `:568`. «Відображення
його викидає» нижче — стан до реалізації.

**Факт.** `ValidationMessage` має `int TableDefId`
(`Validation/ValidationMessage.cs:19` ✔), а відображення в DTO його **викидає**:
`new ValidationFindingDto(m.Severity.ToString(), m.RuleCode, m.Message, m.RowKey, m.ColumnCode, m.BlocksSave)`
(`DocumentsController.cs:153-154` ✔, запис — `:503-504` ✔). Панель Issues у
макеті веде «клац → стрибок до комірки»; без таблиці клієнт не знає, КУДИ
стрибати, коли той самий `RowKey` є в кількох таблицях аркуша.

**Зміна:** додати `int TableDefId` у `ValidationFindingDto` (перед `RowKey`) і в
обидва місця відображення (`Validate` і `GetLatest` — ◐ друге місце знайди
пошуком `new ValidationFindingDto(`). Якщо збережений підсумок
(`IValidationResultStore`) таблицю не зберігає — це 🗄 і окремий PR; спершу
перевір.

**Тест:** документ із порушенням у ДРУГІЙ таблиці аркуша → знахідка несе її
`TableDefId` (мутація: підставити `0` → падає).

---

### ~~BE-05 · `jobId` перерахунку у відповіді на запис комірок ✔~~

✎ **2026-09-21: зроблено.** `Dto/PatchCellsResponse.cs:30`
`string? RecalculationJobId`; «три поля» нижче — стан до реалізації.

**Факт.** `EnqueueRecalculationAsync` повертає `Task`, а не ідентифікатор:
результат `jobs.EnqueueAsync<IFormulaRecalculationJob>(…)` (це `Task<string>` ✔,
`IBackgroundJobScheduler.cs:21`) відкидається (`PatchCellsHandler.cs:1245-1249` ✔).
`PatchCellsResponse` має три поля: `AppliedCells, RowVersions, Validation` ✔.
Макет показує в статус-рядку сітки «Recalculating… → Recalculated 14:02» — без
`jobId` клієнт може лише вгадувати таймером.

**Зміна:**

```csharp
// Dto/PatchCellsResponse.cs
public sealed record PatchCellsResponse(
    int AppliedCells,
    IReadOnlyDictionary<string, string> RowVersions,
    IReadOnlyList<ValidationMessageDto> Validation,
    string? RecalculationJobId = null);
```

`EnqueueRecalculationAsync` → `Task<string>`; у гілці
`deferRecalculationUntilMi02` (`:109-116` ✔) значення лишається `null` — і це
чесно: перерахунку ще не поставлено. Клієнт: `null` → статус-рядок мовчить.

⚠ Автор задачі читає її стан без `System.ViewHealth` лише якщо в
`EnqueueAsync` передано `createdByUserId` (Q-156 ✔ `:21`). Перевір, що виклик у
`PatchCellsHandler` його передає; якщо ні — додай, інакше редактор отримає `403`
на власний перерахунок.

**Споживач:** `useCellPatch.ts` кладе `recalculationJobId` у стан документа;
статус-рядок опитує `GET /jobs/{id}` з інтервалом ≥ 2 с і зупиняється на
термінальному стані. ⛔ Не `invalidateQueries` зрізу на кожне опитування — це
відкотить `CL-01…03` з №14.

---

### ~~BE-06 · Конфлікт версій: чиє значення, хто, коли ✔~~

✎ **2026-09-21: зроблено (основна гілка).** `PatchCellsHandler.cs:533`
`ReadLastChangesAsync`, `CellConflictDto.cs:51` `TheirOrigin`; «заглушки
`null, "", clock.UtcNow`» нижче — стан до реалізації. ⚠ Гілка «розійшовся
весь рядок» (`ColumnCode="*"`) свідомо віддає `null` — дочитка окремим кроком.

**Факт.** `CellConflictDto` має `TheirValue, TheirUser, TheirChangedAt`
(`Dto/CellConflictDto.cs:9-16` ✔), але обробник заповнює їх заглушками:
`null, "", clock.UtcNow` (`PatchCellsHandler.cs:388`, `:396-397` ✔). Діалог
конфлікту в макеті («Their value 12.40 · A. Serikbayev · 14:02 — Keep mine / Take
theirs») сьогодні показав би порожнечу й **поточний час сервера як час чужої
зміни** — це гірше за відсутність поля.

**Зміна.** Після того, як зібрано перелік розбіжних `(RowKey, ColumnCode)`,
ОДНИМ запитом дочитати чинні значення й останню зміну:

- чинне значення — з того ж читання, яким обробник уже дістає `context.Versions`
  (◐ подивись, чи значення вже в пам'яті; якщо так — нуль додаткових запитів);
- автор і час — `aud.CellChange` за `(DocumentId, TableRowId, ColumnDefId)`
  `TOP 1 … ORDER BY ChangedAt DESC` по індексу `IX_CellChange_Cell` ✔, з вікном
  `ChangedAt >= @from` (інакше — всі партиції); ім'я — `sec.User.DisplayName`.

Порт: `IAuditReader.ReadLastChangesAsync(long documentId, IReadOnlyCollection<(long TableRowId, int ColumnDefId)> cells, DateTime since, CancellationToken ct)`.

⚠ Це шлях ПОМИЛКИ, не гарячий шлях: додатковий запит прийнятний. Але стеля —
не більше 100 комірок на відповідь (решта — лічильником `details.moreConflicts`).

⚠ `TheirUser` — відображуване ім'я, **не логін і не SID** (R-A2, D-86).
Системна зміна (перерахунок, імпорт) → `TheirUser = "system"` + `Origin`; для
цього в DTO додати `string TheirOrigin`.

**Тести:** два послідовні PATCH з однією `BaseVersion` → `409`, у конфлікті
`TheirValue` = значення першого, `TheirUser` = ім'я першого, `TheirChangedAt` у
межах секунди від першого запису (мутація: повернути `clock.UtcNow` → тест із
замороженим годинником, зсунутим на годину, падає).

---

### ~~BE-07 · Публічні дані для екрана входу ◐~~

✎ **2026-09-21: зроблено.** `PublicController.cs:39` — `GET /api/v1/public/bootstrap`
(`[AllowAnonymous]`); «анонімних дій дві» нижче — стан до реалізації.

**Факт.** Анонімних дій дві: `LoginLocal` (`AuthController.cs:64` ✔) і каталог
рядків (`UiStringsController.cs:40` ✔). Макет екрана входу показує: перелік мов,
версію продукту, доступні способи входу (Windows / локальний), а після
невдалої спроби — «лишилось N спроб» / «заблоковано на M хв».

**Контракт:**

```csharp
// GET /api/v1/public/bootstrap  [AllowAnonymous]
public sealed record PublicBootstrapResponse(
    string ProductVersion,                    // з AssemblyInformationalVersion
    IReadOnlyList<PublicLanguageDto> Languages, // Code, NativeName, IsDefault
    bool WindowsSignInEnabled,
    bool LocalSignInEnabled);
```

⛔ **Без політики паролів і без лічильника спроб в анонімній відповіді.**
«Лишилось 2 спроби» для НЕІСНУЮЧОГО логіна проти «лишилось 2 спроби» для
існуючого — це оракул перебору логінів. Рішення: повідомлення про блокування
повертається лише у відповіді `401/423` на сам вхід і однакове для існуючого й
неіснуючого логіна. Лічильник «лишилось N» у макеті **не реалізується** — у
`DIRECTIVE-15.md` це рішення `D15-14`.

### ✎ 2026-09-19: три твердження цього розділу виправлено за кодом

Усі три перевірені пошуком, а не переказані.

1. ~~`details.retryAfterSeconds`~~ — такої подробиці **немає й не буде**.
   Час очікування несе стандартний заголовок `Retry-After`
   (`LoginRateLimiting.cs:206-208`), і дублювати його в тілі означало б два
   джерела, які можуть розійтися. ⚠ Код відмови тепер **`ECR-AUTH-0429`**, а
   не `ECR-AUTH-0423`: `0423` означає «обліковку заблоковано», а її ніхто не
   блокував — див. #368.
2. ~~`GET /api/v1/auth/password-policy`~~ — маршруту **не існує**, і директива
   писала про нього в теперішньому часі. **Рішення: не заводити.** Політики
   паролів не просить ніхто — ні клієнт (`0` збігів по `src/Ecr.Web/src`), ні
   макет (`0` по `docs/design/hybrid`). Ендпоінт без споживача суперечить
   `D15-05`; коли екран зміни пароля його справді потребуватиме, він приїде
   разом із тим екраном і своїм тестом.
3. ~~«обмежувач частоти покриває маршрут»~~ — **не покриває**:
   `LoginPathPrefix = "/api/v1/login"`, решта → `GetNoLimiter`. Твердження
   зафіксоване тестом `Обмежувач_частоти_цього_маршруту_НЕ_покриває`, який
   почервоніє тієї миті, коли префікс розширять (окремий крок після #368).

**Тести:** анонімний `GET` → `200` без кукі; відповідь не містить жодного поля
з переліку `[users, logins, attempts, policy]` — рекурсивний скан імен полів за
підрядком, щоб поле не «доросло» сюди пізніше під іншою назвою.

---

## 2. Хвиля B2 — середні (M)

Формат коротший: контракт, джерело даних, право, тести. Код — за ідіомою B1.
Кожен пункт — окремий PR; пункти з 🗄 — по одному, послідовно.

### BE-08 · Фонові задачі: «мої», фільтри, деталі ◐

✎ **2026-09-21: зроблено частково.** Фільтри є (`JobsController.cs:53-58`:
`state`, `code`, `mine`). Лишились поля: `JobSummary`
(`Ports/IBackgroundJobScheduler.cs:196-202`) — лише
`JobId, JobCode, State, Percent, UpdatedAt, StartedAt`, без
`CreatedByDisplayName`/`Message`/`ErrorCode` (`Message` відкладено свідомо,
коментар `:186-194`); `JobStatus` (`:218`) — без `Attempt/MaxAttempts/CorrelationId/DocumentId`.

- ~~`GET /api/v1/jobs?state=&code=&mine=true&limit=` — `mine=true` не вимагає
  `System.ViewHealth` і фільтрує за `itg.JobProgress.CreatedByUserId` (поле є ✔
  `IntegrationLogs.cs:480`). Без `mine` — як зараз.~~ ✎ зроблено, див. вище.
- `JobSummary` розширити: `CreatedAt, CreatedByDisplayName?, Message (конверт
  `JobProgressMessageEnvelope` → локалізується на клієнті), ErrorCode?`.
- `GET /jobs/{id}` → додати `Attempt, MaxAttempts, CorrelationId, DocumentId?`.
  ◐ Перевір, що з цього вже пишеться в `itg.JobProgress`; чого немає — 🗄
  окремим PR, і лише якщо поле реально показується (макет: `jobs-panel-failed`).
- ⛔ «Purge jobs» з макета (`jobs-dlg-purge-jobs`) **не реалізується**: очищення —
  справа `ReportRetentionJob`/регламенту, а не кнопки (рішення `D15-13`).

**UI:** шухляда «My tasks» у шапці (усі ролі) і `#/admin/jobs`.

### BE-09 · Перелік документів: те, що показує StatStrip і рядок ◐

Макет на `#/` показує смугу з ≤ 4 цифр (Draft · Submitted · Approved · With
issues) і в рядку — к-сть зауважень, «змінено ким/коли», позначку пізньої правки,
фільтри `state` і `mine`.

✎ **2026-09-21: зроблено частково.** Смуга — `DocumentsController.cs:84`
`GET /documents/summary`; `DocumentSummary` уже має
`ModifiedAt/ModifiedByDisplayName/ErrorCount/WarningCount`
(`Ports/IDocumentStore.cs:43-46`). **Лишилось («BE-09b»):** `HasLateEdits`
(0 збігів у `src`) і фільтри `state`/`mine` у `GET /documents`
(`DocumentsController.cs:48-51` — лише `limit, cursor, projectId, periodKey`).

- ~~`DocumentSummary` (`IDocumentStore.cs:26-33` ✔) розширити:
  `DateTime? ModifiedAt, string? ModifiedByDisplayName, int ErrorCount, int WarningCount, bool HasLateEdits`.~~
  ✎ зроблено, крім `HasLateEdits`.
  `ErrorCount/WarningCount` — з ОСТАННЬОГО збереженого підсумку валідації
  (`IValidationResultStore.GetLatestAsync` ✔ існує), **не** повторний прогін.
  Немає підсумку → `null`, і UI показує «—», не «0». ⛔ «0 зауважень» для
  документа, який ніколи не перевіряли, — це та сама брехня, що `A7-28`.
- `GET /documents?periodKey&projectId&state=&mine=true&cursor&limit` — `state`
  фільтрує за агрегованим станом аркушів; `mine` — документи, де користувач має
  право редагувати хоч один аркуш (◐ дорогий шлях: зроби через наявний
  `IAccessDecisionService` пакетно, заміряй ⏱ на 500 документах).
- ~~`GET /documents/summary?periodKey&projectId` → `DocumentListSummaryResponse(int Draft, int Submitted, int Approved, int Rejected, int WithIssues)` —
  ОДИН `GROUP BY`, не завантаження переліку. ⚠ Лічить лише документи, які
  користувач бачить (той самий предикат доступу, що й перелік) — інакше смуга
  і таблиця розійдуться.~~ ✎ **2026-09-21:** зроблено, `DocumentsController.cs:84`.

### ~~BE-10 · Заповненість таблиць документа ◐~~

✎ **2026-09-21: зроблено.** `DocumentsController.cs:362`
`GET {id}/tables/status`, DTO — `Documents/Dto/TableStatusDto.cs:34`.

Дерево аркушів у макеті: «68 of 91 tables filled», крапка помилки біля таблиці.

`GET /documents/{id}/tables/status?periodKey` →
`IReadOnlyList<TableStatusDto(int TableDefId, string SheetCode, int FilledCells, int InputCells, int ErrorCount, int WarningCount)>`.

⚠ `InputCells` — лише комірки, які ЛЮДИНА має заповнити (не формульні, не
закриті правилом періоду). Без цього «заповнено 40 %» означатиме «60 % — формули».
Один set-based запит по `doc.CellValue` з `PeriodKey` у предикаті (урок `WR-05`).
⏱ Заміряти на документі з 91 таблицею: ціль ≤ 150 мс; якщо більше — кешувати за
ревізією документа (механізм ревізій — `#346`).

### ~~BE-11 · Хто й коли подав/погодив; історія станів аркуша ◐~~

✎ **2026-09-21: зроблено.** `DocumentWorkflowHistoryController.cs:26`
`GET {id}/workflow/history`, DTO — `Workflow/GetWorkflowHistoryHandler.cs:19`.

`GET /documents/{id}/workflow/history?periodKey` →
`IReadOnlyList<WorkflowEventDto(string SheetCode, string FromState, string ToState, string Action, string ByDisplayName, DateTime At, string? Reason)>`.
Джерело — `IWorkflowStore.GetSnapshotsAsync` і `ApprovalState.cs:34-44` ◐.
Макет: бейдж стану з підказкою «Submitted by … 14:02», вкладка History шухляди
документа, причина відхилення на банері.

### BE-12 · Користувачі: скидання пароля, блокування, останній вхід ◐ (🗄 можливо)

- ~~`POST /security/users/{id}/reset-password` → `ResetPasswordResponse(string TemporaryPassword)`;
  доменні методи є: `User.SetPassword`, `RequirePasswordChange` ◐. Тимчасовий
  пароль показується ОДИН раз (макет `sec-dlg-result-password`), у журнал і лог
  не потрапляє (тест: лог-рядок запиту не містить значення).~~
  ✎ **2026-09-21, рішення людини** (`docs/tz/10-decisions.md`, журнал за
  2026-09-21): сервер тимчасовий пароль НЕ генерує — новий пароль задає
  адміністратор у запиті, далі примусова зміна при вході (`RequirePasswordChange`).
  Вимога «значення не потрапляє в журнал і лог» лишається чинною.
- `POST /security/users/{id}/lock` / `…/unlock` з обов'язковою причиною
  (`ReasonModal` уже є на клієнті).
- `LastSignInAt` у переліку користувачів — якщо колонки немає, 🗄.
- ⛔ Не можна заблокувати себе й останнього адміністратора — `409 ECR-SEC-0409`.
  Це саме той тест, який має падати на мутації.

### BE-13 · Рядки інтерфейсу: покриття перекладу ◐

~~`GET /ui-strings/coverage` → по мовах `Total, Translated, Missing`;
`GET /ui-strings?lang=&missingOnly=true` — «сирі» значення **без fallback на `en`**
(зараз каталог віддає вже зі спадком, і «відсутній переклад» невидимий);
перевірка плейсхолдерів при збереженні (`{0}`/`{name}` у перекладі ті самі, що
в `en`) → `422 ECR-L10N-0422`.~~ ✎ **2026-09-21: частина 1 зроблена.**
`UiStringsController.cs:38` `GET coverage`, `:60` `missingOnly`, перевірка
плейсхолдерів — `SetUiStringHandler.cs:58`/`:99`.

Імпорт/експорт (CSV) — окремим PR після цього. ✎ **2026-09-21: ще НЕ
зроблено** (0 збігів `csv` у `Application/Localization` і
`UiStringsController.cs`); обсяг — `DIRECTIVE-15-DECISIONS.md` рішення 16 і §4.

### ~~BE-14 · Ролі: клонувати, перейменувати, видалити ◐~~

✎ **2026-09-21: зроблено.** `SecurityController.cs:118` clone, `:135` code,
`:154` delete.

Видалення ролі з призначеннями → `409` з кількістю; системні ролі з сіду — не
видаляються й не перейменовуються. ⚠ Модель грантів лишається як є: суб'єкт —
**роль**, набір замінюється цілком. Інтерфейс підлаштовується під це (див.
`DIRECTIVE-15.md` §4), а не навпаки.

### BE-15 · «Де використовується» + видалення: одиниці, вирази ◐

Єдина форма: `GET /<resource>/{id}/usage` →
`UsageResponse(int Total, IReadOnlyList<UsageItemDto(string Kind, string Id, string Label, string? Route)>)` —
перші 20 + загальна кількість. Діалог видалення в макеті завжди спершу показує
залежних (`un-delete-blocked`, `ex-delete-blocked`). `PUT/DELETE /units/{id}` —
видалення з посиланнями `409`.

✎ **2026-09-21: одиниці — зроблено частково, вирази — предмета немає.**
Зроблено: `UnitsController.cs:20` `GET {id}/usage`, `:33` `DELETE {id}`.
~~`PUT /units/{id}`~~ — **ще немає** (у контролері лише GET, POST, usage,
delete, convert). ~~«вирази» (`ex-delete-blocked`)~~ — доменної сутності
виразу з id немає, `ExpressionsController.cs` має лише `validate` (`:53`) і
`metadata` (`:85`); «usage/delete виразу» не має предмета, доки не визначено,
що вважається виразом на `/admin/expressions`.

### BE-16 · Журнал структурних змін і експорт журналу ◐

~~`IAuditReader.ReadStructureChangesAsync` існує ✔ (`AuditReader.cs:89`), але лише
«для однієї сутності». Потрібен загальний перелік із вікном часу й курсором:
`GET /audit/structure?from&to&entityType&cursor`.~~ ✎ **2026-09-21: перелік
зроблено** — `AuditController.cs:107` `GET /audit/structure`. **Лишився
експорт** (0 збігів `export` в `AuditController.cs`). Експорт — фоновою задачею
(`202 + jobId`, файл — через наявний механізм завантаження звітів ◐), не
синхронним CSV: журнал за рік — мільйони рядків.

### ~~BE-17 · Знімки звітності: картка, перевірка, завантаження ◐~~

✎ **2026-09-21: зроблено.** `ReportsController.cs:142`
`POST snapshots/{id}/verify`, `:181` `GET snapshots/{id}/export.xlsx`;
`Report.Export` перевіряє `Reporting/ExportSnapshotHandler.cs:38` (фраза «не
перевіряється ніде» нижче — стан до реалізації).

`IsCurrent` і `ContentHash` уже є в DTO ◐ і не показуються. Додати
`POST /snapshots/{id}/verify` → перерахувати хеш і порівняти
(`SnapshotVerifyResponse(bool Matches, string Stored, string Actual)`),
`GET /snapshots/{id}/content` — право `Report.Export` (зараз не перевіряється
ніде — див. BE-28).

### ~~BE-18 · Стан системи: версія, транспорт, шлях логів ◐~~

✎ **2026-09-21: зроблено.** `SystemHealthController.cs:28` `GET /health/facts`,
`:53` `GET /health/partitions/script`; `LogDirectory` —
`Health/SystemHealthHandlers.cs:59`.

Розширити відповідь health наявними фактами: `ProductVersion`, `StartedAt`,
`Environment`, стан транспорту сповіщень, каталог логів. ⛔ «Create partitions»
з макета — **не кнопка** (D-66: застосунок не виконує DDL). Замість неї сервер
віддає готовий текст команди для DBA: `GET /health/partitions/script` → `text/plain`,
а UI показує «Copy command» + посилання на runbook (рішення `D15-12`).

### BE-19 · Пошук для командної палітри ◐

`GET /search?q=&limit=10` → `SearchHitDto(string Kind, string Id, string Title, string? Subtitle, string Route)`.
Документи — через наявний `DocumentIndexValue` ◐; шаблони/довідники — по назві.
Усе — крізь фільтр доступу. Мінімум 2 символи; обмежувач частоти. Навігаційні
пункти палітри (екрани, дії) — **клієнтські**, сервер шукає лише дані.

### BE-20 · Налаштування користувача на сервері 🗄

`GET/PUT /me/preferences` → `UserPreferencesDto(string Theme, string Density, string Language, int? LastPeriodKey, int? LastProjectId)`.
Нова таблиця `sec.UserPreference` (JSON-колонка + `RowVersion`) — окремий
PR-міграція ПЕРШИМ. До його мержу клієнт тримає це в `localStorage` (як зараз) —
тобто пункт **нічого не блокує** і йде останнім у B2.

---

## 3. Хвиля B3 — великі (L), після відповідей людини

Не стартувати без рішення з `DIRECTIVE-15.md` §3. Тут — лише рамка, щоб
рішення мало ціну.

| ID | Що | Що вже є в домені | Питання |
|---|---|---|---|
| BE-21 | ~~Джерела даних: CRUD, заміна секрету, розклад, «перевірити з'єднання», «зібрати зараз»~~ ✎ **2026-09-21: рядок застарів у трьох місцях із п'яти.** «Розклад» зроблено раніше (`GET/POST/PUT/DELETE /api/v1/collection-schedules`, `BE-21b`), «зібрати зараз» існує від Етапу 5 (`POST /api/v1/sources/{id}/collect` → `202 + jobId`), а **«заміну секрету» знято рішенням людини** (див. колонку питання). Лишалося і зроблено саме це: `GET/POST/PUT/DELETE /api/v1/data-sources` і `POST /api/v1/data-sources/{id}/test`. Права наявні: перелік — `Integration.View` (його ПЕРШИЙ викликач, `BE-28`), правка й проба — `Integration.Manage`. Проба опитує джерело ТИМ САМИМ адаптером, яким збирають (`IExternalDataSource.DiscoverAsync`), причина обов'язкова й іде в журнал безпеки, друга проба того самого джерела — `409 ECR-JOB-0409`. Видалення джерела із сутностями збору або розкладами — **заборона, не каскад** (`409` із лічильниками): каскад стер би `ext.RawDataPoint` і журнал покриття. Міграції не знадобилося. | ~~`DataSource`, `CollectionSchedule`, `ISecretProvider` ◐; право `Integration.EditSchedule` у сіді є, ніде не перевіряється~~ ⚠ `Integration.EditSchedule` перевіряється з `BE-21b`; невикритим лишався `Integration.View` — його й закрито тут. `ISecretProvider` **не розширювався**: у базі як було, так і лишається лише ІМ'Я секрету. | ~~Q15-06: автентифікація PI Web API — факт замовника~~ ✎ **відповідь є, і вона ЗВУЖУЄ задачу.** `DIRECTIVE-15-DECISIONS.md` (рішення 5): **Windows-автентифікація службового облікового запису; секретів у застосунку немає.** Отже ані сховища секретів, ані `PUT …/{id}/secret`, ані поля «секрет» у формі — на відміну від каналів сповіщень (`BE-33`), де секрет неминучий. У відповіді лишилося `hasSecret` (ознака того, що середовище все-таки дає секрет під це джерело), а «write-only» перетворилося на перевірку, якої без цього рішення не знадобилося б: адреса джерела не має нести облікових даних (`422 err.ECR-REQ-0422.dataSourceEndpointCarriesSecret`) — коли сховища секретів немає, єдиний спосіб покласти пароль у базу — вписати його в несекретне поле, а для транспорту `Sql` адреса і є рядком з'єднання. |
| BE-22 | ~~Огляд кампанії: усі проєкти × період, зведення~~ ✎ **2026-09-21: зроблено.** `GET /api/v1/campaign/summary?periodKey=` → `CampaignSummaryResponse(periodKey, totalProjects, projects[])`, у рядку — код і назва проєкту плюс лічильники `documents / draft / submitted / approved / rejected / snapshots`. Агрегат етапів — той самий, що в смузі переліку документів (`BE-09`), але **згрупований по проєктах і без предиката грантів**; перелік іде ВІД `doc.Period`, тому проєкт, у якому кампанія ще не починалася, видно з нулями, а не втрачено. Стеля `GetCampaignSummaryHandler.MaxProjects = 200`, повне число — в `totalProjects`. Ані таблиць, ані колонок, ані міграції не знадобилося. | ~~—~~ ⚠ колонка не брехала, але й не допомогла: у домені справді не було нічого, а от **питання вже мало відповідь** | ~~Q15-07: хто бачить чужі проєкти~~ ✎ **відповідь є, і вона перевертає очікуване.** `DIRECTIVE-15-DECISIONS.md` (рішення 6): **окреме право `Report.ViewCampaign`, видається явно; лише лічильники станів, без значень.** Тобто межа — НЕ грант на проєкт: огляд свідомо віддає чужі проєкти, бо питання «хто затримує кампанію» без них не має відповіді. Право заведено небезпечним (`IsDangerous = 1`) — саме це й тримає його поза шаблоном `Report.%` вбудованого `Approver`, тобто робить видачу явною. |
| BE-23 | ~~Міграція документів на нову версію шаблону~~ ✎ **2026-09-21: знято** рішенням людини (`DIRECTIVE-15-DECISIONS.md`, рішення 4: документ назавжди на своїй версії). | ~~право `Template.Migrate` у сіді, не використовується ◐~~ ✎ право видалено із сіду: `09-seed.sql:41-42` (`DELETE … WHERE Code = N'Template.Migrate'`), доказ — `SeedTests.cs:124`. | ~~Q15-05~~ ✎ відповідь є (рішення 4) |
| BE-24 | Довідники: чернетка визначення → публікація; ~~історія/«де використано»/~~імпорт записів ✎ **2026-09-21:** історія й «де використано» зроблено — `RegistriesController.cs:133` `GET {code}/history`, `:155` `GET {code}/usage`. Лишилось: чернетка → публікація (`PUT {code}/definition`, `:107`, пише одразу) та імпорт записів (маршруту немає). | ~~версіонування визначень ◐~~ ✎ **у домені його НЕМАЄ:** `Entities/Configuration/RegistryDef.cs` — 0 збігів `IsDraft`/`Publish(`/`Status`; стан чернетки потребує 🗄. `Registry.Publish` досі без викликача (`UncheckedPermissionTests.cs:42`). | — (судження; різати на 3–4 PR) ✎ право `Registry.Publish` **лишити**, викликач — крок 2 (`tz/10-decisions.md:456`) |
| BE-25 | Методики: матриця покриття, ~~запит рев'ю,~~ видалення чернетки, diff версій ✎ **2026-09-21:** запит рев'ю знято рішенням 7; решта **ще не зроблена** — у `MethodologiesController.cs` немає ні coverage, ні `DELETE versions/{vid}`, ні diff версій (є лише diff РЕЗУЛЬТАТІВ публікації, `:525-539`). | — | ~~Q15-08: чи є процес рев'ю методик у замовника~~ ✎ відповідь є (`DIRECTIVE-15-DECISIONS.md`, рішення 7): окремого кроку рев'ю немає, публікує той, хто має право, з причиною |
| BE-26 | ~~Шаблон: редагування картки, архівування, лічильник залежних~~ ✎ **2026-09-21: зроблено.** `GET/PUT /api/v1/templates/{id}`, `POST …/archive`, `POST …/restore` — права наявні (`Template.View`/`Template.Edit`), нового не заводилось; заборонений перехід віддає `409 ECR-TMPL-0409` із домену (суфікс `-0409` мапить `ExceptionHandlingMiddleware`, арм не потрібен); журнал безпеки — `TemplateArchived`/`TemplateRestored` разом із лічильниками. Картка редагує **лише назву мовами**: колонки під опис у `cfg.Template` немає, а `Code` — бізнес-ключ і незмінний. Лічильник (версії / опубліковані / проєкти / документи) їде ТІЄЮ САМОЮ відповіддю, що й картка. | ~~—~~ ⚠ **колонка брехала, і саме так, як найдорожче.** `Template.IsActive` + `Template.Deactivate()` існували від Етапу 1 і не викликалися ніде — це і є ознака «архівовано», тож **міграції не потрібно**. Пошук за `IsArchived\|Archived` у `src/Ecr.Domain` (саме його й радить процедура перед BE-26) дає **нуль** і веде до хибного «поля немає → СТОП». Шукати треба ЗМІСТ, а не слово. | — |
| BE-27 | ~~Мапінг: пауза, прийняти зміну одиниці, видалення~~ ✎ **2026-09-21: зроблено.** `POST /api/v1/entity-field-maps/{id}/pause` і `…/resume`, `POST …/accept-unit-change`, `DELETE …/{id}` — право наявне (`Integration.Manage`), нового не заводилось. Конфлікти стану — `409 ECR-INT-0409` (родина `INT` наявна; новий код у ній, не нова родина). **Міграції не знадобилося.** Пауза = `EntityFieldMap.IsActive`, і обидва шляхи збору вже фільтрували за ним: `CollectionStore.GetFieldMapsAsync` (що читати з джерела) і `MaterializeCollectedDataJob` (що класти в комірки). Приймання зміни одиниці пише в журнал безпеки `MappingSourceUnitChangeAccepted` з обома одиницями — id і **кодами**: довідник живий, і самого числа через рік буде замало. **Видалення мапінгу зі зібраними даними заборонене** (`409` з `collectedPoints` / `firstPointAt` / `lastPointAt`): точки `ext.RawDataPoint` пояснює саме мапінг — одиниця, рядок-адресат, згортка, — і вихід тут пауза, а не повтор запиту (той самий шаблон, що `ECR-REG-0409` у `BE-01`). | ~~—~~ ⚠ **колонка брехала тим самим способом, що й у `BE-26`.** `EntityFieldMap.IsActive` + `Deactivate()` існували від Етапу 5 і не викликалися ніде — це і є «пауза», тож міграції не потрібно; `Deactivate()` замінено парою `Pause()`/`Resume()` з відмовою на повторний перехід. Виявлення зміни одиниці теж уже було: `SourceUnitConverter.EnsureDeclaredUnit` зупиняє збір кодом `ECR-INT-0422` (`ФВ-16.9`) — бракувало лише способу цю зупинку зняти. Шукати треба було ЗМІСТ («чим спиняється збір за одним полем»), а не слово `Paused`, якого в домені немає й не було. | — |
| BE-29 | ~~Ручне відкриття/закриття/архівування періоду~~ ✎ **2026-09-21: рядок застарів і коштував пів години розвідки.** Відповідь на Q15-02 звузила обсяг до ОДНОГО перевідкриття закритого періоду з причиною (`close`/`archive` лишаються за `PeriodStateJob`), а саме перевідкриття **вже було реалізоване**: `POST /periods/{id}/reopen`, право `Period.Reopen`, перехід через домен, журнал із причиною, колонки `ReopenedUntil`/`ReopenReason`, узгодження з задачею. Бракувало лише доказу на рівні HTTP — додано (`PeriodReopenTests`). | ~~стан міняє ЛИШЕ `PeriodStateJob` ◐~~ зроблено | Q15-02 (відповідь є) |
| BE-30 | ~~«Підтвердити» знахідку узгодженості~~ ✎ **2026-09-21: рядок застарів і встиг задати хибну рамку задачі.** Відповідь людини на Q15-03 — «Ні. Знахідка зникає сама, коли наступна перевірка проходить»; підтвердження знято з обсягу, натомість лишається «Run check now». Зроблено саме це: `POST /api/v1/consistency/run` під `System.RunJob` (його ПЕРШИЙ викликач узагалі), обов'язкова причина в журнал безпеки, повторний запуск під час чинної перевірки → 409. Колонки «підтверджено» не заводилось — саме її й прибрало рішення. | ~~контролер свідомо лише читає ◐~~ зроблено інакше, ніж тут написано | Q15-03 (відповідь є) |
| BE-31 | ~~Відкликання поданого аркуша автором~~ ✎ **2026-09-21: зроблено** (#429). `DocumentWorkflowHistoryController.cs:37` `POST /api/v1/documents/{id}/recall` (аркуш і період — у тілі, `RecallSheetRequest`), `:53` `GET …/recall` — чи доступна кнопка; обробник `Workflow/RecallSheetHandler.cs:31`. ⚠ Маршрут на рівні документа, а не `…/sheets/{code}/recall`, як записано в рішенні 3. | ~~—~~ ✎ домен: `ApprovalState.cs:260` `Recall(...)`, `ApprovalEvent.cs:26` `ApprovalAction.Recall = 6` (byte, міграції не знадобилося) | ~~Q15-04~~ ✎ відповідь є (рішення 3) |

---

## 4. BE-28 · Борг: ~~вісім прав~~ у сіді, яких ніхто не перевіряє ◐

✎ **2026-09-21: з восьми лишилось ОДНЕ — `Registry.Publish`** (виняток у
`tests/Ecr.Architecture.Tests/UncheckedPermissionTests.cs:42`, закривається
кроком 2 `BE-24`; рішення людини — право лишити, `tz/10-decisions.md:456`).
Решта: `Template.Migrate` — видалено із сіду (`09-seed.sql:41-42`);
`Report.MarkSubmitted` — прибрано з каталогу рішенням людини
(`tz/10-decisions.md:456`); `Document.Delete` — `Documents/DeleteDocumentHandler.cs:30`;
`Report.Export` — `Reporting/ExportSnapshotHandler.cs:38`; `Integration.View` —
`Integration/DataSourceHandlers.cs:57`; `Integration.EditSchedule` —
`Integration/CollectionScheduleHandlers.cs:58`; `System.RunJob` —
`Consistency/RunConsistencyCheckHandler.cs:45`.

~~`Template.Migrate`, `Registry.Publish`, `Document.Delete`, `Report.MarkSubmitted`,
`Report.Export`, `Integration.View`, `Integration.EditSchedule`, `System.RunJob`.~~
Право, яке можна видати й яке нічого не відкриває, — це брехня в матриці доступу:
адміністратор бачить галочку й вірить їй.

**Сторож** (`tests/Ecr.Architecture.Tests`): кожен код із сіду `sec.Permission`
зустрічається як рядковий літерал принаймні в одному файлі `src/Ecr.Application/**`.
Ratchet-перелік винятків — ~~ці вісім~~ ✎ зараз один (`Registry.Publish`), із правилом «перелік лише зменшується».
✎ **2026-09-21: сторож є** — `UncheckedPermissionTests.Кожне_право_з_сіду_перевіряє_обробник_або_воно_в_переліку`,
ratchet у два боки (`UncheckedPermissionTests.cs:34-47`).
⚠ Пам'ятка `source-text-guards-are-brittle`: шукати літерал у лапках, не слово;
ratchet із `NotEmpty` вимагає тримати борг живим — тут краще `Assert.Equal(очікуваний перелік, фактичний)`.

Кожне право закривається своїм `BE-xx` вище (`Report.Export` → BE-17,
`Integration.EditSchedule` → BE-21, ~~`Template.Migrate` → BE-23,~~
`System.RunJob` → «run now» у BE-30/узгодженості) або видаляється з сіду
рішенням людини. ✎ **2026-09-21:** `BE-23` знято, `Template.Migrate`
видалено із сіду (`09-seed.sql:41-42`) — саме «видаляється з сіду», а не BE-23.

✎ **2026-09-21: із восьми лишилося менше, і перелік вище про це мовчить.**
`Integration.EditSchedule` закрито розкладом збору (`BE-21b`), `System.RunJob`
— прогоном перевірки узгодженості (`RunConsistencyCheckHandler`),
`Integration.View` — переліком джерел (`ListDataSourcesHandler`, цей `BE-21`).
`Template.Migrate` рішенням людини на `Q15-05` **видаляється із сіду**, а не
закривається обробником. ~~Сторожа з ratchet-переліком у
`tests/Ecr.Architecture.Tests` на 2026-09-21 ще немає — тобто борг тримається
цим абзацом, а не машиною; перш ніж на нього посилатися, перевір
`git grep -n "\"Integration.View\"" -- src/Ecr.Application`.~~
✎ **2026-09-21, пізніше:** сторож є — `tests/Ecr.Architecture.Tests/UncheckedPermissionTests.cs`;
борг тримає машина, а не цей абзац (див. примітку на початку розділу).

---

## 5. Порядок і паралельність

```
B1:  BE-01 ─┐
     BE-02 ─┤  паралельно (≤ 3–4 гілки), мерж по одному,
     BE-03 ─┤  контракт перегенеровується останнім комітом
     BE-04 ─┤
     BE-07 ─┘
     BE-05 → BE-06      послідовно, обидва чіпають PatchCellsHandler.cs;
                        старт після мержу відкритих PR №14, що його чіпають
B2:  BE-08, BE-09, BE-10, BE-11   паралельно (різні контролери/обробники)
     BE-12 (🗄?) → BE-20 (🗄)     міграції — по одній
     BE-13…BE-19                  паралельно, по 3–4
B3:  після відповідей
```

✎ **2026-09-21:** рядок «B3: після відповідей» застарів — відповіді є
(`DIRECTIVE-15-DECISIONS.md` §1, рішення 1–18), і більшість B3 уже зроблено:
`BE-21` (a, b), `BE-22`, `BE-26`, `BE-27`, `BE-29`, `BE-30` (як «run check
now»), `BE-31`; `BE-23` знято. Лишаються `BE-21c` (обсяг не визначено),
`BE-24` кроки 2–4, `BE-25` — див. таблицю §3. Блоку в коді вище не
перекреслено, бо `~~` усередині коду не рендериться.

**Перетин за файлами, який треба пам'ятати:** `contracts/openapi.snapshot.json`
і `schema.d.ts` змінює КОЖЕН пункт. Це не привід серіалізувати роботу — це
привід перегенеровувати їх **після** `git merge origin/main` безпосередньо
перед пушем, а не на початку гілки.

## 6. Що зробити перед стартом кожного пункту (30 секунд)

```powershell
git fetch; git log origin/main --oneline -5
git grep -n "HttpDelete\|cancel\|RecalculationJobId" origin/main -- src/Ecr.Api/Controllers   # чи не зроблено вже
git worktree list                                                                            # чи не взяла паралельна сесія
```

Пам'ятка `parallel-agents-same-defect`: дві сесії вже брали ту саму задачу.
