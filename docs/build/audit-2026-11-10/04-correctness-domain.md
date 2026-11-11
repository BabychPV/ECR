# 04 — Коректність бізнес-логіки (Domain / Application / Calculations / Expressions / Excel / Jobs)

**Область:** `src/Ecr.Domain`, `src/Ecr.Application`, `src/Ecr.Calculations`, `src/Ecr.Expressions`, `src/Ecr.Adapters.Excel`, `src/Ecr.Infrastructure/Jobs`. Де дефект бізнес-правила фізично живе у сховищі (`src/Ecr.Infrastructure/Persistence/*`), знахідку наведено з точним місцем там.
**SHA `main`:** `f446e9b29`. **Дата:** 2026-11-11. Лише читання, нічого не збиралося й не запускалося.

**Охоплено повністю:** рушій формул (`Evaluator`, `TemplateFunctions`, `StrictDecimalArithmetic`, `SliceEvaluationContext`, `RecalculationService` фаза формул); стан періоду (`PeriodStateCalculator`, `YearGraceWindow`); виняток «місяць відкрито для документа» (`DocumentPeriodReopen*`, усі споживачі правила); Workflow (Submit/Approve/Recall/Reopen, `ClosedPeriodGuard`, `SubmissionPeriodSelector`); історія дозволів (j) (`PermitTimeline`, `SaveCellSplitsHandler`, `PermitHistoryHandlers`, `PatchCellsHandler.PermitValidity`); імпорт/експорт Excel (`ImportDiffBuilder`, `ExcelImporter` apply, `ExcelExporter.WriteValue/WriteFormulas`); видалення аркушів у міграції версії (`DocumentVersionMigrationStore.WorkflowSql/InstancesSql`).
**Поверхово:** `GenericCalculationModule`, `CalculationOrchestrator`, `RecalculationJob` (лише гейти періодів), `PeriodStateJob`, `CollectedFold`/`MaterializeCollectedDataJob`, `RegistrySyncJob`, курсори черги погодження й аудиту, `NotificationJob`.
**Не охоплено:** `SourceEventSyncJob` (2196 рядків) детально, `RowWindowFetchJob`, звіти (`Reporting/*`), методологічні правила категорій поза F13 #806.

Частину напрямів (рушій, міграція+Excel, workflow, PI/дозволи) спершу прочесали чотири допоміжні агенти; кожну знахідку нижче перевірено в коді заново (див. «Самоперевірка»).

## Зведена таблиця

| ID | Важливість | Категорія | Назва | Зусилля | Ризик зміни | Рішення PO |
|---|---|---|---|---|---|---|
| C-1 | Висока | correctness | Формула, що тепер дає помилку, лишає в комірці старе число, і залежні формули рахують від нього | S | середній | так (як зберігати помилку) |
| C-2 | Висока | data-integrity | Міграція версії, що прибирає аркуш із копіями, падає на FK і лишає проєкт під маркером міграції | S | середній | ні |
| C-3 | Висока | correctness | Застосування імпорту Excel блокує й перевіряє стан ОРИГІНАЛУ аркуша замість копії | M | середній | ні |
| C-4 | Висока | correctness | Імпорт відмовляє всю книгу через комірки з формулою комірки/рядка (FormulaScope.Cell/Row) у вхідній колонці | S | низький | ні |
| C-5 | Висока | correctness | `ColumnDef.DefaultValue` не потрапляє у формули сервера, хоча сітка його показує (02b §6.3) | M | середній | так |
| C-6 | Середня | correctness | Ціль подання (D-441) ігнорує виняток «місяць відкрито для документа» — кнопка «Подати» не пропонує цей місяць | S | низький | ні |
| C-7 | Середня | correctness | Відкликання (Recall) перевіряє право подання без коду аркуша, періоду й участі (D-443/D-214) — подавач не може відкликати | M | середній | ні |
| C-8 | Середня | correctness | Reopen аркуша вирішує право ДО транзакції (TOCTOU, той самий клас, що R5-W1/W1-05 у Approve) | S | низький | ні |
| C-9 | Середня | data-integrity | Скасування дозволу шапки лишає частини поділу, які згодом зараховуються як поділ для іншого дозволу | S | низький | ні |
| C-10 | Середня | correctness | Місяць поділу дозволу виводиться з `PeriodKey.Sequence` — неправильно для квартальних/річних проєктів | M | середній | так |
| C-11 | Середня | correctness | Посилання `[Period:-n].[T].[WHERE …]` фільтрує рядки ПОТОЧНОГО періоду — результат хибний | S | низький | так |
| C-12 | Середня | correctness | Незмінена Lookup-комірка відхиляє весь імпорт, якщо імпортер не бачить реєстр (Deny / без Registry.View) | M | середній | ні |
| C-13 | Низька | data-integrity | Лічильники посилань запису довідника не бачать `doc.PermitSegment`/`doc.CellPermitPart` — дозвіл з історії можна м'яко видалити, «де використовується» показує 0 | S | низький | ні |

---

## Знахідки

### C-1 — Висока — Формула, що тепер дає помилку, лишає в комірці старе число, і залежні формули рахують від нього
**Категорія:** correctness
**Де:** `src/Ecr.Application/Recalculation/RecalculationService.cs:1383-1389` (гілка помилки), порівняти з `:1397-1410` (гілка `Null`, виправлена RC14 P2-D, коміт `50b89ef1a`) і `:1446` (`values[key] = value;` — оновлення контексту, до якого гілка помилки не доходить). Розбіжність із переглядом: `src/Ecr.Application/Recalculation/RowLocalPreview.cs:148-153`.
**Проблема:**
```csharp
// RecalculationService.cs:1386
if (result.Value.IsError)
{
    continue;
}
```
Помилка не пишеться і **не кладеться в контекст**: у `values` лишається значення, прочитане зі сховища (старе обчислене число). Наступні формули того ж прогону читають це старе число, а сама комірка в базі лишається зі старим `ValueNumeric` і `IsCalculated = 1`. Для `Null` цю саму ваду вже виправили (RC14 P2-D: «обчислене значення, що лежало в комірці, лишалося жити»), для помилки — ні. Перегляд рядка (`RowLocalPreview.cs:148-153`) робить інакше — `values.Remove(key)`, тобто залежні бачать порожнє: прев'ю і фонова задача розходяться.
**Сценарій відтворення:** шаблон: `C = [A]/[B]`, `D = [C]*2`. Ввести A=10, B=2 → C=5, D=10 збережено. Змінити B на 0.
Фактично: C = `#DIV/0` → пропущено, у базі лишається 5; D читає 5 з контексту → 10 → «без змін» → нічого не пишеться. Сітка: B=0, C=5, D=10, жодної помилки. Те саме для `#REF` (зник запис довідника в `REGFIELD`) і `#BUDGET`.
Очікувано (`docs/build/02b-expressions.md` §6.4, рядки 520-525): «Помилка **поширюється** через операції: `#DIV/0 + 1 = #DIV/0`» і «Комірка з помилкою зберігається як `IsEmpty = 0`, `ValueString = '#DIV/0'`, `IsCalculated = 1` — щоб її було видно у звіті, а не «просто порожньо»». Щонайменше: старе число не лишається, залежні не рахуються від нього.
**Вплив:** будь-який документ, де вхід формули стає нулем/порожнім дільником чи зникає запис довідника. Тихо хибні числа у звіті (зокрема у викидах), без жодного сигналу. Ймовірність — регулярно (ділення на «кількість», «годин роботи» тощо).
**Зміна — покроково:**
1. Файл `src/Ecr.Application/Recalculation/RecalculationService.cs` — гілка помилки поводиться як гілка `Null` (очищає раніше обчислене) і кладе помилку в контекст (залежні отримують помилку → теж очищаються, поширення за §6.4):
```diff
-            // ⛔ Помилка обчислення НЕ записується як значення. `#REF` у
-            // комірці — це не число, і покласти його в `ValueNumeric`
-            // означало б або нуль, або текст у числовій колонці.
-            if (result.Value.IsError)
-            {
-                continue;
-            }
+            // ⛔ Помилка обчислення НЕ записується як значення. `#REF` у
+            // комірці — це не число, і покласти його в `ValueNumeric`
+            // означало б або нуль, або текст у числовій колонці.
+            // ⛔ C-1 (аудит 2026-11-10): але й старе число лишати не можна — раніше ОБЧИСЛЕНА комірка
+            // очищається (як у гілці Null, RC14 P2-D), а в контекст іде сама помилка: залежні формули
+            // цього прогону отримують її (02b §6.4 «помилка поширюється»), а не застаріле число з бази.
+            if (result.Value.IsError)
+            {
+                var errorKey = new CellKey(0, table.Id, rowKey, columnDefId);
+                values[errorKey] = result.Value;
+                if (stored.GetValueOrDefault(errorKey) is { IsCalculated: true, IsEmpty: false })
+                {
+                    upserts[new CellAddress(periodKey, rowId, columnDefId)] = new CellRecord(
+                        new CellAddress(periodKey, rowId, columnDefId),
+                        table.Id,
+                        new CellValueData { IsEmpty = true, IsCalculated = true });
+                }
+
+                continue;
+            }
```
(Якщо PO обере варіант «б» нижче — замість `IsEmpty = true` писати `IsEmpty = false, ValueString = result.Value.ErrorCode, IsCalculated = true`; це вимагає перевірки, що `ExcelExporter.WriteValue`, звіти й валідація не читають `ValueString` числової колонки як дані.)
2. Файл `src/Ecr.Application/Recalculation/RowLocalPreview.cs:148-153` — для узгодженості з фоновою задачею замінити `values.Remove(key);` на `values[key] = evaluated.Value;` (залежні в прев'ю теж бачать помилку). Коментар оновити.
3. `docs/build/02b-expressions.md` §6.4 — дописати, як реально зберігається помилка (за рішенням PO), щоб документ не обіцяв `ValueString = '#DIV/0'`, якщо обрано «а».
**Тест-доказ:** `tests/Ecr.Application.Tests/Recalculation/CascadeRecalculationTests.cs`, новий кейс `Помилка_формули_очищає_раніше_обчислене_і_поширюється_на_залежні`: стан сховища — A=10, B=0, C=5 (`IsCalculated`), D=10 (`IsCalculated`); формули `C=[A]/[B]`, `D=[C]*2`; `RecalculateAllAsync`; перевірити, що в `ApplyAsync/ApplyBatchAsync` пішли записи C і D з `IsEmpty = true, IsCalculated = true`. Без кроку 1 тест падає: записів немає взагалі (C пропущено, D=10 «без змін»).
**Перевірка після впровадження:** `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~Recalculation"`; окремо `ExpressionBudgetReachabilityTests` і `RegistryFieldRecalculationTests` (їхні цілі порожні → `written == 0` має лишитися). Стенд: документ із `[A]/[B]`, поставити B=0 → C порожня, залежна теж.
**Ризики і побічні ефекти:** більше записів у прогоні лише для комірок, що переходять у помилку (одноразово). Тести фіксованої кількості запитів не зачіпаються (запис іде тим самим пакетом `upserts`). Помилка в `values` поширюється на агрегати (`SUM` над колонкою з помилкою → помилка) — це і є §6.4, але кількість порожніх комірок у документах із «звичними» `#DIV/0` зросте; варто попередити UAT.
**Обґрунтування:** мінімальна зміна за зразком уже прийнятого RC14 P2-D; альтернатива «лишити як є» суперечить 02b §6.4 і дає тихо хибні числа.
**Рішення PO:** Як зберігати комірку з помилкою формули: (а) очищати (порожня, `IsCalculated`), (б) зберігати код помилки `ValueString='#DIV/0'` як обіцяє 02b §6.4 (видно у звіті, але потребує правок експорту/звітів)? Рекомендація — (а) зараз, (б) окремою задачею.
**Впевненість:** підтверджено читанням.
**Залежності:** немає.

