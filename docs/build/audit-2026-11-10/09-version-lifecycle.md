# Аудит 2026-11 · 09 — Життєвий цикл версії шаблону й перенос проєкту на іншу версію

**Область.** Створення та клон версії, публікація, виведення з обігу й відкидання; перенос проєкту на іншу версію (`MigrateDocumentVersionHandler`, `DocumentVersionMigrationStore`, `VersionMigrationPlanner`). Окремо розібрано, що перенос робить із кожним видом даних і хто ці дані читає після нього.

**`main`:** `39aea41d9`, 2026-11-11. Лише читання: збірок і тестів не запускав.

**✎ 2026-11-11 (після рішення PO):** V-7 — варіант А (перенос блокується, доки поданий/затверджений аркуш із ручним значенням, якого ціль не дозволяє, не повернуто в роботу); знахідку переписано до впровадження.

**Охоплено повністю:** обробник переносу (`MigrateDocumentVersionHandler.cs`, 1–1899), сховище переносу (`DocumentVersionMigrationStore.cs`, 1–1955), планувальник (`VersionMigrationPlanner.cs`: порівняння колонок, рядків і формул), `FormulaOverrideMigration.cs`, `MigrationRecalculation.cs`, задача `MigrateDocumentVersionJob.cs`, фільтр маркера (`MigrationMarkerGuard.cs`), публікація, виведення з обігу й відкидання версії. Також читачі даних після переносу: зрізи подання й порівняння версій документа, журнал і історія комірки, повторний зріз при затвердженні, маршрути погодження, стабільні вʼюхи SSRS.

**Охоплено поверхово:** внутрішня будова `TemplateVersionCloner`/`TemplateVersionStore.CloneAsync` (перевірено перелік того, що копіюється поза графом); клієнтський діалог (`VersionMigrationDialog.tsx`); режим «з місяця» (прапор `Documents:VersionMigration:FromMonthEnabled` типово вимкнено); процедури архіву (`03-archive-proc.sql`).

**Не охоплено:** блокування й продуктивність переносу (відомий DB-6, `DBA-PROPOSALS.md`), черга задач зсередини.

**Звірено з відомим:** `02-data-layer.md` D-6, D-7, D-10; `04-correctness-domain.md` C-2 (дубль D-6); виправлення аудиту 31.10 (F2: позначка ручного значення, коли колонка втрачає формулу, `MigrateDocumentVersionHandler.cs:1155-1174`). Також `KNOWN-ISSUES.md`, `OPEN-ITEMS.md`, `TODO-REMAINING.md`, `WORK-QUEUE.md` (AN-20, AN-133, PM-10c, CL-8, DB-6), `docs/tz/10-decisions.md`, `docs/build/02-contracts.md` §перенос (LP-32, ACC-OW, `lookupRemaps`), `docs/admin/admin-guide.md:288-318`.

---

## Відповіді на питання PO

**Питання:** «Як себе веде система, якщо відбувається перехід між версіями шаблонів, і чи немає там проблем?»

**Як це працює зараз.**
1. Версія шаблону закріплена за **проєктом**, а не за окремим документом. Тому перенос завжди охоплює весь проєкт, тобто всі його документи, періоди й роки. Перенести частину документів неможливо. Режим «з місяця» існує, але вимкнений прапором (`MigrateDocumentVersionHandler.cs:192-197, 1300-1310`).
2. Запустити перенос може користувач із правом `Template.Edit` і грантом `Manage` на проєкт. Пункт меню документа «Перенести на нову версію» відкриває діалог. Спершу виконується **сухий прогін**: звіт показує, скільки значень переїде, що зникло б, що змінило б тлумачення і що заблоковано. Перенос можливий лише на **опубліковану** версію того самого шаблону (`:1749-1796`). Від 500 000 значень система вимагає підтвердити наявність резервної копії (`:936-950`).
3. **Безпечна частина.**
   - Значення переносяться за кодами аркуша, таблиці, колонки й рядка без перетворення.
   - Режим `Safe` відмовляє, якщо будь-яке введене значення зникло б або змінило тлумачення: зміна типу, одиниці, довідника, обов'язковості, звуження точності, режиму рядків (`VersionMigrationPlanner.cs:531-613`).
   - Разом із даними переїжджають оформлення комірок, ручні значення замість формул (D-461), індекс пошуку, стани й журнал погодження, права (копіюються на нові ідентифікатори), мапінги збору, подій і вікон, а також шапка.
   - Результати перевірки скидаються. Подання перевіряє документ заново.
   - Якщо змінилися формули, після переносу ставиться перерахунок відкритих періодів.
4. **Подані й затверджені періоди** переносяться без змін, лише коли план не зачіпає їхніх аркушів. Інакше система відмовляє з переліком таких аркушів, доки їх не повернуть у роботу (LP-32). Навіть видалення порожньої колонки на аркуші, у якого десь є затверджений місяць, блокує перенос. Так визначено контрактом.

   Після переносу затверджений період показується вже **в новому макеті**. Його формули не перераховуються до повернення в роботу. Старий макет зберігається лише в зрізах подання.

   Закриті періоди теж переносяться. Нові формульні колонки в них лишаються порожніми, бо перерахунок закриті періоди пропускає.
5. **Під час переносу:**
   - проєкт повністю недоступний іншим користувачам: читання й запис дають `409 ECR-DOC-4091` «документ переводиться на іншу версію»;
   - автор бачить прогрес задачі;
   - за виміром адміністратора, 1 млн комірок переноситься приблизно за 1,5 хв;
   - правки інших проєктів можуть чекати 1–3 с.
6. **Якщо перенос обірвався**, проєкт лишається заблокованим. Вихід — повторити той самий перенос: кнопка «Повторити» в «Моїх задачах» або той самий запит API. Повтор продовжує з місця обриву. Відкат можливий лише з резервної копії. «Повернутися назад» можна тільки окремим переносом на старішу опубліковану версію з тими самими перевірками.
7. **Кілька версій одночасно.** Різні проєкти можуть жити на різних версіях, а нова публікація нікого не переносить сама (ФВ-7.7). Але мапінги збору PI/EF та вікна глобальні: після переносу першого проєкту решта проєктів на старій версії перестає збиратися. Звіт показує їхню кількість, але перенос не блокує.

   Застаріла (`Deprecated`) версія далі обслуговує свої проєкти, проте стати ціллю переносу не може. Видалити можна лише чернетку без документів. Архівний проєкт не переноситься, а його архівні роки не зачіпаються.

**Що ризиковано (нові знахідки цього звіту):**
- **V-1:** нова «формула рядка» в цільовій версії мовчки затирає введені людиною значення під час перерахунку після переносу. Сухий прогін про це не попереджає, хоча для «формули комірки» попереджає.
- **V-2 / V-3:** зрізи подання, зняті до переносу, перестають порівнюватися. Порівняння показує всі комірки «зміненими» або не показує змін зовсім. Перше затвердження після переносу знімає зайвий зріз від імені погоджувача.
- **V-4:** історія комірки й журнал документа обриваються на переносі: зміни до переносу в інтерфейсі більше не видно, хоча в базі вони лишаються.
- **V-5:** звіти SSRS за старим кодом перейменованої колонки показують порожньо, доки не перезапустять застосунок.
- **V-6:** зміну «місяця колонки» планувальник не помічає.
- **V-7:** перенос знімає ручні значення (D-461) і в затверджених аркушах, які мали б лишитися без змін. ✎ Рішення PO 11.11 — варіант А: перенос блокується (`lockedOverridesLost`, `409 migrateLockedOverrides`), доки аркуш не повернуть у роботу.

**Уже відомі ризики (не дублюються тут):**
- D-6 (= C-2): аркуш із копіями ламає перенос, і проєкт застрягає заблокованим. Це найнебезпечніше.
- D-7: історія дозволів не переноситься.
- D-10: чат і передачі при перейменуванні таблиці лишаються на старому коді.

**Чого немає в інтерфейсі:** мапи перейменувань таблиць і колонок, мапи Lookup шапки та згода на шапку закритих документів доступні лише через API (`versionMigrationApi.ts:41-47`). Мапи Lookup для **колонок таблиць** немає зовсім (свідоме обмеження v1, `VersionMigrationLookupRemap.cs:8`).

---

## Зведена таблиця

| ID | Важливість | Категорія | Назва | Зусилля | Ризик зміни | Потрібне рішення PO |
|---|---|---|---|---|---|---|
| V-1 | High | data-integrity | Нова формула рядка (`Scope = Row`) на рядку з введеними значеннями не дає `guardedWithData`: перерахунок після переносу мовчки затирає введене | S | низький | ні |
| V-2 | Medium | correctness | Порівняння версій документа через межу переносу: колонки й аркуш зрізу — Id старої версії, межі читання їх не знають; «усе змінено» або «змін немає» | M | середній | ні |
| V-3 | Low | correctness | Перше затвердження перенесеного поданого аркуша не знаходить зрізу (інший `SheetDefId`), перераховує аркуш і знімає зайвий зріз від імені погоджувача | S | низький | ні |
| V-4 | Medium | audit | Історія комірки й журнал документа після переносу не показують змін до нього (`aud.CellChange` на Id старої версії) | M | середній | ні |
| V-5 | Low | reporting | Після переносу з перейменуванням колонок стабільні вʼюхи SSRS не перегенеровуються: старий код колонки — NULL до перезапуску чи публікації | S | низький | ні |
| V-6 | Low | data-integrity | Планувальник не порівнює `IsMonthColumn`/`MonthNumber`: значення місячних колонок мовчки міняють місяць-власника | S | низький | ні |
| V-7 | Low | data-integrity | Перенос знімає ручні значення (D-461) у поданих/затверджених аркушах, які LP-32 обіцяє переносити без змін | M | низький | ні (✎ ухвалено PO 11.11: варіант А) |

---

## Знахідки

### V-1 — High — Нова формула рядка на рядку з введеними значеннями не дає `guardedWithData`: перерахунок мовчки затирає введене

**Категорія:** data-integrity

**Де:**
- `src/Ecr.Application/Documents/VersionMigration/VersionMigrationPlanner.cs:680-747` — `CompareCellFormulas`: розглядає лише `FormulaScope.Cell` (`:685-687`);
- `:423-466` — `CompareFormulas`: нова формула рядка потрапляє у `FormulaChanges`;
- `src/Ecr.Application/Documents/VersionMigration/MigrateDocumentVersionHandler.cs:1125-1133` — після коміту ставиться перерахунок проєкту;
- `src/Ecr.Application/Recalculation/RecalculationService.cs:1695-1715` — `Targets`: рядкова формула без колонки пише в кожну колонку рядка;
- `:1357-1374, 1515-1526` — перезапис не робиться лише для позначки D-461 і для чужого власника Q-17;
- `src/Ecr.Application/Templates/FormulaDefHandlers.cs:274-297` — формули `Row` на колонках вводу дозволені («Row цілить у рядок і ніколи не перевірявся»).

**Проблема.** WW-02 і ACC-OW визнали нову **комірочну** формулу на комірці з введеним значенням зміною тлумачення («перший перерахунок перезапише введене», `VersionMigrationPlanner.cs:670-679`). Така зміна дає `Guarded` і відмову `guardedWithData`, яку знімає лише явна згода `acceptGuardedOverwrite`. **Рядкова** формула пише в ті самі вхідні комірки (`RecalculationService.Targets`), але планувальник її не бачить:
```csharp
        var targetFormulas = to.Formulas
            .Where(f => !f.IsDeleted && f.Scope == FormulaScope.Cell && f.RowDefId is not null && f.ColumnDefId is not null)
            .ToList();
```
Нова формула рядка лише додає таблицю у `FormulaChanges` (`:457-465`). Тоді після коміту `MigrationRecalculation` ставить перерахунок, і він перезаписує введені значення рядка: `IsManualTarget` пропускає тільки позначки D-461 і чужого власника.

**Сценарій відтворення:**
1. V1: фіксована таблиця `T`, рядок `R1`, колонки вводу `A`, `B` (Decimal). Користувачі ввели `A=5` у `R1` в кількох періодах.
2. V2 (клон V1) додає формулу рядка `R1` без колонки, наприклад `[R2]+[R3]` (Scope `Row`). Публікація її пропускає, бо `RequireComputedColumn` перевіряє лише `Column`.
3. Сухий прогін `Safe` для V2 показує `canApply = true`. Пунктів `Modified`/`CellFormula` немає, `guardedValues = 0`.
4. Перенос → коміт → `IRecalculationJob` на проєкт. Значення `A` у `R1` замінюється результатом формули в усіх відкритих періодах, журнал пише `Recalculation`.

Очікуваний результат: той самий `Modified`/Guarded (`CellFormula`), що й для формули комірки. Тобто відмова `guardedWithData` у `Safe`, а з `acceptGuardedOverwrite` — свідомий перезапис. Правило: ФВ-7.5 + ACC-OW (`02-contracts.md:4059`), коментар `VersionMigrationPlanner.cs:670-679`.

**Вплив:** будь-яка цільова версія, що додає формулу рядка (підсумок чи баланс) на рядок, куди раніше вводили вручну. Це той самий клас, що WW-02 для моделей Water, Waste і Land. Введені значення відкритих періодів усіх документів проєкту мовчки замінюються. Слід лишається лише в журналі з автором «система».

**Зміна — покроково:**
1. Файл `src/Ecr.Application/Documents/VersionMigration/VersionMigrationPlanner.cs`, метод `CompareCellFormulas` (`:680-747`).
```diff
-        var targetFormulas = to.Formulas
-            .Where(f => !f.IsDeleted && f.Scope == FormulaScope.Cell && f.RowDefId is not null && f.ColumnDefId is not null)
-            .ToList();
+        // ⛔ V-1: формула РЯДКА пише у вхідні комірки так само, як комірочна (RecalculationService.Targets: з колонкою —
+        // одна комірка, без колонки — кожна колонка рядка, що приймає результат), тож для плану це ті самі нові формули
+        // на комірках: Guarded зі значеннями, перелік ACC-OW.
+        var targetFormulas = to.Formulas
+            .Where(f => !f.IsDeleted && f.RowDefId is not null
+                        && ((f.Scope == FormulaScope.Cell && f.ColumnDefId is not null) || f.Scope == FormulaScope.Row))
+            .ToList();
```
   Далі цикл (`:700-740`):
```diff
         var added = 0;
         foreach (var formula in targetFormulas)
         {
-            if (!targetRows.TryGetValue(formula.RowDefId!.Value, out var targetRow)
-                || !targetColumns.TryGetValue(formula.ColumnDefId!.Value, out var targetColumn))
+            if (!targetRows.TryGetValue(formula.RowDefId!.Value, out var targetRow))
             {
                 continue;
             }
 
+            foreach (var targetColumn in TargetColumnsOf(formula, targetColumns))
+            {
             if (!sourceRows.TryGetValue(targetRow.RowKeyValue, out var sourceRow)
                 || !sourceColumns.TryGetValue(targetColumn.Code, out var sourceColumn))
             {
                 // Комірки у вихідній версії не було — значень там бути не може.
                 added++;
                 continue;
             }
             … (тіло без змін до кінця `if (values > 0) { … }`) …
+            }
         }
```
   Тіло залишається тим самим: `alreadyComputed`, `added++`, `values`, `items.Add(… "CellFormula" …)`, `overwrites.Add(…)`. Змінюється лише відступ. Поле пункту лишається `"CellFormula"`, щоб прапор ACC-OW і клієнтський діалог CL-8 (`guardedOverwritable === guardedValues`) працювали без змін.

   Новий приватний метод поруч, після `CompareCellFormulas`:
```csharp
    /// <summary>V-1: колонки цільової таблиці, у які пише формула комірки чи рядка (як <c>RecalculationService.Targets</c>).</summary>
    /// <remarks>
    /// Рядкова формула без колонки — лише колонки вводу, що приймають число (<c>FormulaTargetTypes</c>): рядкові формули
    /// шаблонів — числові підсумки, а колонку іншого типу перерахунок пропускає. Обчислювані колонки рахувала й стара
    /// версія (або це окрема зміна типу — Guarded у <c>CompareColumns</c>).
    /// </remarks>
    private static IEnumerable<ColumnDef> TargetColumnsOf(FormulaDef formula, IReadOnlyDictionary<int, ColumnDef> targetColumns)
    {
        if (formula.ColumnDefId is { } single)
        {
            return targetColumns.TryGetValue(single, out var column) ? [column] : [];
        }

        return targetColumns.Values.Where(c => !c.IsComputed
            && Ecr.Application.Recalculation.FormulaTargetTypes.Accepts(c.DataType, Ecr.Expressions.Ast.ExpressionValueType.Number));
    }
```
   `targetColumns` уже має тип `Dictionary<int, ColumnDef>` (`:695`), тож у сигнатуру він передається як `IReadOnlyDictionary`. Оновити `<summary>` методу (`:670-679`): «комірочні **і рядкові** формули».
2. Контракт `docs/build/02-contracts.md:4059`: у реченні про ACC-OW дописати «…комірочну формулу (`FormulaScope.Cell`) **або формулу рядка (`FormulaScope.Row`)**…». Схема й API не змінюються.

**Тест-доказ:** `tests/Ecr.Application.Tests/Documents/VersionMigrationCellFormulaTests.cs`.
- У `Version(...)` додати параметр `bool rowFormula = false`. Коли він `true`: `var f = new FormulaDef(table.Id, FormulaScope.Row, "[B]", ExpressionDialect.Template); f.AssignRow(row1.Id); table.AddFormula(f);`.
- Новий кейс `Нова_формула_рядка_на_рядку_зі_значенням_дає_guardedWithData_і_перелік_ACC_OW`:
  - `Plan(source: Version(1, cellFormula: false), target: Version(2, cellFormula: false, rowFormula: true), values: [new VersionMigrationCellUsage(SourceColumn, SourceRow, 5)])`;
  - перевірити `Assert.Single(plan.Items, i => i.Field == "CellFormula")` з `Kind == "Modified"`;
  - перевірити `plan.GuardedOverwritableValues == 5`, `RefusalsFor(Safe)` містить `guardedWithData`, `RefusalsFor(Safe, acceptGuardedOverwrite: true)` порожній.
- Другий кейс `Формула_рядка_на_порожньому_рядку_безпечна`: `values: []` → пункт `Added`/`CellFormulas`, `Modified` немає.

Без фіксу перший кейс падає: пункту `CellFormula` немає, `GuardedValues = 0`. Мутація для перевірки: прибрати `|| f.Scope == FormulaScope.Row` → тест червоніє.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~VersionMigrationCellFormulaTests|FullyQualifiedName~VersionMigrationFormulaChangesTests"`, далі `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~DocumentVersionMigrationTests.GuardedOverwrite"`. На стенді: копія Land, версія з новою формулою рядка на рядку зі значеннями → сухий прогін показує `guardedWithData` і перелік комірок.

**Ризики і побічні ефекти:**
- Переноси, які зараз мовчки проходять, почнуть відмовляти, доки людина не дасть згоду `acceptGuardedOverwrite`. Через клієнтський діалог CL-8 це вже можливо.
- Рядкова формула з нечисловим результатом (текст, дата) у колонку відповідного типу не врахується. Залишковий ризик невеликий, бо рядкові формули шаблонів — числові підсумки.
- Запитів до бази не додається: план чистий, у пам'яті.

**Обґрунтування.** Розширено наявний механізм WW-02/ACC-OW замість нового класу відмови: семантика однакова («комірку тепер пише формула»), а згода й клієнт уже є. Альтернативу «блокувати будь-яку нову формулу рядка» відкинуто: на порожніх рядках вона безпечна.

**Рішення PO:** не потрібне.

**Впевненість:** підтверджено читанням.

**Залежності:** разом із V-6 (той самий файл, послідовно).

---

### V-2 — Medium — Порівняння версій документа через межу переносу дає хибний результат

**Категорія:** correctness (доказ погодження, ФВ-5.22)

**Де:**
- `src/Ecr.Application/Documents/DocumentVersionHandlers.cs:137-172` — `CompareDocumentVersionsHandler.HandleAsync`;
- `:200-230` — `DiffAsync`: ключ `(RowId, ColumnDefId)`, фільтр `readable.CanReadColumn`;
- `:349-366` — `LoadAsync`: `CanReadSheet(found.SheetDefId)`;
- `:80-86` — `ListDocumentVersionsHandler`: `readable.CanReadSheet(v.SheetDefId)`;
- `src/Ecr.Application/Security/DocumentReadScope.cs:20-22, 54-75, 222-234, 400-401` — межі будуються зі знімка **поточної** версії, а невідома колонка невидима;
- `src/Ecr.Infrastructure/Persistence/DocumentVersionStore.cs:12-34` — проєкція зрізу без `TemplateVersionId`;
- `src/Ecr.Application/Ports/IDocumentVersionStore.cs:36-40`;
- перенос зрізів свідомо не чіпає: `src/Ecr.Infrastructure/Persistence/DocumentVersionMigrationStore.cs:1439-1440`.

**Проблема.** `calc.SubmissionSnapshot` лишається на версії подання: `SheetDefId`, `TemplateVersionId`, а `PayloadJson` несе `ColumnDefId` старої версії. Це правильно, бо зріз — доказ. Але порівняння будує межі читання зі знімка поточної версії, а ключ різниці містить `ColumnDefId`:
```csharp
        var oldMap = oldCells.ToDictionary(c => (RowId: c.Row, ColumnDefId: c.Column));
        var newMap = newCells.ToDictionary(c => (RowId: c.Row, ColumnDefId: c.Column));
        …
        var changed = oldMap.Keys.Union(newMap.Keys)
            .Where(k => readable.CanReadColumn(k.ColumnDefId) && !ownerBlocked.Contains(k.ColumnDefId))
```
`CanReadColumn(<старий Id>)` повертає `false`, бо колонки немає в `_tableOfColumn` (`DocumentReadScope.cs:229-230`). Рядки після переносу ті самі, Id не змінюються.

**Сценарій відтворення:**
1. Подати аркуш з `C1=42` (з'являється зріз №1), повернути, змінити `C1=43`, подати знову (зріз №2).
2. Перенести проєкт на V2 з тими самими кодами. Подійде навіть `Presentation` — зміна лише підписів.
3. `GET /documents/{id}/compare?from=<зріз 1>&to=<зріз 2>` дає `changes = []`, хоча 42 → 43. Обидві сторони на старих Id, і обидві відсіюються як невидимі.
4. `GET …/compare?from=<зріз 2>&to=current` повертає кожну непорожню комірку як зміну «порожньо → значення»: старі Id сховані, нові «з'явилися». Значення при цьому не змінювалися.
5. У ролі, звуженої за кодом аркуша, старий `SheetDefId` не має коду в знімку. Тоді версії до переносу можуть зникнути з переліку `GET …/versions`. Потребує перевірки на стенді: `EditRules.CanReadIn` з `sheetCode = null`.

Очікуваний результат: порівняння за тими самими кодами, що й перенос (ФВ-7.2, відповідність за кодами — `MigrateDocumentVersionHandler.cs:199-200`).

**Вплив:** кожен документ із поданнями до переносу. Перегляд «Версії» й «Порівняти» вводить в оману погоджувача й аудитора, а це екран доказу погодження (D-442, ФВ-5.22).

**Зміна — покроково:**
1. Новий файл `src/Ecr.Application/Workflow/SubmissionSnapshotTranslation.cs`:
```csharp
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Workflow;

/// <summary>
/// V-2/V-3: зріз подання адресує аркуш і колонки ВЕРСІЇ, на якій його зняли (перенос проєкту зрізів не чіпає — це
/// доказ). Читачі, що порівнюють зріз із живими даними чи межами читання, перекладають Id на версію документа
/// за кодами «аркуш → таблиця → колонка» — та сама ідентичність, що в <c>VersionMigrationPlanner</c>.
/// </summary>
public static class SubmissionSnapshotTranslation
{
    /// <summary>Аркуш версії <paramref name="to"/> з тим самим кодом; <c>null</c> — такого аркуша немає.</summary>
    public static int? Sheet(int sheetDefId, TemplateVersionSnapshot from, TemplateVersionSnapshot to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (from.TemplateVersionId == to.TemplateVersionId)
        {
            return sheetDefId;
        }

        var code = from.Sheets.FirstOrDefault(s => s.Id == sheetDefId)?.Code;
        return code is null ? null : to.Sheets.FirstOrDefault(s => !s.IsDeleted && s.Code == code)?.Id;
    }

    /// <summary>Комірки з колонками версії <paramref name="to"/>; колонка без відповідника лишається як є.</summary>
    public static IReadOnlyList<SubmissionPayloadCell> Cells(
        IReadOnlyList<SubmissionPayloadCell> cells, TemplateVersionSnapshot from, TemplateVersionSnapshot to)
    {
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (from.TemplateVersionId == to.TemplateVersionId || cells.Count == 0)
        {
            return cells;
        }

        var target = new Dictionary<(string Sheet, string Table, string Column), int>();
        foreach (var sheet in to.Sheets.Where(s => !s.IsDeleted))
        {
            foreach (var table in sheet.Tables.Where(t => !t.IsDeleted))
            {
                foreach (var column in table.Columns.Where(c => !c.IsDeleted))
                {
                    target[(sheet.Code, table.Code, column.Code)] = column.Id;
                }
            }
        }

        var map = new Dictionary<int, int>();
        foreach (var sheet in from.Sheets)
        {
            foreach (var table in sheet.Tables)
            {
                foreach (var column in table.Columns)
                {
                    if (target.TryGetValue((sheet.Code, table.Code, column.Code), out var id))
                    {
                        map[column.Id] = id;
                    }
                }
            }
        }

        return [.. cells.Select(c => map.TryGetValue(c.Column, out var id) ? c with { Column = id } : c)];
    }
}
```
2. `src/Ecr.Application/Ports/IDocumentVersionStore.cs:36-40`. Нові параметри мають значення за замовчуванням, тож порядок не ламається.
```diff
 public sealed record DocumentVersionRecord(
-    long Id, int SheetDefId, int PeriodKey, DateTime SubmittedAt, int SubmittedByUserId, string? SubmittedByDisplayName);
+    long Id, int SheetDefId, int PeriodKey, DateTime SubmittedAt, int SubmittedByUserId, string? SubmittedByDisplayName,
+    int TemplateVersionId = 0);
 
 /// <summary>Зріз подання з вмістом.</summary>
-public sealed record DocumentVersionPayload(long Id, int PeriodKey, string PayloadJson, int SheetDefId);
+public sealed record DocumentVersionPayload(long Id, int PeriodKey, string PayloadJson, int SheetDefId, int TemplateVersionId = 0);
```
3. `src/Ecr.Infrastructure/Persistence/DocumentVersionStore.cs:21-23, 31`:
```diff
             select new DocumentVersionRecord(
                 s.Id, s.SheetDefId, s.PeriodKey, s.SubmittedAt, s.SubmittedByUserId,
-                u == null ? null : u.DisplayName))
+                u == null ? null : u.DisplayName, s.TemplateVersionId))
…
-            .Select(s => new DocumentVersionPayload(s.Id, s.PeriodKey, s.PayloadJson, s.SheetDefId))
+            .Select(s => new DocumentVersionPayload(s.Id, s.PeriodKey, s.PayloadJson, s.SheetDefId, s.TemplateVersionId))
```
4. `src/Ecr.Application/Documents/DocumentVersionHandlers.cs`, `CompareDocumentVersionsHandler`.
   - (а) Нова приватна форма та новий `LoadAsync`, що замінює `:349-367`:
```csharp
    /// <summary>V-2: зріз, перекладений на версію документа в його періоді (колонки й аркуш — за кодами).</summary>
    private sealed record LoadedVersion(
        int PeriodKey, IReadOnlyList<SubmissionPayloadCell> Cells, IReadOnlyDictionary<string, SubmissionPayloadHeaderValue> Header);

    private async Task<LoadedVersion> LoadAsync(
        long documentId, long versionId, Security.DocumentReadScope readable, CancellationToken ct)
    {
        var found = await versions.FindAsync(documentId, versionId, ct).ConfigureAwait(false);
        if (found is not null)
        {
            var currentVersionId = await DocumentPeriodVersion.GetAsync(documents, documentId, found.PeriodKey, ct).ConfigureAwait(false);
            var current = await metadata.GetAsync(currentVersionId, ct).ConfigureAwait(false);
            var source = found.TemplateVersionId > 0 && found.TemplateVersionId != currentVersionId
                ? await metadata.GetAsync(found.TemplateVersionId, ct).ConfigureAwait(false)
                : current;
            var sheetDefId = SubmissionSnapshotTranslation.Sheet(found.SheetDefId, source, current) ?? found.SheetDefId;
            if (readable.InPeriod(new PeriodKey(found.PeriodKey)).CanReadSheet(sheetDefId))
            {
                return new LoadedVersion(
                    found.PeriodKey,
                    SubmissionSnapshotTranslation.Cells(SubmissionPayload.Read(found.PayloadJson), source, current),
                    SubmissionPayload.ReadHeader(found.PayloadJson));
            }
        }

        throw new NotFoundException(
            "ECR-DOC-0404",
            $"Версії {versionId} документа {documentId} немає.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = "err.ECR-DOC-0404.version",
                ["versionId"] = versionId.ToString(CultureInfo.InvariantCulture),
                ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
            });
    }
```
   Порядок «невидимий = неіснуючий» (L1-18) не змінюється: відмова, як і раніше, до повернення вмісту.
   - (б) У `HandleAsync` (`:137-172`) замінити звернення до `PayloadJson`:
```diff
-            newerCells = SubmissionPayload.Read(newer.PayloadJson);
-            newerHeader = SubmissionPayload.ReadHeader(newer.PayloadJson);
+            newerCells = newer.Cells;
+            newerHeader = newer.Header;
…
-            SubmissionPayload.Read(older.PayloadJson), newerCells,
-            SubmissionPayload.ReadHeader(older.PayloadJson), newerHeader,
+            older.Cells, newerCells,
+            older.Header, newerHeader,
```
5. Той самий файл, `ListDocumentVersionsHandler`.
   - Додати необов'язкові параметри конструктора в кінець: `IMetadataCache? metadata = null, IDocumentStore? documents = null`.
   - Замінити `:80-86`:
```diff
         var list = await versions.ListAsync(documentId, key, MaxVersions, ct).ConfigureAwait(false);
