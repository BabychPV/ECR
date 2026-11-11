# 01 — Безпека API й автентифікації (аудит 2026-11-10)

- **Область:** `src/Ecr.Api` (контролери, `Auth`, `Security`, `Middleware`, `Startup`, `Program.cs`, `appsettings*`), авторизація в `src/Ecr.Application` (`EditRules`, профілі доступу, `DocumentReadScope`, перевірки прав в обробниках), `src/Ecr.Adapters.Excel` (вивантаження/розбір), `src/Ecr.Adapters.PiAf` (вихідний HTTP/ODBC).
- **SHA `main`:** `f446e9b29` (Merge PR #811, `rc/20261111-p242`). **Дата:** 2026-11-11. Лише читання.
- **✎ 11.11, рішення PO:** S-2 і S-6 переписано під рішення PO (`00-PLAN.md` §4) і перевірено на `main` = `39aea41d9`. Між `f446e9b29` і `39aea41d9` змінилися лише документи (`git diff --stat f446e9b29 39aea41d9 -- src tests contracts` порожній), тож номери рядків коду в усьому звіті чинні.
- **Охоплено повністю:** конвеєр `Program.cs` (порядок middleware), `AuthenticationSetup` (cookie, DataProtection, Negotiate), `IdleSession` (D-471), `SecurityStampMiddleware`, `CsrfOriginMiddleware`, `SimulationReadOnlyMiddleware`, `PasswordChangeMiddleware`, `AuthController`, `ChangePasswordHandler`, `LoginHandler` (гілки блокування), `ForwardedClientAddress`, усі контролери, додані після 2026-10-09 (≈35, перелік — `git log --diff-filter=A`), і їхні обробники: чат таблиці (`TableDiscussion*`), передачі й набори виконавців (`EditAssignment*`, `EditorSet*`, `TransferDocumentOwner`), відкриття місяця документа (`DocumentPeriodReopen*`), налаштування сеансу, `CellSource`/`PiRawData`/`PiEventData`, `SheetChanges`, `FormulaManualOverride`, `PermitBindings` (+preview/refresh), `Permits` (+usages), `PermitTypes` (+usage), `PermitTableImport`, `AirLimits`, `NaeTree` (+references), `DocumentSheetCopy`, `Jobs` (cancel/restart), експорт CSV (`CsvFormat`), Excel-експорт (ClosedXML 0.104.2), `XlsxSafetyGate`, політика адрес джерел (`DataSourceHandlers.ValidateAsync`, вторинна адреса D-462), SMTP-політика.
- **Поверхово:** `SecurityController`/ролі (D-121, D-470 — свідомі рішення), `ExceptionHandlingMiddleware` (лише пошук витоку `Exception.Message`/стеку), `ProjectsController`, `TemplateVersionsController`, `MigrateDocumentVersionHandler`, `PatchCellsHandler` (лише фільтр перегляду формул і `PatchCellStylesHandler`), PI ODBC (лише параметризація).
- **Не охоплено:** клієнт `src/Ecr.Web` (аудит 05), шар даних/сирий SQL (аудит 02), Worker, інсталятор/розгортання (S2 попереднього аудиту).

## Зведена таблиця

| ID | Важливість | Категорія | Назва | Зусилля | Ризик зміни | Рішення PO |
|---|---|---|---|---|---|---|
| S-1 | high | security (обхід меж читання) | Перегляд прив'язки дозволу (`permit-bindings/{code}/preview?documentId=`) віддає фактичні комірки повз Deny / D-214 / приховані колонки | S | низький | ні |
| S-2 | medium | security (підвищення привілеїв) | Скасування й перезапуск ЧУЖИХ і системних фонових задач — лише за безпечним `System.ViewHealth` (має роль «Аудитор»), хоча каталог прав каже «Запуск і скасування — `System.RunJob`». ✎ PO 11.11: лише `System.RunJob` (`SystemAdministrator` має його з сіду) + клієнт | M | середній | так → **вирішено 11.11** |
| S-3 | medium | security (DoS пам'яті) | Межа одночасних розборів книги (`ImportPreviewGate`) стоїть лише на перегляді імпорту документа; ще 7 ендпоінтів розбирають `.xlsx` у повну модель ClosedXML без межі | S | низький | ні |
| S-4 | low | security (розкриття місця даних) | `GET /permits/{id}/usages` називає аркуш/таблицю/рядок/колонку з дозволом у схованих аркушах, таблицях, колонках і періодах поза областю ролі | S | низький | ні |
| S-5 | low | security (обхід C4) | `GET /registries/{code}/entries/{id}/references` віддає номери й строки дозволів без права читати реєстр шапки дозволів | S | низький | ні |
| S-6 | low | security (DoS/межа частоти) | Чат таблиці: `GET …/mention-candidates` (до 200 рішень доступу на запит) і `POST …/messages` без межі частоти, хоча опис POST обіцяє `429`. ✎ PO 11.11: додати — `search` (30/10 с) і нова `discussion-post` (20/хв) | S | низький | так → **вирішено 11.11** |

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
**✎ рішення PO 11.11:** «лише право System.RunJob або роль адміна». Чужу чи системну задачу скасовує/повторює лише носій `System.RunJob`; роль адміністратора (`SystemAdministrator`) це право вже має з сіду, тож окремої перевірки ролі немає. Власні задачі — як зараз. Нижче — специфікація під це рішення (варіант А попередньої редакції, плюс клієнт і тести).

**Категорія:** security (підвищення привілеїв роллю «лише перегляд»)

**Де:**
- `src/Ecr.Application/Integration/IntegrationHandlers.cs:846-862` — `CancelJobHandler`: чужа/системна задача скасовується за `GetJobStatusHandler.Permission` (= `"System.ViewHealth"`, `:173`);
- `src/Ecr.Application/Integration/IntegrationHandlers.cs:580-618` — `RestartJobHandler`: за `System.ViewHealth` повторюється будь-яка чужа провалена задача, крім збору/синку/узгодженості (`DangerousActionPermission`, `:671-683`);
- `src/Ecr.Application/Integration/IntegrationHandlers.cs:526-533` — коментар: «Межа контракту §9 (`System.ViewHealth` для чужої задачі) не змінена — звуження до `System.RunJob` лишається рішенням людини»;
- XML-документи: `IntegrationHandlers.cs:508-511`, `:742-745`, `:777-780`, `:808`; `src/Ecr.Api/Controllers/JobsController.cs:96-99`, `:116-119` → `contracts/openapi.snapshot.json:43162`, `:43243` → `src/Ecr.Web/src/api/schema.d.ts:8758-8759`, `:8833-8834`;
- контракт `docs/build/02-contracts.md:4773-4774`; довідник адміністратора `docs/admin/admin-guide.md:122`, `:891-892`;
- клієнт: `src/Ecr.Web/src/features/jobs/JobFacts.tsx:131-145` (`canRestartJob(state, isOwnJob, hasViewHealth)`), `:172-187` (`JobRetry`); `src/Ecr.Web/src/pages/admin/JobsPage.tsx:440-456` (кнопка «Скасувати» лише за станом, `JobRetry` з буквальними `isOwnJob={false}` і `hasViewHealth`); `src/Ecr.Web/src/features/jobs/MyTasksDrawer.tsx:236-251`.

**Як у коді представлено «роль адміністратора»** (перевірено читанням; від цього залежить, як перевіряти):
- Ролі — рядки `sec.Role` з кодом. Вбудовані ролі створює сід `src/Ecr.Infrastructure/Persistence/Sql/seed/00-prelude.sql:532-538`: `SystemAdministrator`, `TemplateAdministrator`, `PeriodAdministrator`, `DataEntry`, `Approver`, `Viewer`, `Auditor`, `BootstrapAdministrator`; ще `ReportViewer` (`:707-711`). Прапорця «адмін» чи права-маски (`*`) немає: `AccessProfile.Has(code)` — це просто `Permissions.Contains(code)` (`src/Ecr.Application/Security/AccessProfile.cs:248`), а `PermissionCheck.IsGranted` викликає саме його (`PermissionCheck.cs:166-171`).
- Перевіряти код ролі в обробнику не можна: архітектурне правило 7, «перевірка ролі поза єдиною точкою рішення заборонена» (`PermissionCheck.cs:9-13`; сторож `tests/Ecr.Architecture.Tests/EndpointCoverageTests.cs:93-95` шукає права в `Ecr.Application`). `AccessProfile.RoleIds` (`:43`) потрібні лише правилам періоду (`ФВ-2.15`).
- **`SystemAdministrator` уже має `System.RunJob`.** Шаблон `%` дає йому лише безпечні права (`00-prelude.sql:563`, фільтр `WHERE p.IsDangerous = 0` — `:621`), а `System.RunJob` небезпечне (`:106`). Проте секція `FP:sysadmin-rights` видає його окремим MERGE поіменно: `src/Ecr.Infrastructure/Persistence/Sql/seed/99-epilogue.sql:213-287`, рядок `:258` `(N'System.RunJob')`, роль `:265`. Це рішення `D-388` (`docs/tz/10-decisions.md:851`). Повторний сід повертає пару, якщо її зняли (`99-epilogue.sql`, коментар `:243-244`). Сторож — `tests/Ecr.Infrastructure.Tests/Persistence/SeedTests.cs:523-530` (`SysAdminDangerous` містить `"System.RunJob"`) і `:609` `Системний_адміністратор_має_кожне_адмін_право_каталогу_крім_названих_виключень`.
- `Auditor`, `TemplateAdministrator`, `PeriodAdministrator` мають `System.ViewHealth` (`00-prelude.sql:617`, `:570`, `:576`) і не мають `System.RunJob`.
- `BootstrapAdministrator` має лише `Security.ManageUsers` і `Security.ManageRoles` (`00-prelude.sql:646-664`). Це роль первинного налаштування, і вона вимикається, щойно з'являється доменний адміністратор (`BootstrapAdmin.cs:5-13`, `D-97`).
- **Висновок для реалізації:** «`System.RunJob` АБО роль адміністратора» = одна перевірка `IsGranted(profile, "System.RunJob")`. Вбудований адміністратор проходить її через сід, тож окрема гілка за роллю продублювала б рішення й порушила б правило 7. Власна роль адміністратора (`D-470`) отримує право так само — галочкою `System.RunJob`.

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
Каталог прав описує запуск і скасування як `System.RunJob`: en «Start and cancel background jobs» (`seed/features/CATALOG-en-base.sql:3756`), ru `00-prelude.sql:4317`, kz `:7324`. Код натомість вимагає безпечного права перегляду.

**Сценарій відтворення:** користувач з роллю `Auditor` → `GET /api/v1/jobs` (дозволено, `ViewHealth`) бачить `RecalculationJob-…` іншого користувача в `Running` → `POST /api/v1/jobs/{jobId}/cancel` → `202`, задача `Cancelled`. Аналогічно `POST /jobs/{failedImportJobId}/restart` → `202`. Очікувано (рішення PO 11.11): `403 ECR-AUTH-0403` (`err.ECR-AUTH-0403.jobNotYours`, `permission = "System.RunJob"`); `SystemAdministrator` — `202`; автор своєї задачі — як зараз.

**Вплив:** зрив роботи інших користувачів (перерахунок, перенос версії, імпорт), повтор чужого імпорту в момент, коли автор цього не очікує. Подія `JobCancelled`/`JobRestarted` пишеться, тож слід лишається. `ViewHealth` мають три вбудовані ролі.

**Матриця після зміни** (сервер — єдине рішення; клієнт лише ховає кнопки):

| Хто | Своя задача — скасувати | Своя — повторити | Чужа чи системна (`CreatedByUserId = null`) — скасувати / повторити |
|---|---|---|---|
| без прав | так (Q-156) | за L1-19 (`MayRepeatOwnAsync`) | 403 |
| `System.ViewHealth` (Auditor, TemplateAdm, PeriodAdm) | так | так, крім збору/синку/узгодженості без права на дію (як зараз) | **403** (було 202) |
| `System.RunJob` (SystemAdministrator з сіду; власна роль із цим правом) | так | так | так; повтор збору/синку — ще `Integration.Manage` (R9-F3, без змін) |

**Зміна — покроково:**
1. `src/Ecr.Application/Integration/IntegrationHandlers.cs`, клас `CancelJobHandler`: після `NotActiveErrorCode` (`:792`) додати константу:
```csharp
    /// <summary>
    /// Право скасувати чи повторити ЧУЖУ або системну задачу (S-2, рішення PO 11.11, D-475): каталог — «Запуск і
    /// скасування фонових задач». Вбудований <c>SystemAdministrator</c> має його з сіду (<c>FP:sysadmin-rights</c>,
    /// D-388), тож «або роль адміністратора» окремої гілки не потребує (перевірка ролі — архітектурне правило 7).
    /// </summary>
    public const string ForeignJobPermission = Consistency.RunConsistencyCheckHandler.Permission; // "System.RunJob"
```
   (`Consistency.RunConsistencyCheckHandler.Permission` = `"System.RunJob"`, `src/Ecr.Application/Consistency/RunConsistencyCheckHandler.cs:45`. Так само на нього вже посилається `:681` цього файлу. Літерал `"System.RunJob"` у `Ecr.Application` лишається — сторож `EndpointCoverageTests.cs:88` задоволений.)
2. Той самий клас, `HandleAsync`, замінити `:846-862`. Було:
```csharp
        if (!PermissionCheck.IsGranted(profile, GetJobStatusHandler.Permission))
        {
            // Автор скасовує СВОЮ задачу без `System.ViewHealth`; чужу — ні.
            // ⚠ `null` (системна задача за розкладом) автором не є нікому:
            // порівняння з `int?` дало б `false`, але покладатися тут на
            // семантику `Nullable` мовчки — не варто, і тест на це є.
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
        // ⛔ S-2 (рішення PO 11.11, D-475): автор скасовує СВОЮ задачу без жодного права (Q-156); ЧУЖУ чи системну —
        // лише носій `System.RunJob`. Безпечне `System.ViewHealth` («Аудитор», адміністратори шаблонів і періодів) —
        // лише перегляд. ⚠ `null` (системна задача за розкладом) автором не є нікому — тест на це є.
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
3. Той самий клас, XML-коментарі. `:743-744`: «Право <c>System.ViewHealth</c> — або автор ВЛАСНОЇ задачі (Q-156, та сама межа, що в <see cref="GetJobStatusHandler"/>).» → «Чужу чи системну задачу — право <c>System.RunJob</c> (S-2, D-475); свою — автор без права (Q-156).». Абзац `:755-761` («⚠ Межа права — ТА САМА, що в GetJobStatusHandler…») замінити одним реченням: «⚠ Межа для ЧУЖОЇ задачі — <c>System.RunJob</c> (S-2, рішення PO 11.11): перегляд (<c>System.ViewHealth</c>) не змінює стан чужої роботи. Свою задачу автор скасовує без права — інакше помилково запущений перерахунок нічим не спинити.». `:778-779`: «власник <c>System.ViewHealth</c> (і «Аудитор») скасовує чужі задачі за контрактом §9» → «власник <c>System.RunJob</c> скасовує чужі задачі (S-2)». `:808`: «Чужа задача без <c>System.ViewHealth</c>.» → «Чужа чи системна задача без <c>System.RunJob</c>.».
4. Той самий файл, `RestartJobHandler.HandleAsync`, замінити `:580-618`. Було (початок і кінець блоку, дослівно):
```csharp
        // Автор повторює СВОЮ задачу без `System.ViewHealth` (UX-09, «Мої
        // задачі»); чужу чи системну (автор `null`) — лише з правом.
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        var ownerId = await jobs.GetCreatedByUserIdAsync(jobId, ct).ConfigureAwait(false);

        if (PermissionCheck.IsGranted(profile, GetJobStatusHandler.Permission))
        {
        ...
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-AUTH-0403.jobNotYours",
                        ["permission"] = GetJobStatusHandler.Permission,
                    });
            }
        }