### C-2 — Висока — Міграція версії, що прибирає аркуш із копіями, падає на FK і лишає проєкт під маркером міграції
**Категорія:** data-integrity
**Де:** `src/Ecr.Infrastructure/Persistence/DocumentVersionMigrationStore.cs:1863-1869` (`WorkflowSql`, видалення `doc.DocumentSheet`), `:1760-1765` (`InstancesSql`, видалення `doc.TableInstance`), порядок етапів `:611` (Workflow) → `:748` (Instances); FK `src/Ecr.Infrastructure/Persistence/Migrations/20261103144721_RS1_SheetCopies.cs:202-210` (`FK_TableInstance_SheetCopy`, `ReferentialAction.Restrict`). Код міграції версії взагалі не згадує копій (`git grep -i "sheetcopy\|CopyOfId" src/Ecr.Application/Documents/VersionMigration src/Ecr.Infrastructure/Persistence/DocumentVersionMigrationStore.cs` — порожньо).
**Проблема:**
```sql
-- WorkflowSql, :1865
DELETE s
FROM   doc.DocumentSheet s
JOIN   @docs d ON d.Id = s.DocumentId
WHERE  @fromPeriod = 0
  AND  NOT EXISTS (SELECT 1 FROM @sheetMap m WHERE m.o = s.SheetDefId);
```
Рядки копій мають `SheetDefId` оригіналу, тож видаляються разом із ним. Але екземпляри таблиць копій (`doc.TableInstance.SheetCopyId → DocumentSheet.Id`) видаляються лише пізніше, в `InstancesSql` (`:748`). На етапі Workflow `DELETE` порушує `FK_TableInstance_SheetCopy` (547) → транзакція відкочується.
**Сценарій відтворення:** документ з аркушем S і копією «S 2» (порожньою — копія завжди має екземпляри таблиць). Нова версія шаблону не має аркуша S (планувальник дає `Removed`/`Guarded` з 0 втрат — `VersionMigrationPlanner.cs:526-529`, тож режим Safe не відмовляє). Запустити міграцію документа.
Фактично: етап Workflow падає з помилкою FK, транзакція відкочується; маркер міграції проєкту, поставлений першою транзакцією, лишається (потребує перевірки на стенді: чи знімає його обробник відмови задачі — у коді знято лише в `FinishSql`). Кожен повтор падає так само.
Очікувано: екземпляри таблиць копій прибрано до рядків `DocumentSheet` (той самий порядок, що в `DocumentSheetCopyStore`), міграція проходить.
**Вплив:** будь-який проєкт із копіями аркушів (RS, з 03.11), що мігрує на версію без одного з аркушів. Проєкт стає недоступним для запису (маркер) до ручного втручання DBA.
**Зміна — покроково:**
1. Файл `src/Ecr.Infrastructure/Persistence/DocumentVersionMigrationStore.cs` — прибрати `DELETE s` з `WorkflowSql` і виконати його ПІСЛЯ видалення екземплярів (кінець `InstancesSql`; `@docs`, `@sheetMap`, `@fromPeriod` оголошені в спільній преамбулі команди, `:1475`):
```diff
         -- RPT-STABLE Part B: у режимі «з місяця» склад документа не втрачає аркушів, яких нова версія не має: їх
         -- ще потребують періоди до X (екземпляри таблиць і стани лишилися на старих Id).
-        DELETE s
-        FROM   doc.DocumentSheet s
-        JOIN   @docs d ON d.Id = s.DocumentId
-        WHERE  @fromPeriod = 0
-          AND  NOT EXISTS (SELECT 1 FROM @sheetMap m WHERE m.o = s.SheetDefId);
-
+        -- ⛔ C-2: видалення складу документа — у InstancesSql, ПІСЛЯ екземплярів таблиць: екземпляри копій
+        -- тримають FK_TableInstance_SheetCopy (Restrict) на рядок копії.
         UPDATE s
```
і в кінець `InstancesSql` (після блоку `DELETE ti … UPDATE ti …`):
```diff
+
+        -- C-2: склад документа без аркушів, яких нова версія не має (оригінали й копії одним оператором:
+        -- самопосилання FK_DocumentSheet_CopyOf перевіряється в кінці оператора).
+        DELETE s
+        FROM   doc.DocumentSheet s
+        JOIN   @docs d ON d.Id = s.DocumentId
+        WHERE  @fromPeriod = 0
+          AND  NOT EXISTS (SELECT 1 FROM @sheetMap m WHERE m.o = s.SheetDefId);
```
2. Перевірити (читанням), що `@instances` у `InstancesSql` містить і екземпляри копій (заповнення `@instances` не фільтрує `SheetCopyId`); якщо фільтрує — додати.
**Тест-доказ:** `tests/Ecr.Api.Tests/DocumentVersionMigrationTests.cs` (або новий `DocumentVersionMigrationSheetCopyTests.cs`): документ з аркушем S і копією, нова версія без S, міграція → 200/Completed, `doc.DocumentSheet` і `doc.TableInstance` документа не містять S і копії, маркер знято. Без фіксу — помилка 547 на етапі Workflow.
**Перевірка після впровадження:** `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~DocumentVersionMigration"`; `dotnet test tests/Ecr.Architecture.Tests`. Стенд: проєкт з копією аркуша → міграція на версію без аркуша.
**Ризики і побічні ефекти:** між Workflow і Instances виконуються перевірка й валідація — вони `DocumentSheet` не читають (у файлі інших звернень до `doc.DocumentSheet` немає, `git grep -n DocumentSheet` — лише `:1866`, `:1873`). Кількість запитів незмінна (той самий пакет).
**Обґрунтування:** перестановка оператора замість окремого `DELETE ti … WHERE SheetCopyId IN …` — менше коду, одне місце правила.
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням (FK і порядок етапів); «застряглий маркер» — потребує перевірки на стенді.
**Залежності:** немає.

