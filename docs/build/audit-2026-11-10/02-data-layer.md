# Аудит 2026-11-10 — 02. Шар даних

- **Репозиторій:** `D:/Own project/ECR/project/BabychPV/ECR`, `main` = `f446e9b29`. Дата — 2026-11-11. Лише читання: нічого не збиралось, тести не запускались.
- **Попередній звіт** (main `ac0a1ed2d`, D-1…D-5) перевірено в поточному main. D-1, D-3, D-4 і D-5 підтверджено, їхні рядки оновлено, а зміни доведено до стандарту. D-2 **переглянуто**: шлях PATCH комірок узагалі не бере замок періоду (вердикт наведено в D-2), тому важливість знижено до Low, а зміну звужено.
- **Охоплено повністю:**
  - `DocumentVersionMigrationStore`, `MigrateDocumentVersionHandler`/`VersionMigrationPlanner` — у частині, що стосується даних;
  - `OutboxDispatcher`, `FormulaRecalculationJob`, `CrossPeriodFanOut`;
  - `03-archive-proc.sql` (`usp_RestoreYear`);
  - `SourceEventSyncJob` (жорстке видалення рядка);
  - `PermitHistory*`, `DocumentPeriodReopen*`, `ClosedPeriodGuard`;
  - шлях запису PATCH (`PatchCellsHandler.EnsureSheetStillEditableAsync`, `AccessDecisionService.BuildContextAsync`).
- **Охоплено (вибірково, з повторною перевіркою кандидатів у коді):** `RowStore`, `RecalculationJob`, `AbandonedWorkSweeper`, `src/Ecr.Worker` (зокрема `Child`, `Isolation`), `TemplateVersionStore`, `05-rpt-views.sql`, зміни після 2026-10-09 (`DocumentStore`, `WorkflowStore`, `DocumentDeletionStore`, `UnitOfWork`, `CalculationResultStore`, `CollectionStore`, `12-archive-tables.sql`, міграції RS1…J01).
- **Не охоплено:** `09-seed.sql`/`seed/features/*.sql` (тексти UI); `AccessDecisionService` поза `BuildContextAsync`; `TemplateVersionStore` — лише SQL, LIKE і транзакція клонування.
- **Звірка з відомим:** для ключових ідентифікаторів кожної знахідки зроблено `git grep` по `docs/build/{KNOWN-ISSUES,OPEN-ITEMS,TODO-REMAINING,WORK-QUEUE,DBA-PROPOSALS}.md`, `docs/build/questions/`, `docs/build/audit-2026-10-09*/`, `docs/build/AUDIT-2026-10-09*.md`, `docs/tz/10-decisions.md`. Ідентифікатори: `PermitSegment`, `CellPermitPart`, `DocumentPeriodReopen`, `SheetCopy`, `TableDiscussion`, `MarkFailed`, `NextAttempt`, `FormulaRecalculation`, `CrossPeriodFanOut`, `usp_RestoreYear`, `RegistryValue_ValueRef`, `SourceEventAttributeValue`. Збігів, які б закривали знахідку, немає. Q-241 описує захоплення outbox, але не відступ між спробами.

## Зведена таблиця

| ID | Важливість | Категорія | Назва | Зусилля | Ризик зміни | Рішення PO |
|---|---|---|---|---|---|---|
| D-6 | High | data-integrity | Перенос версії: аркуш, який прибирається і має копії, падає на `FK_TableInstance_SheetCopy` (547) у T2, і проєкт застрягає під маркером 409 | S | низький | ні |
| D-7 | High | data-integrity | Перенос версії не переносить історію дозволів (`doc.PermitSegment`, `doc.CellPermitPart`): вона лишається на старих Id і зникає з документа | S | середній | ні |
| D-1 | Medium | concurrency | Історія дозволів: перевірки «дата після останнього сегмента», «розрив» і «частин немає» читають шкалу без замка й поза транзакцією запису | M | середній | ні |
| D-8 | Medium | correctness | `FormulaRecalculationJob`: відкладення чи збій періоду губить знайдені в прогоні періоди-читачі, і похідні значення лишаються застарілими | S | низький | ні |
| D-9 | Medium | reliability | Outbox: невдала спроба одразу повертається в `Pending`, кожен «відправити зараз» витрачає спробу, і лист стає `Failed` за хвилини замість 5 годин | M | низький | ні |
| D-10 | Medium | data-integrity | Перенос версії з мапою перейменування таблиць не перейменовує код у чаті таблиці й у передачах зрізу (`wf.EditAssignment`): ексклюзивність передачі мовчки зникає | M | середній | ні |
| D-2 | Low | concurrency | Відкриття/закриття винятку «місяць відкрито для документа» не бере замок періоду: розходиться з поверненням аркуша, подвійне відкриття дає 500 | S | низький | ні |
| D-3 | Low | data-integrity | `DeleteCopyAsync` лишає сиріт `doc.PermitSegment`/`doc.CellPermitPart` видаленої копії аркуша | S | низький | ні |
| D-11 | Low | data-integrity | `SourceEventSyncJob`: жорстке видалення рядка події лишає `doc.CellFormulaOverride` цього рядка | S | низький | ні |
| D-12 | Low | data-integrity | `usp_RestoreYear`: частини місяця не звіряються на кроці 2, і прибирання Z6-01 їх не прибирає | S | середній | ні |
| D-13 | Low | reliability | Outbox: результат успішної відправки фіксується зі скасовуваним токеном, і зупинка служби дає дубль листа | S | низький | ні |
| D-4 | Low | data-integrity | `SourceEventAttributeValueWriter`: дедуплікація `ToUpperInvariant` не збігається з рівністю колації БД, і MERGE падає | S | низький | ні |
| D-5 | Low | migration | Міграція `RV01_ValueRefIndex` будує індекс на `dic.RegistryValue` офлайн, без `ONLINE` і без guard | S | низький | ні |

---

### D-6 — High — Перенос версії: аркуш, який прибирається і має копії, ламає T2 (547), і проєкт застрягає під маркером
**Категорія:** data-integrity (міграція даних / доступність)
**Де:**
- `src/Ecr.Infrastructure/Persistence/DocumentVersionMigrationStore.cs:611` — `SingleAsync(VersionMigrationStages.Workflow, WorkflowSql)` (легка частина, транзакція T2);
- `:748` — `SingleAsync(VersionMigrationStages.Instances, InstancesSql)`: єдине місце, де видаляються `doc.TableInstance`;
- `:1865-1869` — `DELETE s FROM doc.DocumentSheet …` у `WorkflowSql`;
- `:1760-1765` — `DELETE ti` у `InstancesSql`;
- `src/Ecr.Infrastructure/Persistence/Migrations/20261103144721_RS1_SheetCopies.cs:203-210` — `FK_TableInstance_SheetCopy` (`doc.TableInstance.SheetCopyId → doc.DocumentSheet.Id`, без каскаду);
- планувальник `src/Ecr.Application/Documents/VersionMigration/VersionMigrationPlanner.cs:273-279` — зниклий аркуш стає звичайним `Removal`, копій планувальник не бачить (`git grep -i "SheetCopy\|CopyOf"` у `VersionMigration/` і в сховищі нічого не знаходить);
- відмови `src/Ecr.Application/Documents/VersionMigration/MigrateDocumentVersionHandler.cs:1410-1462`.

**Проблема:** `WorkflowSql` видаляє рядки складу документа для аркушів, яких немає в `@sheetMap`. Під умову потрапляють і копії, бо в копії той самий `SheetDefId`:
```sql
DELETE s
FROM   doc.DocumentSheet s
JOIN   @docs d ON d.Id = s.DocumentId
WHERE  @fromPeriod = 0
  AND  NOT EXISTS (SELECT 1 FROM @sheetMap m WHERE m.o = s.SheetDefId);
```
Екземпляри таблиць копії (`doc.TableInstance.SheetCopyId = <копія>`) на цей момент ще існують. Їх видаляє лише `InstancesSql`, який виконується пізніше (`:748`). Важка частина видаляє рядки й комірки, а екземпляри не чіпає. Результат — 547 на `FK_TableInstance_SheetCopy`.

**Сценарій відтворення:**
1. Проєкт на версії V1. Аркуш `S` має копію №2 (`doc.DocumentSheet.CopyOfId = <оригінал>`), у копії заповнена хоча б одна таблиця, тобто є `doc.TableInstance` із `SheetCopyId`.
2. Шаблон V2 не має аркуша `S`. Якщо `S` порожній, прибирання безпечне; якщо з даними, режим, що допускає втрату, пропускає його далі.
3. `POST …/migrate-version`:
   - T1 комітить маркер `migratingTo` (`MigrateDocumentVersionHandler.cs:874-880`);
   - важка частина комітить діапазони;
   - у T2 `WorkflowSql` → `SqlException 547` → T2 відкочується.
4. Фактичний результат:
   - маркер лишається, і всі читачі й писарі проєкту отримують `409 ECR-DOC-4091`;
   - повтор із тією ж ціллю проходить той самий порядок і падає так само;
   - перенос на іншу ціль відхиляється як `MigrationInProgress` (`:906-909`);
   - вихід — лише ручний SQL або RESTORE.

   Очікуваний результат: план або коректно прибирає копію, або відмовляє ДО T1. Правило: «відмова — нічого не лишає напівпереробленим» (коментар `DocumentVersionMigrationStore.cs:568-573`).

**Вплив:** увесь проєкт (усі документи) недоступний, доки адміністратор БД не втрутиться. Тригер — шаблон прибирає аркуш, у якого в будь-якому документі проєкту є копія з даними. Копії аркушів (RS1) з'явилися 03.11, а перенос версії існував раніше, тож ця взаємодія ніде не перевірена.

**Зміна — покроково:** обрано відмову на етапі плану з новою причиною `sheetCopiesNotMapped`. Каскадне видалення копій мовчки знищило б дані копій у таблицях, які переїжджають в інший аркуш (див. «Обґрунтування»).
1. Порт `src/Ecr.Application/Ports/IDocumentVersionMigrationStore.cs` — поруч із `CountDenyGrantsAsync` (`:171`):
   ```csharp
   /// <summary>
   /// D-6: скільки копій аркушів (<c>doc.DocumentSheet.CopyOfId IS NOT NULL</c>) у документах проєкту мають аркуш
   /// вихідної версії, якого цільова версія не має. Такий аркуш перенос не прибирає: копію спершу видаляють.
   /// </summary>
   public Task<int> CountSheetCopiesAsync(int projectId, IReadOnlyCollection<int> sheetDefIds, CancellationToken ct);
   ```
2. `src/Ecr.Infrastructure/Persistence/DocumentVersionMigrationStore.cs` — реалізація після `CountDenyGrantsAsync` (`:985`):
   ```csharp
   /// <inheritdoc />
   public async Task<int> CountSheetCopiesAsync(int projectId, IReadOnlyCollection<int> sheetDefIds, CancellationToken ct)
   {
       ArgumentNullException.ThrowIfNull(sheetDefIds);
       if (sheetDefIds.Count == 0)
       {
           return 0;
       }

       var ids = sheetDefIds.Distinct().ToList();
       return await (from s in db.DocumentSheets.AsNoTracking()
                     join d in db.Documents.AsNoTracking() on s.DocumentId equals d.Id
                     where d.ProjectId == projectId && s.CopyOfId != null && ids.Contains(s.SheetDefId)
                     select s.Id)
           .CountAsync(ct)
           .ConfigureAwait(false);
   }
   ```
   Перед правкою перевірити імена властивостей: `git grep -n "CopyOfId\|SheetDefId" src/Ecr.Domain/Entities/Documents/DocumentSheet.cs`.
3. `src/Ecr.Application/Documents/VersionMigration/MigrateDocumentVersionHandler.cs` — після блоку `grantsNotMapped` (`:1459-1462`):
   ```diff
            if (denied > 0)
            {
                refusals.Add("grantsNotMapped");
            }
   +
   +        // ⛔ D-6: аркуш, якого цільова версія не має, з копіями (RS1). WorkflowSql видаляє і копію, а її екземпляри
   +        // таблиць іще живі (FK_TableInstance_SheetCopy) — T2 падав би 547 ПІСЛЯ закомічених діапазонів і лишав маркер.
   +        var removedSheets = from.Sheets.Where(s => !s.IsDeleted && !plan.Sheets.ContainsKey(s.Id)).Select(s => s.Id).ToList();
   +        if (range.FromPeriodKey == 0
   +            && await store.CountSheetCopiesAsync(projectId, removedSheets, ct).ConfigureAwait(false) > 0)
   +        {
   +            refusals.Add("sheetCopiesNotMapped");
   +        }
   ```
   `range` — `RangeInfo(int FromPeriodKey, …)` (`MigrateDocumentVersionHandler.cs:1267`, `:1387`). `0` означає весь проєкт: саме тоді `WorkflowSql` видаляє склад (`@fromPeriod = 0`). У режимі «з місяця» склад не видаляється, тож відмова не потрібна.
4. Там само, у ланцюжку `messageKey` (`:1866-1881`) — гілка перед фінальним `migrateDataLoss`:
   ```diff
   -                                : dto.Refusals.Contains("mappingsNotMapped") && dto.LostValues == 0 && guardedValues == 0
   -                                    ? "err.ECR-SCHM-0422.migrateMappingsNotMapped"
   -                                    : "err.ECR-SCHM-0422.migrateDataLoss",
   +                                : dto.Refusals.Contains("mappingsNotMapped") && dto.LostValues == 0 && guardedValues == 0
   +                                    ? "err.ECR-SCHM-0422.migrateMappingsNotMapped"
   +                                    : dto.Refusals.Contains("sheetCopiesNotMapped")
   +                                        ? "err.ECR-SCHM-0422.migrateSheetCopiesNotMapped"
   +                                        : "err.ECR-SCHM-0422.migrateDataLoss",
   ```
   Оновити xml-коментар `Refusals` (`:26`): додати `sheetCopiesNotMapped`.
5. Тестові обгортки порту: `tests/Ecr.Api.Tests/DocumentVersionMigrationTests.BackgroundProgress.cs:144` і `tests/Ecr.Api.Tests/DocumentVersionMigrationTests.Race.cs:198`. Додати делегування `CountSheetCopiesAsync` до внутрішнього сховища — так само, як там делеговано `CountDenyGrantsAsync`.
6. Клієнт `src/Ecr.Web/src/features/documents/VersionMigrationDialog.tsx:29-52` (`refusalText`):
   ```diff
        case 'mappingsNotMapped':
          return t('documents.migrateRefusalMappingsNotMapped');
   +    case 'sheetCopiesNotMapped':
   +      return t('documents.migrateRefusalSheetCopiesNotMapped');
   ```
