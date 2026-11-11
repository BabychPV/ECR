# 05 — Клієнт (React): correctness / performance / security

**Область:** `src/Ecr.Web/src` (React 19, TanStack Query 5, react-router 7), з перевіркою серверного боку там, де дефект клієнта залежить від контракту (`src/Ecr.Application`, `src/Ecr.Adapters.Excel`, `src/Ecr.Api`).
**SHA `main`:** `f446e9b29` · **Дата:** 2026-11-11 · **Режим:** лише читання (без збирання/тестів).

**Охоплено повністю:** `api/client.ts` (401/`from`, CSRF/Origin, `X-Ecr-User`, `Retry-After`, `isRetryable`), `app/queryClient.ts`, `shared/ui/useUrlState.ts`, `shared/safeReturnPath.ts`, `features/import/*` (синхронний і фоновий Apply), `features/documents/sheetCopies/*`, `features/permits/history/*`, `features/grid/sliceCache.ts`, `features/grid/useAddRow.ts`/`useDeleteRow.ts`, `followRecalculation`/`settleRecalculation` (`useCellPatch.ts`), `applyPatchLocally` (`cellPatchRequest.ts`), `features/grid/liveRefresh/*`, `shared/ui/usePollWhileVisible.ts`, `pages/DocumentsPage.tsx` (пагінація, ключі, фільтри), `features/documents/DocumentHeaderPanel.tsx` (збереження), `features/tableChat/ChatComposer.tsx`/`api.ts`, `features/grid/lostEdits.ts` (сховище), `shared/format/dateOnly.ts`.
**Поверхово:** `features/grid/DocumentGrid.tsx` (слайс-запит, undo-стек, add-row), `useCellStyles.ts`, `SheetActions.tsx`, `VersionMigrationDialog.tsx`, `DocumentInspector.tsx`, `pages/admin/PeriodsPage.tsx` (пояс майданчика), `features/templates/ctor/BulkGroupDialog.tsx`, `PresentationEditor.tsx`, `features/permits/typeEditor/*` (URL-діалоги), `features/permits/lists/ListDrawer.tsx`, `features/registries/tree/*`, `features/editAssignments/*`, `features/export/ExportButton.tsx`.
**Не охоплено:** вставка/клавіатура/віртуалізація сітки (`clipboard.ts`, `bodyPaste.ts`, `keyCommitGate.ts`, `autosave.ts` повністю), `pages/admin/*` поза названими, `features/methodologies`, `features/integration`, e2e-специ, фактичні розміри чанків (потрібна збірка — бюджет 250 KB перевіряється лише міркуванням про імпорти).

## Зведена таблиця

| ID | Важливість | Категорія | Назва | Зусилля | Ризик зміни | Рішення PO |
|---|---|---|---|---|---|---|
| W-1 | Висока | correctness | Синхронний імпорт Excel: перерахунок формул не відстежується — обчислені колонки на екрані старі до 5 хв | S | низький | ні |
| W-2 | Середня | correctness | Збереження шапки документа ставить повний перерахунок усіх періодів, але клієнт про нього не знає — `HDR.*`-формули на екрані старі | M | середній | ні |
| W-3 | Середня | correctness | «Load more» у переліку документів ЗАМІНЮЄ сторінку замість дописати; першу сторінку повернути нічим | M | середній | так |
| W-4 | Середня | data-integrity | «Add copy» аркуша: подвійний клік у перші ~100 мс створює ДВІ копії (по всіх періодах) | S | низький | ні |
| W-5 | Середня | performance | Зміна дозволу (додати/Undo/поділ) перечитує КОЖЕН змонтований зріз, включно з прихованими (обхід `CL-02`/`C1-01`) | S | низький | ні |
| W-6 | Низька | correctness | `401` веде на вхід із `from` без рядка запиту — після входу губляться `periodKey`/`sheet`/фільтри | S | низький | ні |
| W-7 | Низька | correctness | «Сьогодні» за UTC у `BindingPreview` і `naeNodes` — у поясі +05 до 05:00 це вчорашній день | S | низький | ні |

---

## Знахідки

### W-1 — Висока — Синхронний імпорт Excel: перерахунок формул не відстежується
**Категорія:** correctness
**Де:**
- `src/Ecr.Adapters.Excel/ExcelImporter.cs:506-522` (постановка задачі формул книги — id задачі відкидається), `:700` (відповідь без `RecalculationJobId`);
- `src/Ecr.Web/src/features/import/ImportPanel.tsx:140-166` (гілка `200`: інвалідація зрізів ОДРАЗУ, без стеження за задачею);
- `src/Ecr.Application/Documents/Dto/PatchCellsResponse.cs:33-36` (remark, що відповіді таблиць книги id не несуть).

**Проблема:** `ExcelImporter.ApplyAsync` у тій самій транзакції (або після коміту) ставить ОДНУ задачу формул на книгу:
```csharp
await jobs
    .EnqueueCoalescedAsync<IFormulaRecalculationJob>(
        FormulaRecalculationTarget.Of(documentId, plan.PeriodKey, createdByUserId: null), ...
```
але повертає `return new PatchCellsResponse(applied, versions, validation);` — поле `RecalculationJobId` (воно вже є в контракті, `PatchCellsResponse.cs:44`, `schema.d.ts` `recalculationJobId?: null | string`) лишається `null`. Клієнт у гілці синхронного Apply робить `await invalidateSlices(queryClient, { documentId, periodKey });` ДО того, як задача формул щось запише, і більше зрізів не чіпає. `staleTime` зрізів — 5 хв і `refetchOnWindowFocus: false` (`sliceCache.ts:48-57`), тож обчислені колонки показують значення ДО імпорту. Для правки комірки той самий дефект уже закрито (`D-1`/`CL-01`/`N-1`: `followRecalculation`, `useCellPatch.ts`), для імпорту — ні. Коментар `ImportPanel.tsx:171` навіть називає `recalculationJobId`, але код його не читає.

**Сценарій відтворення:** документ з формульною колонкою (напр. сума `A+B`); «Import» книги, що міняє `A` у < 2000 комірках (синхронна гілка, `LargeImportThreshold`) → «Imported» → колонка суми показує старе значення; через ~1–3 с задача формул записала нове, але екран не оновлюється до F9/перезавантаження/5 хв. Очікувано (як після правки комірки, `DocumentGrid.tsx:597-616`): після завершення задачі зрізи документа за період перечитуються.

**Вплив:** кожен синхронний імпорт (основний спосіб масового введення) у документах із формулами; оператор бачить неправдиві підсумки й може звіряти/подавати з ними. Додатково: задача формул піднімає версії рядків, яких кеш не бачив, — наступна власна правка такого рядка може дістати `409` на «чужій» зміні, автор якої — система (потребує перевірки на стенді: чи сервер звіряє версію рядка, змінену лише формулою).