### C-3 — Висока — Застосування імпорту Excel блокує й перевіряє стан ОРИГІНАЛУ аркуша замість копії
**Категорія:** correctness
**Де:** `src/Ecr.Adapters.Excel/ExcelImporter.cs:586-610` (замки й стани за `sheetDefId`), `:838-858` (`SheetsInLockOrderAsync` зводить копії в `SheetDefId`); `src/Ecr.Application/Documents/PatchCellsHandler.Workbook.cs:600-655` (групування `Context.Table.SheetDefId`, `sheetGate.EnterEditAsync(documentId, sheet.Key, period, ct)` на `:652`), `:911` (`var status = statuses[sheetDefId];`). Для порівняння поштучний запис: `PatchCellsHandler.cs:3020-3025` — `EnterEditOfAsync(…, context.Instance.SheetCopyId, ct)`.
**Проблема:** копія аркуша має власний замок і власний стан погодження (`IAccessDecisionService.cs:499-507` `EnterEditOfAsync` → `EnterCopyEditAsync`). Шлях книги бере `EnterEditAsync(documentId, sheetDefId, …)` — замок і стан **оригіналу** — і перевіряє стан оригіналу для правок у копії.
**Сценарій відтворення:** А) оригінал «Stacks 1» Submitted/Approved за період, копія «Stacks 2» Draft. Експорт → правка комірки копії → імпорт. Перегляд показує зміну (рішення доступу копійні), Apply → 403 `deniedCells`/`DocumentSubmitted`, нічого не записано. Б) оригінал Draft; між переглядом і Apply копію подають. Apply тримає замок оригіналу (не конфліктує з винятковим замком подання копії), читає Draft оригіналу і пише в подану копію.
Очікувано: те саме правило, що в поштучному PATCH — замок і стан на (аркуш, копія).
**Вплив:** користувачі копій аркушів (RS): (А) імпорт у копію неможливий, щойно оригінал подано; (Б) рідкісна гонка, правка поданої копії без позначки.
**Зміна — покроково:**
1. `src/Ecr.Adapters.Excel/ExcelImporter.cs` — `SheetsInLockOrderAsync` повертає пари `(int SheetDefId, long? SheetCopyId)` (з `instance.SheetCopyId`, `IRowStore.cs:354`), впорядковані за `(SheetDefId, SheetCopyId ?? 0)`; цикл `:588-610`:
```diff
-                var statuses = new Dictionary<int, Ecr.Domain.Enums.DocumentStatus>();
+                var statuses = new Dictionary<(int SheetDefId, long? SheetCopyId), Ecr.Domain.Enums.DocumentStatus>();
                 var holdsAny = false;
-                foreach (var sheetDefId in sheets)
+                foreach (var (sheetDefId, sheetCopyId) in sheets)
 ...
-                        var status = holdsAny
-                            ? await sheetGate.EnterEditNoWaitAsync(documentId, sheetDefId, new PeriodKey(key), innerCt).ConfigureAwait(false)
-                            : await sheetGate.EnterEditAsync(documentId, sheetDefId, new PeriodKey(key), innerCt).ConfigureAwait(false);
+                        var status = holdsAny
+                            ? await sheetGate.EnterEditNoWaitOfAsync(documentId, sheetDefId, new PeriodKey(key), sheetCopyId, innerCt).ConfigureAwait(false)
+                            : await sheetGate.EnterEditOfAsync(documentId, sheetDefId, new PeriodKey(key), sheetCopyId, innerCt).ConfigureAwait(false);
 ...
-                            statuses[sheetDefId] = status;
+                            statuses[(sheetDefId, sheetCopyId)] = status;
```
(`monthSheets` лишається за `SheetDefId`.) Тип параметра `heldSheetStatuses`, яким словник передається в `PatchCellsHandler`, змінити відповідно.
2. `src/Ecr.Application/Documents/PatchCellsHandler.Workbook.cs` — групувати `active` за `(x.Context.Table.SheetDefId, x.Context.Instance.SheetCopyId)`; усі `sheetGate.EnterEditAsync(documentId, sheet.Key, …)` у блоці `:600-655` замінити на `sheetGate.EnterEditOfAsync(documentId, sheet.Key.SheetDefId, …, sheet.Key.SheetCopyId, ct)`; `RowWriteLock.EnterAsync(…, ct, sheet.Key.SheetCopyId)` (перевантаження з копією вже існує, `PatchCellsHandler.cs:3016`); `statuses` — словник за тим самим ключем; `:911` → `statuses[(sheetDefId, context.Instance.SheetCopyId)]`; `stillApprover` у `EnsureWorkbookSheetsEditableAsync` — за тим самим ключем.
**Тест-доказ:** `tests/Ecr.Adapters.Tests/Excel/ExcelSheetCopiesTests.cs`, новий кейс `Apply_пише_в_копію_чернетку_коли_оригінал_поданий`: оригінал Submitted, копія Draft, книга змінює комірку копії → Apply записує 1 комірку. Без фіксу — `AccessDeniedException` (стан оригіналу). Другий кейс з підміною гейта: перевірити, що викликано `EnterCopyEditAsync(…, copyId, …)`.
**Перевірка після впровадження:** `dotnet test tests/Ecr.Adapters.Tests --filter "FullyQualifiedName~Excel"`; `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~Workbook"`; `dotnet test tests/Ecr.Architecture.Tests`.
**Ризики і побічні ефекти:** порядок узяття замків змінюється (з копіями) — тримати зростання `(SheetDefId, CopyId)`, як у `RecalculationService.EnterSheetsAsync`, щоб не завести дедлок. Кількість звернень та сама (один замок на екземпляр).
**Обґрунтування:** одне правило для всіх писачів; альтернатива «заборонити імпорт у копії» звужує функцію.
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням.
**Залежності:** немає.

### C-4 — Висока — Імпорт відмовляє всю книгу через комірки з формулою комірки/рядка у вхідній колонці
**Категорія:** correctness
**Де:** `src/Ecr.Adapters.Excel/ImportDiffBuilder.cs:192` (пропуск формул лише за колонкою), `:831-832` (`IsCalculated` = тип колонки або `IsReadOnly`), `:335-344` (відмова `deny.CalculatedCell`); експорт пише формули всіх областей: `src/Ecr.Adapters.Excel/ExcelExporter.cs:1222-1257` (`table.Formulas.Where(f => !f.IsDeleted && f.ColumnDefId is not null)` + `Rows()` `:1263-1275`); «все або нічого» `ExcelImporter.cs:1091-1131`.
**Проблема:**
```csharp
// ImportDiffBuilder.cs:192
if (IsCalculated(definition) && cell.HasFormula)
{
    continue;
}
```
Формула області `Cell`/`Row` (WW-02) живе у **вхідній** (Decimal) колонці, тож `IsCalculated(definition)` = false. Excel перераховує її кешоване значення, щойно користувач змінить вхід → значення відрізняється від збереженого → рішення доступу `Deny(CalculatedCell)` → відмова комірки → AN-143 відхиляє весь пакет.
**Сценарій відтворення:** таблиця з вхідною колонкою VOL і рядком TOTAL, формула комірки `VOL = SUM(R1..R3)` (без `AllowManualOverride`). Експорт → змінити R1.VOL 10 → 20 (TOTAL у книзі 60 → 70) → імпорт. Фактично: TOTAL відхилено «Editing the cell is not allowed: CalculatedCell», Apply недоступний. Очікувано: TOTAL пропущено, як формули колонок (обґрунтування V-10 у коментарі `:185-191` дослівно те саме).
**Вплив:** кожен шаблон із підсумковими рядками на формулах комірки — імпорт Excel фактично не працює після будь-якої правки входу.
**Зміна — покроково:**
1. `src/Ecr.Adapters.Excel/ImportDiffBuilder.cs` — у `Build` після `var period = new PeriodKey(periodKey);` зібрати цілі формул комірки/рядка (без D-461 override — їх обробляє гілка `replacesFormula`):
```diff
         var period = new PeriodKey(periodKey);
+
+        // ⛔ C-4: формула КОМІРКИ/РЯДКА (WW-02) стоїть у вхідній колонці, і експорт пише її в комірку
+        // (`ExcelExporter.WriteFormulas`) так само, як колонкову: те саме правило V-10.
+        var formulaCells = table.Formulas
+            .Where(f => !f.IsDeleted && f.Scope != FormulaScope.Column && f.ColumnDefId is not null && !f.AllowManualOverride)
+            .Select(f => (RowKey: Ecr.Application.Recalculation.FormulaOutputs.RowKeyOf(table, f), ColumnDefId: f.ColumnDefId!.Value))
+            .Where(t => t.RowKey is not null)
+            .ToHashSet();
```
і пропуск:
```diff
-                if (IsCalculated(definition) && cell.HasFormula)
+                if (cell.HasFormula
+                    && (IsCalculated(definition) || formulaCells.Contains((row.RowKey, column.ColumnDefId))))
                 {
                     continue;
                 }
```
(`using Ecr.Domain.Enums;` для `FormulaScope`, якщо його ще немає.)
**Тест-доказ:** `tests/Ecr.Adapters.Tests/Excel/`, новий `ExcelImportCellFormulaTests.Змінений_вхід_не_відхиляє_книгу_через_формулу_комірки`: таблиця з формулою `Cell` на TOTAL; книга з експорту, змінено R1 і кешоване TOTAL; `PreviewAsync` → `Rejected` порожній, зміна лише R1. Без фіксу — відмова на TOTAL.
**Перевірка після впровадження:** `dotnet test tests/Ecr.Adapters.Tests --filter "FullyQualifiedName~Excel"`.
**Ризики і побічні ефекти:** якщо користувач свідомо ввів у таку комірку число (формула стерта) — `HasFormula = false`, гілка не спрацьовує, як і раніше (відмова `CalculatedCell` лишається правильною). Нуль нових запитів.
**Обґрунтування:** узгоджує імпорт з експортом; альтернатива — не експортувати формули комірок — гірше для користувача.
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням (у `tests/Ecr.Adapters.Tests` немає жодного `FormulaScope.Cell`).
**Залежності:** немає.