7. Сід — новий файл `src/Ecr.Infrastructure/Persistence/Sql/seed/features/MIGR-sheet-copies-refusal.sql` за зразком `COLL-p3-ent7c.sql` (секції `#I18N` ru/kz і `#UiEn` en, `Scope = 1`). Ключі й тексти:
   - `err.ECR-SCHM-0422.migrateSheetCopiesNotMapped`:
     - en: «The new version has no sheet that has copies in this project's documents. Delete those sheet copies first, then move the project.»
     - ru: «В новой версии нет листа, у которого в документах проекта есть копии. Сначала удалите эти копии листа, затем переносите проект.»
     - kz: «Жаңа нұсқада жоба құжаттарында көшірмелері бар парақ жоқ. Алдымен парақтың осы көшірмелерін жойыңыз, содан кейін жобаны көшіріңіз.»
   - `documents.migrateRefusalSheetCopiesNotMapped`:
     - en: «A removed sheet has copies in documents»
     - ru: «У удаляемого листа есть копии в документах»
     - kz: «Жойылатын парақтың құжаттарда көшірмелері бар»
8. Контракт `docs/build/02-contracts.md` — у таблиці ключів поруч із `:4369` (`migrateGrantsNotMapped`) новий рядок: `err.ECR-SCHM-0422.migrateSheetCopiesNotMapped | 422 | на аркуші, якого в новій версії за кодом нема, є копії (RS1) в документах проєкту (refusals має sheetCopiesNotMapped)`; у переліку значень `refusals` — `sheetCopiesNotMapped`. `openapi.snapshot.json`/`schema.d.ts` не змінюються (`refusals` — `string[]`).

**Тест-доказ:**
- `tests/Ecr.Api.Tests/DocumentVersionMigrationTests.SheetCopies.cs` (нова частина `partial class DocumentVersionMigrationTests`), кейс `Removed_sheet_with_copies_is_refused_before_marker`. Кроки: V1 з аркушами A і S; документ; копія S через API копій (як у `DocumentSheetCopyApiTests`) зі значенням у таблиці копії; V2 без S. Сухий прогін — `refusals` містить `sheetCopiesNotMapped`; застосування — `422` з ключем `migrateSheetCopiesNotMapped`; маркер `doc.Project.MigratingToVersionId IS NULL`; версія проєкту не змінилась.
- Без кроку 3 перший прогін дає 500/помилку задачі і маркер стоїть: це і є відтворення дефекту.
- Другий кейс `Removed_sheet_without_copies_still_migrates` — регресія: S без копій переноситься.

**Перевірка після впровадження:**
- `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~DocumentVersionMigrationTests"`;
- `npx vitest run src/features/documents/__tests__/VersionMigrationAction.test.tsx`;
- `npm run lint`, `npx tsc --noEmit`;
- сторож каталогу ключів (`SeedTextUpdateTests` / каталожні сторожі Architecture);
- стенд: діалог переносу на проєкті з копією аркуша, якого нема в цільовій версії, — підпис відмови замість кнопки «Перенести».

**Ризики і побічні ефекти:** +1 запит у плані переносу (адмін-дія, стелі звернень для плану немає — перевірити `git grep -n "QueryCount" tests/Ecr.Api.Tests/DocumentVersionMigrationTests*.cs`). API сумісний: нова причина — нове значення рядка. Наявні застряглі проєкти (якщо вже траплялось) це не розблокує — для них потрібен runbook.

**Обґрунтування:** альтернатива «перенести `DELETE doc.DocumentSheet` після `InstancesSql`» не рятує, коли таблиця аркуша-копії переїжджає мапою в інший аркуш: екземпляр копії лишається (`TableDefId` перемаплено), а копії вже немає — той самий 547. Каскадне видалення копії з даними — втрата, яку план не показує. Явна відмова відповідає вже прийнятому шаблону (`grantsNotMapped`, `bindingsNotMapped`).

**Рішення PO:** не потрібне. Варіант «переносити копію разом з аркушем» — окрема функція, не виправлення.
**Впевненість:** порядок і FK підтверджено читанням; частоту (чи вже є такі проєкти) треба перевірити на стенді.
**Залежності:** робити в одній гілці з D-7 і D-10 (ті самі файли).

---

### D-7 — High — Перенос версії не переносить історію дозволів (J01)
**Категорія:** data-integrity
**Де:**
- `src/Ecr.Infrastructure/Persistence/DocumentVersionMigrationStore.cs` — `doc.PermitSegment`/`doc.CellPermitPart` не згадано ніде (`git grep -n "PermitSegment\|CellPermitPart" -- src/Ecr.Infrastructure/Persistence/DocumentVersionMigrationStore.cs src/Ecr.Application/Documents/VersionMigration` — порожньо). `WorkflowSql` — `:1842-1876`, прелюдія з мапами — `:1466-1505`.
- Читачі за Id ПОТОЧНОЇ версії:
  - `src/Ecr.Infrastructure/Persistence/PermitHistoryStore.cs:22-37` (`TableDefId == tableDefId && ColumnDefId == columnDefId` / `HeaderFieldDefId`), `:66-74` (`CellPartsAsync`);
  - виклики: `src/Ecr.Application/Permits/History/PermitHistoryHandlers.cs:62,240`, `SaveCellSplitsHandler.cs:112`.
- `FK_CellPermitPart_Table` → `TableDef` (`Configurations/PermitHistoryConfiguration.cs:91-92`).

**Проблема:** перенос перемаплює `TableInstance.TableDefId`, комірки (`ColumnDefId`) і шапку (`HeaderFieldDefId`, `HeaderSql:1795-1806`). Сегменти «дозвіл від дати» і частини місяця лишаються на `TableDefId`/`ColumnDefId`/`HeaderFieldDefId` ВИХІДНОЇ версії. Після переносу жоден читач їх не знаходить.

**Сценарій відтворення:**
1. Документ: у рядку «дозвіл від 15.03» (сегмент), березень поділено на частини A+B (`doc.CellPermitPart`).
2. Перенос проєкту на V2, де ті самі таблиця й колонки (нові Id).
3. Фактичний результат:
   - історія дозволу рядка порожня;
   - валідація покриття дозволом (`PermitCoverageIssues`) і перевірка чинності в PATCH (`PatchCellsHandler.PermitValidity.cs`) працюють, ніби поділу не було;
   - користувач може знову додати «дозвіл від 15.03» (унікальний індекс не спрацює — інші Id);
   - старі рядки лишаються назавжди і тримають `FK_CellPermitPart_Table` на старій `TableDef`.

   Очікуваний результат: дані документа переїжджають за кодом (правило ФВ-7.2, коментар `VersionMigrationPlanner.cs:52-55`).

**Вплив:** кожен документ з історією дозволів при кожному переносі версії. Тихе відʼєднання даних, які впливають на розрахунок місячних підсумків за дозволами. Функція J01 зʼявилась 09.11, після коду переносу, тож перенос її не знає.

**Зміна — покроково:** окремий крок у ТІЙ САМІЙ транзакції T2, наприкінці `WorkflowSql`. Мапи `@tableMap`/`@columnMap`/`@headerMap`/`@periods` уже оголошені прелюдією. Нових параметрів і кроків прогресу не треба.

`src/Ecr.Infrastructure/Persistence/DocumentVersionMigrationStore.cs:1871-1876`:
```diff
         UPDATE s
         SET    s.SheetDefId = m.n
         FROM   doc.DocumentSheet s
         JOIN   @docs d     ON d.Id = s.DocumentId
         JOIN   @sheetMap m ON m.o = s.SheetDefId;
+
+        -- ⛔ D-7: історія дозволів (J01) адресована Id таблиці/колонки/поля шапки ВЕРСІЇ — переїжджає за тими самими
+        -- мапами, що й комірки та шапка. Таблиця/колонка/поле без відповідника: історія йде разом із їхніми значеннями.
+        -- Частини місяця партиціоновані — лише періоди проєкту (@periods, як екземпляри). Шапка й сегменти — не
+        -- періодні (як HeaderSql).
+        DELETE p
+        FROM   doc.CellPermitPart p
+        JOIN   @docs d ON d.Id = p.DocumentId
+        WHERE  p.PeriodKey IN (SELECT PeriodKey FROM @periods)
+          AND  NOT EXISTS (SELECT 1 FROM @columnMap c WHERE c.o = p.ColumnDefId);
+
+        UPDATE p
+        SET    p.TableDefId = c.t, p.ColumnDefId = c.n
+        FROM   doc.CellPermitPart p
+        JOIN   @docs d      ON d.Id = p.DocumentId
+        JOIN   @columnMap c ON c.o = p.ColumnDefId
+        WHERE  p.PeriodKey IN (SELECT PeriodKey FROM @periods);
+
+        DELETE g
+        FROM   doc.PermitSegment g
+        JOIN   @docs d ON d.Id = g.DocumentId
+        WHERE  (g.ColumnDefId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM @columnMap c WHERE c.o = g.ColumnDefId))
+           OR  (g.HeaderFieldDefId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM @headerMap h WHERE h.o = g.HeaderFieldDefId));
+
+        UPDATE g
+        SET    g.TableDefId = c.t, g.ColumnDefId = c.n
+        FROM   doc.PermitSegment g
+        JOIN   @docs d      ON d.Id = g.DocumentId
+        JOIN   @columnMap c ON c.o = g.ColumnDefId;
+
+        UPDATE g
+        SET    g.HeaderFieldDefId = h.n
+        FROM   doc.PermitSegment g
+        JOIN   @docs d      ON d.Id = g.DocumentId
+        JOIN   @headerMap h ON h.o = g.HeaderFieldDefId;
         """;
```
Пояснення до коду:
- `@columnMap (o, n, t)` уже несе цільову таблицю (`t`, прелюдія `:1481-1482`, параметр `@columns` — `:401`), тож `TableDefId` береться з неї. Це правильно і для таблиці, перейменованої мапою (D-10).
- `@columnMap` містить КОЖНУ колонку з відповідником: `VersionMigrationPlanner.cs:600` — `map.Add(new VersionMigrationColumn(column.Id, other.Id, to.Id))` для кожної колонки, що не стала `Removal`. Тому `NOT EXISTS @columnMap` означає саме «колонку прибрано».

Решта:
- `RowKey` і `SheetCopyId` не змінюються.
- Унікальні індекси (`UQ_PermitSegment_Row`, `UQ_CellPermitPart_Cell`) не зіткнуться: мапа колонок 1:1, а `RenameConflict` відхиляється планувальником.
- `CK_PermitSegment_Target` не порушується.
- Аудит T2 (`aud.StructureChange` переносу) уже фіксує перенос. Окремого запису на кожен сегмент не треба.
- **Режим «з місяця»** (прапор, типово вимкнений): частини за періоди до X лишаються на старих Id разом зі своїми екземплярами, а сегменти перемаплюються — це та сама межа, що вже діє для шапки (`HeaderSql` не періодний). Зафіксувати це в коментарі.

**Тест-доказ:** `tests/Ecr.Api.Tests/DocumentVersionMigrationTests.PermitHistory.cs`, кейс `Permit_history_and_month_parts_follow_columns_to_target_version`.
- Кроки: документ із колонкою дозволу; `POST …/permit-history` (додати рядковий сегмент); `SaveCellSplits` на місяць; шапковий сегмент. Далі перенос на V2 із тими самими кодами, після чого `GET …/permit-history` повертає ті самі сегменти й частини; `SELECT COUNT(*) FROM doc.PermitSegment WHERE ColumnDefId IN (<колонки V1>)` = 0.
- Без зміни історія порожня, а рядки лишаються на колонках V1.
- Другий кейс: V2 без колонки дозволу — сегменти й частини цієї колонки видалено, решта документа переноситься.

**Перевірка після впровадження:**
- `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~DocumentVersionMigrationTests|FullyQualifiedName~DocumentPermitHistoryTests"`;
- upgrade-перевірка не потрібна: схема не змінюється;
- стенд: перенос проєкту з поділеним місяцем → діалог історії дозволів після переносу.

**Ризики і побічні ефекти:** +5 операторів у вже наявній команді `WorkflowSql` (той самий round-trip). `doc.CellPermitPart` — за `@periods`, із відсіканням партицій. Блокування — у T2, яка вже тримає структуру документів виключно.
**Обґрунтування:** найменша зміна в точці, де вже перемаплюється все, що адресоване Id версії. Альтернативу «зберігати історію за кодами» відкинуто: це міняє схему J01 і всі читачі.
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням (відсутність переносу і повнота `@columnMap`).
**Залежності:** разом з D-6 і D-10.

---

### D-1 — Medium — Історія дозволів: check-then-write без замка
**Категорія:** concurrency
**Де:**
- `src/Ecr.Application/Permits/History/PermitHistoryHandlers.cs`:
  - add рядка — `:60-64` (читання `RowSegmentsAsync` + `EnsureDate` поза транзакцією), `:123-133` (транзакція лише навколо `store.Add`);
  - undo рядка — `:166-182` (читання, `RowHasPartsOfAsync`) і `:186-196`;
  - add шапки — `:240-241` / `:301-303`;
  - undo шапки — `:365` / `:377-379`.
- `src/Ecr.Application/Permits/History/PermitHistorySupport.cs:158-164` (`EnsureDate`).
- `src/Ecr.Infrastructure/Persistence/PermitHistoryStore.cs:22-37` (читання без блокування), `:56-61` (`RowHasPartsOfAsync`, `AsNoTracking`).
- `src/Ecr.Application/Permits/History/SaveCellSplitsHandler.cs:74` (сегменти читаються до транзакції `:90`), `:109-114`.
- `Configurations/PermitHistoryConfiguration.cs:40-43` — `UQ_PermitSegment_Row (…, ValidFrom)` ловить лише ОДНАКОВУ дату. `2601` на ньому не має арму в `src/Ecr.Api/Errors/ExceptionHandlingMiddleware.cs`, тож гонка на одну дату дає голий 500.

