# 03 — Продуктивність серверної частини (аудит 2026-11-10)

- **Область:** `src/Ecr.Api`, `src/Ecr.Application`, `src/Ecr.Infrastructure`, `src/Ecr.Calculations`, `src/Ecr.Expressions`, `src/Ecr.Worker`, `src/Ecr.Adapters.*`.
- **SHA `main`:** `f446e9b29` (Merge PR #811). **Дата:** 2026-11-11. Лише читання коду; заміри й бенчмарки не виконувались.
- **Охоплено повністю:** перерахунок (план, контекст обчислення, резолвер посилань), валідація («Перевірити»/подання/коміркові правила PATCH), публікація шаблону (перевірки, топологічний порядок), межі читання (`DocumentReadScope`, знімок-обʼєднання RPT-STABLE), кандидати згадок чату, умовні формати зрізу.
- **Поверхово:** перелік документів, журнали аудиту, черга погоджень, сповіщення про подання, матеріалізація PI, перенос версії, експорт Excel/CSV, кеші метаданих/профілів, фонові цикли (`PeriodicTimer`, `Task.Delay`), N+1-сканування (`foreach … await store.*Async`) по всьому Application/Infrastructure.
- **Не охоплено:** SQL-плани в живій базі (немає доступу), `Ecr.Calculations` (методологічний рушій) — лише побіжно, клієнт.
- **Уникнення дублів:** перевірено `docs/build/KNOWN-ISSUES.md`, `OPEN-ITEMS.md`, `TODO-REMAINING.md`, `WORK-QUEUE.md` (AN-13, AN-155a…f, AN-156), `docs/build/perf/*`, `docs/tz/10-decisions.md`, і диференції черг `origin/lane/analiz/perf-cellread` (`01fa135d4`, `NormalizedCellStore`), `perf-patch` (`096e94d0a`, методології в PATCH), `perf-rpt` (`727e907b4`, `05-rpt-views.sql`), `perf-slice` (`dd6db952d`, `SlicePermissionMaps`/`AccessDecisionService`/`GetTableSliceHandler`). Жодна знахідка нижче не перетинається зі змістом цих гілок; файлові перетини названо в «Залежностях».
- Частковий файл попереднього аудитора (main `ac0a1ed2d`) перевірено заново: підтверджено й увійшли P-1, P-2, P-3, P-4 (частково), P-7; «індекс `doc.TableInstance`» і «перелік дозволів» — див. «Відкинуто».

## Зведена таблиця

| ID | Важливість | Категорія | Назва | Зусилля | Ризик зміни | Рішення PO |
|---|---|---|---|---|---|---|
| P-1 | Висока | performance | `ReferenceResolver` будує перелік УСІХ таблиць знімка на кожне читання посилання в перерахунку | S | середній | ні |
| P-2 | Висока | performance | План перерахунку: ребро на КОЖЕН рядок для залежності від колонки — дубль колонкового індексу, на кожну правку | S | низький | ні |
| P-3 | Висока | performance | «Перевірити»/подання: словник усіх комірок таблиці будується заново на КОЖЕН рядок (O(рядки × комірки)) | S | низький | ні |
| P-4 | Висока (потребує заміру) | performance | Публікація шаблону: три квадратичні кроки (виробники формул, топологічне сортування, запис порядку) під блоком версії | M | середній | ні |
| P-5 | Середня | performance | `ValidationEngine` розбирає вираз правила на кожен рядок/комірку | S | низький | ні |
| P-6 | Середня | performance | `DocumentReadScope` перебудовує 7 структурних словників на кожне створення (і на `InPeriod`); знімок-обʼєднання багатоверсійного проєкту — на кожне звернення | M | середній | ні |
| P-7 | Середня | performance | Кандидати згадок чату: до 200 користувачів × (профіль + видимість + межі) на одне натискання | S | низький | так |
| P-8 | Низька | performance | Зріз: умовний формат за текстом Lookup-колонки читає ВЕСЬ довідник (до 50 000 записів) на кожен GET | S | низький | ні |

---

## Знахідки

### P-1 — Висока — `ReferenceResolver` будує перелік УСІХ таблиць знімка на кожне читання посилання

**Категорія:** performance

**Де:**
- `src/Ecr.Expressions/Binding/ReferenceResolver.cs:135-183` (`FindTable`), `:185-210` (`ResolveColumn`).
- Гарячий виклик: `src/Ecr.Application/Recalculation/SliceEvaluationContext.cs:323-329` (`Read` → `_resolver.Resolve(...)` на КОЖЕН `CellReferenceNode` під час обчислення), резолвер створюється раз на контекст — `:134`.
- Те саме на публікації (P-4) і в експорті: `src/Ecr.Application/Templates/PublishChecks.cs:752`, `src/Ecr.Infrastructure/Expressions/FormulaEngine.cs:81`, `src/Ecr.Adapters.Excel/ExcelExporter.cs:1182`, `src/Ecr.Application/Validation/ValidationEngine.cs:171-173`.

**Проблема:** кожен `Resolve` викликає `FindTable`, який матеріалізує список усіх пар (аркуш, таблиця) знімка й шукає в ньому лінійно (двічі, плюс проміжні списки кандидатів); потім `ResolveColumn` лінійно шукає колонку з `OrdinalIgnoreCase`:
```csharp
// ReferenceResolver.cs:137
var tables = snapshot.Sheets.SelectMany(s => s.Tables.Select(t => (Sheet: s, Table: t))).ToList();
...
// :201
var column = table.Columns.FirstOrDefault(
    c => !c.IsDeleted && string.Equals(c.Code, node.ColumnSelector, StringComparison.OrdinalIgnoreCase));
```
Знімок незмінний протягом життя резолвера, тож ця робота однакова для кожного виклику.

**Сценарій відтворення:** річний документ Land (~90 таблиць, за коментарем `RecalculationPlanBuilder.cs:51` шаблон ~30 000 формул). Повний перерахунок або `FormulaRecalculationJob` після правки: кожна формула × кожне посилання → `Read` → список із ~90 кортежів + 1–3 проміжні списки + лінійний пошук колонки. Для предикатних посилань (`SliceEvaluationContext.EvaluatePredicate`) умова читає `[Current]`-колонки КОЖНОГО рядка — тобто рядки × посилання умови × таблиці. Фактично: O(посилань × таблиць) алокацій і порівнянь рядків; очікувано: O(1) на посилання після одноразової побудови індексу.

**Вплив:** CPU і GC (Gen0, сотні МБ алокацій на повний перерахунок) у воркері на КОЖНУ правку комірки (через `FormulaRecalculationJob`) і на нічний/ручний перерахунок; на публікації — множиться на кількість посилань усіх формул. Масштаб: ~90 таблиць × сотні тисяч читань → десятки мільйонів зайвих порівнянь на прогін. Точний час — потребує заміру на стенді (dotnet-counters `alloc-rate` під час перерахунку Land).

**Зміна — покроково:**
1. Файл `src/Ecr.Expressions/Binding/ReferenceResolver.cs` — лінивий індекс знімка в екземплярі резолвера (резолвер живе один прохід/контекст і не ділиться між потоками — див. «Ризики»).

   Додати в клас (після оголошення класу, перед `Resolve`):
   ```csharp
   /// <summary>Індекс таблиць і колонок знімка — будується один раз на резолвер (P-1, аудит 2026-11-10).</summary>
   private TableIndex? _index;

   private TableIndex Index => _index ??= TableIndex.Build(snapshot);

   private sealed class TableIndex
   {
       public Dictionary<int, (SheetDef Sheet, TableDef Table)> ById { get; } = [];

       public Dictionary<string, List<(SheetDef Sheet, TableDef Table)>> ByCode { get; } =
           new(StringComparer.OrdinalIgnoreCase);

       private readonly Dictionary<TableDef, Dictionary<string, ColumnDef>> _columns =
           new(ReferenceEqualityComparer.Instance);

       public static TableIndex Build(TemplateVersionSnapshot snapshot)
       {
           var index = new TableIndex();
           foreach (var sheet in snapshot.Sheets)
           {
               foreach (var table in sheet.Tables)
               {
                   // ⚠ TryAdd/Add у порядку знімка = семантика колишнього `FirstOrDefault` по списку.
                   index.ById.TryAdd(table.Id, (sheet, table));
                   if (!index.ByCode.TryGetValue(table.Code, out var list))
                   {
                       index.ByCode[table.Code] = list = [];
                   }

                   list.Add((sheet, table));
               }
           }

           return index;
       }

       /// <summary>Перша НЕвидалена колонка з кодом (без урахування регістру) — як колишній `FirstOrDefault`.</summary>
       public ColumnDef? Column(TableDef table, string code)
       {
           if (!_columns.TryGetValue(table, out var byCode))
           {
               byCode = new Dictionary<string, ColumnDef>(StringComparer.OrdinalIgnoreCase);
               foreach (var column in table.Columns)
               {
                   if (!column.IsDeleted)
                   {
                       byCode.TryAdd(column.Code, column);
                   }
               }

               _columns[table] = byCode;
           }

           return byCode.GetValueOrDefault(code);
       }
   }
   ```
   `FindTable` (рядки 135-183) — було:
   ```csharp
   private TableDef? FindTable(CellReferenceNode node, int currentTableDefId, List<ExpressionDiagnostic>? diagnostics)
   {
       var tables = snapshot.Sheets.SelectMany(s => s.Tables.Select(t => (Sheet: s, Table: t))).ToList();

       if (node.TableCode is null)
       {
           var own = tables.FirstOrDefault(x => x.Table.Id == currentTableDefId).Table;
   ```
   стало:
   ```csharp
   private TableDef? FindTable(CellReferenceNode node, int currentTableDefId, List<ExpressionDiagnostic>? diagnostics)
   {
       var index = Index;
       index.ById.TryGetValue(currentTableDefId, out var ownPair);

       if (node.TableCode is null)
       {
           var own = ownPair.Table;
   ```
   (решта гілки `node.TableCode is null` — без змін). Блок рядків 153-182 — було:
   ```csharp
       var candidates = tables
           .Where(x => string.Equals(x.Table.Code, node.TableCode, StringComparison.OrdinalIgnoreCase))
           .ToList();

       if (node.SheetCode is { } sheetCode)
       {
           candidates = candidates
               .Where(x => string.Equals(x.Sheet.Code, sheetCode, StringComparison.OrdinalIgnoreCase))
               .ToList();
       }
       else
       {
           // Без коду аркуша шукаємо в ТОМУ САМОМУ аркуші, де живе формула:
           // [Main].[…] означає «таблиця Main тут», а не «будь-яка Main».
           var ownSheet = tables.FirstOrDefault(x => x.Table.Id == currentTableDefId).Sheet;
           if (ownSheet is not null)
           {
               candidates = candidates.Where(x => x.Sheet.Id == ownSheet.Id).ToList();
           }
       }

       if (candidates.Count == 0)
       {
           Report(diagnostics, node, ... "expr.ref.unknownTable" ...);
           return null;
       }

       return candidates[0].Table;
   ```
   стало:
   ```csharp
       TableDef? found = null;
       if (index.ByCode.TryGetValue(node.TableCode, out var byCode))
       {
           foreach (var (sheet, table) in byCode)
           {
               // Без коду аркуша шукаємо в ТОМУ САМОМУ аркуші, де живе формула:
               // [Main].[…] означає «таблиця Main тут», а не «будь-яка Main».
               var matches = node.SheetCode is { } sheetCode
                   ? string.Equals(sheet.Code, sheetCode, StringComparison.OrdinalIgnoreCase)
                   : ownPair.Sheet is null || sheet.Id == ownPair.Sheet.Id;
               if (matches)
               {
                   found = table;
                   break;
               }
           }
       }

       if (found is null)
       {
           Report(diagnostics, node, ... "expr.ref.unknownTable" ... /* той самий виклик, без змін */);
           return null;
       }

       return found;
   ```
   `ResolveColumn` (рядки 185-202): прибрати `static` і замінити пошук — було:
   ```csharp
   private static int? ResolveColumn(
   ...
       var column = table.Columns.FirstOrDefault(
           c => !c.IsDeleted && string.Equals(c.Code, node.ColumnSelector, StringComparison.OrdinalIgnoreCase));
   ```
   стало:
   ```csharp
   private int? ResolveColumn(
   ...
       var column = Index.Column(table, node.ColumnSelector);
   ```
   (`SheetDef`, `TableDef`, `ColumnDef` — `Ecr.Domain.Entities.Configuration`, уже в `using`.)
2. Тест — новий файл `tests/Ecr.Expressions.Tests/Binding/ReferenceResolverIndexTests.cs` (див. «Тест-доказ»).

**Тест-доказ:** `Ecr.Expressions.Tests` / `Binding/ReferenceResolverIndexTests.cs` / клас `ReferenceResolverIndexTests`:
- `Розвязання_посилання_не_алокує_перелік_усіх_таблиць`: `TemplateBuilder` → аркуш `S`, 300 таблиць `T0..T299` (`builder.Table(sheet, $"T{t}", TableRowMode.Fixed, TableLayoutKind.PerPeriodInstance)`), у кожній 30 колонок `C0..C29`; `var resolver = new ReferenceResolver(builder.Build())`; вузол `new CellReferenceNode(null, "T299", new RowSelector.Current(), "C29", 0)`; один прогрівний `Resolve(node, target.Id, "R1")`; далі `GC.GetAllocatedBytesForCurrentThread()` до/після 10 000 викликів. Твердження: `allocated < 2_000_000` і результат `ColumnDefId == C29.Id`. Без фіксу кожен виклик алокує список із 300 кортежів + список кандидатів (~10 КБ) → ~100 МБ — тест падає; з фіксом лише `ResolvedReference` (~0,5 МБ).
- `Індекс_дає_ті_самі_відповіді_що_й_перебір` (диференційний): знімок із двома аркушами, однойменними таблицями в обох аркушах, таблицею з кодом у різному регістрі, видаленою колонкою й дублікатом коду колонки (жива після видаленої); для набору вузлів (без аркуша, з аркушем, неіснуюча таблиця/колонка, `{Month}`) звірити `Resolve` і `diagnostics` з еталонною копією старого `FindTable`/`ResolveColumn` у тесті. Мутація: у `TableIndex.Build` замінити `TryAdd` на індексатор (`ById[table.Id] = …`) → еталон розходиться на дублікатах.

**Перевірка після впровадження:** `dotnet build`; `dotnet test tests/Ecr.Expressions.Tests --filter "FullyQualifiedName~Binding"`; `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~Recalculation|FullyQualifiedName~Templates"`; `dotnet test tests/Ecr.Architecture.Tests`. На стенді: повний перерахунок Land-документа до/після, `dotnet-counters monitor -n Ecr.Worker System.Runtime[alloc-rate,gen-0-gc-count]`.

**Ризики і побічні ефекти:** резолвер не потокобезпечний (як і зараз — він створюється на контекст/виклик; жоден із 6 місць створення не ділить екземпляр між потоками — перевірено `git grep "new ReferenceResolver"`). Індекс будується ліниво при першому `Resolve`, тож тест, що збирає знімок і змінює його ДО першого виклику, поведінки не міняє; зміна знімка ПІСЛЯ першого виклику тим самим резолвером у коді продукту не трапляється (знімок з `MetadataCache` незмінний). Кількість запитів до БД не змінюється.

**Обґрунтування:** індекс у самому резолвері закриває ВСІ 6 споживачів одним місцем; альтернатива «кеш за `CellReferenceNode`» відкинута — вузли AST є `record` з рівністю за значенням, і два однакові посилання в різних таблицях давали б колізію.

**Рішення PO:** не потрібне.

**Впевненість:** підтверджено читанням; величина виграшу — потребує заміру.

**Залежності:** незалежна. Підсилює P-4 (публікація використовує той самий резолвер).

---

### P-2 — Висока — План перерахунку: ребро на кожен рядок для залежності від колонки дублює колонковий індекс

**Категорія:** performance

**Де:**
- `src/Ecr.Application/Recalculation/RecalculationPlanBuilder.cs:100-103` (колонкове ребро `DependsOnColumn`), `:130-143` (цикл по всіх рядках у `AddCellEdge`).
- `src/Ecr.Application/Recalculation/RecalculationService.cs:2294-2302` (`DependsOnCell`), `:2336-2345` (`DependentsOf` — об'єднує точні й колонкові ребра), `:553` (план будується на КОЖЕН прогін), єдиний споживач `DependentsOf` — `:145-151` (кладе в `HashSet`).
- Тест, що звіряє порядок списку: `tests/Ecr.Application.Tests/Recalculation/RecalculationPlanBuilderIndexTests.cs:57-58`.

**Проблема:** для залежності без `RowKey` (колонкові формули `[Current]`, предикати динамічних таблиць) `Build` уже додає колонкове ребро, а `AddCellEdge` додатково створює `CellAddress` + запис у `Dictionary<CellAddress, List<int>>` для КОЖНОГО живого рядка таблиці:
```csharp
// RecalculationPlanBuilder.cs:100-103
if (dependency.RowKey is null && dependency.ColumnDefId is { } wholeColumnId)
{
    plan.DependsOnColumn(formulaId, wholeColumnId);
}
// :135-142
if (dependency.RowKey is not { } rowKey)
{
    foreach (var rowId in rowIdsByKey.Values)
    {
        plan.DependsOnCell(formulaId, new CellAddress(periodKey, rowId, columnId));
    }
    return;
}
```
`DependentsOf` (`RecalculationService.cs:2336-2345`) повертає `exact ∪ wholeColumn`, де `wholeColumn` уже містить цю формулу для будь-якої адреси колонки, — тож рядкові ребра нічого не додають до множини залежних.

**Сценарій відтворення:** динамічна таблиця на 2 000 рядків, 10 колонкових формул по 3 посилання на `[Current]` → 30 залежностей без рядка → 60 000 ребер і стільки ж `List<int>`/записів словника; таблиць таких кілька → сотні тисяч ребер на КОЖНУ правку комірки (`FormulaRecalculationJob` → `RunAsync` → `Build`). Фактично: O(Σ залежностей_без_рядка × рядків) на прогін; очікувано: O(залежностей).

**Вплив:** латентність перерахунку після кожної правки (вікно «формули перераховуються») і GC воркера/API під дедлайн; `FormulaJobFixedCostTests` стереже лише кількість запитів, не CPU.

**Зміна — покроково:**
1. `src/Ecr.Application/Recalculation/RecalculationPlanBuilder.cs:130-143` — було:
   ```csharp
        // ⚠ Залежність БЕЗ конкретного рядка — це предикат динамічного
        // діапазону (`RowMode = Dynamic`): наперед відомого списку рядків не
        // існує, і формула вважається залежною від УСІХ рядків своєї
        // таблиці. Пропустити її означало б, що додавання рядка в динамічну
        // таблицю не перераховує підсумок.
        if (dependency.RowKey is not { } rowKey)
        {
            foreach (var rowId in rowIdsByKey.Values)
            {
                plan.DependsOnCell(formulaId, new CellAddress(periodKey, rowId, columnId));
            }

            return;
        }
   ```
   стало:
   ```csharp
        // ⚠ Залежність БЕЗ конкретного рядка (предикат динамічного діапазону, колонкова формула) залежить від УСІХ
        // рядків колонки. Це покриває колонкове ребро `DependsOnColumn` у `Build` (D3): `DependentsOf` повертає його
        // для КОЖНОЇ адреси колонки, включно з видаленими й новими рядками.
        // ⛔ P-2 (аудит 2026-11-10): ребро на кожен живий рядок тут більше не додається — воно нічого не додавало до
        // множини залежних, а коштувало рядки × залежності алокацій на кожну правку комірки.
        if (dependency.RowKey is not { } rowKey)
        {
            return;
        }
   ```
2. `src/Ecr.Application/Recalculation/RecalculationService.cs` — у клас `RecalculationPlan` після `DependsOnCell` (рядок ~2302) додати:
   ```csharp
    /// <summary>Кількість ребер «комірка → формула» (діагностика розміру плану; тест P-2).</summary>
    internal int CellEdgeCount => _byCell.Values.Sum(list => list.Count);
   ```
3. `tests/Ecr.Application.Tests/Recalculation/RecalculationPlanBuilderIndexTests.cs:58` — порядок списку перестає бути контрактом (споживач — `HashSet`, `RecalculationService.cs:145-151`). Було:
   ```csharp
                    Assert.Equal(expectedDependents, actual.DependentsOf(address));
   ```
   стало:
   ```csharp
                    // P-2: множина, а не порядок — єдиний споживач (`RecalculationService.Plan`) кладе її в HashSet.
                    Assert.Equal(expectedDependents.ToHashSet(), actual.DependentsOf(address).ToHashSet());
   ```
   Еталон `ReferencePlan` (той самий файл, рядки 321-337) НЕ змінювати — він і доводить рівність множин зі старим алгоритмом.
4. Той самий файл — новий кейс (див. «Тест-доказ»).

**Тест-доказ:** `Ecr.Application.Tests` / `Recalculation/RecalculationPlanBuilderIndexTests.cs` / новий `[Fact] Залежність_від_усієї_колонки_не_дає_ребра_на_кожен_рядок`: `TemplateBuilder`, таблиця `T` з колонкою `A` і формульною `TOTAL` (`builder.Column(table, "TOTAL", CellDataType.Formula)`), формула `builder.Formula(table, "1", FormulaScope.Column, total)`; 5 000 рядків `R0..R4999` → Id `1..5000`; одна залежність `FormulaDependency.ForFormula(formula.Id, dependsOnKind: 0, table.Id, rowKey: null, input.Id, null, null, 0)`. Твердження: `plan.CellEdgeCount == 0`; `DependentsOf(new CellAddress(Period, 4321, input.Id))` містить формулу; `DependentsOf(new CellAddress(Period, 99_999, input.Id))` (рядка немає — сценарій D3) теж містить. Без фіксу `CellEdgeCount == 5000` — тест падає. Наявний диференційний `План_з_індексом_збігається_з_планом_прямого_перебору` (з п.3) доводить рівність множин залежних для всіх адрес на 4 сідах.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~Recalculation"`; `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~FormulaJobFixedCostTests"` (стелі не змінюються); `dotnet test tests/Ecr.Architecture.Tests`.

**Ризики і побічні ефекти:** порядок елементів у `DependentsOf` змінюється — споживач лише один і невпорядкований (перевірено `git grep "DependentsOf("`: `RecalculationService.cs:147` і тести). Колонковий індекс ключований лише `ColumnDefId` (без періоду) — він і раніше був надмножиною, тож нових залежних не з'являється. Запитів до БД не додається.

**Обґрунтування:** мінімальна зміна, яка прибирає надлишок там, де він виникає; альтернатива «кешувати план між прогонами» відкинута — план залежить від живих рядків (`rowIdsByTable`), і інвалідація дорожча за побудову без надлишку.

**Рішення PO:** не потрібне.

**Впевненість:** підтверджено читанням.

**Залежності:** незалежна.

---

### P-3 — Висока — «Перевірити»/подання: словник усіх комірок будується заново на КОЖЕН рядок

**Категорія:** performance

**Де:**
- `src/Ecr.Application/Validation/TableValidation.cs:72-80` (цикл по рядках, `slice.ForRow(rowKey)`), `:157-209` (`SliceContext`: поле `_values` з `cells.ToDictionary`, `ForRow` → `new SliceContext(...)`, лінійний `_values.FirstOrDefault` у `GetCell(columnCode)` рядок 197).
- Викликачі: `src/Ecr.Application/Documents/ValidateDocumentHandler.cs:131-134` («Перевірити»), `src/Ecr.Application/Workflow/SubmitSheetHandler.cs:588-590` (подання, ФВ-5.4).

**Проблема:** `ForRow` створює новий `SliceContext`, а ініціалізатор поля знову перетворює ВСІ комірки таблиці на словник (з боксуванням значень через `ToRuleValue`):
```csharp
// TableValidation.cs:170-179
private readonly Dictionary<(long Row, int Column), object?> _values =
    cells.ToDictionary(
        c => (c.Address.TableRowId, c.Address.ColumnDefId),
        c => Ecr.Expressions.Evaluation.CellValueMapping.ToRuleValue(c.Value));
...
public SliceContext ForRow(string rowKey)
    => new SliceContext(table, cells, rowIds) { _currentRowKey = rowKey };
```
Плюс правила рівня таблиці/документа шукають «перше значення колонки» лінійним проходом по всьому словнику на кожне читання (`:197`).

**Сценарій відтворення:** таблиця з хоча б одним правилом валідації, 2 000 рядків × 10 колонок = 20 000 комірок. «Перевірити» / «Подати» → 2 000 × 20 000 = 40 млн вставок у словник і ~1,5 ГБ тимчасових алокацій (LOH: словник на 20 000 записів > 85 КБ) на ОДНУ таблицю. Фактично: O(рядки × комірки); очікувано: O(комірки) — словник один на зріз.

**Вплив:** секунди CPU і Gen2-збирання в API саме в пік дедлайну подання, на кожному «Перевірити» великого документа; бюджет «3 с p95 на документ» (коментар `ValidateDocumentHandler.cs:92-95`) недосяжний на великих динамічних таблицях. `SubmitSheetQueryCountTests` стереже лише запити.

**Зміна — покроково:**
1. `src/Ecr.Application/Validation/TableValidation.cs:157-179` — було:
   ```csharp
    private sealed class SliceContext(
        TableDef table,
        IReadOnlyList<CellRecord> cells,
        IReadOnlyDictionary<string, long> rowIds) : IValidationContext
    {
        // ⛔ Розгортання — СПІЛЬНЕ ... (коментар без змін)
        private readonly Dictionary<(long Row, int Column), object?> _values =
            cells.ToDictionary(
                c => (c.Address.TableRowId, c.Address.ColumnDefId),
                c => Ecr.Expressions.Evaluation.CellValueMapping.ToRuleValue(c.Value));

        private string? _currentRowKey;

        /// <summary>Той самий зріз, наведений на конкретний рядок.</summary>
        public SliceContext ForRow(string rowKey)
            => new SliceContext(table, cells, rowIds) { _currentRowKey = rowKey };
   ```
   стало:
   ```csharp
    private sealed class SliceContext : IValidationContext
    {
        private readonly TableDef _table;
        private readonly IReadOnlyDictionary<string, long> _rowIds;

        // ⛔ Розгортання — СПІЛЬНЕ ... (коментар без змін)
        // ⛔ P-3 (аудит 2026-11-10): словник будується ОДИН раз на зріз і ДІЛИТЬСЯ контекстами рядків. Доти `ForRow`
        // перебудовував його з усіх комірок на кожен рядок — O(рядки × комірки) на «Перевірити» й подання.
        private readonly Dictionary<(long Row, int Column), object?> _values;

        /// <summary>Перше значення кожної колонки в порядку зрізу — те, що віддавав лінійний `FirstOrDefault`.</summary>
        private readonly Dictionary<int, object?> _firstByColumn;

        private readonly string? _currentRowKey;

        public SliceContext(TableDef table, IReadOnlyList<CellRecord> cells, IReadOnlyDictionary<string, long> rowIds)
        {
            _table = table;
            _rowIds = rowIds;
            _values = cells.ToDictionary(
                c => (c.Address.TableRowId, c.Address.ColumnDefId),
                c => Ecr.Expressions.Evaluation.CellValueMapping.ToRuleValue(c.Value));
            _firstByColumn = new Dictionary<int, object?>();
            foreach (var cell in cells)
            {
                _firstByColumn.TryAdd(cell.Address.ColumnDefId, _values[(cell.Address.TableRowId, cell.Address.ColumnDefId)]);
            }
        }

        private SliceContext(SliceContext shared, string rowKey)
        {
            _table = shared._table;
            _rowIds = shared._rowIds;
            _values = shared._values;
            _firstByColumn = shared._firstByColumn;
            _currentRowKey = rowKey;
        }

        /// <summary>Той самий зріз, наведений на конкретний рядок.</summary>
        public SliceContext ForRow(string rowKey) => new(this, rowKey);
   ```
   Рядки 194-197 — було:
   ```csharp
            var column = table.Columns.FirstOrDefault(c => c.Code == columnCode);
            return column is null
                ? null
                : _values.FirstOrDefault(v => v.Key.Column == column.Id).Value;
   ```
   стало:
   ```csharp
            var column = _table.Columns.FirstOrDefault(c => c.Code == columnCode);
            return column is null
                ? null
                : _firstByColumn.GetValueOrDefault(column.Id);
   ```
   Рядки 203-207 — `table.` → `_table.`, `rowIds.` → `_rowIds.` (лише перейменування полів).
2. Тест — новий файл (див. нижче).

**Тест-доказ:** `Ecr.Application.Tests` / `Validation/TableValidationScaleTests.cs` / клас `TableValidationScaleTests`:
- `Перевірка_рядків_не_перебудовує_зріз_на_кожен_рядок`: `TemplateBuilder` → таблиця з 5 колонками `C0..C4`; `table.AddValidationRule(new ValidationRule(table.Id, EcrCode.Create("NONNEG"), ValidationSeverity.Error, scope: 1, "[C0] >= 0", new LocalizedText(new Dictionary<string, string> { ["en"] = "neg" })))`; 2 000 рядків (`rowIds["R{i}"] = i + 1`), 10 000 `CellRecord(new CellAddress(new PeriodKey(202601), rowId, colId), table.Id, new CellValueData { ValueNumeric = 1m })`, рядок `R7` має `C0 = -1`. Виклик `TableValidation.RunAsync(new ValidationEngine(new RealFormulaEngine()), Substitute.For<IRegistryStore>(), snapshot, table, cells, rowIds, new Dictionary<string, ExpressionValue>(), "en", default)` (клас `internal`, `InternalsVisibleTo` є — `src/Ecr.Application/Ecr.Application.csproj:22`). Твердження: рівно одне повідомлення з `RowKey == "R7"`; `GC.GetAllocatedBytesForCurrentThread()` різниця `< 200_000_000`. Без фіксу ~2 000 словників по 10 000 записів ≈ 1,5 ГБ — падає; з фіксом — одиниці МБ.
- `Правило_таблиці_бачить_перше_значення_колонки` (захист семантики `_firstByColumn`): зріз, де колонка `C1` має значення 5 у першій за порядком `cells` комірці й 9 у наступній; правило scope 2 `[C1] = 5` не дає повідомлення, `[C1] = 9` — дає.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~Validation|FullyQualifiedName~ValidateDocument|FullyQualifiedName~SubmitSheet"`; `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~SubmitSheetQueryCountTests"`; на стенді — «Перевірити» на найбільшому Land-документі до/після (час відповіді).

**Ризики і побічні ефекти:** контекст рядка тепер ділить словник — він лише читається (`GetValueOrDefault`), запису немає. Семантика «першого значення» збережена (порядок `cells`, `ToDictionary` кидає на дубль адреси так само). Подання й «Перевірити» лишаються спільним кодом (ФВ-5.4).

**Обґрунтування:** найменша локальна зміна; альтернатива «читати значення одразу з `cells` за індексом» відкинута — потрібен той самий словник для `GetCell(rowKey, columnCode)`.

**Рішення PO:** не потрібне.

**Впевненість:** підтверджено читанням.

**Залежності:** разом із P-5 (той самий цикл; файли різні, порядок довільний).

---

### P-4 — Висока (потребує заміру) — Публікація шаблону: три квадратичні кроки під блоком версії

**Категорія:** performance

**Де:**
- `src/Ecr.Application/Templates/PublishChecks.cs:228-276` (`DependsOn`: для кожної залежності — перебір усіх формул таблиці, а `FormulaOutputs.Produces(formula, table, …)` для не-колонкової формули ще й лінійно шукає її рядок `FormulaOutputs.RowKeyOf` — `src/Ecr.Application/Recalculation/FormulaOutputs.cs`, `table.Rows.FirstOrDefault(r => r.Id == rowId)`).
- `src/Ecr.Application/Templates/PublishChecks.cs:134-142` (запис `EvaluationOrder`: для КОЖНОГО id — `SelectMany` по всіх формулах).
- `src/Ecr.Expressions/Graph/TopologicalSorter.cs:28-50` (Кан із повним проходом по `remaining` на кожен вузол — O(V²)); викликається з `src/Ecr.Infrastructure/Expressions/FormulaEngine.cs:139` для `PublishChecks.cs:124`, `PublishMethodologyHandler.cs:462`, `MethodologyFormulaOrder.cs:67`.
- Транзакція й блок: `src/Ecr.Application/Templates/PublishTemplateVersionHandler.cs:82-84` (`ExecuteInTransactionAsync` → `PublishLockedAsync`, `LockVersionForUpdateAsync` — `:98`), `PublishChecks.Run` — `:127`.

**Проблема:** рантайм уже позбувся рівно цього квадрата (L2b, `RecalculationPlanBuilder.cs:49-52`: «пошук виробників був O(залежності × формули таблиці × рядки) і коштував секунди на правку однієї комірки») через `ProducerIndex`, але публікація лишилась на старому переборі:
```csharp
// PublishChecks.cs:265-271
foreach (var candidate in table.Formulas.Where(f => !f.IsDeleted))
{
    if (Recalculation.FormulaOutputs.Produces(
            candidate, table, dependency.RowKey, dependency.ColumnDefId))
// PublishChecks.cs:136-141
var formula = tables.Values
    .SelectMany(t => t.Formulas)
    .FirstOrDefault(f => f.Id == id);
// TopologicalSorter.cs:43-49
foreach (var (dependent, dependencies) in remaining)
{
    if (dependencies.Remove(node) && dependencies.Count == 0)
```

**Сценарій відтворення:** шаблон масштабу тесту L2b (`RecalculationPlanBuilderIndexTests.cs:110-170`: 40 таблиць × 100 рядків × 10 комірочних формул = 40 000 формул). Публікація: `DependsOn` ≈ (залежностей таблиці ~1 000–2 000) × (формул таблиці 1 000) × (рядків/2 = 50) ≈ 50–100 млн порівнянь на таблицю × 40; запис порядку 40 000 × 40 000 / 2 = 800 млн; сортування 40 000² / 2 = 800 млн. Фактично: хвилини CPU всередині транзакції з блоком рядка версії (паралельні правки чернетки чекають, HTTP-запит публікації може впертися в тайм-аут); очікувано: секунди.

**Вплив:** адміністратор шаблонів і методолог (публікація шаблону/методології); блок версії тримається весь час перевірок. Реальна тривалість на Land — потребує заміру (журнал тривалості `POST …/publish`).

**Зміна — покроково:**
1. `src/Ecr.Application/Templates/PublishChecks.cs` — у `Run` перед циклом по таблицях (після `var nodes = new List<FormulaNode>();`, рядок ~84) додати:
   ```csharp
        // ⛔ P-4 (аудит 2026-11-10): виробники комірок — той самий індекс, що й у рантаймі (L2b,
        // `RecalculationPlanBuilder.ProducerIndex`), а не перебір формул таблиці на кожну залежність.
        var live = new Dictionary<int, (TableDef Table, FormulaDef Formula)>();
        foreach (var liveTable in tables.Values)
        {
            foreach (var liveFormula in liveTable.Formulas.Where(f => !f.IsDeleted))
            {
                live.TryAdd(liveFormula.Id, (liveTable, liveFormula));
            }
        }

        var producers = new Dictionary<int, Recalculation.RecalculationPlanBuilder.ProducerIndex>();
   ```
   Рядок 112 — було `DependsOn(formula, dependencies, tables)));` → стало `DependsOn(formula, dependencies, tables, live, producers)));`.
   Сигнатура (228-231) — було:
   ```csharp
    private static List<int> DependsOn(
        FormulaDef formula,
        IReadOnlyList<FormulaDependencyRef> dependencies,
        Dictionary<int, TableDef> tables)
   ```
   стало:
   ```csharp
    private static List<int> DependsOn(
        FormulaDef formula,
        IReadOnlyList<FormulaDependencyRef> dependencies,
        Dictionary<int, TableDef> tables,
        Dictionary<int, (TableDef Table, FormulaDef Formula)> live,
        Dictionary<int, Recalculation.RecalculationPlanBuilder.ProducerIndex> producers)
   ```
   Рядки 265-272 — було:
   ```csharp
            foreach (var candidate in table.Formulas.Where(f => !f.IsDeleted))
            {
                if (Recalculation.FormulaOutputs.Produces(
                        candidate, table, dependency.RowKey, dependency.ColumnDefId))
                {
                    result.Add(candidate.Id);
                }
            }
   ```
   стало:
   ```csharp
            if (!producers.TryGetValue(table.Id, out var index))
            {
                producers[table.Id] = index = Recalculation.RecalculationPlanBuilder.ProducerIndex.Build(table, live);
            }

            // ⚠ `Candidates` — у порядку `TableDef.Formulas` і з остаточним `FormulaOutputs.Produces`; саму формулу
            // НЕ виключає (самопосилання лишається циклом, ФВ-9.4).
            foreach (var candidate in index.Candidates(dependency.RowKey, dependency.ColumnDefId))
            {
                result.Add(candidate.Id);
            }
   ```
   Рядки 134-142 — було:
   ```csharp
            var order = 0;
            foreach (var id in ordering.Order)
            {
                var formula = tables.Values
                    .SelectMany(t => t.Formulas)
                    .FirstOrDefault(f => f.Id == id);

                formula?.SetEvaluationOrder(order++);
            }
   ```
   стало (⚠ лічильник, як і раніше, росте лише для знайдених формул — `?.` пропускав обчислення аргументу):
   ```csharp
            var byId = new Dictionary<int, FormulaDef>();
            foreach (var candidate in tables.Values.SelectMany(t => t.Formulas))
            {
                byId.TryAdd(candidate.Id, candidate);
            }

            var order = 0;
            foreach (var id in ordering.Order)
            {
                if (byId.TryGetValue(id, out var formula))
                {
                    formula.SetEvaluationOrder(order++);
                }
            }
   ```
2. `src/Ecr.Expressions/Graph/TopologicalSorter.cs:28-60` — Кан зі зворотними ребрами, ПОРЯДОК ВИХОДУ ІДЕНТИЧНИЙ (звільнені вузли ставляться в чергу в порядку вставки в колишній `remaining`, тобто в порядку перебору `nodes`). Було — рядки 28-60 (цитата в «Проблемі»). Стало:
   ```csharp
        // ⛔ P-4 (аудит 2026-11-10): зворотні ребра замість повного проходу по `remaining` на кожен вузол (O(V²)).
        // Порядок виходу той самий: звільнені вузли ставляться в чергу в порядку перебору `nodes` (= порядок вставки
        // колишнього словника `remaining`, з якого нічого не додавалось після побудови).
        var position = new Dictionary<int, int>(nodes.Count);
        var remaining = new Dictionary<int, HashSet<int>>(nodes.Count);
        var dependents = new Dictionary<int, List<int>>();
        var next = 0;
        foreach (var node in nodes)
        {
            position[node] = next++;
            var dependencies = new HashSet<int>(graph.DependenciesOf(node).Where(nodes.Contains));
            remaining[node] = dependencies;
            foreach (var dependency in dependencies)
            {
                if (!dependents.TryGetValue(dependency, out var list))
                {
                    dependents[dependency] = list = [];
                }

                list.Add(node);
            }
        }

        var order = new List<int>(nodes.Count);
        var ready = new Queue<int>(remaining.Where(p => p.Value.Count == 0)
                                            .Select(p => p.Key)
                                            .OrderBy(id => id));
        var released = new List<int>();

        while (ready.Count > 0)
        {
            var node = ready.Dequeue();
            order.Add(node);
            remaining.Remove(node);

            if (!dependents.TryGetValue(node, out var waiting))
            {
                continue;
            }

            released.Clear();
            foreach (var dependent in waiting)
            {
                if (remaining.TryGetValue(dependent, out var dependencies)
                    && dependencies.Remove(node) && dependencies.Count == 0)
                {
                    released.Add(dependent);
                }
            }

            released.Sort((a, b) => position[a].CompareTo(position[b]));
            foreach (var dependent in released)
            {
                ready.Enqueue(dependent);
            }
        }

        if (remaining.Count == 0)
        {
            return new OrderingResult(true, order, null);
        }

        // Прапорця «є цикл» замало: ... (коментар без змін)
        return new OrderingResult(false, order, FindCycle(remaining));
   ```
   (`FindCycle` отримує той самий вміст: множини незвільнених вузлів без уже впорядкованих.)
3. Тести — див. нижче.

**Тест-доказ:**
- `Ecr.Expressions.Tests` / `Graph/TopologicalSorterTests.cs`: (а) `Порядок_збігається_з_еталонним_алгоритмом` — 50 випадкових графів (`new Random(seed)`, 1–300 вузлів, з циклами й без, самопосилання), еталон — дослівна копія старого `Sort` у тесті; звірити `IsSuccess`, `Order` (поелементно) і `CyclePath`. Мутація: прибрати `released.Sort(...)` → порядок розходиться. (б) `Сортування_ланцюга_на_40000_вузлів_не_квадратичне` — ланцюг `i → i-1`, прогрів + найкращий із трьох, бюджет 1000 мс (той самий прийом, що `RecalculationPlanBuilderIndexTests.Побудова_плану_на_шаблоні_Land_масштабу_не_квадратична`). Без фіксу 40 000² / 2 ітерацій — кілька секунд, падає. ⚠ Тест на час: за правилом «Режим тестів» прогнати локально 20/20 перед пушем.
- `Ecr.Application.Tests` / `Templates/PublishChecksTests.cs`: (в) `Порядок_і_залежності_публікації_не_змінились` — на наявних фікстурах файлу звірити `EvaluationOrder` усіх формул і `FormulaNode.DependsOnFormulaDefIds` (через `PublishChecks.Run` + читання `formula.EvaluationOrder`) зі значеннями до фіксу (зафіксувати першим прогоном на старому коді в тесті як очікувані літерали). (г) `Публікація_20000_формул_не_квадратична`: версія 10 таблиць × 200 рядків × 10 комірочних формул (`[C{c-1}]` того ж рядка), `PublishChecks.Run` < 5 с (найкращий із двох). Без фіксу — хвилини.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Expressions.Tests --filter "FullyQualifiedName~Graph"`; `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~Templates|FullyQualifiedName~Calculations|FullyQualifiedName~Recalculation"`; `dotnet test tests/Ecr.Architecture.Tests`. На стенді: публікація копії Land-шаблону до/після (час відповіді, `sys.dm_tran_locks` під час публікації).

**Ризики і побічні ефекти:** `EvaluationOrder` фіксується при публікації й зберігається — тому порядок виходу сортувальника мусить лишитися ІДЕНТИЧНИМ (тест (а)); `ProducerIndex.Candidates` еквівалентний старому `Produces`-перебору (доведено `RecalculationPlanBuilderIndexTests` на 4 сідах), сам `ProducerIndex` — `internal` у тому ж збиранні. Методологічний порядок (`PublishMethodologyHandler`, `MethodologyFormulaOrder`) отримує той самий виграш автоматично. Запитів до БД не змінюється.

**Обґрунтування:** перевикористання вже доведеного індексу L2b замість нового; альтернатива «винести перевірки з транзакції» відкинута — `DraftVersionLock` свідомо тримає блок від читання знімка до коміту (коментар `PublishTemplateVersionHandler.cs:74-81`).

**Рішення PO:** не потрібне.

**Впевненість:** складність підтверджено читанням; абсолютний час на Land — потребує заміру.

**Залежності:** P-1 (резолвер у `CheckExpression`) — незалежна, але разом дають повний ефект.

---

### P-5 — Середня — `ValidationEngine` розбирає вираз правила на кожен рядок і кожну комірку

**Категорія:** performance

**Де:** `src/Ecr.Application/Validation/ValidationEngine.cs:200` (`Evaluate` → `formulaEngine.Parse(rule.Expression, ExpressionDialect.Template)` на КОЖЕН виклик), `:164` (`RegistryFieldReads`); виклики: `ValidateScope` `:122-126` (рядкові правила — з `TableValidation.cs:76-80` на кожен рядок), `ValidateCell` `:74-87` (з `PatchCellsHandler.cs:3619` на кожну комірку PATCH/імпорту). Реєстрація — scoped: `src/Ecr.Application/DependencyInjection.cs:164`. Розбору без кешу немає: `src/Ecr.Infrastructure/Expressions/FormulaEngine.cs:58-59`.

**Проблема:**
```csharp
// ValidationEngine.cs:200
var parsed = formulaEngine.Parse(rule.Expression, ExpressionDialect.Template);
```
Перерахунок це вже виправив (розбір раз на прогін, `RecalculationService.cs:637-653`, сторож `CountingFormulaEngine.ParseCalls`), валідація — ні.

**Сценарій відтворення:** таблиця 2 000 рядків × 20 рядкових правил → «Перевірити» = 40 000 лексувань/розборів; PATCH/імпорт 2 000 комірок колонки з 5 коміркових правил = 10 000 розборів у синхронному шляху запису. Фактично: розбір на кожне застосування; очікувано: раз на вираз на запит.

**Вплив:** CPU API на «Перевірити», поданні й записі комірок (імпорт Excel іде тим самим PATCH-шляхом); на кожну таку дію — десятки тисяч алокацій AST.

**Зміна — покроково:**
1. `src/Ecr.Application/Validation/ValidationEngine.cs` — у клас після `BrokenRuleCode` (рядок ~21) додати:
   ```csharp
    /// <summary>Розібрані вирази правил — один розбір на вираз на екземпляр (scoped = запит).</summary>
    /// <remarks>
    /// ⛔ P-5 (аудит 2026-11-10): доти кожне застосування правила (кожен рядок «Перевірити»/подання, кожна комірка
    /// PATCH) розбирало вираз наново. Ключ — сам текст: діалект тут завжди `Template`. Екземпляр scoped і не
    /// ділиться між потоками.
    /// </remarks>
    private readonly Dictionary<string, Ecr.Expressions.Parsing.ParseResult> _parsed = new(StringComparer.Ordinal);

    private Ecr.Expressions.Parsing.ParseResult ParseRule(string expression)
    {
        if (!_parsed.TryGetValue(expression, out var result))
        {
            result = formulaEngine.Parse(expression, ExpressionDialect.Template);
            _parsed[expression] = result;
        }

        return result;
    }
   ```
   Рядок 200 — було `var parsed = formulaEngine.Parse(rule.Expression, ExpressionDialect.Template);` → стало `var parsed = ParseRule(rule.Expression);`. Рядок 164 — те саме.
2. Тест — новий файл.

**Тест-доказ:** `Ecr.Application.Tests` / `Validation/ValidationRuleParseOnceTests.cs`: (а) `TableValidation.RunAsync(new ValidationEngine(counting), …)` з `var counting = new CountingFormulaEngine()` (`tests/Ecr.Application.Tests/Recalculation/CountingFormulaEngine.cs`, `using Ecr.Application.Tests.Recalculation;`), 500 рядків, одне рядкове правило + одне правило таблиці → `Assert.Equal(2, counting.ParseCalls)`; без фіксу 501. (б) 300 викликів `ValidateCell` з тим самим правилом на одному екземплярі → `ParseCalls == 1`; без фіксу 300. (в) зламаний вираз правила дає `ECR-VAL-RULE` на кожному застосуванні так само (результат розбору з помилкою теж кешується).

**Перевірка після впровадження:** `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~Validation|FullyQualifiedName~PatchCells"`; `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~PatchCellsQueryCountTests"`.

**Ризики і побічні ефекти:** `ParseResult`/`ParsedExpression`/вузли AST — незмінні `record` (`src/Ecr.Expressions/Parsing/ParseResult.cs:11,17`, `src/Ecr.Expressions/Ast/AstNode.cs`), обчислювач їх не змінює — спільне використання в межах запиту безпечне. Пам'ять: кількість різних виразів правил версії (десятки–сотні) на запит.

**Обґрунтування:** кеш у scoped-екземплярі не потребує інвалідації (правка правила — новий запит); альтернатива «глобальний кеш за текстом» відкинута — потребувала б меж і потокобезпеки без вимірного додаткового виграшу.

**Рішення PO:** не потрібне.

**Впевненість:** підтверджено читанням.

**Залежності:** разом із P-3 (незалежні файли).

---

### P-6 — Середня — `DocumentReadScope` перебудовує структурні словники на кожне створення; знімок-обʼєднання — на кожне звернення

**Категорія:** performance

**Де:**
- `src/Ecr.Application/Security/DocumentReadScope.cs:35-41` (7 структурних словників), `:45-75` (конструктор обходить УСІ аркуші/таблиці/колонки знімка), `:123-124` (`InPeriod` → `new(...)` → повна перебудова).
- `src/Ecr.Infrastructure/Security/AccessDecisionService.cs:1067-1109` (`ProjectReadSnapshotAsync`: для багатоверсійного проєкту копіює `ColumnsById` і `RowsByKey` усіх версій і робить новий `record` на КОЖЕН виклик); викликачі: `ReadScopeAsync` `:1050`, `ReadScopesAsync` `:1145`, `SnapshotAsync` `:3478-3494` ← `SliceContextsAsync` `:2126` ← `CanEditSliceAsync`/`CanStyleSliceAsync`/PATCH (`:1324`, `:1486`, `:1656`).
- Гарячий шлях: `src/Ecr.Application/Documents/GetTableSliceHandler.cs:190-191` (`ReadScopeAsync` + `InPeriod`), `:269` (`CanEditSliceAsync`), `:491` (`CanStyleSliceAsync`).

**Проблема:** структурні мапи залежать лише від знімка, а будуються заново на кожне створення меж (і ще раз на `InPeriod`):
```csharp
// DocumentReadScope.cs:55-74
foreach (var sheet in snapshot.Sheets)
{
    _sheetCodes[sheet.Id] = sheet.Code;
    _tablesOfSheet[sheet.Id] = [.. sheet.Tables.Select(t => t.Id)];
    foreach (var table in sheet.Tables)
    { ... foreach (var column in table.Columns) { _tableOfColumn[column.Id] = table.Id; _columnByCode[(table.Id, column.Code)] = column.Id; ... } }
}
// DocumentReadScope.cs:123-124
public DocumentReadScope InPeriod(PeriodKey period)
    => new(_profile, _projectId, _snapshot, period, _grants);
```
Для проєкту з кількома версіями (RPT-STABLE Part B, `cd24aa1fd` 30.10) щоразу будується новий знімок-обʼєднання:
```csharp
// AccessDecisionService.cs:1090-1108
var columns = new Dictionary<int, ...ColumnDef>(current.ColumnsById);
var rows = new Dictionary<(int TableDefId, string RowKey), ...RowDef>(current.RowsByKey);
foreach (var versionId in others) { ... columns.TryAdd(...); rows.TryAdd(...); }
return current with { Sheets = sheets, ColumnsById = columns, RowsByKey = rows };
```

**Сценарій відтворення:** GET зрізу Land-таблиці: `ReadScopeAsync` (побудова №1) + `InPeriod` (№2) + `CanEditSliceAsync`/`CanStyleSliceAsync` (знімки через `SnapshotAsync`); у проєкті після переносу «з місяця» кожне з цих звернень ще й копіює словники колонок і рядків усіх версій (кілька словників > 85 КБ → LOH). Перелік документів (`DocumentSheetVisibility.ScopesAsync`, `src/Ecr.Application/Documents/DocumentSheetVisibility.cs:56-81`) — по побудові на проєкт сторінки + `InPeriod`. Фактично: O(колонок шаблону × версій) алокацій кілька разів на запит; очікувано: раз на ревізію знімка.

**Вплив:** найчастіші запити (зріз, PATCH, перелік) кожного користувача; Gen2/LOH-тиск при багатьох одночасних користувачах. Абсолютні числа — потребує заміру (кількість колонок Land-шаблону й версій у проєкті).

**Зміна — покроково:**
1. `src/Ecr.Application/Security/DocumentReadScope.cs`:
   - `using System.Runtime.CompilerServices;` угорі.
   - Рядки 35-41 — прибрати ініціалізатори `= []` у семи структурних полів (`_tableCodes`, `_sheetCodes`, `_sheetOfTable`, `_tablesOfSheet`, `_tableOfColumn`, `_columnsOfTable`, `_columnByCode`), типи без змін; `_tables`, `_columns` (мемо рішень, `:42-43`) лишити як є — вони залежать від профілю/періоду.
   - Конструктор рядки 55-74 (цикл) — було: цикл (цитата вище); стало:
     ```csharp
        // ⛔ P-6 (аудит 2026-11-10): структура залежить лише від знімка — береться з кешу за знімком, а не
        // будується на кожне створення меж (і на кожен `InPeriod`). Мапи лише читаються.
        var structure = Structure.For(snapshot);
        _tableCodes = structure.TableCodes;
        _sheetCodes = structure.SheetCodes;
        _sheetOfTable = structure.SheetOfTable;
        _tablesOfSheet = structure.TablesOfSheet;
        _tableOfColumn = structure.TableOfColumn;
        _columnsOfTable = structure.ColumnsOfTable;
        _columnByCode = structure.ColumnByCode;
     ```
   - Новий вкладений клас (у кінці `DocumentReadScope`):
     ```csharp
    /// <summary>Структурні мапи знімка; кеш за посиланням на знімок з підписом (як `RecalculationPlanBuilder.SnapshotIndex`).</summary>
    private sealed class Structure
    {
        private static readonly ConditionalWeakTable<TemplateVersionSnapshot, Structure> Cache = new();

        public required long Signature { get; init; }
        public Dictionary<int, string> TableCodes { get; } = [];
        public Dictionary<int, string> SheetCodes { get; } = [];
        public Dictionary<int, int> SheetOfTable { get; } = [];
        public Dictionary<int, List<int>> TablesOfSheet { get; } = [];
        public Dictionary<int, int> TableOfColumn { get; } = [];
        public Dictionary<int, List<int>> ColumnsOfTable { get; } = [];
        public Dictionary<(int TableDefId, string Code), int> ColumnByCode { get; } = [];

        public static Structure For(TemplateVersionSnapshot snapshot)
        {
            var signature = SignatureOf(snapshot);
            if (Cache.TryGetValue(snapshot, out var known) && known.Signature == signature)
            {
                return known;
            }

            var built = new Structure { Signature = signature };
            foreach (var sheet in snapshot.Sheets)          // ⚠ ДОСЛІВНО колишній цикл конструктора (рядки 55-74)
            {
                built.SheetCodes[sheet.Id] = sheet.Code;
                built.TablesOfSheet[sheet.Id] = [.. sheet.Tables.Select(t => t.Id)];
                foreach (var table in sheet.Tables)
                {
                    built.SheetOfTable[table.Id] = sheet.Id;
                    built.TableCodes[table.Id] = table.Code;
                    var columns = new List<int>(table.Columns.Count);
                    foreach (var column in table.Columns)
                    {
                        built.TableOfColumn[column.Id] = table.Id;
                        built.ColumnByCode[(table.Id, column.Code)] = column.Id;
                        columns.Add(column.Id);
                    }

                    built.ColumnsOfTable[table.Id] = columns;
                }
            }

            Cache.AddOrUpdate(snapshot, built);
            return built;
        }

        /// <summary>Аркуші, таблиці й кількість колонок — страховка від знімка, добудованого після першого виклику (тести).</summary>
        private static long SignatureOf(TemplateVersionSnapshot snapshot)
        {
            long signature = 17;
            foreach (var sheet in snapshot.Sheets)
            {
                signature = (signature * 31) + sheet.Id;
                foreach (var table in sheet.Tables)
                {
                    signature = (signature * 31) + table.Id;
                    signature = (signature * 31) + table.Columns.Count;
                }
            }

            return signature;
        }
    }
     ```
2. Новий файл `src/Ecr.Infrastructure/Security/ProjectReadSnapshots.cs`:
   ```csharp
   using System.Collections.Concurrent;
   using System.Runtime.CompilerServices;
   using Ecr.Domain.Entities.Configuration;

   namespace Ecr.Infrastructure.Security;

   /// <summary>Знімок-обʼєднання багатоверсійного проєкту (RPT-STABLE Part B) — один на набір ЕКЗЕМПЛЯРІВ знімків.</summary>
   /// <remarks>
   /// ⛔ P-6 (аудит 2026-11-10): `MetadataCache` віддає той самий екземпляр знімка, доки ревізія не змінилась; нова
   /// ревізія — новий екземпляр, тож кеш за посиланнями не потребує інвалідації.
   /// </remarks>
   internal static class ProjectReadSnapshots
   {
       private sealed record Merged(TemplateVersionSnapshot[] Others, TemplateVersionSnapshot Snapshot);

       private static readonly ConditionalWeakTable<TemplateVersionSnapshot, ConcurrentDictionary<string, Merged>> Cache = new();

       public static TemplateVersionSnapshot Merge(TemplateVersionSnapshot current, IReadOnlyList<TemplateVersionSnapshot> others)
       {
           var key = string.Join(',', others.Select(o => o.TemplateVersionId));
           var byOthers = Cache.GetValue(current, _ => new ConcurrentDictionary<string, Merged>(StringComparer.Ordinal));
           if (byOthers.TryGetValue(key, out var known)
               && known.Others.Length == others.Count
               && known.Others.Zip(others).All(p => ReferenceEquals(p.First, p.Second)))
           {
               return known.Snapshot;
           }

           var sheets = new List<SheetDef>(current.Sheets);
           var columns = new Dictionary<int, ColumnDef>(current.ColumnsById);
           var rows = new Dictionary<(int TableDefId, string RowKey), RowDef>(current.RowsByKey);
           foreach (var other in others)          // ⚠ ДОСЛІВНО колишній цикл `ProjectReadSnapshotAsync`
           {
               sheets.AddRange(other.Sheets);
               foreach (var column in other.ColumnsById) { columns.TryAdd(column.Key, column.Value); }
               foreach (var row in other.RowsByKey) { rows.TryAdd(row.Key, row.Value); }
           }

           var merged = current with { Sheets = sheets, ColumnsById = columns, RowsByKey = rows };
           byOthers[key] = new Merged([.. others], merged);
           return merged;
       }
   }
   ```
3. `src/Ecr.Infrastructure/Security/AccessDecisionService.cs:1090-1108` — було (цитата в «Проблемі», рядки 1090-1108); стало:
   ```csharp
        var parts = new List<Ecr.Domain.Entities.Configuration.TemplateVersionSnapshot>(others.Count);
        foreach (var versionId in others)
        {
            parts.Add(await metadata.GetAsync(versionId, ct).ConfigureAwait(false));
        }

        return ProjectReadSnapshots.Merge(current, parts);
   ```
4. Тести — див. нижче.

**Тест-доказ:**
- `Ecr.Application.Tests` / `Security/DocumentReadScopeStructureTests.cs`: `InPeriod_не_перебудовує_структуру_знімка` — знімок 50 таблиць × 40 колонок; `var scope = DocumentReadScope.For(profile, 1, snapshot)` (прогрів), потім `GC.GetAllocatedBytesForCurrentThread()` навколо 200 × `scope.InPeriod(new PeriodKey(202601))` → `< 1_000_000` байт. Без фіксу кожен виклик будує ~2 000 записів у 7 словниках (~300 КБ) → ~60 МБ — падає. Друге твердження: `CanReadColumn`/`CanReadTableCode` для набору колонок дають ті самі відповіді, що й до фіксу (наявні `DocumentReadScopeTests` — прогнати повністю).
- `Ecr.Infrastructure.Tests` / `Security/ProjectReadSnapshotsTests.cs` (`InternalsVisibleTo` є — `src/Ecr.Infrastructure/Ecr.Infrastructure.csproj:37`): (а) два виклики `Merge(current, [v1, v2])` з тими самими екземплярами → `Assert.Same`; (б) новий екземпляр `v2'` (та сама `TemplateVersionId`) → НЕ той самий об'єкт і містить колонки `v2'`; (в) вміст = колишнє обʼєднання (кількість аркушів, `ColumnsById.Keys`, `RowsByKey.Keys`, перемагає `current` при конфлікті ключів). Без кешу (а) падає.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~Security"`; `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~Security|FullyQualifiedName~ProjectTemplateVersion"`; `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~DenyRead|FullyQualifiedName~Slice"`; `dotnet test tests/Ecr.Architecture.Tests` (зокрема `ProjectTemplateVersionReadGuardTests`, `DenyLeakGuardTests`).

**Ризики і побічні ефекти:** це шлях прав (S6, D-443) — мапи тепер спільні між запитами, тож будь-яка майбутня ЗАПИСУЮЧА операція над ними зламала б межі іншого користувача: у коді зараз запис є лише в колишньому конструкторі (перевірено `grep` присвоєнь — рядки 57-73); варто додати коментар-заборону біля полів. Знімок-обʼєднання стає спільним об'єктом — він і так незмінний (`record` зі словниками, які ніхто не пише). Кількість запитів до БД не змінюється (запит `others` лишається). Файловий перетин із `origin/lane/analiz/perf-slice` (`AccessDecisionService.cs`) — зводити після неї.

**Обґрунтування:** той самий прийом, що вже прийнятий у `RecalculationPlanBuilder.SnapshotIndex` (`ConditionalWeakTable` + підпис); альтернатива «per-request memo в `AccessDecisionService`» відкинута — не прибирає побудову між запитами й не допомагає `InPeriod`.

**Рішення PO:** не потрібне.

**Впевненість:** підтверджено читанням; виграш — потребує заміру.

**Залежності:** після злиття `lane/analiz/perf-slice` (спільний файл `AccessDecisionService.cs`).

---

### P-7 — Середня — Кандидати згадок чату: до 200 користувачів × (профіль + видимість + межі) на одне натискання

**Категорія:** performance

**Де:** `src/Ecr.Application/Documents/Discussions/TableDiscussionQueries.cs:190-213` (до 5 сторінок × 40 кандидатів), `src/Ecr.Application/Documents/Discussions/TableDiscussionAccess.cs:165-185` (`CanSeeAsync`: `TryBuildProfileAsync` + `RequireVisibleAsync` + `ReadScopeAsync` на КОЖНОГО кандидата); маршрут `GET /api/v1/documents/{id}/tables/{tableCode}/mention-candidates` (`docs/build/02-contracts.md:4648`).

**Проблема:**
```csharp
// TableDiscussionQueries.cs:190
for (var skip = 0; skip < 200 && result.Count < 20; skip += 40)
...
    if (await TableDiscussionAccess
            .CanSeeAsync(access, candidate.Id, documentId, tableCode, period, ct).ConfigureAwait(false))
```
На кандидата: `BuildProfileAsync` (1 запит акаунта + до 2 запитів відбитка груп, `AccessDecisionService.cs:41-100`; холодний профіль — ще повний `LoadAsync`), `CanReadDocumentAsync` (`ProjectIdAsync`, `:3461`), `ReadScopeAsync` (документ+проєкт `:1031-1046` + надання) — 4–10 звернень; холодні профілі ще й засмічують `IMemoryCache`.

**Сценарій відтворення:** організація з кількома сотнями активних користувачів; користувач у чаті таблиці вводить «@ан» (мінімум 2 символи, `:176`) — `DisplayName LIKE '%ан%'` дає сотні збігів, більшість без доступу до документа → 200 перевірок ≈ 800–2 000 запитів на одну відповідь; кожна наступна літера — новий запит. Очікувано: стала, мала кількість звернень на відповідь.

**Вплив:** латентність підказки (секунди) і навантаження на БД від кожного, хто згадує колегу; прогрів сотень чужих профілів у кеші процесу.

**Зміна — покроково:**
1. `src/Ecr.Application/Documents/Discussions/TableDiscussionQueries.cs` — константа в класі:
   ```csharp
    /// <summary>Скільки кандидатів перевіряється на одну відповідь (P-7, аудит 2026-11-10).</summary>
    /// <remarks>Кожна перевірка — профіль + видимість + межі (4–10 звернень); 80 = дві сторінки пошуку.</remarks>
    internal const int MentionScanBudget = 80;
   ```
   Рядок 190 — було `for (var skip = 0; skip < 200 && result.Count < 20; skip += 40)` → стало `for (var skip = 0; skip < MentionScanBudget && result.Count < 20; skip += 40)`.
2. Варіант Б (якщо PO хоче повну глибину 200): новий пакетний метод порту `IAccessDecisionService.UsersSeeingTableAsync(IReadOnlyCollection<int> userIds, long documentId, string tableCode, PeriodKey? period, CancellationToken ct)` з одним читанням документа/знімка і профілями пакетом — зусилля L, зачіпає `AccessDecisionService.cs` і `SimulationAwareAccessDecisionService.cs`; у цьому звіті не специфіковано.

**Тест-доказ:** `Ecr.Api.Tests` / `MentionCandidatesQueryCountTests.cs` (за зразком `AdminListQueryCountTests`, `[Collection("SqlServer")]`, лічильник `Ecr.TestKit.DbCommandCounter`): сід — документ, автор чату з `Document.Discuss`, N активних користувачів з іменем `"Ан{i}"` БЕЗ жодної ролі. Твердження: число команд при N = 81 і при N = 200 ОДНАКОВЕ (бюджет вичерпується на 80), відповідь порожня в обох. Без фіксу числа відрізняються (сканування до 200) — падає. Додатково: один видимий кандидат на позиції ≤ 80 повертається.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~MentionCandidates|FullyQualifiedName~TableDiscussion"`; e2e чату (`grep` специ чату таблиці) — підказка згадок працює.

**Ризики і побічні ефекти:** при сотнях невидимих збігів раніше за видимого користувача підказка його не покаже, доки не уточнено введення (поведінка змінюється → рішення PO). Контракт API не змінюється.

**Обґрунтування:** найменша зміна з обмеженою ціною відповіді; повна альтернатива (Б) — пакетне рішення доступу — дорожча й зачіпає сервіс прав.

**Рішення PO:** «Обмежити перевірку кандидатів згадок 80 найближчими за алфавітом збігами (варіант А, дефолт), чи лишити 200 і замовити пакетну перевірку доступу (варіант Б, L)?»

**Впевненість:** кількість звернень на кандидата — за читанням коду; точне число — зафіксує тест.

**Залежності:** незалежна.

---

### P-8 — Низька — Зріз: умовний формат за текстом Lookup-колонки читає весь довідник на кожен GET

**Категорія:** performance

**Де:** `src/Ecr.Application/Documents/GetTableSliceHandler.cs:702-713` (для кожної колонки-джерела текстового правила — `LookupTargetEntries.ListAsync`), `src/Ecr.Application/Permits/Groups/LookupTargetEntries.cs:73-74,85-99` (`ListEntriesAsync` усього реєстру / усіх реєстрів-членів групи), `src/Ecr.Infrastructure/Persistence/RegistryStore.cs:238-247` (стеля `MaxEntries = 50_000`, `:24`), без кешу.

**Проблема:**
```csharp
// GetTableSliceHandler.cs:707-711
var entries = await Permits.Groups.LookupTargetEntries.ListAsync(registries, groups, registryId, ct)
    .ConfigureAwait(false);
codeByColumn[reference] = entries
    .GroupBy(e => e.Entry.Id)
    .ToDictionary(g => g.Key, g => g.First().Entry.Code);
```
Потрібні лише коди записів, що реально стоять у комірках зрізу (≤ рядків), а читається весь реєстр (для групи дозволів — усі реєстри-члени).

**Сценарій відтворення:** таблиця з умовним форматом `textEq` на Lookup-колонці, що вказує на групу дозволів / великий реєстр; кожне відкриття/прокрутка зрізу → повне читання реєстру (до 50 000 рядків `RegistryEntry` з `DisplayL10n`). Очікувано: читання лише Id із зрізу.

**Вплив:** зайвий обсяг читання на найгарячішому запиті; для малих статусних реєстрів — непомітно (тому «Низька»).

**Зміна — покроково:**
1. `src/Ecr.Application/Documents/GetTableSliceHandler.cs:702-713` — було (цитата вище, весь `foreach (var reference in needCodes.Order(...))`); стало:
   ```csharp
            foreach (var reference in needCodes.Order(StringComparer.Ordinal))
            {
                if (visible.TryGetValue(reference, out var source) && source.LookupRegistryDefId is { } registryId)
                {
                    // ⛔ P-8 (аудит 2026-11-10): лише записи, що стоять у комірках ЦЬОГО зрізу, а не весь реєстр.
                    var ids = rows
                        .Select(r => r.Cells.TryGetValue(reference, out var v) ? v : null)
                        .OfType<long>()
                        .Distinct()
                        .ToList();
                    if (groups is not null)
                    {
                        await groups.EnsureFreshAsync(ct).ConfigureAwait(false);
                    }

                    var entries = ids.Count == 0 ? [] : await registries.FindEntriesByIdsAsync(ids, ct).ConfigureAwait(false);
                    codeByColumn[reference] = entries
                        .Where(e => groups is not null && groups.IsGroup(registryId)
                            ? groups.Accepts(registryId, e.RegistryDefId)
                            : e.RegistryDefId == registryId)
                        .GroupBy(e => e.Id)
                        .ToDictionary(g => g.Key, g => g.First().Code);
                }
            }
   ```
2. Тест — див. нижче.

**Тест-доказ:** `Ecr.Application.Tests` / `Documents/SliceConditionalFormatEntryCodesTests.cs`: обробник зрізу з підміненим `IRegistryStore` (NSubstitute, як у наявних тестах зрізу в `tests/Ecr.Application.Tests/Documents`), правило `textEq "REMOVED"` на Lookup-колонці; комірки двох рядків з Id 11 і 12. Твердження: `ListEntriesAsync` не викликано (`DidNotReceive()`), `FindEntriesByIdsAsync` викликано з `{11, 12}`; рядок із кодом `REMOVED` отримав формат. Без фіксу `ListEntriesAsync` викликається — падає.

**Перевірка після впровадження:** `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~ConditionalFormat|FullyQualifiedName~TableSlice"`; `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~TableSliceMethodologyQueryCountTests"`.

**Ризики і побічні ефекти:** `FindEntriesByIdsAsync` читає з відстеженням (`RegistryStore.cs:1216-1232`) — у GET зрізу `SaveChanges` немає, кількість відстежених ≤ рядків; фільтр `Accepts`/`RegistryDefId` зберігає колишню межу «лише записи цілі колонки». Логічно видалені записи тепер теж дають код (колишній `ListEntriesAsync` теж їх не відсіював — `RegistryStore.cs:240-246` без фільтра `IsDeleted`). Файловий перетин із `lane/analiz/perf-slice` (`GetTableSliceHandler.cs`).

**Обґрунтування:** точкове читання за наявним пакетним методом порту; альтернатива «кеш реєстру через `IRegistryEntryCache`» відкинута — кешував би ті самі 50 000 записів у пам'яті кожного вузла.

**Рішення PO:** не потрібне.

**Впевненість:** підтверджено читанням; частота на Land — потребує перевірки (скільки текстових правил на Lookup-колонках груп).

**Залежності:** після злиття `lane/analiz/perf-slice`.

---

## Порядок впровадження

Усі знахідки мають неперетинні множини файлів продукту (крім названих lane-перетинів), тому:

- **Група А (паралельно, одразу):** P-1 (`ReferenceResolver.cs` + новий тест Expressions), P-2 (`RecalculationPlanBuilder.cs`, `RecalculationService.cs`, `RecalculationPlanBuilderIndexTests.cs`), P-3 (`TableValidation.cs` + новий тест), P-5 (`ValidationEngine.cs` + новий тест), P-7 (`TableDiscussionQueries.cs` + новий тест; лише після відповіді PO).
- **Група Б (паралельно з А):** P-4 (`PublishChecks.cs`, `TopologicalSorter.cs`, `PublishChecksTests.cs`, `TopologicalSorterTests.cs`). Не перетинається з А за файлами; логічно підсилюється P-1.
- **Група В (після злиття `origin/lane/analiz/perf-slice`):** P-6 (`DocumentReadScope.cs`, `AccessDecisionService.cs`, новий `ProjectReadSnapshots.cs`) і P-8 (`GetTableSliceHandler.cs`, тест) — між собою не перетинаються, паралельно.
- Тести з бюджетом часу (P-4 (б), (г)) — правило «20/20 локально» з `CLAUDE.md`.

## Відкинуто

- **«`doc.TableInstance` без індексу з провідним `DocumentId`»** (чернетка попереднього аудитора) — не підтверджено читанням: схему індексів дають міграції EF + `Sql/*.sql`, а вирішити без живої бази не можна; це зона звіту `02-data-layer.md`.
- **«Перелік дозволів: усе в пам'ять на кожну сторінку»** (чернетка) — `PermitQueryHandlers.cs:117-131` фільтрує в пам'яті переліки за типами, але обсяг обмежений переліками типів дозволів; для них є сторож `PermitRuntimeRowsTests.QueryCount`. Без заміру — не знахідка.
- **Зріз ~21 звернення на GET** — відомо (`docs/build/MS-01-BASELINE.md` §3.1, сторож `TableSliceMethodologyQueryCountTests`), у роботі lane `perf-slice`/`perf-cellread`.
- **N+1 методологій у PATCH** — lane `perf-patch` (`096e94d0a`).
- **Повторні завантаження мапи застарілості** — AN-155e/AN-155f; **холодний старт** — AN-156/AN-155d; **`ext.RawDataPoint`** — D-240 / R2.
- **`BuildProfileAsync` — 1–3 запити на кожен запит** — свідомо (D-443 debt, AN-138: коментар `AccessDecisionService.cs:43-49`).
- **`IMemoryCache` без `SizeLimit`** — свідомо (пояснення біля `AddMemoryCache`, `MetadataCache.cs:210-212`); холодні ключі злиті `SingleFlight` (RD-05).
- **Журнал змін комірок: `LIKE '%q%' OR …`** (`AuditReader.cs:466-478`) — свідомо (UI-38), обмежено вікном часу й `TOP`/курсором.
- **`JobProgress.Message LIKE`** (`JobProgressStore.cs:363-373, 727-746`) — щогодинне зведення, обмежено `UpdatedAt >= since`.
- **Поштучний запис після відмови батча в матеріалізації** (`MaterializeCollectedDataJob.cs:405-424`) — свідомо (F1-03/L3-02), лише на шляху помилки.
- **Сповіщення про подання: профіль на кожного одержувача** (`SheetWorkflowNotifier.cs:175-183`) — обмежено `Capped(...)`.
- **`metadata.GetAsync` у циклах** (`ValidateDocumentHandler.cs:76`, черга погоджень, `CrossPeriodFanOut.cs:241` тощо) — кешоване читання знімка, не звернення до БД.
- **Можливе перевищення 2100 параметрів** у `TemplateVersionStore.ReplaceFormulaDependenciesAsync` (`formulaIds.Contains` за EF 10 — кілька параметрів, коментар `RecalculationJob.cs:1089`) на шаблоні з > 2100 формулами — це коректність, не продуктивність, і читанням не встановлюється (залежить від поведінки EF 10 на великих колекціях) — **потребує перевірки на стенді**; передано на розгляд звіту `02-data-layer.md`.

## Перевірено й у порядку

- Розбір формул раз на прогін перерахунку (`RecalculationService.cs:643-678`, сторож `CountingFormulaEngine.ParseCalls`).
- Граф залежностей версії — у кеші метаданих (`TemplateVersionStore.cs:1214-1232`, L2b); індекс виробників у рантаймі (`RecalculationPlanBuilder.SnapshotIndex`).
- Пакетні читання: рядки всіх таблиць документа (Q-166, `RecalculationService.cs:497-506`), зрізи «Перевірити» (Q-165), стани переліку документів (Q-167, `DocumentStore.cs:537-579`).
- Черга погоджень: версія періоду — кешоване `VersionOfAsync` (`ProjectVersionTimelineService.cs:27`), знімки мемоїзовані в межах запиту.
- Журнал аудиту: keyset-сторінки за кластерним ключем, вибір архівного плану пробником (`AuditReader.cs:230-347`).
- CSV-імпорт довідника: коди Lookup пакетом (`RegistryEntryCsvHandlers.cs:796-816`, сторож `RegistryEntryImportQueryCountTests`).
- Експорти (аудит, сирі дані PI, довідники) — потоком зі стелями рядків (`AuditController.cs:256,309`, `PiRawDataController.cs:116`, `RegistryRowsController.cs:158`).
- Sync-over-async: `git grep "\.Result\b|\.Wait\(\)|GetAwaiter\(\)\.GetResult"` у області — порожньо; фонові цикли — `PeriodicTimer`/`Task.Delay` з налаштованими інтервалами.
- `MetadataCache` — `SingleFlight` на промах, токен інвалідації до читання (Y6-01).

## Самоперевірка

Кожен `file:line` перевідкрито (`Read`/`sed -n`/`grep -n` на `f446e9b29`):

- **P-1:** `ReferenceResolver.cs:135-183` (FindTable; рядок 137 — `ToList()` всіх таблиць), `:185-210` (ResolveColumn, `:201` FirstOrDefault) — збігається дослівно; `SliceEvaluationContext.cs:134` (створення), `:328` (`_resolver.Resolve`) — так. Типи `SheetDef`/`TableDef`/`ColumnDef` — `Ecr.Domain.Entities.Configuration` (уже в `using`), `TableDef.Code`/`SheetDef.Code`/`ColumnDef.Code` — `string` (перевірено), `ReferenceEqualityComparer.Instance` придатний для `Dictionary<TableDef,…>` (контраваріантність `IEqualityComparer<in T>`). Тест падає без фіксу: ~10 КБ алокацій на виклик × 10 000 ≫ 2 МБ. Не дублює: `git grep -i "ReferenceResolver"` у `docs/build` — згадок про продуктивність немає.
- **P-2:** `RecalculationPlanBuilder.cs:100-103`, `:130-143` — дослівно; `RecalculationService.cs:2294`, `:2336`, `:553`, `:147` — так. Єдиний споживач `DependentsOf` у `src` — `RecalculationService.cs:147` (`git grep`). `CellEdgeCount` — `internal` у `Ecr.Application`, видимий тестам (`Ecr.Application.csproj:22`). Наявний тест `:58` із порівнянням порядку ЗМІНЮЄТЬСЯ на множини (інакше впав би) — внесено як крок 3. Новий тест без фіксу дає 5000 ≠ 0.
- **P-3:** `TableValidation.cs:72-80`, `:157-209` (поле `:170-173`, `ForRow` `:178-179`, лінійний пошук `:197`) — дослівно; `ValidateDocumentHandler.cs:133`, `SubmitSheetHandler.cs:589` — так. `TableValidation` — `internal static`, `RunAsync` — `public static` (досяжний із тестів). Тест падає без фіксу через ~1,5 ГБ алокацій > 200 МБ.
- **P-4:** `PublishChecks.cs:112`, `:124`, `:134-142`, `:228-276` (цикл `:265-272`) — дослівно; `TopologicalSorter.cs:28-60` — дослівно; `FormulaEngine.cs:139`; `PublishTemplateVersionHandler.cs:82-84`, `:98`, `:127` — так. `ProducerIndex.Build(TableDef, Dictionary<int,(TableDef Table, FormulaDef Formula)>)` і `Candidates(string?, int?)` — сигнатури звірено (`RecalculationPlanBuilder.cs:282`, `:322`); `ProducerIndex` — `internal sealed`, вкладений у `public static class RecalculationPlanBuilder` того ж збирання. Порядок виходу сортувальника доведено еквівалентним (вставка в `remaining` без подальших додавань → перебір у порядку вставки) і стережеться диференційним тестом. Нюанс `formula?.SetEvaluationOrder(order++)` (аргумент не обчислюється для `null`) — збережено.
- **P-5:** `ValidationEngine.cs:164`, `:200`, `:122-126`, `:74-87` — дослівно; scoped-реєстрація `Application/DependencyInjection.cs:164`; `ParseResult` — `Ecr.Expressions.Parsing` (повна кваліфікація в diff). `CountingFormulaEngine` — `public sealed`, `Ecr.Application.Tests.Recalculation`. Без фіксу `ParseCalls` = 501/300 ≠ 2/1.
- **P-6:** `DocumentReadScope.cs:35-43`, `:45-75`, `:123-124` — дослівно; присвоєння структурних мап лише в конструкторі (`grep` присвоєнь — рядки 57-73), назовні списки не віддаються (`:358`, `:375` будують нові). `AccessDecisionService.cs:1050`, `:1067-1109` (цикл `:1093-1106`), `:1145`, `:2126`, `:3478-3494` — так. `ConditionalWeakTable.GetValue/AddOrUpdate/TryGetValue` — API .NET 10. Тести падають без фіксу (алокації; `Assert.Same`).
- **P-7:** `TableDiscussionQueries.cs:176` (мін. 2 символи), `:190-213` — дослівно; `TableDiscussionAccess.cs:165-185` — так; `EditAssignmentManagementStore.cs:104-117` (`Contains` → `LIKE`) — так. Тест-рівність N=81 vs N=200 без фіксу розходиться.
- **P-8:** `GetTableSliceHandler.cs:702-713` — так (`registries`/`groups` — поля конструктора `:89`, `:91`); `IPermitGroupResolver.IsGroup/Accepts/EnsureFreshAsync` — існують (`IPermitGroupResolver.cs:49-…`); `IRegistryStore.FindEntriesByIdsAsync(IReadOnlyCollection<long>, CancellationToken)` — `IRegistryStore.cs:407`; `RegistryEntry.RegistryDefId` — використовується в `RegistryStore.cs:242`. Значення Lookup у `row.Cells` — `long` (`ConditionalFormatEvaluator.WithEntryCode`, `value is long id`).
- **Дублі:** перевірено WORK-QUEUE (AN-13, AN-155a…f, AN-156), KNOWN-ISSUES/OPEN-ITEMS/TODO-REMAINING, `docs/build/perf/*`, `10-decisions.md` (D-240, D-395, D-443…D-465), диференції чотирьох `lane/analiz/perf-*` — жодна з P-1…P-8 не збігається.
- **Вилучено/виправлено під час перевірки:** кандидат «кеш розбору за вузлом AST» для P-1 замінено на індекс знімка (вузли — `record` з рівністю за значенням → колізії); кандидат «поштучний запис матеріалізації» і «JobProgress LIKE» перенесено у «Відкинуто» (шлях помилки / обмежене вікно); кандидат «2100 параметрів у ReplaceFormulaDependenciesAsync» не включено як знахідку продуктивності (не встановлюється читанням); перелік дозволів і індекс `TableInstance` з чернетки — у «Відкинуто».
