# 01 — Безпека API й автентифікації (аудит 2026-11-10)

- **Область:** `src/Ecr.Api` (контролери, `Auth`, `Security`, `Middleware`, `Startup`, `Program.cs`, `appsettings*`), авторизація в `src/Ecr.Application` (`EditRules`, профілі доступу, `DocumentReadScope`, перевірки прав в обробниках), `src/Ecr.Adapters.Excel` (вивантаження/розбір), `src/Ecr.Adapters.PiAf` (вихідний HTTP/ODBC).
- **SHA `main`:** `f446e9b29` (Merge PR #811, `rc/20261111-p242`). **Дата:** 2026-11-11. Лише читання.
- **Охоплено повністю:** конвеєр `Program.cs` (порядок middleware), `AuthenticationSetup` (cookie, DataProtection, Negotiate), `IdleSession` (D-471), `SecurityStampMiddleware`, `CsrfOriginMiddleware`, `SimulationReadOnlyMiddleware`, `PasswordChangeMiddleware`, `AuthController`, `ChangePasswordHandler`, `LoginHandler` (гілки блокування), `ForwardedClientAddress`, усі контролери, додані після 2026-10-09 (≈35, перелік — `git log --diff-filter=A`), і їхні обробники: чат таблиці (`TableDiscussion*`), передачі й набори виконавців (`EditAssignment*`, `EditorSet*`, `TransferDocumentOwner`), відкриття місяця документа (`DocumentPeriodReopen*`), налаштування сеансу, `CellSource`/`PiRawData`/`PiEventData`, `SheetChanges`, `FormulaManualOverride`, `PermitBindings` (+preview/refresh), `Permits` (+usages), `PermitTypes` (+usage), `PermitTableImport`, `AirLimits`, `NaeTree` (+references), `DocumentSheetCopy`, `Jobs` (cancel/restart), експорт CSV (`CsvFormat`), Excel-експорт (ClosedXML 0.104.2), `XlsxSafetyGate`, політика адрес джерел (`DataSourceHandlers.ValidateAsync`, вторинна адреса D-462), SMTP-політика.
- **Поверхово:** `SecurityController`/ролі (D-121, D-470 — свідомі рішення), `ExceptionHandlingMiddleware` (лише пошук витоку `Exception.Message`/стеку), `ProjectsController`, `TemplateVersionsController`, `MigrateDocumentVersionHandler`, `PatchCellsHandler` (лише фільтр перегляду формул і `PatchCellStylesHandler`), PI ODBC (лише параметризація).
- **Не охоплено:** клієнт `src/Ecr.Web` (аудит 05), шар даних/сирий SQL (аудит 02), Worker, інсталятор/розгортання (S2 попереднього аудиту).

## Зведена таблиця

| ID | Важливість | Категорія | Назва | Зусилля | Ризик зміни | Рішення PO |
|---|---|---|---|---|---|---|
| S-1 | high | security (обхід меж читання) | Перегляд прив'язки дозволу (`permit-bindings/{code}/preview?documentId=`) віддає фактичні комірки повз Deny / D-214 / приховані колонки | S | низький | ні |
| S-2 | medium | security (підвищення привілеїв) | Скасування й перезапуск ЧУЖИХ і системних фонових задач — лише за безпечним `System.ViewHealth` (має роль «Аудитор»), хоча каталог прав каже «Запуск і скасування — `System.RunJob`» | S | середній | так |
| S-3 | medium | security (DoS пам'яті) | Межа одночасних розборів книги (`ImportPreviewGate`) стоїть лише на перегляді імпорту документа; ще 7 ендпоінтів розбирають `.xlsx` у повну модель ClosedXML без межі | S | низький | ні |
| S-4 | low | security (розкриття місця даних) | `GET /permits/{id}/usages` називає аркуш/таблицю/рядок/колонку з дозволом у схованих аркушах, таблицях, колонках і періодах поза областю ролі | S | низький | ні |
| S-5 | low | security (обхід C4) | `GET /registries/{code}/entries/{id}/references` віддає номери й строки дозволів без права читати реєстр шапки дозволів | S | низький | ні |
| S-6 | low | security (DoS/межа частоти) | `GET …/mention-candidates` без межі частоти: до 200 побудов профілю й меж читання на запит (аналог `edit-assignments/candidates` має `SearchRateLimitPolicy`) | S | низький | так (лише про POST) |

---

## Знахідки

### S-1 — high — Перегляд прив'язки дозволу віддає фактичні комірки документа повз межі читання (Deny / D-214 / приховані колонки)
**Категорія:** security (обхід авторизації на рівні аркуша/таблиці/колонки/періоду)

**Де:**
- `src/Ecr.Application/Permits/Bindings/PreviewPermitBindingHandler.cs:143-147` — для `documentId` перевіряється лише видимість документа (`Document.View` у проєкті), меж читання (`ReadScopeAsync`) немає;
- `src/Ecr.Application/Permits/Bindings/PreviewPermitBindingHandler.cs:211-255` — `OverlayFactsAsync` читає ВЕСЬ зріз екземпляра (`cells.ReadSliceAsync`, `:233`) і кладе значення всіх незіставлених колонок у відповідь (`:235`, `:244-250`);
- виклик: `src/Ecr.Application/Permits/Bindings/PreviewPermitBindingHandler.cs:124`;
- маршрут: `src/Ecr.Api/Controllers/PermitBindingsController.cs:108` (`GET api/v1/template-versions/{id}/permit-bindings/{code}/preview?documentId=&periodKey=`), контракт `docs/build/02-contracts.md:4937` (`Template.View` (+ `Document.View` з `documentId`)).

**Проблема:** усі інші читання комірок документа звіряють межі читання — зріз (`GetTableSliceHandler`), перегляд формул у відповіді PATCH (`PatchCellsHandler.cs:325-329`: «прихована колонка чи колонка поза межами читання автора не повертається, інакше відповідь PATCH стала б оракулом її значення»), джерело комірки (`CellSourceHandler.ResolveAsync`: `readable.CanReadTable/CanReadColumn`), проблеми розрахунку (`GetCalculationIssuesHandler`), оновлення прив'язки (`RefreshPermitBindingHandler.cs:100-103`, `RequireTableVisibleAsync`). Перегляд прив'язки — ні:
```csharp
// :145-146
var profile = await PermissionCheck.RequireInAnyProjectAsync(access, currentUser, DocumentPermission, ct)...;
await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, DocumentPermission, ct)...;
// :233-235, :247-249
var slice = (await cells.ReadSliceAsync(instance.TableInstanceId, periodKey, ct)...).ToLookup(c => c.Address.TableRowId);
var columnsById = sheetTable.Columns.Where(c => !c.IsDeleted).ToDictionary(c => c.Id);
...
if (columnsById.TryGetValue(cell.Address.ColumnDefId, out var column) && !row.Locked.Contains(column.Code))
    ((Dictionary<string, object?>)row.Cells)[column.Code] = FactValue(column.DataType, cell.Value);
```
Не враховуються: Deny на аркуш/таблицю/колонку (ФВ-6.6, S6), роль, звужена аркушами чи періодами (D-214), прихована колонка (`ColumnDef.IsHidden`), період, якого читач не бачить.

**Сценарій відтворення:**
1. Користувач U з вбудованою роллю `DataEntry` (вона має `Template.View`, `Document.View`, `Registry.View` — `src/Ecr.Infrastructure/Persistence/Sql/seed/00-prelude.sql:579-592`) і грантом `Read` на проєкт P; на таблицю T (таблиця аркуша з прив'язкою дозволу в режимі `Rows`) — `ResourceGrant(..., ResourceKind.Table, T, …, isDeny: true)`, або його роль звужена періодом 202601.
2. `GET /api/v1/documents/{doc}/tables/{inst}/slice?periodKey=202606` → `404` (таблиця схована) — очікувано.
3. `GET /api/v1/template-versions/{tv}/permit-bindings/{code}/preview?documentId={doc}&periodKey=202606` → `200`, у `rows[i].cells` — фактичні значення незіставлених колонок (наприклад «Fact» — фактичні викиди) кожного рядка дозволу.

Очікувано (правило S6/ФВ-6.6, D-214 і прецедент `PatchCellsHandler.cs:325`): значення таблиці/колонки поза межами читання у відповідь не потрапляють; перегляд показує лише запланові рядки дозволу.

**Вплив:** будь-який користувач із типовою роллю `DataEntry` у проєкті читає факти таблиць, прив'язаних до дозволів, у будь-якому періоді, навіть коли таблиця/колонка йому заборонена чи роль звужена періодами. Масштаб — усі рядки дозволу таблиці (до кількох сотень на документ), усі документи проєктів, де є `Document.View`. Запис неможливий — лише читання.

**Зміна — покроково:**
1. Файл `src/Ecr.Application/Permits/Bindings/PreviewPermitBindingHandler.cs`, метод `OverlayFactsAsync` (`:223-235`). Було:
```csharp
        var periodKey = PeriodKey.Parse(periodValue);
        var instance = (await rows.GetTableInstancesAsync(documentId, periodKey, ct).ConfigureAwait(false))
            .FirstOrDefault(i => i.TableDefId == sheetTable.Id && i.SheetCopyId is null);   // RS: лише оригінал (Q5)
        if (instance is null)
        {
            return result;
        }

        var rowIds = (await rows.GetRowsAsync(instance.TableInstanceId, periodKey, ct).ConfigureAwait(false))
            .ToDictionary(r => r.RowKey, r => r.Id, StringComparer.Ordinal);
        var slice = (await cells.ReadSliceAsync(instance.TableInstanceId, periodKey, ct).ConfigureAwait(false))
            .ToLookup(c => c.Address.TableRowId);
        var columnsById = sheetTable.Columns.Where(c => !c.IsDeleted).ToDictionary(c => c.Id);
```
Стало:
```csharp
        var periodKey = PeriodKey.Parse(periodValue);
        var instance = (await rows.GetTableInstancesAsync(documentId, periodKey, ct).ConfigureAwait(false))
            .FirstOrDefault(i => i.TableDefId == sheetTable.Id && i.SheetCopyId is null);   // RS: лише оригінал (Q5)
        if (instance is null)
        {
            return result;
        }

        // ⛔ S-1 (аудит 2026-11-10): фактичні комірки — те саме читання, що й зріз (GetTableSliceHandler) і перегляд
        // формул у відповіді PATCH: схована таблиця, колонка під забороною чи поза періодом ролі (D-214) і прихована
        // колонка не накладаються. Інакше перегляд прив'язки ставав оракулом значень повз межі читання.
        var reader = await access.BuildProfileAsync(currentUser.UserId!.Value, ct).ConfigureAwait(false);
        var readable = (await access.ReadScopeAsync(reader, documentId, ct).ConfigureAwait(false)).InPeriod(periodKey);
        if (!readable.CanReadTable(sheetTable.Id))
        {
            return result;
        }

        var rowIds = (await rows.GetRowsAsync(instance.TableInstanceId, periodKey, ct).ConfigureAwait(false))
            .ToDictionary(r => r.RowKey, r => r.Id, StringComparer.Ordinal);
        var slice = (await cells.ReadSliceAsync(instance.TableInstanceId, periodKey, ct).ConfigureAwait(false))
            .ToLookup(c => c.Address.TableRowId);
        var columnsById = sheetTable.Columns
            .Where(c => !c.IsDeleted && !c.IsHidden && readable.CanReadColumn(c.Id))
            .ToDictionary(c => c.Id);
```
(`currentUser.UserId!.Value` безпечний: до цього місця `ReadSourceAsync` уже пройшов `RequireInAnyProjectAsync`, який кидає 401 для аноніма. `access`, `currentUser` — уже є в конструкторі, `:61-62`. `DocumentReadScope.InPeriod(PeriodKey)`, `CanReadTable(int)`, `CanReadColumn(int)` — `src/Ecr.Application/Security/DocumentReadScope.cs:123/244/222`; `ColumnDef.IsHidden` — `src/Ecr.Domain/Entities/Configuration/ColumnDef.cs:30`.)
2. Файл `src/Ecr.Application/Permits/Bindings/PreviewPermitBindingHandler.cs`, XML-коментар класу (`:35-38`, абзац «Фактичні комірки існуючого екземпляра накладаються лише на незіставлені колонки») — дописати «…і лише ті, що в межах читання користувача (як зріз)». Це звичайний коментар класу, у контракт OpenAPI не йде.
3. Контракт: `docs/build/02-contracts.md:4937` — дописати в колонку права: «фактичні комірки — лише таблиці/колонки в межах читання (S6, D-214), приховані колонки не накладаються». `openapi.snapshot.json`/`schema.d.ts` не змінюються (форма відповіді та сама).

**Тест-доказ:** `tests/Ecr.Infrastructure.Tests/Persistence/PermitBindingPreviewTests.cs`, клас `PermitBindingPreviewTests`.
- Новий кейс `Документ_не_накладає_комірки_колонки_під_забороною_читача`: копія наявного `Документ_дає_об_єкт_кінець_періоду_і_накладає_фактичні_комірки_лише_на_незіставлені_колонки` (`:145-188`), плюс заглушка меж читання:
```csharp
var snapshot = Ecr.Application.Templates.PublishChecks.Snapshot(
    await read.Store.GetWithStructureAsync(doc.TemplateVersionId, default));
var denied = new AccessBuilder { UserId = read.Types.UserId }
    .Permission("Document.View")
    .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read)
    .Deny(ResourceKind.Column, doc.ColumnDefIds[2])   // колонка «Fact»
    .Build();
read.Types.Access.ReadScopeAsync(Arg.Any<AccessProfile>(), documentId, Arg.Any<CancellationToken>())
    .Returns(DocumentReadScope.For(denied, AccessBuilder.ProjectId, snapshot));
...
Assert.False(preview.Rows[0].Cells.ContainsKey(ColumnCode(preview, "Fact")));
Assert.Equal(12.5m, preview.Rows[0].Cells[ColumnCode(preview, "Limit")]);   // запланове значення лишається
```
- У наявному кейсі `Документ_дає_…` (`:145`) додати таку саму заглушку `ReadScopeAsync`, але профіль БЕЗ `.Deny(...)` — він має й далі бачити `7m` у «Fact» (інакше NSubstitute поверне `null` замість меж і буде `NullReferenceException`).
- Чому падає без фіксу: без кроку 1 обробник не кличе `ReadScopeAsync` взагалі, і «Fact» = `7m` присутня → `Assert.False(... ContainsKey("Fact"))` червоний. Мутація для перевірки: прибрати `&& readable.CanReadColumn(c.Id)` — червоний новий кейс.

**Перевірка після впровадження:**
- `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~PermitBindingPreviewTests"` (потрібен `ECR_TEST_SQL`);
- `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~DocumentTablesPermitBindingTests"`;
- `dotnet test tests/Ecr.Architecture.Tests`;
- стенд: користувач із Deny на таблицю прив'язки відкриває «Шаблон → Прив'язки дозволів → Перегляд» з документом — колонка факту порожня; адміністратор — бачить значення.

**Ризики і побічні ефекти:** +1 `BuildProfileAsync` (кешований профіль, `Cache:AccessProfileSlidingMinutes`) і +1 `ReadScopeAsync` лише коли передано `documentId` і є екземпляр. Тестів фіксованої кількості запитів на цей маршрут не знайдено (`git grep -n "PreviewPermitBinding" -- tests` — лише три файли вище). Контракт форми відповіді незмінний.

**Обґрунтування:** той самий фільтр (`!IsHidden && CanReadColumn`), що вже стоїть у `PatchCellsHandler.cs:328-329`, — одне правило на всі шляхи читання. Альтернатива `DocumentVisibility.RequireTableVisibleAsync` (як у Refresh) відкинута: вона дає 404 на весь перегляд, хоча перегляд без документа (лише план дозволу) читач бачити вправі.

**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням (повний ланцюг контролер → обробник → `ICellStore.ReadSliceAsync`).
**Залежності:** немає.

---

### S-2 — medium — Скасування й перезапуск чужих і системних фонових задач за безпечним правом `System.ViewHealth`
**Категорія:** security (підвищення привілеїв роллю «лише перегляд»)

**Де:**
- `src/Ecr.Application/Integration/IntegrationHandlers.cs:846-862` — `CancelJobHandler`: чужа/системна задача скасовується за `GetJobStatusHandler.Permission` (= `"System.ViewHealth"`, `:173`);
- `src/Ecr.Application/Integration/IntegrationHandlers.cs:586-620` — `RestartJobHandler`: за `System.ViewHealth` повторюється будь-яка чужа провалена задача, крім збору/синку/узгодженості (`DangerousActionPermission`, `:671-683`);
- `src/Ecr.Application/Integration/IntegrationHandlers.cs:526-533` — коментар прямо: «Межа контракту §9 (`System.ViewHealth` для чужої задачі) не змінена — звуження до `System.RunJob` лишається рішенням людини»;
- контракт `docs/build/02-contracts.md:4773-4774`; XML-документи `src/Ecr.Api/Controllers/JobsController.cs:97`, `:117`;
- права в сіді: `src/Ecr.Infrastructure/Persistence/Sql/seed/00-prelude.sql:106` (`System.ViewHealth` безпечне, `System.RunJob` небезпечне), `:570`, `:576`, `:617` (`ViewHealth` мають `TemplateAdministrator`, `PeriodAdministrator`, `Auditor`); текст права `RunJob` — `:4317` «Запуск и отмена фоновых задач»;
- клієнт: `src/Ecr.Web/src/features/jobs/JobFacts.tsx:139` (`canRestartJob(state, isOwnJob, hasViewHealth)`).

**Проблема:** роль «Аудитор» (усе — `*.View`, `Security.ViewAudit`, `System.ViewHealth`) може:
- скасувати БУДЬ-ЯКУ задачу в стані `Queued/Running` — нічний перерахунок, перенос версії документа іншого користувача, імпорт, архівацію, експорт;
- перезапустити чужу провалену задачу імпорту/перерахунку/переносу версії, яка пише дані від імені автора з payload (`:526-531`).
```csharp
// :846, :852
if (!PermissionCheck.IsGranted(profile, GetJobStatusHandler.Permission))   // System.ViewHealth
{
    if (ownerId is null || ownerId.Value != userId) { throw ... jobNotYours ... }
}
```
Каталог прав сам описує скасування й запуск як `System.RunJob` (небезпечне, лише `SystemAdministrator`, D-388), а код вимагає безпечного права перегляду.

**Сценарій відтворення:** користувач з роллю `Auditor` → `GET /api/v1/jobs` (дозволено, `ViewHealth`) бачить `RecalculationJob-…` іншого користувача в `Running` → `POST /api/v1/jobs/{jobId}/cancel` → `202`, задача `Cancelled`. Аналогічно `POST /jobs/{failedImportJobId}/restart` → `202`. Очікувано (текст права в каталозі, принцип «перегляд не змінює стан»): `403 ECR-AUTH-0403` без `System.RunJob`.

**Вплив:** зрив роботи інших користувачів (перерахунок, перенос версії, імпорт), повтор чужого імпорту в момент, коли автор цього не очікує. Подія `JobCancelled`/`JobRestarted` пишеться (слід є), тож це не прихована дія. Кількість носіїв `ViewHealth` — три вбудовані ролі.

**Зміна — покроково (варіант А, рекомендований):**
1. `src/Ecr.Application/Integration/IntegrationHandlers.cs`, клас `CancelJobHandler` (`:769`): додати константу після `NotActiveErrorCode`:
```csharp
    /// <summary>Право скасувати/повторити ЧУЖУ чи системну задачу (каталог: «Запуск і скасування фонових задач»).</summary>
    public const string ForeignJobPermission = Consistency.RunConsistencyCheckHandler.Permission; // "System.RunJob"
```
   і замінити `:846-862`. Було:
```csharp
        if (!PermissionCheck.IsGranted(profile, GetJobStatusHandler.Permission))
        {
            // Автор скасовує СВОЮ задачу без `System.ViewHealth`; чужу — ні.
            ...
            if (ownerId is null || ownerId.Value != userId)
            {
                throw new AccessDeniedException(
                    "ECR-AUTH-0403", $"Потрібне право {GetJobStatusHandler.Permission}.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-AUTH-0403.jobNotYours",
                        ["permission"] = GetJobStatusHandler.Permission,
                    });
            }
        }
```
   Стало:
```csharp
        // ⛔ S-2: автор скасовує СВОЮ задачу без жодного права; чужу чи системну (автор null) — лише з System.RunJob.
        var own = ownerId is { } owner && owner == userId;
        if (!own && !PermissionCheck.IsGranted(profile, ForeignJobPermission))
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Потрібне право {ForeignJobPermission}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.jobNotYours",
                    ["permission"] = ForeignJobPermission,
                });
        }
```
2. Той самий файл, `RestartJobHandler.HandleAsync`, замінити блок `:586-620` (`if (PermissionCheck.IsGranted(profile, GetJobStatusHandler.Permission)) { … } else { … }`) на:
```csharp
        // ⛔ S-2: автор повторює СВОЮ задачу, на яку право ще за ним (L1-19); чужу/системну — лише з System.RunJob
        // і правом на саму дію (R9-F3).
        var own = ownerId is { } owner && owner == userId;
        if (!(own && await MayRepeatOwnAsync(profile, jobId, status.DocumentId, ct).ConfigureAwait(false)))
        {
            if (!PermissionCheck.IsGranted(profile, CancelJobHandler.ForeignJobPermission))
            {
                throw new AccessDeniedException(
                    "ECR-AUTH-0403", $"Потрібне право {CancelJobHandler.ForeignJobPermission}.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-AUTH-0403.jobNotYours",
                        ["permission"] = CancelJobHandler.ForeignJobPermission,
                    });
            }

            if (DangerousActionPermission(jobId) is { } required && !PermissionCheck.IsGranted(profile, required))
            {
                throw new AccessDeniedException(
                    "ECR-AUTH-0403", $"Потрібне право {required}.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-AUTH-0403.permission",
                        ["permission"] = required,
                    });
            }
        }
```
   (власник `System.RunJob`, що повторює СВОЮ задачу, яку `MayRepeatOwnAsync` не пропускає — наприклад перенос версії, — іде другою гілкою, як і раніше йшов гілкою `ViewHealth`.) Оновити XML-коментар класу `:508-535` (межа — `System.RunJob`).
3. `src/Ecr.Api/Controllers/JobsController.cs:97` і `:117` — у `<summary>` замінити «Право `System.ViewHealth` — або автор…» на «Право `System.RunJob` — або автор…»; потім перегенерувати `contracts/openapi.snapshot.json` і `src/Ecr.Web/src/api/schema.d.ts` (останнім кроком, одним комітом).
4. `docs/build/02-contracts.md:4773-4774` — колонка права: «`System.RunJob` для чужої/системної задачі; автор — свою без права (UX-09, L1-19)».
5. `docs/tz/10-decisions.md` — новий запис `D-4xx` «Скасування/повтор чужих задач — `System.RunJob`» + рядок суми в `docs/CHECKSUMS.txt` (тест `SpecificationBaselineTests` підкаже готовий рядок).
6. Клієнт (окрема UI-ланка, аудит 05): `src/Ecr.Web/src/features/jobs/JobFacts.tsx:139` — `canRestartJob` показувати кнопку для чужих задач за `System.RunJob`, не `System.ViewHealth`; так само умова кнопки «Скасувати» на сторінці задач.

**Тест-доказ:**
- `tests/Ecr.Application.Tests/Integration/RestartJobHandlerTests.cs` — новий кейс `З_ViewHealth_без_RunJob_чужу_провалену_задачу_перезапустити_не_можна` (профіль з `System.ViewHealth`, автор задачі інший → `AccessDeniedException`, `RestartAsync` не викликано). Без фіксу — проходить гілка `ViewHealth` → тест червоний.
- `tests/Ecr.Api.Tests/JobCancelTests.cs` — новий кейс `Чужу_задачу_з_ViewHealth_без_RunJob_скасувати_не_можна` (403). Наявні кейси, що скасовують/повторюють чужу задачу під `ViewHealth` (`Скасування_чужої_задачі_пише_подію_JobCancelled` `:197`, `Провалену_задачу_можна_перезапустити` у `RestartJobHandlerTests.cs:40`), — додати користувачу `System.RunJob`.
- Кейси автора (`Свою_задачу_автор_скасовує_без_ViewHealth` `:143`) мають лишитися зеленими без змін.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~RestartJobHandlerTests"`; `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~JobCancelTests|FullyQualifiedName~JobRestart"`; `dotnet test tests/Ecr.Architecture.Tests` (EndpointCoverage/контракт); стенд: «Аудитор» на сторінці задач не має кнопок «Скасувати/Повторити» для чужих задач і отримує 403 прямим запитом.

**Ризики і побічні ефекти:** `TemplateAdministrator`/`PeriodAdministrator` втрачають можливість скасувати чужу задачу — це робить `SystemAdministrator` (має `RunJob`, D-388). Зміна контракту прав (не форми) — потрібні примітки до оновлення.

**Обґрунтування:** право перегляду не має змінювати стан чужої роботи; каталог прав уже називає `System.RunJob` правом «запуску й скасування». Варіант «окреме нове право `System.CancelJob`» відкинуто — нове право потребує сіду, перекладів і ролі без потреби.

**Рішення PO:** так. Питання: «Скасування й повтор ЧУЖИХ фонових задач: (А) лише `System.RunJob` (рекомендовано; Аудитор/адміністратори шаблонів і періодів — лише свої задачі); (Б) лишити `System.ViewHealth` як зараз і зафіксувати це рішенням D-…». Дефолт до відповіді — Б (нічого не змінювати).
**Впевненість:** підтверджено читанням.
**Залежності:** немає.

---

### S-3 — medium — Межа одночасних розборів книги стоїть лише на перегляді імпорту документа
**Категорія:** security (відмова в обслуговуванні через пам'ять процесу)

**Де:**
- межа є лише тут: `src/Ecr.Api/Controllers/DocumentsController.cs:743-756` (`[FromServices] ImportPreviewGate previewGate`, `using var parsePermit = await previewGate.EnterAsync(ct)`);
- без межі, з повним розбором `new XLWorkbook` (`src/Ecr.Adapters.Excel/ExcelImporter.cs:1197-1203`, через `ExcelRegistryImporter.cs:53-63`, `NaeTreeWorkbook.cs:23-30`, `PermitImportWorkbook.cs:27-31`):
  - `src/Ecr.Api/Controllers/AirLimitsController.cs:122-134` (`air-limits/import/analyse`), `:152-172` (`…/preview`), `:190-209` (`…/apply`) — тіло до 30 МБ (`:26`);
  - `src/Ecr.Api/Controllers/NaeTreeController.cs:101-112`, `:122-135`, `:148-161` — тіло до 30 МБ (`:21`);
  - `src/Ecr.Api/Controllers/PermitTableImportController.cs:83-95` — тіло до 6 МБ.
- стелі пакета: `src/Ecr.Adapters.Excel/XlsxSafetyGate.cs` — `MaxUncompressedBytes = 256 МіБ`, `MaxCompressionRatio = 100`.

**Проблема:** S1-02 / P1-04 (аудит 2026-10-09) показав, що розбір книги на стелі `XlsxSafetyGate` — до ~1,5 ГБ пам'яті процесу API (оцінка в коментарі воріт; runbook `docs/admin/operations-runbook.md:134`), і межу одночасності (AN-123) поставили лише на `POST /documents/{id}/import/preview`. Після цього з'явились сім нових ендпоінтів (2026-10-29…11-06), що розбирають книгу тим самим `ExcelImporter.Open` → `new XLWorkbook(file)`, і жоден не бере місце в `ImportPreviewGate`. Навіть 6 МБ стисненого файлу при співвідношенні 100 дають 256 МіБ XML (вище — відмова воріт), тобто той самий найгірший випадок.

**Сценарій відтворення:** два-три користувачі з `Registry.EditData` одночасно запускають «Аналіз» імпорту лімітів повітря (`POST /api/v1/air-limits/import/analyse`) з книгами ~200 МіБ розпакованого XML (один аркуш, багато рядків) → пам'ять процесу API росте на ~1,5 ГБ на запит, без черги й 429; на сервері з 8 ГБ процес упирається в пам'ять (GC thrash/OOM), відповіді сповільнюються для всіх ~100 користувачів. Очікувано (правило AN-123/P1-04): понад межу `Security:RateLimit:ImportPreviewConcurrency` (2) + черга (4) — `429 ECR-REQ-0429` (`err.ECR-REQ-0429.importBusy`).

**Вплив:** доступність API для всіх користувачів; ініціатори — обмежене коло (`Registry.EditData` у сіді не має `DataEntry`), тому medium, а не high. Потребує заміру пам'яті на стенді.

**Зміна — покроково:** спільна межа (той самий синглтон `ImportPreviewGate`, бо пам'ять процесу спільна).
1. `src/Ecr.Api/Controllers/AirLimitsController.cs`, дія `Analyse` (`:127-133`). Було:
```csharp
    public async Task<ActionResult<AirLimitsAnalysisDto>> Analyse(
        IFormFile file, [FromForm] string? request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        var options = ParseOptions(request);
        await using var stream = file.OpenReadStream();
```
Стало:
```csharp
    public async Task<ActionResult<AirLimitsAnalysisDto>> Analyse(
        IFormFile file, [FromServices] Ecr.Api.Security.ImportPreviewGate parseGate,
        [FromForm] string? request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(parseGate);

        // ⛔ S-3: розбір книги — повна модель ClosedXML у пам'яті процесу; та сама межа, що й перегляд імпорту
        // документа (AN-123, P1-04): місце — після прив'язки тіла, понад чергу — 429.
        using var parsePermit = await parseGate.EnterAsync(ct).ConfigureAwait(false);
        var options = ParseOptions(request);
        await using var stream = file.OpenReadStream();
```
2. Те саме (параметр `[FromServices] Ecr.Api.Security.ImportPreviewGate parseGate` одразу після `IFormFile file`; `ArgumentNullException.ThrowIfNull(parseGate);` і `using var parsePermit = await parseGate.EnterAsync(ct).ConfigureAwait(false);` одразу після `ArgumentNullException.ThrowIfNull(file);`) у:
   - `AirLimitsController.Preview` (`:158-171`) і `AirLimitsController.Apply` (`:196-208`);
   - `NaeTreeController.AnalyseImport` (`:107-111`), `PreviewImport` (`:128-134`), `ApplyImport` (`:154-160`);
   - `PermitTableImportController.ReadWorkbook` (`:89-94`).
   Параметр `[FromServices]` не потрапляє в OpenAPI (так само, як у `DocumentsController.ImportPreview`) — `openapi.snapshot.json` не змінюється; конструктори контролерів не чіпати (архітектурні правила залежностей контролерів).
3. `docs/admin/operations-runbook.md:134` — у рядку `ImportPreviewConcurrency / ImportPreviewQueueLimit` замінити «скільки переглядів імпорту книги (`POST /api/v1/documents/{id}/import/preview`)» на «скільки розборів книги `.xlsx` (перегляд імпорту документа, імпорт лімітів повітря — analyse/preview/apply, дерево НАЕ — analyse/preview/apply, книга таблиці дозволу) — одна спільна межа на процес». `src/Ecr.Api/Security/ImportPreviewGate.cs:9-22` — те саме в `<summary>`.

**Тест-доказ:** новий файл `tests/Ecr.Api.Tests/Security/WorkbookParseConcurrencyTests.cs` за зразком `ImportPreviewConcurrencyTests.Межа_вичерпана_дія_відповідає_429_не_торкаючись_розбору` (`tests/Ecr.Api.Tests/Security/ImportPreviewConcurrencyTests.cs:84-101`):
- `[Theory]` по семи діях: `gate` з `PermitKey=1`, `QueueLimitKey=0`, одне місце зайняте (`using var busy = await gate.EnterAsync(...)`); контролер через `RuntimeHelpers.GetUninitializedObject` (без залежностей); виклик дії з `FormFile(new MemoryStream([1,2,3]), 0, 3, "file", "book.xlsx")` і зайнятим `gate` → `Assert.ThrowsAsync<BusinessRuleException>`, `ErrorCode == "ECR-REQ-0429"`.
- Без фіксу дія звертається до поля-обробника (`import`/`manual`/`workbook` = `null`) → `NullReferenceException`, не `BusinessRuleException` → тест червоний.
- Плюс рефлексійний кейс (як `ImportPreviewConcurrencyTests.cs:70-81`): кожна з семи дій має параметр типу `ImportPreviewGate` з `FromServicesAttribute`.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~WorkbookParseConcurrencyTests|FullyQualifiedName~ImportPreviewConcurrencyTests|FullyQualifiedName~AirLimits|FullyQualifiedName~NaeTree|FullyQualifiedName~PermitTableImport"`; `dotnet test tests/Ecr.Architecture.Tests`; стенд: три паралельні `curl` на `air-limits/import/analyse` з великою книгою за `ImportPreviewConcurrency=1`, `QueueLimit=0` — третій отримує 429.

**Ризики і побічні ефекти:** довге `apply` лімітів повітря тримає місце, і перегляд імпорту документа може чекати в черзі або отримати 429 — прийнятно (та сама пам'ять); за потреби адміністратор піднімає межу з запасом RAM. Новий код відповіді 429 на цих маршрутах — клієнт уже обробляє `ECR-REQ-0429` (той самий ключ, що й для документа).

**Обґрунтування:** одна межа на процес відповідає ресурсу, що вичерпується (пам'ять процесу), — окремі межі на кожен маршрут множили б найгірший випадок. Зниження стелі `XlsxSafetyGate` відкинуто — заміряна реальна книга директиви має 113 МіБ XML (перевіряльник S1-02).

**Рішення PO:** не потрібне.
**Впевненість:** відсутність межі — підтверджено читанням; оцінка пам'яті — потребує перевірки на стенді.
**Залежності:** немає.

---

### S-4 — low — `GET /permits/{id}/usages` називає місця використання дозволу в схованих аркушах, таблицях, колонках і періодах
**Категорія:** security (розкриття змісту схованого)

**Де:**
- `src/Ecr.Application/Permits/GetPermitUsagesHandler.cs:76-83` — фільтр лише за проєктами (`ListDocumentsHandler.ReadableProjects(profile, "Document.View")`);
- `src/Ecr.Infrastructure/Persistence/PermitStore.cs:52-139` — `cellQuery` повертає `SheetCode`, `TableCode`, `RowKey`, `FieldCode`, `PeriodKey` будь-якої комірки з цим дозволом;
- `src/Ecr.Application/Documents/DocumentQueryHandlers.cs:628-646` — `ReadableProjects(profile, permission)` свідомо включає проєкти, де документ відкриває лише роль, звужена аркушами/періодами (D-214);
- маршрут `src/Ecr.Api/Controllers/PermitsController.cs:77-83`; контракт `docs/build/02-contracts.md:4942`.

**Проблема:** елемент `kind = "cell"` каже «у документі D, період P, аркуш S, таблиця T, рядок R, колонка C стоїть дозвіл X» — тобто значення Lookup-комірки. Межі читання документа (Deny на аркуш/таблицю/колонку, звуження періодами D-214) не застосовуються, хоча саме вони визначають, які комірки людина бачить (порівняння: валідація ховає адресу й текст прихованих повідомлень — `ValidateDocumentHandler.cs:191-201`, «адреса (`RowKey`, `ColumnCode`) і текст правила — теж зміст прихованого»).

**Сценарій відтворення:** користувач з `Registry.View` + `Document.View` і грантом `Read` на проєкт, Deny на таблицю T, де колонка Lookup на дозвіл → `GET /api/v1/permits/{id}/usages` → `items` містить `{"kind":"cell","tableCode":"T","rowKey":"…","fieldCode":"…"}`. Очікувано: елементи комірок поза межами читання відсутні.

**Вплив:** низький — розкривається місце й факт посилання на дозвіл (не числові дані) лише в документах, які людина й так бачить.

**Зміна — покроково:**
1. `src/Ecr.Application/Security/DocumentReadScope.cs` — після `CanReadAt` (`:306-309`) додати публічний метод:
```csharp
    /// <summary>Чи бачить профіль колонку <paramref name="columnCode"/> таблиці з кодом <paramref name="tableCode"/> у періоді (D-214).</summary>
    /// <param name="tableCode">Код таблиці; невідомий знімку — невидима.</param>
    /// <param name="columnCode">Код колонки.</param>
    /// <param name="period">Період даних.</param>
    public bool CanReadColumnCodeIn(string tableCode, string columnCode, PeriodKey period)
        => TableIdOf(tableCode) is { } tableDefId && InPeriod(period).CanReadAt(tableDefId, columnCode);
```
2. `src/Ecr.Application/Permits/GetPermitUsagesHandler.cs:83`. Було:
```csharp
        return await permits.FindUsagesAsync(permitId, projects, periodKey, limit ?? DefaultLimit, ct).ConfigureAwait(false);
    }
```
Стало:
```csharp
        var usage = await permits.FindUsagesAsync(permitId, projects, periodKey, limit ?? DefaultLimit, ct).ConfigureAwait(false);
        return await WithoutHiddenCellsAsync(profile, usage, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// ⛔ S-4: місце комірки поза межами читання документа (Deny, D-214, період) не називається — те саме рішення, що й зріз.
    /// Поля шапки лишаються: шапку бачить кожен, хто бачить документ.
    /// </summary>
    private async Task<PermitUsageDto> WithoutHiddenCellsAsync(
        Security.AccessProfile profile, PermitUsageDto usage, CancellationToken ct)
    {
        var scopes = new Dictionary<long, Security.DocumentReadScope>();
        var kept = new List<PermitUsageItemDto>(usage.Items.Count);

        foreach (var item in usage.Items)
        {
            if (!string.Equals(item.Kind, PermitUsageKinds.Cell, StringComparison.Ordinal))
            {
                kept.Add(item);
                continue;
            }

            if (!scopes.TryGetValue(item.DocumentId, out var scope))
            {
                scope = await access.ReadScopeAsync(profile, item.DocumentId, ct).ConfigureAwait(false);
                scopes[item.DocumentId] = scope;
            }

            if (item.TableCode is { } table && item.PeriodKey is { } period
                && scope.CanReadColumnCodeIn(table, item.FieldCode, new PeriodKey(period)))
            {
                kept.Add(item);
            }
        }

        var removed = usage.Items.Count - kept.Count;
        return removed == 0 ? usage : usage with { LinkCount = Math.Max(0, usage.LinkCount - removed), Items = kept };
    }
```
(`PermitUsageDto`/`PermitUsageItemDto`/`PermitUsageKinds` — `src/Ecr.Application/Permits/Dto/PermitUsageDtos.cs`; `PeriodKey` — `Ecr.Domain.ValueObjects`, уже імпортовано.)
3. `docs/build/02-contracts.md:4942` — дописати: «комірки поза межами читання документа (S6, D-214) не віддаються; `linkCount` зменшується на сховані з поточної сторінки».

**Тест-доказ:** `tests/Ecr.Api.Tests/PermitUsagesHttpTests.cs` — новий кейс `Комірки_схованої_таблиці_не_називаються_у_використанні`: у `ArrangeAsync`-подібному сценарії ще користувач з роллю (`Registry.View`, `Document.View`), `ResourceGrant(role, Project, document.ProjectId, Read)` і `ResourceGrant(role, ResourceKind.Table, document.TableDefId, GrantLevel.Read, isDeny: true)`; `GET /api/v1/permits/{s.Active}/usages` → жодного елемента `kind == "cell"`, елементи `header` є. Без фіксу store повертає комірки таблиці → тест червоний.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~PermitUsagesHttpTests"`; `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~DocumentReadScope"`.

**Ризики і побічні ефекти:** +1 `ReadScopeAsync` на кожен документ сторінки (≤ 500 елементів, зазвичай одиниці документів). `documentCount` і `linkCount` за межами сторінки (`Truncated = true`) можуть ще враховувати сховані — лишковий ризик лише лічильника; точне виправлення вимагає фільтра в SQL і не варте складності.

**Обґрунтування:** фільтр у застосунку тим самим `DocumentReadScope`, що й скрізь; перенесення меж читання в SQL (`PermitStore`) відкинуто — межі обчислюються з профілю й знімка в пам'яті.

**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням.
**Залежності:** немає.

---

### S-5 — low — «Де використано» запису довідника віддає номери дозволів без права читати реєстр шапки дозволів
**Категорія:** security (обхід правила C4 «Template.View/Registry.View одного довідника не відкривають реєстри дозволів»)

**Де:**
- `src/Ecr.Application/Registries/Nae/NaeHandlers.cs:606-621` — `GetRegistryEntryReferencesHandler`: право перевіряється лише на ЗАПИТАНИЙ довідник (дерево НАЕ або довідник речовин), результат `reader.ListAsync` повертається як є;
- `src/Ecr.Application/Permits/AirLimits/AirLimitsReferenceReader.cs:45-88` — читає записи ІНШИХ довідників (джерела, шапка дозволу) і віддає `PermitNumber`, `ValidFrom`, `ValidTo`, кількість речовин;
- маршрут `GET api/v1/registries/{code}/entries/{id:long}/references` (`src/Ecr.Api/Controllers/NaeTreeController.cs:174`), контракт `docs/build/02-contracts.md:4889`.

**Проблема:** користувач, якому заборонено реєстр шапки дозволів (`Deny` на `Registry:{headerId}`), але дозволено дерево НАЕ, отримує номери й строки дозволів через «де використано» вузла НАЕ. Скрізь в іншому місці читання даних дозволу проходить `PermitRegistryReadGuard` (`src/Ecr.Application/Permits/Bindings/PermitRegistryReadGuard.cs:20-35`) — тут ні.

**Сценарій відтворення:** роль з `Registry.View`, `ResourceGrant(role, ResourceKind.Registry, <id реєстру шапки AIR>, Read, isDeny: true)` → `GET /api/v1/registries/NAE_TREE/entries/{nodeId}/references` → `200` з `permitNumber`. Очікувано: дозволи з забороненого реєстру не віддаються.

**Вплив:** низький — потрібна точкова заборона на реєстр, у стандартних ролях її немає.

**Зміна — покроково:** `src/Ecr.Application/Registries/Nae/NaeHandlers.cs:610-621`. Було:
```csharp
        var lookup = new RegistryLookup(registries, registryCode);
        await RegistryAccess.RequireAsync(access, currentUser, Permission, GrantLevel.Read, lookup, ct).ConfigureAwait(false);
        var definition = await lookup.RequireAsync(ct).ConfigureAwait(false);
        ...
        var items = await reader.ListAsync(definition.Id, entryId, ct).ConfigureAwait(false);
        return new RegistryEntryReferencesDto(definition.Code, entryId, items, items.Count);
```
Стало:
```csharp
        var lookup = new RegistryLookup(registries, registryCode);
        var profile = await RegistryAccess.RequireAsync(access, currentUser, Permission, GrantLevel.Read, lookup, ct).ConfigureAwait(false);
        var definition = await lookup.RequireAsync(ct).ConfigureAwait(false);
        ...
        var items = await reader.ListAsync(definition.Id, entryId, ct).ConfigureAwait(false);

        // ⛔ S-5 (C4): дозвіл — запис ІНШОГО довідника (шапка дозволу); його номер і строки — лише тому, хто той довідник читає.
        var visible = new List<RegistryEntryReferenceDto>(items.Count);
        foreach (var item in items)
        {
            var permit = await registries.FindEntryAsync(item.PermitId, ct).ConfigureAwait(false);
            if (permit is not null
                && !RegistryAccess.IsDenied(profile, permit.RegistryDefId)
                && RegistryAccess.CanAccess(profile, Permission, GrantLevel.Read, permit.RegistryDefId))
            {
                visible.Add(item);
            }
        }

        return new RegistryEntryReferencesDto(definition.Code, entryId, visible, visible.Count);
```
(`RegistryAccess.RequireAsync(…, RegistryLookup, …)` повертає `Task<AccessProfile>` — `RegistryAccess.cs:198`; `IsDenied` — `:50`; `CanAccess` — `internal`, `:272`, той самий assembly; `FindEntryAsync` уже використовується вище, `:614`.) Контракт `docs/build/02-contracts.md:4889` — дописати «дозволи — лише з реєстрів, які читач бачить».

**Тест-доказ:** тест «де використано» (знайти виконавцем: `git grep -n "references" -- tests/Ecr.Api.Tests/NaeTreeHttpTests.cs`) — новий кейс: користувач із Deny на реєстр шапки AIR отримує `items = []` (а адміністратор — непорожній). Без фіксу дозволи повертаються → червоний.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~NaeTreeHttpTests"`.

**Ризики і побічні ефекти:** N+1 `FindEntryAsync` на кількість дозволів вузла (одиниці–десятки). `Count` тепер — видимих.

**Обґрунтування:** перевірка в обробнику (а не в `AirLimitsReferenceReader`) — інтерфейс порту `IRegistryEntryReferenceReader` лишається без профілю.

**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням.
**Залежності:** немає.

---

### S-6 — low — `mention-candidates` чату таблиці без межі частоти
**Категорія:** security (DoS / межа частоти)

**Де:**
- `src/Ecr.Api/Controllers/TableDiscussionsController.cs:89-96` — дія `MentionCandidates` без `[EnableRateLimiting]`;
- `src/Ecr.Application/Documents/Discussions/TableDiscussionQueries.cs:189-216` — до 200 кандидатів (5 сторінок × 40), для КОЖНОГО `TableDiscussionAccess.CanSeeAsync` → `BuildProfileAsync` + `RequireVisibleAsync` + `ReadScopeAsync` (`TableDiscussionAccess.cs:165-188`);
- аналог із межею: `src/Ecr.Api/Controllers/DocumentEditAssignmentsController.cs:114` (`[EnableRateLimiting(SearchRateLimitPolicy.PolicyName)] // T8: межа на користувача, 429`);
- XML-опис POST обіцяє 429: `TableDiscussionsController.cs:103` («`429` — ліміт запитів»), а межі на POST немає.

**Проблема:** будь-який користувач із `Document.Discuss` може слати `GET …/mention-candidates?q=aa` без обмеження; кожен запит — до 200 рішень доступу для інших користувачів. Кандидати передач (`edit-assignments/candidates`) — та сама форма пошуку, і вони вже під `SearchRateLimitPolicy` (T8).

**Сценарій відтворення:** скрипт у циклі `GET /api/v1/documents/{id}/tables/{code}/mention-candidates?q=ab` → завжди `200`, без `429`. Очікувано (прецедент T8): понад 30 запитів за 10 с на користувача — `429 ECR-REQ-0429` (`err.ECR-REQ-0429.tooManySearches`).

**Вплив:** низький — навантаження на БД/CPU від одного автентифікованого користувача; кількість звернень до БД на запит залежить від кешу профілю — потребує заміру на стенді.

**Зміна — покроково:**
1. `src/Ecr.Api/Controllers/TableDiscussionsController.cs:1-5` — додати `using Ecr.Api.Security;` і `using Microsoft.AspNetCore.RateLimiting;`.
2. Той самий файл, `:89`. Було:
```csharp
    [HttpGet("tables/{tableCode}/mention-candidates")]
    [ProducesResponseType<IReadOnlyList<TableDiscussionMentionCandidateDto>>(StatusCodes.Status200OK)]
```
Стало:
```csharp
    [HttpGet("tables/{tableCode}/mention-candidates")]
    [EnableRateLimiting(SearchRateLimitPolicy.PolicyName)] // S-6: межа на користувача, 429 — як кандидати передач (T8)
    [ProducesResponseType<IReadOnlyList<TableDiscussionMentionCandidateDto>>(StatusCodes.Status200OK)]
```
   Атрибут у OpenAPI не потрапляє — `openapi.snapshot.json` не змінюється.
3. POST (`:97-117`) — за рішенням PO (див. нижче).

**Тест-доказ:** `tests/Ecr.Api.Tests/TableDiscussionApiTests.cs` — рефлексійний кейс `Кандидати_згадок_під_межею_пошуку`: `typeof(TableDiscussionsController).GetMethod("MentionCandidates")!.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName == SearchRateLimitPolicy.PolicyName`. Без фіксу атрибута немає → червоний. За бажанням — HTTP-кейс з `Security:RateLimit:SearchPermit=1`: другий запит → 429.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~TableDiscussionApiTests"`; стенд: швидкий набір `@імʼя` у чаті не дає 429 при звичайному темпі (клієнт уже має debounce 200 мс — `src/Ecr.Web/src/features/tableChat/ChatComposer.tsx:63`).

**Ризики і побічні ефекти:** кошик `search` спільний для пошуку, кандидатів передач і кандидатів згадок (30 за 10 с на користувача) — при дуже швидкому наборі можлива 429 у підказці згадок; клієнт тоді просто не показує кандидатів.

**Обґрунтування:** повторне використання наявної політики (T8) замість нової; окрема політика на POST — лише якщо PO вирішить, що вона потрібна.

**Рішення PO:** так, лише щодо POST: «Опис `POST …/messages` обіцяє `429`, але межі немає. (А) додати межу частоти на запис у чат (нова політика, наприклад 20 повідомлень/хв на користувача); (Б) прибрати згадку про `429` з опису (зміна `openapi.snapshot.json`/`schema.d.ts`)». Дефолт до відповіді — Б не робити нічого (лишити як є). Для `mention-candidates` рішення не потрібне.
**Впевненість:** відсутність межі — підтверджено читанням; вартість запиту — потребує заміру на стенді.
**Залежності:** немає.

---

## Порядок впровадження

Усі шість знахідок незалежні й не мають спільних файлів (крім `docs/build/02-contracts.md` — різні рядки; правити по черзі або в одному коміті документації):

| Група (паралельно) | Знахідки | Файли запису |
|---|---|---|
| A | S-1 | `PreviewPermitBindingHandler.cs`, `PermitBindingPreviewTests.cs`, `02-contracts.md:4937` |
| B | S-3 | `AirLimitsController.cs`, `NaeTreeController.cs`, `PermitTableImportController.cs`, `ImportPreviewGate.cs` (коментар), новий `WorkbookParseConcurrencyTests.cs`, `operations-runbook.md:134` |
| C | S-4 | `DocumentReadScope.cs`, `GetPermitUsagesHandler.cs`, `PermitUsagesHttpTests.cs`, `02-contracts.md:4942` |
| D | S-5 | `NaeHandlers.cs`, тест у `NaeTreeHttpTests.cs`, `02-contracts.md:4889` |
| E | S-6 | `TableDiscussionsController.cs`, `TableDiscussionApiTests.cs` |
| F (після рішення PO) | S-2 | `IntegrationHandlers.cs`, `JobsController.cs` (XML → перегенерувати `openapi.snapshot.json` + `schema.d.ts`), тести Jobs, `02-contracts.md:4773-4774`, `10-decisions.md` + `CHECKSUMS.txt`; потім UI-ланка `JobFacts.tsx` |

Порядок пріоритету: S-1 → S-3 → S-2 (після відповіді PO) → S-4/S-5/S-6. S-2 — єдина зі зміною контракту OpenAPI, тож іде через `lane/integ-*` (CLAUDE.md, п. 4).

## Відкинуто

- **`423` на вході без перевірки пароля** (S1-05 попереднього аудиту) — свідомо: коментар `src/Ecr.Application/Security/LoginHandler.cs:121-124` посилається на ФВ-6.4a («законний власник має дізнатися, що запис заблоковано»).
- **Будь-який носій `Security.ManageRoles` видає будь-яке право, зокрема власній ролі** — свідоме рішення `D-121` (правило «не видати те, чого не маєш» прибрано), редагування власних ролей — `D-470`.
- **`/health/db` доступний будь-якому автентифікованому без права** — рішення Q-221 (`docs/build/questions.md:299`, `.RequireAuthorization()`).
- **CSP у режимі report-only за замовчуванням** (`appsettings.json`, `Security:Csp:Enforce=false`) — свідомо (S-22, згадано в S1 «Перевірено» п. 8).
- **Відкриття місяця документа (`POST /documents/{id}/period-reopen`) без верхньої межі `openUntil`** (`DocumentPeriodReopenHandlers.cs:157`, домен `DocumentPeriodReopen.cs:100-111` перевіряє лише «в майбутньому») — не вада безпеки (дія носія небезпечного `Period.Reopen`, з аудитом); питання продукту, передаю аудиту 04.
- **Видалення копії аркуша (`DELETE /documents/{id}/sheet-copies/{sheetCopyId}`) видаляє дані закритих періодів, якщо копія не подана** (`DocumentSheetCopyStore.FindBlockingStateAsync` блокує лише `Submitted/Approved` і архів) — цілісність даних, не авторизація (потрібні `Document.Create` + грант `Write` на проєкт + видимий аркуш, `DocumentSheetCopyHandler.cs:292-327`); передаю аудиту 04.
- **`logout/idle` не крутить штамп безпеки** (`AuthController` — лише `SignOutAsync`) — копія cookie однаково гасне сервером за тим самим моментом останньої дії (`IdleSession.ValidateAsync`, `IdleSession.cs:97-106`); не дефект.
- **Перезапуск чужої проваленої задачі імпорту пише від імені автора** — частина S-2 (не окрема знахідка).
- **Негативні числа в CSV-експорті отримують апостроф** (`CsvFormat.Field`, `-5` → `'-5`) — це захист від формул (OWASP), а не вада безпеки.

## Перевірено й у порядку

1. Усі 74 контролери: 72 з `[Authorize]`, анонімні — лише `PublicController.Bootstrap`, `CspReportController.Report`, `login/local`, `GET ui-strings/{lang}` (`git grep -n "AllowAnonymous" -- src/Ecr.Api`).
2. Cookie: `HttpOnly`, `SameSite=Strict`, `Secure` за `Auth:RequireHttps=true`, ковзний строк 8 год + абсолютна межа 12 год (`AuthenticationSetup.cs:76`, `:184-202`) + сервірна межа бездіяльності D-471 (`IdleSession.cs`: GET ніколи не продовжує сеанс). Штамп безпеки на кожен запит, перевидання лише на власну ротацію (`SecurityStampMiddleware.cs`). DataProtection у Production — лише з сертифікатом (S11).
3. CSRF: `CsrfOriginMiddleware` (`Sec-Fetch-Site` / `Origin`) на всіх POST/PUT/PATCH/DELETE до авторизації; виняток — лише CSP-звіт. `X-Forwarded-Host` не підробляється крос-сайтово (кастомний заголовок вимагає preflight, CORS не налаштовано).
4. Симуляція: будь-який не-GET блокує `SimulationReadOnlyMiddleware` (виняток — точні шляхи входу/виходу/активності/кінця симуляції).
5. Зміна пароля: перевірка чинного пароля навіть при разовому, спільний із входом лічильник невдалих спроб, ротація штампа (`ChangePasswordHandler.cs`). Межа частоти входу — `ForwardedClientAddress` з `KnownProxies` (S1-01 закрито).
6. Чат таблиці (TC/TC02): 404 для схованої таблиці/документа, звуження періодами, правка — лише автор у 15 хв, видалення — автор або `DiscussModerate`, згадки фільтруються видимістю, нормалізація тексту (bidi/zero-width).
7. Передачі місяця (EA/D-443): «видати не вище власного рівня» (`EditAssignmentAccess.OwnLevelOver` з колонками), набори виконавців — лише свої, члени без оракула існування; передача власності — новий власник мусить мати `Write`.
8. `CellSource`, `PiEventData`, `PiRawData`: документ/екземпляр/рядок/колонка/мапінг звіряються з маршрутом, межі читання в періоді й у періоді-власнику (Q-17); CSV — через `CsvFormat.Field`.
9. `RefreshPermitBindingHandler`, `PatchCellStylesHandler`, `GetSheetChangesHandler`, `GetSubmissionTargetHandler`, `GetCalculationIssuesHandler`, `ValidateDocumentHandler` (видача), перегляд формул у PATCH — межі читання застосовано.
10. SSRF: основна й ЗАПАСНА адреса джерела (D-462) проходять ту саму політику (`DataSourceHandlers.cs:460-492`); SMTP — політика хоста/порту з DNS; PI Web API — `EscapeDataString` у шляхах; PI ODBC — параметри `OdbcParameter`.
11. Експорт Excel: ClosedXML 0.104.2, рядок у `.Value` — текст, не формула; формули — лише з білого списку (S1 п. 5).
12. Вивантаження: `XlsxSafetyGate` на всіх шляхах розбору `.xlsx` (кількість записів, розпакований розмір, співвідношення стиснення); CSV довідників і рядків UI — строгий UTF-8, стеля 1 МБ.
13. Вихідні абсолютні посилання в листах — з конфігурації `Notifications:PublicBaseUrl`, не з `Host` запиту (немає підміни через заголовок Host).
14. `500` — стале речення без стеку; тексти винятків джерел/БД у `/jobs` не потрапляють (`JobFailureText`), `ExceptionHandlingMiddleware` віддає лише `Message` бізнес-винятків застосунку.

## Самоперевірка

| ID | `file:line` відкрито повторно | «було» дослівно | компілюється за змістом | тест падає без фіксу | не дублює відоме | Результат |
|---|---|---|---|---|---|---|
| S-1 | так: `PreviewPermitBindingHandler.cs:124, 143-147, 211-255` (вивід `awk` з номерами) | так, блок `:223-235` скопійовано з виводу `awk` | `access`, `currentUser` — поля конструктора (`:61-62`); `IAccessDecisionService.BuildProfileAsync`/`ReadScopeAsync` (`IAccessDecisionService.cs:87`); `DocumentReadScope.InPeriod/CanReadTable/CanReadColumn`; `ColumnDef.IsHidden`; у тесті — `PublishChecks.Snapshot` (public static, `PublishChecks.cs:30/396`), `harness.Store` (`PermitBindingHarness.cs:30`), `AccessBuilder.Deny(ResourceKind.Column, …)`, `ResourceKind.Column` існує | так — без фіксу `ReadScopeAsync` не викликається, «Fact» = 7m присутня | перевірено `KNOWN-ISSUES.md`, `AUDIT-2026-10-09*.md`, `audit-2026-10-09b/S1.md`, `02-contracts.md:4937` — не згадано | залишено |
| S-2 | так: `IntegrationHandlers.cs:173, 505-535, 586-620, 671, 769, 840-862`; `02-contracts.md:4773-4774`; `JobsController.cs:97,117`; сід `00-prelude.sql:106, 570, 576, 617, 4317` | так, `:846-862` з виводу `awk` | `Consistency.RunConsistencyCheckHandler.Permission` = `"System.RunJob"` (`RunConsistencyCheckHandler.cs:45`, той самий assembly); `MayRepeatOwnAsync`, `DangerousActionPermission` — наявні члени класу | так — під `ViewHealth` без фіксу гілка `IsGranted(ViewHealth)` пропускає | коментар `:532-533` прямо каже «рішення людини»; у `10-decisions.md`, `questions/`, `QUESTIONS-BUSINESS.md` запису про це немає (`git grep RunJob`) → подано з «Рішення PO: так» | залишено |
| S-3 | так: `DocumentsController.cs:743-756`, `AirLimitsController.cs:26, 122-134, 152-172, 190-208`, `NaeTreeController.cs:21, 101-161`, `PermitTableImportController.cs:83-95`, `ExcelImporter.cs:1197-1203` | так (вивід `awk`) | `ImportPreviewGate.EnterAsync(CancellationToken)` повертає `RateLimitLease` (IDisposable) — `ImportPreviewGate.cs`; синглтон зареєстровано `LoginRateLimiting.cs:267`; `[FromServices]` уже використовується так само в `DocumentsController.ImportPreview` | так — `GetUninitializedObject` → поле обробника `null` → `NullReferenceException` замість `BusinessRuleException` (той самий прийом, що й `ImportPreviewConcurrencyTests.cs:93-99`) | S1-02/P1-04/AN-123 закрили лише перегляд документа (runbook `:134`); нових ендпоінтів там немає | залишено |
| S-4 | так: `GetPermitUsagesHandler.cs:76-83`, `PermitStore.cs:52-139`, `DocumentQueryHandlers.cs:591-646` | так, `:83` | новий метод використовує приватний `TableIdOf` (`DocumentReadScope.cs:184`) і публічні `InPeriod`/`CanReadAt` (`:123`, `:306`); `PermitUsageDto` — record (`with` працює); `PermitUsageKinds.Cell` існує | так — store повертає комірки схованої таблиці | контракт `02-contracts.md:4942` про межі читання мовчить; у відомих — немає | залишено, важливість low |
| S-5 | так: `NaeHandlers.cs:592-622`, `AirLimitsReferenceReader.cs:45-88`, `RegistryAccess.cs:50, 198, 272` | так, `:610-621` | `RegistryAccess.RequireAsync(…, RegistryLookup, …)` — `internal Task<AccessProfile>` (`:198`), викликається з того ж assembly; `CanAccess` — `internal` (`:272`) | так — без фіксу дозволи повертаються | у відомих — немає | залишено, важливість low |
| S-6 | так: `TableDiscussionsController.cs:1-5, 89-96, 103`; `TableDiscussionQueries.cs:167-216`; `DocumentEditAssignmentsController.cs:1, 5, 114` | так, `:89-90` | `SearchRateLimitPolicy.PolicyName` (`Ecr.Api.Security`), `EnableRateLimitingAttribute` (`Microsoft.AspNetCore.RateLimiting`) — як у `DocumentEditAssignmentsController` | так — атрибута немає | у відомих — немає | залишено, важливість low |

Вилучено під час самоперевірки: (1) кандидат «будь-який носій `ManageRoles` додає небезпечні права власній ролі» — `D-121`/`D-470`, перенесено у «Відкинуто»; (2) кандидат «`423` без пароля» — ФВ-6.4a, «Відкинуто»; (3) кандидат «видалення копії аркуша з даними закритих періодів» — не авторизація, передано аудиту 04; (4) кандидат «верхньої межі `openUntil` немає» — питання продукту, передано аудиту 04.