**Проблема:** правила «нова дата пізніша за останній сегмент», «немає розриву з попереднім дозволом», «не той самий дозвіл» і «undo лише без частин місяця» рахуються від шкали, прочитаної без замка до транзакції:
```csharp
var segments = await store
    .RowSegmentsAsync(documentId, row.Table.Id, row.Instance.SheetCopyId, row.RowKey, row.Column.Id, ct)
    .ConfigureAwait(false);
PermitHistoryRules.EnsureDate(request.ValidFrom, window, segments);
...
await uow.ExecuteInTransactionAsync(async innerCt =>
{
    store.Add(segment);
```
Жодна з цих операцій не бере замок аркуша чи періоду.

**Сценарій відтворення:**
1. Рядок має базовий дозвіл P0 без сегментів. A шле `AddRowPermit(ValidFrom=01.06, P1)`, B одночасно — `(01.03, P2)`.
2. Обидва читають `[]` і проходять `EnsureDate` і `GapDays` проти P0.
3. Обидва коміти успішні (дати різні). Шкала стала P0 → P2 → P1, а P1 перевірено проти P0: розрив P2→P1 не перевірено. В аудиті P1 записано `previous = P0` — неправда.
4. Гонка undo ↔ поділ:
   - `UndoRowPermit` бачить `RowHasPartsOfAsync == false`;
   - `SaveCellSplits` (сегменти прочитані на `:74` до своєї транзакції) вставляє частини з дозволом цього сегмента;
   - undo видаляє сегмент.

   У результаті частини місяця посилаються на дозвіл, якого в шкалі вже немає.
5. Однакова дата від двох користувачів → `2601 UQ_PermitSegment_Row` → 500.

**Вплив:** рідкісна гонка двох редакторів однієї комірки дозволу. Наслідок — тиха неузгодженість шкали й частин, а також неправдивий журнал.

**Зміна — покроково:** оптимістична перевірка під замком. Валідація лишається, де є; у транзакції запису шкала перечитується з `UPDLOCK, HOLDLOCK` і має збігатися з тією, на якій перевіряли. Інакше — `409 ECR-CELL-0409` (наявний ключ `err.ECR-CELL-0409.concurrentChange`, нових рядків UI немає).
1. Порт `src/Ecr.Application/Ports/IPermitHistoryStore.cs` — після `HeaderSegmentsAsync` (`:24`):
   ```csharp
   /// <summary>D-1: сегменти рядка під UPDLOCK+HOLDLOCK до кінця транзакції (викликати лише в транзакції).</summary>
   public Task<IReadOnlyList<PermitSegment>> LockRowSegmentsAsync(
       long documentId, int tableDefId, long? sheetCopyId, string rowKey, int columnDefId, CancellationToken ct);

   /// <summary>D-1: сегменти поля шапки під UPDLOCK+HOLDLOCK до кінця транзакції.</summary>
   public Task<IReadOnlyList<PermitSegment>> LockHeaderSegmentsAsync(long documentId, int headerFieldDefId, CancellationToken ct);
   ```
2. `src/Ecr.Infrastructure/Persistence/PermitHistoryStore.cs` — після `HeaderSegmentsAsync`. Зразок — `TableDiscussionStore.cs:267`, `DocumentKeyStore.cs:19`:
   ```csharp
   /// <inheritdoc />
   public async Task<IReadOnlyList<PermitSegment>> LockRowSegmentsAsync(
       long documentId, int tableDefId, long? sheetCopyId, string rowKey, int columnDefId, CancellationToken ct)
       => await (sheetCopyId is { } copy
               ? db.PermitSegments.FromSql($"""
                   SELECT * FROM doc.PermitSegment WITH (UPDLOCK, HOLDLOCK)
                    WHERE DocumentId = {documentId} AND TableDefId = {tableDefId} AND SheetCopyId = {copy}
                      AND RowKey = {rowKey} AND ColumnDefId = {columnDefId}
                   """)
               : db.PermitSegments.FromSql($"""
                   SELECT * FROM doc.PermitSegment WITH (UPDLOCK, HOLDLOCK)
                    WHERE DocumentId = {documentId} AND TableDefId = {tableDefId} AND SheetCopyId IS NULL
                      AND RowKey = {rowKey} AND ColumnDefId = {columnDefId}
                   """))
           .OrderBy(s => s.ValidFrom)
           .ToListAsync(ct)
           .ConfigureAwait(false);

   /// <inheritdoc />
   public async Task<IReadOnlyList<PermitSegment>> LockHeaderSegmentsAsync(long documentId, int headerFieldDefId, CancellationToken ct)
       => await db.PermitSegments.FromSql($"""
               SELECT * FROM doc.PermitSegment WITH (UPDLOCK, HOLDLOCK)
                WHERE DocumentId = {documentId} AND HeaderFieldDefId = {headerFieldDefId}
               """)
           .OrderBy(s => s.ValidFrom)
           .ToListAsync(ct)
           .ConfigureAwait(false);
   ```
   Дві гілки замість `SheetCopyId = @p OR …` потрібні, щоб діапазонне блокування сіло на seek фільтрованого `UQ_PermitSegment_Row`.
3. `src/Ecr.Application/Permits/History/PermitHistorySupport.cs` — у `PermitHistoryRules` (`:142`), поруч із `NothingToUndo` (`:186`):
   ```csharp
   /// <summary>
   /// D-1: шкала, на якій перевіряли (поза транзакцією), мусить збігтися з шкалою під замком. Інакше між перевіркою й
   /// записом шкалу змінив хтось інший — 409, клієнт перечитує історію.
   /// </summary>
   public static void EnsureUnchanged(IEnumerable<long> checkedOn, IReadOnlyList<PermitSegment> locked)
   {
       if (!checkedOn.SequenceEqual(locked.Select(s => s.Id)))
       {
           throw new ConcurrencyConflictException(
               ErrorCodes.CellConflict,
               "Історію дозволу щойно змінив хтось інший: оновіть її й повторіть.",
               new Dictionary<string, object?> { ["messageKey"] = "err.ECR-CELL-0409.concurrentChange" });
       }
   }
   ```
   Простори імен `Ecr.Application.Errors` і `Ecr.Domain.Errors` у файлі вже підключено.
4. `PermitHistoryHandlers.cs`, add рядка (`:123-125`):
   ```diff
            await uow.ExecuteInTransactionAsync(async innerCt =>
            {
   +            // ⛔ D-1: та сама шкала, на якій рахувались дата й розрив, — під UPDLOCK+HOLDLOCK до коміту.
   +            PermitHistoryRules.EnsureUnchanged(
   +                segments.Select(s => s.Id),
   +                await store.LockRowSegmentsAsync(
   +                        documentId, row.Table.Id, row.Instance.SheetCopyId, row.RowKey, row.Column.Id, innerCt)
   +                    .ConfigureAwait(false));
                store.Add(segment);
   ```
   Undo рядка (`:186-188`):
   ```diff
            await uow.ExecuteInTransactionAsync(async innerCt =>
            {
   +            PermitHistoryRules.EnsureUnchanged(
   +                segments.Select(s => s.Id),
   +                await store.LockRowSegmentsAsync(
   +                        documentId, row.Table.Id, row.Instance.SheetCopyId, row.RowKey, row.Column.Id, innerCt)
   +                    .ConfigureAwait(false));
   +
   +            // ⛔ D-1: частини місяця — повторно ПІД замком (поділ бере той самий замок, див. SaveCellSplitsHandler).
   +            if (await store.RowHasPartsOfAsync(
   +                    documentId, row.Table.Id, row.Instance.SheetCopyId, row.RowKey, last.RegistryEntryId, innerCt)
   +                    .ConfigureAwait(false))
   +            {
   +                throw PermitHistoryRules.NothingToUndo();
   +            }
   +
                store.Remove(last);
   ```
   Шапка: те саме в `AddHeaderPermitHandler` (`:301-303`) і `UndoHeaderPermitHandler` (`:377-379`) — `LockHeaderSegmentsAsync(documentId, header.Field.Id, innerCt)` + `EnsureUnchanged(segments.Select(s => s.Id), …)`.
5. `src/Ecr.Application/Permits/History/SaveCellSplitsHandler.cs`:
   - (а) `Plan` (`:276`) — три поля в кінець: `IReadOnlyList<long> SegmentIds, int? PermitColumnId, int? HeaderFieldId`.
   - (б) У `PlanAsync` в рядковій гілці (`rowSegments.Count == 1`) запамʼятати `var used = rowSegments[0];`, у шапковій — `header`. У `return new Plan(…)` (`:265`) передати `SegmentIds: [.. used.OrderBy(s => s.ValidFrom).Select(s => s.Id)]`, а також `PermitColumnId: used.Key` (рядок) / `HeaderFieldId: header.Key` (шапка).
   - (в) У транзакції (`:90`) ПІСЛЯ блоку `patchCells` і ПЕРЕД `foreach (var plan in plans)` (`:109`):
   ```csharp
   // ⛔ D-1: шкала кожного плану — під UPDLOCK+HOLDLOCK і та сама, на якій план рахувався; undo/add чекають на нас
   // або ми на них. Порядок — за ключем, щоб два поділи з різним порядком комірок не взяли замки навхрест.
   foreach (var plan in plans
                .OrderBy(p => p.Table.Id).ThenBy(p => p.Instance.SheetCopyId ?? 0)
                .ThenBy(p => p.RowKey, StringComparer.Ordinal).ThenBy(p => p.PermitColumnId ?? p.HeaderFieldId))
   {
       var locked = plan.PermitColumnId is { } permitColumn
           ? await store.LockRowSegmentsAsync(
               documentId, plan.Table.Id, plan.Instance.SheetCopyId, plan.RowKey, permitColumn, innerCt).ConfigureAwait(false)
           : await store.LockHeaderSegmentsAsync(documentId, plan.HeaderFieldId!.Value, innerCt).ConfigureAwait(false);
       PermitHistoryRules.EnsureUnchanged(plan.SegmentIds, locked);
   }
   ```
   (`PermitHistoryRules` — `internal` у тій самій збірці `Ecr.Application`.)

**Тест-доказ:**
- Детермінований: `tests/Ecr.Infrastructure.Tests/Persistence/PermitHistoryStoreLockTests.cs` (новий, `[Collection("SqlServer")]`), кейс `LockRowSegments_blocks_second_locker_until_commit`. Сесія 1 у транзакції викликає `LockRowSegmentsAsync` для порожньої шкали. Сесія 2 з `SET LOCK_TIMEOUT 200` робить той самий виклик → `SqlException 1222`; виклик для ІНШОГО `RowKey` проходить.
- Без `WITH (UPDLOCK, HOLDLOCK)` сесія 2 проходить одразу.
- API: `tests/Ecr.Api.Tests/DocumentPermitHistoryTests.cs`, кейс `AddRowPermit_after_concurrent_change_returns_409`. Підмінити `IPermitHistoryStore` декоратором, що між `RowSegmentsAsync` і `LockRowSegmentsAsync` вставляє сегмент через окремий контекст. Очікування — `409 err.ECR-CELL-0409.concurrentChange`; без кроку 4 буде `200` і два сегменти.
- Правило CLAUDE.md для тестів на замки: 20/20 локально.

**Перевірка після впровадження:**
- `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~PermitHistoryStoreLockTests"`;
- `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~DocumentPermitHistoryTests"`;
- переконатися планом (`SET STATISTICS XML ON` у тесті або вручну), що читання бере seek по `UQ_PermitSegment_Row` / `UQ_PermitSegment_Header`.

**Ризики і побічні ефекти:**
- +1 звернення на add/undo і +1 на кожну комірку поділу (≤ 500 на запит, `MaxItems`). Тестів зі стелею звернень для цих маршрутів немає (`git grep -n -i querycount tests/Ecr.Api.Tests/DocumentPermitHistoryTests*.cs` — порожньо).
- Новий ресурс блокування беруть лише ці обробники. У поділі він береться ПІСЛЯ замків аркуша/періоду, що їх бере `patchCells`, тож інверсії немає.
- Тіло `ExecuteInTransactionAsync` повторюване: читання під замком — у тілі.

**Обґрунтування:** перенесення всієї валідації (з `EnsureFreshOnMismatchAsync` і довідниками) в лямбду транзакції — більший і ризикованіший diff. Перевірка «шкала не змінилась» під замком дає ту саму гарантію: запис відбувається лише на тій шкалі, на якій перевіряли.
**Рішення PO:** не потрібне.
**Впевненість:** гонку підтверджено читанням. Вибір seek-плану перевірити на стенді або тестом.
**Залежності:** немає (з D-3 файлів не ділить).

---

### D-8 — Medium — `FormulaRecalculationJob`: відкладення губить ланцюжок міжперіодних читачів
**Категорія:** correctness
**Де:**
- `src/Ecr.Infrastructure/Jobs/FormulaRecalculationJob.cs`:
  - `:116` — `JobDeferredException? deferral = null;`;
  - `:167-190` — дві гілки `deferral ??= …; continue;`;
  - `:201-222` — читачі дописуються в `work` лише в памʼяті;
  - `:225-228` — `throw deferral`.
- `src/Ecr.Infrastructure/Jobs/JobWorker.cs:527-531`, `:594-596` — повтор виконує той самий `job.PayloadJson` (`DeferAsync`).
- `src/Ecr.Application/Recalculation/RecalculationService.cs:532-538` (насіння завжди потрапляє в `changed`), `:1086-1100` (похідні — лише записані), `~:1461` (`CellValueComparison.AreEqual(stored…, data)` — незмінене значення не записується).
- `src/Ecr.Infrastructure/Jobs/CrossPeriodFanOut.cs:34` («ланцюжок завершується сам: прогін-читач записує лише ЗМІНЕНЕ»).

**Проблема:** з L2 (одна задача на документ) період-читач, знайдений у прогоні (`work[reader.Value] = …`), існує лише в памʼяті. Якщо його період відкладено (подання аркуша або вичерпане очікування блокування), задача повертається в чергу з ПОЧАТКОВИМ payload, де є тільки насіння. На повторі насіннєвий період уже записаний: похідні колонки не змінюються і в `changed` не потрапляють, тож читач, який читає похідну колонку, більше не знаходиться.

**Сценарій відтворення:**
1. P1 (січень) — правка `A`. Формула `Total = SUM(A)`. P2 (лютий) має `X = [Period:#1].Total`.
2. Прогін:
   - P1 рахується, `Total` змінено, а `FindReadersAsync` додає P2 із джерелом `(P1, Total)`;
   - P2 кидає `sheetBeingSubmitted` (аркуш лютого подається) → `deferral`;
   - наприкінці — `throw deferral`.