### C-5 — Висока — `ColumnDef.DefaultValue` не потрапляє у формули сервера, хоча сітка його показує
**Категорія:** correctness
**Де:** `src/Ecr.Application/Recalculation/SliceEvaluationContext.cs:546-559` (`Value`), `RowLocalPreview.cs:102`, `ValidationEngine.cs:356` (обидва передають `ExpressionValue.Null` як значення за замовчуванням); єдині читачі `DefaultValue` у серверному коді — DTO зрізу (`GetTableSliceHandler.cs:349`, коментар `:380-381`). Клієнт підставляє його: `src/Ecr.Web/src/features/grid/DocumentGrid.tsx:3988` (`row.cells[column.code] ?? column.defaultValue ?? ''`).
**Проблема:**
```csharp
// SliceEvaluationContext.cs:558
return _values.TryGetValue(key, out var value) ? value : ExpressionValue.Null;
```
`docs/build/02b-expressions.md:488-491` (§6.3): «рядка `CellValue` немає → `ColumnDef.DefaultValue`, або `null`»; ФВ-3.8 «Нова комірка бере ColumnDef.DefaultValue». Сервер ніколи не підставляє значення за замовчуванням.
**Сценарій відтворення:** колонка Factor з `DefaultValue = "1.5"`, формула `Emission = [Qty]*[Factor]`. Ввести Qty=10, Factor не чіпати. Сітка показує Factor 1.5, сервер рахує `10 * null = null` → Emission порожня; `SUM` колонки Factor = 0.
**Вплив:** будь-який шаблон із дефолтами у вхідних колонках, від яких залежать формули; за D-474 (8)(iv) у новій версії Land з'являється `DefaultValue` `NOTPASSEDOVERHAUL` — він не діятиме ні у формулах, ні (окремо перевірити) у входах методологій.
**Зміна — покроково (варіант «а» — підставляти при читанні):**
1. `SliceEvaluationContext` — у конструкторі зібрати `Dictionary<int, ExpressionValue> _defaults` з `_tables` (`ColumnDef.DefaultValue` через `CellValueMapping`/розбір за `DataType`, інваріантна культура). `Value(...)`: якщо ключа немає **і** рядок існує в `_rowsByTable` для відповідного періоду — повертати дефолт колонки; явна порожнеча (`IsEmpty=1`) лежить у `_values` як `Null` і дефолт не отримує (§6.3).
2. Те саме для `RowLocalPreview.cs:102` і `ValidationEngine.cs:356` (там уже є параметр «значення за замовчуванням» — передати дефолт колонки).
3. Тест-хелпер `TestEvaluationContext.Defaults` уже моделює правило — використати ті самі очікування.
**Тест-доказ:** `tests/Ecr.Application.Tests/Recalculation/CascadeRecalculationTests.cs`, кейс `Незаповнена_комірка_дає_формулі_DefaultValue_колонки`: Factor без `CellValue`, `DefaultValue="1.5"`, Qty=10 → запис Emission=15. Другий кейс: Factor з `IsEmpty=1` → Emission порожня. Без фіксу перший падає (Emission не записано).
**Перевірка після впровадження:** `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~Recalculation|FullyQualifiedName~Validation"`.
**Ризики і побічні ефекти:** змінюються числа в чинних документах із дефолтами (після наступного перерахунку) — потрібне повідомлення UAT. Нуль нових запитів (дефолти з метаданих).
**Обґрунтування:** варіант «б» — матеріалізувати дефолт при створенні рядка — змінює «не заповнювали» на «заповнено» і ламає §6.3 (R-B4), тому рекомендовано «а».
**Рішення PO:** Чи мають формули/валідація/методології використовувати `DefaultValue` незаповненої комірки (02b §6.3), чи `DefaultValue` — лише підказка в сітці (тоді треба виправити 02b §6.3 і ФВ-3.8, а клієнт має показувати дефолт інакше, ніж введене значення)? Варіанти: (а) підставляти при читанні (рекомендовано); (б) матеріалізувати при створенні рядка; (в) лише підказка.
**Впевненість:** підтверджено читанням.
**Залежності:** немає.

### C-6 — Середня — Ціль подання (D-441) ігнорує виняток «місяць відкрито для документа»
**Категорія:** correctness
**Де:** `src/Ecr.Infrastructure/Persistence/WorkflowStore.cs:254-313` (`GetSubmissionCalendarAsync`, стан на `:309`), споживач `src/Ecr.Application/Workflow/SubmissionPeriodSelector.cs:37-55` (`Choose`), клієнт подає лише запропоноване сервером: `src/Ecr.Web/src/features/workflow/SheetActions.tsx:848-852`.
**Проблема:**
```csharp
// WorkflowStore.cs:309
window is null ? p.State : PeriodStates.Effective(p, utcNow, window),
```
Правило `DocumentPeriodReopenRule.Apply` застосовано в `AccessDecisionService` (подання дозволене — тест `tests/Ecr.Infrastructure.Tests/Security/DocumentPeriodReopenTests.cs:78-79`: `Assert.NotEqual(EditDenyReason.PeriodClosed, own.Reason)`), `PeriodStore`, `RecalculationJob`, `ClosedPeriodGuard` — але не в календарі подання. Для закритого місяця з винятком `Choose` бачить `Closed` → `Submittable = false`.
**Сценарій відтворення:** місяць 202610 Closed, аркуш Draft. Адміністратор відкриває місяць для документа (`OpenDocumentPeriodReopenHandler`). Користувач править дані → «Подати»: `GET …/submission-target` не пропонує 202610 (немає в `periods`, `targetPeriodKey` інший/`null`) → кнопка не подає цей місяць. Очікувано: 202610 у стані `Grace`, пропонується (D-441 «найраніший неподаний Open/Grace»), бо сервер саме подання дозволяє.
**Вплив:** функція «відкрити місяць для одного документа» (08.11 (i)) не доводиться до подання через UI; користувач застрягає з виправленими, але не поданими даними до кінця винятку.
**Зміна — покроково:**
1. `src/Ecr.Infrastructure/Persistence/WorkflowStore.cs` — після читання `statuses` (`:286-291`):
```diff
+        // ⛔ C-6: місяць, відкритий лише для ЦЬОГО документа (08.11 (i)), — Grace і в календарі подання,
+        // як у рішенні про подання (`AccessDecisionService`, `DocumentPeriodReopenRule`).
+        var reopened = (await db.DocumentPeriodReopens
+                .AsNoTracking()
+                .Where(x => x.DocumentId == documentId && x.ClosedAt == null && x.OpenUntil > utcNow)
+                .Select(x => x.PeriodKeyValue)
+                .ToListAsync(ct)
+                .ConfigureAwait(false))
+            .ToHashSet();
```
і на `:309`:
```diff
-                    window is null ? p.State : PeriodStates.Effective(p, utcNow, window),
+                    Domain.Services.DocumentPeriodReopenRule.Apply(
+                        window is null ? p.State : PeriodStates.Effective(p, utcNow, window),
+                        reopened.Contains(p.PeriodKeyValue)),
```
**Тест-доказ:** `tests/Ecr.Infrastructure.Tests/Security/DocumentPeriodReopenTests.cs`, новий кейс: після `Open(...)` викликати `new WorkflowStore(db, …).GetSubmissionCalendarAsync(doc.DocumentId, doc.SheetDefId, T0.AddHours(1), ct)` → період має `State == Grace`; для іншого документа — `Closed`. Без фіксу перший `Closed`.
**Перевірка після впровадження:** `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~DocumentPeriodReopen"`; `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~SubmissionTarget"`.
**Ризики і побічні ефекти:** +1 запит до `GET …/submission-target`; якщо є тест фіксованої кількості запитів цього ендпойнта — стелю НЕ піднімати, а вбудувати `EXISTS` у запит періодів (підзапит у `Select` як у `PeriodStore.cs:178`).
**Обґрунтування:** одне правило в усіх рішеннях про період (коментар `DocumentPeriodReopenRule` саме це вимагає).
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням.
**Залежності:** немає.

### C-7 — Середня — Recall перевіряє право подання без коду аркуша, періоду й участі
**Категорія:** correctness
**Де:** `src/Ecr.Application/Workflow/RecallSheetHandler.cs:232-238` (`HasSubmitGrant`), виклики `:108` (дія) і `:211` (`CanRecallAsync`, кнопка); правила `src/Ecr.Application/Security/EditRules.cs:707` (звужені шари діють лише при `AppliesTo(SheetCode, Period)`), `:757-766` (без ролі на проєкт — `ParticipationLevel`), `AccessProfile.cs:390-395` (`AppliesTo` з `null` → false). Подання збагачує контекст: `src/Ecr.Infrastructure/Security/AccessDecisionService.cs:2765-2766` (`WithSheetCodeAsync`, `WithParticipationAsync`).
**Проблема:**
```csharp
private static bool HasSubmitGrant(AccessProfile profile, int? projectId, int sheetDefId)
    => !profile.IsSimulation && projectId is { } project
       && EditRules.MeetsSubmit(profile, project,
           EditRules.Effective(profile, new CellAccessContext { ProjectId = project, SheetDefId = sheetDefId }));
```
Контекст без `SheetCode`, `Period`, `ParticipationLevel`. Учасник документа (D-443) без ролі на проєкт отримує `None`; користувач зі звуженою роллю (D-214, лише аркуші {S}) — шар не діє → `None`.
**Сценарій відтворення:** (А) власник надає P участь рівня Submit; P подає аркуш (204), далі `POST …/recall` → 403 `recallDenied`, кнопки немає. (Б) роль N звужена до аркуша S з грантом Submit; N подає S, відкликати не може. Очікувано: дозволено — клас прямо каже «відкликання не має вимагати від автора більше, ніж подання» (коментар `:230-231`, D-285).
**Вплив:** учасники документа й користувачі звужених ролей — не можуть виправити помилково поданий аркуш без погоджувача.
**Зміна — покроково:**
1. `src/Ecr.Application/Security/IAccessDecisionService.cs` — новий метод:
```csharp
/// <summary>D-285: чи має користувач повноваження подання цього аркуша (рівень Submit або Write + Document.Submit) у
/// повному контексті рішення — код аркуша, період, участь (D-443, D-214), без перевірки стану аркуша.</summary>
public Task<bool> HasSubmitGrantAsync(
    AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, long? sheetCopyId, CancellationToken ct);
```
2. `src/Ecr.Infrastructure/Security/AccessDecisionService.cs` — реалізація:
```csharp
public async Task<bool> HasSubmitGrantAsync(
    AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, long? sheetCopyId, CancellationToken ct)
{
    ArgumentNullException.ThrowIfNull(profile);
    if (profile.IsSimulation) return false;
    var context = await BuildContextAsync(documentId, periodKey, sheetDefId, columnDefId: 0,
        evaluateAccessWindow: false, ct, sheetCopyId).ConfigureAwait(false);
    context = await WithSheetCodeAsync(profile, documentId, context, ct).ConfigureAwait(false);
    context = await WithParticipationAsync(profile, documentId, context, ct).ConfigureAwait(false);
    return EditRules.MeetsSubmit(profile, context.ProjectId,
        EditRules.Effective(profile, context with { TableDefId = 0, ColumnDefId = 0 }));
}
```
3. `src/Ecr.Application/Workflow/RecallSheetHandler.cs` — `:108` і `:211`:
```diff
-        if (!HasSubmitGrant(profile, projectId, sheetDefId))
+        if (projectId is null
+            || !await access.HasSubmitGrantAsync(profile, documentId, sheetDefId, key, sheetCopyId, ct).ConfigureAwait(false))
```
і видалити приватний `HasSubmitGrant` (`:230-238`).
**Тест-доказ:** `tests/Ecr.Infrastructure.Tests/Security/` новий `RecallParticipantTests`: учасник рівня Submit без ролі — подає, потім `RecallSheetHandler.HandleAsync` → без винятку, стан Draft. Без фіксу — `AccessDeniedException recallDenied`. Другий кейс — звужена роль.
**Перевірка після впровадження:** `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~Recall"`; `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~Recall|FullyQualifiedName~SubmitRight"`; `dotnet test tests/Ecr.Architecture.Tests`.
**Ризики і побічні ефекти:** тести з `Substitute.For<IAccessDecisionService>()` для Recall отримають `false` за замовчуванням — налаштувати новий метод у них. +1–2 запити на Recall (контекст рішення) — Recall рідкісний.
**Обґрунтування:** те саме збагачення, що в `CanSubmitCoreAsync`; не використовуємо `CanSubmitOfAsync`, бо він перевіряє стан аркуша (Draft), а відкликається Submitted.
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням.
**Залежності:** немає.