**Зміна — покроково:**
1. `src/Ecr.Adapters.Excel/ExcelImporter.cs` — запам'ятати id задачі книги й повернути його.
   ```diff
   -        async Task EnqueueRecalculationAsync(CancellationToken token)
   -        {
   -            if (seeds.Count > 0)
   -            {
   -                // O1: злиття за документо-періодом (системна постановка — ціль без автора).
   -                await jobs
   -                    .EnqueueCoalescedAsync<IFormulaRecalculationJob>(
   +        // W-1: id задачі формул КНИГИ — у відповідь (`PatchCellsResponse.RecalculationJobId`): без нього клієнт
   +        // перечитував зрізи до перерахунку й показував обчислені колонки старими (той самий клас, що `D-1`/`CL-01`).
   +        string? recalculationJobId = null;
   +
   +        async Task EnqueueRecalculationAsync(CancellationToken token)
   +        {
   +            if (seeds.Count > 0)
   +            {
   +                // O1: злиття за документо-періодом (системна постановка — ціль без автора).
   +                recalculationJobId = await jobs
   +                    .EnqueueCoalescedAsync<IFormulaRecalculationJob>(
   ```
   і `:700`:
   ```diff
   -        return new PatchCellsResponse(applied, versions, validation);
   +        return new PatchCellsResponse(applied, versions, validation, recalculationJobId);
   ```
   (Задачі власників `ownerRecalcs`, `:525-539`, — інший період власника; клієнт стежить за періодом документа, тож їх не повертаємо. Кінець задачі книги вже скидає зрізи всіх `writtenPeriods` — `settleRecalculation`.)
2. `src/Ecr.Application/Documents/Dto/PatchCellsResponse.cs:33-36` — уточнити remark (без зміни контракту):
   ```diff
   -/// книги (імпорт, `DAT-05`) ідентифікатора не несуть — одну задачу на всю книгу
   -/// ставить <c>ExcelImporter</c> (у транзакції книги або після її коміту,
   -/// MI-02 (в)), і назвати її від імені окремої таблиці означало б збрехати.
   +/// книги (імпорт, `DAT-05`) ідентифікатора не несуть — одну задачу на всю книгу
   +/// ставить <c>ExcelImporter</c> (у транзакції книги або після її коміту,
   +/// MI-02 (в)), і назвати її від імені окремої таблиці означало б збрехати.
   +/// ✎ W-1: ЗВЕДЕНА відповідь книги (<c>ExcelImporter.ApplyAsync</c>) несе саме цю задачу.
   ```
3. `src/Ecr.Web/src/features/import/ImportPanel.tsx` — імпорт і стеження:
   ```diff
    import { invalidateSlices } from '@/features/grid/sliceCache';
   +import { followRecalculation } from '@/features/grid/useCellPatch';
    import { followImportJob } from './followImportJob';
   ```
   ```diff
          await invalidateSlices(queryClient, { documentId, periodKey });
          await queryClient.invalidateQueries({ queryKey: ['document', documentId, periodKey] });
   +
   +      // ⛔ W-1: книга поставила задачу формул — обчислені колонки перечитуються, коли вона ЗАВЕРШИТЬСЯ
   +      // (той самий слідкувач поза компонентом, що й після правки комірки; панель може вже розмонтуватися).
   +      const recalcJobId = result.recalculationJobId;
   +      if (typeof recalcJobId === 'string' && recalcJobId.length > 0) {
   +        followRecalculation(queryClient, recalcJobId, documentId, periodKey);
   +      }
   ```
   (`result` тут уже звужений до `PatchCellsResponse` через `isQueued`, `ImportPanel.tsx:657`.) `ImportPanel` — лінивий чанк (`DocumentActionBar.tsx:20`), `useCellPatch` вже в чанку сітки — бюджет маршруту `DocumentPage` не зачіпається.