3. Повтор (той самий payload: лише насіння P1): P1 перераховується без змін, `changed = {A}`. P2 читає `Total`, а не `A`, тож у `work` не потрапляє.
4. Фактичний результат: `X` лютого лишається старим без жодної помилки — до наступного повного перерахунку. Очікуваний результат: каскад доходить до всіх читачів (WW-03).
5. Те саме при будь-якому непередбаченому винятку в пізнішому періоді: ранні періоди вже закомічені, а ретрай отримує той самий payload.

**Вплив:** документи з міжперіодними формулами (`[Period:-1]`, `[Period:#k]`) під час подань і довгих операцій — тобто наприкінці періоду, коли це найважливіше. Застарілі похідні значення.

**Зміна — покроково:**
1. `src/Ecr.Infrastructure/Jobs/CrossPeriodFanOut.cs` — новий метод після `EnqueueReadersAsync`. Тіло — те саме, що цикл `:86-105`, тільки для вже знайдених джерел:
   ```csharp
   /// <summary>
   /// D-8: ставить у чергу період-читач із ВЖЕ знайденими джерелами (задача документа, що відкладається чи падає, не
   /// може віддати їх у власний payload — повтор бачить лише насіння).
   /// </summary>
   public async Task EnqueueSourcesAsync(
       long documentId, PeriodKey reader, IReadOnlyCollection<CrossPeriodSource> sources, int hops, CancellationToken ct)
   {
       ArgumentNullException.ThrowIfNull(sources);
       if (sources.Count == 0)
       {
           return;
       }

       var cells = sources
           .Select(s => new DirtyCell(
               0, s.ColumnDefId, s.TableDefId, s.SourceSequence, hops,
               OwnerInput: s.OwnerInput ? true : null, PeriodKey: reader.Value))
           .ToList();

       await jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(
               FormulaRecalculationTarget.Of(documentId, reader.Value, createdByUserId: null),
               new { DocumentId = documentId, TableInstanceId = 0L, PeriodKey = reader.Value, Cells = cells },
               ct)
           .ConfigureAwait(false);
   }
   ```
2. `src/Ecr.Infrastructure/Jobs/FormulaRecalculationJob.cs`:
   - після `:116` додати `var postponed = new List<(int Slot, PeriodWork Item)>();`;
   - в обох catch (`:175-177` і `:184-189`) перед `continue;` додати `postponed.Add((slot, item));`;
   - `:225-228`:
   ```diff
            if (deferral is not null)
            {
   +            // ⛔ D-8: повтор отримає ЛИШЕ початковий payload; читачі, знайдені в цьому прогоні, — окремою постановкою.
   +            await RequeueSourcesAsync(documentId, postponed, ct).ConfigureAwait(false);
                throw deferral;
            }
   ```
   - збій посеред проходу: обгорнути цикл `while (work.Count > 0 && passes < MaxPasses) { … }` (`:128-223`) так:
   ```csharp
   (int Slot, PeriodWork Item)? current = null;
   try
   {
       while (work.Count > 0 && passes < MaxPasses)
       {
           var (slot, item) = work.First();
           work.Remove(slot);
           current = (slot, item);
           // … тіло без змін …
       }
   }
   catch (Exception ex) when (ex is not OperationCanceledException)
   {
       // ⛔ D-8: ретрай отримає лише насіння; поточний і ще не оброблені періоди-читачі — окремою постановкою.
       var pending = postponed.Concat(work.Select(p => (p.Key, p.Value)));
       await RequeueSourcesAsync(documentId, current is { } c ? pending.Append(c) : pending, ct).ConfigureAwait(false);
       throw;
   }
   ```
   - новий приватний метод:
   ```csharp
   private async Task RequeueSourcesAsync(
       long documentId, IEnumerable<(int Slot, PeriodWork Item)> pending, CancellationToken ct)
   {
       if (fanOut is null || documentId == 0)
       {
           return;
       }

       foreach (var (slot, item) in pending.Where(p => p.Item.Sources.Count > 0))
       {
           await fanOut.EnqueueSourcesAsync(documentId, new PeriodKey(slot), item.Sources, item.Hops, ct).ConfigureAwait(false);
       }
   }
   ```

**Тест-доказ:** `tests/Ecr.Infrastructure.Tests/Jobs/FormulaRecalculationLockWaitDeferralTests.cs` (наявний клас), новий кейс `Deferred_reader_period_is_requeued_with_its_sources`.
- Документ із P1 → P2 (`X = [Period:#1].Total`). Утримати замок даних P2 тим самим способом, що в першому тесті класу. Задача з насінням P1 → `JobDeferredException`.
- Очікування: у `itg.JobProgress` є Queued-задача `doc{id}-formula`, у payload якої є елемент `{ PeriodKey = P2, TableDefId, SourcePeriod = 1 }`. Після звільнення замка й виконання обох задач `X == Total`.
- Без кроку 2 Queued-задачі з джерелом немає, і `X` лишається старим.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~FormulaRecalculation|FullyQualifiedName~CrossPeriod"`; `tests/Ecr.Api.Tests/FormulaJobFixedCostTests.cs` (сталі звернення задачі формул) лишається зеленим — нові постановки є лише на шляху відкладення чи збою.
**Ризики і побічні ефекти:**
- Постановка злиттям (`EnqueueCoalescedAsync`), тож дублікати зливаються.
- Якщо ціль відкладеної задачі збігається (`doc{id}-formula`, системна постановка), `DeferAsync` зливає масив із новою Queued-задачею (`DbJobQueue.cs:461-465`).
- Зайвий прохід уже обробленого періоду нічого не пише.
**Обґрунтування:** змінити payload задачі, що повертається, `JobWorker` не дає: це торкалося б черги, Quartz-режиму й `JobDeferral`. Окрема постановка використовує вже наявний механізм (`EnqueueReadersAsync`).
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням (ланцюжок `changed` → `FindReadersAsync`).
**Залежності:** немає.

---

### D-9 — Medium — Outbox: спроби витрачаються на кожен «відправити зараз», без відступу
**Категорія:** reliability
**Де:**
- `src/Ecr.Infrastructure/Integration/OutboxDispatcher.cs`:
  - `:99-105` (`MaxAttempts = 5`, «п'ять спроб — це п'ять годин»);
  - `:317-325` (`ClaimBatchAsync`: усі `Pending`, без фільтра часу);
  - `:224-227`, `:270` (`MarkFailed`);
  - `:385-401` (`PersistAsync`).
- `src/Ecr.Domain/Entities/Integration/NotificationOutbox.cs:130-137` (`MarkFailed`: `State = Attempts >= maxAttempts ? "Failed" : "Pending"` — одразу знову доступний для захоплення).
- `src/Ecr.Application/Workflow/SheetWorkflowNotifier.cs:321` — кожне подання чи повернення аркуша ставить `IOutboxFlushJob`.
- `src/Ecr.Infrastructure/Jobs/OutboxFlushJob.cs:23-29`.
- `src/Ecr.Infrastructure/Persistence/Configurations/ExternalConfiguration.cs:574-597`.

**Проблема:** часу останньої спроби немає. Невдалий рядок стає `Pending` і доступний будь-якому наступному `FlushAsync`. Злиття (`EnqueueCoalescedAsync`) зливає лише постановки, що перетинаються в часі, тож п'ять послідовних подань дають п'ять прогонів.

**Сценарій відтворення:** SMTP недоступний 10 хвилин перед дедлайном. За ці хвилини п'ять подань аркушів дають п'ять прогонів `OutboxFlushJob`. Кожен пробує всі `Pending`-листи аркушів, і після п'ятого всі вони `Failed`. Пошта відновилась, але `Failed` ніхто не повторює: листи «подано на погодження» втрачено. Очікувано — п'ять спроб із проміжком приблизно годину (`:99-105`).

**Вплив:** погоджувачі не отримують листів про подання саме в пікові дні. Тихо — лише рядок `Failed` у сторінці обслуговування.

**Зміна — покроково:**
1. `src/Ecr.Domain/Entities/Integration/NotificationOutbox.cs` — властивість після `ClaimedAt` (`:72`) і зміна `MarkSent`/`MarkFailed`:
   ```csharp
   /// <summary>D-9: не раніше якого моменту (UTC) повторювати невдалу спробу; <c>null</c> — будь-коли.</summary>
   public DateTime? NextAttemptAt { get; private set; }
   ```
   ```diff
        public void MarkSent(DateTime utcNow)
        {
            State = "Sent";
            SentAt = utcNow;
            Error = null;
            Attempts++;
   +        NextAttemptAt = null;
            ClaimedAt = null;
            ClaimToken = null;
        }
   ```
   ```diff
   -    public void MarkFailed(string error, int maxAttempts)
   +    public void MarkFailed(string error, int maxAttempts, DateTime? retryNotBefore = null)
        {
            Attempts++;
            Error = error;
            State = Attempts >= maxAttempts ? "Failed" : "Pending";
   +        NextAttemptAt = State == "Pending" ? retryNotBefore : null;
            ClaimedAt = null;
            ClaimToken = null;
        }
   ```
2. `ExternalConfiguration.cs:589-590` — `builder.Property(x => x.NextAttemptAt);` (тип за замовчуванням, як `ClaimedAt`).
3. Міграція (токен міграції): `dotnet ef migrations add OB01_OutboxRetryBackoff`. Очікуваний `Up` — `AddColumn<DateTime>(name: "NextAttemptAt", schema: "itg", table: "NotificationOutbox", type: "datetime2", nullable: true)`. Це лише зміна метаданих, upgrade-безпечна. Індекс не потрібен: черга мала, а відбір іде за `IX_Outbox_State`.
4. `OutboxDispatcher.cs`:
   - константа після `MaxAttempts` (`:105`):
   ```csharp
   /// <summary>
   /// D-9: мінімальна пауза між невдалими спробами. Трохи менше за годину між прогонами зведення (NotificationJob),
   /// щоб щогодинний тик її не пропускав; «відправити зараз» (OutboxFlushJob) рядок у паузі не бере.
   /// </summary>
   public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(50);
   ```
   - `:224-227` і `:270` — третім аргументом передати `clock.UtcNow + RetryDelay`;
   - `ClaimBatchAsync` (`:319`):
   ```diff
   -            .Where(n => n.State == "Pending" && (onlyEventCode == null || n.EventCode == onlyEventCode))
   +            .Where(n => n.State == "Pending" && (n.NextAttemptAt == null || n.NextAttemptAt <= now)
   +                        && (onlyEventCode == null || n.EventCode == onlyEventCode))
   ```
   - `PersistAsync` (`:395`): після `.SetProperty(n => n.Error, item.Error)` додати `.SetProperty(n => n.NextAttemptAt, item.NextAttemptAt)`.
5. `docs/tz/10-decisions.md` — рядок у журналі змін про пауза-між-спробами (D-nnn за наступним вільним номером) + `docs/CHECKSUMS.txt`.

**Тест-доказ:** `tests/Ecr.Infrastructure.Tests/Integration/OutboxDispatcherRetryBackoffTests.cs` (новий, реальний SQL, зразок — `OutboxDispatcherClaimTests`).
- Кейс 1 `Failed_attempt_is_not_retried_by_next_flush_within_delay`: відправник кидає виняток → `FlushAsync` → `Attempts = 1`, `Pending`. Другий `FlushAsync` із тим самим годинником + 1 хв → `Attempts` лишається 1, `SendAsync` викликано один раз.
- Кейс 2: годинник + 51 хв → друга спроба відбувається.
- Без кроку 4 (фільтр) кейс 1 червоний: `Attempts = 2`.

**Перевірка після впровадження:**
- `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~OutboxDispatcher"`;
- `dotnet test tests/Ecr.Domain.Tests --filter "FullyQualifiedName~NotificationOutbox"` (якщо є);
- upgrade-перевірка ×2 (нова міграція).

**Ризики і побічні ефекти:** перша спроба не змінюється (негайний алерт H-20 лишається негайним). Повтори — не частіше ніж раз на ~50 хв. Наявні рядки з `NextAttemptAt = NULL` поводяться як раніше.
**Обґрунтування:** перевикористати `ClaimedAt` як «час останньої спроби» можна без міграції, але це змішує два значення колонки, на якій стоїть `ReclaimStaleAsync`. Окрема колонка — однозначна.
**Рішення PO:** не потрібне. Значення паузи (50 хв) узгоджено з чинним описом «5 спроб = 5 годин».
**Впевненість:** підтверджено читанням.
**Залежності:** D-13 (той самий файл) — робити разом.

---

### D-10 — Medium — Перенос версії з мапою перейменування таблиць лишає чат і передачі на старому коді
**Категорія:** data-integrity / security (ексклюзивність передачі)
**Де:**
- `src/Ecr.Application/Documents/VersionMigration/VersionMigrationPlanner.cs:292-316, 339-344` — мапа `X → Y` і пункт `Renamed` (`Path = "{sheet}.{table}"`, `OldValue = X`, `NewValue = Y`);
- `src/Ecr.Infrastructure/Persistence/DocumentVersionMigrationStore.cs` — `TableDiscussion*`/`EditAssignment` не згадано (`git grep` — порожньо);
- чат ключується кодом: `Configurations/TableDiscussionConfiguration.cs:38, 86-87, 107-108, 133`;
- передача: `src/Ecr.Domain/Entities/Documents/EditAssignment.cs:102-104, 124-127`, `SliceKey` = `EditAssignmentSlice.Key` (`EditAssignmentSlice.cs:44-47`, коди у верхньому регістрі);
- збіг — `src/Ecr.Application/Documents/EditAssignments/EditAssignmentAccess.cs:282, 552` (`string.Equals(table.Code, slice.TableCode, …)`).

**Проблема:** після переносу з перейменуванням таблиця має код `Y`, а `doc.TableDiscussion*` (`TableDefCode = X`) і `wf.EditAssignment` (`TableCode = X`, `SliceKey "…|T=X|…"`) лишаються на `X`.