```
   Стало (увесь блок `:580-618`):
```csharp
        // ⛔ S-2 (рішення PO 11.11, D-475): ЧУЖУ чи системну (автор `null`) задачу повторює лише носій
        // `System.RunJob` (вбудований `SystemAdministrator` — із сіду `FP:sysadmin-rights`). СВОЮ — як і раніше:
        // з `System.ViewHealth` або за L1-19 (`MayRepeatOwnAsync`: право на саму дію ще за автором; UX-09).
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        var ownerId = await jobs.GetCreatedByUserIdAsync(jobId, ct).ConfigureAwait(false);
        var own = ownerId is { } owner && owner == userId;

        var allowed = own
            ? PermissionCheck.IsGranted(profile, GetJobStatusHandler.Permission)
              || await MayRepeatOwnAsync(profile, jobId, status.DocumentId, ct).ConfigureAwait(false)
            : PermissionCheck.IsGranted(profile, CancelJobHandler.ForeignJobPermission);

        if (!allowed)
        {
            // L1-19: відмова автору — та сама, що для чужої задачі: нічого про цільовий документ чи право на дію.
            var required = own ? GetJobStatusHandler.Permission : CancelJobHandler.ForeignJobPermission;
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Потрібне право {required}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.jobNotYours",
                    ["permission"] = required,
                });
        }

        // ⛔ R9-F3 / F3-02: збір, синк подій і перевірка узгодженості прав під час виконання не
        // перевіряють — право на саму дію потрібне від КОЖНОГО, хто повторює (і від носія `System.RunJob`).
        if (DangerousActionPermission(jobId) is { } action && !PermissionCheck.IsGranted(profile, action))
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Потрібне право {action}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.permission",
                    ["permission"] = action,
                });
        }
```
   Чому для своєї задачі поведінка та сама: (а) з `ViewHealth` — дозвіл, далі перевірка права на дію, як у старій гілці `:586-600`. (б) Без `ViewHealth` — `MayRepeatOwnAsync`. Для небезпечних типів він повертає `IsGranted(required)` (`:709-712`), тож друга перевірка нічого не відхиляє, як і стара гілка `:601-618`. Порядок `ViewHealth || MayRepeatOwnAsync` зберігає коротке замикання: з `ViewHealth` `MayRepeatOwnAsync` (а з ним `RequireVisibleAsync`) не викликається, як і було.
5. Той самий клас, XML-коментар `:508-511`: «Право <c>System.ViewHealth</c> — або автор ВЛАСНОЇ задачі» → «Чужу чи системну — право <c>System.RunJob</c> (S-2, D-475); свою — автор (з <c>System.ViewHealth</c> або за L1-19; UX-09)». Останнє речення абзацу `:532-533` («Межа контракту §9 (<c>System.ViewHealth</c> для чужої задачі) не змінена — звуження до <c>System.RunJob</c> лишається рішенням людини.») → «✎ S-2 (рішення PO 11.11, D-475): для чужої задачі — <c>System.RunJob</c>.». У `MayRepeatOwnAsync`, `:702-703`: «повторити його може лише власник <c>System.ViewHealth</c>» → «свою задачу невідомого типу автор повторює лише з <c>System.ViewHealth</c>; чужу — лише носій <c>System.RunJob</c>».
6. `src/Ecr.Api/Controllers/JobsController.cs:96-99`. Було:
```csharp
    /// <summary>
    /// Ручний перезапуск проваленої задачі. Право <c>System.ViewHealth</c> — або
    /// автор ВЛАСНОЇ задачі (директива №11, T10 #40; UX-09).
    /// </summary>