### C-8 — Середня — Reopen аркуша вирішує право ДО транзакції (TOCTOU)
**Категорія:** correctness
**Де:** `src/Ecr.Application/Workflow/ReopenDocumentHandler.cs:109` (рішення), `:137-150` (транзакція: `GetOrCreateOfAsync` `:146`, `state.Reopen` `:150`); вимога ролі кроку `AccessDecisionService.cs:2985-2994` (Submitted — роль поточного кроку, Approved — останнього, P2-4/D-454); еталон виправлення `ApproveSheetHandler.cs:113-129` (R5-W1/W1-05).
**Проблема:** рішення `CanReopenOfAsync` бачить крок N; якщо інший погоджувач закомітив крок N до `GetOrCreateOfAsync` у транзакції Reopen, стан читається свіжим (без конфлікту `RowVersion`) — і `Reopen` повертає в роботу аркуш, що вже на кроці N+1 (або вже `Approved`), користувачем ролі кроку N.
**Сценарій відтворення:** маршрут [A, B], аркуш Submitted на кроці 1. U (роль A) тисне Reopen → рішення «так». Між рішенням і транзакцією V (роль A) затверджує крок 1 → крок 2 (роль B). Транзакція U читає новий стан і повертає в Draft: підпис V скасовано людиною без ролі B. Очікувано: 403 (P2-4: роль кроку на момент запису).
**Вплив:** вузьке вікно гонки; наслідок — скасоване погодження не тим погоджувачем.
**Зміна — покроково:**
1. `src/Ecr.Application/Workflow/ReopenDocumentHandler.cs` — повторити рішення в транзакції ПІСЛЯ читання стану (передперевірку до транзакції лишити для швидкої відмови):
```diff
             var state = await workflow
                 .GetOrCreateOfAsync(documentId, sheetDefId, key, sheetCopyId, innerCt)
                 .ConfigureAwait(false);
+
+            // ⛔ C-8 (як R5-W1 / W1-05 у Approve): роль кроку — ПІСЛЯ читання стану, у тій самій транзакції.
+            // Коміт іншого погоджувача до читання — рішення бачить новий крок і відмовляє; після — збереження
+            // падає на RowVersion (409).
+            var inner = await access.CanReopenOfAsync(profile, documentId, sheetDefId, key, sheetCopyId, innerCt)
+                .ConfigureAwait(false);
+            if (!inner.IsAllowed)
+            {
+                throw new AccessDeniedException(
+                    "ECR-ACCS-0403",
+                    $"Повернення аркуша {sheetDefId} у роботу відхилено: {inner.Reason}.",
+                    new Dictionary<string, object?>
+                    {
+                        ["messageKey"] = "err.ECR-ACCS-0403.reopenDenied",
+                        ["sheetDefId"] = sheetDefId.ToString(System.Globalization.CultureInfo.InvariantCulture),
+                        ["reason"] = inner.Reason.ToString(),
+                        ["reasonKey"] = $"deny.{inner.Reason}",
+                    });
+            }
+
             var fromStatus = state.Status;
```
**Тест-доказ:** `tests/Ecr.Application.Tests/Workflow/` новий `ReopenTocTouTests`: підміна `IAccessDecisionService.CanReopenOfAsync` повертає Allow на першому виклику і Deny на другому (імітація кроку, що змінився); очікувати `AccessDeniedException`, `Reopen` не викликано. Без фіксу — Reopen проходить.
**Перевірка після впровадження:** `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~Reopen"`; `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~WorkflowTransaction"`.
**Ризики і побічні ефекти:** +1 рішення (кілька запитів) на Reopen — рідкісна дія. Підміни в наявних тестах мають повертати те саме рішення на обидва виклики (NSubstitute так робить за замовчуванням).
**Обґрунтування:** той самий патерн, що прийнятий в Approve.
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням.
**Залежності:** немає.

### C-9 — Середня — Скасування дозволу шапки лишає частини поділу
**Категорія:** data-integrity
**Де:** `src/Ecr.Application/Permits/History/PermitHistoryHandlers.cs:371-388` (`UndoHeaderPermitHandler`: `store.Remove(last)` без перевірки частин); для рядка є сторож — `:174-179` (`RowHasPartsOfAsync … throw PermitHistoryRules.NothingToUndo()`); перевірка «поділено» не дивиться, якому дозволу належить частина: `PermitHistorySupport.cs:388-391` (`IsSplit`).
**Проблема:** після Ship 2 (коміт `349519087`) зміна дозволу шапки посеред місяця ділить місяці таблиць без власної колонки дозволу, але скасування дозволу шапки не перевіряє, чи вже введено частини. Частини лишаються в `doc.CellPermitPart` із `RegistryEntryId` скасованого дозволу.
**Сценарій відтворення:** шапка A; додати B з 15.03.2026; поділити M3 рядка R: A=40, B=60 (сума 100). Скасувати B → успіх, частини лишились. Змінити M3 на 120 (місяць уже не спільний — дозволено). Додати C з 15.03 → березень знову спільний (A, C); у комірки 2 заповнені частини → `IsSplit` = true: PERMIT-SPLIT мовчить, подання не блокується, `rpt.v_DocumentCellsByPermit` дає 40 на A і 60 на B (дозволу вже немає в історії), сума 100 ≠ 120. Очікувано: скасування B відмовлено, як для рядка («або його місяці вже поділено»).
**Вплив:** хибний розподіл значень між дозволами у звітах і пропущене блокування подання; рідкісний, але тихий.
**Зміна — покроково:**
1. `src/Ecr.Application/Permits/History/PermitHistoryHandlers.cs` — у `UndoHeaderPermitHandler.HandleAsync` після `var last = segments[^1];` (`:371`):
```diff
         var last = segments[^1];
+
+        // ⛔ C-9: як для рядка (`RowHasPartsOfAsync`) — місяць, уже поділений із цим дозволом, не скасовується мовчки:
+        // частини лишилися б і зарахувалися б як поділ для наступного дозволу того самого місяця.
+        var monthKey = (last.ValidFrom.Year * 100) + last.ValidFrom.Month;
+        if ((await store.PartsAsync(documentId, [monthKey], ct).ConfigureAwait(false))
+            .Any(p => p.RegistryEntryId == last.RegistryEntryId))
+        {
+            throw PermitHistoryRules.NothingToUndo();
+        }
+
         var previousId = segments.Count > 1 ? segments[^2].RegistryEntryId : header.BaseEntryId;
```
**Тест-доказ:** `tests/Ecr.Api.Tests/` (поруч із `DocumentPermitHistoryTests*`) новий кейс `Скасування_дозволу_шапки_з_поділеним_місяцем_відмовлено`: шапка A → B з 15.03 → `PUT …/permit-history/splits` → `POST …/permit-history/header/undo` → 422 `permitHistoryNothingToUndo`; частини на місці. Без фіксу — 200.
**Перевірка після впровадження:** `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~PermitHistory"`.
**Ризики і побічні ефекти:** +1 легкий запит на скасування шапки. Контракт не змінюється (той самий код/ключ, що для рядка; `02-contracts.md:4671` варто доповнити «або місяць уже поділено»).
**Обґрунтування:** узгодження з уже прийнятим правилом рядка; альтернатива (видаляти частини разом зі скасуванням) мовчки знищує введені людиною числа.
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням.
**Залежності:** C-10 (та сама підсистема; файли різні).

