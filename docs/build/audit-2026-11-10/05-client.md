# 05 — Клієнт (React): correctness / performance / security

**Область:** `src/Ecr.Web/src` (React 19, TanStack Query 5, react-router 7), з перевіркою серверного боку там, де дефект клієнта залежить від контракту (`src/Ecr.Application`, `src/Ecr.Adapters.Excel`, `src/Ecr.Api`).
**SHA `main`:** `f446e9b29` · **Дата:** 2026-11-11 · **Режим:** лише читання (без збирання/тестів).
**✎ 11.11 (рішення PO, `00-PLAN.md` §4):** W-3 переписано — нескінченна прокрутка в переліку документів і 5 журналах (підкроки W-3a…W-3f, сервер перевірено); рядки W-3 звірено з `main` `39aea41d9` (код ідентичний `f446e9b29`).

**Охоплено повністю:** `api/client.ts` (401/`from`, CSRF/Origin, `X-Ecr-User`, `Retry-After`, `isRetryable`), `app/queryClient.ts`, `shared/ui/useUrlState.ts`, `shared/safeReturnPath.ts`, `features/import/*` (синхронний і фоновий Apply), `features/documents/sheetCopies/*`, `features/permits/history/*`, `features/grid/sliceCache.ts`, `features/grid/useAddRow.ts`/`useDeleteRow.ts`, `followRecalculation`/`settleRecalculation` (`useCellPatch.ts`), `applyPatchLocally` (`cellPatchRequest.ts`), `features/grid/liveRefresh/*`, `shared/ui/usePollWhileVisible.ts`, `pages/DocumentsPage.tsx` (пагінація, ключі, фільтри), `features/documents/DocumentHeaderPanel.tsx` (збереження), `features/tableChat/ChatComposer.tsx`/`api.ts`, `features/grid/lostEdits.ts` (сховище), `shared/format/dateOnly.ts`.
**Поверхово:** `features/grid/DocumentGrid.tsx` (слайс-запит, undo-стек, add-row), `useCellStyles.ts`, `SheetActions.tsx`, `VersionMigrationDialog.tsx`, `DocumentInspector.tsx`, `pages/admin/PeriodsPage.tsx` (пояс майданчика), `features/templates/ctor/BulkGroupDialog.tsx`, `PresentationEditor.tsx`, `features/permits/typeEditor/*` (URL-діалоги), `features/permits/lists/ListDrawer.tsx`, `features/registries/tree/*`, `features/editAssignments/*`, `features/export/ExportButton.tsx`.
**Не охоплено:** вставка/клавіатура/віртуалізація сітки (`clipboard.ts`, `bodyPaste.ts`, `keyCommitGate.ts`, `autosave.ts` повністю), `pages/admin/*` поза названими, `features/methodologies`, `features/integration`, e2e-специ, фактичні розміри чанків (потрібна збірка — бюджет 250 KB перевіряється лише міркуванням про імпорти).

## Зведена таблиця

| ID | Важливість | Категорія | Назва | Зусилля | Ризик зміни | Рішення PO |
|---|---|---|---|---|---|---|
| W-1 | Висока | correctness | Синхронний імпорт Excel: перерахунок формул не відстежується — обчислені колонки на екрані старі до 5 хв | S | низький | ні |
| W-2 | Середня | correctness | Збереження шапки документа ставить повний перерахунок усіх періодів, але клієнт про нього не знає — `HDR.*`-формули на екрані старі | M | середній | ні |
| W-3 | Середня | correctness / ux | «Load more» ЗАМІНЮЄ сторінку (перелік документів + 5 журналів) → ✎ рішення PO 11.11: нескінченна прокрутка (W-3a…W-3f) | L | середній | ні (отримано 11.11) |
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