-        return list
-            .Where(v => readable.CanReadSheet(v.SheetDefId))
-            .Select(v => new DocumentVersionDto(
-                v.Id, v.SheetDefId, v.PeriodKey, v.SubmittedAt,
+
+        // ⛔ V-2: зріз до переносу проєкту — на аркуші СТАРОЇ версії; межі й клієнт знають аркуші поточної (за кодом).
+        var currentVersionId = metadata is null || documents is null
+            ? 0
+            : await DocumentPeriodVersion.GetAsync(documents, documentId, key.Value, ct).ConfigureAwait(false);
+        var current = currentVersionId == 0 ? null : await metadata!.GetAsync(currentVersionId, ct).ConfigureAwait(false);
+        var translated = new List<(DocumentVersionRecord Record, int SheetDefId)>(list.Count);
+        foreach (var v in list)
+        {
+            var sheet = v.SheetDefId;
+            if (current is not null && v.TemplateVersionId > 0 && v.TemplateVersionId != currentVersionId)
+            {
+                var source = await metadata!.GetAsync(v.TemplateVersionId, ct).ConfigureAwait(false);
+                sheet = Workflow.SubmissionSnapshotTranslation.Sheet(v.SheetDefId, source, current) ?? v.SheetDefId;
+            }
+
+            translated.Add((v, sheet));
+        }
+
+        return translated
+            .Where(x => readable.CanReadSheet(x.SheetDefId))
+            .Select(x => (v: x.Record, x.SheetDefId))
+            .Select(x => new DocumentVersionDto(
+                x.v.Id, x.SheetDefId, x.v.PeriodKey, x.v.SubmittedAt,
-                v.SubmittedByDisplayName ?? "#" + v.SubmittedByUserId.ToString(CultureInfo.InvariantCulture)))
+                x.v.SubmittedByDisplayName ?? "#" + x.v.SubmittedByUserId.ToString(CultureInfo.InvariantCulture)))
             .ToList();
```
   `IMetadataCache` кешує знімки: одна-дві версії на перелік, без запиту на кожен зріз.
6. Контракт `docs/build/02-contracts.md`, розділ версій документа (`GET /documents/{id}/versions`, `/compare`). Дописати: «`sheetDefId` і колонки зрізу, знятого до переносу проєкту, перекладаються за кодами на версію документа в періоді; колонка, якої в цій версії немає, у порівнянні не показується». Схема OpenAPI не змінюється.

**Тест-доказ:** новий файл `tests/Ecr.Api.Tests/DocumentVersionMigrationTests.Snapshots.cs` (`partial class DocumentVersionMigrationTests`).
- Сценарій: `ArrangeAsync(Target.OnlyLabels)`, потім `POST /api/v1/documents/{id}/submit` (зріз №1). Далі повернути (`reopen`), змінити `C2` на 43 через `PATCH …/cells`, подати знову (зріз №2). Потім перенос `Presentation` (`PostAsync(client, s, "Presentation", dryRun: false)`).
- Перевірки:
  - (а) `GET …/compare?from=<1>&to=<2>` містить рівно одну зміну `C2`: 42 → 43;
  - (б) `GET …/compare?from=<2>&to=current` повертає `changes = []`;
  - (в) `GET …/versions` повертає обидва зрізи з `sheetDefId == s.TargetSheetDefId`.

Без фіксу (а) дає 0 змін, (б) — стільки змін, скільки непорожніх комірок, (в) — старий `sheetDefId`. Мутація для перевірки: у `LoadAsync` повернути `SubmissionPayload.Read(found.PayloadJson)` без `Cells(...)` → (а) і (б) червоніють.

Unit-тест `tests/Ecr.Application.Tests/Workflow/SubmissionSnapshotTranslationTests.cs`: дві версії з тими самими кодами й різними Id → переклад комірок і аркуша. Колонка, якої в цілі немає, лишається як є.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Application.Tests --filter FullyQualifiedName~SubmissionSnapshotTranslationTests`; `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~DocumentCompareTests|FullyQualifiedName~DocumentVersionMigrationTests.Snapshots|FullyQualifiedName~HiddenSheetOracleTests"`. На стенді: документ Land із поданнями до переносу → «Версії» → «Порівняти».

**Ризики і побічні ефекти:**
- Знімок старої версії (`metadata.GetAsync`) — одне звернення до кешу, на холодному кеші — один прогрів.
- Стелі тестів фіксованої кількості запитів для `compare` перевірити (`grep -rn "QueryCount" tests | grep -i compare`). Якщо запит додається, його породжує лише холодний кеш.
- Колонка або таблиця, перейменована мапою перейменувань, за кодом не перекладається і в порівнянні не видна. Це залишковий ризик; `cfg.ColumnRename` можна додати окремим кроком.
- Межа L1-18 (невидима версія = 404) зберігається.

**Обґрунтування.** Переклад під час читання замість переписування зрізу в переносі: зріз — незмінний доказ, тож `TemplateVersionId` у ньому лишається. Переклад за кодами з уже наявних знімків кешу не потребує нового порту.

**Рішення PO:** не потрібне.

**Впевненість:** підтверджено читанням. Пункт 5 сценарію (рольові межі за кодом аркуша) потребує перевірки на стенді.

**Залежності:** V-3 використовує `SubmissionSnapshotTranslation`, тому робиться після V-2.

---

### V-3 — Low — Перше затвердження перенесеного поданого аркуша знімає зайвий зріз від імені погоджувача

**Категорія:** correctness (журнал версій, D-442)

**Де:**
- `src/Ecr.Application/Workflow/ApprovalSnapshotRefresh.cs:73-150`, зокрема `:108-123`;
- `src/Ecr.Infrastructure/Persistence/WorkflowStore.cs:416-433` — `GetLatestSnapshotKeyCoreAsync`, фільтр `s.SheetDefId == sheetDefId`;
- перенос перемаплює `wf.ApprovalState`/`ApprovalEvent` на нові `SheetDefId` (`DocumentVersionMigrationStore.cs:1849-1861`), а `calc.SubmissionSnapshot` — ні (`:1439-1440`);
- виклик: `src/Ecr.Application/Workflow/ApproveSheetHandler.cs:219-223`.

**Проблема.** LP-32 переносить поданий аркуш без змін, а його стан тепер на новому `SheetDefId`. При затвердженні `RefreshAsync` шукає останній зріз за новим `SheetDefId` і не знаходить його. `SheetChanged(null, …)` повертає `true`:
```csharp
        if (latestPayload is null)
        {
            return true;
        }
```
Далі аркуш перераховується під замком подання (`:126-128`), і зберігається новий зріз з автором-погоджувачем (`SubmittedByUserId: userId`, `:135-147`). У «Версіях» з'являється запис, ніби погоджувач правив аркуш.

**Сценарій відтворення:** подати аркуш → перенести проєкт (план аркуша не зачіпає, LP-32) → затвердити. Фактично в `calc.SubmissionSnapshot` з'являється новий рядок з `SubmittedByUserId = погоджувач`, хоча значення не змінювалися. Очікувано за D-442: зріз лише тоді, коли погоджувач **правив** аркуш (`ApprovalSnapshotRefresh.cs:18-22`).

**Вплив:** кожен поданий аркуш, перенесений LP-32, при першому затвердженні: хибний запис історії та зайвий перерахунок. Значення не втрачаються.

**Зміна — покроково:**
1. `src/Ecr.Application/Ports/IWorkflowStore.cs` — новий метод інтерфейсу поруч із `GetLatestSnapshotKeyAsync`:
```csharp
    /// <summary>
    /// V-3: найновіший зріз аркуша з тим самим КОДОМ (оригінал чи копія) за період — зокрема знятий на іншій версії
    /// шаблону до переносу проєкту; <c>null</c> — немає.
    /// </summary>
    public Task<(long Id, int TemplateVersionId)?> FindLatestSnapshotOfSheetCodeAsync(
        long documentId, string sheetCode, PeriodKey periodKey, long? sheetCopyId, CancellationToken ct);
```
2. `src/Ecr.Infrastructure/Persistence/WorkflowStore.cs` — реалізація після `GetLatestSnapshotKeyCoreAsync` (`:433`):
```csharp
    /// <inheritdoc />
    public async Task<(long Id, int TemplateVersionId)?> FindLatestSnapshotOfSheetCodeAsync(
        long documentId, string sheetCode, PeriodKey periodKey, long? sheetCopyId, CancellationToken ct)
    {
        var row = await (
                from s in db.SubmissionSnapshots.AsNoTracking()
                join sd in db.SheetDefs.AsNoTracking() on s.SheetDefId equals sd.Id
                where s.DocumentId == documentId && s.PeriodKey == periodKey.Value && s.SheetCopyId == sheetCopyId
                      && sd.Code == sheetCode
                orderby s.SubmittedAt descending, s.Id descending
                select new { s.Id, s.TemplateVersionId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return row is null ? null : (row.Id, row.TemplateVersionId);
    }
```
   Перевірити `grep -rn ": IWorkflowStore" src tests`: кожна реалізація чи тестовий двійник має отримати метод (двійникам достатньо `=> Task.FromResult<(long, int)?>(null)`).
3. `src/Ecr.Application/Workflow/ApprovalSnapshotRefresh.cs`.
   - (а) Нове перевантаження `SheetChanged` поруч із наявним (`:159`):
```csharp
    /// <summary>V-3: те саме порівняння, коли останній зріз уже розібрано (і перекладено на версію документа).</summary>
    public static bool SheetChanged(IReadOnlyList<SubmissionPayloadCell>? latestCells, string livePayload, IReadOnlySet<int> sheetColumns)
    {
        ArgumentNullException.ThrowIfNull(livePayload);
        ArgumentNullException.ThrowIfNull(sheetColumns);
        if (latestCells is null)
        {
            return true;
        }

        var before = Filter(latestCells, sheetColumns);
        var after = Filter(SubmissionPayload.Read(livePayload), sheetColumns);
        return !(before.Count == after.Count && SequenceEqual(before, after)) && !before.ToHashSet().SetEquals(after);
    }
```
   - (б) У `RefreshAsync` замінити `:116-123`:
```diff
-        var latest = latestKey is { } found
-            ? await workflow.GetSnapshotPayloadAsync(found.Id, ct).ConfigureAwait(false)
-            : null;
-
-        if (!SheetChanged(latest, live, sheetColumns))
-        {
-            return false;
-        }
+        bool changed;
+        if (latestKey is { } found)
+        {
+            changed = SheetChanged(await workflow.GetSnapshotPayloadAsync(found.Id, ct).ConfigureAwait(false), live, sheetColumns);
+        }
+        else
+        {
+            // ⛔ V-3: перенос проєкту лишив зріз на аркуші СТАРОЇ версії (стан — уже на новому SheetDefId): шукаємо за
+            // кодом аркуша і порівнюємо після перекладу колонок; інакше кожне перше затвердження після переносу
+            // знімало б зріз «правки погоджувача», якої не було.
+            changed = true;
+            if (snapshot.Sheets.FirstOrDefault(s => s.Id == sheetDefId) is { } sheetDef
+                && await workflow.FindLatestSnapshotOfSheetCodeAsync(documentId, sheetDef.Code, periodKey, sheetCopyId, ct)
+                    .ConfigureAwait(false) is { } foreign
+                && foreign.TemplateVersionId != templateVersionId
+                && await workflow.GetSnapshotPayloadAsync(foreign.Id, ct).ConfigureAwait(false) is { } foreignPayload)
+            {
+                var source = await metadata.GetAsync(foreign.TemplateVersionId, ct).ConfigureAwait(false);
+                changed = SheetChanged(
+                    SubmissionSnapshotTranslation.Cells(SubmissionPayload.Read(foreignPayload), source, snapshot),
+                    live, sheetColumns);
+            }
+        }
+
+        if (!changed)
+        {
+            return false;
+        }
```

**Тест-доказ:** у новому `tests/Ecr.Api.Tests/DocumentVersionMigrationTests.Snapshots.cs` (той самий, що у V-2) кейс `Затвердження_після_переносу_без_правок_не_знімає_нового_зрізу`. Кроки: подати → перенести `Presentation` → затвердити → `COUNT(*)` у `calc.SubmissionSnapshot` для документа й періоду дорівнює 1. Без фіксу виходить 2. Мутація: прибрати гілку `else` → тест червоніє.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Application.Tests --filter FullyQualifiedName~ApprovalSnapshotRefresh`; `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~DocumentVersionMigrationTests.Snapshots|FullyQualifiedName~ApproverEdit"`.

**Ризики і побічні ефекти:**
- Гілка `else` спрацьовує лише тоді, коли зрізу на поточному `SheetDefId` немає, тож звичайне затвердження не змінюється (PERF AN-135 зберігається).
- На старому зрізі додається одне читання payload (~10 МБ на великому документі), один раз на аркуш після переносу.

**Обґрунтування.** Альтернативу «перемапити `SheetDefId` зрізу в переносі» відкинуто: зріз містив би `TemplateVersionId` старої версії разом з аркушем нової, а payload — старі колонки.

**Рішення PO:** не потрібне.

**Впевненість:** підтверджено читанням.

**Залежності:** V-2 (`SubmissionSnapshotTranslation`).

---

### V-4 — Medium — Історія комірки й журнал документа після переносу не показують змін до нього

**Категорія:** audit (безперервність журналу)

**Де:**
- журнал не переписується (свідомо): `src/Ecr.Infrastructure/Persistence/DocumentVersionMigrationStore.cs:1439-1440` («aud.* — незмінний журнал»);
- `src/Ecr.Application/Audit/GetCellChangesHandler.cs:104-124, 146-155` — `VisibleColumnIds = readable.VisibleColumnIds()` і фільтр `readable.CanReadColumn`;
- `src/Ecr.Application/Security/DocumentReadScope.cs:335-336` — лише колонки поточного знімка;
- `src/Ecr.Infrastructure/Persistence/AuditReader.cs:424-427` — `ColumnDefId = @columnDefId` для однієї комірки;
- `:444-459` — `ColumnDefId IN (@visibleColumns)`;
- порт `src/Ecr.Application/Ports/IAuditReader.cs:170-195`.
- Той самий клас у суміжних читачах. Вони не входять у цю зміну, див. «Ризики»:
  - `AuditReader.ReadLastChangesAsync` (`:1115-1230`) — «хто змінив останнім» у сітці;
  - `ReadOutOfWindowCellsAsync` (`:1234-1308`) — значок «правка поза вікном», D-239;
  - `src/Ecr.Infrastructure/Persistence/CellSourceStore.cs:30-42` — панель «Джерело» D-463.

**Проблема.** Перенос переселяє комірку на `ColumnDefId` цільової версії: `TableRowId` той самий, `ColumnDefId` новий. Рядки `aud.CellChange` до переносу лишаються на старому `ColumnDefId`. Історія комірки в інспекторі запитує `columnDefId` поточної версії і старих рядків не знаходить:
```csharp
        if (filter.ColumnDefId is { } columnDefId)
        {
            And("ColumnDefId = @columnDefId", "@columnDefId", columnDefId, SqlDbType.Int);
        }
```
Журнал документа (`Security.ViewAudit`) обмежений видимими колонками поточного знімка (`filter with { VisibleColumnIds = readable.VisibleColumnIds() }`), тож усі рядки до переносу відсіюються в SQL як «невидимі» (R-11).