### C-10 — Середня — Місяць поділу дозволу виводиться з `PeriodKey.Sequence`
**Категорія:** correctness
**Де:** `src/Ecr.Application/Permits/History/PermitHistorySupport.cs:381` (`DataCellsInMonth`: `period.Sequence == month.Month`), `src/Ecr.Application/Documents/PatchCellsHandler.PermitValidity.cs:461` (`MonthOf` у j3), `:561` (очищення частин), `src/Ecr.Application/Permits/History/SaveCellSplitsHandler.cs:239`, `src/Ecr.Infrastructure/Persistence/Sql/05-rpt-views.sql:1551` (`COALESCE(cd.MonthNumber, cv.PeriodKey % 100)`); живить PERMIT-SPLIT `src/Ecr.Application/Validation/PermitCoverageIssues.cs:317`, `:427`. Правило: `src/Ecr.Domain/ValueObjects/PeriodKey.cs:9-11` («тому виводити місяць арифметикою заборонено»); `PeriodKind.Quarterly/Yearly` існують (`PeriodCalendar.cs:37`, `PeriodKey.cs:83`). Правило (h) у тому ж файлі робить правильно: `PatchCellsHandler.PermitValidity.cs:154-157` (`MonthsOfPeriod(b.PeriodStart, b.PeriodEnd)`).
**Проблема:** для таблиці без місячних колонок «місяць комірки» = `PeriodKey.Sequence`; у квартальному проєкті Sequence 2 — це Q2 (квітень–червень), а не лютий.
**Сценарій відтворення:** квартальний проєкт, таблиця без місячних колонок покладається на дозвіл шапки; шапка A → B з 15.02.2026. Документ Q2 (ключ 202602): введення числа → 422 `ECR-CELL-4225 splitRequired «month 02.2026»`; Issues/Validate вимагають поділу лютого для всіх комірок Q2 (PERMIT-SPLIT блокує подання). Документ Q1 (202601), що справді охоплює A і B: `MonthOf` = січень → нічого не відмовлено, поділу не вимагається. Річний проєкт: спільним вважається лише січень.
**Вплив:** квартальні/річні проєкти з історією дозволів: хибні відмови й блокування подання в одних періодах, пропущений поділ в інших. Наявність таких проєктів на стенді — потребує перевірки (Land/HSE401 місячні).
**Зміна — покроково (рекомендований мінімум до рішення PO):**
1. Новий метод у `src/Ecr.Application/Permits/Validity/PermitMonthCoverage.cs`:
```csharp
/// <summary>Місяць періоду для комірки без місячної колонки — лише коли період дорівнює одному календарному
/// місяцю; інакше <c>null</c> (квартал/рік: поділ за місяцями до рішення PO не застосовується, C-10).</summary>
public static DateOnly? SingleMonthOf(DateOnly periodStart, DateOnly periodEnd)
    => periodStart.Year == periodEnd.Year && periodStart.Month == periodEnd.Month
        ? new DateOnly(periodStart.Year, periodStart.Month, 1)
        : null;
```
2. Кожне з місць вище замість `period.Sequence`/`PeriodKey % 100` бере `SingleMonthOf(bounds)` (межі — `IPeriodStore.FindPeriodBoundsAsync`, уже викликається в `PatchCellsHandler.PermitValidity.cs:148-150`; у `PermitHistoryReader`/`PermitCoverageIssues` передати межі періоду в `row`/параметр); `null` → комірка не бере участі в j2/j3/PERMIT-SPLIT. `DataCellsInMonth` отримує параметр `DateOnly? periodMonth` замість обчислення з `period.Sequence`. У `05-rpt-views.sql:1551` — `JOIN doc.Period` за межами, місяць лише коли `PeriodStart`/`PeriodEnd` в одному місяці (оновити `CHECKSUMS`, якщо файл під контролем).
**Тест-доказ:** `tests/Ecr.Application.Tests/Permits/` новий `PermitSplitQuarterlyTests`: квартальний період Q2, шапка A→B з 15.02, запис числа → без відмови; Issues без PERMIT-SPLIT. Без фіксу — `ECR-CELL-4225`.
**Перевірка після впровадження:** `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~Permit"`; `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~PermitHistory"`.
**Ризики і побічні ефекти:** місячні проєкти не змінюються (період = місяць). +0 запитів у PatchCells (межі вже читаються в тому ж запиті правила (h) — передати їх), +1 у `GetPermitHistory`/валідації, якщо меж там ще немає.
**Обґрунтування:** A4-10/`PeriodKey` забороняють арифметику; мінімальна зміна прибирає хибні відмови, не вигадуючи правила для кварталу.
**Рішення PO:** Як ділити значення таблиці без місячних колонок у квартальному/річному періоді, якщо дозвіл змінився всередині періоду: (а) не ділити (як мінімум вище); (б) ділити значення періоду на частини за дозволами (частина = дозвіл × дні періоду), ключ частини — період, а не місяць; (в) заборонити зміну дозволу посеред періоду для немісячних проєктів?
**Впевненість:** підтверджено читанням; наявність квартальних проєктів з (j) — потребує перевірки на стенді.
**Залежності:** C-9.

### C-11 — Середня — Посилання `[Period:-n].[T].[WHERE …]` фільтрує рядки поточного періоду
**Категорія:** correctness
**Де:** `src/Ecr.Application/Recalculation/SliceEvaluationContext.cs:371-375` (виклик), `:413-449` (`EvaluatePredicate`: обхід `_rowsByTable` поточного періоду, умова читає `[Current]` зі зсувом 0); парсер приймає форму (`Parser.cs:1153-1212`), `PredicateValidator.cs:100-108` забороняє міжперіодне посилання лише ВСЕРЕДИНІ умови; динамічні ключі рядків — GUID (`RowKey.cs:48`), тож ключі січня й лютого не збігаються.
**Проблема:**
```csharp
foreach (var rowKey in rows.Keys.OrderBy(k => k, StringComparer.Ordinal))   // рядки ПОТОЧНОГО періоду
{
    CurrentRowKey = rowKey;
    var verdict = PredicateEvaluator.Evaluate(condition, this, …);            // умова — на даних поточного періоду
    if (…) matched.Add(Value(tableDefId, rowKey, columnDefId, periodOffset)); // значення — з іншого періоду за чужим ключем
}
```
**Сценарій відтворення:** місячний проєкт, динамічна таблиця Main; у січні рядки Kind='A' з Qty 3 і 4; формула лютого `SUM([Period:-1].[Main].[WHERE [Kind]='A'].[Qty])`. Фактично 0 (ключі лютого не мають значень у січні); для фіксованих рядків — умова береться з лютневого Kind. Очікувано 7 (02b §4.2: предикат «над фактичними рядками екземпляра таблиці» — тут січневого).
**Вплив:** будь-яка формула такого вигляду (у сідах і тестах не знайдено — `git grep "\[Period:…\]\.\[…\]\.\[WHERE"` порожньо), тобто латентна пастка для авторів шаблонів.
**Зміна — покроково (рекомендовано: відхиляти при публікації):**
1. `src/Ecr.Expressions/Binding/PredicateValidator.cs` — у `Validate` перевіряти саме посилання з предикатом:
```diff
-        foreach (var predicate in FindPredicates(root))
-        {
-            CheckCondition(predicate, diagnostics);
-        }
+        foreach (var reference in FindPredicateReferences(root))
+        {
+            // ⛔ C-11: рядки предиката беруться з ПОТОЧНОГО періоду (`SliceEvaluationContext.EvaluatePredicate`), тож
+            // `[Period:-1].[T].[WHERE …]` фільтрував би чужий період поточними рядками — відмова при публікації.
+            if (reference.IsCrossPeriod)
+            {
+                Report(diagnostics, reference, "expr.predicate.crossPeriod", null,
+                    "A cross-period reference is not allowed in a predicate: filtering this period must not read another one.");
+            }
+
+            CheckCondition(((RowSelector.Predicate)reference.Row).Condition, diagnostics);
+        }
```
і перейменувати `FindPredicates` → `FindPredicateReferences`, що повертає `CellReferenceNode` (рядок `:53-56`: `yield return reference;` замість `yield return predicate.Condition;`). Ключ `expr.predicate.crossPeriod` уже має en/ru/kz (`CATALOG-en-base.sql:3390`, `00-prelude.sql:4020`, `:7027`) — нових рядків не треба.
**Тест-доказ:** `tests/Ecr.Expressions.Tests/` (поруч із тестами `PredicateValidator`) кейс `Міжперіодне_посилання_з_предикатом_відхиляється`: `[Period:-1].[Main].[WHERE [Kind]='A'].[Qty]` → діагностика `expr.predicate.crossPeriod`. Без фіксу — діагностик немає.
**Перевірка після впровадження:** `dotnet test tests/Ecr.Expressions.Tests`; `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~Publish"`.
**Ризики і побічні ефекти:** чинні опубліковані версії з такою формою (не знайдено) перестануть проходити повторну публікацію — перед злиттям перевірити БД стенда: `SELECT … FROM cfg.FormulaDef WHERE Expression LIKE '%[[]Period:%[[]WHERE%'`.
**Обґрунтування:** підтримати форму коректно (завантажувати рядки іншого періоду в `_rowsByTable` за зсувом) — L-зусилля й нові запити; відмова — S і чесно.
**Рішення PO:** Підтримувати фільтр рядків іншого періоду (`[Period:-1].[T].[WHERE …]`) чи забороняти його при публікації? Рекомендація — заборонити зараз.
**Впевненість:** підтверджено читанням.
**Залежності:** немає.