**Сценарій відтворення:**
1. Таблиця `X` має чат і ЕКСКЛЮЗИВНУ передачу Write користувачу U.
2. Перенос на V2 з мапою `X → Y`.
3. Фактичний результат:
   - історія чату `Y` порожня;
   - передача більше ні з чим не збігається, тож ексклюзивність мовчки зникає: інші з правом Write за роллю можуть правити;
   - U втрачає право, видане передачею (`NotAssignedToYou`).

   Очікувано: дані документа переїжджають разом з таблицею (мапа для того й існує — `VersionMigrationRenames.cs:17-23`).

**Вплив:** лише проєкти, які переносять із мапою перейменування (необовʼязковий шлях). Тиха втрата ексклюзивності — питання доступу.

**Зміна — покроково:** у T2 (легка частина), одразу після `WorkflowSql` (`DocumentVersionMigrationStore.cs:611`):
1. `src/Ecr.Domain/Entities/Documents/EditAssignment.cs` — новий метод поруч із `Reconfigure` (`:368`):
   ```csharp
   /// <summary>
   /// D-10: таблицю зрізу перейменовано переносом версії (мапа перейменування). Ключ зрізу рахує лише домен.
   /// </summary>
   public void RenameTable(string toTableCode, DateTime utcNow)
   {
       var slice = EditAssignmentSlice.Create(SheetCode, toTableCode, MonthNumber, PeriodKeyValue);
       TableCode = slice.TableCode;
       SliceKey = KeyOf(slice, Level);
       ModifiedAt = utcNow;
   }
   ```
2. `DocumentVersionMigrationStore.cs` — приватний метод і виклик:
   ```diff
               await SingleAsync(VersionMigrationStages.Workflow, WorkflowSql).ConfigureAwait(false);
   +
   +           // ⛔ D-10: дані, що ключуються КОДОМ таблиці (чат, передачі зрізу), — за мапою перейменування цього плану.
   +           await RenameTableCodesAsync(projectId, plan, now, ct).ConfigureAwait(false);
   ```
   `now` — момент переносу, уже обчислений у цьому методі (`:394`, `SYSUTCDATETIME()`). `IClock` у сховищі немає.
   ```csharp
   /// <summary>D-10: код таблиці X → Y (пункти <c>Renamed</c> плану) у чаті таблиці й у передачах зрізу.</summary>
   private async Task RenameTableCodesAsync(int projectId, VersionMigrationPlan plan, DateTime now, CancellationToken ct)
   {
       var renames = plan.Items
           .Where(i => i.Kind == "Renamed" && i.Field == "Code" && i.OldValue is not null && i.NewValue is not null
                       && i.Path.Count(c => c == '.') == 1)
           .Select(i => (Sheet: i.Path[..i.Path.IndexOf('.', StringComparison.Ordinal)], From: i.OldValue!, To: i.NewValue!))
           .ToList();
       if (renames.Count == 0)
       {
           return;
       }

       var json = JsonSerializer.Serialize(renames.Select(r => new { f = r.From, t = r.To }));
       await db.Database.ExecuteSqlAsync($"""
           DECLARE @r TABLE (f nvarchar(64) PRIMARY KEY, t nvarchar(64) NOT NULL);
           INSERT @r (f, t) SELECT DISTINCT f, t FROM OPENJSON({json}) WITH (f nvarchar(64), t nvarchar(64));

           UPDATE m SET m.TableDefCode = r.t
             FROM doc.TableDiscussionMessage m JOIN @r r ON r.f = m.TableDefCode
             JOIN doc.Document d ON d.Id = m.DocumentId AND d.ProjectId = {projectId};
           UPDATE x SET x.TableDefCode = r.t
             FROM doc.TableDiscussionMention x JOIN @r r ON r.f = x.TableDefCode
             JOIN doc.TableDiscussionMessage m ON m.Id = x.MessageId
             JOIN doc.Document d ON d.Id = m.DocumentId AND d.ProjectId = {projectId};
           -- Підсумок і «прочитано» мають код у ключі: рядок під Y міг лишитися від старішої версії — тоді старий рядок X
           -- знімається (повідомлення вже переїхали вище), інакше перейменовується.
           DELETE t FROM doc.TableDiscussion t JOIN @r r ON r.f = t.TableDefCode
             JOIN doc.Document d ON d.Id = t.DocumentId AND d.ProjectId = {projectId}
            WHERE EXISTS (SELECT 1 FROM doc.TableDiscussion y WHERE y.DocumentId = t.DocumentId
                            AND y.TableDefCode = r.t AND y.SheetCopyKey = t.SheetCopyKey);
           UPDATE t SET t.TableDefCode = r.t
             FROM doc.TableDiscussion t JOIN @r r ON r.f = t.TableDefCode
             JOIN doc.Document d ON d.Id = t.DocumentId AND d.ProjectId = {projectId};
           DELETE t FROM doc.TableDiscussionRead t JOIN @r r ON r.f = t.TableDefCode
             JOIN doc.Document d ON d.Id = t.DocumentId AND d.ProjectId = {projectId}
            WHERE EXISTS (SELECT 1 FROM doc.TableDiscussionRead y WHERE y.UserId = t.UserId AND y.DocumentId = t.DocumentId
                            AND y.TableDefCode = r.t AND y.SheetCopyKey = t.SheetCopyKey);
           UPDATE t SET t.TableDefCode = r.t
             FROM doc.TableDiscussionRead t JOIN @r r ON r.f = t.TableDefCode
             JOIN doc.Document d ON d.Id = t.DocumentId AND d.ProjectId = {projectId};
           """, ct).ConfigureAwait(false);

       foreach (var (sheet, from, to) in renames)
       {
           var assignments = await db.EditAssignments
               .Where(a => a.SheetCode == sheet && a.TableCode == from
                           && db.Documents.Any(d => d.Id == a.DocumentId && d.ProjectId == projectId))
               .ToListAsync(ct).ConfigureAwait(false);
           foreach (var assignment in assignments)
           {
               assignment.RenameTable(to, now);
           }
       }

       await db.SaveChangesAsync(ct).ConfigureAwait(false);
   }
   ```
   `db.EditAssignments` — `EcrDbContext.cs:120`. `db.SaveChangesAsync` у T2 приєднується до відкритої транзакції контексту (на неї ж спирається `verify.Transaction = db.Database.CurrentTransaction!…` у легкій частині).
3. Колізія передач: якщо в документі вже є передача з тим самим `SliceKey` під `Y` (стара, від версії, де `Y` існував), `SaveChanges` дасть 2601 на `UQ_EditAssignment`, і T2 відкотиться без маркерних наслідків для даних. Це прийнятна явна відмова. За бажанням PO можна додати відмову плану `assignmentsConflict` (не обовʼязково).

**Тест-доказ:** `tests/Ecr.Api.Tests/DocumentVersionMigrationTests.TableRenames.cs` (наявний), новий кейс `Rename_map_carries_table_chat_and_exclusive_assignment`.
- Кроки: таблиця `X`, повідомлення в чаті, ексклюзивна передача U. Перенос із мапою `X → Y`. Далі `GET …/table-discussions/Y` містить повідомлення; інший користувач із Write отримує `403` на PATCH у `Y` (ексклюзивність тримається); U редагує.
- Без зміни: чат порожній, PATCH іншого — `200`.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~DocumentVersionMigrationTests.TableRenames|FullyQualifiedName~TableDiscussion"`, `dotnet test tests/Ecr.Domain.Tests --filter "FullyQualifiedName~EditAssignment"` (новий юніт на `RenameTable`: `SliceKey` містить `T=Y`).
**Ризики і побічні ефекти:** лише на шляху з мапою (пункти `Renamed`). Без мапи — 0 нових звернень. Блокування — у T2 під виключною структурою документів. Профіль доступу U кешується, а передачі скидають профіль явно (KNOWN-ISSUES, AN-138). Чи скидає перенос версії кеш профілів, перевірити: `GrantProfileInvalidation.cs` у `VersionMigration/`.
**Обґрунтування:** перерахувати `SliceKey` у SQL (`REPLACE`) можна, але домен прямо забороняє рахувати ключ поза собою (`EditAssignmentSlice.cs:12-13`). Чат зберігається за кодом навмисно (один чат на документ × код), тож перейменування кодом — природне.
**Рішення PO:** не потрібне. Необовʼязкова відмова `assignmentsConflict` — на розсуд PO.
**Впевненість:** підтверджено читанням (розрив, `DbSet`, наявність `now`).
**Залежності:** разом з D-6, D-7 (ті самі файли; D-7 бере `TableDefId` з `@columnMap.t`, що правильно і для перейменованих таблиць).

---

### D-2 — Low — Виняток «місяць відкрито для документа»: відкриття/закриття без замка періоду
**Категорія:** concurrency
**Де:**
- `src/Ecr.Application/Periods/ProjectDocumentReopenHandlers.cs:72-78` (конструктор), `:97-99` (транзакція закриття);
- `src/Ecr.Application/Periods/DocumentPeriodReopenHandlers.cs:95-101` (конструктор), `:160-164` (транзакція відкриття/заміни);
- `src/Ecr.Infrastructure/Persistence/DocumentPeriodReopenStore.cs:66-68` (`FindOpenAsync`, без замка);
- `src/Ecr.Application/Workflow/ClosedPeriodGuard.cs:37-47` (повернення аркуша: `LockPeriodAsync` → `HasDocumentPeriodReopenAsync`);
- `src/Ecr.Infrastructure/Persistence/WorkflowStore.cs:48-55, 317-341`;
- `UQ_DocumentPeriodReopen_Active` (`Configurations/DocumentPeriodReopenConfiguration.cs:56`) не має арму в `ExceptionHandlingMiddleware.cs:515-590`.

**Проблема і вердикт щодо PATCH:**
1. **PATCH комірок** не бере замок періоду ВЗАГАЛІ (`LockPeriodAsync` викликається лише з `ClosedPeriodGuard`: `git grep -n "LockPeriodAsync\|RequireNotClosedAsync" -- src/Ecr.Application`).
   - Стан періоду для рішення про запис (разом із `DocumentReopened`) рахується в `AccessDecisionService.BuildContextAsync` (`:3115-3195`): `AsNoTracking`, поза транзакцією (`PatchCellsHandler.cs:160` → `EnsureAccessAsync:1141-1160`).
   - У транзакції запису (`PersistCoreAsync:2862` → `EnsureSheetStillEditableAsync:2996-3070`) перевіряється лише стан АРКУША, не періоду.
   - Тож для PATCH закриття винятку — та сама межа, що й закриття періоду за часом (`ReopenedUntil`/`ComputedCloseAt`): правка, рішення про яку прийняте до межі, комітиться через мілісекунди після неї.
   - Висновок: **для PATCH зміна не потрібна** (див. «Обґрунтування»).
2. **Повернення аркуша в Draft** (`RecallSheetHandler.cs:127`, `ReopenDocumentHandler.cs:142`) бере `UPDLOCK` на `doc.Period` і перевіряє виняток. Закриття ж винятку цей рядок не бере, тож не серіалізується з поверненням: аркуш повертається в Draft у місяці, де виняток уже закрито, і застрягає — саме той стан, від якого `ClosedPeriodGuard` захищає (`:13-22`).
3. Два одночасні «відкрити виняток» обидва бачать `FindOpenAsync == null`; другий падає на `UQ_DocumentPeriodReopen_Active` → `DbUpdateException` → 500.

**Сценарій відтворення:**
- (а) Аркуш подано в закритому місяці, виняток діє. Власник тисне «Відкликати», адміністратор одночасно — «Закрити виняток». Recall бере замок → бачить виняток → T2 закриває виняток без очікування → Recall комітить Draft. Фактично аркуш у Draft у закритому місяці; очікувано — 422 `reopenPeriodFirst` або закриття після Recall.
- (б) Два адміністратори одночасно відкривають виняток → один отримує 500.

**Вплив:** рідкісні гонки адмін-дій. Аркуш застрягає (виправляється повторним відкриттям), 500 замість заміни.

**Зміна — покроково:**
1. `ProjectDocumentReopenHandlers.cs:72-78` — останнім параметром конструктора `CloseDocumentPeriodReopenHandler` додати `IWorkflowStore? workflow = null`. Необовʼязковий, як заведено, щоб прямі конструкції в `tests/Ecr.Application.Tests/Periods/ProjectDocumentReopensTests.cs:107,134,151,167` і `tests/Ecr.Infrastructure.Tests/Security/DocumentPeriodReopenTests.cs:260` компілювались.
2. `ProjectDocumentReopenHandlers.cs:97-99`:
   ```diff
            await uow.ExecuteInTransactionAsync(async innerCt =>
            {
   +            // ⛔ D-2: той самий UPDLOCK на doc.Period, що його бере повернення аркуша (ClosedPeriodGuard): закриття
   +            // винятку чекає Recall/Reopen, що вже його перевірили, а наступний бачить виняток закритим.
   +            if (workflow is not null && facts.PeriodState is not null)
   +            {
   +                _ = await workflow.LockPeriodAsync(documentId, new Ecr.Domain.ValueObjects.PeriodKey(periodKey), innerCt)
   +                    .ConfigureAwait(false);
   +            }
   +
                var open = await store.FindOpenAsync(documentId, periodKey, innerCt).ConfigureAwait(false);
   ```
   Умова `facts.PeriodState is not null` потрібна, бо без рядка періоду `LockPeriodAsync` кидає `NotFound` (`WorkflowStore.cs:333-341`), а закриття має лишатися 204-no-op.
3. `DocumentPeriodReopenHandlers.cs:95-101` — той самий необовʼязковий параметр у `OpenDocumentPeriodReopenHandler`. `:160-164`:
   ```diff
            await uow.ExecuteInTransactionAsync(async innerCt =>
            {
   +            // ⛔ D-2: замок періоду — серіалізує заміну винятку з паралельним відкриттям і з поверненням аркуша.
   +            if (workflow is not null)
   +            {
   +                _ = await workflow.LockPeriodAsync(documentId, new Ecr.Domain.ValueObjects.PeriodKey(periodKey), innerCt)
   +                    .ConfigureAwait(false);
   +            }
   +
                // ⚠ Унікальний фільтрований індекс тримає «один незакритий на документ × місяць»: попередній закривається
   ```
   Тут період гарантовано існує: `DenialOf` уже відхилив `PeriodMissing` (`:136-137`).
4. DI: обидва обробники створює контейнер; `IWorkflowStore` зареєстровано. Перевірити реєстрацію обробників: `git grep -n "DocumentPeriodReopenHandler" src/Ecr.Api src/Ecr.Application/DependencyInjection*.cs`.