```
   Стало:
```csharp
    /// <summary>
    /// Ручний перезапуск проваленої задачі. Чужу чи системну — право <c>System.RunJob</c>
    /// (S-2, D-475); свою — автор (директива №11, T10 #40; UX-09).
    /// </summary>
```
   `:116-119`. Було:
```csharp
    /// <summary>
    /// Просить задачу завершитися. Право <c>System.ViewHealth</c> — або автор
    /// ВЛАСНОЇ задачі (Q-156).
    /// </summary>
```
   Стало:
```csharp
    /// <summary>
    /// Просить задачу завершитися. Чужу чи системну — право <c>System.RunJob</c>
    /// (S-2, D-475); свою — автор без права (Q-156).
    /// </summary>
```
   Потім — останнім кроком лінії, після rebase, тим самим комітом (CLAUDE.md, таблиця спільних файлів): `ECR_UPDATE_SNAPSHOT=1 dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~OpenApiSnapshotTests"` (`tests/Ecr.Api.Tests/OpenApiSnapshotTests.cs:36-44`) → змінюються лише `summary` у `contracts/openapi.snapshot.json:43162` і `:43243`. Далі `npm run api:types` у `src/Ecr.Web` (`package.json:15`) → `schema.d.ts:8758-8759`, `:8833-8834`. Форма відповідей не змінюється.
7. `docs/build/02-contracts.md:4773-4774`. Було:
```
| `POST` | `/api/v1/jobs/{jobId}/restart` | `System.ViewHealth` | 5 |
| `POST` | `/api/v1/jobs/{jobId}/cancel` | `System.ViewHealth` | 5 |
```
   Стало:
```
| `POST` | `/api/v1/jobs/{jobId}/restart` | `System.RunJob` (чужа чи системна задача, S-2/D-475; свою — автор з `System.ViewHealth` або за L1-19; повтор збору/синку — ще `Integration.Manage`) | 5 |
| `POST` | `/api/v1/jobs/{jobId}/cancel` | `System.RunJob` (чужа чи системна задача, S-2/D-475; свою — автор без права, Q-156) | 5 |
```
   (Комірка з поясненням — та сама форма, що й `:4771`. Регулярка сторожа `EndpointCoverageTests.cs:1763-1767` такий рядок не розбирає, але `System.RunJob` уже оголошено рядком `:4766` (`POST /consistency/run`), а `System.ViewHealth` — `:4764` та іншими. Покриття прав не змінюється.)
8. `docs/admin/admin-guide.md:891-892` — колонка «Хто може»: «Повтор» → «чужу чи системну — `System.RunJob` (є в `SystemAdministrator`); свою — автор»; «Скасування» → «чужу чи системну — `System.RunJob`; свою — автор». `:122` (рядок права `System.RunJob`) — «ручний запуск системних задач, зокрема перевірки узгодженості» → «ручний запуск системних задач, зокрема перевірки узгодженості; скасування й повтор ЧУЖИХ і системних задач (S-2, D-475)».
9. `docs/tz/10-decisions.md` — новий рядок після `:911` (останній рядок розділу 1 — `D-474`). Номер — наступний вільний: на `39aea41d9` це `D-475`. `D-473` згаданий у тексті (`:685`, `:1138`), але рядка не має — його не брати; якщо інша лінія зайняла `D-475` раніше, взяти наступний і поправити посилання в кроках 1–6. Текст:
```
| **D-475** | **✎ 2026-11-11. Скасування й повтор ЧУЖИХ і системних фонових задач — лише `System.RunJob`; свої — як і раніше.** Рішення PO 11.11 (аудит 2026-11-10, S-2): «лише право System.RunJob або роль адміна». Що робить код: `CancelJobHandler`/`RestartJobHandler` (`IntegrationHandlers.cs`) для задачі, автор якої не поточний користувач (зокрема системної, `CreatedByUserId = null`), вимагають `System.RunJob` (`CancelJobHandler.ForeignJobPermission`); безпечне `System.ViewHealth` («Аудитор», `TemplateAdministrator`, `PeriodAdministrator`) дає лише перегляд. «Роль адміністратора» окремо не перевіряється (архітектурне правило 7): вбудований `SystemAdministrator` має `System.RunJob` із сіду `FP:sysadmin-rights` (`D-388`), власна роль — якщо право видано. Своя задача: скасування — без права (Q-156); повтор — з `System.ViewHealth` або за L1-19. Повтор збору/синку й далі вимагає `Integration.Manage` (R9-F3). Клієнт ховає «Скасувати»/«Повторити» чужої задачі без `System.RunJob` (`JobsPage.tsx`, `JobFacts.tsx`). **Наслідки:** `TemplateAdministrator`/`PeriodAdministrator`/`Auditor` більше не зупиняють чужий перерахунок — це робить `SystemAdministrator`; зміна контракту прав (`02-contracts.md` §9, описи OpenAPI), форма відповідей та сама. **Статус: рішення людини.** | PO 11.11 (`docs/build/audit-2026-11-10/00-PLAN.md` §4) | `D-388`, `D-121`, `D-470`, Q-156, L1-19, R9-F3 · `docs/build/audit-2026-11-10/01-security-api.md` S-2 · `IntegrationHandlers.cs` · `JobsController.cs` · `JobCancelTests`, `JobRestartOwnerTests`, `JobRestartTargetRightTests`, `RestartJobHandlerTests` |
```
   І рядок історії в кінці файлу, після `:1139`:
```
| 2026-11-11 | `D-475`: скасування й повтор чужих і системних фонових задач — лише `System.RunJob` (S-2 аудиту 2026-11-10); `docs/CHECKSUMS.txt` оновлено в тому ж коміті | рішення PO 11.11 |
```
10. `docs/CHECKSUMS.txt:11` — нова сума `tz/10-decisions.md` (`sha256sum docs/tz/10-decisions.md`). Готовий рядок дає падіння `tests/Ecr.Architecture.Tests/SpecificationBaselineTests.cs:35`.
11. Клієнт, `src/Ecr.Web/src/features/jobs/JobFacts.tsx:131-145`. Було:
```tsx
/**
 * Чи показувати «Повторити» (UX-09, директива №11, T10 #40): лише `Failed`,
 * і лише власнику ВЛАСНОЇ задачі АБО праву `System.ViewHealth`.
 *
 * ⛔ Чиста функція, окремо від компонента, саме заради мутаційного доказу:
 * «чужа задача без ViewHealth» і «не-Failed стан» перевіряються без монтування
 * дерева й без заглушки мережі під `/restart`.
 */
export function canRestartJob(
  state: string,
  isOwnJob: boolean,
  hasViewHealth: boolean,
): boolean {
  return state === 'Failed' && (isOwnJob || hasViewHealth);
}
```
   Стало:
```tsx
/**
 * Право дії над ЧУЖОЮ чи системною задачею — повторити або скасувати (S-2, рішення PO 11.11, D-475).
 * Дзеркало серверного `CancelJobHandler.ForeignJobPermission`: вбудований `SystemAdministrator` має його з сіду
 * (`FP:sysadmin-rights`), «Аудитор» і адміністратори шаблонів/періодів — ні (у них лише `System.ViewHealth`).
 */
export const ForeignJobPermission = 'System.RunJob';

/** Чи задача ВЛАСНА: автор відомий і це поточний користувач. Системна (`null`) — нічия. */
export function isOwnJobOf(
  createdByUserId: number | null | undefined,
  userId: number | undefined,
): boolean {
  return createdByUserId !== null && createdByUserId !== undefined && createdByUserId === userId;
}

/**
 * Чи показувати «Повторити» (UX-09, директива №11, T10 #40; S-2): лише `Failed`,
 * і лише автору ВЛАСНОЇ задачі АБО носію `System.RunJob` (`ForeignJobPermission`).
 *
 * ⛔ Чиста функція, окремо від компонента, саме заради мутаційного доказу:
 * «чужа задача без RunJob» і «не-Failed стан» перевіряються без монтування
 * дерева й без заглушки мережі під `/restart`.
 */
export function canRestartJob(
  state: string,
  isOwnJob: boolean,
  hasRunJob: boolean,
): boolean {
  return state === 'Failed' && (isOwnJob || hasRunJob);
}
```
   Той самий файл, `JobRetry` (`:172-187`): у деструктуризації й типі пропсів `hasViewHealth` → `hasRunJob` (`readonly hasRunJob: boolean;`), виклик `canRestartJob(state, isOwnJob, hasRunJob)`. У докстрінгу `:147-171` речення «на `/admin/jobs` видимість і так гейтується правом `System.ViewHealth` (`JobsPage.tsx` передає `isOwnJob` буквально `false`, `hasViewHealth` — `true`)» замінити на «на `/admin/jobs` власність обчислює `isOwnJobOf(createdByUserId, сеанс)`, а `hasRunJob` — `can(me, ForeignJobPermission)` (S-2)»; речення про `RecentJobs` «НАВМИСНО не обмежений власністю» — прибрати (після S-2 обмежений).
12. `src/Ecr.Web/src/pages/admin/JobsPage.tsx`:
   - імпорти: `:14` `import { JobAttempt, JobResultLink, JobRetry, jobAuthor } from '@/features/jobs/JobFacts';` → `import { ForeignJobPermission, JobAttempt, JobResultLink, JobRetry, isOwnJobOf, jobAuthor } from '@/features/jobs/JobFacts';`; новий рядок `import { can, useSession } from '@/shared/session/useSession';` (той самий імпорт, що `ConsistencyIssuesPage.tsx:13`).
   - після `isCancellable` (`:51-54`) додати:
```tsx
/**
 * Чи показувати «Скасувати» (S-2, рішення PO 11.11): стан ще активний, і задача ВЛАСНА або є `System.RunJob`.
 * Сервер (`CancelJobHandler`) вирішує сам (`403 jobNotYours`); це лише відсутність кнопки, що дасть 403.
 */
export function canCancelJob(state: string, isOwnJob: boolean, hasRunJob: boolean): boolean {
  return isCancellable(state) && (isOwnJob || hasRunJob);
}
```
   - у тілі `JobsPage` після `:238` (`const resultUrl = …`):