**Сценарій відтворення:** змінити `C2` у документі (рядок журналу з `ColumnDefId = C2@V1`) → перенести проєкт на V2 → відкрити інспектор комірки `C2`, вкладку «Історія» (`GET /api/v1/audit/cells?documentId&rowKey&columnDefId=C2@V2&from&to`). Фактично історія порожня, і журнал документа за вікно теж порожній. Очікувано: комірка та сама (ті самі коди й рядок), тож історія безперервна. Аналог — правило читання з перекладом за шляхом, `IColumnPathMapper` (C1).

**Вплив:** кожен документ кожного перенесеного проєкту. Історія «хто, коли, що» до переносу зникає з інтерфейсу. У базі вона є, але з продукту недоступна. Для звітності регулятору й аудиту це втрата доказу.

**Зміна — покроково:**
1. `src/Ecr.Application/Ports/IColumnPathMapper.cs` — новий метод інтерфейсу:
```csharp
    /// <summary>
    /// V-4: «родовід» колонок — Id ТІЄЇ САМОЇ колонки (шлях «аркуш → таблиця → колонка») в усіх версіях її шаблону
    /// плюс джерела пар <c>cfg.ColumnRename</c>, що ведуть у будь-який із них. Для читачів журналу, який після переносу
    /// проєкту лишається на Id старої версії.
    /// </summary>
    /// <returns>Для кожної названої колонки — непорожній перелік, що містить і її саму (невідомий Id — лише він сам).</returns>
    public Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> LineageAsync(IReadOnlyCollection<int> columnIds, CancellationToken ct);
```
2. `src/Ecr.Infrastructure/Persistence/ColumnPathMapper.cs` — реалізація (нові `using` не потрібні):
```csharp
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> LineageAsync(
        IReadOnlyCollection<int> columnIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(columnIds);
        var wanted = columnIds.Distinct().ToList();
        var result = wanted.ToDictionary(id => id, id => new HashSet<int> { id });
        if (wanted.Count == 0)
        {
            return new Dictionary<int, IReadOnlyList<int>>();
        }

        var paths = await (
                from column in db.ColumnDefs.AsNoTracking()
                where wanted.Contains(column.Id)
                join table in db.TableDefs.AsNoTracking() on column.TableDefId equals table.Id
                join sheet in db.SheetDefs.AsNoTracking() on table.SheetDefId equals sheet.Id
                join version in db.TemplateVersions.AsNoTracking() on sheet.TemplateVersionId equals version.Id
                select new { column.Id, version.TemplateId, SheetCode = sheet.Code, TableCode = table.Code, ColumnCode = column.Code })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var templates = paths.Select(p => p.TemplateId).Distinct().ToList();
        var codes = paths.Select(p => p.ColumnCode).Distinct().ToList();
        var siblings = await (
                from column in db.ColumnDefs.AsNoTracking()
                where codes.Contains(column.Code)
                join table in db.TableDefs.AsNoTracking() on column.TableDefId equals table.Id
                join sheet in db.SheetDefs.AsNoTracking() on table.SheetDefId equals sheet.Id
                join version in db.TemplateVersions.AsNoTracking() on sheet.TemplateVersionId equals version.Id
                where templates.Contains(version.TemplateId)
                select new { column.Id, version.TemplateId, SheetCode = sheet.Code, TableCode = table.Code, ColumnCode = column.Code })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var byPath = siblings.ToLookup(s => (s.TemplateId, s.SheetCode, s.TableCode, s.ColumnCode), s => s.Id);
        foreach (var p in paths)
        {
            result[p.Id].UnionWith(byPath[(p.TemplateId, p.SheetCode, p.TableCode, p.ColumnCode)]);
        }

        // Пари збереженої мапи перейменувань (FinishSql 6b): колонка, перейменована переносом, має інший код.
        var all = result.Values.SelectMany(s => s).Distinct().ToList();
        var renames = await db.ColumnRenames.AsNoTracking()
            .Where(r => all.Contains(r.ToColumnDefId))
            .Select(r => new { r.FromColumnDefId, r.ToColumnDefId })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var set in result.Values)
        {
            set.UnionWith(renames.Where(r => set.Contains(r.ToColumnDefId)).Select(r => r.FromColumnDefId).ToList());
        }

        return result.ToDictionary(p => p.Key, p => (IReadOnlyList<int>)[.. p.Value.Order()]);
    }
```
   `db.ColumnRenames` — `EcrDbContext.cs:95`; поля `FromColumnDefId`/`ToColumnDefId` — сутність `ColumnRename`.
3. `src/Ecr.Application/Documents/PrefetchedColumnMapper.cs` — делегування (клас реалізує той самий інтерфейс):
```csharp
    public Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> LineageAsync(IReadOnlyCollection<int> columnIds, CancellationToken ct)
        => _inner.LineageAsync(columnIds, ct);
```
4. `src/Ecr.Application/Ports/IAuditReader.cs`, `CellChangeFilter` (`:170-183`) — новий параметр **останнім** (позиційна сумісність):
```diff
     bool HumanOnly = false,
-    AuditSort Sort = AuditSort.ChangedAtDesc)
+    AuditSort Sort = AuditSort.ChangedAtDesc,
+    IReadOnlyList<int>? ColumnLineage = null)
```
   Документація параметра: «V-4: Id тієї самої колонки в інших версіях шаблону; з `ColumnDefId` фільтр — `IN` по цьому переліку».
5. `src/Ecr.Infrastructure/Persistence/AuditReader.cs:424-427`:
```diff
         if (filter.ColumnDefId is { } columnDefId)
         {
-            And("ColumnDefId = @columnDefId", "@columnDefId", columnDefId, SqlDbType.Int);
+            if (filter.ColumnLineage is { Count: > 1 } lineage)
+            {
+                // ⛔ V-4: та сама комірка на колонках інших версій шаблону (перенос проєкту) — історія не обривається.
+                where.Append("\n                   AND ColumnDefId IN (SELECT CAST([value] AS int) FROM OPENJSON(@columnLineage))");
+                command.Parameters.Add("@columnLineage", SqlDbType.NVarChar, -1).Value =
+                    "[" + string.Join(",", lineage.Select(v => v.ToString(CultureInfo.InvariantCulture))) + "]";
+            }
+            else
+            {
+                And("ColumnDefId = @columnDefId", "@columnDefId", columnDefId, SqlDbType.Int);
+            }
         }
```
6. `src/Ecr.Application/Audit/GetCellChangesHandler.cs`.
   - (а) Конструктор (`:9-10`): додати необов'язковий параметр **останнім**: `IColumnPathMapper? columns = null`.
   - (б) Новий приватний помічник:
```csharp
    /// <summary>V-4: видимі колонки разом з їхніми Id у версіях до переносу проєкту.</summary>
    private async Task<IReadOnlyList<int>> WithLineageAsync(IReadOnlyList<int> visible, CancellationToken ct)
    {
        if (columns is null || visible.Count == 0)
        {
            return visible;
        }

        var lineage = await columns.LineageAsync(visible, ct).ConfigureAwait(false);
        return [.. visible.Concat(lineage.Values.SelectMany(v => v)).Distinct().Order()];
    }
```
   - (в) У `HandleAsync` (`:104-124`):
```diff
-        if (readable is not null)
-        {
-            filter = filter with { VisibleColumnIds = readable.VisibleColumnIds() };
-        }
+        IReadOnlySet<int>? visibleSet = null;
+        if (readable is not null)
+        {
+            var visible = await WithLineageAsync(readable.VisibleColumnIds(), ct).ConfigureAwait(false);
+            visibleSet = visible.ToHashSet();
+            filter = filter with { VisibleColumnIds = visible };
+        }
+
+        // ⛔ V-4: адреса однієї комірки — і на її колонках у версіях до переносу проєкту.
+        if (filter.IsSingleCell && columns is not null
+            && (readable is null || readable.CanReadColumn(filter.ColumnDefId!.Value)))
+        {
+            var lineage = await columns.LineageAsync([filter.ColumnDefId!.Value], ct).ConfigureAwait(false);
+            filter = filter with { ColumnLineage = lineage[filter.ColumnDefId!.Value] };
+        }
…
-                counts.Where(c => readable is null || readable.CanReadColumn(c.ColumnDefId)).Sum(c => c.Total), int.MaxValue);
+                counts.Where(c => visibleSet is null || visibleSet.Contains(c.ColumnDefId)).Sum(c => c.Total), int.MaxValue);
…
-        var visible = readable is null
-            ? result.Items
-            : [.. result.Items.Where(c => readable.CanReadColumn(c.ColumnDefId))];
+        var shown = visibleSet is null
+            ? result.Items
+            : [.. result.Items.Where(c => visibleSet.Contains(c.ColumnDefId))];
 
-        return result with { Items = visible, TotalCount = total };
+        return result with { Items = shown, TotalCount = total };
```
   - (г) У `SummaryAsync` (`:146-155`) зробити те саме: `WithLineageAsync` + `visibleSet` замість `readable.CanReadColumn`.

**Тест-доказ:**
- Новий файл `tests/Ecr.Api.Tests/DocumentVersionMigrationTests.AuditContinuity.cs`, кейс `Історія_комірки_не_обривається_на_переносі_версії`. Кроки: `ArrangeAsync(Target.OnlyLabels)` → `PATCH …/cells` (`C2 = 7`) → перенос `Presentation` → `GET /api/v1/audit/cells?documentId=…&rowKey=R1&columnDefId=<s.TargetColumns["C2"]>&from=…&to=…` → у `items` є рядок із `newValue = "7"`. Другий кейс для журналу документа: `GET /api/v1/audit/cells?documentId=…&from&to` від користувача з `Security.ViewAudit`. Без фіксу обидва повертають 0 елементів. Мутація: у `AuditReader` прибрати гілку `ColumnLineage` → перший тест червоніє.
- Інфраструктурний тест `tests/Ecr.Infrastructure.Tests/Persistence/ColumnPathMapperLineageTests.cs`: дві версії з колонкою `C2` (+ пара `cfg.ColumnRename` X → C2@V2) → `LineageAsync([C2@V2])` повертає `{C2@V1, C2@V2, X}`.

**Перевірка після впровадження:**
```
dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~DocumentVersionMigrationTests.AuditContinuity|FullyQualifiedName~DenyReadAudit|FullyQualifiedName~CellHistory"
dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~CellHistoryPermissionThenReadScopeTests|FullyQualifiedName~ErrorFamilyTests"
dotnet test tests/Ecr.Architecture.Tests
```
На стенді: інспектор комірки перенесеного документа Land.

**Ризики і побічні ефекти:**
- **Безпека R-11.** Родовід розширюється лише від колонок, які читач **бачить** у поточній версії. Гранти копіюються за кодом (`FinishSql`), тож видима колонка нової версії відповідає тій самій колонці старої. Тести `DenyReadAudit*` мають лишитися зеленими.
- **Кількість запитів.** Додається 3 запити на сторінку журналу документа. Якщо є тести фіксованої кількості запитів для `audit/cells`, стелі не піднімати. Тоді родовід кешувати в `IMemoryCache` за ключем шаблону (версії незмінні після публікації) або рахувати лише на першій сторінці й класти в курсор. Вибір — за результатом `grep -rn "QueryCount" tests | grep -i audit`.
- `VisibleColumnIds` росте пропорційно кількості версій: тисячі Id в одному JSON-параметрі, що допустимо.
- Суміжні читачі `ReadLastChangesAsync`, `ReadOutOfWindowCellsAsync` і `CellSourceStore` лишаються з тим самим обривом. Значок «поза вікном» і «останній автор» для комірок до переносу після нього зникають. Їх варто доробити тим самим `LineageAsync` окремою зміною.

**Обґрунтування.** Журнал незмінний (B01 §6.4, `IntegrationCellPatcher.cs:846-850`), тож переписувати `aud.CellChange` у переносі не можна. Переклад під час читання за шляхом — той самий прийом, що C1 (`IColumnPathMapper`).

**Рішення PO:** не потрібне.

**Впевненість:** підтверджено читанням.

**Залежності:** немає.

---

### V-5 — Low — Після переносу з перейменуванням колонок стабільні вʼюхи SSRS не перегенеровуються

**Категорія:** reporting

**Де:**
- `src/Ecr.Infrastructure/Persistence/DocumentVersionMigrationStore.cs:1908-1912` — `INSERT cfg.ColumnRename` у `FinishSql`;
- `src/Ecr.Application/Documents/VersionMigration/MigrateDocumentVersionHandler.cs:1351-1357` — `RenamePairs` (кожна пара колонок з різним кодом);
- `:1116-1133` — після коміту лише профілі, шкала й перерахунок, генерації вʼюх немає;
- `src/Ecr.Infrastructure/Persistence/Sql/05-rpt-views.sql:969-1015` — аліаси `cfg.ColumnRename` вшиваються в текст стабільної вʼюхи під час **генерації**;
- `:111-160` — відбиток містить `cfg.ColumnRename`;
- генерація: лише публікація (`PublishTemplateVersionHandler.cs:300-317`) і старт (`src/Ecr.Api/Startup/StartupSequence.cs:120-141`).

**Проблема.** Стабільна вʼюха (RPT-STABLE Part B) показує перейменовану колонку під **обома** кодами, читаючи всі Id класу аліасів. Але клас будується з `cfg.ColumnRename` у момент генерації, а нова пара з'являється лише в `FinishSql` переносу. До перезапуску застосунку чи наступної публікації колонка зі старим кодом читає тільки старі Id, а дані проєкту вже на новому Id. Тест `DocumentVersionMigrationTests.FromMonth.cs:366-368` генерує вʼюхи сам (`generator.GenerateAsync(...)`), тож прогалини не бачить.

**Сценарій відтворення:** версія V2 перейменовує колонку `C2 → C2X` → перенос з `tableRenames: [{fromTableCode: T, toTableCode: T, columns: [{fromColumnCode: C2, toColumnCode: C2X}]}]` → `SELECT C2 FROM rpt.v_<tpl>_<sheet>_<table>` для рядків проєкту. Фактично NULL. Очікувано — значення, як після `EXEC rpt.usp_GenerateStableViews`.

**Вплив:** звіти SSRS, побудовані на старому коді колонки, для перенесеного проєкту порожні до перезапуску. Лише переноси з мапою перейменувань колонок.

**Зміна — покроково:**
1. Новий файл `src/Ecr.Application/Documents/VersionMigration/MigrationReportViews.cs` (за зразком `MigrationRecalculation.cs`):
```csharp
using Ecr.Application.Ports;
using Microsoft.Extensions.Logging;

namespace Ecr.Application.Documents.VersionMigration;

/// <summary>
/// V-5: стабільні вʼюхи SSRS після переносу, що зберіг нові аліаси колонок (<c>cfg.ColumnRename</c>): аліаси
/// вшиваються в текст вʼюхи при генерації. Після коміту, «найкращим зусиллям» (як у публікації).
/// </summary>
public static partial class MigrationReportViews
{
    /// <summary>Перегенеровує вʼюхи шаблону цільової версії, коли перенос зберіг пари перейменувань; ніколи не кидає.</summary>
    public static async Task RefreshAsync(
        IReportViewGenerator? views, ILogger log, int targetVersionId, int columnRenames, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (views is null || columnRenames == 0)
        {
            return;
        }

        try
        {
            await views.GenerateAsync(targetVersionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            LogFailed(log, ex, targetVersionId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Перенос версії: вʼюхи rpt.* шаблону версії {TemplateVersionId} не перегенеровано після нових аліасів cfg.ColumnRename; повтор — EXEC rpt.usp_GenerateStableViews або перезапуск застосунку.")]
    private static partial void LogFailed(ILogger log, Exception ex, int templateVersionId);
}
```
2. `src/Ecr.Application/Documents/VersionMigration/MigrateDocumentVersionHandler.cs`.
   - Конструктор (`:235`): після `VersionMigrationOptions? migrationOptions = null` додати `, IReportViewGenerator? reportViews = null` із коментарем `// ✎ V-5`.
   - Після `timeline?.Invalidate(projectId);` (`:1123`):