**Тест-доказ:**
- `tests/Ecr.Infrastructure.Tests/Security/DocumentPeriodReopenTests.cs`, кейс `Close_waits_for_period_lock_held_by_recall`. Сесія 1 у транзакції тримає `WorkflowStore.LockPeriodAsync`; сесія 2 з `SET LOCK_TIMEOUT 200` виконує `CloseDocumentPeriodReopenHandler` (з `workflow`) → `SqlException 1222`. Без кроку 2 закриття проходить миттєво.
- Кейс `Two_concurrent_opens_replace_not_500`: два відкриття через бар'єр — обидва успішні, активний один (`ClosedReplaced` у першого). 20/20 локально.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~DocumentPeriodReopenTests"`, `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~ProjectDocumentReopensTests"`.
**Ризики і побічні ефекти:** +1 звернення в двох рідкісних адмін-обробниках. `ROWLOCK` на рядку періоду проєкту коротко чекає на активні Recall/Reopen і `PeriodStateJob` (той бере той самий рядок, `PeriodStateJob.cs:419-423`). Порядок замків: усі беруть лише рядок періоду, інверсії немає.
**Обґрунтування:**
- PATCH свідомо рахує стан періоду поза транзакцією: храповик `PatchCellsQueryCountTests` (коментар `AccessDecisionService.cs:3127-3128`).
- Повторна перевірка під замком додала б звернення на кожен PATCH заради межі, яка за часом і так «мʼяка» (закриття за `ReopenedUntil` має те саме вікно).
- Для повернення аркуша замок уже є, тож бракує лише його взяття з боку винятку.
- Попередня версія D-2 пропонувала те саме для PATCH «за потреби»: відкинуто з наведених причин.

**Рішення PO:** не потрібне. Якщо PO хоче жорсткої межі і для PATCH, це окреме рішення: +1 звернення на запис і підняття стелі `PatchCellsQueryCountTests`.
**Впевненість:** підтверджено читанням.
**Залежності:** немає.

---

### D-3 — Low — `DeleteCopyAsync` лишає сиріт історії дозволів копії
**Категорія:** data-integrity
**Де:**
- `src/Ecr.Infrastructure/Persistence/DocumentSheetCopyStore.cs:191-249` (`DeleteCopyAsync`) — видаляє комірки, стилі, override, вікна, рядки, екземпляри (`:204-225`), `CalculationIssues` (`:228-230`), події й стани (`:233-238`) і `DocumentSheets` (`:245-247`). `doc.CellPermitPart`/`doc.PermitSegment` із `SheetCopyId = <копія>` НЕ видаляє.
- FK на `DocumentSheet` у цих таблиць немає (`Configurations/PermitHistoryConfiguration.cs:45-50, 88-95`), тож видалення не падає.
- Для порівняння, `DocumentDeletionStore.cs:297-298` їх прибирає.

**Проблема:** каскад копії не оновили, коли J01 (09.11) додали після RS1 (03.11).
**Сценарій відтворення:** копія №2 з колонкою дозволу → у рядку «дозвіл від дати» + поділ місяця → `DELETE` копії. `TableInstance`/`CellValue` зникли, а сегмент і частини лишаються. Вони:
- повертаються з `PermitHistoryStore.ListAsync` (`:12-20`), тобто йдуть у `SaveCellSplitsHandler.cs:74` і в `PermitCoverageIssues`;
- тримають `FK_*_Entry` (запис-дозвіл не видалити, 547);
- архівуються в `arc.CellPermitPart` (`03-archive-proc.sql:440-446`).

**Вплив:** сміття, зайві читання і незрозуміла 547 при видаленні запису довідника. Звітів не спотворює.
**Зміна — покроково:**
1. `DocumentSheetCopyStore.cs` — після блоку `CalculationIssues` (`:228-230`):
   ```diff
            await db.CalculationIssues
                .Where(i => i.DocumentId == documentId && i.SheetCopyId == sheetCopyId)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
   +
   +        // ⛔ D-3: історія дозволів копії (J01) — FK на копію немає. Частини — по одному періоду (відсічка партицій);
   +        // періоди — з самих частин (поділ може лежати в місяці, де екземплярів копії вже немає).
   +        var partPeriods = await db.CellPermitParts.AsNoTracking()
   +            .Where(p => p.DocumentId == documentId && p.SheetCopyId == sheetCopyId)
   +            .Select(p => p.PeriodKeyValue).Distinct().ToListAsync(ct).ConfigureAwait(false);
   +        foreach (var key in partPeriods.Order())
   +        {
   +            await db.CellPermitParts
   +                .Where(p => p.PeriodKeyValue == key && p.DocumentId == documentId && p.SheetCopyId == sheetCopyId)
   +                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
   +        }
   +
   +        await db.PermitSegments
   +            .Where(s => s.DocumentId == documentId && s.SheetCopyId == sheetCopyId)
   +            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
   ```
2. Одноразове прибирання наявних сиріт — порожня EF-міграція `RS2_PermitHistoryCopyOrphans` (токен міграції). Модель не змінюється, `Up` містить лише `migrationBuilder.Sql(...)`; `Down` порожній:
   ```sql
   DELETE p FROM doc.CellPermitPart AS p
    WHERE p.SheetCopyId IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM doc.DocumentSheet AS s WHERE s.Id = p.SheetCopyId);
   DELETE g FROM doc.PermitSegment AS g
    WHERE g.SheetCopyId IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM doc.DocumentSheet AS s WHERE s.Id = g.SheetCopyId);
   ```
   Ідемпотентно. Міграція, а не сід, бо виконується раз; сід виконується на кожному старті. `arc.*` не чіпати.

**Тест-доказ:** `tests/Ecr.Api.Tests/DocumentSheetCopyApiTests.cs`, кейс `DeleteCopy_removes_permit_history_of_the_copy`. Кроки: копія → рядок із дозволом → add permit посеред місяця + `SaveCellSplits` → `DELETE` копії. Тоді `SELECT COUNT(*) FROM doc.PermitSegment / doc.CellPermitPart WHERE SheetCopyId = @id` = 0; без кроку 1 > 0. Міграція — `tests/Ecr.Infrastructure.Tests/Persistence/RS2PermitHistoryCopyOrphansMigrationTests.cs` за зразком `D450DocumentOwnerIndexMigrationTests`: сирота зникає, живий сегмент чинної копії лишається.
**Перевірка після впровадження:** `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~DocumentSheetCopyApiTests"`; upgrade-перевірка ×2 (нова міграція).
**Ризики і побічні ефекти:** +2…3 звернення у видаленні копії (стелі звернень у `DocumentSheetCopyApiTests` немає). Блокування — у тій самій транзакції під структурним applock (`EnterStructureAsync`).
**Обґрунтування:** проєкт тримає FK `Restrict` і ручні каскади. FK на `DocumentSheet` для партиційованої `CellPermitPart` не пропоную (архів виконує `TRUNCATE` партицій).
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням.
**Залежності:** порядок токена міграцій із D-9.

---

### D-11 — Low — `SourceEventSyncJob`: жорстке видалення рядка події лишає `doc.CellFormulaOverride`
**Категорія:** data-integrity
**Де:**
- `src/Ecr.Infrastructure/Jobs/SourceEventSyncJob.cs:1562-1588` — видаляє `CellStyle`, `CellValue`, `TableRow`, `SourceEventLink`, `SourceEventAttributeValue`; `CellFormulaOverride` (D-461, без FK) — ні;
- для порівняння: `DocumentDeletionStore.cs:161`, `DocumentSheetCopyStore.cs:217-219`;
- читач без зʼєднання з рядком: `FormulaOverrideStore.cs:169-196` (`ReadKeysAsync`) → `RecalculationService.cs:636`; `ReadProjectAsync` → план переносу (`MigrateDocumentVersionHandler.cs:1397-1402`, пункти `OverrideLost`).

**Проблема:** повна звірка подій жорстко видаляє рядок, але не його позначки «ручне значення замість формули».
**Сценарій відтворення:** у рядку `EF-…` користувач поставив ручне значення замість формули (override). Подія зникла з джерела, повна звірка видалила рядок. Позначка лишається:
- з'являється в `ReadKeysAsync` (зайва адреса в `overridden`);
- рахується пунктом `OverrideLost` у сухому прогоні переносу версії (хибний пункт звіту);
- архівується й відновлюється.

**Вплив:** низький — сміття й хибні пункти звіту переносу. `TableRowId` — identity, тож позначка до нового рядка не «прилипне».
**Зміна — покроково:** `SourceEventSyncJob.cs:1565` — перед видаленням стилів у тому самому тексті команди:
```diff
         delete.CommandText = """
+            -- ⛔ D-11: позначки «ручне замість формули» (D-461) рядка — без FK на рядок, як і стилі.
+            DELETE o
+              FROM doc.CellFormulaOverride AS o
+             WHERE o.PeriodKey = @period
+               AND o.TableRowId IN (SELECT r.Id FROM doc.TableRow AS r
+                                     WHERE r.PeriodKey = @period AND r.TableInstanceId = @instance AND r.RowKey = @rowKey);
             -- CELL-STYLE L3b: користувацьке оформлення комірок рядка (без FK на рядок — див. CellStyle).
```
Колонки — `PK_CellFormulaOverride (PeriodKey, TableRowId, ColumnDefId)` (`Configurations/CellFormulaOverrideConfiguration.cs:27`). Наявних сиріт не прибирати окремо: вони нешкідливі, а прибирання вимагало б скану всіх партицій.
**Тест-доказ:** `tests/Ecr.Infrastructure.Tests/Jobs/SourceEventSyncJobTests.cs`, кейс `Full_reconcile_removing_row_also_removes_formula_overrides`. Кроки: рядок події з override (вставка через `FormulaOverrideStore` або прямий INSERT) → подія зникла з джерела → прогін повної звірки. Очікування: `SELECT COUNT(*) FROM doc.CellFormulaOverride WHERE TableRowId = @row` = 0; без зміни — 1.
**Перевірка після впровадження:** `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~SourceEventSyncJobTests"`.
**Ризики і побічні ефекти:** той самий round-trip (один текст команди), та сама транзакція `tx`. Партиція — за `@period`.
**Обґрунтування:** узгодження з іншими каскадами (`DocumentDeletionStore`, `DocumentSheetCopyStore`).
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням.
**Залежності:** немає (D-4 — інший файл).

---

### D-12 — Low — `usp_RestoreYear`: частини місяця не звіряються і не прибираються при відкаті
**Категорія:** data-integrity (архів)
**Де:** `src/Ecr.Infrastructure/Persistence/Sql/03-archive-proc.sql`:
- `:887-898` — крок 1, вставка `doc.CellPermitPart` з `NOT EXISTS`;
- `:978-988` — крок 2: звірка комірок, рядків, екземплярів, стилів і override, але НЕ частин;
- `:1004` — `DELETE FROM arc.CellPermitPart` однаково;
- `:1095-1139` — прибирання Z6-01 видаляє стилі, override, комірки, рядки й екземпляри, але не `doc.CellPermitPart`.

Для порівняння, `usp_ArchiveYear` частини звіряє (`:571-577`).

**Проблема:** частини — єдина таблиця з `arc.*`, яку розархівація не звіряє і яку відкат Z6-01 не прибирає.

**Сценарій відтворення:**
1. Розархівація року падає на кроці 2 (розбіжність комірок, 50011). CATCH прибирає з `doc.*` часткову копію екземплярів, а відновлені частини місяця лишаються в гарячих партиціях.
2. Архів цілий, і `usp_ArchiveYear` наступного разу виходить гілкою «вже заархівовано» (`:221-234`), бо `doc.CellValue` порожня. Частини назавжди лишаються в гарячих партиціях заархівованого року.
3. Окремо: гарячий рядок частини з тим самим логічним ключем, але іншим значенням (`NOT EXISTS` пропускає архівний) тихо замінює архівний, без 50011.

**Вплив:** низький — лише після збою розархівації. Сироти частин у гарячих партиціях архівного року.

**Зміна — покроково:** `03-archive-proc.sql`, процедура `arc.usp_RestoreYear`.
1. `:691` — оголошення змінних:
   ```diff
   -    DECLARE @srcStyles bigint, @dstStyles bigint, @srcOverrides bigint, @dstOverrides bigint;
   +    DECLARE @srcStyles bigint, @dstStyles bigint, @srcOverrides bigint, @dstOverrides bigint;
   +    DECLARE @srcParts bigint, @dstParts bigint;  -- D-12
   ```
2. Після підрахунку `@dstOverrides` (перед `IF @srcCells <> @dstCells`, `:985`):
   ```sql
            -- ⛔ D-12: частини місяця — за логічним ключем архіву і ТОТОЖНИМ значенням (як комірки).
            SELECT @srcParts = COUNT_BIG(*) FROM arc.CellPermitPart WHERE PeriodKey = @k;
            SELECT @dstParts = COUNT_BIG(*)
            FROM arc.CellPermitPart AS a
            JOIN doc.CellPermitPart AS d
              ON d.PeriodKey = a.PeriodKey AND d.DocumentId = a.DocumentId AND d.TableDefId = a.TableDefId
             AND (d.SheetCopyId = a.SheetCopyId OR (d.SheetCopyId IS NULL AND a.SheetCopyId IS NULL))
             AND d.RowKey = a.RowKey AND d.ColumnDefId = a.ColumnDefId AND d.PartNo = a.PartNo
             AND EXISTS (SELECT a.RegistryEntryId, a.DayFrom, a.DayTo, a.ValueNumeric
                         INTERSECT SELECT d.RegistryEntryId, d.DayFrom, d.DayTo, d.ValueNumeric)
            WHERE a.PeriodKey = @k;
   ```
   і в умову `:985-987` додати `OR @srcParts <> @dstParts`.