### W-3 — Середня — «Load more» замінює сторінку (перелік документів і 5 журналів) → нескінченна прокрутка
**✎ рішення PO 11.11:** «Замість Load More краще інфініті скрол … якщо це не суперечить фреймворкам» (`00-PLAN.md` §4). Перевірено: не суперечить — TanStack Query `5.62.11` (`src/Ecr.Web/package.json:30`) має `useInfiniteQuery` (у проєкті вже 16 модулів-споживачів, напр. `features/jobs/useApprovalQueue.ts:25`, `pages/admin/SecurityPage.tsx`), `IntersectionObserver` уже керує монтуванням сіток (`features/grid/SheetTables.tsx:267`, запасна гілка без IO — `:251-265`), а набір `KIT.md` §6.5 (`docs/design/hybrid/KIT.md:311-313`, «Showing 25 of 112 · Show more») лишається видимим: прокрутка ДОДАЄТЬСЯ до рядка «показано N з M» і кнопки, а не замінює їх. Варіант B старої картки («Next page») відпав. Специфікацію переписано: спільний підвал переліку + 6 послідовних підкроків W-3a…W-3f (+ необов'язковий W-3g). Рядки звірено з `main` `39aea41d9` (клієнтський і серверний код ідентичний `f446e9b29`: `git diff --stat f446e9b29 39aea41d9 -- src tests` порожній).

**Категорія:** correctness / ux / a11y
**Де:**
- `src/Ecr.Web/src/pages/DocumentsPage.tsx:118` (курсор з адреси), `:151-184` (`useQuery` з `cursor` у ключі), `:392`, `:395`, `:398`, `:443-447`, `:486`, `:672-678` (кнопка `setUrlParams({ cursor: page.nextCursor })`), `:697-698`;
- `src/Ecr.Web/src/pages/admin/AuditPage.tsx:173-174`, `:185`, `:191`, `:205`, `:207`, `:439-463` (кнопка `:456-460`);
- `src/Ecr.Web/src/pages/admin/ConsistencyIssuesPage.tsx:97`, `:109-118`, `:129`, `:267`, `:306-314` (кнопка `:310-312`);
- `src/Ecr.Web/src/features/audit/SecurityEventsPanel.tsx:41-44`, `:88-97`, `:141-145`; `StructureChangesPanel.tsx:49-52`, `:90-99`, `:150-154`; `WorkflowEventsPanel.tsx:69-79`, `:123-132`, `:192-196`;
- хуки `src/Ecr.Web/src/features/audit/api.ts:453-460` (`useWorkflowEvents`), `:513-520` (`useSecurityEvents`), `:523-532` (`useStructureChanges`); `useFilterCursor` — `src/Ecr.Web/src/shared/ui/useDebouncedFilter.ts:45-59`.
- Сервер (контракт курсора — див. «Серверний бік» нижче): `src/Ecr.Infrastructure/Persistence/DocumentStore.cs:474-490`, `:599-603`, `:761-804`, `:823`, `:840`; `src/Ecr.Infrastructure/Persistence/AuditPaging.cs:69-106`; `AuditReader.cs:45-78`, `:632-641`, `:853-858`, `:1023-1029`; `ConsistencyIssueReader.cs:36-46`, `:142`.

**Проблема:** усі шість переліків тримають курсор у СТАНІ (адреса — `DocumentsPage.tsx:118`; `useFilterCursor` — решта) і кладуть його в ключ запиту, тож «Load more» відкриває ІНШИЙ запис кешу: таблиця показує лише нову сторінку, попередні зникають. Перелік документів:
```tsx
{page.nextCursor !== null && (
  <Button mt="md" variant="default" onClick={() => setUrlParams({ cursor: page.nextCursor })}>
```
журнали — те саме через `setCursor(page.nextCursor)` (`AuditPage.tsx:457`, `ConsistencyIssuesPage.tsx:310`, `SecurityEventsPanel.tsx:142`, `StructureChangesPanel.tsx:151`, `WorkflowEventsPanel.tsx:193`). Повернутися до першої сторінки нічим: `useUrlState` пише адресу з `replace: true` (`useUrlState.ts:56`), а курсор `useFilterCursor` живе лише в стані компонента. Текст кнопки (`documents.more` = «Load more» / «Загрузить ещё» / «Тағы жүктеу», `CATALOG-en-base.sql:1098`, `00-prelude.sql:2186`, `:5202`) обіцяє дописування; `SecurityPage` (той самий рядок, `SecurityPage.tsx:529`) справді дописує.

**Сценарій відтворення:** рік із > 50 документів → «Load more» → у таблиці лише документи другої сторінки; лічильник «застарілих на сторінці» (`:395`) і варіанти фільтра версій (`:392`) рахуються лише з неї. Те саме в журналах (100 рядків на сторінку): «Load more» на `/admin/audit` → перші 100 змін зникають. Очікувано (рішення PO 11.11): прокрутка до кінця переліку сама дописує наступну сторінку; якщо рядки не заповнюють вікно (великий екран, збільшення вікна) — наступна сторінка довантажується без прокрутки; для клавіатури й старих браузерів лишається видима кнопка.

**Вплив:** усі користувачі переліку документів у проєктах із > 50 документами і всі читачі журналів аудиту/узгодженості; втрата контексту, хибне враження «документів/подій стало менше».

**Серверний бік — контракт курсора (перевірено, серверних змін НЕ потрібно):**

| Ендпоінт | Порядок і курсор | Розрив рівних | Висновок |
|---|---|---|---|
| `GET /api/v1/documents` | keyset `d.Id > after` (`DocumentStore.cs:823`, `:840`) + `ORDER BY d.Id` (`:490`), курсор — `Id` останнього (`:603`). Сортування за версією: групи проєктів у порядку версій, усередині — `OrderBy(d => d.Id)` (`:474-485`), курсор «група + Id» (`:761-804`) | `Id` — PK, унікальний | Стабільно. Новий документ (більший `Id`) з'являється в кінці видачі, видалений — не зсуває keyset. ⚠ Документ, чий проєкт змінив версію шаблону між підвантаженнями, переходить у іншу групу — може трапитися двічі або випасти (рідкісно; так само було з кнопкою). Клієнт прибирає дублі за `id` (`mergePages`, W-3a). Відсів видимості ПІСЛЯ сховища (`DocumentQueryHandlers.cs:229-245`) дає сторінки, коротші за 50 або порожні при `nextCursor` — підвал добирає їх сам (див. `isEmpty`, W-3a). `totalCount` — лише коли видача з першої сторінки й одна (`:255`) |
| `GET /api/v1/audit/cells` | `AuditPaging.Order` = `(ChangedAt, Id)` або `(ім'я автора, UserId, ChangedAt, Id)` (`AuditPaging.cs:99-106`), keyset `Keyset` (`:69-81`) з `Id` останнім складником; курсор `v2|порядок|ticks|id|user|ім'я` (`AuditCursorCodec.cs:24-32`) | `Id` | Стабільно: `ChangedAt` — `datetime2(3)` (`11-audit-tables.sql:44`), параметр `@afterAt` — `Scale = 3` (`AuditPaging.cs:86-88`), округлення немає. Гаряче+архів — `UNION ALL`, пара `(ChangedAt, Id)` унікальна по обох (SWITCH зберігає `Id`, `AuditReader.cs:248-249`). Курсор чужого порядку — `422 auditCursorSortMismatch` (`AuditPaging.cs:38-44`); порядок у ключі запиту, тож сторінки одного ключа — одного порядку. ⚠ Порядки «за користувачем»: перейменування автора між підвантаженнями зсуває ключ (рідкісно, так само з кнопкою). `totalCount` — лише на першій сторінці (`GetCellChangesHandler.cs:84`, `:97`) |
| `GET /api/v1/audit/structure` | `AppendKeyset(… "ChangedAt", "Id" …)` (`AuditReader.cs:634-636`), `Order` (`:641`) | `Id` | Стабільно (`datetime2(3)`, `11-audit-tables.sql:169`) |
| `GET /api/v1/audit/security` | `AppendKeyset` (`AuditReader.cs:1026-1028`), `Order` (`:1029`) | `Id` | Стабільно (`11-audit-tables.sql:217`) |
| `GET /api/v1/audit/workflow` | `AppendKeyset(… "At", "Id" …)` (`AuditReader.cs:857`), `Order` (`:858`) | `Id` | Стабільно (`wf.ApprovalEvent.At` — `datetime2(3)`, `Migrations/20260919194439_BE11ApprovalEvent.cs:28`) |
| `GET /api/v1/consistency/issues` | `WHERE Id < @before … ORDER BY Id DESC` (`ConsistencyIssueReader.cs:36-46`), курсор — `Id` (`:142`) | `Id` | Стабільно; розв'язання знахідки між підвантаженнями (`openOnly`) не зсуває keyset. `totalCount` — на кожній сторінці |

Нові події журналів (порядок типово «найновіші першими») з'являються ВГОРІ, поза довантаженими сторінками — keyset їх не зачіпає, дублів немає; побачити їх — перечитати (зміна фільтра / F5).

**Архітектурні рішення (застосовуються в усіх підкроках):**
- **Курсор — параметр сторінки `useInfiniteQuery`**, ключ — лише ЗАСТОСОВАНІ фільтри (без курсора). Фільтри перелік документів і далі тримає в адресі (`useDocumentListFilters`, `useUrlState`), курсор з адреси ЙДЕ (посилання «на третю сторінку» не відтворюється — свідомо). Зміна фільтра = новий ключ = нова видача з першої сторінки в тому самому рендері (те, що робив `useFilterCursor`).
- **Ключі журналів — `[…, 'pages', запит]`**, а не ключі наявних `useQuery`-хуків: `useQuery` і `useInfiniteQuery` під одним ключем ділили б запис кешу різної форми (`DocumentInspector.tsx:801` читає `['audit-cells', запит]` звичайним `useCellChanges`).
- **Місце спільного коду — `src/Ecr.Web/src/features/common/infiniteList/`, не `shared/ui`.** (1) `shared/ui/**` — гаряча зона: лист-попередження перед правкою (`CLAUDE.md:224`) і обов'язковий lane `integ-*` з повним CI (`CLAUDE.md:23`, `:106`); (2) компоненти `shared/ui` за правилом набору не кличуть `t()` (підписи — пропами: `DataTable.tsx:213-227`, `CATALOG-en-base.sql:3166-3171`), а підвалу потрібні 4 рядки каталогу; (3) `features/common` уже тримає наскрізні UI-частини з `t()` (`CollapsibleNote.tsx:3`, `usePendingLoading.ts`), і його імпортують фічі й сторінки (`SheetCopyBar.tsx:4`, `DocumentsPage.tsx:31`). `DataTable.onShowMore` (`DataTable.tsx:103`) не беремо: сторож-вузол мусив би жити в `shared/ui`.
- **Хуки журналів — окремий модуль `features/audit/journalPages.ts`, не `api.ts`:** `api.ts` статично в графі `DocumentPage` (`DocumentInspector.tsx:18`), і `useInfiniteQuery` там важив би на бюджеті `D-132` сторінки, якій журналу не треба (той самий прийом, що `fileName.ts`, `api.ts:8-10`).
- **Без дублю запитів:** спостерігач існує лише коли `hasNextPage && !isFetching && !isFetchNextPageError`; при перетині він від'єднується ДО виклику; `fetchNextPage({ cancelRefetch: false })` (дубль-клік чи перетин під час запиту повертає той самий проміс, а не перезапускає його — типове `cancelRefetch: true` перезапускало б).
- **Автозаповнення вікна:** після КОЖНОЇ сторінки (перехід `isFetching → false`) створюється новий спостерігач, а новий спостерігач браузер одразу повідомляє про ПОТОЧНИЙ перетин (той самий самовідновний прийом, що `SheetTables.tsx:295-302`) — якщо сторож усе ще у вікні (+400 px), береться наступна сторінка. Збільшення вікна — звичайна подія перетину. Запобіжники: ланцюг автодовантаження без виходу сторожа за межі вікна — не більше `MaxAutoFillChain = 5` сторінок (порожні після відсіву сторінки, 4K-екран); після `MaxAutoRows = 1000` рядків автодовантаження зупиняється (див. «Віртуалізація»); далі — кнопка, клік знімає паузу.
- **Відмова наступної сторінки не ховає таблицю:** `AsyncBoundary`/`DataTable` отримують `error = isFetchNextPageError ? null : error` (інакше `AsyncBoundary.tsx:121-131` замінив би вже показані рядки станом помилки); підвал показує `ErrorAlert` з «Повторити», автоповтору немає (спостерігач не створюється при `isFetchNextPageError`).
- **A11y:** рядок стану `role="status"` (ввічливий live-регіон, змонтований завжди) — «Showing N of M» / «Showing the first N; there are more» (наявні `common.shownOf`/`common.shownSoFar`) або «All N shown» (новий `list.allShown`); видима кнопка «Load more» (`documents.more`) поки є наступна сторінка — шлях клавіатури (WCAG 2.1.1) і середовищ без IO; остання сторінка прибирає кнопку з-під фокуса → фокус переходить на рядок стану (`tabIndex={-1}`), а не на `body`; сторож — `aria-hidden`.
- **Менше перечитувань:** злиття сторінок — `useMemo` над `query.data` (структурне спільне використання TanStack лишає незмінні сторінки тими самими об'єктами); журнали — `refetchOnWindowFocus: false` (історія; повернення у вкладку перечитувало б УСІ довантажені сторінки підряд); перелік документів — фокус-перечитування лише поки довантажена ОДНА сторінка (`refetchOnWindowFocus: (q) => (q.state.data?.pages.length ?? 0) <= 1`), мутації й далі інвалідують.
- **Інвалідація після мутацій працює без змін:** `invalidateQueries({ queryKey: ['documents'] })` (`CreateDocumentModal.tsx:369`, `DeleteDocumentAction.tsx:117`, `BusinessKeyChangeAction.tsx:200`, `useProjectRecalcProgress.ts:152`) і `ConsistencyIssuesKey` (`useConsistencyRun.ts:127`) — префіксні; TanStack перечитує всі довантажені сторінки ПОСЛІДОВНО, беручи курсор із щойно отриманої попередньої — без дублів і пропусків. Ціна — N запитів замість одного (N ≤ 20 для документів через `MaxAutoRows`). `setQueryData` на ключ переліку ніде немає (`git grep`).
- **Відновлення прокрутки:** `AppScrollRestoration` (`app/AppScrollRestoration.tsx:12-18`, ключ — шлях) відновлює `window.scrollY` при поверненні з документа; довантажені сторінки лежать у кеші (`gcTime` 5 хв за замовчуванням), рядки рендеряться синхронно — позиція відновлюється. Після спливання кешу повертається перша сторінка, автозаповнення добирає вікно (точну позицію не гарантуємо). Той самий механізм уже відновлює позицію на довгому переліку `/admin/ui-strings` (3000→3300, 5555→5654 px — `docs/build/questions/Q-277.md:132-140`). Перевіряє e2e (крок W-3a.6).
- **Віртуалізація — НЕ вводиться (рішення з доказами):** (1) прецедент `D-332` (`docs/tz/10-decisions.md:781`): переліки — таблиці без віртуалізації до 2000 записів; (2) бібліотеки віртуалізації в залежностях немає (`package.json`), нова — ризикова зона lock-файла (`CLAUDE.md:106`) і бюджет `D-132`; (3) віртуалізований `<table>` ламає нативну семантику рядків для читалки, `Ctrl+F` і липку шапку `ecr-sticky-head`; (4) DOM обмежено `MaxAutoRows = 1000` (20 сторінок документів / 10 журналу), далі — свідомий клік; (5) прецедент у застосунку: `/admin/ui-strings` — 977 рядків у DOM без віртуалізації (`docs/build/questions/Q-277.md:132-134`), а реальний обсяг переліку документів — 93 документи на Land (`docs/build/WORK-QUEUE.md:410`, CL-10: сторінка p50 287 мс), тобто 2 сторінки; для масового читання журналів є CSV-експорт (`AuditExportButton`). Чи плавна прокрутка на 1000 рядках переліку документів (смуга 12 місяців у кожному) — **потребує перевірки на стенді** (INP < 200 мс у Performance-панелі); якщо ні — знизити константу (один рядок), а не вводити віртуалізацію без окремого рішення.
- **Бюджет маршруту 250 KB (`D-132`, `scripts/check-bundle-budget.mjs:33`):** `infiniteQueryBehavior` уже у вхідному чанку (його тягне `QueryClient`); нові — `useInfiniteQuery`/`InfiniteQueryObserver` (~0.5–1 KB gzip) + `features/common/infiniteList` (~1 KB). `DocumentsPage` «на межі» (`DocumentsPage.tsx:71`; останній замір 232.2 KB — `docs/build/questions/Q-284.md:131`). Вимір обов'язковий (`npm run build && npm run budget`); перевищення — стоп і ескалація (стелю не піднімати, `Grandfathered` порожній, `check-bundle-budget.mjs:46-55`).
- **Рядки UI:** нові ключі `list.allShown`, `list.autoPaused` — новий файл сіду (W-3a.3). `documents.more` **не прибирається**: його й далі читає `SecurityPage.tsx:529` і підпис кнопки підвалу (той самий текст «Load more», без перейменування — інакше 8 наявних тестів шукають кнопку за `⟦documents.more⟧`, напр. `filterCursorReset.test.tsx:101`, `AuditPage.sort.test.tsx:283`).

**Зміна — покроково** (підкроки ПОСЛІДОВНІ, один lane; кожен — окремий коміт із власним тестом):

#### W-3a — спільний підвал + перелік документів

1. **Новий** `src/Ecr.Web/src/features/common/infiniteList/mergePages.ts`:
   ```ts
   /**
    * W-3 (рішення PO 11.11): сторінки курсорної видачі як одна. Рядки — підряд, без дублів за ключем (рядок, що між
    * підвантаженнями перейшов на іншу сторінку, не дає двох вузлів з одним `key`); `nextCursor` і решта полів — ОСТАННЬОЇ
    * сторінки, `totalCount` — ПЕРШОЇ (журнали рахують його лише на першій, `GetCellChangesHandler.cs:84`).
    */
   export function mergePages<TPage extends { readonly items: readonly unknown[]; readonly totalCount: number | null }>(
     pages: readonly TPage[] | undefined,
     keyOf?: (item: TPage['items'][number]) => string | number,
   ): TPage | undefined {
     if (pages === undefined) return undefined;
     const first = pages[0];
     const last = pages.at(-1);
     if (first === undefined || last === undefined) return undefined;

     const all = pages.flatMap<TPage['items'][number]>((page) => page.items);
     const seen = new Set<string | number>();
     const items =
       keyOf === undefined
         ? all
         : all.filter((item) => {
             const key = keyOf(item);
             if (seen.has(key)) return false;
             seen.add(key);
             return true;
           });

     return { ...last, items, totalCount: first.totalCount } as TPage;
   }

   /**
    * Наступний курсор для `getNextPageParam`. ⚠ Той самий курсор, що щойно прочитаний, — кінець: інакше сервер, що повернув
    * незмінний курсор, зациклив би автодовантаження. `last?.` — тести інших екранів мокають чужі адреси відповіддю `null`
    * (той самий захист, що `CollectionRunsPanel.tsx:140-147`).
    */
   export function nextCursorOf(
     last: { readonly nextCursor: string | null } | null | undefined,
     lastParam: string | null,
   ): string | null {
     const next = last?.nextCursor ?? null;

     return next !== null && next === lastParam ? null : next;
   }
   ```
2. **Новий** `src/Ecr.Web/src/features/common/infiniteList/useInfiniteScroll.ts`:
   ```ts
   import { useCallback, useEffect, useState } from 'react';

   /** Запас знизу: наступна сторінка проситься, коли до сторожа лишається стільки пікселів. */
   export const InfiniteScrollAheadPx = 400;

   /** Скільки сторінок поспіль хук бере САМ, поки сторож не виходив за межі вікна (порожні сторінки, 4K-екран). */
   export const MaxAutoFillChain = 5;

   /** Після стількох рядків автодовантаження зупиняється (W-3: без віртуалізації, `D-332`); далі — кнопка. */
   export const MaxAutoRows = 1000;

   /** Те, що потрібно від `useInfiniteQuery` (структурно сумісне з `UseInfiniteQueryResult`). */
   export interface InfinitePager {
     readonly hasNextPage: boolean;
     readonly isFetching: boolean;
     readonly isFetchingNextPage: boolean;
     readonly isFetchNextPageError: boolean;
     readonly error: unknown;
     readonly fetchNextPage: (options?: { cancelRefetch?: boolean }) => Promise<unknown>;
   }

   /** Чому автодовантаження не діє: немає IO, вичерпано ланцюг, досягнуто стелі рядків. */
   export type AutoPause = 'unsupported' | 'chain' | 'rows' | null;

   export interface InfiniteScroll {
     /** Ref-колбек вузла-сторожа під останнім рядком. */
     readonly sentinelRef: (node: HTMLElement | null) => void;
     readonly pause: AutoPause;
     /** Наступна сторінка дією людини (кнопка, «Повторити»); знімає паузу ланцюга. */
     readonly loadMore: () => void;
   }

   /**
    * W-3 (рішення PO 11.11): нескінченна прокрутка переліку.
    *
    * ⛔ Спостерігач живе ЛИШЕ коли запиту немає (`!isFetching`) і є що брати: при перетині він від'єднується ДО виклику, а
    * `cancelRefetch: false` повертає проміс запиту, що вже йде, — дубль-запитів немає.
    * ⚠ Після кожної сторінки спостерігач створюється НАНОВО: браузер одразу повідомляє новому про поточний перетин, тож
    * рядки, що не заповнили вікно, самі тягнуть наступну сторінку (прийом `SheetTables.tsx:295-302`).
    * ⚠ Сторож вийшов за межі вікна — ланцюг з нуля: далі сторінки бере прокрутка людини.
    */
   export function useInfiniteScroll(pager: InfinitePager, shown: number): InfiniteScroll {
     const [sentinel, setSentinel] = useState<HTMLElement | null>(null);
     const [streak, setStreak] = useState(0);
     const { hasNextPage, isFetching, isFetchNextPageError, fetchNextPage } = pager;

     const pause: AutoPause =
       typeof IntersectionObserver === 'undefined'
         ? 'unsupported'
         : shown >= MaxAutoRows
           ? 'rows'
           : streak >= MaxAutoFillChain
             ? 'chain'
             : null;
     const ready = hasNextPage && !isFetching && !isFetchNextPageError && pause === null;

     useEffect(() => {
       if (sentinel === null || !ready) return undefined;

       const observer = new IntersectionObserver(
         (entries) => {
           if (!entries.some((entry) => entry.isIntersecting)) {
             setStreak(0);
             return;
           }

           observer.disconnect();
           setStreak((value) => value + 1);
           void fetchNextPage({ cancelRefetch: false });
         },
         { rootMargin: `0px 0px ${String(InfiniteScrollAheadPx)}px 0px` },
       );
       observer.observe(sentinel);

       return () => observer.disconnect();
     }, [sentinel, ready, fetchNextPage]);

     const loadMore = useCallback(() => {
       setStreak(0);
       void fetchNextPage({ cancelRefetch: false });
     }, [fetchNextPage]);

     return { sentinelRef: setSentinel, pause, loadMore };
   }
   ```
   (Корінь спостерігача — вікно: усі шість переліків прокручуються сторінкою — `ecr-sticky-head` липне до вікна, `documentsList.css:8-11` має лише `overflow-x`, `DataTable` без `height` не обмежує висоту, `DataTable.tsx:604`.)
3. **Новий** `src/Ecr.Web/src/features/common/infiniteList/InfiniteListFooter.tsx`:
   ```tsx
   import { useEffect, useRef, useState, type JSX } from 'react';
   import { Box, Button, Group, Text } from '@mantine/core';
   import { usePendingLoading } from '@/features/common/usePendingLoading';
   import { t } from '@/shared/i18n';
   import { ErrorAlert } from '@/shared/ui/ErrorAlert';
   import { useInfiniteScroll, type InfinitePager } from './useInfiniteScroll';

   export interface InfiniteListFooterProps {
     readonly pager: InfinitePager;
     /** Скільки рядків уже показано (після злиття сторінок). */
     readonly shown: number;
     /** Скільки всього, якщо сервер знає (`totalCount` першої сторінки); інакше `null`. */
     readonly total?: number | null | undefined;
   }

   /**
    * W-3 (рішення PO 11.11): підвал переліку з нескінченною прокруткою — сторож, рядок стану (live-регіон), пауза,
    * відмова з «Повторити» і кнопка «Load more» (клавіатура, середовища без `IntersectionObserver`; `KIT.md` §6.5).
    *
    * ⚠ Викликач ставить `key` = ключ видачі: новий фільтр — новий підвал (лічильник ланцюга з нуля).
    */
   export function InfiniteListFooter({ pager, shown, total }: InfiniteListFooterProps): JSX.Element {
     const { sentinelRef, pause, loadMore } = useInfiniteScroll(pager, shown);
     // ФВ-14.26: спінер лише після порогу (а не з першого кадру) — `usePendingLoading`.
     const loading = usePendingLoading(pager.isFetchingNextPage);
     const status = useRef<HTMLParagraphElement | null>(null);
     const [buttonFocused, setButtonFocused] = useState(false);
     const { hasNextPage } = pager;

     // ⚠ Остання сторінка прибирає кнопку з-під фокуса клавіатури: фокус — на рядок стану, а не на `body`.
     useEffect(() => {
       if (!hasNextPage && buttonFocused) {
         status.current?.focus();
         setButtonFocused(false);
       }
     }, [hasNextPage, buttonFocused]);

     const text =
       shown === 0
         ? ''
         : hasNextPage
           ? typeof total === 'number'
             ? t('common.shownOf', { shown, total })
             : t('common.shownSoFar', { shown })
           : t('list.allShown', { shown });

     return (
       <Box mt="md" data-infinite-footer="" data-auto-pause={pause ?? 'none'}>
         <div ref={sentinelRef} aria-hidden="true" data-infinite-sentinel="" style={{ height: 1 }} />
         {pager.isFetchNextPageError && <ErrorAlert error={pager.error} onRetry={loadMore} />}
         <Group gap="sm">
           <Text ref={status} tabIndex={-1} size="sm" c="dimmed" role="status" data-infinite-status="">
             {text}
           </Text>
           {hasNextPage && (pause === 'chain' || pause === 'rows') && (
             <Text size="sm" c="dimmed" data-infinite-paused="">
               {t('list.autoPaused')}
             </Text>
           )}
           {hasNextPage && !pager.isFetchNextPageError && (
             <Button
               variant="default"
               loading={loading}
               onClick={loadMore}
               onFocus={() => setButtonFocused(true)}
               onBlur={() => setButtonFocused(false)}
               data-load-more=""
             >
               {t('documents.more')}
             </Button>
           )}
         </Group>
       </Box>
     );
   }
   ```
   (`loading={loading}` не підпадає під храповик `pendingSpinner.ratchet.test.ts` — регулярка шукає `isPending` усередині `loading={…}`.)
4. **Новий сід** `src/Ecr.Infrastructure/Persistence/Sql/seed/features/UI-infinite-scroll.sql` (ім'я — під регулярку `^[A-Z0-9]+-[a-z0-9-]+\.sql$`, `SeedSplitLayoutTests.cs:263`; зразок будови — `COLL-r5c1-issues-more.sql`; плейсхолдер `{shown}` однаковий у трьох мовах — `SeedTranslationTests.cs:177`):
   ```sql
   -- COLL:ui-infinite-scroll ── ru/kz: W-3 (рішення PO 11.11) — підвал переліку з нескінченною прокруткою; власна порція ──
   INSERT INTO #I18N ([Key], Lang, Val)
   SELECT v.[Key], v.Lang, v.Val
     FROM (VALUES
       (N'list.allShown', N'ru', N'Показаны все: {shown}'),
       (N'list.allShown', N'kz', N'Барлығы көрсетілді: {shown}'),
       (N'list.autoPaused', N'ru', N'Автозагрузка приостановлена: сузьте список фильтрами или загрузите ещё вручную.'),
       (N'list.autoPaused', N'kz', N'Автожүктеу тоқтатылды: тізімді сүзгілермен тарылтыңыз немесе қолмен тағы жүктеңіз.')
          ) AS v ([Key], Lang, Val)
   OPTION (RECOMPILE);
   GO
   -- COLL:ui-infinite-scroll ── кінець секції ──

   -- COLL:ui-infinite-scroll ── W-3: кінець видачі й пауза автодовантаження в підвалі переліку; ru/kz — порцією вище ──
   INSERT INTO #UiEn ([Key], Lang, Val, Scope)
   SELECT v.[Key], v.Lang, v.Val, v.Scope
     FROM (VALUES
       (N'list.allShown',   N'en', N'All {shown} shown', 1),
       (N'list.autoPaused', N'en', N'Automatic loading paused: narrow the list with filters or load more manually.', 1)
          ) AS v ([Key], Lang, Val, Scope)
   OPTION (RECOMPILE);
   GO
   -- COLL:ui-infinite-scroll ── кінець секції ──
   ```
   Наявні ключі, які підвал читає без змін: `common.shownOf` / `common.shownSoFar` (`CATALOG-en-base.sql:4037-4038`, `00-prelude.sql:4541-4542`, `:7548-7549`), `common.retry` (`ErrorAlert.tsx:101`), `documents.more`.
5. `src/Ecr.Web/src/pages/DocumentsPage.tsx`:
   ```diff
   -import { lazy, Suspense, useRef, useState, type JSX } from 'react';
   +import { lazy, Suspense, useMemo, useRef, useState, type JSX } from 'react';
   ```
   ```diff
   -import { useQuery } from '@tanstack/react-query';
   +import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
   ```
   ```diff
    import { CollapsibleNote } from '@/features/common/CollapsibleNote';
   +import { InfiniteListFooter } from '@/features/common/infiniteList/InfiniteListFooter';
   +import { mergePages, nextCursorOf } from '@/features/common/infiniteList/mergePages';
   ```
   `:118`:
   ```diff
   -  const [cursor] = useUrlState('cursor');
   ```
   `:151-184`:
   ```diff
   -  const query = useQuery({
   -    queryKey: [
   -      'documents',
   -      null,
   -      year,
   -      cursor,
   -      filters.state,
   +  /*
   +   * ✎ W-3a (рішення PO 11.11): нескінченна прокрутка. Курсор — параметр СТОРІНКИ `useInfiniteQuery`, а не адреси: доти
   +   * `?cursor=` у ключі давав ІНШИЙ запис кешу — перші 50 рядків зникали, а `replace: true` (`useUrlState`) не лишав «Назад».
   +   * Ключ — лише фільтри (вони й далі в адресі, `ФВ-14.29`). Стара адреса з `?cursor=` відкриває першу сторінку.
   +   */
   +  const listKey = [
   +    'documents',
   +    null,
   +    year,
   +    filters.state,
   ```
   (елементи `filters.mine` … `filters.versionSort` між ханками, `:158-166`, — без змін, лише відступ на 2 пробіли менше.)
   ```diff
          filters.versionSort,
   -    ],
   -    queryFn: () =>
   +  ] as const;
   +  const query = useInfiniteQuery({
   +    queryKey: listKey,
   +    queryFn: ({ pageParam }) =>
          listDocuments({
            periodKey: null,
            year,
   -        cursor,
   +        cursor: pageParam,
            state: filters.state,
   ```
   ```diff
            projectId: filters.projectId,
          }),
   +    initialPageParam: null as string | null,
   +    getNextPageParam: (last, _pages, lastParam) => nextCursorOf(last, lastParam),
   +    // ⚠ Повернення у вкладку перечитує ВСІ довантажені сторінки підряд — лише поки сторінка одна; мутації й далі інвалідують.
   +    refetchOnWindowFocus: (q) => (q.state.data?.pages.length ?? 0) <= 1,
      });
   +
   +  // Довантажені сторінки як одна: без дублів за `id` (документ, що змінив групу версії між сторінками, — `DocumentStore.cs:474-485`).
   +  const listPage = useMemo(() => mergePages(query.data?.pages, (document) => document.id), [query.data]);
   ```
   (`DocumentListParams.cursor` — `string | null` (`features/documents/api.ts:119`), `pageParam` — `string | null`.) `:392`, `:395`, `:398`:
   ```diff
   -          versionOptionsQuery.data?.versions ?? distinctVersions(query.data?.items ?? []),
   +          versionOptionsQuery.data?.versions ?? distinctVersions(listPage?.items ?? []),
   ```
   ```diff
   -        staleOnPage={query.data?.items.filter((d) => d.resultsStale === true).length ?? 0}
   +        staleOnPage={listPage?.items.filter((d) => d.resultsStale === true).length ?? 0}
   ```
   ```diff
   -          query.data?.items.find((d) => filters.owner !== null && d.ownerUserId === filters.owner)?.ownerDisplayName ??
   +          listPage?.items.find((d) => filters.owner !== null && d.ownerUserId === filters.owner)?.ownerDisplayName ??
   ```
   `:417` (коментар перемикача подань):
   ```diff
   -            фільтри, період і курсор — СПІЛЬНІ для обох подань, бо лежать в адресі. */}
   +            фільтри й рік — СПІЛЬНІ для обох подань (в адресі); довантажені сторінки — теж (один запис кешу, W-3a). */}
   ```
   `:443-447`:
   ```diff
          <AsyncBoundary<DocumentListPage>
            isPending={query.isPending}
   -        error={query.error}
   -        data={query.data}
   -        isEmpty={(page) => page.items.length === 0}
   +        // ✎ W-3a: відмова НАСТУПНОЇ сторінки не замінює таблицю станом помилки — її показує підвал із «Повторити».
   +        error={query.isFetchNextPageError ? null : query.error}
   +        data={listPage}
   +        // ⚠ Порожньо — лише коли й далі нічого: після відсіву видимості (`DocumentQueryHandlers.cs:229-245`) сторінка може
   +        // бути порожньою при `nextCursor` — тоді добирає підвал, а не стан «документів немає».
   +        isEmpty={(page) => page.items.length === 0 && page.nextCursor === null}
   ```
   `:672-678`:
   ```diff
                {/* ⚠ Курсорна пагінація, а не offset: за місяць у проєкті тисячі
                    документів, і сторінка 200 через OFFSET сканує все, що до неї. */}
   -            {page.nextCursor !== null && (
   -              <Button mt="md" variant="default" onClick={() => setUrlParams({ cursor: page.nextCursor })}>
   -                {t('documents.more')}
   -              </Button>
   -            )}
   +            {/* ✎ W-3a: наступна сторінка ДОПИСУЄТЬСЯ — сама при прокрутці до кінця або коли рядки не заповнюють вікно;
   +                кнопка лишається для клавіатури й середовищ без `IntersectionObserver`. */}
   +            <InfiniteListFooter key={JSON.stringify(listKey)} pager={query} shown={page.items.length} total={page.totalCount} />
   ```
   `:697-698`:
   ```diff
   -            document={query.data?.items.find((item) => String(item.id) === quickLookId)}
   -            listReady={query.data !== undefined}
   +            document={listPage?.items.find((item) => String(item.id) === quickLookId)}
   +            listReady={listPage !== undefined}
   ```
   `Button`, `setUrlParams`, `useUrlState` лишаються вжитими (`:327`, `:308`, `:120`). Сеттери, що пишуть `cursor: null` (`:313`, `:476`, `documentListFilters.ts:158-243`), НЕ чіпати: вони прибирають `?cursor=` зі старих посилань.
6. **Новий e2e** `src/Ecr.Web/e2e/infiniteScroll.spec.ts` (зразок — `documentsBoardCard.spec.ts`: підміна відповіді переліку в браузері, `signIn` — локальна функція, як `documentsBoardCard.spec.ts:88-95`; `test.skip` без стенда):
   ```ts
   import { expect, test, type Page } from '@playwright/test';

   /**
    * Infinite scroll (W-3, рішення PO 11.11) проти РЕАЛЬНОГО стенда (`tools/e2e-stand.ps1`, `-Grep "Infinite scroll:"`).
    * ⚠ Сторінки переліку підміняються в браузері (на стенді кілька документів, а прокрутці потрібно 150): перший справжній
    * рядок року клонується з іншими `id`/`businessKey`. Журнал безпеки — повністю синтетичний.
    */
   const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
   const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
   const PageSize = 50;
   const Pages = 3;

   async function servePages(page: Page, cursors: string[]): Promise<void> {
     await page.route(/\/api\/v1\/documents\?/, async (route) => {
       const url = new URL(route.request().url());
       if (!url.searchParams.has('year')) return route.continue(); // «Мої задачі» теж читають перелік — не наш запит
       const cursor = url.searchParams.get('cursor');
       cursors.push(cursor ?? '');
       url.searchParams.delete('cursor');
       const response = await route.fetch({ url: url.toString() });
       const body = (await response.json()) as { items: Array<Record<string, unknown>> };
       const sample = body.items[0];
       if (sample === undefined) throw new Error('на стенді за рік немає документа — нема що клонувати');
       const index = cursor === null ? 0 : Number(cursor.slice(1));
       const items = Array.from({ length: PageSize }, (_, n) => {
         const k = index * PageSize + n + 1;
         return { ...sample, id: 900_000 + k, businessKey: `E2E-INF-${String(k).padStart(4, '0')}` };
       });
       await route.fulfill({
         response,
         json: { items, nextCursor: index + 1 < Pages ? `p${String(index + 1)}` : null, totalCount: null },
       });
     });
   }

   const rows = (page: Page) => page.locator('[data-document-row^="E2E-INF-"]');
   ```
   Кейси (`test.describe('Infinite scroll:', …)`, `test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: …')`, адреса `/?year=${Math.trunc(Number(PeriodKey) / 100)}`):
   - `Infinite scroll: documents — scrolling appends pages, the first stays, the end is announced` (1440×900): 50 рядків → `[data-infinite-sentinel]`.`scrollIntoViewIfNeeded()` → 100, `[data-document-row="E2E-INF-0001"]` усе ще є → ще раз → 150; `[data-load-more]` — 0; `[data-infinite-status]` містить `150`; `cursors` = `['', 'p1', 'p2']` (рівно по одному запиту).
   - `Infinite scroll: documents — rows that do not fill a tall window load by themselves; resize loads more`: 1440×900 → 50 → `page.setViewportSize({ width: 1440, height: 9000 })` → без прокрутки 150; `new Set(cursors).size === cursors.length` (жодного дубля).
   - `Infinite scroll: documents — keyboard: Load more, then focus moves to the status line`: цикл `more.focus(); keyboard.press('Enter'); expect.poll(() => rows(page).count()).toBeGreaterThan(before)`, поки є `[data-load-more]`; наприкінці 150 і `[data-infinite-status]` — `toBeFocused()`.
   - `Infinite scroll: documents — back from a document restores the loaded rows and the scroll position`: довантажити 100, `[data-document-row="E2E-INF-0080"] a` → `scrollIntoViewIfNeeded`, запам'ятати `window.scrollY`, клік (сторінка документа 900080 покаже відмову — очікувано), `page.goBack()` → рядків ≥ 100, `expect.poll(scrollY)` у межах ±100 px від запам'ятованого.
   - `Infinite scroll: audit security — journal pages of 100 are appended`: `page.route(/\/api\/v1\/audit\/security\?/)` віддає 2 сторінки по 100 синтетичних подій (`{ changedAt, eventType: 'AccessDenied', targetUserId: null, targetRoleId: null, detailsJson: null, changedByUserId: 1, changedByDisplayName: 'E2E', correlationId: 'inf-<k>', siteTimeZoneId: null }`, поля — `SecurityEventView` у `schema.d.ts`), `nextCursor` `p1` / `null`; `/admin/audit?view=security` → `table.ecr-audit-table tbody tr` 100 → прокрутка до сторожа → 200, `inf-1` усе ще є.
   Наявні e2e не змінюються: жоден spec не клікає «Load more» переліку документів чи журналів (`git grep "documents.more\|Load more" -- e2e` порожньо; `securityExpiredRole.spec.ts:111-118` клікає кнопку `SecurityPage`, якої W-3 не чіпає).

#### W-3b — журнал змін комірок (`AuditPage`)

1. **Новий** `src/Ecr.Web/src/features/audit/journalPages.ts`:
   ```ts
   import { useInfiniteQuery, type InfiniteData, type UseInfiniteQueryResult } from '@tanstack/react-query';
   import { apiFetch } from '@/api/client';
   import type { CellChangePage } from '@/api/types';
   import { nextCursorOf } from '@/features/common/infiniteList/mergePages';
   import { cellChangesQuery } from './api';

   /*
    * ✎ W-3 (рішення PO 11.11): журнали аудиту — нескінченною прокруткою. Окремий модуль, а не `api.ts`: `api.ts` статично
    * в графі `DocumentPage` (`DocumentInspector` → `useCellChanges`), і `useInfiniteQuery` важив би на бюджеті D-132
    * сторінки, якій журналу не треба (прийом `fileName.ts`, `api.ts:8-10`).
    * ⛔ Адреса — літералом поруч із `apiFetch` (сторож `EndpointCoverageTests`, `api.ts:12-17`).
    * ⚠ Ключі — `[…, 'pages', запит]`: `DocumentInspector` читає `['audit-cells', запит]` звичайним `useQuery`, і спільний
    * ключ дав би запис кешу двох форм.
    * ⚠ `refetchOnWindowFocus: false`: журнал — історія; повернення у вкладку перечитувало б ВСІ довантажені сторінки підряд.
    */

   type CellChangeFilter = Omit<Parameters<typeof cellChangesQuery>[0], 'cursor'>;

   /** Журнал змін комірок сторінками (W-3b). */
   export function useCellChangePages(
     filter: CellChangeFilter,
     enabled = true,
   ): UseInfiniteQueryResult<InfiniteData<CellChangePage, string | null>> {
     const query = cellChangesQuery(filter);

     return useInfiniteQuery({
       enabled,
       queryKey: ['audit-cells', 'pages', query],
       queryFn: ({ pageParam }) => {
         const params = cellChangesQuery({ ...filter, cursor: pageParam });
         return apiFetch<CellChangePage>(`/api/v1/audit/cells?${params}`);
       },
       initialPageParam: null as string | null,
       getNextPageParam: (last, _pages, lastParam) => nextCursorOf(last, lastParam),
       refetchOnWindowFocus: false,
     });
   }
   ```
   (`useCellChanges`, `api.ts:546-557`, лишається для `DocumentInspector.tsx:801` — не чіпати.)
2. `src/Ecr.Web/src/pages/admin/AuditPage.tsx`:
   ```diff
   -import { lazy, Suspense, useEffect, useRef, type JSX } from 'react';
   +import { lazy, Suspense, useEffect, useMemo, useRef, type JSX } from 'react';
   ```
   ```diff
      isSingleCell,
   -  useCellChanges,
    } from '@/features/audit/api';
   +import { useCellChangePages } from '@/features/audit/journalPages';
   +import { InfiniteListFooter } from '@/features/common/infiniteList/InfiniteListFooter';
   +import { mergePages } from '@/features/common/infiniteList/mergePages';
   ```
   ```diff
   -import { useDebouncedFilter, useFilterCursor } from '@/shared/ui/useDebouncedFilter';
   +import { useDebouncedFilter } from '@/shared/ui/useDebouncedFilter';
   ```
   `:170-174`:
   ```diff
   -  // ⛔ Курсор скидається разом із ЗАСТОСОВАНИМ фільтром (`useFilterCursor`), а
   -  // не з `onChange` полів: інакше після «More» перша ж клавіша давала зайвий
   -  // запит «старий фільтр, перша сторінка» ще до паузи debounce.
   -  const [cursor, setCursor] = useFilterCursor(cellChangesQuery(applied));
   -  const filter = { ...applied, cursor };
   +  // ✎ W-3b (рішення PO 11.11): курсор — параметр сторінки `useInfiniteQuery`; ключ — ЗАСТОСОВАНИЙ фільтр без курсора,
   +  // тож нова видача починається з першої сторінки в тому самому рендері, що й новий фільтр (раніше — `useFilterCursor`).
   +  const listKey = cellChangesQuery(applied);
   ```
   `:185`:
   ```diff
   -  const changes = useCellChanges(filter, !other);
   +  const changes = useCellChangePages(applied, !other);
   +  const cellPage = useMemo(() => mergePages(changes.data?.pages), [changes.data]);
   ```
   `:191`:
   ```diff
   -  const shownChanges = useLastData(changes.data, changes.error === null);
   +  const shownChanges = useLastData(cellPage, changes.error === null || changes.isFetchNextPageError);
   ```
   `:205`, `:207`:
   ```diff
   -  const single = isSingleCell(filter);
   +  const single = isSingleCell(applied);
   ```
   ```diff
   -  const authorOptions = useAuthorOptions(changes.data?.items, author);
   +  const authorOptions = useAuthorOptions(cellPage?.items, author);
   ```
   `:441`:
   ```diff
   -        error={changes.error}
   +        // ✎ W-3b: відмова НАСТУПНОЇ сторінки не ховає вже показані рядки — її показує підвал із «Повторити».
   +        error={changes.isFetchNextPageError ? null : changes.error}
   ```
   `:454-460`:
   ```diff
                {/* ⚠ Курсорна пагінація: журнал за рік — мільйони рядків, і
                    `OFFSET` на сторінці 200 сканував би все, що до неї. */}
   -            {page.nextCursor !== null && (
   -              <Button mt="md" variant="default" onClick={() => setCursor(page.nextCursor)}>
   -                {t('documents.more')}
   -              </Button>
   -            )}
   +            {/* ✎ W-3b: сторінки ДОПИСУЮТЬСЯ. Підвал — лише над даними ПОТОЧНОГО фільтра: під `useLastData` (старий
   +                фільтр показано, нова видача в дорозі) курсора нової видачі ще немає. */}
   +            {changes.data !== undefined && (
   +              <InfiniteListFooter key={listKey} pager={changes} shown={page.items.length} total={page.totalCount} />
   +            )}
   ```
   `cellChangesQuery` і `Button` лишаються вжитими (`listKey`; `:384`, `:397`). `useLastData` (`:471-478`) — без змін.
3. Коментарі-мутації в тестах (лише текст): `pages/admin/__tests__/AuditPage.security.test.tsx:109` і `AuditPage.structure.test.tsx:105` — `useCellChanges(filter, !other)` → `useCellChangePages(applied, !other)`.

#### W-3c — журнал узгодженості (`ConsistencyIssuesPage`)

1. `src/Ecr.Web/src/pages/admin/ConsistencyIssuesPage.tsx`:
   ```diff
   -import { lazy, Suspense, useState, type JSX } from 'react';
   -import { Badge, Button, Checkbox, Group, Text, UnstyledButton } from '@mantine/core';
   -import { useQuery } from '@tanstack/react-query';
   +import { lazy, Suspense, useMemo, useState, type JSX } from 'react';
   +import { Badge, Checkbox, Group, Text, UnstyledButton } from '@mantine/core';
   +import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
   ```
   ```diff
   -import { useDebouncedFilter, useFilterCursor } from '@/shared/ui/useDebouncedFilter';
   +import { useDebouncedFilter } from '@/shared/ui/useDebouncedFilter';
   +import { InfiniteListFooter } from '@/features/common/infiniteList/InfiniteListFooter';
   +import { mergePages, nextCursorOf } from '@/features/common/infiniteList/mergePages';
   ```
   `:94-97`:
   ```diff
   -  // ⛔ Курсор — від застосованого фільтра, не від сирого поля (`useFilterCursor`).
   -  // ⛔ Курсор — від застосованого фільтра, не від сирого поля (`useFilterCursor`).
   -  // Вага — теж фільтр СЕРВЕРА (LS-E), тож входить у ключ курсора.
   -  const [cursor, setCursor] = useFilterCursor(`${appliedRule}|${String(openOnly)}|${severity ?? ''}`);
   +  // ✎ W-3c: курсор — параметр сторінки `useInfiniteQuery`; ключ — застосовані фільтри (вага — теж фільтр СЕРВЕРА, LS-E).
   ```
   `:109-118`:
   ```diff
   -  const issues = useQuery({
   -    queryKey: [...ConsistencyIssuesKey, appliedRule, openOnly, severityCode ?? null, cursor],
   -    queryFn: () =>
   +  const listKey = [...ConsistencyIssuesKey, 'pages', appliedRule, openOnly, severityCode ?? null] as const;
   +  const issues = useInfiniteQuery({
   +    queryKey: listKey,
   +    queryFn: ({ pageParam }) =>
          apiFetch<ConsistencyIssuePage>(
            `/api/v1/consistency/issues?limit=100&openOnly=${String(openOnly)}` +
              (appliedRule.length === 0 ? '' : `&ruleCode=${encodeURIComponent(appliedRule)}`) +
              (severityCode === undefined || severityCode === null ? '' : `&severity=${String(severityCode)}`) +
   -          (cursor === null ? '' : `&cursor=${encodeURIComponent(cursor)}`),
   +          (pageParam === null ? '' : `&cursor=${encodeURIComponent(pageParam)}`),
          ),
   +    initialPageParam: null as string | null,
   +    getNextPageParam: (last, _pages, lastParam) => nextCursorOf(last, lastParam),
   +    // ⚠ Нові знахідки приносить «Run now» (інвалідація `ConsistencyIssuesKey`, `useConsistencyRun.ts:127`), а не фокус вкладки.
   +    refetchOnWindowFocus: false,
      });
   ```
   (Ключ зведення `[...ConsistencyIssuesKey, 'summary', openOnly]`, `:125`, не перетинається з `'pages'`; префіксна інвалідація після прогону зачіпає обидва.) `:129`:
   ```diff
   -  const page = issues.data;
   +  // Довантажені сторінки як одна (без дублів за `id`); `totals`/`nextCursor` — останньої, `totalCount` — першої.
   +  const page = useMemo(() => mergePages(issues.data?.pages, (issue) => issue.id), [issues.data]);
   ```
   `:267`:
   ```diff
   -            error={issues.error}
   +            error={issues.isFetchNextPageError ? null : issues.error}
   ```
   `:306-314`:
   ```diff
         {/* ⚠ Курсорна пагінація: журнал росте від кожного нічного прогону,
             і стеля одного проходу — тисяча знахідок. */}
   -      {page !== undefined && page.nextCursor !== null && (
   -        <Group>
   -          <Button variant="default" onClick={() => setCursor(page.nextCursor)}>
   -            {t('documents.more')}
   -          </Button>
   -        </Group>
   -      )}
   +      {/* ✎ W-3c: сторінки дописуються прокруткою; лічильник у шапці — `summary` (весь журнал), у підвалі — завантажені. */}
   +      {page !== undefined && (
   +        <InfiniteListFooter key={JSON.stringify(listKey)} pager={issues} shown={page.items.length} total={page.totalCount} />
   +      )}
   ```
   `Group` лишається вжитим (`RunStatus`, `:356`); `Button` — більше ні (прибрано з імпорту). `total` у `DataTable` НЕ передавати (дубль лічильника, `DataTable.tsx:696-713`).

#### W-3d — журнал безпеки (`SecurityEventsPanel`)

1. `src/Ecr.Web/src/features/audit/journalPages.ts` — дописати (імпорти: `type SecurityEventPage`, `securityEventsQuery` з `./api`):
   ```ts
   type SecurityEventFilter = Omit<Parameters<typeof securityEventsQuery>[0], 'cursor'>;

   /** Журнал подій безпеки сторінками (W-3d). */
   export function useSecurityEventPages(
     filter: SecurityEventFilter,
   ): UseInfiniteQueryResult<InfiniteData<SecurityEventPage, string | null>> {
     const query = securityEventsQuery(filter);

     return useInfiniteQuery({
       queryKey: ['audit-security', 'pages', query],
       queryFn: ({ pageParam }) => {
         const params = securityEventsQuery({ ...filter, cursor: pageParam });
         return apiFetch<SecurityEventPage>(`/api/v1/audit/security?${params}`);
       },
       initialPageParam: null as string | null,
       getNextPageParam: (last, _pages, lastParam) => nextCursorOf(last, lastParam),
       refetchOnWindowFocus: false,
     });
   }
   ```
2. `src/Ecr.Web/src/features/audit/api.ts:512-520` — прибрати `useSecurityEvents` (інших споживачів немає: `git grep useSecurityEvents`).
3. `src/Ecr.Web/src/features/audit/SecurityEventsPanel.tsx`:
   ```diff
   -import type { JSX } from 'react';
   -import { Button, Select, Table, Text, TextInput } from '@mantine/core';
   +import { useMemo, type JSX } from 'react';
   +import { Select, Table, Text, TextInput } from '@mantine/core';
    import {
      fetchSecurityExport,
      securityEventsQuery,
   -  useSecurityEvents,
      type SecurityEventPage,
    } from '@/features/audit/api';
   +import { useSecurityEventPages } from '@/features/audit/journalPages';
   +import { InfiniteListFooter } from '@/features/common/infiniteList/InfiniteListFooter';
   +import { mergePages } from '@/features/common/infiniteList/mergePages';
   ```
   ```diff
   -import { useDebouncedFilter, useFilterCursor } from '@/shared/ui/useDebouncedFilter';
   +import { useDebouncedFilter } from '@/shared/ui/useDebouncedFilter';
   ```
   `:41-44`:
   ```diff
   -  const [cursor, setCursor] = useFilterCursor(securityEventsQuery(applied));
   -
   -  const events = useSecurityEvents({ ...applied, cursor });
   -  const authorOptions = useAuthorOptions(events.data?.items, changedBy);
   +  // ✎ W-3d: курсор — параметр сторінки `useInfiniteQuery`; ключ — застосований фільтр без курсора.
   +  const listKey = securityEventsQuery(applied);
   +  const events = useSecurityEventPages(applied);
   +  const eventPage = useMemo(() => mergePages(events.data?.pages), [events.data]);
   +  const authorOptions = useAuthorOptions(eventPage?.items, changedBy);
   ```
   `:90-91`:
   ```diff
   -        error={events.error}
   -        data={events.data}
   +        error={events.isFetchNextPageError ? null : events.error}
   +        data={eventPage}
   ```
   `:141-145`:
   ```diff
   -            {page.nextCursor !== null && (
   -              <Button mt="md" variant="default" onClick={() => setCursor(page.nextCursor)}>
   -                {t('documents.more')}
   -              </Button>
   -            )}
   +            {/* ✎ W-3d: сторінки ДОПИСУЮТЬСЯ прокруткою (`InfiniteListFooter`). */}
   +            <InfiniteListFooter key={listKey} pager={events} shown={page.items.length} total={page.totalCount} />
   ```

#### W-3e — журнал структурних змін (`StructureChangesPanel`)

1. `journalPages.ts` — дописати `useStructureChangePages(filter: Omit<Parameters<typeof structureChangesQuery>[0], 'cursor'>): UseInfiniteQueryResult<InfiniteData<StructureChangePage, string | null>>` — тіло як у W-3d, ключ `['audit-structure', 'pages', query]`, адреса `` `/api/v1/audit/structure?${params}` ``, `params = structureChangesQuery({ ...filter, cursor: pageParam })`; імпорти `structureChangesQuery`, `type StructureChangePage` з `./api`.
2. `api.ts:522-532` — прибрати `useStructureChanges` (споживач лише панель).
3. `src/Ecr.Web/src/features/audit/StructureChangesPanel.tsx`:
   ```diff
   -import type { JSX } from 'react';
   -import { Button, Select, Table, Text, TextInput } from '@mantine/core';
   -import { structureChangesQuery, useStructureChanges, type StructureChangePage } from '@/features/audit/api';
   +import { useMemo, type JSX } from 'react';
   +import { Select, Table, Text, TextInput } from '@mantine/core';
   +import { structureChangesQuery, type StructureChangePage } from '@/features/audit/api';
   +import { useStructureChangePages } from '@/features/audit/journalPages';
   +import { InfiniteListFooter } from '@/features/common/infiniteList/InfiniteListFooter';
   +import { mergePages } from '@/features/common/infiniteList/mergePages';
   ```
   ```diff
   -import { useDebouncedFilter, useFilterCursor } from '@/shared/ui/useDebouncedFilter';
   +import { useDebouncedFilter } from '@/shared/ui/useDebouncedFilter';
   ```
   `:48-52`:
   ```diff
   -  // ⛔ Курсор — від застосованого фільтра, не від сирого поля (`useFilterCursor`).
   -  const [cursor, setCursor] = useFilterCursor(structureChangesQuery(applied));
   -
   -  const changes = useStructureChanges({ ...applied, cursor });
   -  const authorOptions = useAuthorOptions(changes.data?.items, changedBy);
   +  // ✎ W-3e: курсор — параметр сторінки `useInfiniteQuery`; ключ — застосований (відкладений) фільтр без курсора.
   +  const listKey = structureChangesQuery(applied);
   +  const changes = useStructureChangePages(applied);
   +  const changePage = useMemo(() => mergePages(changes.data?.pages), [changes.data]);
   +  const authorOptions = useAuthorOptions(changePage?.items, changedBy);
   ```
   `:92-93`: `error={changes.error}` → `error={changes.isFetchNextPageError ? null : changes.error}`, `data={changes.data}` → `data={changePage}`. `:150-154` — кнопку замінити на `<InfiniteListFooter key={listKey} pager={changes} shown={page.items.length} total={page.totalCount} />` (як у W-3d). Коментар компонента `:25-26` («зміна дат скидає курсор перемонтуванням») — лишити: `key` з вікна (`AuditPage.tsx:282`) і далі перемонтовує панель.
4. `src/Ecr.Web/src/pages/admin/__tests__/filterCursorReset.test.tsx:22-25` — оновити текст мутаційного доказу (тест не змінюється й лишається зеленим: після довантаження нова видача йде одним запитом без курсора):
   ```diff
   - * ⛔ Мутаційний доказ: у `useFilterCursor` повернути `state.cursor` без
   - * перевірки `forKey` — курсор переживає зміну фільтра, і запит із новим
   - * фільтром іде з курсором старої видачі (тести червоніють на `cursor`);
   - * повернути в `onChange` `setCursor(null)` — з'являється зайвий запит.
   + * ⛔ Мутаційний доказ (✎ W-3): у ключ `useInfiniteQuery` (`journalPages.ts` / `ConsistencyIssuesPage`) покласти
   + * СИРЕ значення поля замість застосованого — з'являється зайвий запит «старий фільтр» до паузи; прибрати фільтр
   + * із ключа — запит із новим фільтром не йде зовсім (тести червоніють).
   ```

#### W-3f — журнал погодження (`WorkflowEventsPanel`)

1. `journalPages.ts` — дописати `useWorkflowEventPages(filter: Omit<Parameters<typeof workflowEventsQuery>[0], 'cursor'>): UseInfiniteQueryResult<InfiniteData<WorkflowEventPage, string | null>>` — як W-3d, ключ `['audit-workflow', 'pages', query]`, адреса `` `/api/v1/audit/workflow?${params}` ``.
2. `api.ts:452-460` — прибрати `useWorkflowEvents`. Після W-3d…f `useQuery`/`UseQueryResult` в `api.ts` лишаються вжитими (`useCellChanges`).
3. `src/Ecr.Web/src/features/audit/WorkflowEventsPanel.tsx`:
   ```diff
   -import { Badge, Button, Select, Table, Text } from '@mantine/core';
   +import { Badge, Select, Table, Text } from '@mantine/core';
    import {
      fetchWorkflowExport,
   -  useWorkflowEvents,
      workflowEventsQuery,
      type WorkflowEventPage,
    } from '@/features/audit/api';
   +import { useWorkflowEventPages } from '@/features/audit/journalPages';
   +import { InfiniteListFooter } from '@/features/common/infiniteList/InfiniteListFooter';
   +import { mergePages } from '@/features/common/infiniteList/mergePages';
   ```
   ```diff
   -import { useDebouncedFilter, useFilterCursor } from '@/shared/ui/useDebouncedFilter';
   +import { useDebouncedFilter } from '@/shared/ui/useDebouncedFilter';
   ```
   `:69-78`:
   ```diff
   -  const [cursor, setCursor] = useFilterCursor(workflowEventsQuery(applied));
   -
   -  const events = useWorkflowEvents({ ...applied, cursor });
   +  // ✎ W-3f: курсор — параметр сторінки `useInfiniteQuery`; ключ — застосований фільтр без курсора.
   +  const listKey = workflowEventsQuery(applied);
   +  const events = useWorkflowEventPages(applied);
   +  const eventPage = useMemo(() => mergePages(events.data?.pages), [events.data]);
      const authored = useMemo(
        () =>
   -      (events.data?.items ?? [])
   +      (eventPage?.items ?? [])
            .filter((e) => e.changedByUserId !== null)
            .map((e) => ({ changedByUserId: e.changedByUserId ?? 0, changedByDisplayName: e.changedByDisplayName })),
   -    [events.data],
   +    [eventPage],
      );
   ```
   `:125-126`: `error` / `data` — як у W-3d (`eventPage`). `:192-196` — кнопку замінити на `<InfiniteListFooter key={listKey} pager={events} shown={page.items.length} total={page.totalCount} />`.

#### W-3g — (необов'язково, окремим lane `integ-*`) прибрати `useFilterCursor`

Після W-3b…f `useFilterCursor` (`shared/ui/useDebouncedFilter.ts:31-59`) не має споживачів (`git grep useFilterCursor -- src` — лише коментарі). Прибрати функцію з її коментарем і імпорт `useCallback, useState` (`:1`, лишається `useDebouncedValue`). Це `shared/ui` — лист-попередження і lane `integ-*` (`CLAUDE.md:224`, `:106`); якщо лінія не має повного CI — лишити як мертвий експорт і записати в `TODO-REMAINING.md`.

**Решта файлів:** `docs/tz/10-decisions.md` — новий запис `D-4xx` (наступний вільний номер на момент злиття; зараз останній — `D-474`): «✎ 11.11 (рішення PO). Курсорні переліки (документи, журнали аудиту й узгодженості) — нескінченна прокрутка: `useInfiniteQuery` + сторож `IntersectionObserver`, автозаповнення вікна (≤ 5 сторінок поспіль), видима кнопка й рядок «показано N з M» (KIT §6.5); без віртуалізації, автодовантаження до 1000 рядків (як `D-332`); курсор не в адресі.» + рядок суми в `docs/CHECKSUMS.txt` (`:11`; готовий рядок дає падіння `SpecificationBaselineTests`). Контракт API, міграції — без змін.

**Тест-доказ:**
- **W-3a, підвал** — новий `src/Ecr.Web/src/features/common/infiniteList/__tests__/useInfiniteScroll.test.tsx` + помічник `__tests__/fakeIntersection.ts`: керована підміна `IntersectionObserver` (`vi.stubGlobal`, як `SheetTables.lazy.test.tsx:55-118`), у якій `observe()` через `queueMicrotask` повідомляє ПОТОЧНИЙ стан `visible` (як браузер новому спостерігачу), `setVisible(v)` повідомляє всіх живих, `disconnect()` прибирає зі списку, `live()` — кількість живих. Обвʼязка: компонент із `useInfiniteQuery` над фейковим `fetchPage(cursor)` (записує `requested`, сторінка `{ items: ['row-<i>'], nextCursor: i + 1 < pages ? 'p<i+1>' : null, totalCount: null }`), `mergePages`, `InfiniteListFooter`; `QueryClient` з `retry: false`. Кейси:
  1. `'сторож у вікні: сторінки довантажуються самі, кожна рівно один раз'` — `setVisible(true)`, 3 сторінки → `requested` = `[null, 'p1', 'p2']`, після ще одного такту — та сама довжина; `[data-infinite-status]` = `⟦list.allShown⟧`. Мутація: прибрати `observer.disconnect()` у колбеку і `!isFetching` з `ready` — у `requested` з'являються дублі.
  2. `'сторож поза вікном — лише перша сторінка; перетин — наступна'` — `setVisible(false)` → `[null]`; `setVisible(true)` → містить `'p1'`, без дублів.
  3. `'ланцюг обмежено MaxAutoFillChain; кнопка продовжує'` — 20 сторінок, `setVisible(true)` → `requested.length === 1 + MaxAutoFillChain`, є `[data-infinite-paused]`; клік `⟦documents.more⟧` → довжина більша. Мутація: прибрати `streak >= MaxAutoFillChain` — тягне всі 20.
  4. `'відмова наступної сторінки: рядки лишаються, автоповтору немає, «Повторити» дочитує'` — 2 сторінки, перша спроба `p1` кидає → `row-0` на місці, `ErrorAlert` є, довжина `requested` лишається 2 після кількох тактів; клік `⟦common.retry⟧` → `row-1`. Мутація: прибрати `!isFetchNextPageError` з `ready` — нескінченні повтори.
  5. `'без IntersectionObserver — лише кнопка'` — `vi.stubGlobal('IntersectionObserver', undefined)` → `[null]`, `[data-auto-pause="unsupported"]`, `[data-infinite-paused]` немає; клік → `row-1`.
  6. `'клавіатура: остання сторінка переводить фокус із кнопки на рядок стану'` — `setVisible(false)`, 2 сторінки, `button.focus()`, клік → `document.activeElement` — `[data-infinite-status]`.
  7. `'розмонтування від'єднує спостерігача'` — `unmount()` → `live() === 0`.
  Тест нового модуля червоний без нього (імпорт не резолвиться); мутації вище показують, що кожен запобіжник стережено.
- **W-3a, перелік** — `src/Ecr.Web/src/features/documents/__tests__/DocumentsPage.filters.test.tsx`: `ListReply` += `'paged' | 'pagedFailOnce'`; у гілці `/api/v1/documents` для них: без `cursor=` → `{ items: [Late], nextCursor: 'c2', totalCount: null }`; з `cursor=c2` → `{ items: [OnTime], nextCursor: null, totalCount: null }` (для `'pagedFailOnce'` перша спроба `c2` — `json({ ...Forbidden, status: 500, title: 'Server error', errorCode: 'ECR-SYS-0500' }, 500)`). Кейси: (а) `'W-3a: Load more ДОПИСУЄ другу сторінку, перша лишається'` — `mockFetch('paged'); show('/?year=2026')` → `findByText('LATE-0001')` → клік `⟦documents.more⟧` → `findByText('ONTIME-0002')` і `getByText('LATE-0001')`; `lastListed().get('cursor')` = `'c2'`; у `search` немає `cursor=`. Без фіксу `LATE-0001` зникає — червоний. (б) `'W-3a: відмова другої сторінки — перша лишається, «Повторити» дочитує'` — без фіксу (`useQuery` + `AsyncBoundary error`) таблиця заміняється станом помилки — червоний. (в) `'W-3a: новий фільтр після довантаження — перша сторінка без курсора'` — після (а) змінити стан (`stateField`) → `lastListed().has('cursor') === false`, `LATE-0001` знову перший. Наявні кейси з `?cursor=abc` (`DocumentsPage.filters.test.tsx:187`, `:532`, `pages/__tests__/DocumentsPage.periodFilter.test.tsx:121`, `DocumentsPage.versionColumn.test.tsx:214`) лишаються зеленими: перший запит іде без курсора, сеттери й далі пишуть `cursor: null`. Глобальна заглушка IO в `test/setup.ts:79-90` інертна — автодовантаження в цих тестах немає, кнопка — єдиний шлях (тому наявні тести з кліком «More» не змінюються).
- **W-3b** — новий `src/Ecr.Web/src/pages/admin/__tests__/AuditPage.infinite.test.tsx` (обвʼязка й фікстура рядка — як `AuditPage.sort.test.tsx:21-45`, `:121-196`): `/api/v1/audit/cells` без курсора → 1 рядок, `nextCursor: 'c2'`, `totalCount: 2`; з `cursor=c2` → інший рядок, `null`. Кейс `'W-3b: Load more дописує сторінку журналу комірок'`: `/admin/audit?from=2026-01-01&to=2026-01-31` → 1 рядок у `table.ecr-audit-table tbody` → клік `⟦documents.more⟧` → 2 рядки, `[data-infinite-status]` = `⟦list.allShown⟧`. Без фіксу — 1 рядок (сторінку замінено), червоний. Другий кейс: відмова другої сторінки — рядок першої лишається, є `⟦common.retry⟧`.
- **W-3c** — новий `src/Ecr.Web/src/pages/admin/__tests__/ConsistencyIssuesPage.infinite.test.tsx` (обвʼязка — `filterCursorReset.test.tsx:35-94`): сторінка 1 — знахідка `id: 2, message: 'first'`, `nextCursor: 'c2'`; сторінка 2 — `id: 2` (дубль) і `id: 1, message: 'second'`. Кейс `'W-3c: дописує сторінку, дубль за id — один рядок'`: клік → видно `first` і `second`, `getAllByText('first')` — 1. Без фіксу `first` зникає; без `keyOf` у `mergePages` — два `first`.
- **W-3d/e/f** — нові `src/Ecr.Web/src/features/audit/__tests__/SecurityEventsPanel.infinite.test.tsx`, `StructureChangesPanel.infinite.test.tsx`, `WorkflowEventsPanel.infinite.test.tsx` (обвʼязка — `SecurityEventsPanel.siteZone.test.tsx`; рендер панелі з `from="2026-01-01" to="2026-01-31"` у `MemoryRouter` + `QueryClientProvider`): дві сторінки з різними видимими значеннями (`correlationId` `corr-1`/`corr-2`; `entityLabel` `BG_Total`/`BG_Other`; `documentBusinessKey` `DOC-7`/`DOC-8`) → після кліку `⟦documents.more⟧` видно обидва. Без фіксу перше зникає — червоний.
- Наявні `filterCursorReset.test.tsx` і `AuditPage.sort.test.tsx:274-297` («показати ще» несе курсор того самого порядку; зміна порядку — запит без курсора) лишаються зеленими без змін — вони стережуть ту саму властивість нового ключа.

**Перевірка після впровадження:** після кожного підкроку — `npx vitest run src/features/common/infiniteList src/features/documents/__tests__/DocumentsPage.filters.test.tsx src/pages/__tests__ src/pages/admin/__tests__ src/features/audit/__tests__`; `npx tsc --noEmit`; `npm run lint`; після W-3a і W-3b — `npm run build && npm run budget` (маршрути `DocumentsPage`, `AuditPage`, `ConsistencyIssuesPage` ≤ 250 KB); `dotnet test tests/Ecr.Architecture.Tests` (сід: `SeedSplitLayoutTests`, `SeedTranslationTests`, `SeedCatalogTextTests`; ключі каталогу й адреси клієнта — `EndpointCoverageTests`; `SpecificationBaselineTests` — запис `D-4xx`). Стенд: `tools/e2e-stand.ps1 -Grep "Infinite scroll:"`; вручну — рік із > 50 документів: прокрутка дописує, `F12 → Network` — кожен `cursor=` рівно один раз; вікно 2560×1440 — автозаповнення; Tab до «Load more» + Enter; читалка (NVDA) оголошує «Showing 100 …»; повернення з документа — та сама позиція; INP на 1000 рядках (`Performance`).

**Ризики і побічні ефекти:**
- `invalidateQueries` переліку після мутацій перечитує ВСІ довантажені сторінки послідовно (≤ 20 запитів документів, ≤ 10 журналу) — прийнятно (людина сама їх довантажила); фокус-перечитування переліку документів — лише при одній сторінці, журналів — вимкнено.
- Посилання «на N-ну сторінку» більше не відтворюється (курсор поза адресою) — свідомо; старі `?cursor=` відкривають першу сторінку.
- Клієнтські сортування `DataTable` (`ConsistencyIssuesPage`, `sortValue`) сортують лише довантажені рядки — як і раніше з однією сторінкою.
- Бюджет `D-132`: +~1.5–2 KB на `DocumentsPage`; вимір обов'язковий (див. вище).
- Кількість запитів сервера не росте понад те, що людина прокрутила; автозаповнення обмежене 5 сторінками поспіль і 1000 рядками. Стелі тестів фіксованої кількості запитів (серверні) не зачіпаються — сервер не змінюється.
- `shared/ui` не змінюється (крім необов'язкового W-3g) — lane без вимоги `integ-*`, але з сідом (`seed/features`) і `docs/tz` — повний CI перед RC.

**Обґрунтування:** `useInfiniteQuery` — штатний механізм TanStack для курсорних видач (16 модулів-споживачів у репо), перечитування/інвалідація сторінок — його, а не наші. Сторож `IntersectionObserver` з перестворенням після кожної сторінки покриває одним механізмом і прокрутку, і «рядки не заповнили вікно», і зміну розміру вікна, без `scroll`/`resize`-обробників і читань розкладки в рендері. Кнопка лишається, бо чиста нескінченна прокрутка без неї недоступна з клавіатури й ламає підвал сторінки (WCAG 2.1.1, 2.4.3) і суперечила б `KIT.md` §6.5. Відкинуто: (а) «Next/First page» (старий варіант B) — PO обрав прокрутку; (б) віртуалізація — див. вище, прецедент `D-332`; (в) спільний хук у `shared/ui` — гаряча зона й правило «набір не кличе `t()`»; (г) `maxPages` TanStack — викидав би перші сторінки з-під очей, потрібен `getPreviousPageParam`, якого сервери не дають; (д) серверні зміни — усі шість ендпоінтів уже мають унікальний завершальний ключ keyset.
**Рішення PO:** отримано 11.11 (див. ✎ угорі); нових питань немає.
**Впевненість:** підтверджено читанням (клієнт, сервер, сід, тести); плавність на 1000 рядках, точне відновлення прокрутки й реальний приріст бюджету — потребують перевірки на стенді/збірці.
**Залежності:** W-3a → W-3b → W-3d → W-3e → W-3f (спільні `features/common/infiniteList/*`, `features/audit/journalPages.ts`, `features/audit/api.ts`); W-3c — після W-3a, паралельно з W-3b…f (власні файли); W-3g — останнім. З W-1, W-2, W-4…W-7 спільних файлів немає.

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

Усі сім знахідок не мають спільних файлів між собою — їх можна робити паралельно (W-3 — шість послідовних підкроків в одній групі):
- **Група A (сервер + клієнт, контракт):** W-2 (`DocumentHeaderHandlers.cs`, `DocumentHeaderDto.cs`, `openapi.snapshot.json`, `schema.d.ts`, `02-contracts.md`, `DocumentHeaderPanel.tsx`). Єдина зі зміною контракту — інтегрувати першою або окремим PR (регенерація `schema.d.ts` конфліктує з будь-якою іншою зміною контракту).
- **Група B (сервер + клієнт, без контракту):** W-1 (`ExcelImporter.cs`, `PatchCellsResponse.cs` remark, `ImportPanel.tsx`).
- **Група C (лише клієнт):** W-4 (`SheetCopyBar.tsx`); W-5 (`features/permits/history/*`); W-6 (`api/client.ts`); W-7 (`BindingPreview.tsx`, `naeNodes.ts`).
- **Група D (клієнт + сід + `docs/tz`, ✎ рішення PO 11.11) — W-3, один lane, підкроки послідовно:** W-3a (`features/common/infiniteList/*`, `DocumentsPage.tsx`, сід `UI-infinite-scroll.sql`, `e2e/infiniteScroll.spec.ts`, `10-decisions.md` + `CHECKSUMS.txt`) → W-3b (`features/audit/journalPages.ts` — новий, `AuditPage.tsx`) → W-3d (`SecurityEventsPanel.tsx`, `journalPages.ts`, `api.ts`) → W-3e (`StructureChangesPanel.tsx`, `journalPages.ts`, `api.ts`, `filterCursorReset.test.tsx`) → W-3f (`WorkflowEventsPanel.tsx`, `journalPages.ts`, `api.ts`). W-3c (`ConsistencyIssuesPage.tsx` + власний тест) — після W-3a, паралельно з W-3b…f. W-3g (`shared/ui/useDebouncedFilter.ts`) — необов'язково, останнім, окремим lane `integ-*`. Після W-3a і W-3b — замір бюджету `npm run budget`. З групами A–C спільних файлів немає.
- Залежностей між знахідками немає (усередині W-3 — порядок підкроків вище). W-1 і W-2 спираються на наявний `followRecalculation` (`useCellPatch.ts`) — його не змінювати.

## Відкинуто

- **Частковий звіт, «збереження шапки без `jobId`»** — підтверджено, оформлено як W-2. **«`from` без `search`»** — підтверджено, W-6.
- **`BulkGroupDialog` (`features/templates/ctor/BulkGroupDialog.tsx:155-157`): «Apply» без `isPending`-перевірки** — другий `PATCH` іде з тією самою `expectedPresentationRevision` і дістає `409 presentationRevisionConflict`; подвійного запису немає (лише зайвий запит).
- **Гонка `refreshAfterPermitChange`/`addRow`-перечитування з `PATCH` у дорозі** — закрито `G1-01` у `applyPatchLocally` (`cellPatchRequest.ts:155-164`: читання, що перекрилось, перезапускається).
- **`useCellStyles.onError` відкочує цілий знімок зрізу** (`useCellStyles.ts:136-138`) — `onSettled` після відмови завжди інвалідує зріз (`:186`), стан сервера повертається.
- **`ChatComposer`: подвійний Ctrl+Enter** — той самий `clientMessageId` для того самого тексту (`ChatComposer.tsx:104-105`), сервер дедуплікує.
- **Відкритий редирект `?from=`** — `shared/safeReturnPath.ts` відсікає `//`, `/\`, керівні символи; навігація через `navigate`.
- **CSRF-заголовок у `apiFetch`** — сервер перевіряє `Origin` (`src/Ecr.Api/Security/CsrfOriginMiddleware.cs`), cookie `HttpOnly`; окремий токен не потрібен.
- **Скидання курсора при зміні фільтрів переліку** — усі сеттери пишуть `cursor: null` одним переходом (`documentListFilters.ts:158-243`).
- **W-3: серверна правка курсорів** — не потрібна: усі шість ендпоінтів мають унікальний завершальний ключ keyset (`Id`), `datetime2(3)` без округлення параметра (таблиця «Серверний бік» у W-3).
- **W-3: віртуалізація довгих переліків** — не вводиться: прецедент `D-332` (`10-decisions.md:781`), семантика `<table>`, бюджет `D-132`; DOM обмежено `MaxAutoRows = 1000`.
- **W-3: прибрати `documents.more`** — ключ і далі вжитий (`SecurityPage.tsx:529`, підпис кнопки підвалу); перейменування — лише шум у тестах.
- **`AddRowPermitForm` `nextDay`/`previousDay` через `toISOString`** (`AddRowPermitForm.tsx:40-44`, `:214-219`) — арифметика повністю в UTC над рядком дати, результат коректний.
- **Undo-стек сітки при зміні періоду** (`DocumentGrid.tsx:619`) — сітки ключовані `tableInstanceId` (`SheetTables.tsx:386`), екземпляр унікальний на період → новий стек.
- **`ListDrawer`: Escape під час перейменування значення закриває шторку** — незбережений текст одного значення; UX/a11y, не дефект даних (до аудитора UI).
- **`PresentationEditor`/`reorderApi` без `expectedPresentationRevision`** — паралельна правка вигляду двома адміністраторами шаблону не продемонстрована читанням як втрата даних (поля шлються лише змінені); лишаю без картки.

## Перевірено й у порядку

`apiFetch`: `credentials: 'include'`, кореляція, `X-Ecr-User` на небезпечних методах, `401` форм входу/зміни пароля не перенаправляє, `Retry-After` 429/503; глобальні повтори — лише запити, 4xx не повторюються, мутації без повторів (крім `isTransientBusy` у `useAddRow`/`useDeleteRow`); `MutationCache.onError` + `meta.handled`; `useUrlState` будує від живої локації (`pending`), подвійні сеттери в одному обробнику безпечні; `sliceCache` (`CL-02`, `C1-01`, `X2-05`); `followRecalculation` (один слідкувач, пауза у прихованій вкладці, `finally`-передача сітці); `usePollWhileVisible` і `useSheetChanges` (покоління відповідей, зупинка на 404/422); `useDiscussionSummary` (jitter, без опитування у фоні, `If-None-Match`); `lostEdits` — `sessionStorage` з `userId` у ключі; експорт/результат задачі — same-origin `<a download>`; `dangerouslySetInnerHTML`/`innerHTML`/`javascript:` — не знайдено; `dateOnly.ts`, `endOfSiteDayUtc` (`PeriodsPage.tsx:148-153`) — пояс майданчика; `useAddRow` (`if (addRow.isPending) return`, `DocumentGrid.tsx:3070`); `ExportButton` (прив'язка задачі до документа/періоду, тост один раз); `TreeEditor` пошук дебаунсено; `GridHScroll`/`IdleSignOut`/`TableChatPanel` таймери з прибиранням.

## Самоперевірка

- **W-1:** `ExcelImporter.cs:506` (`async Task EnqueueRecalculationAsync`) і `:700` (`return new PatchCellsResponse(applied, versions, validation);`) перевірено `grep -n`; diff «було» дослівний (рядки 506-512 прочитано). `EnqueueCoalescedAsync` → `Task<string>` (`IBackgroundJobScheduler.cs:109`); 4-й позиційний параметр `PatchCellsResponse` — `string? RecalculationJobId` (`PatchCellsResponse.cs:40-46`). Клієнт: `ImportPanel.tsx:165` — рядок `await invalidateSlices(...)` дослівний; `followRecalculation` експортовано з `useCellPatch.ts` (використовується в `useAddRow.ts:7`); `result.recalculationJobId` є в `schema.d.ts`. Тест упаде без фіксу: сервер повертає `null`; клієнт не викликає `followRecalculation`. Дублікатів у `KNOWN-ISSUES.md`, `docs/build/audit-2026-10-09*`, `questions/`, `10-decisions.md` немає (grep «recalculationJobId», «ідентифікатора не несуть»).
- **W-2:** `DocumentHeaderHandlers.cs:357` (`var changed = 0;`), `:389-403`, `:805-836` перевірено `awk NR`; `DocumentHeaderDto.cs:47` дослівно; `DocumentHeaderPanel.tsx:620-627` дослівно; імпорт `@/features/grid/edits` є в `:22`. `with { … }` на `sealed record` з `init` — валідно. Хелпери тесту (`Patch()`, `Request`, `VersionOfAsync`, `Values`, `AddPeriod`, `_jobs`, `_access`) існують у `DocumentHeaderHandlersTests.cs:905-944`. Тест упаде без фіксу (властивості немає → не компілюється/`null`). Дублікатів немає (поза частковим звітом цього ж файлу).
- **W-3 (✎ переписано 11.11 за рішенням PO):** рядки звірено з `main` `39aea41d9` (`git diff --stat f446e9b29 39aea41d9 -- src tests` порожній). (1) `file:line` відкрито повторно: `DocumentsPage.tsx:1`, `:15`, `:31`, `:118`, `:120`, `:151-184`, `:313`, `:327`, `:392`, `:395`, `:398`, `:417`, `:443-447`, `:476`, `:486`, `:672-678`, `:697-698`; `AuditPage.tsx:1`, `:17-24`, `:28`, `:170-174`, `:185`, `:191`, `:205`, `:207`, `:282`, `:384`, `:397`, `:439-463`, `:471-478`; `ConsistencyIssuesPage.tsx:1-3`, `:23`, `:94-97`, `:109-118`, `:125`, `:129`, `:267`, `:306-314`, `:356`; `SecurityEventsPanel.tsx:1-8`, `:15`, `:40-44`, `:88-97`, `:141-145`; `StructureChangesPanel.tsx:1-3`, `:9`, `:48-52`, `:90-99`, `:150-154`; `WorkflowEventsPanel.tsx:1-8`, `:16`, `:68-79`, `:123-132`, `:192-196`; `features/audit/api.ts:1-17`, `:239-283`, `:452-460`, `:493-532`, `:546-557`; `useDebouncedFilter.ts:31-59`; `SheetTables.tsx:251-265`, `:267`, `:295-302`; `AsyncBoundary.tsx:121-131`; `DataTable.tsx:103`, `:213-227`, `:604`, `:696-713`; `test/setup.ts:79-90`; `AppScrollRestoration.tsx:12-18`; сервер — `DocumentStore.cs:474-490`, `:599-603`, `:761-804`, `:823`, `:840`, `DocumentQueryHandlers.cs:229-245`, `:255`, `AuditPaging.cs:38-44`, `:69-106`, `AuditCursorCodec.cs:24-32`, `AuditReader.cs:45-78`, `:248-249`, `:634-641`, `:853-858`, `:1023-1029`, `ConsistencyIssueReader.cs:36-46`, `:142`, `11-audit-tables.sql:44`, `:169`, `:217`, `BE11ApprovalEvent.cs:28`, `GetCellChangesHandler.cs:84`, `:97`. (2) Diff «було» дослівно звірено з `cat -n`/`sed -n` (усі шість кнопок `setCursor(page.nextCursor)`/`setUrlParams({ cursor: page.nextCursor })`, блоки `useFilterCursor`, `useQuery` переліку й журналу узгодженості, `AsyncBoundary`/`DataTable` `error=`, коментар `filterCursorReset.test.tsx:22-25`). (3) Імена існують: `useInfiniteQuery`, `InfiniteData`, `UseInfiniteQueryResult`, `isFetchNextPageError`, `fetchNextPage({ cancelRefetch })`, функція в `refetchOnWindowFocus` — TanStack Query `5.62.11` (`package.json:30`; ті самі типи вже в `useApprovalQueue.ts:1`, `:25`); `ErrorAlert({ error, onRetry })` (`ErrorAlert.tsx:53-59`); `usePendingLoading` (`features/common/usePendingLoading.ts`); `t(key, params)` (`shared/i18n/index.ts:600`); експорти `cellChangesQuery`, `structureChangesQuery`, `securityEventsQuery`, `workflowEventsQuery`, типи `StructureChangePage`/`SecurityEventPage`/`WorkflowEventPage` (`api.ts:239`, `:297`, `:314`, `:408`, `:425`, `:477`, `:493`), `CellChangePage` (`@/api/types`); `ConsistencyIssuesKey` (`useConsistencyRun.ts:19`); `DocumentListParams.cursor: string | null` (`documents/api.ts:119`); `tsconfig.json`: `ES2022` (`at(-1)`), `exactOptionalPropertyTypes`, `noUncheckedIndexedAccess` враховано (перевірки `undefined` у `mergePages`); `argsIgnorePattern: '^_'` (`eslint.config.js:41`) — для `_pages`; `Button`/`Group` після правок — вжиті там, де лишаються в імпорті. Типи generics у `mergePages` (`flatMap<TPage['items'][number]>`, `as TPage`) — за змістом коректні, остаточно підтверджує `npx tsc --noEmit`. (4) Тести впадуть без фіксу: у всіх шести переліках клік «Load more» за старого коду ЗАМІНЮЄ сторінку — перший рядок/значення зникає (кейси перевіряють, що він лишається); відмова другої сторінки за старого коду замінює таблицю станом помилки (`AsyncBoundary.tsx:121-131`); тести підвалу червоні без модуля, а мутації (без `disconnect`/`!isFetching`, без ланцюга, без `!isFetchNextPageError`) дають дублі/зайві/нескінченні запити. Інертна заглушка IO (`test/setup.ts:79-90`) не дає автодовантаження в наявних тестах — вони зелені без змін (`filterCursorReset.test.tsx`, `AuditPage.sort.test.tsx:274-297`, кейси з `?cursor=abc`). (5) Не дублює відоме: `git grep -i "infinite\|нескінченн\|віртуаліз"` у `docs/tz/10-decisions.md`, `KNOWN-ISSUES.md`, `OPEN-ITEMS.md`, `docs/build/questions/` — лише `D-332` (віртуалізація переліків — не робимо; узгоджено) і `D-168`; рішення PO — `00-PLAN.md` §4. Інші звіти аудиту ці файли не правлять (`grep` по `01…06-*.md`). (6) Виправлено під час самоперевірки: межі `Resume` (`:761-804`, не `:805`), `AsyncBoundary.tsx:121-131` (не `:42-51`), `SheetTables.tsx:295-302`, `documentsBoardCard.spec.ts:88-95`; кількість модулів-споживачів `useInfiniteQuery` — 16; хуки журналів винесено з `api.ts` у `journalPages.ts` (бюджет `DocumentPage`); ключі журналів — з `'pages'` (конфлікт із `useCellChanges` `DocumentInspector`); `totalCount` злитої сторінки — з ПЕРШОЇ сторінки (журнал рахує лише там); `documents.more` НЕ прибирається (`SecurityPage.tsx:529`).
- **W-4:** `SheetCopyBar.tsx:110-120`, імпорт `useState` у `:1`, `nameDraft` у `:49` перевірено; поріг 100 мс — `useDurationIndicator.ts:25`. Тест із двома синхронними `fireEvent.click` дає 2 POST без фіксу (два `mutate()` до перерендеру). Дублікатів немає.
- **W-5:** `usePermitHistory.ts:39-46` дослівно (`queryClient.invalidateQueries({ queryKey: queryKeys.slices.all() })`); `invalidateSlices`/`markSliceHidden` експортовано з `sliceCache.ts`; виклики `AddRowPermitForm.tsx:71`, `HeaderPermitSection.tsx:36`, `RowPermitSection.tsx:67` і наявність `periodKey` у їхніх пропсах перевірено; `AddHeaderPermitRequest`/`SaveCellSplitsRequest` несуть `periodKey`, `UndoRowPermitRequest`/`UndoHeaderPermitRequest` — ні (`schema.d.ts:41567-41582`), тому сигнатури трьох хуків змінюються. Тест упаде без фіксу: префіксна інвалідація перечитує й прихований `502`. Дублікатів немає.
- **W-6:** `client.ts:646` дослівно; порівняльні рядки `:284`, `:360`, `:431` перевірено. Середовище тестів — `jsdom` (`vite.config.ts:188`). Тест упаде без фіксу (`'/documents/7'`). Дублікатів немає.
- **W-7:** `BindingPreview.tsx:31`, `naeNodes.ts:24` дослівно; `todayDateOnly` експортовано з `shared/format/dateOnly.ts`. Тест із моком і фальшивим часом упаде без фіксу (UTC-дата 31.03 лишає вузол чинним). Дублікатів немає.
- **Вилучено під час самоперевірки:** кандидат «гонка перечитування після зміни дозволу з `PATCH` у дорозі» (закрито `G1-01`); «відкат знімка в `useCellStyles`» (виправляється `onSettled`); «подвійний клік `BulkGroupDialog`» (захищено `409`); «Undo-стек сітки при зміні періоду» (сітки ключовані). Перелічено в «Відкинуто».