```tsx
  // ⛔ S-2: чужу чи системну задачу скасовує/повторює лише носій `System.RunJob`; маршрут гейтує лише `System.ViewHealth`.
  const session = useSession();
  const hasRunJob = can(session.data, ForeignJobPermission);
  const ownJob = isOwnJobOf(detail.data?.createdByUserId ?? summary?.createdByUserId, session.data?.userId);
```
   - `:440` `{isCancellable(openState) && (` → `{canCancelJob(openState, ownJob, hasRunJob) && (`;
   - `:445-456`. Було:
```tsx
                    {/*
                     * ⛔ `hasViewHealth` — буквально `true`: маршрут `/admin/jobs`
                     * вимагає `System.ViewHealth` (`routes.ts`), тож кожен, хто
                     * бачить шторку, право має. `isOwnJob` — `false` буквально:
                     * перелік може містити чужі задачі, і `true` тут було б
                     * вигадкою. Сервер перевіряє власника сам (`403`).
                     */}
                    <JobRetry
                      jobId={panel}
                      state={openState}
                      isOwnJob={false}
                      hasViewHealth
```
   Стало:
```tsx
                    {/*
                     * ⛔ S-2: маршрут `/admin/jobs` гейтує лише `System.ViewHealth` (перегляд); «Повторити»
                     * чужої чи системної задачі — лише з `System.RunJob`. Власність — з `createdByUserId`
                     * (його бачить кожен, хто має `ViewHealth`). Сервер перевіряє заново (`403`).
                     */}
                    <JobRetry
                      jobId={panel}
                      state={openState}
                      isOwnJob={ownJob}
                      hasRunJob={hasRunJob}
```
   (`JobStatus.createdByUserId` і `JobSummary.createdByUserId` — `schema.d.ts:32767`, `:32835`; `CurrentUserDto.userId` — те саме поле, що `useSession.ts:39-41` звіряє з сеансом.) Нових рядків UI немає.