```diff
         timeline?.Invalidate(projectId);
 
+        // ⛔ V-5: нові аліаси колонок (cfg.ColumnRename) діють у стабільних вʼюхах лише після генерації.
+        if (result is not null)
+        {
+            await MigrationReportViews
+                .RefreshAsync(reportViews, log, target.Id, planned.Plan.ColumnRenames.Count, ct)
+                .ConfigureAwait(false);
+        }
+
```
3. `docs/admin/admin-guide.md:288-318`: дописати один рядок «після переносу з перейменуванням колонок вʼюхи SSRS перегенеровуються автоматично; у разі збою — Warning у журналі й `EXEC rpt.usp_GenerateStableViews`».

**Тест-доказ:** `tests/Ecr.Api.Tests/DocumentVersionMigrationTests.TableRenames.cs`.
- У `ArrangeAsync` додати `Target.ColumnRenamed`: та сама таблиця, `C2` у цілі має код `C2X_<tag>`.
- Новий кейс: згенерувати вʼюхи для обох версій (`new ReportViewGenerator(db).GenerateAsync(...)`, як `FromMonth.cs:366-368`) → перенос через API з мапою колонки → `SELECT [C2_<tag>] FROM rpt.<стабільна вʼюха> WHERE …` дає 42.
- Без фіксу результат NULL. Мутація: прибрати виклик `RefreshAsync` → тест червоніє.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~DocumentVersionMigrationTests.TableRenames|FullyQualifiedName~DocumentVersionMigrationTests.FromMonth"`, `dotnet test tests/Ecr.Infrastructure.Tests --filter FullyQualifiedName~ReportViewStable`, `dotnet test tests/Ecr.Architecture.Tests`.

**Ризики і побічні ефекти:**
- Генерація триває 12–20 с на повний набір (`StartupSequence.cs:131`, AN-155d). Вона після коміту й поза транзакцією, на час переносу у фоні, і лише за наявності перейменувань колонок.
- DDL робить процедура (`EXECUTE AS OWNER`, D-66). Застосунку вистачає `EXECUTE`, як у публікації.

**Обґрунтування.** Той самий «найкращим зусиллям» шлях, що в публікації. Альтернатива «не вшивати аліаси» зламала б AN-155d (незмінні вʼюхи не чіпаються).

**Рішення PO:** не потрібне.

**Впевненість:** підтверджено читанням.

**Залежності:** робити після L4 (D-6/D-7/D-10 правлять той самий обробник) і разом із V-7 (той самий файл).

---

### V-6 — Low — Планувальник не порівнює місце колонки в році (`IsMonthColumn`/`MonthNumber`)

**Категорія:** data-integrity

**Де:**
- `src/Ecr.Application/Documents/VersionMigration/VersionMigrationPlanner.cs:600-618` — `CompareColumns` порівнює `DataType`, `Precision`/`Scale`, `UnitId`, `LookupRegistryDefId`, `IsRequired` і презентацію, але не місяць;
- власник введеної комірки визначається саме місяцем колонки: `src/Ecr.Infrastructure/Persistence/MonthColumnRouter.cs:235-254`;
- стабільні вʼюхи теж читають місяць (`05-rpt-views.sql:1003-1005`, відбиток `:146-151`);
- прапор маршрутизації `Collection:MonthColumnRouting` (`src/Ecr.Api/appsettings.json:150`, типово `false`).

**Проблема.** У таблиці «місяці в колонках» значення місячної колонки фізично лежить в екземплярі місяця-власника (Q-17, `OwnerOf`). Перенос переселяє комірку на нову колонку з тим самим кодом, не змінюючи періоду екземпляра. Якщо у V2 та сама колонка має інший `MonthNumber` або стала чи перестала бути місячною, значення читається з іншого періоду і зникає з виду або «переїжджає» на інший місяць. План це мовчки пропускає як `Safe`.

**Сценарій відтворення:** V1, колонка `M03` з `IsMonthColumn = false` (шаблон, створений до PS-P1C, `ColumnDef.cs:230-235`); V2 — `SetMonth(true, 3)`. За увімкненої маршрутизації значення, введене в якорі року, після переносу читається з березня й не видно. Очікувано: зміна тлумачення, тобто `Modified`/Guarded зі значеннями, як для типу колонки.

**Вплив:** лише проєкти з таблицями «місяці в колонках» і версіями, що змінюють місце колонки в році. За вимкненого прапора зачеплено вʼюхи SSRS і агрегацію за місяцем. Рідко, але мовчки.

**Зміна — покроково:**
1. `src/Ecr.Application/Documents/VersionMigration/VersionMigrationPlanner.cs:604-605`:
```diff
             Guarded(items, path, values, nameof(ColumnDef.UnitId), column.UnitId, other.UnitId);
             Guarded(items, path, values, nameof(ColumnDef.LookupRegistryDefId), column.LookupRegistryDefId, other.LookupRegistryDefId);
+
+            // ⛔ V-6: місце колонки в році визначає період-власник введеного значення (MonthColumnRouter.OwnerOf,
+            // стабільні вʼюхи); перенос періодів не міняє — інша позиція = значення в іншому місяці.
+            Guarded(items, path, values, "MonthPlacement", MonthPlacement(column), MonthPlacement(other));
```
   Помічник поруч із `Guarded`:
```csharp
    /// <summary>V-6: місце колонки в році для порівняння: <c>none</c> або номер місяця.</summary>
    private static string MonthPlacement(ColumnDef column)
        => column.IsMonthColumn
            ? (column.MonthNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?")
            : "none";
```

**Тест-доказ:** `tests/Ecr.Application.Tests/Documents/VersionMigrationNumericWidenTests.cs` або новий `VersionMigrationMonthPlacementTests.cs` (за зразком `VersionMigrationCellFormulaTests`, помічник `Version(...)`). Кейс: у цілі `columnA.SetMonth(true, 3)`, у джерелі не місячна, значень 5 → `Assert.Single(plan.Items, i => i.Field == "MonthPlacement")` з `ChangeClass.Guarded`, а `RefusalsFor(Safe)` містить `guardedWithData`. Без фіксу пункту немає.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~VersionMigration"`.

**Ризики і побічні ефекти:** нові відмови `guardedWithData`, але лише за реальної зміни місця колонки зі значеннями. Без значень пункт усе одно зачіпає подані аркуші (LP-32, `TouchesLockedSheet`), що правильно.

**Обґрунтування.** Перенесення значень між періодами в SQL переносу — значно ризиковіша зміна. Чесна відмова узгоджена з рештою класу Guarded.

**Рішення PO:** не потрібне.

**Впевненість:** підтверджено читанням. Вплив за увімкненого прапора потребує перевірки на стенді.

**Залежності:** V-1 (той самий файл).

---

### V-7 — Low — Перенос знімає ручні значення (D-461) у поданих/затверджених аркушах, які LP-32 обіцяє переносити без змін

**✎ рішення PO 11.11:** варіант **(А)** — перенос версії відмовляє, поки на затвердженому аркуші лежить ручне значення замість формули (D-461), якого формула цільової версії вже не дозволяє; спершу аркуш повертають у роботу. Це узгоджено з наявним правилом для затверджених аркушів (LP-32). Окрема причина відмови `lockedOverridesLost` і окремий ключ помилки `err.ECR-DOC-0409.migrateLockedOverrides`. ⚠ Обсяг: LP-32 у коді не розрізняє «подано» і «затверджено» — набір замкнених пар береться з обох статусів (`DocumentVersionMigrationStore.cs:161-164`: `Submitted || Approved`), і поданий аркуш перерахунок теж не пише (рядок «Формули поданих і затверджених аркушів після переносу не перераховуються» у «Відкинуто»). Тому специфікація блокує і подані, і затверджені — так само, як `sheetsLocked`. Якщо PO хоче лише затверджені: у кроці 2 додати фільтр `l.Status == nameof(DocumentStatus.Approved)` (одне місце).

**Категорія:** data-integrity / workflow

**Де:**
- `src/Ecr.Application/Documents/VersionMigration/MigrateDocumentVersionHandler.cs:1396-1407` — `lostOverrides` (позначки, яких ціль не дозволяє) додаються лише як пункти звіту `OverrideLost`;
- `:1418-1422` — `ClassifyLocked` дає відмову `sheetsLocked` лише за планом структури (`plan.Sheets` / `plan.TouchedSheets`, `:1709-1747`), про позначки не знає;
- `:929-934` — та сама `PlanAsync` під блоками в застосуванні, далі `ThrowIfRefused`; сухий прогін — `:452-456` (та сама `PlanAsync`, `dryRun: true`);
- `:1032-1038`, `:1142-1191` — `ReleaseLostOverridesAsync` у T2 знімає позначки в УСІХ періодах діапазону, зокрема на поданих/затверджених аркушах;
- `:1818-1849` — гілка `409 ECR-DOC-0409` у `ThrowIfRefused` (лише `projectArchived` / `sheetsLocked`);
- `src/Ecr.Infrastructure/Persistence/FormulaOverrideStore.cs:235-256` — `DELETE doc.CellFormulaOverride` + `UPDATE CellValue SET IsCalculated = 1`;
- `src/Ecr.Infrastructure/Jobs/JobRetryPolicy.cs:128-133` — `MigrationRefusalKeys`: ключі, які фонова задача не повторює;
- обіцянка LP-32: `docs/build/02-contracts.md:4046-4051` («статус, значення й журнал погодження лишаються»), коментар `MigrateDocumentVersionHandler.cs:1411-1417`;
- UI: `src/Ecr.Web/src/features/documents/VersionMigrationDialog.tsx:29-52` (`refusalText`), `:519-525` (попередження `lostFormulaOverrides`, `data-migrate-lost-formula-overrides` на `:522`), `:538-567` (блок відмови `data-migrate-refused`).

**Проблема.** D-461 (30.10): ручне значення, якого цільова формула не дозволяє, перенос не блокує — позначка знімається, комірка стає обчисленою, журнал `FormulaRestored`. LP-32: поданий чи затверджений аркуш, якого план не зачіпає, переноситься без змін. Код поєднує їх так, що в затвердженому аркуші перенос змінює дані: позначка зникає, `IsCalculated = 1`, у журналі рядок `FormulaRestored` з `NewValue = null`. Перерахунок затверджений період пропускає, тож аж до повернення в роботу в комірці лежить «обчислене» число, яке насправді ввела людина.

**Сценарій відтворення:** формула з `AllowManualOverride` → ручне значення → аркуш подано й затверджено → клон версії, у клоні дозвіл вимкнено, публікація → перенос. Фактично: сухий прогін — `OverrideLost`, `canApply = true`; застосування — 200, позначки немає, `IsCalculated = 1`, новий рядок журналу. Очікувано (рішення PO 11.11): сухий прогін `canApply = false` з причиною `lockedOverridesLost` і переліком аркушів; застосування — `409 ECR-DOC-0409` (`err.ECR-DOC-0409.migrateLockedOverrides`), нічого не змінено; після «повернути в роботу» той самий перенос проходить і знімає позначку наявним шляхом D-461 (журнал `FormulaRestored`, причина `Version migration`).

**Вплив:** лише проєкти з D-461 у поданих/затверджених періодах і версії, що забирає дозвіл. Число не змінюється, але змінюється його походження в затвердженому документі.

**Зміна — покроково:**
1. `src/Ecr.Application/Documents/VersionMigration/MigrateDocumentVersionHandler.cs`, звіт — два нові необов'язкові поля в КІНЦІ `DocumentVersionMigrationDto` (адитивно; усі виклики конструктора вже передають хвіст іменовано, `:1559-1568`):
   - XML-документація перед `public sealed record DocumentVersionMigrationDto(` (`:113`), після блоку `<param name="Warnings">…</param>` (`:109-112`):
     ```csharp
     /// <param name="LockedOverridesLost">
     /// ✎ V-7 (рішення PO 11.11): скільки ручних значень замість формул (D-461), яких формула цільової версії не дозволяє,
     /// лежить на ПОДАНИХ або ЗАТВЕРДЖЕНИХ аркушах. Більше нуля — відмова <c>lockedOverridesLost</c> (<c>409 ECR-DOC-0409</c>,
     /// <c>migrateLockedOverrides</c>), доки аркуші не повернуть у роботу. <c>null</c> — таких немає.
     /// </param>
     /// <param name="LockedOverrideItems">
     /// ✎ V-7: ці подані/затверджені пари «аркуш × період» (<see cref="LockedSheetItem.Affected"/> = <c>true</c>), не більше
     /// <c>MaxLockedItems</c>; <c>null</c> — таких немає. ⛔ Лише коди аркушів, періоди й статуси.
     /// </param>
     ```
   - хвіст запису (`:153`):
     ```diff
     -    IReadOnlyList<VersionMigrationWarning>? Warnings = null);
     +    IReadOnlyList<VersionMigrationWarning>? Warnings = null,
     +    int? LockedOverridesLost = null,
     +    IReadOnlyList<LockedSheetItem>? LockedOverrideItems = null);
     ```
2. Той самий файл, `PlanAsync`, одразу після блоку `sheetsLocked` (`:1418-1422`):
   ```diff
            var locked = ClassifyLocked(scope, plan, from);
            if (locked.Affected > 0)
            {
                refusals.Add("sheetsLocked");
            }
   +
   +        // ✎ V-7 (рішення PO 11.11, варіант А): ручне значення (D-461), яке перенос зняв би (`lostOverrides`), на
   +        // поданому/затвердженому аркуші змінює його дані (позначка, IsCalculated, журнал FormulaRestored), а перерахунок
   +        // такий аркуш не пише. Тому — відмова, доки аркуш не повернуть у роботу, як решта LP-32. Окрема причина: план
   +        // структури цей аркуш може й не зачіпати.
   +        var lockedOverrides = LockedOverrideTouches(lostOverrides, from, scope.LockedSheetRefs);
   +        if (lockedOverrides.Marks > 0)
   +        {
   +            refusals.Add("lockedOverridesLost");
   +        }
   ```
   `scope.LockedSheetRefs == null` (обгортка сховища в тесті) — помічник повертає 0; у цьому разі `ClassifyLocked` і так рахує всі подані зачепленими (`:1719-1724`), тож відмова `sheetsLocked` уже є.
3. Той самий файл, конструкція звіту (`:1564-1568`):
   ```diff
                LostFormulaOverrides: lostOverrides.Count == 0 ? null : lostOverrides.Count,
                Range: Range,
                FromPeriodKey: Range == VersionMigrationRange.FromMonth ? FromPeriodKey : null,
                EffectiveFromPeriodKey: range.Effective,
   -            Warnings: range.Warnings);
   +            Warnings: range.Warnings,
   +            LockedOverridesLost: lockedOverrides.Marks == 0 ? null : lockedOverrides.Marks,
   +            LockedOverrideItems: lockedOverrides.Items.Count == 0 ? null : lockedOverrides.Items);
   ```