3. У CATCH після циклу `WHILE 1 = 1 … END;` (перед `IF @undoInst > 0 OR @undoKept > 0`, `:1144`) додати:
   ```sql
            -- ⛔ D-12: відновлені частини місяця з ТОТОЖНИМ двійником в arc.* (архів тут цілий), яким уже не лишилося
            -- гарячого екземпляра тієї ж таблиці документа × копії в цьому періоді.
            DELETE p
            FROM doc.CellPermitPart AS p
            WHERE p.PeriodKey BETWEEN @FromPeriodKey AND @ToPeriodKey
              AND EXISTS (SELECT 1 FROM arc.CellPermitPart AS a
                           WHERE a.PeriodKey = p.PeriodKey AND a.DocumentId = p.DocumentId AND a.TableDefId = p.TableDefId
                             AND (a.SheetCopyId = p.SheetCopyId OR (a.SheetCopyId IS NULL AND p.SheetCopyId IS NULL))
                             AND a.RowKey = p.RowKey AND a.ColumnDefId = p.ColumnDefId AND a.PartNo = p.PartNo
                             AND EXISTS (SELECT a.RegistryEntryId, a.DayFrom, a.DayTo, a.ValueNumeric
                                         INTERSECT SELECT p.RegistryEntryId, p.DayFrom, p.DayTo, p.ValueNumeric))
              AND NOT EXISTS (SELECT 1 FROM doc.TableInstance AS i
                               WHERE i.PeriodKey = p.PeriodKey AND i.DocumentId = p.DocumentId
                                 AND i.TableDefId = p.TableDefId
                                 AND (i.SheetCopyId = p.SheetCopyId OR (i.SheetCopyId IS NULL AND p.SheetCopyId IS NULL)));
   ```

**Тест-доказ:** `tests/Ecr.Infrastructure.Tests/Jobs/RestoreYearTests.cs` (наявний клас).
- Кейс 1 `Restore_mismatch_on_parts_throws_50011_and_keeps_archive`: після архівації вставити в `doc.CellPermitPart` рядок із тим самим ключем, але іншим `ValueNumeric` → `usp_RestoreYear` → 50011, `arc.CellPermitPart` цілий. Без кроку 2 розархівація успішна, архівне значення втрачено.
- Кейс 2 `Failed_restore_removes_restored_parts`: змусити 50011 на комірках (як наявні тести Z6-01) → після CATCH `doc.CellPermitPart` за рік порожня. Без кроку 3 рядки лишаються.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~RestoreYearTests|FullyQualifiedName~ArchiveYear"`; upgrade-перевірка ×2 (скрипт `CREATE OR ALTER`, ідемпотентний).
**Ризики і побічні ефекти:** +2 читання на період у кроці 2 (таблиця мала). DELETE у CATCH — поза циклом екземплярів, один оператор. Логіку кроку 2 (лише читання + `DELETE arc.*`) не змінено.
**Обґрунтування:** узгодження з рештою таблиць кроку 2 і з `usp_ArchiveYear`.
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням; ймовірність сценарію низька.
**Залежності:** немає.

---

### D-13 — Low — Outbox: після успішної відправки результат фіксується зі скасовуваним токеном
**Категорія:** reliability
**Де:** `src/Ecr.Infrastructure/Integration/OutboxDispatcher.cs:237-240` (`SendAsync` → `MarkSent`), `:278` (`PersistAsync(item, claim.Token, ct)`), `:385-401` (`ExecuteUpdateAsync(…, ct)`), `:78-83` (межа «at-least-once»).
**Проблема:** службу зупиняють (деплой чи перезапуск), токен Quartz/виконавця скасовується одразу після того, як `SendAsync` повернувся. `ExecuteUpdateAsync(ct)` кидає `OperationCanceledException` ще до `UPDATE`, і рядок лишається `Sending`. Через 10 хв `ReclaimStaleAsync` повертає його в `Pending`, і лист іде вдруге.
**Сценарій відтворення:** штатна зупинка служби посеред партії листів → після старту той самий лист отримано двічі. Очікувано: дубль лише при вбитому процесі (коментар `:78-83`), не при штатній зупинці.
**Вплив:** дубль одного листа на кожну штатну зупинку посеред відправки.
**Зміна — покроково:** `OutboxDispatcher.cs:278`:
```diff
-            if (!await PersistAsync(item, claim.Token, ct).ConfigureAwait(false))
+            // ⛔ D-13: лист уже прийнято SMTP — фіксацію не скасовуємо зупинкою служби (інакше рядок лишився б
+            // `Sending`, ReclaimStaleAsync повернув би його в чергу й лист пішов би вдруге). UPDATE — один рядок, мілісекунди.
+            if (!await PersistAsync(item, claim.Token, CancellationToken.None).ConfigureAwait(false))
```
**Тест-доказ:** `tests/Ecr.Infrastructure.Tests/Integration/OutboxDispatcherDuplicateSendTests.cs` (наявний), кейс `Cancellation_right_after_send_still_persists_sent`. Фейковий `INotificationSender` скасовує `CancellationTokenSource` у тілі `SendAsync` і повертається успішно. Після `FlushAsync` (очікувано кидає OCE на наступній ітерації або завершується) рядок — `Sent`. Без зміни — `Sending`.
**Перевірка після впровадження:** `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~OutboxDispatcher"`.
**Ризики і побічні ефекти:** зупинка служби може затриматися на час одного `UPDATE`. Перевірка `deadline`/`ct` на наступній ітерації лишається.
**Обґрунтування:** найменша зміна; альтернатива «окремий таймаут-токен» без потреби.
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням.
**Залежності:** разом з D-9 (той самий файл).

---

### D-4 — Low — `SourceEventAttributeValueWriter`: ключ дедуплікації ≠ рівність колації БД
**Категорія:** data-integrity
**Де:**
- `src/Ecr.Infrastructure/Jobs/SourceEventAttributeValueWriter.cs:200-206` (`seen.Add((attribute.Scope, name.ToUpperInvariant()))`);
- `MergeSql:46-70`, зокрема `:57-62` — `USING … OPENJSON(@values)`, `ON t.LinkId = s.l AND t.Scope = s.s AND t.Attribute = s.a` (колація БД);
- `:146-160` — формування `values`;
- PK `PK_SourceEventAttributeValue (LinkId, Scope, Attribute)` — `Configurations/SourceEventConfiguration.cs:206-208`;
- колація тестової бази — `Latin1_General_100_CI_AS_SC` (`tests/Ecr.TestKit/SqlServerFixture.cs:354`).

**Проблема:** C# вважає різними імена, які `*_CI_AS` вважає рівними: повноширинні й звичайні літери (колація нечутлива до ширини), кана, `ß`/`ss`. Обидва потрапляють у `@values`, і MERGE падає з `8672` (той самий цільовий рядок двічі) або `2627`.
**Сценарій відтворення:** подія PI AF з атрибутами `Flow` і `Ｆｌｏｗ` (повноширинні) однієї області → `WriteAsync` → `SqlException 8672`. Збій — на всю сторінку з 500 зв'язків (`:146-170`, виклик із `SourceEventSyncJob.cs:878`) і повторюється на кожному прогоні: значення атрибутів подій сторінки перестають оновлюватись. Очікувано — «перший виграє» (xml-коментар `Rows`, `:194-197`).
**Вплив:** низька ймовірність (імена атрибутів зазвичай ASCII), але збій стійкий.
**Зміна — покроково:**
1. `MergeSql` (`:57-61`):
   ```diff
   -        USING (SELECT j.l, j.s, j.a, j.n, j.x, j.tr, j.u, CONVERT(binary(32), j.h, 1) AS h, j.m
   -                 FROM OPENJSON(@values) WITH (
   -                        l bigint '$.l', s tinyint '$.s', a nvarchar(200) '$.a', n decimal(34,16) '$.n',
   -                        x nvarchar(4000) '$.x', tr bit '$.tr', u nvarchar(64) '$.u', h varchar(66) '$.h',
   -                        m datetime2(3) '$.m') AS j) AS s
   +        USING (SELECT d.l, d.s, d.a, d.n, d.x, d.tr, d.u, d.h, d.m
   +                 FROM (SELECT j.l, j.s, j.a, j.n, j.x, j.tr, j.u, CONVERT(binary(32), j.h, 1) AS h, j.m,
   +                              -- ⛔ D-4: один рядок на ключ у КОЛАЦІЇ БАЗИ (як PK цілі) — перший за порядком джерела.
   +                              ROW_NUMBER() OVER (PARTITION BY j.l, j.s, j.a ORDER BY j.o) AS rn
   +                         FROM OPENJSON(@values) WITH (
   +                                o int '$.o',
   +                                l bigint '$.l', s tinyint '$.s', a nvarchar(200) '$.a', n decimal(34,16) '$.n',
   +                                x nvarchar(4000) '$.x', tr bit '$.tr', u nvarchar(64) '$.u', h varchar(66) '$.h',
   +                                m datetime2(3) '$.m') AS j) AS d
   +                WHERE d.rn = 1) AS s
   ```
2. `WriteAsync` (`:150-160`) — першим полем анонімного обʼєкта додати `o = values.Count,`. `nvarchar(200)` = `SourceEventMap.MaxAttributeLength` (`src/Ecr.Domain/Entities/External/SourceEventMap.cs:58`), тож збігається.