### C-12 — Середня — Незмінена Lookup-комірка відхиляє весь імпорт, якщо імпортер не бачить реєстр
**Категорія:** correctness
**Де:** експорт без профілю: `src/Ecr.Adapters.Excel/ExcelExporter.cs:1097-1103` (код запису) і `:1309-1331` (`LookupTargetEntries.ListAsync(registries, groups, registryId, ct)`); імпорт із профілем: `src/Ecr.Adapters.Excel/ExcelImporter.cs:1321-1323` (`ListAsync(…, ct, profile, viewProject)`); порівняння `ImportDiffBuilder.cs:899-902` (рядок збігається лише з Id, записаним текстом); відмова `ExpectsIdentifier` → AN-143 весь пакет. Коментар `ExcelImporter.cs:1284-1285` обіцяє протилежне («Значення, яке вже стоїть у комірці, — не новий вибір… його відсіює порівняння з поточним»).
**Проблема:** для користувача без права обирати записи реєстру (Deny D-312 або без `Registry.View`) коди не розв'язуються; код «N100», що вже стоїть у комірці, приходить рядком і не дорівнює збереженому Id → «зміна» → відмова «очікується ідентифікатор» → вся книга відхилена. Повторний експорт пише той самий код — імпорт для цього користувача неможливий назавжди.
**Сценарій відтворення:** роль із Deny на реєстр R; документ із заповненою Lookup→R коміркою; користувач змінює іншу комірку офлайн → імпорт → відмова всієї книги. Очікувано: лише інша правка.
**Вплив:** користувачі зі звуженими правами на довідники (кастомні ролі); засіяні ролі мають `Registry.View`, тож частота — низька/середня.
**Зміна — покроково:**
1. `src/Ecr.Adapters.Excel/ExcelImporter.cs` (`LookupsAsync`) — коди будувати з повного переліку (як експорт), а записи, яких профіль не може обрати, позначати непридатними (нове значення відхиляється, незмінене відсіює `Same`):
```diff
-            var targetEntries = await Ecr.Application.Permits.Groups.LookupTargetEntries
-                .ListAsync(registries, groups, registryId, ct, profile, viewProject).ConfigureAwait(false);
+            // ⛔ C-12: коди — з ПОВНОГО переліку (як експорт), інакше незмінений код, якого профіль не може обрати,
+            // ставав «нерозпізнаним» і відхиляв усю книгу; вибір НОВОГО такого запису відхиляє `unusable` нижче.
+            var targetEntries = await Ecr.Application.Permits.Groups.LookupTargetEntries
+                .ListAsync(registries, groups, registryId, ct).ConfigureAwait(false);
+            var choosable = (await Ecr.Application.Permits.Groups.LookupTargetEntries
+                    .ListAsync(registries, groups, registryId, ct, profile, viewProject).ConfigureAwait(false))
+                .Select(e => e.Entry.Id).ToHashSet();
```
і в циклі `:1330-1340` перед `if (reason is not null)`:
```diff
+                reason ??= choosable.Contains(entry.Id) ? null : ImportMessageKeys.LookupNotChoosable;
```
2. `src/Ecr.Adapters.Excel/ImportDiffBuilder.cs` — `ImportMessageKeys`: `public const string LookupNotChoosable = "err.ECR-CELL-4223.importEntryNotChoosable";`.
3. Сід `src/Ecr.Infrastructure/Persistence/Sql/seed/features/` (новий файл за зразком сусідніх, en MERGE + ru/kz `#I18N`): ключ `err.ECR-CELL-4223.importEntryNotChoosable` — en «You cannot choose this registry entry.»; ru «Вам недоступен выбор этой записи справочника.»; kz «Бұл анықтамалық жазбасын таңдау сізге қолжетімсіз.». `docs/build/02-contracts.md` — додати ключ до переліку відмов імпорту.
**Тест-доказ:** `tests/Ecr.Adapters.Tests/Excel/ExcelImporterDirectTargetDenyPreviewTests.cs` новий кейс: поточне значення Lookup = запис реєстру з Deny; книга з тим самим кодом + зміна іншої комірки → `Rejected` порожній, одна зміна. Без фіксу — відмова `ExpectsIdentifier`. Другий кейс: новий код недоступного запису → відмова `LookupNotChoosable`.
**Перевірка після впровадження:** `dotnet test tests/Ecr.Adapters.Tests --filter "FullyQualifiedName~Excel"`.
**Ризики і побічні ефекти:** +1 читання переліку реєстру на Lookup-колонку в перегляді (переліки кешуються `IRegistryStore`? — перевірити; за потреби будувати `choosable` фільтром у пам'яті через `LookupTargetMatcher.MayChoose`).
**Обґрунтування:** узгоджує імпорт з експортом і з наміром, записаним у коментарі.
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням; сценарій із роллю з Deny — потребує перевірки на стенді.
**Залежності:** C-3, C-4 (той самий адаптер — робити послідовно).

### C-13 — Низька — Лічильники посилань запису довідника не бачать історії дозволів
**Категорія:** data-integrity
**Де:** `src/Ecr.Infrastructure/Persistence/RegistryStore.cs:343-374` (`CountReferencesAsync`) і `:382-434` (`CountReferencesFromOutsideAsync`) — лише комірки, шапки, значення, діти, зв'язки, методології; перевірка очищення (purge) уже рахує сегменти й частини — `:489-502`. Споживач: `src/Ecr.Application/Registries/RegistryAdminHandlers.cs:549-560` (`references.Total > 0` → 409 `entryReferenced`). «Де використовується»: `RegistryStore.FindEntryDocumentsAsync` (`:954-1006`), `PermitStore.FindUsagesAsync`.
**Проблема:** дозвіл B, що є лише пізнішим сегментом історії рядка/шапки (не в комірці), має 0 посилань: його можна м'яко видалити через загальний ендпойнт довідника, і «де використовується» показує 0 документів.
**Сценарій відтворення:** рядок «A до 14.02 · B з 15.02»; B не стоїть у жодній комірці. `DELETE …/registries/{code}/entries/{B}` → 204 (очікувано 409 `entryReferenced`); картка дозволу — «0 документів».
**Вплив:** низький (звичайне видалення дозволу — лише чернетки), але історія документа посилається на видалений запис.
**Зміна — покроково:**
1. `src/Ecr.Infrastructure/Persistence/RegistryStore.cs` — у `CountReferencesAsync` перед `return`:
```diff
+        // ⛔ C-13: історія дозволів (j) тримає запис так само, як комірка (див. purge нижче).
+        cells += await db.PermitSegments.CountAsync(s => s.RegistryEntryId == registryEntryId, ct).ConfigureAwait(false)
+                 + await db.CellPermitParts.CountAsync(p => p.RegistryEntryId == registryEntryId, ct).ConfigureAwait(false);
         return new RegistryEntryReferences(cells, headers, values, children, links, constants, substances);
```
(`cells` зробити `var`, не `const`; аналогічно в `CountReferencesFromOutsideAsync` для набору id.) У `FindEntryDocumentsAsync`/`PermitStore.FindUsagesAsync` додати `UNION` документів із `doc.PermitSegment` за `RegistryEntryId`.
**Тест-доказ:** `tests/Ecr.Infrastructure.Tests/Persistence/` кейс: сегмент з B, `CountReferencesAsync(B).Total == 1`. Без фіксу — 0.
**Перевірка після впровадження:** `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~Registry"`.
**Ризики і побічні ефекти:** +2 легкі запити на видалення запису (рідкісна дія); індекси за `RegistryEntryId` є (FK).
**Обґрунтування:** узгодження з уже прийнятим правилом purge.
**Рішення PO:** не потрібне.
**Впевненість:** підтверджено читанням.
**Залежності:** немає.

---

## Порядок впровадження

Паралельні групи (без спільних файлів):
- **Група A (рушій):** C-1 (`RecalculationService.cs`, `RowLocalPreview.cs`), потім C-5 (`SliceEvaluationContext.cs`, `RowLocalPreview.cs`, `ValidationEngine.cs`) — послідовно (спільний `RowLocalPreview.cs`); C-11 (`PredicateValidator.cs`) — паралельно з ними.
- **Група B (Excel):** C-4 → C-12 → C-3 послідовно (спільні `ImportDiffBuilder.cs`/`ExcelImporter.cs`).
- **Група C (міграція):** C-2 (`DocumentVersionMigrationStore.cs`).
- **Група D (workflow):** C-6 (`WorkflowStore.cs`), C-8 (`ReopenDocumentHandler.cs`) — паралельно; C-7 (`IAccessDecisionService.cs`, `AccessDecisionService.cs`, `RecallSheetHandler.cs`) — окремо (інтерфейс зачіпає підміни в тестах).
- **Група E (дозволи):** C-9 (`PermitHistoryHandlers.cs`), потім C-10 (після рішення PO; `PermitHistorySupport.cs`, `PatchCellsHandler.PermitValidity.cs`, `SaveCellSplitsHandler.cs`, `PermitCoverageIssues.cs`, `05-rpt-views.sql`); C-13 (`RegistryStore.cs`) — паралельно.

Рішення PO потрібні до: C-1 (варіант збереження — можна почати з «а»), C-5, C-10, C-11.

## Відкинуто

- **`-2^2 = -4`, `NOT`, `&` vs `+`** — свідомо, Q-065, тести (`Parser.cs:544-553`).
- **`SUM` над усіма `null` = 0, `AVERAGE/MIN/MAX` = `null`** — задокументована семантика (`TemplateFunctions.cs`, 02b §6.1).
- **`FALSE AND null` = `null` (без короткого замикання)** — 02b §6.2 «null поширюється»; не дефект без рішення про тризначну логіку.
- **`[Period:-1]` у січні = `null`** — 02b §3.2.
- **ROUND/масштаб сховища** — обидва `AwayFromZero` (`TemplateFunctions.Round`, `ImportDiffBuilder.RoundToColumnScale`); `ToEven` лише в Legacy за специфікацією.
- **Застарілі результати при `CalculatedStale` відхиляють весь імпорт** — свідомо, `ExcelImportStaleWorkbookTests`.
- **Прострочена передача місяця блокує виконавця з власним Write** — закріплено `EditRulesDelegationTests:181` і 02-contracts EA-B.
- **Approve у закритому періоді** — немає правила, що вимагає інакше.
- **Сповіщення копій аркушів не надсилаються** — свідомо відкладено («коміт 8»).
- **Маршрут обирається за поточною версією в Recall/LastApprovalStepRole** — недосяжно (маршрути лише проєктні, без версії).
- **`DocumentPeriodReopen` відкриття/строк/межі `> now`** — узгоджено скрізь.
- **`SubmitSheetHandler.TemplateVersionOfAsync` бере поточну версію для періоду без екземплярів** — без даних комірок практичної шкоди немає; низько, не включено.
- **Видалення рядка не сповіщає читачів інших періодів** — реальна шкода лише в Q-17 (прапор `MonthColumnRouting` вимкнено) і для міжперіодних предикатів (закриваються C-11).
- **Значення затвердженого аркуша, перераховане в пам'яті, живить формули чернетки** — немає рішення про очікувану поведінку (ФВ-9.17); потребує питання до PO окремо, не включено як дефект.
- **FromMonth-режим міграції** — прапор `FromMonthEnabled` вимкнено (`appsettings.json`).
- **Повтор частково виконаної важкої міграції** — предикати ідемпотентні, водяний знак коректний.
- **Excel formula injection** — ClosedXML 0.104.2 пише рядок як текст (`cell.Value = text`), формул не створює.
- Вже виправлено 2026-10-31 (не перевідкривалося): реімпорт незмінених формульних комірок, міграція перевизначення без формули, ліміти видалення EF, правило очищення згортки D-462.

## Перевірено й у порядку

`StrictDecimalArithmetic` (ділення/переповнення → значення-помилки); `TemplateFunctions` (агрегати, ROUND з межами, SUMIF, IFERROR не ловить null); `Evaluator.ApplyBinary` (поширення помилки/null, виключення для `&`, `=`, ділення на null); `PeriodStateCalculator` (`Calculate`/`Effective`/`PlanTransitions`, лише вперед, вікно року D-204); `YearGraceWindow` (межі в поясі майданчика); `SubmissionPeriodSelector.Choose/MonthColumns`; `ClosedPeriodGuard` (з винятком документа); `RecalculationJob.RefusedPeriodsAsync/GuardCompletionAsync` (виняток документа); `PermitTimeline` (`Spans`, `InMonth`, `GapDays`, `Merge`); `SaveCellSplitsHandler` (одна транзакція, повна сума лише з усіх частин); `ImportDiffBuilder.ReadNumber/Read` (інваріантні число/дата); `ExcelExporter.WriteValue/WriteText`; `GenericCalculationModule.ToValue` (інваріантний розбір Legacy); `CollectedFold`/межі материалізації (опівніч майданчика, півінтервал); курсори черги погодження й аудиту (строгий `>`, однакова колація); пошук `DateTime.Now/UtcNow` в області — 0 збігів; культурозалежний розбір без `CultureInfo` — 0 збігів.

## Самоперевірка

- **C-1:** `RecalculationService.cs:1386` перечитано (`if (result.Value.IsError) { continue; }`), гілка Null `:1397-1410` і `values[key] = value;` `:1446` — збігаються; `CellKey(0, table.Id, rowKey, columnDefId)`, `stored.GetValueOrDefault`, `upserts`, `CellRecord`, `CellValueData { IsEmpty, IsCalculated }` — ті самі імена, що в гілці Null (компілюється за змістом). `RowLocalPreview.cs:148-153` — `values.Remove(key)` підтверджено. Тест падає без фіксу, бо записів немає. Перевірено відсутність у `KNOWN-ISSUES.md`, `10-decisions.md`; тести `ExpressionBudgetReachabilityTests:92`, `RegistryFieldRecalculationTests:122` — цілі порожні, не суперечать. Пройшла.
- **C-2:** `DocumentVersionMigrationStore.cs:1865-1869` і `:1760-1765`, етапи `:611`/`:748`, FK `RS1_SheetCopies.cs:202-210` — перечитано дослівно; `@sheetMap` оголошено в преамбулі (`:1475`). У коді міграції немає жодної згадки копій. «Застряглий маркер» позначено як потребує стенда. Пройшла.
- **C-3:** `ExcelImporter.cs:586-607`, `PatchCellsHandler.Workbook.cs:600`, `:652`, `:911` — збігаються; `EnterEditOfAsync`/`EnterEditNoWaitOfAsync` існують (`IAccessDecisionService.cs:499-517`), `TableInstanceRef.SheetCopyId` існує (`IRowStore.cs:354`), `RowWriteLock.EnterAsync(..., sheetCopyId)` використовується в `PatchCellsHandler.cs:3016`. `ExcelSheetCopiesTests` покриває лише експорт/перегляд (за звітом агента; не суперечить). Пройшла.
- **C-4:** `ImportDiffBuilder.cs:192`, `:831-832`, `:241-245` (replacesFormula), `ExcelExporter.cs:1222-1257`, `:1263-1275` — перечитано; `FormulaDef.Scope/ColumnDefId/AllowManualOverride/IsDeleted` існують (`FormulaDef.cs:23-51`), `FormulaOutputs.RowKeyOf(table, formula)` використовується експортером (`ExcelExporter.cs:1270`). У `tests/Ecr.Adapters.Tests` немає `FormulaScope.Cell`. Пройшла.
- **C-5:** `SliceEvaluationContext.cs:546-558` перечитано; `git grep DefaultValue` у `src/Ecr.Application|Infrastructure|Calculations` — лише DTO/шаблони; 02b §6.3 `:488-491` процитовано. Зміна описана на рівні методу (новий словник) — компілюваність залежить від реалізації розбору, позначено M. Пройшла (з рішенням PO).
- **C-6:** `WorkflowStore.cs:309` дослівно; `DocumentPeriodReopenRule.Apply` (`Ecr.Domain.Services`), `db.DocumentPeriodReopens`, поля `DocumentId/ClosedAt/OpenUntil/PeriodKeyValue` — існують (`DocumentPeriodReopen.cs`). Тест `DocumentPeriodReopenTests.cs:78-79` закріплює дозвіл подання (підтримує знахідку). Пройшла.
- **C-7:** `RecallSheetHandler.cs:108`, `:211`, `:232-238`; `EditRules.cs:707`, `:757-766`; `AccessProfile.cs:390-395`; `AccessDecisionService.cs:2765-2766` — перечитано. `EditRules.MeetsSubmit(AccessProfile, int, GrantLevel)` (`:373`), `BuildContextAsync(..., evaluateAccessWindow, ct, sheetCopyId)` (`:3098-3100`, підтверджено), `WithSheetCodeAsync`/`WithParticipationAsync` — існують (приватні в тому ж класі, де додається метод). `key`, `sheetCopyId` визначені в обох місцях Recall. Пройшла.
- **C-8:** `ReopenDocumentHandler.cs:109`, `:146`, `:150`; `ApproveSheetHandler.cs:113-129`; `AccessDecisionService.cs:2985-2994` — перечитано. Тексти/ключі відмови скопійовано з `:113-123`. Пройшла.
- **C-9:** `PermitHistoryHandlers.cs:174-179` (рядок зі сторожем), `:371-388` (шапка без) — перечитано; `IPermitHistoryStore.PartsAsync(long, IReadOnlyCollection<int>, CancellationToken)` фільтрує `PeriodKeyValue` (`PermitHistoryStore.cs:41-49`); `PermitSegment.ValidFrom` (`PermitSegment.cs:58`), `CellPermitPart.RegistryEntryId` (використовується в `SaveCellSplitsHandler`) — існують. Тестів на скасування шапки немає (`git grep "UndoHeaderPermit\|header/undo" tests` — порожньо). Пройшла.
- **C-10:** `PermitHistorySupport.cs:381`, `PatchCellsHandler.PermitValidity.cs:461`, `:561`, `SaveCellSplitsHandler.cs:239` — перечитано дослівно; `PeriodKind.Quarterly` існує (`PeriodCalendar.cs:37`). `05-rpt-views.sql:1551` і `PermitCoverageIssues.cs:317/427` — з докладу агента, рядки `DataCellsInMonth(` у `PermitCoverageIssues.cs:317`, `:427` підтверджено `git grep`. Зміна описана як план (M), з рішенням PO. Пройшла.
- **C-11:** `SliceEvaluationContext.cs:371`, `:413-449`, `PredicateValidator.cs:53`, `:102` — перечитано; ключ `expr.predicate.crossPeriod` має en/ru/kz у сідах. `Report(...)` і `RowSelector.Predicate.Condition` використовуються в тому ж файлі. Пройшла.
- **C-12:** `ExcelImporter.cs:1284-1285`, `:1321-1323`, `:1330-1340`, `ImportDiffBuilder.cs:899-902` — перечитано; перевантаження `LookupTargetEntries.ListAsync(registries, groups, registryId, ct)` без профілю використовує експортер. Новий ключ і сід — у кроках. Пройшла (стенд — для ролі з Deny).
- **C-13:** `RegistryStore.cs:343-373`, `:489-502`, `RegistryAdminHandlers.cs` (`references.Total > 0`) — перечитано; `db.PermitSegments`, `db.CellPermitParts` існують (`:492`, `:498`). Пройшла.
- **Вилучено після перевірки:** «шаблонна версія знімка подання» (низька шкода), «видалення рядка в Q-17» (прапор вимкнено), «значення затвердженого аркуша в каскаді» (немає правила) — перенесено до «Відкинуто». Дублів із `KNOWN-ISSUES.md`/`10-decisions.md`/виправленим 2026-10-31 не виявлено (пошук за ключовими словами кожної знахідки).