4. Той самий файл, новий помічник поруч із `ClassifyLocked` (після його кінця `:1747`, перед `RequireTargetAsync` `:1749`); `internal` — для юніт-тесту (`InternalsVisibleTo Ecr.Application.Tests`, `Ecr.Application.csproj:22`):
   ```csharp
   /// <summary>✎ V-7: ручні значення (D-461), що зникли б, на поданих/затверджених парах «документ × аркуш × період».</summary>
   /// <param name="Marks">Скільки позначок лежить на таких парах.</param>
   /// <param name="Items">Самі пари (≤ <c>MaxLockedItems</c>), <c>Affected = true</c>.</param>
   internal sealed record LockedOverrideClassification(int Marks, IReadOnlyList<LockedSheetItem> Items);

   /// <summary>
   /// ✎ V-7: які з позначок <paramref name="lost"/> (колонки ВИХІДНОЇ версії) лежать на поданих/затверджених парах
   /// <paramref name="lockedRefs"/>. Копії аркуша зараховуються разом з оригіналом: <see cref="VersionMigrationLockedSheet"/>
   /// не несе <c>SheetCopyId</c> (консервативно, як <see cref="ClassifyLocked"/>).
   /// </summary>
   internal static LockedOverrideClassification LockedOverrideTouches(
       IReadOnlyList<ProjectFormulaOverride> lost, TemplateVersionSnapshot from,
       IReadOnlyList<VersionMigrationLockedSheet>? lockedRefs)
   {
       if (lost.Count == 0 || lockedRefs is not { Count: > 0 })
       {
           return new LockedOverrideClassification(0, []);
       }

       var lockedStatus = lockedRefs
           .GroupBy(l => (l.DocumentId, l.SheetDefId, l.PeriodKey))
           .ToDictionary(g => g.Key, g => g.First().Status);
       var sheetOfTable = from.Sheets
           .SelectMany(s => s.Tables.Select(t => (TableId: t.Id, SheetId: s.Id)))
           .ToDictionary(x => x.TableId, x => x.SheetId);
       var codes = from.Sheets.ToDictionary(s => s.Id, s => s.Code.ToString());

       var marks = 0;
       var pairs = new Dictionary<(long DocumentId, int SheetDefId, int PeriodKey), string>();
       foreach (var mark in lost)
       {
           if (!from.ColumnsById.TryGetValue(mark.Address.ColumnDefId, out var column)
               || !sheetOfTable.TryGetValue(column.TableDefId, out var sheetId))
           {
               continue;
           }

           var key = (mark.DocumentId, sheetId, mark.Address.PeriodKey.Value);
           if (lockedStatus.TryGetValue(key, out var status))
           {
               marks++;
               pairs.TryAdd(key, status);
           }
       }

       var items = pairs
           .Select(p => new LockedSheetItem(
               p.Key.DocumentId,
               codes.GetValueOrDefault(p.Key.SheetDefId)
                   ?? string.Create(CultureInfo.InvariantCulture, $"#{p.Key.SheetDefId}"),
               p.Key.PeriodKey,
               p.Value,
               Affected: true))
           .OrderBy(i => i.SheetCode, StringComparer.Ordinal)
           .ThenBy(i => i.PeriodKey)
           .ThenBy(i => i.DocumentId)
           .Take(MaxLockedItems)
           .ToList();

       return new LockedOverrideClassification(marks, items);
   }
   ```
   Усі імена існують: `ProjectFormulaOverride(CellAddress Address, long DocumentId, string RowKey, string? Value)` (`IFormulaOverrideStore.cs:102`), `CellAddress(PeriodKey PeriodKey, long TableRowId, int ColumnDefId)` (`CellAddress.cs:11`), `VersionMigrationLockedSheet(long DocumentId, int SheetDefId, int PeriodKey, string Status)` (`IDocumentVersionMigrationStore.cs:23`), `from.ColumnsById` і `column.TableDefId` (так само в `FormulaOverrideMigration.cs:55-57`), `s.Code.ToString()` (так само в `ClassifyLocked`, `:1724`), `MaxLockedItems = 200` (`:309`). `using System.Globalization` уже є (`:1`).
5. Той самий файл, `ThrowIfRefused`, гілка 409 (`:1818-1849`):
   ```diff
   -        if (dto.Refusals.Contains("projectArchived") || dto.Refusals.Contains("sheetsLocked"))
   +        if (dto.Refusals.Contains("projectArchived") || dto.Refusals.Contains("sheetsLocked")
   +            || dto.Refusals.Contains("lockedOverridesLost"))
            {
                // Подане чи затверджене стосувалося СТАРОЇ структури, і зачепити його
                // переносом означало б видати чуже затвердження за нове. Спершу
                // повернути аркуші в роботу. (LP-32: незачеплені переносяться без змін.)
   +            // ✎ V-7: те саме для ручних значень (D-461), які перенос зняв би на поданому/затвердженому аркуші.
   +            // Пріоритет ключа: архів > структура (sheetsLocked) > ручні значення; `refusals` несе всі причини.
   +            var messageKey = dto.Refusals.Contains("projectArchived")
   +                ? "err.ECR-DOC-0409.migrateProjectArchived"
   +                : dto.Refusals.Contains("sheetsLocked")
   +                    ? "err.ECR-DOC-0409.migrateSheetsLocked"
   +                    : "err.ECR-DOC-0409.migrateLockedOverrides";
                throw new ConcurrencyConflictException(
                    ErrorCodes.DocumentSubmitted,
   -                dto.Refusals.Contains("projectArchived")
   -                    ? "Проєкт архівовано: документи архівного проєкту не переносяться."
   -                    : $"Перенос зачепив би подані або затверджені аркуші: {dto.LockedSheetsAffected}. Спершу поверніть їх у роботу.",
   +                messageKey switch
   +                {
   +                    "err.ECR-DOC-0409.migrateProjectArchived" => "Проєкт архівовано: документи архівного проєкту не переносяться.",
   +                    "err.ECR-DOC-0409.migrateSheetsLocked" =>
   +                        $"Перенос зачепив би подані або затверджені аркуші: {dto.LockedSheetsAffected}. Спершу поверніть їх у роботу.",
   +                    _ => $"Перенос зняв би ручні значення замість формул на поданих або затверджених аркушах: значень "
   +                         + $"{dto.LockedOverridesLost}, аркушів {dto.LockedOverrideItems?.Count ?? 0}. Спершу поверніть їх у роботу.",
   +                },
                    new Dictionary<string, object?>
                    {
   -                    ["messageKey"] = dto.Refusals.Contains("projectArchived")
   -                        ? "err.ECR-DOC-0409.migrateProjectArchived"
   -                        : "err.ECR-DOC-0409.migrateSheetsLocked",
   +                    ["messageKey"] = messageKey,
   ```
   і перед `["refusals"] = dto.Refusals,` (`:1847`) цієї ж гілки:
   ```diff
   +                    // ✎ V-7: лише кількість і коди аркушів / періоди / статуси, без значень.
   +                    ["lockedOverrides"] = dto.LockedOverridesLost?.ToString(CultureInfo.InvariantCulture),
   +                    ["lockedOverrideSheets"] = (dto.LockedOverrideItems?.Count ?? 0).ToString(CultureInfo.InvariantCulture),
   +                    ["lockedOverrideItems"] = dto.LockedOverrideItems is { Count: > 0 } overrideItems
   +                        ? overrideItems.Select(i => string.Create(CultureInfo.InvariantCulture, $"{i.SheetCode}|{i.PeriodKey}|{i.Status}")).ToList()
   +                        : null,
                        ["refusals"] = dto.Refusals,
   ```
   Код помилки — той самий `409 ECR-DOC-0409` (`ErrorCodes.DocumentSubmitted`), що й для `sheetsLocked`: це конфлікт стану, а не неповна стратегія (`422 ECR-SCHM-0422`).
6. `src/Ecr.Infrastructure/Jobs/JobRetryPolicy.cs:129-133` — фонова задача не має повторювати детерміновану відмову (інакше 3 повтори 30+60+120 с зі статусом «виконується», див. коментар `:136-145`):
   ```diff
        private static readonly HashSet<string> MigrationRefusalKeys = new(StringComparer.Ordinal)
        {
            "err.ECR-DOC-0409.migrateSheetsLocked",
            "err.ECR-DOC-0409.migrateProjectArchived",
   +        "err.ECR-DOC-0409.migrateLockedOverrides",   // ✎ V-7
        };
   ```