**Тест-доказ:** `tests/Ecr.Infrastructure.Tests/Jobs/SourceEventAttributeValueWriterCollationTests.cs` (новий, реальний SQL; підготовку зв'язку взяти з `SourceEventSyncAnnualTests.D465.cs`), кейс `Attributes_equal_in_database_collation_keep_first_and_do_not_fail`. Подія з атрибутами `Flow` (1.0) і `Ｆｌｏｗ` (2.0) однієї області → `WriteAsync` не кидає; у таблиці один рядок зі значенням 1.0. Без зміни — `SqlException` 8672 (на колації `Latin1_General_100_CI_AS_SC` повноширинні = звичайні).
**Перевірка після впровадження:** `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~SourceEventAttributeValueWriter|FullyQualifiedName~SourceEventSync"`.
**Ризики і побічні ефекти:** звернень не додається. Сортування ≤ кількох тисяч рядків JSON — нехтовне.
**Обґрунтування:** дедуплікація там, де діє та сама рівність, що й у PK.
**Рішення PO:** не потрібне.
**Впевненість:** розбіжність підтверджено читанням; чи є такі імена в PI AF замовника — потребує перевірки на стенді.
**Залежності:** немає.

---

### D-5 — Low — `RV01_ValueRefIndex`: офлайн-побудова індексу на `dic.RegistryValue`
**Категорія:** migration
**Де:** `src/Ecr.Infrastructure/Persistence/Migrations/20261104185622_RV01_ValueRefIndex.cs:11-19`. Зразок — `20261025072353_D450DocumentOwnerIndex.cs:30, 41-58, 61-64`. Тест-зразок — `tests/Ecr.Infrastructure.Tests/Persistence/D450DocumentOwnerIndexMigrationTests.cs`.
**Проблема:**
```csharp
migrationBuilder.CreateIndex(
    name: "IX_RegistryValue_ValueRef",
    schema: "dic",
    table: "RegistryValue",
    column: "ValueRefEntryId",
    filter: "[ValueRefEntryId] IS NOT NULL");
```
Індекс будується без `ONLINE` і без guard «уже існує» — на відміну від прийнятого в репозиторії правила для великих таблиць (D450, D-463, D-465, AN34).
**Сценарій відтворення:** оновлення живої бази. `CREATE INDEX` тримає блокування `dic.RegistryValue` на час скану, тож записи в довідники й `RegistrySyncJob` чекають. Якщо DBA зібрав індекс вручну до оновлення, міграція падає з 1913.
**Вплив:** довший простій або збій оновлення на великих довідниках; розмір у замовника невідомий.
**Зміна — покроково:** редагування тіла вже застосованої міграції безпечне. EF звіряє лише `MigrationId`, тож бази, де `RV01` уже застосовано, її не виконують. Ті, де ще ні, отримують ONLINE і guard. Ідемпотентний скрипт загортає тіло в `IF NOT EXISTS (__EFMigrationsHistory)`.

`20261104185622_RV01_ValueRefIndex.cs:11-19`:
```diff
         protected override void Up(MigrationBuilder migrationBuilder)
         {
-            migrationBuilder.CreateIndex(
-                name: "IX_RegistryValue_ValueRef",
-                schema: "dic",
-                table: "RegistryValue",
-                column: "ValueRefEntryId",
-                filter: "[ValueRefEntryId] IS NOT NULL");
+            // ⛔ D-5: ONLINE там, де він є (Enterprise/Developer/Azure), і перевірка наявності — як D450DocumentOwnerIndex.
+            migrationBuilder.Sql(CreateIndexSql(D450DocumentOwnerIndex.EngineEditionExpression));
         }
+
+        /// <summary>T-SQL індексу для заданої редакції (літерал — у тесті гілки без ONLINE).</summary>
+        public static string CreateIndexSql(string engineEdition)
+            => $"""
+                DECLARE @rv01Edition int = {engineEdition};
+                DECLARE @rv01Online nvarchar(40) =
+                    CASE WHEN @rv01Edition IN (3, 5, 8) THEN N' WITH (ONLINE = ON)' ELSE N'' END;
+                DECLARE @rv01 nvarchar(max) =
+                      N'CREATE NONCLUSTERED INDEX IX_RegistryValue_ValueRef ON dic.RegistryValue (ValueRefEntryId) '
+                    + N'WHERE ValueRefEntryId IS NOT NULL'
+                    + @rv01Online
+                    + N';';
+
+                IF INDEXPROPERTY(OBJECT_ID(N'dic.RegistryValue'), N'IX_RegistryValue_ValueRef', 'IndexID') IS NULL
+                    EXEC sys.sp_executesql @rv01;
+                """;
```
`Down`, `Designer` і snapshot не змінюються (модель та сама).

**Тест-доказ:** `tests/Ecr.Infrastructure.Tests/Persistence/RV01ValueRefIndexMigrationTests.cs` за зразком `D450DocumentOwnerIndexMigrationTests` (`BeforeMigration = "20261104084809_CI01_CalculationIssue"`).
- Кейс 1 — Up/Down/Up.
- Кейс 2 — `Ідемпотентний_скрипт_двічі_і_наявний_індекс_нічого_не_ламають`: індекс створено вручну до міграції.
- Кейс 3 — `CreateIndexSql("4")` (гілка без ONLINE).

Кейс 2 падає на поточному коді з 1913 «already exists».
**Перевірка після впровадження:** `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~RV01ValueRefIndexMigrationTests"`; upgrade-перевірка ×2 (ідемпотентний `migration.sql`).
**Ризики і побічні ефекти:** на Standard/Express поведінка не змінюється (ONLINE недоступний). Звернень застосунку не торкається.
**Обґрунтування:** узгодження з прийнятим правилом репозиторію.
**Рішення PO:** не потрібне.
**Впевненість:** форму міграції підтверджено читанням; розмір `dic.RegistryValue` у замовника треба перевірити на стенді.
**Залежності:** немає. Токен міграцій не потрібен: нова міграція не створюється, але файл міграції — гарячий, тож узгодити з власником токена.

---

## Порядок впровадження

Групи без спільних файлів можна робити паралельно:

- **A (перенос версії, послідовно, одна гілка):** D-6 → D-7 → D-10. Спільні `DocumentVersionMigrationStore.cs`, `MigrateDocumentVersionHandler.cs`, `DocumentVersionMigrationTests.*`. D-6 першим, бо його відмова знімає найгірший наслідок.
- **B (історія дозволів):** D-1 (`PermitHistoryHandlers.cs`, `SaveCellSplitsHandler.cs`, `PermitHistorySupport.cs`, `PermitHistoryStore.cs`, порт).
- **C (копії аркушів):** D-3 (`DocumentSheetCopyStore.cs` + міграція `RS2_…`).
- **D (виняток періоду):** D-2 (`ProjectDocumentReopenHandlers.cs`, `DocumentPeriodReopenHandlers.cs`).
- **E (outbox):** D-9 + D-13 (`OutboxDispatcher.cs`, `NotificationOutbox.cs`, `ExternalConfiguration.cs` + міграція `OB01_…`).
- **F (перерахунок):** D-8 (`FormulaRecalculationJob.cs`, `CrossPeriodFanOut.cs`).
- **G (синк подій):** D-11 (`SourceEventSyncJob.cs`) і D-4 (`SourceEventAttributeValueWriter.cs`) — різні файли, можна паралельно.
- **H (архів):** D-12 (`03-archive-proc.sql`).
- **I (міграції):** D-5 (тіло `RV01`).

**Токен міграцій** (по одній у польоті): `OB01_OutboxRetryBackoff` (D-9) і `RS2_PermitHistoryCopyOrphans` (D-3) — послідовно. Правка `RV01` (D-5) і snapshot не змінює, але узгоджується з власником токена.

**Залежності:** D-7 спирається на `@columnMap.t`; D-10 цього не змінює. Інших залежностей між знахідками немає.

## Відкинуто

| Кандидат | Чому |
|---|---|
| D-2 (попередня редакція): «повторна перевірка винятку в транзакції PATCH» | PATCH взагалі не бере замок періоду: стан рахується поза транзакцією за храповиком звернень (`AccessDecisionService.cs:3127-3128`, `PatchCellsQueryCountTests`). Закриття винятку для PATCH — та сама «мʼяка» межа, що й закриття за часом. Звужено до шляху повернення аркуша (див. D-2) |
| D-1 (попередня редакція): твердження про `rpt`-вʼюхи (`05-rpt-views.sql:1591-1615`) | Наслідок для звітів не перевірено; з опису вилучено, на суть D-1 не впливає |
| `RecalculationJob`: рішення «позначити Failed» за статусом у памʼяті після відкоченого завершення (`RecalculationJob.cs:612-617`) | Лише косметичний статус прогону. Жоден споживач не гейтить за `calc.CalculationRun.Running`, `AbandonedWorkSweeper` закриває його (`:102, 293`) |
| Перенос версії: сироти `ext.RowWindowValue` / `calc.CalculationIssue` прибраних таблиць | Сміття без впливу на читачів: вікна читаються за чинним екземпляром, проблеми — за `TableDefId` поточної версії. Мала цінність, не включено до ліміту |
| `usp_GenerateStableViews`: сторож `@n + @extra > 1000` (`05-rpt-views.sql:1161`) не рахує `<Code>_Text` | Реально лише для групи з ≈990+ колонок. Однорядкову правку (`SET @extra = @extra + 1` у гілці `@hasText = 1`, `:1133-1139`) можна зробити попутно |
| Сесійний applock перерахунку без нагляду за з'єднанням (`SqlDistributedLock.cs:63-102`) | Обрив з'єднання валить і власні запити задачі; гіпотетично |
| Перекіс годинника між хостами для `ClaimedAt` outbox | Потребує перекосу > 3 хв; не підтверджено |
| `05-rpt-views.sql` — ін'єкції через коди | Коди обмежені `EcrCode ^[A-Za-z][A-Za-z0-9_]{0,63}`, скрізь `QUOTENAME` / `REPLACE ''`, `@sql nvarchar(max)`, DDL під `EXECUTE AS OWNER` |
| Попередні D-1…D-5 | Усі п'ять підтверджено в поточному main (D-2 звужено) |

## Перевірено й у порядку

- **SQL-ін'єкції:**
  - `DocumentVersionMigrationStore` — усе через параметри / `OPENJSON`, `ApplyPresentationAsync` — білий список статичних текстів;
  - `TemplateVersionStore` — LIKE з `ESCAPE '!'`;
  - `RowStore`/`RecalculationJob` — `SqlQuery($"")` параметризовано, `ExecuteSqlRaw` — константи;
  - `05-rpt-views.sql` — див. «Відкинуто».
- **Конкурентність переносу версії:** `UPDLOCK, HOLDLOCK, ROWLOCK` на проєкті + структурні applock за зростанням `DocumentId` у T1 і T2; оренда важкої частини з `THROW 51000`; водяний знак діапазонів у тій самій транзакції; хеш плану блокує відновлення з іншою мапою.
- **Outbox:** атомарне захоплення `Pending → Sending`, кожен запис фіксується токеном захоплення, межа партії 7 хв < оренди 10 хв (Q-241).
- **Перерахунок:** завершення огороджене `FenceAsync` у тій самій транзакції; старший прогін не перезаписує новіший (`Superseded`).
- **Черга й sweeper:**
  - `AbandonedWorkSweeper` — кожне закриття умовним `UPDATE … WHERE Status = 'Running'`, рядки черги БД виключено (`Lane == null`), purge пакетами;
  - Worker: рядок з'єднання дочірньому процесу — лише через успадковане `ECR_`-оточення, не командним рядком; дочірній процес — `MaxConcurrency 1`, м'яка зупинка перед закриттям Job Object.
- **Каскади:**
  - `DocumentDeletionStore` покриває всі FK на `doc.Document`, додані після 09.10;
  - `RowStore` — порядковий номер рядка під замком аркуша в транзакції з аудитом; видалення + структурний аудит + журнал рядка в одній транзакції.
- **Архів:** колонки, додані в `doc.*` після 09.10 (`TableInstance.SheetCopyId`, `CellPermitPart`, `CellStyle`, `CellFormulaOverride`), є в `12-archive-tables.sql` і обох списках `03-archive-proc.sql`; `aud.CellChange` нові колонки — в кінці, SWITCH працює.
- **Міграції після 09.10:**
  - `RS1` (UQ перебудовано з `DROP_EXISTING`, ONLINE за редакцією) — лише метадані;
  - `TC02`, `CI01`, `DPR01`, `J01`, `D401` — лише метадані;
  - `RPTV01` — дані ідемпотентно;
  - D-463/D-465 — ONLINE + guard (виправлено 31.10).
- **Найменші привілеї й секрети:** runtime-DDL лише через `rpt.usp_Generate*Views`/`arc.usp_*` (EXECUTE, D-66); тексти `SqlException` у відповідях замінені кодом і кореляцією.

## Самоперевірка

- **D-6:**
  - рядки `DocumentVersionMigrationStore.cs:611, 748, 1760-1765, 1865-1869` і `RS1_SheetCopies.cs:203-210` відкрито повторно (awk/`git grep -n`);
  - `MigrateDocumentVersionHandler.cs:1453-1462, 1866-1881` звірено (`git grep -n "refusals.Add(\"grantsNotMapped\")"` → `:1461`; `migrateDataLoss` → `:1881`);
  - `CountDenyGrantsAsync` (порт `:171`, реалізація `:985`) існує, отже зразок сигнатури реальний;
  - `range` — `RangeInfo(int FromPeriodKey, …)` (`:1267`), тож умова `== 0` компілюється;
  - `DocumentSheet.CopyOfId`/`SheetDefId` існують (`DocumentSheet.cs:8, 24`), `db.DocumentSheets` — `EcrDbContext.cs:107`;
  - тест падає без фіксу, бо T2 кидає 547;
  - у відомому не знайдено (`SheetCopy`, `CopyOf`).
- **D-7:**
  - `git grep` по `PermitSegment|CellPermitPart` у сховищі й `VersionMigration/` — порожньо;
  - прелюдія `@columnMap (o, n, t)` — `:1481-1482`, повнота — `VersionMigrationPlanner.cs:600`;
  - фінальні рядки `WorkflowSql` `:1871-1876` дослівно збігаються з «було»;
  - назви колонок взято з `PermitHistoryConfiguration.cs`;
  - тест падає без фіксу, бо історія лишається на Id V1.
- **D-1:**
  - `PermitHistoryHandlers.cs:60-64, 123-125, 166-189, 240-241, 301-303, 365, 377-379` перечитано;
  - `SaveCellSplitsHandler.cs:74, 90, 109, 112, 265, 276` — `git grep -n`;
  - `PermitHistoryRules` — `internal static class` (`PermitHistorySupport.cs:142`), `NothingToUndo` — `:186`;
  - `ConcurrencyConflictException(string, string, IReadOnlyDictionary)` — `EcrException.cs:54`;
  - `ErrorCodes.CellConflict` — `ErrorCodes.cs:242`;
  - ключ `err.ECR-CELL-0409.concurrentChange` є в `CATALOG-en-base.sql:927` без параметрів;
  - тестових реалізацій `IPermitHistoryStore` немає (`git grep`), тож розширення порту нічого не ламає;
  - тест на замок падає без `UPDLOCK, HOLDLOCK`.
- **D-8:**
  - `FormulaRecalculationJob.cs:116, 128, 167, 175-177, 179-189, 203, 225-227` — `git grep -n`;
  - `CrossPeriodSource(SourceSequence, TableDefId, ColumnDefId, OwnerInput)` — `CrossPeriodReaders.cs:15-16`;
  - `DirtyCell` — `FormulaRecalculationJob.cs:173-175`;
  - `FormulaRecalculationTarget.Of(long, int, int?)` — `IBackgroundJobScheduler.cs:582`;
  - `EnqueueCoalescedAsync<TJob>` → `Task<string>` (`:109`);
  - злиття при `DeferAsync` — `DbJobQueue.cs:461-465`;
  - тест падає без фіксу: Queued-задачі з джерелом немає.
- **D-9:**
  - `OutboxDispatcher.cs:105, 142, 224, 229, 270, 278, 319, 385, 395` — `grep -n`;
  - `NotificationOutbox.cs:72, 86-94, 130-137` перечитано;
  - `ExternalConfiguration.cs:589-596`;
  - тест падає без фільтра в `ClaimBatchAsync`;
  - Q-241 описує захоплення, не відступ — не дубль.
- **D-10:**
  - `VersionMigrationPlanner.cs:292-316, 339-344` перечитано (шлях пункту — `"{sheet}.{table}"`);
  - `EditAssignment.cs:102-104, 124-127, 368, 397-401`, `EditAssignmentSlice.cs:44-47, 55` (`Create`);
  - TableDiscussion-таблиці не мають композитних FK на `TableDefCode`, тож `UPDATE` ключа безпечний;
  - `db.EditAssignments` — `EcrDbContext.cs:120`; `now` — `DocumentVersionMigrationStore.cs:394`;
  - тест падає без фіксу: чат на `X`.
- **D-2:**
  - `ProjectDocumentReopenHandlers.cs:72-78, 97-99`, `DocumentPeriodReopenHandlers.cs:95-101, 136-137, 160-164`, `WorkflowStore.cs:48-55, 317-341`, `ClosedPeriodGuard.cs:37-47` перечитано;
  - PATCH: `PatchCellsHandler.cs:160, 1141-1160, 2862, 2996-3070` і `AccessDecisionService.cs:3115-3195` — замка періоду немає (`git grep LockPeriodAsync` → лише `ClosedPeriodGuard`);
  - `DocumentPeriodFacts.PeriodState` — `IDocumentPeriodReopenStore.cs:84`;
  - прямі конструкції в тестах враховано (параметр необовʼязковий).
- **D-3:**
  - `DocumentSheetCopyStore.cs:191-249` перечитано (рядки зсунулись проти попереднього звіту: блок `CalculationIssues` — `:228-230`);
  - `DocumentDeletionStore.cs:297-298` підтверджено.
- **D-11:**
  - `SourceEventSyncJob.cs:1562-1588` перечитано;
  - `PK_CellFormulaOverride` — `CellFormulaOverrideConfiguration.cs:27`;
  - `FormulaOverrideStore.cs:169-196` (`ReadKeysAsync`) без зʼєднання з рядком.
- **D-12:**
  - `03-archive-proc.sql:691, 887-898, 978-988, 1004, 1137-1139, 1144` — `grep -n`;
  - `usp_ArchiveYear` звіряє частини на `:571-577`;
  - кейс 1 падає без кроку 2, кейс 2 — без кроку 3.
- **D-13:** `OutboxDispatcher.cs:237-240, 278, 385-401` перечитано.
- **D-4:**
  - `SourceEventAttributeValueWriter.cs:57-62, 146-160, 200-206` перечитано (рядки зсунулись проти попереднього звіту);
  - колація тестової бази `Latin1_General_100_CI_AS_SC` (`SqlServerFixture.cs:354`) — повноширинні = звичайні, тож тест відтворює 8672.
- **D-5:**
  - тіло міграції перечитано повністю;
  - `D450DocumentOwnerIndex.EngineEditionExpression` — `public const` (`:30`), тож посилання компілюється;
  - кейс «наявний індекс» падає 1913 без guard.
- **Що виправлено чи вилучено під час перевірки:**
  - D-2 звужено (PATCH — без зміни, вердикт обґрунтовано);
  - з D-1 вилучено неперевірене твердження про `rpt`-вʼюхи;
  - з D-3 прибрано пропозицію сіду: прибирання сиріт тепер одноразовою міграцією;
  - у D-5 прибрано умову «лише якщо реліз не видано» — guard робить правку безпечною в обох випадках;
  - до ліміту не включено: `RecalculationJob` (статус у памʼяті), сироти `RowWindowValue`/`CalculationIssue` при переносі, сторож колонок stable-views — див. «Відкинуто».