**Тест-доказ:**
- `tests/Ecr.Adapters.Tests/Excel/ExcelImporterAtomicApplyTests.cs` — новий `[Fact]` `W1_Зведена_відповідь_книги_несе_задачу_формул`: у конструкторі вже є `_jobs.When(j => j.EnqueueCoalescedAsync<IFormulaRecalculationJob>(...))`; у тесті додати `_jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>()).Returns("formula-job-1");`, викликати `var response = await Importer().ApplyAsync(DocumentId, Token, CancellationToken.None);` і `Assert.Equal("formula-job-1", response.RecalculationJobId);`. Без кроку 1 — `null`, червоний.
- `src/Ecr.Web/src/features/import/__tests__/ImportPanelHints.test.tsx` — на початок файлу `vi.mock('@/features/grid/useCellPatch', async (importOriginal) => ({ ...(await importOriginal<typeof import('@/features/grid/useCellPatch')>()), followRecalculation: vi.fn() }));`; у `mockServer` додати третій параметр `recalcJobId: string | null = null` і повертати його в `recalculationJobId` гілки `/import/apply`. Новий кейс `'W-1: Apply із задачею формул — стеження за нею (documentId/periodKey панелі)'`: `mockServer(oneChange, undefined, 'job-7')`, `await openPreview()`, клік «⟦import.apply⟧», `await vi.waitFor(() => expect(vi.mocked(followRecalculation)).toHaveBeenCalledWith(expect.anything(), 'job-7', 7, 202609))`. Другий кейс — `recalcJobId = null` → `followRecalculation` не викликано. Без кроку 3 перший кейс червоний.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Adapters.Tests --filter "FullyQualifiedName~ExcelImporterAtomicApplyTests"`; `npx vitest run src/features/import`; `npx tsc --noEmit`; `npm run lint`. Стенд: імпорт книги, що міняє вхід формульної колонки, → через кілька секунд обчислена колонка оновлюється без F9; у Network — `GET /api/v1/jobs/{id}` до завершення і рівно одне перечитування зрізів після.
**Ризики і побічні ефекти:** +1 опитування `GET /jobs/{id}` раз на 1–2 с до кінця задачі (той самий `followRecalculation`, один слідкувач на документ×період, видимість вкладки враховано). Контракт не змінюється (поле вже є). Фонова гілка (`202`, `ExcelImportJob`) лишається без стеження за задачею формул — окреме питання (id задачі формул там не доходить до `JobStatus`).
**Обґрунтування:** повторне використання наявного механізму `followRecalculation` (`N-1`), а не таймер чи друга інвалідація «через N секунд»: точний момент, один запит. Альтернатива «повертати id у кожній відповіді таблиці» відкинута тим самим аргументом, що в remark (`PatchCellsResponse.cs:33-36`).
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням (сервер і клієнт); `409`-наслідок — потребує перевірки на стенді.
**Залежності:** немає.

---

### W-2 — Середня — Збереження шапки: повний перерахунок не відстежується клієнтом
**Категорія:** correctness
**Де:**
- `src/Ecr.Application/Documents/DocumentHeaderHandlers.cs:357`, `:389-403` (постановка й відповідь), `:805-836` (`EnqueueRecalculationAsync`, id задач відкидаються);
- `src/Ecr.Application/Documents/Dto/DocumentHeaderDto.cs:47` (DTO без задач);
- `src/Ecr.Web/src/features/documents/DocumentHeaderPanel.tsx:620-627` (`onSuccess` лише кладе DTO в кеш).

**Проблема:** `PATCH /documents/{id}/header` при `changed > 0` ставить `IRecalculationJob` на КОЖЕН період, куди перерахунок має право писати (`EnqueueRecalculationAsync`, `HDR.*` читають формули шаблону й методології), але повертає лише `HeaderVersion.ToDto(...)` — `DocumentHeaderDto(Fields, Version)`. Клієнт:
```ts
onSuccess: (result) => {
  queryClient.setQueryData(['document-header', documentId], result);
  adopt(result);
  showDone(t('document.header.saved'));
},
```
— жодної інвалідації зрізів, жодного стеження. Зрізи відкритого аркуша (`staleTime` 5 хв) показують значення, обчислені зі СТАРОЇ шапки. На клієнті немає жодного іншого спостерігача перерахунку документа (`git grep` по `pages/DocumentPage.tsx`, `features/documents`, `features/recalc` — порожньо).

**Сценарій відтворення:** формула колонки посилається на `HDR.Area`; змінити поле шапки «Area» → «Saved» → колонка показує результат для старого значення, поки людина не натисне F9/не перезавантажить сторінку/не мине 5 хв. Чи покаже банер «Updated by …» (`useSheetChanges`, 30 с) ці зміни як чужі — потребує перевірки на стенді (`GetSheetChangesHandler.cs:96-109` атрибутує рядки користувачу).

**Вплив:** кожна правка шапки документа з `HDR.*`-формулами; неправдиві обчислені значення на екрані одразу після збереження.

**Зміна — покроково:**
1. `src/Ecr.Application/Documents/Dto/DocumentHeaderDto.cs:47`:
   ```diff
   -public sealed record DocumentHeaderDto(IReadOnlyList<DocumentHeaderFieldDto> Fields, string Version);
   +public sealed record DocumentHeaderDto(IReadOnlyList<DocumentHeaderFieldDto> Fields, string Version)
   +{
   +    /// <summary>
   +    /// ✎ W-2: задачі повного перерахунку, поставлені ЦИМ <c>PATCH</c> (по одній на період); <c>null</c> у <c>GET</c>.
   +    /// Клієнт стежить за ними й перечитує зрізи, щойно задача завершиться (як після правки комірки, <c>N-1</c>).
   +    /// </summary>
   +    public IReadOnlyList<HeaderRecalculationJobDto>? RecalculationJobs { get; init; }
   +}
   +
   +/// <summary>Задача перерахунку документа за період, поставлена правкою шапки (W-2).</summary>
   +/// <param name="PeriodKey">Період <c>yyyyMM</c>.</param>
   +/// <param name="JobId">Ідентифікатор задачі (<c>GET /api/v1/jobs/{id}</c>).</param>
   +public sealed record HeaderRecalculationJobDto(int PeriodKey, string JobId);
   ```
2. `src/Ecr.Application/Documents/DocumentHeaderHandlers.cs`:
   - `:357`:
     ```diff
              var changed = 0;
     +        IReadOnlyList<HeaderRecalculationJobDto> recalculationJobs = [];
     ```
   - `:389-403`:
     ```diff
                  if (enlist && changed > 0)
                  {
     -                await EnqueueRecalculationAsync(documentId, project, userId, mayPreempt, innerCt).ConfigureAwait(false);
     +                recalculationJobs = await EnqueueRecalculationAsync(documentId, project, userId, mayPreempt, innerCt).ConfigureAwait(false);
                  }
              }, ct).ConfigureAwait(false);

              // ⚠ Quartz: після коміту й поза транзакцією — задача, поставлена до коміту, під RCSI прочитала б стару шапку.
              if (!enlist && changed > 0)
              {
     -            await EnqueueRecalculationAsync(documentId, project, userId, mayPreempt, ct).ConfigureAwait(false);
     +            recalculationJobs = await EnqueueRecalculationAsync(documentId, project, userId, mayPreempt, ct).ConfigureAwait(false);
              }

              var values = await headers.GetValuesAsync(documentId, ct).ConfigureAwait(false);
              return HeaderVersion.ToDto(
     -            snapshot, values, registryDefId => Registries.RegistryAccess.IsDeniedIn(profile, registryDefId, document.ProjectId));
     +            snapshot, values, registryDefId => Registries.RegistryAccess.IsDeniedIn(profile, registryDefId, document.ProjectId))
     +            with { RecalculationJobs = recalculationJobs };
     ```
     (⚠ Якщо транзакцію повторює стратегія виконання, лямбда виконається ще раз і перепише `recalculationJobs` — це правильно: лишаються задачі останньої, закомітченої спроби.)
   - `:805-836`:
     ```diff
     -    private async Task EnqueueRecalculationAsync(
     +    private async Task<IReadOnlyList<HeaderRecalculationJobDto>> EnqueueRecalculationAsync(
              long documentId, Domain.Entities.Documents.Project project, int userId, bool mayPreempt, CancellationToken ct)
          {
     +        var queued = new List<HeaderRecalculationJobDto>();
              foreach (var period in project.Periods.OrderBy(p => p.PeriodKeyValue))
              {
     ```
     ```diff
                  if (mayPreempt)
                  {
     -                await jobs.EnqueueExclusiveAsync<IRecalculationJob>(target, payload, ct, userId).ConfigureAwait(false);
     +                queued.Add(new(period.PeriodKeyValue,
     +                    await jobs.EnqueueExclusiveAsync<IRecalculationJob>(target, payload, ct, userId).ConfigureAwait(false)));
                  }
                  else
                  {
     -                await jobs.EnqueueCoalescedAsync<IRecalculationJob>(target, payload, ct, userId).ConfigureAwait(false);
     +                queued.Add(new(period.PeriodKeyValue,
     +                    await jobs.EnqueueCoalescedAsync<IRecalculationJob>(target, payload, ct, userId).ConfigureAwait(false)));
                  }
              }
     +
     +        return queued;
          }
     ```
     (`EnqueueExclusiveAsync`/`EnqueueCoalescedAsync` повертають `Task<string>` — `src/Ecr.Application/Ports/IBackgroundJobScheduler.cs:68`, `:109`.)
3. Контракт: перегенерувати `contracts/openapi.snapshot.json` (`ECR_UPDATE_SNAPSHOT=1` прогоном `Ecr.Api.Tests`) і `src/Ecr.Web/src/api/schema.d.ts` (`npm run api:types`); у `docs/build/02-contracts.md` біля рядка `:4610` (`PATCH /api/v1/documents/{id}/header`) дописати: «відповідь — `DocumentHeaderDto` + `recalculationJobs[] {periodKey, jobId}` поставлених перерахунків (W-2); у `GET` поле `null`».
4. `src/Ecr.Web/src/features/documents/DocumentHeaderPanel.tsx`:
   ```diff
    import { coerce } from '@/features/grid/edits';
   +import { followRecalculation } from '@/features/grid/useCellPatch';
   ```
   ```diff
        onSuccess: (result) => {
          queryClient.setQueryData(['document-header', documentId], result);
          adopt(result);
          showDone(t('document.header.saved'));
   +      // ⛔ W-2: шапку читають формули (`HDR.*`) — сервер поставив повний перерахунок кожного відкритого періоду.
   +      // Зрізи перечитуються, коли задача ЗАВЕРШИТЬСЯ (слідкувач поза компонентом, один на документ×період).
   +      for (const job of result.recalculationJobs ?? []) {
   +        followRecalculation(queryClient, job.jobId, documentId, job.periodKey);
   +      }
        },
   ```
   `DocumentHeaderPanel` — лінивий чанк (`DocumentPage.tsx:108`), бюджет маршруту не зачіпається.

**Тест-доказ:**
- `tests/Ecr.Application.Tests/Documents/DocumentHeaderHandlersTests.cs` — новий `[Fact]` `W2_PATCH_повертає_задачі_перерахунку_по_періодах`: та сама підготовка, що в `PATCH_ставить_повний_перерахунок_на_кожен_відкритий_період_і_лише_на_них` (рядки 905-919: профіль із `Calculation.Recalculate`, `AddPeriod(202601 Closed / 202602 Grace / 202603 Open / 202604 Scheduled)`, `VersionOfAsync`, `Values`); `_jobs.EnqueueExclusiveAsync<IRecalculationJob>(RecalculateDocumentHandler.TargetOf(DocumentId, new PeriodKey(202602)), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>()).Returns("job-02")` і так само `202603` → `"job-03"`; `var dto = await Patch().HandleAsync(DocumentId, Request(version, new PatchHeaderField("Area", "Tengiz", false)), CancellationToken.None);` → `Assert.Equal([new HeaderRecalculationJobDto(202602, "job-02"), new HeaderRecalculationJobDto(202603, "job-03")], dto.RecalculationJobs);`. Без кроку 2 — поле `null`, червоний.
- `src/Ecr.Web/src/features/documents/__tests__/DocumentHeaderPanel.test.tsx` — `vi.mock('@/features/grid/useCellPatch', async (o) => ({ ...(await o<typeof import('@/features/grid/useCellPatch')>()), followRecalculation: vi.fn() }))`; кейс за зразком `'надсилає лише ЗМІНЕНІ поля'` (рядок 358), але `show({ fields: [...], patchResponse: { status: 200, body: { fields: [...], version: SavedVersion, recalculationJobs: [{ periodKey: 202603, jobId: 'job-03' }] } } })`; після кліку «⟦common.save⟧» — `await waitFor(() => expect(vi.mocked(followRecalculation)).toHaveBeenCalledWith(expect.anything(), 'job-03', DocumentId, 202603))`. Без кроку 4 — червоний.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~DocumentHeaderHandlersTests"`; `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~OpenApi"` (снепшот); Architecture-тести (`tests/Ecr.Architecture.Tests` або відповідний проєкт — пам'ятка інтегратора); `npx vitest run src/features/documents/__tests__/DocumentHeaderPanel.test.tsx`; `npx tsc --noEmit`. Стенд: змінити поле шапки, на яке посилається формула, — колонка оновлюється за кілька секунд без F9.
**Ризики і побічні ефекти:** `GET …/header` тепер серіалізує `"recalculationJobs": null` (у `Ecr.Api` немає `DefaultIgnoreCondition`) — тести, що порівнюють тіло `GET` дослівно, треба оновити; клієнт поле в `GET` ігнорує. До N опитувань `GET /jobs/{id}` паралельно (N = відкриті періоди, зазвичай 1–2; слідкувач один на документ×період).
**Обґрунтування:** окремий тип відповіді `PatchDocumentHeaderResponse` чистіший, але ламає клієнтський тип `DocumentHeaderDto` і `setQueryData(['document-header', …])`; `init`-властивість — мінімальна сумісна зміна. Клієнтський таймер «перечитати через N с» відкинуто: невідомий час задачі, зайві `GET` зрізів (найважчий запит системи).
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням; поведінка банера `useSheetChanges` — потребує перевірки на стенді.
**Залежності:** немає (з W-1 спільних файлів немає).

---

### W-3 — Середня — «Load more» у переліку документів замінює сторінку
**Категорія:** correctness
**Де:** `src/Ecr.Web/src/pages/DocumentsPage.tsx:118` (курсор з адреси), `:151-182` (`useQuery` з `cursor` у ключі), `:446`, `:483-486` (`rows = page.items`), `:674-678` (кнопка), `:392`, `:395`, `:398`, `:697-698` (читання `query.data`).
**Проблема:** кнопка з текстом `documents.more` = «Load more» / «Загрузить ещё» / «Тағы жүктеу» (`CATALOG-en-base.sql:1098`, `00-prelude.sql:2186`, `:5202`) робить `setUrlParams({ cursor: page.nextCursor })`; `cursor` — частина ключа запиту, тож сторінка 2 — ІНШИЙ запис кешу, і таблиця показує лише її: перші 50 документів зникають. Шляху назад немає: `useUrlState` пише адресу з `replace: true` (`useUrlState.ts:56`), «Назад» браузера йде зі сторінки. Сусідній `SecurityPage` для того самого рядка «Load more» використовує `useInfiniteQuery` і дописує (`SecurityPage.usersPaging.test.tsx:84-98`: «решту можна довантажити»).
**Сценарій відтворення:** рік із > 1 сторінкою документів → «Load more» → у таблиці лише документи другої сторінки; лічильник «застарілих на сторінці» і варіанти фільтра версій теж рахуються лише з неї. Очікувано: перелік доповнюється (текст кнопки; поведінка `SecurityPage`).
**Вплив:** усі користувачі переліку документів у проєктах із великою кількістю документів; втрата контексту, хибне враження «документів стало менше».
**Зміна — покроково:**
1. `src/Ecr.Web/src/pages/DocumentsPage.tsx`:
   ```diff
   -import { lazy, Suspense, useRef, useState, type JSX } from 'react';
   +import { lazy, Suspense, useMemo, useRef, useState, type JSX } from 'react';
   ```
   ```diff
   -import { useQuery } from '@tanstack/react-query';
   +import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
   ```
   ```diff
   -  const [cursor] = useUrlState('cursor');
   ```
   ```diff
   -  const query = useQuery({
   +  /*
   +   * ⛔ W-3: «Load more» ДОПИСУЄ сторінку, а не замінює: курсор — параметр сторінки `useInfiniteQuery`, а не адреси.
   +   * Доти `?cursor=` у ключі давав ІНШИЙ запис кешу — перші 50 рядків зникали, а `replace: true` не лишав «Назад».
   +   * Старі адреси з `?cursor=` відкривають першу сторінку (сеттери фільтрів і далі чистять `cursor: null`).
   +   */
   +  const query = useInfiniteQuery({
        queryKey: [
          'documents',
          null,
          year,
   -      cursor,
          filters.state,
   ```
   ```diff
   -    queryFn: () =>
   +    queryFn: ({ pageParam }) =>
          listDocuments({
            periodKey: null,
            year,
   -        cursor,
   +        cursor: pageParam,
            state: filters.state,
   ```
   і після закриваючої дужки об'єкта `listDocuments({...})`:
   ```diff
            projectId: filters.projectId,
          }),
   +    initialPageParam: null as string | null,
   +    getNextPageParam: (last) => last.nextCursor ?? undefined,
      });
   +
   +  // Усі довантажені сторінки як одна: `nextCursor`/`totalCount` — останньої.
   +  const listPage = useMemo<DocumentListPage | undefined>(() => {
   +    const pages = query.data?.pages;
   +    const last = pages?.at(-1);
   +    return pages === undefined || last === undefined ? undefined : { ...last, items: pages.flatMap((p) => p.items) };
   +  }, [query.data]);
   ```
   Замінити `query.data` на `listPage` у рядках `:392`, `:395`, `:398`, `:446` (`data={listPage}`), `:697`, `:698` (`listReady={listPage !== undefined}`). Кнопка `:675`:
   ```diff
   -              <Button mt="md" variant="default" onClick={() => setUrlParams({ cursor: page.nextCursor })}>
   +              <Button
   +                mt="md"
   +                variant="default"
   +                loading={query.isFetchingNextPage}
   +                disabled={query.isFetchingNextPage}
   +                onClick={() => void query.fetchNextPage()}
   +              >
   ```
   (`disabled` обов'язковий: `fetchNextPage` за замовчуванням `cancelRefetch: true` — повторний клік перезапускав би запит.) `query.isPending`/`query.error`/`query.refetch` у `:444`, `:445`, `:482` працюють без змін. Тип `DocumentListPage` уже імпортовано (`:17`).
**Тест-доказ:** `src/Ecr.Web/src/features/documents/__tests__/DocumentsPage.filters.test.tsx` — до `ListReply` додати `'paged'`; у `mockFetch` гілка `/api/v1/documents`: для `'paged'` без `cursor=` повертати `{ items: [Late], nextCursor: 'c2', totalCount: 2 }`, з `cursor=c2` — `{ items: [OnTime], nextCursor: null, totalCount: 2 }`. Кейс `'W-3: Load more дописує другу сторінку, перша лишається'`: `mockFetch('paged'); show('/?year=2026');` → `await screen.findByText('LATE-0001')` → клік `⟦documents.more⟧` → `await screen.findByText('ONTIME-0002')` і `expect(screen.getByText('LATE-0001')).toBeTruthy()`; `expect(lastListed().get('cursor')).toBe('c2')`. Без фіксу `LATE-0001` зникає — червоний. Наявні кейси з `?cursor=abc` (`:187`, `:532`, `DocumentsPage.periodFilter.test.tsx:121`, `DocumentsPage.versionColumn.test.tsx:214`) лишаються зеленими: перший запит іде без курсора, а сеттери й далі пишуть `cursor: null`.
**Перевірка після впровадження:** `npx vitest run src/features/documents/__tests__/DocumentsPage.filters.test.tsx src/pages/__tests__/DocumentsPage*.test.tsx`; `npx tsc --noEmit`; `npm run lint`. Стенд: рік із > 50 документів → «Load more» → рядків стало більше; зміна фільтра → знову перша сторінка.
**Ризики і побічні ефекти:** `invalidateQueries({ queryKey: ['documents'] })` після мутацій тепер перечитує ВСІ довантажені сторінки послідовно (N запитів замість одного) — прийнятно (людина сама їх довантажила). Посилання «на другу сторінку» більше не відтворюється — свідомо (курсор — не стан, яким діляться).
**Обґрунтування:** `useInfiniteQuery` — штатний механізм, уже вжитий у `SecurityPage`, `CollectionRunsPanel`, `useApprovalQueue`. Альтернатива — перейменувати кнопку на «Next page» і додати «Previous» (курсор назад сервер не дає) — гірша.
**Рішення PO:** так — «Load more» має ДОПИСУВАТИ (варіант A, цей фікс) чи це навмисна «наступна сторінка» (варіант B: тоді змінити текст `documents.more` на «Next page» і додати «First page»)? Той самий шаблон «заміни» також у `pages/admin/AuditPage.tsx:456-458`, `pages/admin/ConsistencyIssuesPage.tsx:311`, `features/audit/SecurityEventsPanel.tsx:143`, `StructureChangesPanel.tsx:152`, `WorkflowEventsPanel.tsx:194` — рішення PO варто застосувати до всіх (окремими змінами після W-3).
**Впевненість:** підтверджено читанням.
**Залежності:** немає.

---

### W-4 — Середня — «Add copy» аркуша: подвійний клік створює дві копії
**Категорія:** data-integrity
**Де:** `src/Ecr.Web/src/features/documents/sheetCopies/SheetCopyBar.tsx:110-120`; сервер `src/Ecr.Api/Controllers/DocumentsController.cs:538-546` (`POST …/copies`, неідемпотентний, копія «у КОЖНОМУ періоді»).
**Проблема:**
```tsx
disabled={!canAdd}
loading={addLoading}
onClick={() => {
  remove.reset();
  add.mutate();
}}
```
`addLoading = usePendingLoading(add.isPending)` вмикає `loading` (а з ним і блокування Mantine) лише через `DurationThresholdsMs.inline` = 100 мс (`useDurationIndicator.ts:25`); контракт хука прямо вимагає від споживача `if (x.isPending) return` (`usePendingLoading.ts:17-18`) — тут його немає (порівн. сітка: `DocumentGrid.tsx:3070`). До того ж `isPending` оновлюється через планувальник TanStack, тож навіть така перевірка не ловить два кліки до перерендеру.
**Сценарій відтворення:** швидкий подвійний клік «Add copy» → два `POST /documents/{id}/sheets/{sheetDefId}/copies` → дві нові копії («Boiler 3», «Boiler 4»), кожна з таблицями в усіх періодах. Очікувано: одна копія на одну дію.
**Вплив:** користувачі з правом `Document.Create`, що звикли до подвійного кліку; зайва копія важка (екземпляри таблиць у кожному періоді), прибирається лише видаленням із підтвердженням; чат/дані можуть встигнути потрапити не в ту копію.
**Зміна — покроково:**
1. `SheetCopyBar.tsx`:
   ```diff
   -import { useState, type JSX } from 'react';
   +import { useRef, useState, type JSX } from 'react';
   ```
   ```diff
      const [nameDraft, setNameDraft] = useState<LocalizedValue>({});
   +  // ⛔ W-4: «Add copy» — неідемпотентний POST (копія в КОЖНОМУ періоді). `loading` блокує кнопку лише після 100 мс
   +  // (`usePendingLoading`), а `isPending` оновлюється після перерендеру: подвійний клік давав дві копії. Ref — синхронно.
   +  const adding = useRef(false);
   ```
   (вставити після `const [nameDraft, setNameDraft] = useState<LocalizedValue>({});`, рядок 49)
   ```diff
              onClick={() => {
   +            if (adding.current) return;
   +            adding.current = true;
                remove.reset();
   -            add.mutate();
   +            add.mutate(undefined, {
   +              onSettled: () => {
   +                adding.current = false;
   +              },
   +            });
              }}
   ```
**Тест-доказ:** `src/Ecr.Web/src/features/documents/sheetCopies/__tests__/SheetCopyBar.test.tsx`, `describe('SheetCopyBar: додати копію')`, новий кейс `'W-4: подвійний клік до відповіді — рівно один POST'`: `mockServer({ 'POST /api/v1/documents/42/sheets/10/copies': { status: 201, body: { sheetCopyId: 78, ordinal: 3 } } })`; `const { onSelect } = show(Original)`; `const button = screen.getByTestId('sheet-copy-add'); fireEvent.click(button); fireEvent.click(button);` → `await waitFor(() => expect(onSelect).toHaveBeenCalledWith('GEN~78'))`; `expect(sent.filter((r) => r.method === 'POST')).toHaveLength(1)`. Без фіксу — 2 POST. Наявний кейс `'відмова сервера — банер …'` (рядок 138) лишається зеленим (ref знімається в `onSettled`).
**Перевірка після впровадження:** `npx vitest run src/features/documents/sheetCopies`; `npm run lint`. Стенд: подвійний клік «Add copy» → одна нова вкладка.
**Ризики і побічні ефекти:** якщо компонент розмонтується до відповіді, `onSettled` рівня виклику не спрацює — ref зникає разом із компонентом, наслідків немає.
**Обґрунтування:** `disabled={add.isPending}` не допомагає до перерендеру й додає сірий «блим» на швидкій дії (те, від чого стереже `ФВ-14.26`). Серверна ідемпотентність (ключ запиту) — дорожча й ширша зміна.
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням.
**Залежності:** немає.

---

### W-5 — Середня — Зміна дозволу перечитує всі змонтовані зрізи, включно з прихованими
**Категорія:** performance
**Де:** `src/Ecr.Web/src/features/permits/history/usePermitHistory.ts:39-46` (`refreshAfterPermitChange`), `:49-105` (хуки); виклики `AddRowPermitForm.tsx:71`, `HeaderPermitSection.tsx:36`, `RowPermitSection.tsx:67`; тест `__tests__/refreshAfterPermitChange.test.ts:16`.
**Проблема:**
```ts
queryClient.invalidateQueries({ queryKey: queryKeys.slices.all() }),
```
— префіксна інвалідація з `refetchType: 'active'`: перечитується КОЖЕН зріз зі спостерігачем. Саме від цього стереже `sliceCache.ts` (`CL-02`: «до 91 таблиці… найважчим запитом системи», `:1-26`) і `C1-01`: приховані сітки режиму «одна таблиця» лишаються змонтованими (`SheetTables.tsx`, `display: none`) і мають лише позначатися застарілими (`sliceCache.ts:141-154`, `markSliceHidden`). `invalidateSlices` це враховує, `slices.all()` — ні.
**Сценарій відтворення:** аркуш у режимі «одна таблиця», людина переглянула 10 таблиць (10 змонтованих сіток) → «+ Add permit from date…» у будь-якому рядку → 10 одночасних `GET /documents/{id}/tables/{tid}` (на Land до 5,8 МБ кожен — коментар `useCellStyles.ts`), хоча видима одна. Те саме на «Undo last» і «Split».
**Вплив:** кожна дія з дозволами (`(j)` Ship 1–2) на важких аркушах; секунди заморозки й мегабайти трафіку.
**Зміна — покроково:**
1. `usePermitHistory.ts`:
   ```diff
    import { queryKeys } from '@/api/queryKeys';
   +import { invalidateSlices } from '@/features/grid/sliceCache';
   ```
   ```diff
   -export async function refreshAfterPermitChange(queryClient: QueryClient, documentId: number): Promise<void> {
   +export async function refreshAfterPermitChange(
   +  queryClient: QueryClient,
   +  documentId: number,
   +  periodKey: number,
   +): Promise<void> {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.document.ofDocument(documentId) }),
   -    queryClient.invalidateQueries({ queryKey: queryKeys.slices.all() }),
   +    // ⛔ W-5: адресно (`CL-02`/`C1-01`): видимі зрізи документа за період — перезапит; приховані й решта — лише
   +    // «застарілі» (дочитаються при показі/монтуванні). `slices.all()` перечитував кожну змонтовану сітку.
   +    invalidateSlices(queryClient, { documentId, periodKey }),
   ```
   Хуки: `useAddRowPermit(documentId: number, periodKey: number)`, `useUndoRowPermit(documentId: number, periodKey: number)`, `useUndoHeaderPermit(documentId: number, periodKey: number)` — у їхніх `onSuccess` `refreshAfterPermitChange(queryClient, documentId, periodKey)`; у `useAddHeaderPermit` — `onSuccess: (_result, body) => { void refreshAfterPermitChange(queryClient, documentId, body.periodKey); }`; у `useSaveCellSplits` — `refreshAfterPermitChange(queryClient, documentId, body.periodKey)` (`body` вже є в сигнатурі `onSuccess`).
2. Виклики: `AddRowPermitForm.tsx:71` `useAddRowPermit(documentId)` → `useAddRowPermit(documentId, periodKey)` (`periodKey` деструктуровано в `:61`); `HeaderPermitSection.tsx:36` `useUndoHeaderPermit(documentId)` → `useUndoHeaderPermit(documentId, periodKey)`; `RowPermitSection.tsx:67` `useUndoRowPermit(documentId)` → `useUndoRowPermit(documentId, periodKey)`.
3. `__tests__/refreshAfterPermitChange.test.ts:16`: `refreshAfterPermitChange(client, 3)` → `refreshAfterPermitChange(client, 3, 202602)`.
**Тест-доказ:** той самий файл, новий кейс `'W-5: прихований змонтований зріз лише застаріває, видимий — перечитується'`: `QueryClient` зі `staleTime: Infinity`; два спостерігачі `new QueryObserver(client, { queryKey: queryKeys.slices.one(501, 202602), queryFn: async () => { fetched.push(501); return { rows: [] }; } }).subscribe(() => {})` і так само `502`; дочекатися початкових запитів, `fetched.length = 0`; `const release = markSliceHidden(502, 202602)`; `await refreshAfterPermitChange(client, 3, 202602)`; `await waitFor(() => expect(fetched).toEqual([501]))`; `expect(client.getQueryState(queryKeys.slices.one(502, 202602))?.isInvalidated).toBe(true)`; `release()`. Без фіксу (`slices.all()`) — `fetched` = `[501, 502]`, червоний.
**Перевірка після впровадження:** `npx vitest run src/features/permits/history`; `npx tsc --noEmit`. Стенд: Network при «Add permit» на аркуші з кількома переглянутими таблицями — `GET …/tables/…` лише для видимої.
**Ризики і побічні ефекти:** зрізи ІНШИХ періодів стають застарілими без запиту (поза наміром `invalidateSlices`) — вони не змонтовані, при перемиканні періоду прочитаються свіжими. Поділ місяця дозволом шапки зачіпає кілька таблиць одного документа × періоду — усі в межах наміру.
**Обґрунтування:** повторне використання `invalidateSlices` (єдине правило `CL-02`/`C1-01`/`X2-05`), а не новий предикат.
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням.
**Залежності:** немає. (Те саме `slices.all()` є в `FormulaManualOverrideToggle.tsx:64`, `DocumentPeriodReopenDialog.tsx:62`, `useDocumentPeriodReopen.tsx:82`, `PeriodReopenedDocuments.tsx:81` — адміністративні/рідкісні дії, не перевірено, чи там є змонтовані сітки; кандидати на ту саму заміну.)

---

### W-6 — Низька — `401`: `from` без рядка запиту
**Категорія:** correctness
**Де:** `src/Ecr.Web/src/api/client.ts:646`.
**Проблема:** `const from = typeof window === 'undefined' ? path : window.location.pathname;` — без `search`. Решта шляхів виходу беруть `pathname + search`: `signOutForIdle` (`:284`), `recordBeforeSignOut` (`:360`), `abandonSwitchedSession` (`:431`). Той самий `from` іде і в слід незбережених правок (`runBeforeLoginRedirect(from)` → `lostEdits.ts:166`), і в `?from=` сторінки входу (`LoginPage.tsx:215`, `:290` через `safeReturnPath`, який рядок запиту приймає).
**Сценарій відтворення:** `/documents/7?periodKey=202603&sheet=GEN&table=…` → сесія спливла → будь-який запит `401` → вхід → повернення на `/documents/7` (період і аркуш за замовчуванням, не ті, де людина працювала; банер відновлення правок веде теж на голий шлях). Очікувано: як при виході через бездіяльність — та сама адреса.
**Вплив:** кожен вхід після спливання сесії; людина шукає свій місяць/аркуш/фільтр наново.
**Зміна — покроково:**
1. `api/client.ts:646`:
   ```diff
   -    const from = typeof window === 'undefined' ? path : window.location.pathname;
   +    // ✎ W-6: разом із рядком запиту (період, аркуш, фільтри) — як `signOutForIdle`/`recordBeforeSignOut`.
   +    const from = typeof window === 'undefined' ? path : window.location.pathname + window.location.search;
   ```
**Тест-доказ:** `src/Ecr.Web/src/api/__tests__/errors.test.ts`, новий кейс `'W-6: 401 несе в from і рядок запиту сторінки'`: `window.history.replaceState(null, '', '/documents/7?periodKey=202603&sheet=GEN')`; `const redirect = vi.fn(); setLoginRedirect(redirect);` `vi.stubGlobal('fetch', vi.fn(async () => new Response('', { status: 401 })))`; `await apiFetch('/api/v1/documents/7').catch(() => undefined)`; `expect(redirect.mock.calls[0]?.[0]).toBe('/documents/7?periodKey=202603&sheet=GEN')`; наприкінці `window.history.replaceState(null, '', '/')`. Без фіксу — `'/documents/7'`.
**Перевірка після впровадження:** `npx vitest run src/api src/features/grid/__tests__/lostEdits.test.tsx src/pages/__tests__/LoginPage.sessionInvalidated.test.tsx`.
**Ризики і побічні ефекти:** `?from=` довшає (кодується `encodeURIComponent`, `client.ts:187`); `safeReturnPath` уже пропускає `?…`.
**Обґрунтування:** узгодження з трьома сусідніми шляхами того самого файлу.
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням.
**Залежності:** немає.

---

### W-7 — Низька — «Сьогодні» за UTC у двох місцях дозволів
**Категорія:** correctness
**Де:** `src/Ecr.Web/src/features/permits/bindings/BindingPreview.tsx:31` (типовий «кінець періоду» перегляду, `:62`); `src/Ecr.Web/src/features/permits/air/import/naeNodes.ts:24` (типове `today` для фільтра чинних вузлів `isLive`, виклик `PreviewStep.tsx:59`).
**Проблема:** `new Date().toISOString().slice(0, 10)` — дата UTC. Модуль `shared/format/dateOnly.ts:7-10` прямо забороняє саме це («о 00:30 у додатному поясі дає ВЧОРАШНІЙ день») і дає `todayDateOnly()`. Пояс майданчика — `Asia/Atyrau` (+05:00, `PeriodsPage.tsx`).
**Сценарій відтворення:** 1 квітня о 03:00 за Атирау (31.03 22:00 UTC): майстер імпорту AIR пропонує вузли, чий `validTo = 2026-03-31`, як чинні (бо «сьогодні» = 31.03); перегляд прив'язки бере кінець періоду 31.03 замість 01.04.
**Вплив:** лише в нічному вікні 00:00–05:00 місцевого часу; рідко, але тихо.
**Зміна — покроково:**
1. `BindingPreview.tsx`:
   ```diff
   -const today = (): string => new Date().toISOString().slice(0, 10);
   +// ✎ W-7: календарний день людини, а не UTC (`dateOnly.ts`).
   +const today = (): string => todayDateOnly();
   ```
   і імпорт `import { todayDateOnly } from '@/shared/format/dateOnly';`.
2. `naeNodes.ts:24`:
   ```diff
   -export function nodeOptions(nodes: readonly NaeTreeNode[], today: string = new Date().toISOString().slice(0, 10)): readonly NodeOption[] {
   +export function nodeOptions(nodes: readonly NaeTreeNode[], today: string = todayDateOnly()): readonly NodeOption[] {
   ```
   і `import { todayDateOnly } from '@/shared/format/dateOnly';`.
**Тест-доказ:** новий `src/Ecr.Web/src/features/permits/air/import/__tests__/naeNodes.test.ts`: `vi.mock('@/shared/format/dateOnly', () => ({ todayDateOnly: () => '2026-04-01' }))`; `vi.useFakeTimers({ now: new Date('2026-03-31T22:00:00Z') })`; вузол `{ id: 1, display: 'A', parentEntryId: null, levelCode: 'AREA', isActive: true, validTo: '2026-03-31' }` (решта полів `NaeTreeNode` — за типом `airTypes.ts`); `expect(nodeOptions([node])).toEqual([])`. Без фіксу (UTC «сьогодні» = 31.03) вузол лишається чинним — червоний. (Пояс у `vmThreads` не задати — `dateOnly.test.ts:7`, тому мок, а не `TZ`.)
**Перевірка після впровадження:** `npx vitest run src/features/permits/air src/features/permits/bindings`; `npm run lint`.
**Ризики і побічні ефекти:** немає.
**Обґрунтування:** єдине джерело «сьогодні» (`L9-17`).
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням.
**Залежності:** немає.

---

## Порядок впровадження

Усі сім знахідок не мають спільних файлів — їх можна робити паралельно:
- **Група A (сервер + клієнт, контракт):** W-2 (`DocumentHeaderHandlers.cs`, `DocumentHeaderDto.cs`, `openapi.snapshot.json`, `schema.d.ts`, `02-contracts.md`, `DocumentHeaderPanel.tsx`). Єдина зі зміною контракту — інтегрувати першою або окремим PR (регенерація `schema.d.ts` конфліктує з будь-якою іншою зміною контракту).
- **Група B (сервер + клієнт, без контракту):** W-1 (`ExcelImporter.cs`, `PatchCellsResponse.cs` remark, `ImportPanel.tsx`).
- **Група C (лише клієнт):** W-3 (`DocumentsPage.tsx`) — після рішення PO; W-4 (`SheetCopyBar.tsx`); W-5 (`features/permits/history/*`); W-6 (`api/client.ts`); W-7 (`BindingPreview.tsx`, `naeNodes.ts`).
- Залежностей між знахідками немає. W-1 і W-2 спираються на наявний `followRecalculation` (`useCellPatch.ts`) — його не змінювати.

## Відкинуто

- **Частковий звіт, «збереження шапки без `jobId`»** — підтверджено, оформлено як W-2. **«`from` без `search`»** — підтверджено, W-6.
- **`BulkGroupDialog` (`features/templates/ctor/BulkGroupDialog.tsx:155-157`): «Apply» без `isPending`-перевірки** — другий `PATCH` іде з тією самою `expectedPresentationRevision` і дістає `409 presentationRevisionConflict`; подвійного запису немає (лише зайвий запит).
- **Гонка `refreshAfterPermitChange`/`addRow`-перечитування з `PATCH` у дорозі** — закрито `G1-01` у `applyPatchLocally` (`cellPatchRequest.ts:155-164`: читання, що перекрилось, перезапускається).
- **`useCellStyles.onError` відкочує цілий знімок зрізу** (`useCellStyles.ts:136-138`) — `onSettled` після відмови завжди інвалідує зріз (`:186`), стан сервера повертається.
- **`ChatComposer`: подвійний Ctrl+Enter** — той самий `clientMessageId` для того самого тексту (`ChatComposer.tsx:104-105`), сервер дедуплікує.
- **Відкритий редирект `?from=`** — `shared/safeReturnPath.ts` відсікає `//`, `/\`, керівні символи; навігація через `navigate`.
- **CSRF-заголовок у `apiFetch`** — сервер перевіряє `Origin` (`src/Ecr.Api/Security/CsrfOriginMiddleware.cs`), cookie `HttpOnly`; окремий токен не потрібен.
- **Скидання курсора при зміні фільтрів переліку** — усі сеттери пишуть `cursor: null` одним переходом (`documentListFilters.ts:158-243`).
- **`AddRowPermitForm` `nextDay`/`previousDay` через `toISOString`** (`AddRowPermitForm.tsx:40-44`, `:214-219`) — арифметика повністю в UTC над рядком дати, результат коректний.
- **Undo-стек сітки при зміні періоду** (`DocumentGrid.tsx:619`) — сітки ключовані `tableInstanceId` (`SheetTables.tsx:386`), екземпляр унікальний на період → новий стек.
- **`ListDrawer`: Escape під час перейменування значення закриває шторку** — незбережений текст одного значення; UX/a11y, не дефект даних (до аудитора UI).
- **`PresentationEditor`/`reorderApi` без `expectedPresentationRevision`** — паралельна правка вигляду двома адміністраторами шаблону не продемонстрована читанням як втрата даних (поля шлються лише змінені); лишаю без картки.

## Перевірено й у порядку

`apiFetch`: `credentials: 'include'`, кореляція, `X-Ecr-User` на небезпечних методах, `401` форм входу/зміни пароля не перенаправляє, `Retry-After` 429/503; глобальні повтори — лише запити, 4xx не повторюються, мутації без повторів (крім `isTransientBusy` у `useAddRow`/`useDeleteRow`); `MutationCache.onError` + `meta.handled`; `useUrlState` будує від живої локації (`pending`), подвійні сеттери в одному обробнику безпечні; `sliceCache` (`CL-02`, `C1-01`, `X2-05`); `followRecalculation` (один слідкувач, пауза у прихованій вкладці, `finally`-передача сітці); `usePollWhileVisible` і `useSheetChanges` (покоління відповідей, зупинка на 404/422); `useDiscussionSummary` (jitter, без опитування у фоні, `If-None-Match`); `lostEdits` — `sessionStorage` з `userId` у ключі; експорт/результат задачі — same-origin `<a download>`; `dangerouslySetInnerHTML`/`innerHTML`/`javascript:` — не знайдено; `dateOnly.ts`, `endOfSiteDayUtc` (`PeriodsPage.tsx:148-153`) — пояс майданчика; `useAddRow` (`if (addRow.isPending) return`, `DocumentGrid.tsx:3070`); `ExportButton` (прив'язка задачі до документа/періоду, тост один раз); `TreeEditor` пошук дебаунсено; `GridHScroll`/`IdleSignOut`/`TableChatPanel` таймери з прибиранням.

## Самоперевірка

- **W-1:** `ExcelImporter.cs:506` (`async Task EnqueueRecalculationAsync`) і `:700` (`return new PatchCellsResponse(applied, versions, validation);`) перевірено `grep -n`; diff «було» дослівний (рядки 506-512 прочитано). `EnqueueCoalescedAsync` → `Task<string>` (`IBackgroundJobScheduler.cs:109`); 4-й позиційний параметр `PatchCellsResponse` — `string? RecalculationJobId` (`PatchCellsResponse.cs:40-46`). Клієнт: `ImportPanel.tsx:165` — рядок `await invalidateSlices(...)` дослівний; `followRecalculation` експортовано з `useCellPatch.ts` (використовується в `useAddRow.ts:7`); `result.recalculationJobId` є в `schema.d.ts`. Тест упаде без фіксу: сервер повертає `null`; клієнт не викликає `followRecalculation`. Дублікатів у `KNOWN-ISSUES.md`, `docs/build/audit-2026-10-09*`, `questions/`, `10-decisions.md` немає (grep «recalculationJobId», «ідентифікатора не несуть»).
- **W-2:** `DocumentHeaderHandlers.cs:357` (`var changed = 0;`), `:389-403`, `:805-836` перевірено `awk NR`; `DocumentHeaderDto.cs:47` дослівно; `DocumentHeaderPanel.tsx:620-627` дослівно; імпорт `@/features/grid/edits` є в `:22`. `with { … }` на `sealed record` з `init` — валідно. Хелпери тесту (`Patch()`, `Request`, `VersionOfAsync`, `Values`, `AddPeriod`, `_jobs`, `_access`) існують у `DocumentHeaderHandlersTests.cs:905-944`. Тест упаде без фіксу (властивості немає → не компілюється/`null`). Дублікатів немає (поза частковим звітом цього ж файлу).
- **W-3:** `DocumentsPage.tsx:118`, `:151-182`, `:392/395/398`, `:446`, `:674-678`, `:697-698` перевірено `awk`; рядок `onClick={() => setUrlParams({ cursor: page.nextCursor })}` дослівний. `useInfiniteQuery` уже вживається в проєкті (TanStack 5, `initialPageParam` обов'язковий); `DocumentListPage` має `nextCursor: null | string` (`schema.d.ts:34449-34458`); `noUncheckedIndexedAccess` увімкнено — тому `pages.at(-1)` з перевіркою. Тест упаде без фіксу (перша сторінка зникає). Відомим/рішенням не є (grep «Load more», «cursor» у `10-decisions.md` — лише D-298 про аудит-журнал).
- **W-4:** `SheetCopyBar.tsx:110-120`, імпорт `useState` у `:1`, `nameDraft` у `:49` перевірено; поріг 100 мс — `useDurationIndicator.ts:25`. Тест із двома синхронними `fireEvent.click` дає 2 POST без фіксу (два `mutate()` до перерендеру). Дублікатів немає.
- **W-5:** `usePermitHistory.ts:39-46` дослівно (`queryClient.invalidateQueries({ queryKey: queryKeys.slices.all() })`); `invalidateSlices`/`markSliceHidden` експортовано з `sliceCache.ts`; виклики `AddRowPermitForm.tsx:71`, `HeaderPermitSection.tsx:36`, `RowPermitSection.tsx:67` і наявність `periodKey` у їхніх пропсах перевірено; `AddHeaderPermitRequest`/`SaveCellSplitsRequest` несуть `periodKey`, `UndoRowPermitRequest`/`UndoHeaderPermitRequest` — ні (`schema.d.ts:41567-41582`), тому сигнатури трьох хуків змінюються. Тест упаде без фіксу: префіксна інвалідація перечитує й прихований `502`. Дублікатів немає.
- **W-6:** `client.ts:646` дослівно; порівняльні рядки `:284`, `:360`, `:431` перевірено. Середовище тестів — `jsdom` (`vite.config.ts:188`). Тест упаде без фіксу (`'/documents/7'`). Дублікатів немає.
- **W-7:** `BindingPreview.tsx:31`, `naeNodes.ts:24` дослівно; `todayDateOnly` експортовано з `shared/format/dateOnly.ts`. Тест із моком і фальшивим часом упаде без фіксу (UTC-дата 31.03 лишає вузол чинним). Дублікатів немає.
- **Вилучено під час самоперевірки:** кандидат «гонка перечитування після зміни дозволу з `PATCH` у дорозі» (закрито `G1-01`); «відкат знімка в `useCellStyles`» (виправляється `onSettled`); «подвійний клік `BulkGroupDialog`» (захищено `409`); «Undo-стек сітки при зміні періоду» (сітки ключовані). Перелічено в «Відкинуто».