7. Новий сід `src/Ecr.Infrastructure/Persistence/Sql/seed/features/DOC-migrate-locked-overrides.sql` (форма — як `COLL-lp32-locked-safe.sql`: спершу порція `#I18N` ru/kz, потім `#UiEn` en зі `Scope = 1`, кожна — окремий батч із `GO` і рядками-маркерами секції; ім'я відповідає шаблону `<AREA>-<slug>.sql`, сторож `SeedSplitLayoutTests`):
   ```sql
   -- DOC:migrate-locked-overrides ── ru/kz: V-7, ручні значення (D-461) на поданих/затверджених аркушах блокують перенос версії; власна порція ──
   INSERT INTO #I18N ([Key], Lang, Val)
   SELECT v.[Key], v.Lang, v.Val
     FROM (VALUES
       (N'err.ECR-DOC-0409.migrateLockedOverrides', N'ru', N'На поданных или утверждённых листах есть ручные значения вместо формул, которые целевая версия не разрешает: значений {lockedOverrides}, листов {lockedOverrideSheets}. Верните эти листы в работу, прежде чем переносить документы на другую версию шаблона.'),
       (N'err.ECR-DOC-0409.migrateLockedOverrides', N'kz', N'Тапсырылған немесе бекітілген парақтарда мақсатты нұсқа рұқсат етпейтін формулалардың орнына қолмен енгізілген мәндер бар: мәндер {lockedOverrides}, парақтар {lockedOverrideSheets}. Құжаттарды үлгінің басқа нұсқасына көшірмес бұрын бұл парақтарды жұмысқа қайтарыңыз.'),
       (N'documents.migrateRefusalLockedOverrides', N'ru', N'На поданных или утверждённых листах есть ручные значения, которые целевая версия не разрешает: сначала верните эти листы в работу.'),
       (N'documents.migrateRefusalLockedOverrides', N'kz', N'Тапсырылған немесе бекітілген парақтарда мақсатты нұсқа рұқсат етпейтін қолмен енгізілген мәндер бар: алдымен бұл парақтарды жұмысқа қайтарыңыз.'),
       (N'documents.migrateLockedOverrides', N'ru', N'Ручных значений вместо формул на поданных или утверждённых листах, которые заменила бы формула: {count}. Верните в работу листы:'),
       (N'documents.migrateLockedOverrides', N'kz', N'Тапсырылған немесе бекітілген парақтардағы формула ауыстыратын қолмен енгізілген мәндер: {count}. Мына парақтарды жұмысқа қайтарыңыз:')
          ) AS v ([Key], Lang, Val)
   OPTION (RECOMPILE);
   GO
   -- DOC:migrate-locked-overrides ── кінець секції ──
   -- DOC:migrate-locked-overrides ── V-7: ручні значення (D-461) на поданих/затверджених аркушах блокують перенос версії; ru/kz — порцією вище ──
   INSERT INTO #UiEn ([Key], Lang, Val, Scope)
   SELECT v.[Key], v.Lang, v.Val, v.Scope
     FROM (VALUES
       (N'err.ECR-DOC-0409.migrateLockedOverrides', N'en', N'Submitted or approved sheets hold manual values instead of formulas that the target version does not allow: {lockedOverrides} value(s) on {lockedOverrideSheets} sheet(s). Return these sheets to work before moving the documents to another template version.', 1),
       (N'documents.migrateRefusalLockedOverrides', N'en', N'Submitted or approved sheets hold manual values that the target version does not allow: return these sheets to work first.', 1),
       (N'documents.migrateLockedOverrides', N'en', N'Manual values instead of formulas on submitted or approved sheets that the formula would replace: {count}. Return these sheets to work:', 1)
          ) AS v ([Key], Lang, Val, Scope)
   OPTION (RECOMPILE);
   GO
   -- DOC:migrate-locked-overrides ── кінець секції ──
   ```
   Підстановки ru/kz збігаються з en (сторож `SeedTranslationTests.Підстановки_перекладу_збігаються_з_оригіналом`); пара «ключ × мова» нова (`git grep -n "migrateLockedOverrides\|migrateRefusalLockedOverrides" -- src` — порожньо), сторож `SeedSplitLayoutTests.Пара_ключ_мова_заведена_у_сіді_рівно_один_раз`.
8. UI `src/Ecr.Web/src/features/documents/VersionMigrationDialog.tsx`:
   - `refusalText` (`:37-38`), після `case 'sheetsLocked'`:
     ```diff
          case 'sheetsLocked':
            return t('documents.migrateRefusalSheetsLocked');
     +    // ✎ V-7: ручні значення (D-461) на поданих/затверджених аркушах, яких ціль не дозволяє.
     +    case 'lockedOverridesLost':
     +      return t('documents.migrateRefusalLockedOverrides');
     ```
   - у блоці відмови, одразу після закриття блоку `data-migrate-locked-affected` (`:545-561`, перед рядком `{report.blockedGrantCount !== null && …` на `:562`):
     ```tsx
                 {(report.lockedOverridesLost ?? 0) > 0 && (
                   <Stack gap="xs" data-migrate-locked-overrides="">
                     <Text size="sm">{t('documents.migrateLockedOverrides', { count: report.lockedOverridesLost ?? 0 })}</Text>
                     {(report.lockedOverrideItems ?? []).slice(0, ShownItems).map((item) => (
                       <Text size="xs" key={`o|${String(item.documentId)}|${item.sheetCode}|${String(item.periodKey)}`}>
                         {t('documents.migrateLockedItem', { sheet: item.sheetCode, period: item.periodKey, status: item.status })}
                       </Text>
                     ))}
                   </Stack>
                 )}
     ```
     Ключ рядка — літералом (сторож каталогу шукає літерали, коментар `:25-28`). Наявне попередження `lostFormulaOverrides` (`:519-525`) лишається: воно про всі втрачені позначки, нове — лише про замкнені.
   - Тип звіту — `components['schemas']['DocumentVersionMigrationDto']` (`versionMigrationApi.ts:5`), тож нові поля з'являються після кроку 9.
9. Контракт.
   - `docs/build/02-contracts.md:4049`: у перелік «якщо зачіпає (…)» дописати «; ✎ V-7 (рішення PO 11.11): ручне значення замість формули (D-461), якого формула цільової версії не дозволяє, на поданому/затвердженому аркуші — окрема відмова `lockedOverridesLost` → `409 ECR-DOC-0409` (`migrateLockedOverrides`), звіт `lockedOverridesLost` (кількість позначок) і `lockedOverrideItems` (код аркуша, період, статус; ≤ 200), без значень; після повернення аркуша в роботу перенос знімає позначку, як для решти D-461».
   - `docs/build/02-contracts.md` §7.1, після рядка `err.ECR-DOC-0409.migrateSheetsLocked` (`:4367`):
     ```
     | `err.ECR-DOC-0409.migrateLockedOverrides` | 409 | ✎ V-7: перенос версії зняв би ручні значення замість формул (D-461) на поданих/затверджених аркушах (`lockedOverrides`, `lockedOverrideSheets`, `lockedOverrideItems` — `SHEET\|період\|статус`); спершу повернути аркуші в роботу |
     ```
   - `contracts/openapi.snapshot.json` (`DocumentVersionMigrationDto`, `:7014…`): `ECR_UPDATE_SNAPSHOT=1 dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~OpenApiSnapshotTests"` (`OpenApiSnapshotTests.cs:36-42`); описи полів беруться з XML-документації кроку 1.
   - `src/Ecr.Web/src/api/schema.d.ts`: `cd src/Ecr.Web && npm run api:types` (`package.json:15`).
10. `docs/tz/10-decisions.md`: новий рядок після `D-474` (`:911`), номер — наступний вільний (нижче `D-V7`):
    ```
    | **D-V7** | **✎ 2026-11-11. `D-461` × `LP-32`: ручне значення, яке перенос версії зняв би, на поданому чи затвердженому аркуші блокує перенос.** Перенос відмовляє (`lockedOverridesLost`, `409 ECR-DOC-0409`, `migrateLockedOverrides`), поки на поданому/затвердженому аркуші лежить ручне значення замість формули, якого формула цільової версії не дозволяє; сухий прогін перелічує такі аркуші (`lockedOverrideItems`). Після «повернути в роботу» перенос знімає позначку за `D-461` (журнал `FormulaRestored`, причина «Version migration»). Позначка на комірці, яку в цілі не рахує жодна формула (аудит 31.10 F2), перенос не блокує: значення лишається введеним. | рішення PO 11.11 (аудит 2026-11, `V-7`, варіант А, через координатора) | `V-7` · `D-461` · `LP-32` |
    ```
    і рядок журналу змін унизу (за зразком `:1139`); `docs/CHECKSUMS.txt:11` — як у X-2 звіту 08: `git add docs/tz/10-decisions.md && git show :docs/tz/10-decisions.md | sha256sum`.

**Тест-доказ:**
- `tests/Ecr.Api.Tests/FormulaManualOverrideHttpFlowTests.cs`, новий `[Fact]` `Ручне_значення_на_затвердженому_аркуші_блокує_перенос_до_повернення_в_роботу` за зразком `Ручне_значення_замість_формули_наскрізно` (`:106-255`):
  1. `ArrangeAsync`; `admin` — як у `:109-110` плюс `"Document.Reopen"` (`ReopenDocumentHandler.Permission`, `ReopenDocumentHandler.cs:36`); `author` і `approver` — як `:112-113`.
  2. Формула `F = [A] * 2`, публікація, перемикач дозволу `true` (`:121-143`), входи `A` 1..5 (`:145-150`), ручне `R4 = 100` з причиною (`:172-174`).
  3. `author` подає (`:218-222`), `approver` затверджує: `POST /api/v1/documents/{id}/approve` з `{ sheetDefId, periodKey, approved = true }` (форма запиту — `ApprovalQueueApiTests.cs:327-331`, `ApproveSheetRequest` у `DocumentsController.cs:487-499`).
  4. Клон, у клоні дозвіл `false`, публікація (`:233-239`).
  5. Сухий прогін → 200, `canApply == false`, `refusals` містить `lockedOverridesLost` і НЕ містить `sheetsLocked`, `lockedOverridesLost == 1`, `lockedOverrideItems` — один елемент зі `status == "Approved"` і `affected == true`; `lostFormulaOverrides == 1`.
  6. Застосування (`dryRun = false`) → `409`, `code = ECR-DOC-0409`, `messageKey = err.ECR-DOC-0409.migrateLockedOverrides`, `lockedOverrides == "1"`; `MarksAsync(s) == 1` (`:524-529`), версія проєкту не змінилась.
  7. `admin` повертає аркуш: `POST /api/v1/documents/{id}/reopen` з `{ sheetDefId, periodKey, reason = "V-7" }` (`DocumentsController.cs:510-523`).
  8. Застосування знову → 200, `applied == true`; `MarksAsync(s) == 0`; останній рядок журналу комірки `R4` на колонці `F` ЦІЛЬОВОЇ версії (Id взяти з `db.ColumnDefs` за кодом `s.FCode` у таблиці клону) — `(FormulaRestored, "Version migration")` через `LastChangeAsync(s, s.Rows[3], columnId: <F цілі>)` (`:559-572`; `FormulaOverrideMigration.Reason`, `FormulaOverrideMigration.cs:23`).
  Без кроків 2-5 крок 5 тесту дає `canApply == true`, крок 6 — 200 і `MarksAsync == 0`, тож тест червоний. Якщо `approve` у кроці 3 відповідає 403 — дати `approver` той самий набір, що в `ApproverEditHttpFlowTests.cs:633` (там аркуш затверджується тим самим запитом).
- `tests/Ecr.Application.Tests/Documents/VersionMigrationLockedOverridesTests.cs` (новий, за зразком `FormulaOverrideMigrationTests.cs`): `MigrateDocumentVersionHandler.LockedOverrideTouches` — (а) позначка на парі зі статусом `Approved` → `Marks = 1`, один `Item`; (б) та сама позначка без пари у `lockedRefs` (аркуш у роботі) → `Marks = 0`; (в) дві позначки одного аркуша-періоду → `Marks = 2`, `Items.Count = 1`; (г) `lockedRefs == null` → `(0, [])`.
- `tests/Ecr.Infrastructure.Tests/Jobs/JobRetryPolicyTests.cs:107`: до масиву ключів додати `"err.ECR-DOC-0409.migrateLockedOverrides"`. Без кроку 6 червоніє `Assert.False(JobRetryPolicy.IsWorthRetrying(refusal))`.
- `src/Ecr.Web/src/features/documents/__tests__/VersionMigrationDialog.formulaOverride.test.tsx` (за зразком наявного `report()`, `:18-45`): звіт із `canApply: false`, `refusals: ['lockedOverridesLost']`, `lockedOverridesLost: 1`, `lockedOverrideItems: [{ documentId: 42, sheetCode: 'S1', periodKey: 202601, status: 'Approved', affected: true }]` → видно текст `documents.migrateRefusalLockedOverrides` і рядок `documents.migrateLockedItem`; плюс `expect(refusalText('lockedOverridesLost'))` не дорівнює `'lockedOverridesLost'`.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~FormulaManualOverrideHttpFlowTests|FullyQualifiedName~DocumentVersionMigrationTests.LockedSafe|FullyQualifiedName~DocumentVersionMigrationTests.Refusals|FullyQualifiedName~DocumentVersionMigrationTests.Background|FullyQualifiedName~OpenApiSnapshotTests"`; `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~FormulaOverrideMigrationTests|FullyQualifiedName~VersionMigrationLockedOverridesTests"`; `dotnet test tests/Ecr.Infrastructure.Tests --filter FullyQualifiedName~JobRetryPolicyTests`; `dotnet test tests/Ecr.Architecture.Tests` (сід, каталог, `CHECKSUMS`); `cd src/Ecr.Web && npx vitest run src/features/documents/__tests__/VersionMigrationDialog && npx tsc --noEmit && npm run lint`. Стенд: проєкт із затвердженим аркушем і ручним значенням → перенос на версію без дозволу → діалог показує причину й аркуш; «повернути в роботу» → перенос проходить, у журналі комірки «Formula restored».

**Ризики і побічні ефекти:**
- Нові відмови для проєктів із D-461 у поданих/затверджених періодах (саме те, що вирішив PO). Наявні тести не чекають `canApply == true` у такому стані: `Ручне_значення_замість_формули_наскрізно` перевіряє лише `lostFormulaOverrides` і пункти (`:241-248`), а `LockedSafe` позначок не має.
- Звіт отримує два nullable-поля (адитивно, як `lostFormulaOverrides`); старий клієнт їх ігнорує. Кількість запитів переносу не змінюється: усе рахується з уже прочитаних `lostOverrides` і `scope.LockedSheetRefs`.
- Позначки, які ціль знімає як «немає формули» (`FormulaOverrideMigration.Unformulated`, `FormulaOverrideMigration.cs:92-127`), перенос не блокують: значення лишається введеним (`IsCalculated = 0`), змінюється лише походження в журналі (`FormulaOverride` з причиною `Version migration: no formula`). Це поза рішенням PO («формула більше не дозволяє»); записано в `D-V7`.
- Копії аркуша зараховуються разом з оригіналом (немає `SheetCopyId` у `VersionMigrationLockedSheet`) — консервативно, як `ClassifyLocked`.

**Обґрунтування.** Окрема причина, а не позначка `Affected` у `lockedSheetItems`: `lockedSheetsAffected`/`sheetsLocked` за контрактом означають «план структури зачіпає аркуш» (`02-contracts.md:4049`), і змішування зламало б це значення та тексти `migrateSheetsLocked`. Варіант Б (лишити як є) PO відхилив. Варіант «не знімати позначки на замкнених аркушах» лишив би позначку на формулі, що її не дозволяє, після повернення в роботу (`ReleaseLostOverridesAsync` більше не запускається).

**Рішення PO:** ухвалено 11.11 — варіант А. Відкрите лише уточнення обсягу (подані + затверджені, як LP-32, чи лише затверджені) — див. примітку на початку; за замовчуванням специфікація йде за LP-32.

**Впевненість:** підтверджено читанням.

**Залежності:** після L4 звіту 02 (D-6 → D-7 → D-10 правлять `MigrateDocumentVersionHandler.cs`), далі V-7 → V-5 (той самий файл). `02-contracts.md`, сід, `openapi.snapshot.json`, `schema.d.ts`, `10-decisions.md` + `CHECKSUMS.txt` — у тому ж PR.

---

## Порядок впровадження

| Лінія | Знахідки (порядок) | Спільні файли | Модель |
|---|---|---|---|
| L9-a Планувальник | V-1 → V-6 | `VersionMigrationPlanner.cs` | sonnet |
| L9-b Обробник переносу | **після L4 (D-6 → D-7 → D-10)**: V-7 (✎ рішення PO 11.11 — варіант А, можна робити) → V-5 | `MigrateDocumentVersionHandler.cs`; V-7 ще `JobRetryPolicy.cs`, `VersionMigrationDialog.tsx`, сід `DOC-migrate-locked-overrides.sql`, `02-contracts.md`, `openapi.snapshot.json`, `schema.d.ts`, `10-decisions.md` + `CHECKSUMS.txt` | opus (V-7), sonnet (V-5) |
| L9-c Зрізи подання | V-2 → V-3 | `SubmissionSnapshotTranslation.cs` (новий), `DocumentVersionHandlers.cs`, `ApprovalSnapshotRefresh.cs`, `IWorkflowStore.cs`/`WorkflowStore.cs` | opus |
| L9-d Журнал | V-4 | `IColumnPathMapper.cs`, `ColumnPathMapper.cs`, `PrefetchedColumnMapper.cs`, `IAuditReader.cs`, `AuditReader.cs`, `GetCellChangesHandler.cs` | opus |

- L9-a, L9-c і L9-d можна робити паралельно, бо спільних файлів у них немає.
- L9-c змінює `WorkflowStore.cs`, який також правлять лінії C-6/C-8 (L9 Workflow у `00-PLAN.md`). Лінії йдуть послідовно, хоч правки й в іншому методі.
- L9-d (`GetCellChangesHandler.cs`) не перетинається з L1/L19 (`DocumentReadScope.cs` не змінюється).
- Міграцій БД немає ні в одній знахідці. ✎ V-7 (після рішення PO 11.11) — єдина зі зміною OpenAPI: два адитивні nullable-поля звіту (`lockedOverridesLost`, `lockedOverrideItems`), новий ключ помилки й сід; решта знахідок — лише текст `02-contracts.md`.
- `docs/tz/10-decisions.md` + `CHECKSUMS.txt` правлять також інші лінії (звіт 08 X-2, звіт 07 R-1/R-3): робити послідовно або перераховувати суму при злитті.

## Відкинуто

| Кандидат | Чому відкинуто |
|---|---|
| Маршрут погодження, прив'язаний до версії шаблону (`ApprovalRoute.TemplateVersionId`), не клонується й після переносу перестає діяти | Продукт створює лише маршрути проєкту з `TemplateVersionId = null` (`ApprovalRouteHandlers.cs:198-201`); маршрут версії можливий тільки прямим SQL. Невідомий крок маршруту починає маршрут спочатку (`ApprovalRoute.cs:154-162`) |
| Видалення навіть **порожньої** колонки чи рядка на аркуші з поданим місяцем блокує перенос (`sheetsLocked`) | Свідомо: контракт LP-32 «втрата/видалення» (`02-contracts.md:4046-4051`), `VersionMigrationPlanner.cs:491-497, 526-529`. Висвітлено у «Відповідях PO» як практичне обмеження |
| Мапа Lookup для колонок таблиць відсутня | Свідоме обмеження v1 (`VersionMigrationLookupRemap.cs:7-8`): зміна довідника колонки дає `guardedWithData` |
| Глобальні мапінги збору й вікон: після першого переносу інші проєкти на старій версії не збираються | Свідомо D-1b P3: звіт показує кількість, перенос не блокує (`MigrateDocumentVersionHandler.cs:1522-1525`, `48-53`) |
| Зняття забороняючого гранта заради переносу послаблює доступ іншим проєктам тієї самої версії (гранти без `ProjectId`) | Відомо: AN-20 / ent7 P2-2 (`WORK-QUEUE.md:46`), «потрібне рішення Аудиту» |
| Перенос неатомарний: комірки комітяться діапазонами, відкат лише бекапом | Свідомо, рішення координатора 21.10 (`DocumentVersionMigrationStore.cs:1449-1456`, `admin-guide.md:288-312`); бекап обов'язковий від 500 000 значень |
| Формули поданих і затверджених аркушів після переносу не перераховуються | Контракт LP-32 (`02-contracts.md:4051`) |
| Нові формульні колонки закритих періодів лишаються порожніми | Свідомо (`MigrationRecalculation.cs:14-17`) |
| Документи на чернетці шаблону | Свідомо ФВ-7.4: структурні правки чернетки з документами класифікуються через `HasDocumentsAsync` (`ITemplateVersionStore.cs:57-65`) |
| Режим «з місяця» перемаплює склад документа (`WorkflowSql` `UPDATE s`) для всіх періодів | Покрито тестом `DocumentVersionMigrationTests.FromMonth.cs:476` (старий період подається й експортується на старій версії); прапор типово вимкнено |
| Правки, відхилені `409 structureChanging` під час переносу, утримуються без автоповтору | Повтор після переносу проходить (екземпляри й рядки зберігають Id, `EnsureUnchanged` діє в межах одного запиту, `DocumentStructure.cs:32`); текст `err.ECR-DOC-4091.structureChanging` каже повторити (`COLL-an36.sql:6-22`). Не дефект даних |
| Застряглий після збою проєкт неможливо довести з UI (документ не відкривається — 409) | Є «Повторити» для проваленої задачі (`RestartJobHandler`, `IntegrationHandlers.cs:536-640`, `JobsController.cs:105-111`); runbook — `admin-guide.md:296-300`. Детермінований збій D-6 — окрема відома знахідка |
| Рядки з дозволу `P<id>` губляться при переносі (PM-10c) | Характеризаційний тест показує, що рядки й походження переживають перенос (`DocumentVersionMigrationTests.PermitRows.cs:17-25`); запис `WORK-QUEUE.md:364` застарів |
| Читачі SSRS бачать напівперенесений стан під час важкої частини | Відомий прийнятий ризик (`DocumentVersionMigrationStore.cs:1459-1460`) |
| Архівний проєкт / архівні роки | Перенос архівного чи архівованого проєкту — відмова `projectArchived` (`MigrateDocumentVersionHandler.cs:559-562, 800, 1442-1445`); архівуються лише періоди архівних проєктів (`03-archive-proc.sql:102-127`), тож розходження «архів на старих Id» не виникає |

## Перевірено й у порядку

- **Право:** `Template.Edit` і грант `Manage` на проєкт, також для сухого прогону й переліку цілей (`MigrateDocumentVersionHandler.cs:320-338, 788-803, 1242-1251`). Невидимий документ дає 404.
- **Ціль:** лише опублікована версія того самого шаблону; та сама версія — 422 (`:1749-1796`). Задача перевіряє все повторно на старті (`:750-759`, ACC «право відкликане до старту»).
- **Сухий прогін** нічого не пише (`:451-457`). План перераховується під блоком рядка проєкту й винятковими замками структури всіх документів (`:885-934`, L6-02).
- **T1 / важка частина / T2.** Маркер ставиться останнім у T1 разом із подією `DocumentVersionMigrationStarted` (`:979-998`). Важкі діапазони йдуть на власному з'єднанні (`DocumentVersionMigrationStore.cs:620-706`). T2 повторно бере блок і перевіряє версію (`MigrateDocumentVersionHandler.cs:1010-1023`). Перемикання версії й зняття маркера виконуються однією командою (`DocumentVersionMigrationStore.cs:1938-1953`).
- **Відновлення:** відбиток плану (`:257-288, 331-353`), оренда виконавця (`:629-640, 780-826`), watermark (`:656-667, 1678-1687`), ідемпотентні предикати, звірка діапазону (`:1654-1677`) і постумова «жодної комірки на чужій колонці» (`:713-742`). Повтор від того самого автора повертає ту саму задачу, від іншого — 409 (`MigrateDocumentVersionHandler.cs:564-640`).
- **Блокування користувачів:** фільтр маркера охоплює маршрути документа й проєкту, query й тіло (`src/Ecr.Api/Startup/MigrationMarkerGuard.cs`, `MigrationMarkerFilter`). Писарі бачать маркер у тому самому запиті, що й версію (`SheetEditGate.cs:296-301`). Інтеграція пише через `PatchCellsHandler` (`IntegrationCellPatcher.cs:20-55`). Затримка поширення маркера — 3 с при оновленні кешу кожні 2 с (`DocumentVersionMigrationStore.cs:227-229, 642-646`).
- **Значення:** переносяться без перетворення. Тип, одиниця, довідник, обов'язковість, «лише читання» рядка, режим рядків і розкладка дають Guarded (`VersionMigrationPlanner.cs:531-613, 648-660`). Розширення `Precision`/`Scale` — `Safe` (`:881-935`). Нова комірочна формула на значенні — Guarded/ACC-OW (`:670-747`). Втрата рахується по комірках (`:373-393`). Значення на невідомих колонках рахуються як «втрачені», а не мовчки видаляються (`:379-385`).
- **Оформлення комірок і позначки D-461** йдуть за колонкою (`DocumentVersionMigrationStore.cs:1538-1586`). Позначки, яких ціль не дозволяє, знімаються з журналом (`MigrateDocumentVersionHandler.cs:1142-1191`). Колонка без формули після 31.10 лишає значення введеним (`:1155-1174`).
- **Рядки:** фіксовані мапляться за ключем, зниклі знімаються. Нові рядки фіксованих таблиць вставляються в кожен екземпляр, зокрема в копії аркушів (`DocumentVersionMigrationStore.cs:1588-1609, 1775-1792`). Динамічні лишаються як є.
- **Шапка:** зниклі поля видаляються, решта мапиться. Мапа Lookup зі звіркою кількості (P2-2) і згодою для закритих документів (`DocumentVersionMigrationStore.cs:577-605, 1794-1839`; `MigrateDocumentVersionHandler.cs:1424-1440`).
- **Індекс пошуку** (`DocumentVersionMigrationStore.cs:1707-1745`) переноситься. **Результати перевірки** видаляються лише в T2 (`:744-745, 1747-1757`). Перелік документів показує «не перевірено» (`DocumentStore.cs:1559-1592`). Подання перевіряє наново (`SubmitSheetHandler.cs:33`).
- **Погодження:** стани й журнал перемапляються (`DocumentVersionMigrationStore.cs:1842-1861`). Подані й затверджені аркуші, яких план не зачіпає, переносяться, решта — відмова з переліком (`MigrateDocumentVersionHandler.cs:1412-1422, 1705-1747, 1818-1849`).
- **Права:** гранти аркуша, таблиці й колонки копіюються на нові Id за кодом; заборона перекриває дозвіл (`DocumentVersionMigrationStore.cs:1879-1906`). Грант на ресурсі без відповідника — відмова `grantsNotMapped` (`MigrateDocumentVersionHandler.cs:1447-1462`). Профілі скидаються (`:1116-1120`, `GrantProfileInvalidation.cs`).
- **Методології:** активна прив'язка мусить мати відповідник у цілі, інакше `bindingsNotMapped` (`MigrateDocumentVersionHandler.cs:1464-1473`, `DocumentVersionMigrationStore.cs:1014-1050`). Ключі правил на втрачені колонки дають відмову, на перекладні — переклад (`:1280-1368`).
- **PI/EF:** мапінги збору, подій і вікон переносяться в T2. Активний мапінг без відповідника, з іншим режимом рядків чи без рядка-адресата — відмова (`MigrateDocumentVersionHandler.cs:1052-1067, 1475-1507`; `DocumentVersionMigrationStore.cs:1082-1278`). Провенанс вікон (`RowWindowValue`) теж переноситься.
- **Перерахунок після переносу:** один на проєкт, після коміту, лише при змінах формул; збій постановки записується в журнал, а не дає 500 (`MigrateDocumentVersionHandler.cs:1125-1133`, `MigrationRecalculation.cs`). Закриті й подані періоди перерахунок пропускає.
- **Публікація:** одна транзакція з блоком версії (`PublishTemplateVersionHandler.cs:76-91`), усі перевірки одразу, граф залежностей фіксується (`:262-270`). Структура опублікованої версії заморожена (`TemplateVersion.cs:215-245`, ФВ-7.1). Вʼюхи генеруються «найкращим зусиллям» (`:296-317`).
- **Виведення з обігу:** проєкти на версії працюють далі (`TemplateVersion.cs:140-179`, ФВ-7.8). Ціллю переносу виведена з обігу версія бути не може (`MigrateDocumentVersionHandler.cs:1780-1793`). **Відкинути** можна лише чернетку без використання (`TemplateVersion.cs:181-195`, `DiscardTemplateVersionHandler.cs:60-67`).
- **Клон** копіює поза графом стилі, гранти, правила доступу до періоду, прив'язки методологій (з перекладом предиката), умовне форматування, правила груп аркушів (`TemplateVersionStore.cs:748-768`) і прив'язки дозволів (`:387`). Дані документів не копіюються (`TemplateVersionCloner.cs:22-24`).
- **SSRS:** стабільні вʼюхи ведуть ряд через версії: `_TemplateVersion` береться з таблиці рядка, колонки не видаляються (`05-rpt-views.sql:832-845`). Обмеження див. у V-5.
- **Експорт і показ старого періоду після переносу** (увесь проєкт) ідуть у макеті нової версії, бо дані перемаплені. Старий макет зберігається в зрізах подання з `TemplateVersionId` (`SubmissionSnapshot.cs:66, 92`).

## Самоперевірка

| ID | `file:line` відкрито повторно | «Було» дослівно | Компілюється за змістом | Тест падає без фіксу | Не дублює відоме | Результат |
|---|---|---|---|---|---|---|
| V-1 | `VersionMigrationPlanner.cs:685-687` (фільтр `Scope == Cell`), `:700-740` (цикл), `:457-465` (`FormulaChanges`); `RecalculationService.cs:1357-1374, 1515-1526, 1695-1715`; `FormulaDefHandlers.cs:274-297` — так | Фільтр і перші рядки циклу скопійовано з Read `:685-687, 700-707` | `FormulaTargetTypes.Accepts(CellDataType?, ExpressionValueType)` існує (`src/Ecr.Application/Recalculation/FormulaTargetTypes.cs`); `ColumnDef.IsComputed`, `FormulaDef.AssignRow` існують; повні імена просторів — без конфлікту імен | Так: без фіксу формула `Row` не дає пункту `CellFormula` і `GuardedValues = 0`; тест-хелпер має `AssignRow` | WW-02/ACC-OW покривають лише `Cell` (`02-contracts.md:4059`); у `04-correctness-domain.md` C-4 — інша тема (імпорт); KNOWN-ISSUES / WORK-QUEUE — нічого | Залишено |
| V-2 | `DocumentVersionHandlers.cs:80-86, 137-172, 208-230, 349-366`; `DocumentReadScope.cs:222-234, 335-336`; `DocumentVersionStore.cs:12-34`; `IDocumentVersionStore.cs:36-40` — так | `DocumentVersionRecord`/`DocumentVersionPayload`, `Select(...)`, рядки `newer.PayloadJson`/`older.PayloadJson` — дослівно | `DocumentPeriodVersion.GetAsync(IDocumentStore, long, int, CancellationToken)` існує (`DocumentPeriodVersion.cs:16`); `SubmissionPayloadCell` — `record` (`with`); `TemplateVersionSnapshot.Sheets`/`Code` (`string`) існують; обробник уже має `documents` і `metadata` | Так: (а) 0 змін через `CanReadColumn(old) = false`, (б) N хибних змін | Перенос свідомо не чіпає зрізів (`Store:1439-1440`), читачі в аудитах 10-09* і 11-10 не розглядалися | Залишено; рольові межі аркуша позначено «стенд» |
| V-3 | `ApprovalSnapshotRefresh.cs:108-123, 159-167`; `WorkflowStore.cs:416-433`; `ApproveSheetHandler.cs:219-223`; `Store:1849-1861` — так | Блок `:116-123` — дослівно | `GetSnapshotPayloadAsync`, `Filter`, `SequenceEqual` — члени того самого класу; `SheetDef.Code` — `string` (`SheetDef.cs:23`); новий метод інтерфейсу — інструкція звірити двійники | Так: без фіксу знімається другий зріз (лічильник 2) | D-442 описує лише правку погоджувача; не відоме | Залишено (Low) |
| V-4 | `AuditReader.cs:424-427, 444-459`; `GetCellChangesHandler.cs:104-124, 146-155, 9-10`; `IAuditReader.cs:170-195`; `DocumentReadScope.cs:335-336`; `ColumnPathMapper.cs`, `PrefetchedColumnMapper.cs` — так | Блок `if (filter.ColumnDefId …) And(…)` і рядки обробника — дослівно | `db.ColumnRenames` (`EcrDbContext.cs:95`), поля `FromColumnDefId`/`ToColumnDefId` існують; `IColumnPathMapper` має лише двох реалізаторів (`grep ": IColumnPathMapper"`) | Так: без фіксу 0 рядків історії до переносу | Журнал свідомо незмінний, але обрив читання не описано ні в KNOWN-ISSUES, ні в аудитах; D-457 (Q-17) — інша вісь (період) | Залишено; суміжні читачі винесено в «Ризики» |
| V-5 | `Store:1908-1912`; `Handler:1116-1133, 1351-1357, 235`; `05-rpt-views.sql:111-160, 969-1015`; `StartupSequence.cs:120-141`; `PublishTemplateVersionHandler.cs:300-317` — так | Рядок `timeline?.Invalidate(projectId);` — дослівно | `IReportViewGenerator.GenerateAsync(int?, CancellationToken)` (`IReportViewGenerator.cs:21`); шаблон `LoggerMessage` + `catch … when` — як `MigrationRecalculation.cs`; `planned`/`target` видимі після T2 | Так: без генерації стабільна вʼюха має старий клас аліасів (текст вшитий) | Тест `FromMonth` генерує вʼюхи вручну; не відоме | Залишено (Low) |
| V-6 | `VersionMigrationPlanner.cs:600-618, 937-946`; `MonthColumnRouter.cs:235-254`; `appsettings.json:150` — так | Два рядки `Guarded(…UnitId…)` / `Guarded(…LookupRegistryDefId…)` — дослівно | `ColumnDef.IsMonthColumn`/`MonthNumber`/`SetMonth(bool, byte?)` існують (`ColumnDef.cs:54-55, 239`) | Так: без фіксу пункту `MonthPlacement` немає | Не відоме; PS-P1C стосується сеттера, не переносу | Залишено (Low) |
| V-7 (✎ переписано після рішення PO 11.11) | `Handler:109-113` (`<param Warnings>`, запис), `:153` (хвіст запису), `:309` (`MaxLockedItems`), `:452-456`, `:929-934` (одна `PlanAsync` для сухого прогону й застосування), `:1396-1407`, `:1411-1422`, `:1559-1568`, `:1709-1747` (`ClassifyLocked`, `codes` на `:1724`), `:1749`, `:1818-1849`, `:1032-1038`, `:1142-1191`; `JobRetryPolicy.cs:128-133, 146`; `DocumentVersionMigrationStore.cs:161-179`; `IDocumentVersionMigrationStore.cs:11-23`; `FormulaOverrideStore.cs:235-256`; `VersionMigrationDialog.tsx:29-52, 519-525, 538-567`; `02-contracts.md:4046-4051, 4366-4367`; `FormulaManualOverrideHttpFlowTests.cs:106-255, 524-529, 559-572, 576-598`; `JobRetryPolicyTests.cs:107` — так | Рядки `var locked = ClassifyLocked(scope, plan, from);` + `sheetsLocked`, хвіст `Warnings: range.Warnings);`, гілка 409 з `messageKey`, `MigrationRefusalKeys`, `case 'sheetsLocked'` — дослівно | `ProjectFormulaOverride` (`IFormulaOverrideStore.cs:102`), `CellAddress` (`CellAddress.cs:11`), `VersionMigrationLockedSheet` (`IDocumentVersionMigrationStore.cs:23`), `ColumnsById`/`TableDefId` (як `FormulaOverrideMigration.cs:55-57`), `LockedSheetItem` (`Handler:170`), `ReopenDocumentHandler.Permission = "Document.Reopen"` (`ReopenDocumentHandler.cs:36`), `ApproveSheetRequest`/`ReopenDocumentRequest` (`DocumentsController.cs:846, 854`), `InternalsVisibleTo` (`Ecr.Application.csproj:22`); ключі сіду нові (`git grep` порожньо) | Так: без фіксу сухий прогін `canApply = true`, застосування 200 і позначка знята; без кроку 6 — `IsWorthRetrying = true` | D-461 і LP-32 — окремі рішення, перетин не описано; аудит 31.10 F2 — про колонку без формули (поза V-7, винесено в «Ризики»); KNOWN-ISSUES/WORK-QUEUE — нічого | Залишено; обсяг «подані + затверджені» за LP-32 позначено для PO |

**Вилучено під час самоперевірки:**
- «Маршрут версії не клонується» (лише SQL, перенесено у «Відкинуто»).
- «Застряглий проєкт без UI» (є «Повторити» задачі).
- «Правки під час переносу губляться» (повтор проходить).
- «Рядки `P<id>` губляться» (спростовано тестом PM-10c).
- «Архівні роки на старих Id» (перенос архівного проєкту заборонено).