13. `src/Ecr.Web/src/features/jobs/MyTasksDrawer.tsx:250` `hasViewHealth={false}` → `hasRunJob={false}`; у коментарі `:240-242` «окремо ходити за правом `System.ViewHealth` заради нього немає сенсу (`hasViewHealth` нижче…» → «…за правом `System.RunJob` заради нього немає сенсу (`hasRunJob` нижче…». `src/Ecr.Web/src/features/jobs/api.ts:163-164`: «Право `System.ViewHealth` — АБО автор ВЛАСНОЇ задачі» → «Чужу чи системну — право `System.RunJob` (S-2); свою — автор».

**Тест-доказ:**
- `tests/Ecr.Api.Tests/JobCancelTests.cs` (через дію контролера на підроблених портах — так влаштований файл, `:25-29`):
  - помічник `WithHealthPermission` (`:213-220`) перейменувати на `WithForeignJobPermission`, профіль — `.Permission(CancelJobHandler.ForeignJobPermission)`; три виклики (`:70`, `:93`, `:199`) — на нього. Без цього `Скасування_задачі_що_виконується…` (`:68`, автор не налаштований → `null` → системна), `Успішну_задачу_скасувати_не_можна` (`:90`, право перевіряється ДО стану) і `Скасування_чужої_задачі_пише_подію_JobCancelled` (`:197`) після фіксу отримають 403.
  - новий `[Theory]` `Матриця_права_скасування_чужої_системної_і_своєї_задачі(string permissions, int? owner, bool allowed)`. Входить `Stranger` (`:35`), права — через кому в `AccessBuilder`, `_jobs.GetCreatedByUserIdAsync(JobId, …).Returns(owner)`, виклик `Controller().Cancel(JobId, …)`. Рядки: `("System.ViewHealth", Author, false)` — Аудитор/адміністратор шаблонів, чужа; `("System.ViewHealth", null, false)` — системна; `("System.ViewHealth,System.RunJob", Author, true)` — набір `SystemAdministrator`; `("System.RunJob", null, true)`; `("", Stranger, true)` — своя без права; `("System.ViewHealth", Stranger, true)` — своя. Для `false`: `AccessDeniedException`, `Details["messageKey"] == "err.ECR-AUTH-0403.jobNotYours"`, `Details["permission"] == CancelJobHandler.ForeignJobPermission`, `CancelAsync` не викликано. Для `true`: `AcceptedResult`, `CancelAsync` — рівно раз.
  - падає без фіксу: рядки `("System.ViewHealth", Author|null, false)` — стара гілка `IsGranted(ViewHealth)` пропускає, і `ThrowsAsync` червоний.
- `tests/Ecr.Api.Tests/JobRestartOwnerTests.cs`: кейс `З_ViewHealth_повторюється_будь_яка_задача_навіть_системна` (`:88-95`) замінити двома. `З_ViewHealth_без_RunJob_чужу_і_системну_не_повторити` — `[Theory]` з `owner` = `Author` і `null`; `SignedIn(Stranger, GetJobStatusHandler.Permission)` → 403 `jobNotYours`, `permission == "System.RunJob"`, `RestartAsync` не викликано. `З_RunJob_повторюється_чужа_і_системна` — `SignedIn(Stranger, CancelJobHandler.ForeignJobPermission)` → `202`. Перший без фіксу червоний (гілка `ViewHealth` повторює).
- `tests/Ecr.Api.Tests/JobRestartTargetRightTests.cs`:
  - `З_ViewHealth_чужий_збір_і_перевірку_без_права_на_дію_не_повторити` (`:141-161`) → `З_RunJob_чужий_збір_і_синк_без_Integration_Manage_не_повторити`: `SignedIn("System.RunJob")`, лише `InlineData` `ICollectionJob`/`ISourceEventSyncJob` (рядок `IConsistencyCheckJob` прибрати — `RunJob` і є його правом на дію). Твердження ті самі (`messageKey == "err.ECR-AUTH-0403.permission"`, `permission == "Integration.Manage"`).
  - `З_ViewHealth_і_Integration_Manage_чужий_збір_повторюється_і_лишає_слід` (`:165-183`) → `З_RunJob_і_Integration_Manage_…`, `SignedIn("System.RunJob", "Integration.Manage")`.
  - Без цих правок обидва кейси після фіксу падають (чужа задача під `ViewHealth` → `jobNotYours`). `Quartz_скан_сиріт_лише_з_ViewHealth` (`:245`) — своя задача (`Failed` ставить автором `Author`), лишається зеленим без змін.
- `tests/Ecr.Application.Tests/Integration/RestartJobHandlerTests.cs`: профіль конструктора (`:31-32`) — `.Permission(CancelJobHandler.ForeignJobPermission)`. Автор задачі там не налаштований → `null` → системна, тож інакше `:54`, `:89` отримали б 403 замість 409/404. Новий кейс `З_ViewHealth_без_RunJob_чужу_провалену_задачу_перезапустити_не_можна`: профіль лише з `GetJobStatusHandler.Permission`, автор `Viewer + 1` → `AccessDeniedException`, `RestartAsync` не викликано. Без фіксу — червоний.
- «Адміністратор → OK» на рівні ролі — `tests/Ecr.Infrastructure.Tests/Persistence/SeedTests.cs`, новий кейс `System_RunJob_серед_вбудованих_ролей_має_лише_SystemAdministrator` за зразком `:605-646` (`ScalarAsync`, `:1171`). Перевіряє, що `COUNT(*)` пар `System.RunJob` у вбудованої `SystemAdministrator` = 1, а в будь-якої іншої вбудованої ролі = 0. Це сторож ПЕРЕДУМОВИ рішення (адміністратор проходить через право, Аудитор — ні): він зелений і до, і після фіксу. Червоніє, якщо `FP:sysadmin-rights` втратить пару або право потрапить у ще одну вбудовану роль.
- Клієнт, `src/Ecr.Web/src/features/jobs/__tests__/jobRetry.test.tsx`: проп `hasViewHealth` → `hasRunJob` (`:60`, `:68`, `:76`, `:85`), назви кейсів «ViewHealth» → «RunJob». Новий `describe('isOwnJobOf')`: `(7, 7)` → `true`, `(7, 8)` → `false`, `(null, 7)` → `false`, `(7, undefined)` → `false`.
- `src/Ecr.Web/src/pages/admin/__tests__/JobsPage.cancelJob.test.tsx` і `JobsPage.retryResult.test.tsx`: `mockFetch` відповідає на `url.endsWith('/api/v1/me')` тілом форми з `JobsPage.drawer.a11y.test.tsx:45-56` (`userId: 1`, `permissions: ['System.ViewHealth', 'System.RunJob']`). Без цього задачі тестів (без `createdByUserId` → системні) втратять кнопки, і наявні кейси впадуть. Новий кейс у `JobsPage.cancelJob.test.tsx` — `мутація: лише ViewHealth, чужа задача — кнопки «Скасувати» немає`: `/me` з `permissions: ['System.ViewHealth']`, задача `Running` з `createdByUserId: 2` → у шторці немає `⟦jobs.cancel⟧`. Те саме з `createdByUserId: 1` → кнопка є. Аналогічна пара для `⟦jobs.restart⟧` у `JobsPage.retryResult.test.tsx`. Без кроку 12 кнопка показується за самим станом → «немає» червоний.

**Перевірка після впровадження:**
- `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~JobCancelTests|FullyQualifiedName~JobRestart|FullyQualifiedName~RecalculationJobIdTests"`;
- `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~RestartJobHandlerTests"`;
- `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~SeedTests"` (потрібен `ECR_TEST_SQL`);
- `dotnet test tests/Ecr.Architecture.Tests` (`EndpointCoverageTests`, `SpecificationBaselineTests`, `ErrorTitleCatalogTests`) — обов'язково навіть для клієнтських правок (правило інтегратора);
- `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~OpenApiSnapshotTests"` (після перегенерації — зелений);
- `cd src/Ecr.Web && npx vitest run src/features/jobs src/pages/admin/__tests__/JobsPage && npx tsc --noEmit && npm run lint`;
- стенд: «Аудитор» на `/admin/jobs` у шторці чужої задачі не бачить «Скасувати/Повторити», а прямий `POST /api/v1/jobs/{id}/cancel` дає 403 `jobNotYours`. `SystemAdministrator` — кнопки є, `202`. Автор у шухляді «Мої задачі» скасовує й повторює свою, як раніше.

**Ризики і побічні ефекти:** `TemplateAdministrator`/`PeriodAdministrator`/`Auditor` втрачають скасування й повтор чужих задач — це робить `SystemAdministrator`. Зміна контракту прав (не форми), тож потрібні примітки до оновлення (`D-475`). Лінія — через `lane/integ-*`, послідовно з іншими змінами `openapi.snapshot.json`/`schema.d.ts` (`00-PLAN.md` §3, L16/L17). Кількість запитів не змінюється: `BuildProfileAsync` і `GetCreatedByUserIdAsync` уже є. `BootstrapAdministrator` чужих задач не скасовує, але як носій `Security.ManageRoles` може видати собі право (`D-121`).

**Обґрунтування:** одна перевірка права (`System.RunJob`) замість «права АБО ролі». Роль перевіряти в обробнику заборонено правилом 7, а вбудований адміністратор має це право за `D-388`. Друга гілка за роллю дублювала б сід і розійшлася б із ним, щойно адміністратор змінить роль. Відкинуто: (а) нове право `System.CancelJob` — потребує сіду, перекладів, ролей і сторожа `SeedTests`, хоча каталог уже називає `RunJob` «запуском і скасуванням»; (б) `RunJob ∨ Security.ManageUsers` (`BootstrapAdmin.AdminPermission`, `BootstrapAdmin.cs:37-38`) — це право керування користувачами, не задачами, і його носії `SystemAdministrator` (уже з `RunJob`) і тимчасовий bootstrap-запис. Два права на одну дію заплутали б контракт.

**Рішення PO:** отримано 11.11 — «лише право System.RunJob або роль адміна» (`00-PLAN.md` §4). Нових питань немає.
**Впевненість:** підтверджено читанням (обробники, сід, клієнтські місця виклику, тести, яких торкається зміна).
**Залежності:** немає за кодом. За процесом — токен контракту (`openapi.snapshot.json`/`schema.d.ts`) разом із L17/W-2 (`00-PLAN.md` §3). Спільний файл документації `docs/build/02-contracts.md` з S-1/S-4/S-5/S-6 — різні рядки.

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

### S-6 — low — Чат таблиці: `mention-candidates` і запис повідомлення без межі частоти (опис POST обіцяє `429`)
**✎ рішення PO 11.11:** «(а) додати». Межа частоти для `GET …/mention-candidates` і `POST …/messages`, щоб обіцяна `429` стала правдою. Нижче — повна специфікація. Знахідка доповнює P-7 (звіт 03): та сама точка `mention-candidates`, файли не перетинаються. Порядок — у «Порядку впровадження».

**Категорія:** security (DoS / межа частоти)

**Де:**
- `src/Ecr.Api/Controllers/TableDiscussionsController.cs:89-96` — дія `MentionCandidates` без `[EnableRateLimiting]`;
- `src/Ecr.Api/Controllers/TableDiscussionsController.cs:106-121` — дія `Post` без `[EnableRateLimiting]`, хоча її XML-опис обіцяє «`429` — ліміт запитів» (`:103`; цей текст уже в `openapi.snapshot.json`);
- вартість кандидатів: `src/Ecr.Application/Documents/Discussions/TableDiscussionQueries.cs:190-216` — до 200 кандидатів (5 сторінок × 40), для кожного `TableDiscussionAccess.CanSeeAsync` → `BuildProfileAsync` + `RequireVisibleAsync` + `ReadScopeAsync` (`TableDiscussionAccess.cs:165-188`);
- вартість запису: `src/Ecr.Application/Documents/Discussions/TableDiscussionCommands.cs:43-147`. Кожне повідомлення — до 10 згадок (`TableDiscussionAccess.MaxMentions`, `TableDiscussionAccess.cs:39`), для кожної рішення доступу (`FilterMentionsAsync`, `:82-84`); транзакція з блокуванням зведення (`LockStateAsync`, `:96`); рядок повідомлення, згадок і `aud.SecurityEvent` (`:111-125`);
- наявна інфраструктура меж: реєстрація політик `src/Ecr.Api/Security/LoginRateLimiting.cs:269-328` (`AddPolicy`, `:324-327`); `app.UseRateLimiter()` — після `UseAuthentication`/`UseAuthorization`, до обробників (`src/Ecr.Api/Program.cs:352-365`); спільна відмова `LoginRateLimiting.RejectAsync` (`:394-410`) → `problem+json` (`:431-460`: `status`, `title`/`detail` з каталогу, `errorCode`, `correlationId`, `messageKey`, заголовок `Retry-After`);
- зразки: `SearchRateLimitPolicy` (`src/Ecr.Api/Security/SearchRateLimitPolicy.cs:20-67`, 30 за 10 с на користувача, ключі `Security:RateLimit:SearchPermit`/`SearchWindowSeconds`) на `GET /search` (`SearchController.cs:21`) і на кандидатах передач (`DocumentEditAssignmentsController.cs:114`, «T8»); база `UserPerMinuteRateLimitPolicy` (`src/Ecr.Api/Security/VerifyValidateRateLimitPolicies.cs:21-68`) з двома нащадками `SnapshotVerifyRateLimitPolicy`/`DocumentValidateRateLimitPolicy` (`:72-104`, D-386);
- конфігурація: `src/Ecr.Api/appsettings.json:38-55` (`Security:RateLimit`); перевірка мінімумів `src/Ecr.Api/Options/EcrConfigurationValidation.cs:58-73`; runbook `docs/admin/operations-runbook.md:131-141`; контракт `docs/build/02-contracts.md:4233` (`ECR-REQ-0429`), `:4648-4649` (маршрути чату).

**Проблема:** будь-який користувач із `Document.Discuss` може (1) слати `GET …/mention-candidates?q=aa` без обмеження — кожен запит означає до 200 рішень доступу щодо інших користувачів; (2) писати повідомлення без обмеження — кожне означає транзакцію з блокуванням зведення чату, до 10 рішень доступу на згадки й рядок у журналі безпеки. Кандидати передач (`edit-assignments/candidates`) — та сама форма пошуку, і вони вже під `SearchRateLimitPolicy` (T8). Опис POST обіцяє `429`, якого сервер не віддає.

**Сценарій відтворення:** (1) скрипт у циклі `GET /api/v1/documents/{id}/tables/{code}/mention-candidates?q=ab` → завжди `200`. (2) цикл `POST /api/v1/documents/{id}/tables/{code}/messages` з `{ "body": "x", "periodKey": 202601 }` → завжди `201`; росте `aud.SecurityEvent` і лічильники «непрочитано» в усіх, хто бачить таблицю. Очікувано (рішення PO 11.11): (1) понад 30 запитів за 10 с на користувача — `429 ECR-REQ-0429` (`err.ECR-REQ-0429.tooManySearches`), як у прецеденті T8; (2) понад 20 повідомлень за хвилину на користувача — `429 ECR-REQ-0429` (`err.ECR-REQ-0429.tooManyChatMessages`), обидва з `Retry-After`.

**Вплив:** навантаження на БД/CPU від одного автентифікованого користувача; спам у журнал безпеки й «непрочитані» інших. Точна кількість звернень до БД на кандидата залежить від кешу профілю — потребує заміру на стенді (див. P-7).

**Зміна — покроково:**
1. **Кандидати згадок — наявна політика `search`.** `src/Ecr.Api/Controllers/TableDiscussionsController.cs:1-5`: додати `using Ecr.Api.Security;` і `using Microsoft.AspNetCore.RateLimiting;` (як `DocumentEditAssignmentsController.cs:1`, `:5`). Той самий файл, `:89-90`. Було:
```csharp
    [HttpGet("tables/{tableCode}/mention-candidates")]
    [ProducesResponseType<IReadOnlyList<TableDiscussionMentionCandidateDto>>(StatusCodes.Status200OK)]
```
   Стало:
```csharp
    [HttpGet("tables/{tableCode}/mention-candidates")]
    [EnableRateLimiting(SearchRateLimitPolicy.PolicyName)] // S-6: межа на користувача, 429 — той самий кошик, що пошук і кандидати передач (T8)
    [ProducesResponseType<IReadOnlyList<TableDiscussionMentionCandidateDto>>(StatusCodes.Status200OK)]
```
   Чому чинна політика, а не нова: форма та сама (підказка на кожен символ з дебаунсом 200 мс, `src/Ecr.Web/src/features/tableChat/ChatComposer.tsx:63`), числа обґрунтовано в `SearchRateLimitPolicy.cs:25-30` («30 за 10 с = 3/с; швидкий набір із дебаунсом дає 1–2/с»). Додаткова ціна кандидата обмежена P-7 (80 перевірок на відповідь), тож найгірший випадок на користувача — 30 × 80 = 2 400 перевірок за 10 с замість необмеженої кількості.
2. **Запис у чат — нова політика.** `src/Ecr.Api/Security/VerifyValidateRateLimitPolicies.cs:32-42` — дозволити нащадку власний дефолт (наявні нащадки не змінюються). Було:
```csharp
    /// <param name="detailFallback">Запасна подробиця — коли каталог недоступний.</param>
    protected UserPerMinuteRateLimitPolicy(
        IConfiguration configuration, string permitKey, string detailKey, string detailFallback)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _permit = configuration.GetValue(permitKey, DefaultPermitPerMinute);
```
   Стало:
```csharp
    /// <param name="detailFallback">Запасна подробиця — коли каталог недоступний.</param>
    /// <param name="defaultPermit">Межа, коли конфігурація мовчить; типово <see cref="DefaultPermitPerMinute"/>.</param>
    protected UserPerMinuteRateLimitPolicy(
        IConfiguration configuration, string permitKey, string detailKey, string detailFallback,
        int defaultPermit = DefaultPermitPerMinute)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _permit = configuration.GetValue(permitKey, defaultPermit);
```
3. Новий файл `src/Ecr.Api/Security/TableDiscussionPostRateLimitPolicy.cs`:
```csharp
// src/Ecr.Api/Security/TableDiscussionPostRateLimitPolicy.cs

namespace Ecr.Api.Security;

/// <summary>
/// Межа <c>POST /api/v1/documents/{id}/tables/{tableCode}/messages</c> (S-6, рішення PO 11.11): запис у чат таблиці
/// на КОРИСТУВАЧА — типово 20 повідомлень за хвилину; понад межу — 429 <c>ECR-REQ-0429</c> із <c>Retry-After</c>.
/// </summary>
/// <remarks>
/// Кожне повідомлення — транзакція з блокуванням зведення чату, до 10 згадок (кожна — рішення доступу) і рядок
/// <c>aud.SecurityEvent</c> (<c>TableDiscussionCommands.PostAsync</c>). 20/хв — одне повідомлення на 3 с тривало:
/// людина в чаті межі не бачить, скрипт не заповнює журнал безпеки й чужі «непрочитані». Повтор із тим самим
/// <c>clientMessageId</c> теж рахується: обмежувач стоїть до обробника (<c>Program.cs</c>, <c>UseRateLimiter</c>).
/// </remarks>
/// <param name="configuration">Конфігурація застосунку.</param>
public sealed class TableDiscussionPostRateLimitPolicy(IConfiguration configuration)
    : UserPerMinuteRateLimitPolicy(configuration, PermitKey, DetailKey, DetailFallback, DefaultPermit)
{
    /// <summary>Ім'я політики для <c>[EnableRateLimiting]</c>.</summary>
    public const string PolicyName = "discussion-post";

    /// <summary>Ключ конфігурації: межа за хвилину на користувача.</summary>
    public const string PermitKey = "Security:RateLimit:DiscussionPostPermitPerMinute";

    /// <summary>Межа за хвилину на користувача, якщо конфігурація мовчить.</summary>
    public const int DefaultPermit = 20;

    /// <summary>Ключ каталогу подробиці відмови (повним літералом — так його шукає сторож каталогу).</summary>
    public const string DetailKey = "err.ECR-REQ-0429.tooManyChatMessages";

    /// <summary>Запасна подробиця відмови — коли каталог недоступний.</summary>
    public const string DetailFallback = "Too many chat messages in a short time. Wait a moment and try again.";
}
```
   Розділ — `"user:" + UserIdClaim`, анонім — один «закритий» розділ (успадковано, `VerifyValidateRateLimitPolicies.cs:53-67`). Вікно — фіксована хвилина без черги. `IConfiguration` — неявний `using` Web SDK, як у сусідньому файлі.
4. `src/Ecr.Api/Security/LoginRateLimiting.cs:327` — після рядка `DocumentValidateRateLimitPolicy` додати:
```csharp
            options.AddPolicy<string, TableDiscussionPostRateLimitPolicy>(TableDiscussionPostRateLimitPolicy.PolicyName); // S-6
```
5. `src/Ecr.Api/Controllers/TableDiscussionsController.cs:106-107`. Було:
```csharp
    [HttpPost("tables/{tableCode}/messages")]
    [ProducesResponseType<TableDiscussionMessageDto>(StatusCodes.Status201Created)]
```
   Стало:
```csharp
    [HttpPost("tables/{tableCode}/messages")]
    [EnableRateLimiting(TableDiscussionPostRateLimitPolicy.PolicyName)] // S-6: 20/хв на користувача — обіцяна в описі 429
    [ProducesResponseType<TableDiscussionMessageDto>(StatusCodes.Status201Created)]
```
   XML-описи обох дій НЕ змінювати. У POST `429` уже названо (`:103`). Будь-яка правка `<summary>/<remarks>` чи новий `[ProducesResponseType(429)]` змінили б `openapi.snapshot.json`/`schema.d.ts` і зробили б лінію L15 контрактною (послідовно з L16/L17). Атрибут `[EnableRateLimiting]` в OpenAPI не потрапляє (прецедент `DocumentEditAssignmentsController.cs:114`).
6. `src/Ecr.Api/appsettings.json:53-54` — після `"ValidatePermitPerMinute": 100` (додати кому) рядок `"DiscussionPostPermitPerMinute": 20`. Сторож `tests/Ecr.Architecture.Tests/ConfigurationKeysTests.cs:191` вимагає, щоб кожен ключ, який читає код, був у файлі. Значення збігається з дефолтом коду.
7. `src/Ecr.Api/Options/EcrConfigurationValidation.cs:70` — після `("Security:RateLimit:ValidatePermitPerMinute", 1),` додати `(Ecr.Api.Security.TableDiscussionPostRateLimitPolicy.PermitKey, 1),`. Нуль дав би межу «жодного повідомлення» — той самий привід, що U1-09 (`:60`).
8. Сід — новий файл `src/Ecr.Infrastructure/Persistence/Sql/seed/features/TC-post-rate-limit.sql`. Ім'я відповідає шаблону `^[A-Z0-9]+-[a-z0-9-]+\.sql$` (`SeedSplitLayoutTests.cs:263`), структура — як `COLL-quota-verify-validate.sql`:
```sql
-- TC:post-rate-limit ── ru/kz: S-6 (аудит 2026-11-10): межа частоти запису в чат таблиці; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0429.tooManyChatMessages', N'ru', N'Слишком много сообщений в чат за короткое время. Подождите немного и повторите попытку.'),
    (N'err.ECR-REQ-0429.tooManyChatMessages', N'kz', N'Қысқа уақыт ішінде чатқа хабарламалар тым көп. Біраз күтіп, қайталап көріңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- TC:post-rate-limit ── кінець секції ──
-- TC:post-rate-limit ── S-6: межа частоти запису в чат таблиці на користувача; ru/kz — порцією TC:post-rate-limit вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REQ-0429.tooManyChatMessages', N'en', N'Too many chat messages in a short time. Wait a moment and try again.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- TC:post-rate-limit ── кінець секції ──
```
   (Ресурс підхоплюється маскою `Persistence\Sql\seed\**\*.sql`, `Ecr.Infrastructure.csproj:48`. Порядок фіч — ordinal, `SeedScript.cs:63-66`. Сторож `ErrorTitleCatalogTests.Кожен_messageKey_із_коду_заведено_в_сіді` (`:220`) без цього файлу червоний.)
9. `docs/admin/operations-runbook.md` — новий рядок таблиці після `:141` (`ValidatePermitPerMinute`):
```
| `Security:RateLimit:DiscussionPostPermitPerMinute` | 20 | повідомлень `POST /api/v1/documents/{id}/tables/{tableCode}/messages` (запис у чат таблиці) на КОРИСТУВАЧА за хвилину; фіксоване вікно 1 хв; понад межу — `429` (`ECR-REQ-0429`, ключ `err.ECR-REQ-0429.tooManyChatMessages`) із `Retry-After`; повтор із тим самим `clientMessageId` теж рахується (S-6). Кандидати згадок (`GET …/mention-candidates`) власної межі не мають — ділять кошик `SearchPermit`/`SearchWindowSeconds` (30 за 10 с) із пошуком і кандидатами передач. Лічильник у пам'яті процесу: перезапуск скидає, на кількох вузлах межа діє на кожному окремо. Мінімум — 1 |
```
   Рядок `:137` (`SearchPermit`) і `:134` (зміна S-3) НЕ чіпати — так немає спільного блоку з S-3.
10. `docs/build/02-contracts.md`:
   - `:4233` (`ECR-REQ-0429`) — перед «); у відповіді `Retry-After`» дописати: «; кандидати згадок чату `GET /documents/{id}/tables/{tableCode}/mention-candidates` — той самий кошик, що й пошук (політика `search`); запис у чат `POST /documents/{id}/tables/{tableCode}/messages`, політика `discussion-post`: 20/хв на користувача, `Security:RateLimit:DiscussionPostPermitPerMinute`, ключ `tooManyChatMessages`»;
   - `:4648` — у дужки після «імена решти не повертаються» дописати «; межа частоти — політика `search`, 30 за 10 с на користувача, понад — 429»;
   - `:4649` — у дужки в кінці дописати «; межа 20/хв на користувача (`discussion-post`), понад — 429 `tooManyChatMessages` із `Retry-After`; повтор з тим самим `clientMessageId` теж рахується».
11. `tools/e2e-stand.ps1` — після `:548` (`ECR_Documents__VersionMigration__FromMonthEnabled`):
```powershell
# S-6: межа запису в чат (20/хв на користувача). `tableChatLiveRefresh.spec.ts:102` пише 25 повідомлень поспіль від
# адміністратора, і інші специ чату пишуть від того самого користувача. Лише на одноразовому стенді; типове значення не змінюється.
$env:ECR_Security__RateLimit__DiscussionPostPermitPerMinute = '1000'
```
   Без цього `tableChatLiveRefresh.spec.ts` на 21-му `post` (`:329-336`, `expect(status) < 300`) падає з 429.
12. `docs/tz/10-decisions.md` не змінюється. Межа не відступає від вимог ТЗ, і задокументована в контракті й runbook так само, як межі пошуку, перерахунку й проб SMTP (D-рядків вони не мають). Рішення PO зафіксовано в `00-PLAN.md` §4. Це також уникає спільного файлу з S-2 (`10-decisions.md` + `CHECKSUMS.txt`).

**Клієнт:** змін не треба. Відмова POST: мутація `post` (`src/Ecr.Web/src/features/tableChat/TableChatPanel.tsx:96-106`) показує її через `showApiError` (подробиця з `messageKey`). Композер лишає текст і той самий `clientMessageId` для повтору (`ChatComposer.tsx:103-118`). Відмова кандидатів: `useMentionCandidates` має `retry: false` (`api.ts:274`), а на помилці `candidates.data` порожнє (`ChatComposer.tsx:74`). Глобального обробника помилок запитів немає — лише `MutationCache` (`src/Ecr.Web/src/app/queryClient.ts:1`).

**Тест-доказ:** новий файл `tests/Ecr.Api.Tests/Security/TableDiscussionRateLimitTests.cs`, `[Collection("SqlServer")]`, за зразком `VerifyValidateRateLimitTests.cs` (помічники `ContextOf`, `AllowedInFirstWindow`, `SignedInAsync`, `AssertRejectedAsync` — скопіювати, вони `private` там):
- `Без_ключа_межа_запису_в_чат_20_на_хвилину_а_ключ_її_змінює` (юніт): `AllowedInFirstWindow(new TableDiscussionPostRateLimitPolicy(empty), "1") == 20`, зі значенням ключа `"4"` → 4. Конфігурація з `"0"` → `Assert.Single(EcrConfigurationValidation.Validate(zero))`. Без кроку 2 (дефолт нащадка) отримаємо 100 → червоний.
- `Дії_чату_під_своїми_політиками` (рефлексія): `typeof(TableDiscussionsController).GetMethod("MentionCandidates")!.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName == SearchRateLimitPolicy.PolicyName` і `GetMethod("Post")…PolicyName == TableDiscussionPostRateLimitPolicy.PolicyName`. Без кроків 1/5 атрибута немає → червоний.
- `Запис_у_чат_понад_межу_дає_429_tooManyChatMessages_а_інший_користувач_не_обмежений` (інтеграція): `EcrApiFactory(sql).WithWebHostBuilder(b => b.UseSetting(TableDiscussionPostRateLimitPolicy.PermitKey, "3"))`. Двоє щойно створених користувачів без ролей. Три `POST /api/v1/documents/999999/tables/T1/messages` з `{ body = "x", periodKey = 202601 }` → не `429` (обробник відповідає `403`/`404` — обмежувач рахує ДО нього). Четвертий → `AssertRejectedAsync` (`429`, `application/problem+json`, `Retry-After > 0`, `errorCode == "ECR-REQ-0429"`) плюс `messageKey == "err.ECR-REQ-0429.tooManyChatMessages"`. Сусід → не `429`. Без кроку 5 четвертий — `404` → червоний.
- `Кандидати_згадок_ділять_кошик_пошуку` (інтеграція): `UseSetting("Security:RateLimit:SearchPermit", "2")`. Два `GET /api/v1/documents/999999/tables/T1/mention-candidates?q=ab` → не `429`, третій → `429` з `messageKey == "err.ECR-REQ-0429.tooManySearches"`. Без кроку 1 третій — `404` → червоний.
- Наявні тести не впираються в межу. `TableDiscussionApiTests` пише щонайбільше 10 повідомлень на користувача в одному прогоні (`:337-366`), кожен кейс має нових користувачів. `mention-candidates` — 5 викликів на файл. Перевірено `git grep -n "for (var i" -- tests/Ecr.Api.Tests/TableDiscussionApiTests*.cs`.

**Перевірка після впровадження:**
- `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~TableDiscussionRateLimitTests|FullyQualifiedName~TableDiscussionApiTests|FullyQualifiedName~VerifyValidateRateLimitTests|FullyQualifiedName~SearchRateLimitTests|FullyQualifiedName~EcrConfigurationValidationTests"` (потрібен `ECR_TEST_SQL`);
- `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~OpenApiSnapshotTests"` — знімок НЕ змінився (контракт OpenAPI той самий);
- `dotnet test tests/Ecr.Architecture.Tests` (`ConfigurationKeysTests`, `ErrorTitleCatalogTests`, `SeedSplitLayoutTests`);
- e2e: `tableChat.spec.ts`, `tableChatLiveRefresh.spec.ts` на стенді `tools/e2e-stand.ps1` (з кроком 11);
- стенд без перевизначення: 21-ше повідомлення за хвилину → сповіщення «Too many chat messages…», текст у композері лишається, через `Retry-After` повтор проходить. Звичайний набір `@ім'я` не дає 429.

**Ризики і побічні ефекти:** кошик `search` спільний для пошуку, кандидатів передач і кандидатів згадок (30 за 10 с на користувача). При дуже швидкому наборі можлива 429 у підказці згадок — клієнт тоді просто не показує кандидатів. Межа запису 20/хв може зачепити масове ручне копіювання повідомлень (немає такого сценарію в продукті) — адміністратор піднімає ключ. Лічильник у пам'яті процесу (як усі політики `UserPerMinuteRateLimitPolicy`). PATCH/DELETE повідомлень не обмежуються — поза рішенням PO; PATCH дозволений лише автору впродовж 15 хв (`TableDiscussionsController.cs:123-137`). Тестів фіксованої кількості запитів на ці маршрути немає. P-7 додасть такий на `mention-candidates`: обмежувач на дозволеному запиті до БД не ходить (`RejectAsync` — лише на відмові), тож лічильник команд не зміниться.

**Обґрунтування:** кандидати згадок — повторне використання T8, бо форма запиту та сама, числа вже обґрунтовано, і кандидати передач уже під тією політикою. Запис — окрема політика на базі `UserPerMinuteRateLimitPolicy` (D-386): та сама форма відповіді, розділ за користувачем і хвилинне вікно, різниться лише дефолт. 20/хв: PO бачив це число в питанні («(А) … наприклад 20 повідомлень/хв») і обрав (а). Воно на порядок нижче за «важкі» читання (100/хв, D-386) і поруч із наявними межами записів на користувача (`RecalculateUserPermitPerMinute` = 30, `ChangePasswordPermitPerMinute` = 10, `SmtpTestPermitPerMinute` = 5 — `appsettings.json:40-54`). Відкинуто: (а) кошик `search` для POST — інша природа (запис, не пошук) і вікно 10 с; (б) межа в `TableDiscussionCommands` з поверненням токена на `4xx` (як `DocumentRecalculateQuota`) — складніше, а відмови `4xx` у чаті дешеві; (в) `[ProducesResponseType(429)]` — зміна контракту без зміни поведінки клієнта (крок 5).

**Рішення PO:** отримано 11.11 — «(а) додати» (`00-PLAN.md` §4). Нових питань немає.
**Впевненість:** відсутність меж і порядок конвеєра — підтверджено читанням; вартість запиту кандидатів — потребує заміру на стенді (P-7).
**Залежності:** P-7 (звіт 03) — та сама точка `mention-candidates`, файли не перетинаються (див. «Порядок впровадження»). S-3 — спільний файл `docs/admin/operations-runbook.md` (різні, не суміжні рядки: `:134` і новий після `:141`). S-1/S-4/S-5/S-2 — спільний `docs/build/02-contracts.md` (різні рядки).

---

## Порядок впровадження

✎ 11.11: після рішень PO змінилися групи E (S-6) і F (S-2). Спільні файли між групами: `docs/build/02-contracts.md` (S-1 `:4937`, S-2 `:4773-4774`, S-4 `:4942`, S-5 `:4889`, S-6 `:4233`, `:4648-4649`) — різні рядки, правити по черзі або одним комітом документації; `docs/admin/operations-runbook.md` (S-3 `:134`, S-6 новий рядок після `:141`) — не суміжні блоки, але групи B і E зливати по черзі.

| Група (паралельно) | Знахідки | Файли запису |
|---|---|---|
| A | S-1 | `PreviewPermitBindingHandler.cs`, `PermitBindingPreviewTests.cs`, `02-contracts.md:4937` |
| B | S-3 | `AirLimitsController.cs`, `NaeTreeController.cs`, `PermitTableImportController.cs`, `ImportPreviewGate.cs` (коментар), новий `WorkbookParseConcurrencyTests.cs`, `operations-runbook.md:134` |
| C | S-4 | `DocumentReadScope.cs`, `GetPermitUsagesHandler.cs`, `PermitUsagesHttpTests.cs`, `02-contracts.md:4942` |
| D | S-5 | `NaeHandlers.cs`, тест у `NaeTreeHttpTests.cs`, `02-contracts.md:4889` |
| E (лінія L15, разом із P-7 звіту 03) | S-6, потім P-7 | S-6: `TableDiscussionsController.cs`, `VerifyValidateRateLimitPolicies.cs` (конструктор бази), новий `TableDiscussionPostRateLimitPolicy.cs`, `LoginRateLimiting.cs:327`, `appsettings.json`, `EcrConfigurationValidation.cs`, новий сід `features/TC-post-rate-limit.sql`, `operations-runbook.md` (новий рядок після `:141`), `02-contracts.md:4233, 4648-4649`, `tools/e2e-stand.ps1`, новий `TableDiscussionRateLimitTests.cs`. P-7: `TableDiscussionQueries.cs`, новий `MentionCandidatesQueryCountTests.cs` |
| F (лінія L16, контракт) | S-2 | `IntegrationHandlers.cs`, `JobsController.cs` (XML → перегенерувати `openapi.snapshot.json` + `schema.d.ts` останнім кроком), `JobCancelTests.cs`, `JobRestartOwnerTests.cs`, `JobRestartTargetRightTests.cs`, `RestartJobHandlerTests.cs`, `SeedTests.cs`, `02-contracts.md:4773-4774`, `admin-guide.md:122, 891-892`, `10-decisions.md` + `CHECKSUMS.txt`; клієнт — `JobFacts.tsx`, `JobsPage.tsx`, `MyTasksDrawer.tsx`, `features/jobs/api.ts` (коментар), `jobRetry.test.tsx`, `JobsPage.cancelJob.test.tsx`, `JobsPage.retryResult.test.tsx` (одним комітом із сервером: клієнт без сервера ховав би кнопки, які сервер ще дозволяє, а сервер без клієнта показував би кнопки, що дають 403) |

**S-6 і P-7 — одна точка `GET …/mention-candidates`.** Продуктових файлів спільних немає: S-6 змінює лише атрибут дії в контролері, P-7 — цикл у `TableDiscussionQueries.cs:190`. Порядок — S-6, потім P-7, однією лінією L15 (`00-PLAN.md` §3). Тест P-7 (`MentionCandidatesQueryCountTests`) робить 2 запити на користувача — менше за межу `search` (30 за 10 с). На дозволеному запиті обмежувач до БД не ходить, тож лічильник команд P-7 від S-6 не залежить. Разом вони дають сталу ціну відповіді (≤ 80 перевірок, P-7) і сталу частоту (≤ 30 запитів за 10 с на користувача, S-6).

Порядок пріоритету: S-1 → S-3 → S-2 → S-4/S-5/S-6. S-2 — єдина зі зміною контракту OpenAPI, тож іде через `lane/integ-*` (CLAUDE.md, п. 4), по черзі з L17/W-2 (`00-PLAN.md` §3). S-6 контракт OpenAPI не змінює (крок 5 S-6).

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
| S-2 (✎ PO 11.11, перевірено на `39aea41d9`) | так: `IntegrationHandlers.cs:173, 508-535, 580-618, 671-683, 702-712, 742-761, 776-781, 792, 808, 843-862`; `JobsController.cs:96-99, 116-119`; `openapi.snapshot.json:43162, 43243`; `schema.d.ts:8758, 8833, 32767, 32835`; `02-contracts.md:4764-4774`; `admin-guide.md:122, 891-892`; `10-decisions.md:851, 911, 1139`; `CHECKSUMS.txt:11`; сід `00-prelude.sql:106, 532-538, 563, 570, 576, 617, 621, 646-664`, `99-epilogue.sql:213-287 (258, 265)`; `AccessProfile.cs:43, 248`; `PermissionCheck.cs:9-13, 166-171`; `BootstrapAdmin.cs:37-38`; клієнт `JobFacts.tsx:131-145, 147-187`, `JobsPage.tsx:14, 49-54, 229-238, 438-462`, `MyTasksDrawer.tsx:240-251`, `useSession.ts:67-69`; тести `JobCancelTests.cs:68-243`, `JobRestartOwnerTests.cs:27-95`, `JobRestartTargetRightTests.cs:141-183, 245-254`, `RestartJobHandlerTests.cs:28-124`, `SeedTests.cs:523-530, 605-646, 1171` | так — сім блоків «було» звірено скриптом із поточними файлами (усі входження знайдено дослівно); однорядкові заміни XML-коментарів звірено `sed -n` | `Consistency.RunConsistencyCheckHandler.Permission` = `"System.RunJob"` (`RunConsistencyCheckHandler.cs:45`, той самий assembly, відносна назва вже є на `:681`); `MayRepeatOwnAsync`, `DangerousActionPermission`, `GetJobStatusHandler.Permission` — наявні члени; `AccessBuilder.Permission(string)` (`TestKit/AccessBuilder.cs:56`); клієнт: `can`/`useSession` (`useSession.ts:46, 67`), `JobStatus`/`JobSummary.createdByUserId` (`api/types.ts:426, 429`), `useJobStatus` → `UseQueryResult<JobStatus>` | так — матриця `JobCancelTests`: рядки `ViewHealth` + чужа/системна → без фіксу гілка `IsGranted(ViewHealth)` (`:846`) пропускає, `ThrowsAsync` червоний; `JobRestartOwnerTests`/`RestartJobHandlerTests` — гілка `:586` пропускає; клієнт «немає кнопки» — без кроку 12 кнопка за самим станом; `SeedTests` — сторож передумови (зелений до й після, сказано в тексті) | рішення PO 11.11 (`00-PLAN.md` §4); `10-decisions.md` — запису про межу cancel/restart немає (`git grep -n RunJob docs/tz/10-decisions.md` → лише `D-388`/history), `D-475` вільний (останній рядок — `D-474`, `:911`) | переписано під рішення; уточнено: роль адміністратора = право з сіду (окремої гілки немає, правило 7); виправлено діапазон «було» restart (`:580-618` замість `:586-620`); додано клієнт і правки наявних тестів, які інакше впали б (`JobCancelTests:70/93/199`, `JobRestartOwnerTests:88`, `JobRestartTargetRightTests:146/167`, `RestartJobHandlerTests:31-32`) |
| S-3 | так: `DocumentsController.cs:743-756`, `AirLimitsController.cs:26, 122-134, 152-172, 190-208`, `NaeTreeController.cs:21, 101-161`, `PermitTableImportController.cs:83-95`, `ExcelImporter.cs:1197-1203` | так (вивід `awk`) | `ImportPreviewGate.EnterAsync(CancellationToken)` повертає `RateLimitLease` (IDisposable) — `ImportPreviewGate.cs`; синглтон зареєстровано `LoginRateLimiting.cs:267`; `[FromServices]` уже використовується так само в `DocumentsController.ImportPreview` | так — `GetUninitializedObject` → поле обробника `null` → `NullReferenceException` замість `BusinessRuleException` (той самий прийом, що й `ImportPreviewConcurrencyTests.cs:93-99`) | S1-02/P1-04/AN-123 закрили лише перегляд документа (runbook `:134`); нових ендпоінтів там немає | залишено |
| S-4 | так: `GetPermitUsagesHandler.cs:76-83`, `PermitStore.cs:52-139`, `DocumentQueryHandlers.cs:591-646` | так, `:83` | новий метод використовує приватний `TableIdOf` (`DocumentReadScope.cs:184`) і публічні `InPeriod`/`CanReadAt` (`:123`, `:306`); `PermitUsageDto` — record (`with` працює); `PermitUsageKinds.Cell` існує | так — store повертає комірки схованої таблиці | контракт `02-contracts.md:4942` про межі читання мовчить; у відомих — немає | залишено, важливість low |
| S-5 | так: `NaeHandlers.cs:592-622`, `AirLimitsReferenceReader.cs:45-88`, `RegistryAccess.cs:50, 198, 272` | так, `:610-621` | `RegistryAccess.RequireAsync(…, RegistryLookup, …)` — `internal Task<AccessProfile>` (`:198`), викликається з того ж assembly; `CanAccess` — `internal` (`:272`) | так — без фіксу дозволи повертаються | у відомих — немає | залишено, важливість low |
| S-6 (✎ PO 11.11, перевірено на `39aea41d9`) | так: `TableDiscussionsController.cs:1-5, 87-121, 123-137`; `TableDiscussionQueries.cs:167-219`; `TableDiscussionCommands.cs:43-147 (82-84, 96, 111-125)`; `TableDiscussionAccess.cs:39, 165`; `SearchRateLimitPolicy.cs:20-67`; `VerifyValidateRateLimitPolicies.cs:21-104`; `LoginRateLimiting.cs:246-331, 394-460`; `Program.cs:352-365`; `appsettings.json:38-55`; `EcrConfigurationValidation.cs:58-73`; `operations-runbook.md:131-141`; `02-contracts.md:4233, 4648-4649`; `seed/features/COLL-quota-verify-validate.sql`; `Ecr.Infrastructure.csproj:48`; `SeedScript.cs:63-66`; `tools/e2e-stand.ps1:526-548`; `tableChatLiveRefresh.spec.ts:102, 329-336`; клієнт `TableChatPanel.tsx:96-106`, `ChatComposer.tsx:63, 74, 100-124`, `api.ts:260-276`, `queryClient.ts:1` | так — три блоки «було» (`TableDiscussionsController.cs:89-90`, `:106-107`; `VerifyValidateRateLimitPolicies.cs:36-42`) звірено скриптом дослівно | `SearchRateLimitPolicy.PolicyName`, `UserPerMinuteRateLimitPolicy` (abstract, `protected` ctor — додається необов'язковий параметр, нащадки `:72-104` компілюються без змін), `LoginRateLimiting.RejectAsync` (`internal`, той самий assembly), `EnableRateLimitingAttribute` (`Microsoft.AspNetCore.RateLimiting`), `options.AddPolicy<string, T>` — як `:324-327`; `IConfiguration` — неявний using Web SDK (сусідній файл без явного `using`) | так — рефлексійний кейс: без атрибутів `PolicyName` = null; інтеграційні: без кроку 5 четвертий POST — `404`, без кроку 1 третій GET — `404`; юніт: без кроку 2 дефолт — 100, не 20 | PO 11.11 «(а) додати»; у `KNOWN-ISSUES`/`OPEN-ITEMS`/`10-decisions.md` межі чату немає (`git grep -n mention-candidates\|DiscussionPost`); перевірено, що наявні тести/e2e не перевищать межі, крім `tableChatLiveRefresh.spec.ts` (25 POST поспіль) — для нього крок 11 | переписано під рішення; додано POST (нова політика), сід, конфіг, runbook, контракт, перевизначення e2e-стенда; XML-описи навмисно не змінюються (контракт OpenAPI той самий) |

Вилучено під час самоперевірки: (1) кандидат «будь-який носій `ManageRoles` додає небезпечні права власній ролі» — `D-121`/`D-470`, перенесено у «Відкинуто»; (2) кандидат «`423` без пароля» — ФВ-6.4a, «Відкинуто»; (3) кандидат «видалення копії аркуша з даними закритих періодів» — не авторизація, передано аудиту 04; (4) кандидат «верхньої межі `openUntil` немає» — питання продукту, передано аудиту 04.
