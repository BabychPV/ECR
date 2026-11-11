# 04 — Коректність бізнес-логіки (Domain / Application / Calculations / Expressions / Excel / Jobs)

**Область:** `src/Ecr.Domain`, `src/Ecr.Application`, `src/Ecr.Calculations`, `src/Ecr.Expressions`, `src/Ecr.Adapters.Excel`, `src/Ecr.Infrastructure/Jobs`. Де дефект бізнес-правила фізично живе у сховищі (`src/Ecr.Infrastructure/Persistence/*`), знахідку наведено з точним місцем там.
**SHA `main`:** `f446e9b29`. **Дата:** 2026-11-11. Лише читання, нічого не збиралося й не запускалося.
**✎ 2026-11-11 (рішення PO, `00-PLAN.md` §4):** C-1, C-5, C-10, C-11 переписано за рішеннями PO і перевірено на `main` `39aea41d9`; решта знахідок — без змін (аналіз на `f446e9b29`).

**Охоплено повністю:** рушій формул (`Evaluator`, `TemplateFunctions`, `StrictDecimalArithmetic`, `SliceEvaluationContext`, `RecalculationService` фаза формул); стан періоду (`PeriodStateCalculator`, `YearGraceWindow`); виняток «місяць відкрито для документа» (`DocumentPeriodReopen*`, усі споживачі правила); Workflow (Submit/Approve/Recall/Reopen, `ClosedPeriodGuard`, `SubmissionPeriodSelector`); історія дозволів (j) (`PermitTimeline`, `SaveCellSplitsHandler`, `PermitHistoryHandlers`, `PatchCellsHandler.PermitValidity`); імпорт/експорт Excel (`ImportDiffBuilder`, `ExcelImporter` apply, `ExcelExporter.WriteValue/WriteFormulas`); видалення аркушів у міграції версії (`DocumentVersionMigrationStore.WorkflowSql/InstancesSql`).
**Поверхово:** `GenericCalculationModule`, `CalculationOrchestrator`, `RecalculationJob` (лише гейти періодів), `PeriodStateJob`, `CollectedFold`/`MaterializeCollectedDataJob`, `RegistrySyncJob`, курсори черги погодження й аудиту, `NotificationJob`.
**Не охоплено:** `SourceEventSyncJob` (2196 рядків) детально, `RowWindowFetchJob`, звіти (`Reporting/*`), методологічні правила категорій поза F13 #806.

Частину напрямів (рушій, міграція+Excel, workflow, PI/дозволи) спершу прочесали чотири допоміжні агенти; кожну знахідку нижче перевірено в коді заново (див. «Самоперевірка»).

## Зведена таблиця

| ID | Важливість | Категорія | Назва | Зусилля | Ризик зміни | Рішення PO |
|---|---|---|---|---|---|---|
| C-1 | Висока | correctness | Формула з помилкою: значення не перезаписується, залежні не перераховуються; позначка в сітці, група «Помилки формул», подія журналу, блок подання (✎ PO 11.11) | L | високий | отримано 11.11 |
| C-2 | Висока | data-integrity | Міграція версії, що прибирає аркуш із копіями, падає на FK і лишає проєкт під маркером міграції | S | середній | ні |
| C-3 | Висока | correctness | Застосування імпорту Excel блокує й перевіряє стан ОРИГІНАЛУ аркуша замість копії | M | середній | ні |
| C-4 | Висока | correctness | Імпорт відмовляє всю книгу через комірки з формулою комірки/рядка (FormulaScope.Cell/Row) у вхідній колонці | S | низький | ні |
| C-5 | Висока | correctness | `ColumnDef.DefaultValue` у формулах, валідації, методологіях і зв'язках сервера (02b §6.3; ✎ PO 11.11 «а») | M | середній | отримано 11.11 |
| C-6 | Середня | correctness | Ціль подання (D-441) ігнорує виняток «місяць відкрито для документа» — кнопка «Подати» не пропонує цей місяць | S | низький | ні |
| C-7 | Середня | correctness | Відкликання (Recall) перевіряє право подання без коду аркуша, періоду й участі (D-443/D-214) — подавач не може відкликати | M | середній | ні |
| C-8 | Середня | correctness | Reopen аркуша вирішує право ДО транзакції (TOCTOU, той самий клас, що R5-W1/W1-05 у Approve) | S | низький | ні |
| C-9 | Середня | data-integrity | Скасування дозволу шапки лишає частини поділу, які згодом зараховуються як поділ для іншого дозволу | S | низький | ні |
| C-10 | Середня | correctness | Поділ дозволу колонки без місяця: одиниця — період комірки (квартал/рік), не «місяць» із `PeriodKey.Sequence` (✎ PO 11.11) | M | середній | отримано 11.11 |
| C-11 | Середня | correctness | Підтримати `[Period:-n].[T].[WHERE …]`: умова й значення — з рядків зсунутого періоду, читачі будяться змінами джерела (✎ PO 11.11 «б») | M | середній | отримано 11.11 |
| C-12 | Середня | correctness | Незмінена Lookup-комірка відхиляє весь імпорт, якщо імпортер не бачить реєстр (Deny / без Registry.View) | M | середній | ні |
| C-13 | Низька | data-integrity | Лічильники посилань запису довідника не бачать `doc.PermitSegment`/`doc.CellPermitPart` — дозвіл з історії можна м'яко видалити, «де використовується» показує 0 | S | низький | ні |

---

## Знахідки

### C-1 — Висока — Формула, що тепер дає помилку, лишає в комірці старе число, і залежні формули рахують від нього
**✎ рішення PO 11.11:** схема координатора «так, блокувати подання» (`00-PLAN.md` §4). (1) Значення комірки з помилкою НЕ перезаписується — лишається останнє правильне («нічого не оновлено, як транзакція»); формули, що від неї залежать, від неї НЕ перераховуються (лишають свої значення). (2) Сітка позначає комірку «помилка розрахунку» (причина, з якого часу) і залежні — «не оновлено через помилку в <комірка>». (3) Панель проблем розрахунку документа (`GetCalculationIssuesHandler`, AN-69) отримує групу «Помилки формул» (комірка, формула, причина, вхідні значення, перехід до комірки); зникає сама після успішного перерахунку. (4) Журнал: тип події «Помилка розрахунку» у вкладці Cell changes (з фільтром) — запис при появі й при усуненні, без дублів, поки та сама помилка триває. (5) Помилка формули БЛОКУЄ подання аркуша — так само, як PERMIT-SPLIT. Варіанти «очищати» / «`ValueString='#DIV/0'`» попередньої редакції відкинуто. Нове рішення — `D-475` (крок C-1g).

**Категорія:** correctness
**Де:**
- Гілка помилки: `src/Ecr.Application/Recalculation/RecalculationService.cs:1383-1389` (`if (result.Value.IsError) { continue; }`); гілка `Null` — `:1397-1410`; оновлення контексту — `:1446` (`values[key] = value;`), до якого гілка помилки не доходить, тож у `values` лишається число зі сховища (`LoadPeriodAsync`, `:2007-2008`).
- Перегляд рядка: `src/Ecr.Application/Recalculation/RowLocalPreview.cs:148-153` (`values.Remove(key)` — залежні бачать порожнє, тобто прев'ю й фонова задача розходяться).
- Усі читання комірок контекстом ідуть через один метод: `SliceEvaluationContext.Value` (`src/Ecr.Application/Recalculation/SliceEvaluationContext.cs:546-559`), до нього приходять `Read` (`:324-378`, діапазони `:359-363`, предикати `:438`) і `GetCell` (`:381-382`).
- Блокування подання PERMIT-SPLIT: `src/Ecr.Application/Validation/PermitCoverageIssues.cs:332-344` (Error `SplitRuleCode`) → `src/Ecr.Application/Workflow/SubmitSheetHandler.cs:625` (`blocking.AddRange(... Severity == Error)`) → `:628-667` (`ECR-SUB-4221`, `validationBlocked`); у «Перевірити» — `src/Ecr.Application/Documents/ValidateDocumentHandler.cs:169-172`. Перерахунок аркуша під винятковим блокуванням подання — `SubmitSheetHandler.cs:458-460` (`RecalculateSheetUnderSubmitLockAsync`), порт — `src/Ecr.Application/Recalculation/ISubmitRecalculation.cs:38-39`.
- Панель проблем: `src/Ecr.Application/Documents/GetCalculationIssuesHandler.cs:53-113`, DTO `src/Ecr.Application/Calculations/Dto/CalculationDtos.cs:113-115`; клієнт `src/Ecr.Web/src/features/documents/useUnmatchedRows.ts:37-68`, банер `src/Ecr.Web/src/features/documents/UnmatchedRowsBanner.tsx:79`, монтування `src/Ecr.Web/src/pages/DocumentPage.tsx:749-754`, `:907-917`, перехід до комірки `DocumentPage.tsx:509-514` (`revealRow(tableInstanceId, rowKey, columnCode)`).
- Журнал: `aud.CellChange.Origin nvarchar(32)` (`src/Ecr.Infrastructure/Persistence/Sql/11-audit-tables.sql:53`), `Reason nvarchar(1000)` (`:58`); походження — `src/Ecr.Application/Documents/CellChangeOrigins.cs:26-150`; фільтр вкладки — `src/Ecr.Infrastructure/Persistence/AuditReader.cs:442` (`Origin = @origin`), клієнт `src/Ecr.Web/src/features/audit/api.ts:25-34`, `src/Ecr.Web/src/features/audit/CellChangesTable.tsx:71-91`, `src/Ecr.Web/src/pages/admin/AuditPage.tsx:324-331`.
- Зразок розрідженої позначки на комірці зі зрізом: D-461 `FormulaOverrideSliceDto` (`src/Ecr.Application/Documents/Dto/TableSliceDto.cs:85`, `:111-121`), читання в зрізі `src/Ecr.Application/Documents/GetTableSliceHandler.cs:495-505`, `:516-571`; сітка `src/Ecr.Web/src/features/grid/DocumentGrid.tsx:3704-3708`, `:3788-3827`, CSS `src/Ecr.Web/src/shared/theme/cell-states.css:390-425`.
- Зразок «поточних проблем» без партиціювання: `calc.CalculationIssue` (міграція `src/Ecr.Infrastructure/Persistence/Migrations/20261104084809_CI01_CalculationIssue.cs`, конфігурація `src/Ecr.Infrastructure/Persistence/Configurations/CalculationsConfiguration.cs:582-600`).

**Проблема:**
```csharp
// RecalculationService.cs:1386
if (result.Value.IsError)
{
    continue;
}
```
Помилка не пишеться і не потрапляє в стан прогону: у `values` лишається старе обчислене число зі сховища, і залежні формули того ж прогону рахують від нього (`D = [C]*2 + [E]` після зміни `E` дає нове число зі старого `C`). Користувач не бачить жодного сигналу, подання проходить. Перегляд рядка (`RowLocalPreview.cs:148-153`) робить інакше (`values.Remove(key)` → залежні бачать порожнє). Рішення PO вимагає третьої поведінки: не перераховувати ні саму комірку, ні її залежних, показати це й заблокувати подання.

**Сценарій відтворення:** шаблон: `C = [A]/[B]`, `D = [C]*2 + [E]`. Ввести A=10, B=2, E=1 → C=5, D=11. Змінити B на 0 і E на 2.
Фактично: C = `#DIV/0` → пропущено, у базі лишається 5; D читає з контексту старе C=5 → 12 → записується; сітка: C=5, D=12, жодної позначки; «Подати» проходить.
Очікувано (PO 11.11): C=5 і D=11 лишаються (D не перераховано від помилки); C позначено «помилка розрахунку: ділення на нуль (#DIV/0), з <час>», D — «не оновлено через помилку в C»; у панелі — група «Помилки формул» з формулою `[A]/[B]` і входами `A=10; B=0`; у Cell changes — подія «Помилка розрахунку» для C (одна); «Подати» → 422 `ECR-SUB-4221` з рядком `FORMULA-ERROR`. Після B=2: C=5, D=12 пишуться, позначки й рядок панелі зникають, у журналі — «Помилку розрахунку усунуто».

**Вплив:** будь-який документ, де вхід формули стає нулем (ділення на «кількість», «години роботи»), зникає запис довідника (`#REF` у `REGFIELD`) або формула вичерпує бюджет (`#BUDGET`). Зараз — тихо хибні числа в похідних комірках і звітах без жодного сигналу; подання таких аркушів проходить.

**Рішення щодо зберігання (обґрунтування вибору):**
- **Нова таблиця `calc.FormulaError`, не колонки `doc.CellValue`.** Колонки в `doc.CellValue` вимагали б змінити табличний тип `MERGE` (`NormalizedCellStore.ApplyBatchAsync`) і всі читачі зрізу; запис стану помилки змінював би рядок значення (`RowVersion`, аудит). Окрема таблиця «лише поточного стану» — той самий прийом, що `calc.CalculationIssue` (CI01): без зовнішніх ключів, без партиціювання, видаляється документом.
- **Без партиціювання й без змін архіву.** Таблиця розріджена (лише комірки з помилкою та їх залежні), зовнішніх ключів на `doc.TableRow` немає, тож `TRUNCATE … WITH (PARTITIONS)` архівації (`03-archive-proc.sql:624`) її не зачіпає, і `07-partition-tables.sql` (перелік `:63-76`, перевірка `:300-306`) не змінюється. Затверджений аркуш помилок не має (помилка блокує подання; затверджений аркуш перерахунок не пише — `RecalculationService.cs:1323-1327`), тож у архівованих роках рядків практично немає; залишок (рядок без живого рядка/екземпляра) читачі відкидають з'єднанням з `doc.TableRow` (крок 4).
- **Нуль нових звернень, коли помилок немає.** Прапорець «в екземплярі є стан помилок» додається до ВЖЕ наявного запиту екземплярів як `EXISTS` (`RowStore.cs:85`, `:797`) — та сама команда SQL. Сам стан читається одним запитом лише коли прапорець є. Тести фіксованої кількості запитів не змінюються (перелік у «Ризики»).
- **Журнал — нові походження в `aud.CellChange`, без міграції.** Вкладка Cell changes уже фільтрує за `Origin` (`AuditReader.cs:442`, `AuditPage.tsx:324-331`). Стовпець `Origin nvarchar(32)` вміщує `CalcError`/`CalcErrorResolved` так само, як `ApproverEdit`/`FormulaOverride` додавались без міграції (`CellChangeOrigins.cs:80-81`). Значення комірки подія не змінює (`OldValue = NewValue`), причина — у `Reason`. Читачі журналу, що відсікають `Recalculation` як «не правку», відсікатимуть і ці походження (крок C-1d).

**Зміна — покроково.** Підкроки виконуються в одній лінії послідовно (C-1a → C-1g), один PR.

#### C-1a. Схема, сутність, сховище, прапорець екземпляра
1. Новий файл `src/Ecr.Domain/Entities/Calculations/FormulaError.cs` (за зразком `CalculationIssue.cs`):
```csharp
// src/Ecr.Domain/Entities/Calculations/FormulaError.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// ✎ C-1 / D-475: ПОТОЧНИЙ стан помилки формули комірки (<c>calc.FormulaError</c>): власна помилка обчислення
/// (<see cref="ErrorKind"/>) або «не оновлено через помилку в іншій комірці» (<see cref="BlockedKind"/>).
/// </summary>
/// <remarks>
/// ⛔ Значення комірки тут НЕ зберігається: у <c>doc.CellValue</c> лишається останнє правильне (рішення PO 11.11).
/// Рядок живе, доки помилка триває; успішний перерахунок його видаляє. Зовнішніх ключів немає навмисно (архівація
/// звільняє партиції <c>doc.TableRow</c> через TRUNCATE), читачі відкидають рядки без живого рядка таблиці.
/// </remarks>
public sealed class FormulaError : Entity<long>
{
    public const string ErrorKind = "Error";
    public const string BlockedKind = "Blocked";
    public const int MaxRowKeyLength = 100;
    public const int MaxErrorCodeLength = 16;
    public const int MaxInputsLength = 2000;

    private FormulaError() { }

    public FormulaError(
        long documentId, int periodKey, long tableInstanceId, int tableDefId, long? sheetCopyId,
        long tableRowId, string rowKey, int columnDefId, int formulaDefId, string kind, string errorCode,
        string? inputsJson, int? sourcePeriodKey, int? sourceTableDefId, string? sourceRowKey, int? sourceColumnDefId,
        DateTime since)
    {
        ArgumentException.ThrowIfNullOrEmpty(rowKey);
        ArgumentException.ThrowIfNullOrEmpty(errorCode);
        if (kind is not (ErrorKind or BlockedKind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Kind is Error or Blocked.");
        }

        DocumentId = documentId; PeriodKey = periodKey; TableInstanceId = tableInstanceId; TableDefId = tableDefId;
        SheetCopyId = sheetCopyId; TableRowId = tableRowId; RowKey = rowKey; ColumnDefId = columnDefId;
        FormulaDefId = formulaDefId; Kind = kind; ErrorCode = errorCode; InputsJson = inputsJson;
        SourcePeriodKey = sourcePeriodKey; SourceTableDefId = sourceTableDefId; SourceRowKey = sourceRowKey;
        SourceColumnDefId = sourceColumnDefId; Since = since;
    }

    public long DocumentId { get; private set; }
    public int PeriodKey { get; private set; }
    public long TableInstanceId { get; private set; }
    public int TableDefId { get; private set; }
    public long? SheetCopyId { get; private set; }
    public long TableRowId { get; private set; }
    public string RowKey { get; private set; } = null!;
    public int ColumnDefId { get; private set; }
    public int FormulaDefId { get; private set; }
    public string Kind { get; private set; } = null!;

    /// <summary>Код помилки (<c>#DIV/0</c>, <c>#REF</c>, <c>#BUDGET</c>…); для Blocked — код джерела.</summary>
    public string ErrorCode { get; private set; } = null!;

    /// <summary>Вхідні значення, що дали помилку (JSON, до 20 записів); лише для Error.</summary>
    public string? InputsJson { get; private set; }

    public int? SourcePeriodKey { get; private set; }
    public int? SourceTableDefId { get; private set; }
    public string? SourceRowKey { get; private set; }
    public int? SourceColumnDefId { get; private set; }

    /// <summary>Відколи помилка триває (UTC); не змінюється, поки той самий стан повторюється.</summary>
    public DateTime Since { get; private set; }
}
```
2. `src/Ecr.Infrastructure/Persistence/Configurations/CalculationsConfiguration.cs` — після класу `CalculationIssueConfiguration` (рядок `:600`) додати:
```csharp
/// <summary>✎ C-1 / D-475: конфігурація <see cref="FormulaError"/> — поточний стан помилок формул (без партицій, без FK).</summary>
public sealed class FormulaErrorConfiguration : IEntityTypeConfiguration<FormulaError>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<FormulaError> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("FormulaError", "calc", t =>
            t.HasCheckConstraint("CK_FormulaError_Kind", "Kind IN (N'Error', N'Blocked')"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.RowKey).HasMaxLength(FormulaError.MaxRowKeyLength).IsRequired();
        builder.Property(x => x.SourceRowKey).HasMaxLength(FormulaError.MaxRowKeyLength);
        builder.Property(x => x.Kind).HasMaxLength(16).IsRequired();
        builder.Property(x => x.ErrorCode).HasMaxLength(FormulaError.MaxErrorCodeLength).IsRequired();
        builder.Property(x => x.InputsJson).HasMaxLength(FormulaError.MaxInputsLength);
        builder.Property(x => x.Since).HasColumnType("datetime2(3)").IsRequired();

        // Один стан на комірку; пошук і заміна — за адресою комірки.
        builder.HasIndex(x => new { x.PeriodKey, x.TableRowId, x.ColumnDefId })
               .IsUnique()
               .HasDatabaseName("UX_FormulaError_Cell");

        // EXISTS у запиті екземплярів (RowStore) і читання зрізу — за екземпляром.
        builder.HasIndex(x => new { x.PeriodKey, x.TableInstanceId })
               .HasDatabaseName("IX_FormulaError_Instance");

        // Панель проблем і «Перевірити» — документ × період × копія.
        builder.HasIndex(x => new { x.DocumentId, x.PeriodKey, x.SheetCopyId })
               .HasDatabaseName("IX_FormulaError_Document");
    }
}
```
3. `src/Ecr.Infrastructure/Persistence/EcrDbContext.cs` — після `:204` (`DbSet<CalculationIssue> CalculationIssues`) додати `public DbSet<FormulaError> FormulaErrors => Set<FormulaError>();` (з `/// <summary>✎ C-1: поточний стан помилок формул.</summary>`). Конфігурацію підхоплює `ApplyConfigurationsFromAssembly` (`EcrDbContext.cs:285`).
4. Міграція (токен міграцій, див. «Порядок»): `dotnet ef migrations add FE01_FormulaError` (проєкт `src/Ecr.Infrastructure`, як CI01). Очікуваний `Up`:
```csharp
migrationBuilder.CreateTable(
    name: "FormulaError",
    schema: "calc",
    columns: table => new
    {
        Id = table.Column<long>(type: "bigint", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
        DocumentId = table.Column<long>(type: "bigint", nullable: false),
        PeriodKey = table.Column<int>(type: "int", nullable: false),
        TableInstanceId = table.Column<long>(type: "bigint", nullable: false),
        TableDefId = table.Column<int>(type: "int", nullable: false),
        SheetCopyId = table.Column<long>(type: "bigint", nullable: true),
        TableRowId = table.Column<long>(type: "bigint", nullable: false),
        RowKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
        ColumnDefId = table.Column<int>(type: "int", nullable: false),
        FormulaDefId = table.Column<int>(type: "int", nullable: false),
        Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
        ErrorCode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
        InputsJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
        SourcePeriodKey = table.Column<int>(type: "int", nullable: true),
        SourceTableDefId = table.Column<int>(type: "int", nullable: true),
        SourceRowKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
        SourceColumnDefId = table.Column<int>(type: "int", nullable: true),
        Since = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
    },
    constraints: table =>
    {
        table.PrimaryKey("PK_FormulaError", x => x.Id);
        table.CheckConstraint("CK_FormulaError_Kind", "Kind IN (N'Error', N'Blocked')");
    });
migrationBuilder.CreateIndex(name: "UX_FormulaError_Cell", schema: "calc", table: "FormulaError",
    columns: new[] { "PeriodKey", "TableRowId", "ColumnDefId" }, unique: true);
migrationBuilder.CreateIndex(name: "IX_FormulaError_Instance", schema: "calc", table: "FormulaError",
    columns: new[] { "PeriodKey", "TableInstanceId" });
migrationBuilder.CreateIndex(name: "IX_FormulaError_Document", schema: "calc", table: "FormulaError",
    columns: new[] { "DocumentId", "PeriodKey", "SheetCopyId" });
```
`Down` — `DropTable("FormulaError", "calc")`. Таблиця нова й порожня — звичайний `CreateIndex`, без `ONLINE`/`INDEXPROPERTY` (це правило для індексів на великих наявних таблицях). Схема партицій: **немає** (обґрунтування вище). `Designer.cs` і `EcrDbContextModelSnapshot.cs` генерує EF.
5. Новий порт `src/Ecr.Application/Ports/IFormulaErrorStore.cs`:
```csharp
// src/Ecr.Application/Ports/IFormulaErrorStore.cs
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>✎ C-1 / D-475: поточний стан помилок формул (<c>calc.FormulaError</c>).</summary>
public interface IFormulaErrorStore
{
    /// <summary>Стан комірок екземплярів одного періоду — ОДНИМ запитом.</summary>
    public Task<IReadOnlyList<FormulaErrorRow>> ReadInstancesAsync(
        IReadOnlyCollection<long> tableInstanceIds, PeriodKey periodKey, CancellationToken ct);

    /// <summary>
    /// Замінює стан комірок: <paramref name="upserts"/> пишуться (попередній рядок тієї ж комірки видаляється),
    /// <paramref name="deletes"/> — видаляються. Видалення — одразу, у транзакції викликача; нові рядки — в контекст
    /// (їх зберігає викликач своїм <c>SaveChanges</c>, як <c>ICalculationIssueStore.ReplaceAsync</c>).
    /// </summary>
    public Task ApplyAsync(
        IReadOnlyCollection<FormulaErrorRow> upserts, IReadOnlyCollection<CellAddress> deletes, CancellationToken ct);

    /// <summary>Стан документа за період і копію (панель проблем, «Перевірити») — ОДНИМ запитом, лише живі рядки.</summary>
    public Task<IReadOnlyList<FormulaErrorView>> ListAsync(
        long documentId, int periodKey, long? sheetCopyId, CancellationToken ct);
}

/// <summary>Рядок стану (для читання й запису).</summary>
public sealed record FormulaErrorRow(
    long DocumentId, int PeriodKey, long TableInstanceId, int TableDefId, long? SheetCopyId,
    long TableRowId, string RowKey, int ColumnDefId, int FormulaDefId, string Kind, string ErrorCode,
    string? InputsJson, int? SourcePeriodKey, int? SourceTableDefId, string? SourceRowKey, int? SourceColumnDefId,
    DateTime Since)
{
    public CellAddress Address => new(new PeriodKey(PeriodKey), TableRowId, ColumnDefId);
}

/// <summary>Рядок стану з кодами для показу (з'єднання з cfg.* і живим doc.TableRow).</summary>
public sealed record FormulaErrorView(
    FormulaErrorRow Row, string TableCode, string ColumnCode, Domain.ValueObjects.LocalizedText ColumnHeaderL10n,
    int RowOrdinal, string? Formula, string? SourceTableCode, string? SourceColumnCode);
```
(`LocalizedText` — той самий тип, що `ColumnDef.HeaderL10n`; перевір простір імен grep'ом `record LocalizedText` і вкажи його в `using`.)
6. Нове сховище `src/Ecr.Infrastructure/Persistence/FormulaErrorStore.cs` (за зразком `CalculationIssueStore.cs`):
   - `ReadInstancesAsync`: `db.FormulaErrors.AsNoTracking().Where(e => e.PeriodKey == periodKey.Value && ids.Contains(e.TableInstanceId))` → `FormulaErrorRow`; порожній набір id — `[]` без запиту.
   - `ApplyAsync`: якщо обидва набори порожні — нічого. Інакше ОДИН сирий `DELETE` за адресами `upserts ∪ deletes`:
```sql
DELETE e FROM calc.FormulaError AS e
JOIN OPENJSON(@keys) WITH (PeriodKey int '$.p', TableRowId bigint '$.r', ColumnDefId int '$.c') AS k
  ON k.PeriodKey = e.PeriodKey AND k.TableRowId = e.TableRowId AND k.ColumnDefId = e.ColumnDefId;
```
     (`db.Database.ExecuteSqlInterpolatedAsync`, `@keys` = `JsonSerializer.Serialize(addresses.Select(a => new { p = a.PeriodKey.Value, r = a.TableRowId, c = a.ColumnDefId }))`; виконується в поточній транзакції контексту — тій самій, що `NormalizedCellStore`), потім `db.FormulaErrors.Add(new FormulaError(...))` на кожен upsert (зберігає викликач).
   - `ListAsync`: `from e in db.FormulaErrors.AsNoTracking() where e.DocumentId == documentId && e.PeriodKey == periodKey && e.SheetCopyId == sheetCopyId join r in db.TableRows.AsNoTracking() on new { P = e.PeriodKey, Id = e.TableRowId } equals new { P = r.PeriodKeyValue, r.Id } where !r.IsDeleted join t in db.TableDefs … join c in db.ColumnDefs … join f in db.FormulaDefs … into fs from f in fs.DefaultIfEmpty() join st in db.TableDefs on e.SourceTableDefId equals (int?)st.Id into sts … join sc in db.ColumnDefs on e.SourceColumnDefId equals (int?)sc.Id into scs … orderby t.Id, r.Ordinal, c.Ordinal select new FormulaErrorView(...)`. Імена властивостей `TableRow` (`PeriodKeyValue`, `IsDeleted`, `Ordinal`) — перевір grep'ом у `src/Ecr.Domain/Entities/Documents/TableRow.cs` перед написанням. Одна команда SQL.
   - `db.TableInstances` у сховищі НЕ використовувати (тоді `SheetCopyQuerySitesTests` не вимагає нового запису в `Reviewed`; `SheetCopyId` береться з самого рядка).
7. `src/Ecr.Infrastructure/DependencyInjection.cs` — після `:184` (`AddScoped<ICalculationIssueStore, CalculationIssueStore>()`) додати `services.AddScoped<IFormulaErrorStore, FormulaErrorStore>(); // ✎ C-1: стан помилок формул`.
8. Прапорець екземпляра — `src/Ecr.Application/Ports/IRowStore.cs:353-354`:
```diff
 public sealed record TableInstanceRef(
-    long TableInstanceId, long DocumentId, int TableDefId, int TemplateVersionId, int PeriodKey, long? SheetCopyId = null);
+    long TableInstanceId, long DocumentId, int TableDefId, int TemplateVersionId, int PeriodKey, long? SheetCopyId = null,
+    bool HasFormulaErrors = false);
```
   і в XML-документації додати `/// <param name="HasFormulaErrors">✎ C-1: в екземплярі є рядки calc.FormulaError (EXISTS у тому самому запиті екземплярів); false — читати стан не треба.</param>`.
9. `src/Ecr.Infrastructure/Persistence/RowStore.cs:85-88` (`ResolveTableInstancesAsync`) і `:797-799` (`GetTableInstancesAsync`) — додати сьомий аргумент у проєкцію (EF перекладає в `CASE WHEN EXISTS(...)` тієї самої команди):
```diff
                     select new TableInstanceRef(
                         instance.Id, instance.DocumentId, instance.TableDefId,
                         EcrDbContext.VersionOf(project.Id, instance.PeriodKeyValue), instance.PeriodKeyValue,
-                        instance.SheetCopyId),
+                        instance.SheetCopyId,
+                        db.FormulaErrors.Any(e => e.PeriodKey == instance.PeriodKeyValue && e.TableInstanceId == instance.Id)),
```
```diff
                   (x, p) => new TableInstanceRef(
                       x.t.Id, x.t.DocumentId, x.t.TableDefId, EcrDbContext.VersionOf(p.Id, x.t.PeriodKeyValue), x.t.PeriodKeyValue,
-                      x.t.SheetCopyId))
+                      x.t.SheetCopyId,
+                      db.FormulaErrors.Any(e => e.PeriodKey == x.t.PeriodKeyValue && e.TableInstanceId == x.t.Id)))
```
   Архівний шлях (`ReadArchivedTableInstancesAsync`) і `:1211` (екземпляри року Q-17) прапорця не дають (`false`).
10. `src/Ecr.Infrastructure/Persistence/DocumentDeletionStore.cs` — поруч із `:264` (`db.CalculationIssues…ExecuteDeleteAsync`) додати `await db.FormulaErrors.Where(e => e.DocumentId == documentId).ExecuteDeleteAsync(ct).ConfigureAwait(false);` (коментар `// ✎ C-1: стан помилок формул документа`). Копії аркуша (`DocumentSheetCopyStore.cs`) і перенос версії (`DocumentVersionMigrationStore.cs`) НЕ змінюються: рядок видаленого екземпляра/рядка відкидає з'єднання з живим `doc.TableRow` (`ListAsync`), колонку чужої версії — зріз (`visibleCodeById`), а повний прогін видаляє невідвідані рядки своїх формул (C-1b, крок 6). Так C-1 не перетинається з лінією L4 (`DocumentVersionMigrationStore.cs`).

#### C-1b. Перерахунок: стан помилок прогону, блокування залежних, запис стану й журналу
1. Новий файл `src/Ecr.Application/Recalculation/FormulaErrorTracker.cs`:
```csharp
// src/Ecr.Application/Recalculation/FormulaErrorTracker.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;

namespace Ecr.Application.Recalculation;

/// <summary>✎ C-1: комірка-джерело недовіри (власна помилка або її корінь для «не оновлено»).</summary>
public sealed record FormulaErrorSource(
    int PeriodKey, int TableDefId, string RowKey, long TableRowId, int ColumnDefId, string ErrorCode);

/// <summary>✎ C-1: підсумок обчислення однієї цілі прогону.</summary>
/// <param name="Kind"><c>null</c> — порахувалася; <see cref="FormulaError.ErrorKind"/> / <see cref="FormulaError.BlockedKind"/>.</param>
public sealed record FormulaCellOutcome(
    CellKey Key, CellAddress Address, int TableDefId, string RowKey, int FormulaDefId, string? Kind,
    string? ErrorCode, FormulaErrorSource? Source, string? InputsJson);

/// <summary>✎ C-1: комірка аркуша з помилкою — для блокування подання й «Перевірити».</summary>
public sealed record FormulaErrorCell(
    int TableDefId, long? SheetCopyId, string RowKey, int ColumnDefId, string Kind, string ErrorCode,
    int? SourcePeriodKey, int? SourceTableDefId, string? SourceRowKey, int? SourceColumnDefId);

/// <summary>
/// ✎ C-1 (рішення PO 11.11, D-475): стан помилок ОДНОГО прогону. Комірка з помилкою та всі, що (транзитивно) її
/// читають, лежать у <see cref="Blocked"/>; контекст бачить читання такої комірки
/// (<see cref="SliceEvaluationContext.BlockedRead"/>), і ціль не перераховується.
/// </summary>
internal sealed class FormulaErrorTracker
{
    /// <summary>Скільки входів помилки зберігається (JSON у <c>calc.FormulaError.InputsJson</c>).</summary>
    public const int MaxInputs = 20;

    public Dictionary<CellKey, FormulaErrorSource> Blocked { get; } = [];

    public Dictionary<CellAddress, FormulaCellOutcome> Outcomes { get; } = [];

    /// <summary>Засіває збережений стан (поточний період — <paramref name="offset"/> 0, сусідній — його зсув).</summary>
    public void Seed(IEnumerable<FormulaErrorRow> rows, int offset)
    {
        foreach (var row in rows)
        {
            var source = row.SourceRowKey is { } sourceRow && row.SourceTableDefId is { } sourceTable
                         && row.SourceColumnDefId is { } sourceColumn && row.SourcePeriodKey is { } sourcePeriod
                ? new FormulaErrorSource(sourcePeriod, sourceTable, sourceRow, 0, sourceColumn, row.ErrorCode)
                : new FormulaErrorSource(row.PeriodKey, row.TableDefId, row.RowKey, row.TableRowId, row.ColumnDefId, row.ErrorCode);
            Blocked[new CellKey(offset, row.TableDefId, row.RowKey, row.ColumnDefId)] = source;
        }
    }

    public void Ok(CellKey key, CellAddress address, int tableDefId, string rowKey, int formulaDefId)
        => Outcomes[address] = new FormulaCellOutcome(key, address, tableDefId, rowKey, formulaDefId, null, null, null, null);

    public void Fail(CellKey key, CellAddress address, int tableDefId, string rowKey, int formulaDefId, string errorCode, string? inputsJson)
    {
        Blocked[key] = new FormulaErrorSource(address.PeriodKey.Value, tableDefId, rowKey, address.TableRowId, address.ColumnDefId, errorCode);
        Outcomes[address] = new FormulaCellOutcome(
            key, address, tableDefId, rowKey, formulaDefId, FormulaError.ErrorKind, errorCode, null, inputsJson);
    }

    public void Block(CellKey key, CellAddress address, int tableDefId, string rowKey, int formulaDefId, FormulaErrorSource source)
    {
        Blocked[key] = source;
        Outcomes[address] = new FormulaCellOutcome(
            key, address, tableDefId, rowKey, formulaDefId, FormulaError.BlockedKind, source.ErrorCode, source, null);
    }
}
```
2. `src/Ecr.Application/Recalculation/SliceEvaluationContext.cs` — недовіра й трасування читань (обидва — в одному методі `Value`, `:546-559`):
```diff
     /// <summary>Значення комірки; відсутня комірка — це <c>null</c>, а не помилка (02b §6.3).</summary>
     private ExpressionValue Value(int tableDefId, string? rowKey, int columnDefId, int periodOffset)
     {
         if (rowKey is null)
         {
             return ExpressionValue.Null;
         }
 
         var key = new CellKey(periodOffset, tableDefId, rowKey, columnDefId);
 
+        // ✎ C-1 (D-475): читання комірки з помилкою формули чи «не оновленої через помилку» — ціль, що її читає, не
+        // перераховується (RecalculationService.Evaluate); запам'ятовується ПЕРШЕ таке джерело.
+        if (BlockedRead is null && Blocked is { Count: > 0 } blocked && blocked.TryGetValue(key, out var source))
+        {
+            BlockedRead = source;
+        }
+
         // ⚠ Комірки немає — `null`, а не помилка: незаповнена комірка це
         // нормальний стан (`ФВ-3.8`), і агрегат над нею має визначений
         // результат. Помилка тут перетворила б порожню таблицю на `#REF`.
-        return _values.TryGetValue(key, out var value) ? value : ExpressionValue.Null;
+        var value = _values.TryGetValue(key, out var found) ? found : ExpressionValue.Null;
+        if (_trace is { Count: < FormulaErrorTracker.MaxInputs } trace)
+        {
+            trace.Add((key, value));
+        }
+
+        return value;
     }
```
   і поруч із `CurrentColumnDefId` (`:320-321`) додати:
```csharp
    /// <summary>✎ C-1: комірки, яким прогін не довіряє (помилка формули або «не оновлено через помилку»).</summary>
    internal IReadOnlyDictionary<CellKey, FormulaErrorSource>? Blocked { get; set; }

    /// <summary>✎ C-1: перше прочитане недовірене джерело поточного обчислення; скидається <see cref="BeginCell"/>.</summary>
    internal FormulaErrorSource? BlockedRead { get; private set; }

    private List<(CellKey Key, ExpressionValue Value)>? _trace;

    /// <summary>✎ C-1: початок обчислення однієї цілі.</summary>
    internal void BeginCell() => BlockedRead = null;

    /// <summary>✎ C-1: записувати прочитані комірки (до <see cref="FormulaErrorTracker.MaxInputs"/>) — лише для повтору цілі з помилкою.</summary>
    internal void BeginTrace() => _trace = [];

    internal IReadOnlyList<(CellKey Key, ExpressionValue Value)> EndTrace()
    {
        var trace = _trace ?? [];
        _trace = null;
        return trace;
    }
```
   (`internal` достатньо: `RecalculationService` і `RowLocalPreview` в тій самій збірці; для тестів `Ecr.Application.Tests` перевір `InternalsVisibleTo` у `src/Ecr.Application/Ecr.Application.csproj` або `AssemblyInfo` — якщо його немає, зроби члени `public`.)
3. `src/Ecr.Application/Recalculation/RecalculationService.cs` — `Evaluate` (`:1340-1469`). Підпис (`:1340-1352`) отримує обов'язковий параметр `FormulaErrorTracker errors` після `upserts`; виклик у `EvaluateTargets` (`:841-844`) передає `errors` (змінна з кроку 4). Тіло циклу `:1370-1389`:
```diff
-            if (IsManualTarget(table.Id, rowKey, rowId, columnDefId, periodKey, stored, foreignColumns, overridden))
-            {
-                continue;
-            }
-
-            context.CurrentTableDefId = table.Id;
-            context.CurrentRowKey = rowKey;
-            context.CurrentColumnDefId = columnDefId;
-
-            var result = formulaEngine.Evaluate(parsed, context);
-
-            // ⛔ Помилка обчислення НЕ записується як значення. `#REF` у
-            // комірці — це не число, і покласти його в `ValueNumeric`
-            // означало б або нуль, або текст у числовій колонці.
-            if (result.Value.IsError)
-            {
-                continue;
-            }
+            var targetKey = new CellKey(0, table.Id, rowKey, columnDefId);
+            var targetAddress = new CellAddress(periodKey, rowId, columnDefId);
+
+            if (IsManualTarget(table.Id, rowKey, rowId, columnDefId, periodKey, stored, foreignColumns, overridden))
+            {
+                // ✎ C-1: ручне значення — не результат формули; стан помилки цієї комірки (якщо був) знімається.
+                errors.Ok(targetKey, targetAddress, table.Id, rowKey, formula.Id);
+                continue;
+            }
+
+            context.CurrentTableDefId = table.Id;
+            context.CurrentRowKey = rowKey;
+            context.CurrentColumnDefId = columnDefId;
+
+            // ✎ C-1: ціль рахується заново — її попередній стан (помилка чи «не оновлено») більше не діє. Порядок
+            // обчислення топологічний, тож джерела цієї цілі вже переоцінені в цьому прогоні.
+            errors.Blocked.Remove(targetKey);
+            context.BeginCell();
+
+            var result = formulaEngine.Evaluate(parsed, context);
+
+            // ✎ C-1 (рішення PO 11.11, D-475): формула прочитала комірку з помилкою (чи «не оновлену через помилку») —
+            // ціль НЕ перераховується від неї: лишається останнє збережене значення, як після транзакції, що не відбулася.
+            if (context.BlockedRead is { } source)
+            {
+                upserts.Remove(targetAddress);
+                errors.Block(targetKey, targetAddress, table.Id, rowKey, formula.Id, source);
+                continue;
+            }
+
+            // ⛔ Помилка обчислення НЕ записується як значення. `#REF` у
+            // комірці — це не число, і покласти його в `ValueNumeric`
+            // означало б або нуль, або текст у числовій колонці.
+            // ✎ C-1: і старе число НЕ стирається — «нічого не оновлено»; помилка йде в стан прогону, залежні формули
+            // бачать її через `Blocked`. Входи — повторним обчисленням зі збором читань (лише на помилці).
+            if (result.Value.IsError)
+            {
+                upserts.Remove(targetAddress);
+                context.BeginTrace();
+                _ = formulaEngine.Evaluate(parsed, context);
+                errors.Fail(
+                    targetKey, targetAddress, table.Id, rowKey, formula.Id,
+                    result.Value.ErrorCode ?? Ecr.Expressions.ExpressionErrors.BadValue,
+                    FormulaErrorInputs.Format(context.EndTrace(), _tablesForInputs));
+                continue;
+            }
+
+            errors.Ok(targetKey, targetAddress, table.Id, rowKey, formula.Id);
```
   `FormulaErrorInputs.Format` — статичний помічник у тому самому файлі `FormulaErrorTracker.cs`:
```csharp
/// <summary>✎ C-1: входи помилки → JSON (до MaxInputs записів, ≤ 2000 символів): таблиця, рядок, колонка, зсув, значення.</summary>
internal static class FormulaErrorInputs
{
    public static string? Format(
        IReadOnlyList<(CellKey Key, ExpressionValue Value)> reads,
        IReadOnlyDictionary<int, Domain.Entities.Configuration.TableDef> tables)
    {
        if (reads.Count == 0) return null;
        var items = reads
            .DistinctBy(r => r.Key)
            .Select(r => new
            {
                tableDefId = r.Key.TableDefId,
                tableCode = tables.TryGetValue(r.Key.TableDefId, out var t) ? t.Code.ToString() : null,
                rowKey = r.Key.RowKey,
                columnDefId = r.Key.ColumnDefId,
                columnCode = t?.Columns.FirstOrDefault(c => c.Id == r.Key.ColumnDefId)?.Code.ToString(),
                periodOffset = r.Key.PeriodOffset,
                value = r.Value.IsNull ? null
                    : r.Value.IsError ? r.Value.ErrorCode
                    : Convert.ToString(r.Value.Value, System.Globalization.CultureInfo.InvariantCulture),
            })
            .ToList();
        var json = System.Text.Json.JsonSerializer.Serialize(items);
        while (json.Length > Domain.Entities.Calculations.FormulaError.MaxInputsLength && items.Count > 1)
        {
            items.RemoveAt(items.Count - 1);
            json = System.Text.Json.JsonSerializer.Serialize(items);
        }
        return json.Length <= Domain.Entities.Calculations.FormulaError.MaxInputsLength ? json : null;
    }
}
```
   `_tablesForInputs` — поле `private Dictionary<int, Domain.Entities.Configuration.TableDef> _tablesForInputs = [];`, яке `RunAsync` присвоює `tables` одразу після `var tables = snapshotIndex.Tables;` (`:475`). (Альтернатива без поля — передати `tables` у `Evaluate` ще одним параметром; обери одне й будь послідовним.)
4. `RunAsync` — читання й засів стану. Одразу перед `var (values, stored) = await LoadValuesAsync(` (`:705`):
```csharp
        // ✎ C-1 (D-475): стан помилок формул. Прапорець — із запиту екземплярів (`TableInstanceRef.HasFormulaErrors`,
        // EXISTS у тій самій команді); без помилок — нуль звернень (храповик FormulaJobFixedCostTests).
        var errors = new FormulaErrorTracker();
        var flagged = instances.Where(i => i.HasFormulaErrors).Select(i => i.TableInstanceId).ToList();
        IReadOnlyList<FormulaErrorRow> persisted = formulaErrors is not null && flagged.Count > 0
            ? await formulaErrors.ReadInstancesAsync(flagged, periodKey, ct).ConfigureAwait(false)
            : [];
        errors.Seed(persisted, offset: 0);
        var loadExtras = new PeriodLoadExtras();
```
   Виклик `LoadValuesAsync` (`:705-706`) і каскадний (`:954-957`) отримують аргумент `extras: loadExtras` (новий останній параметр `PeriodLoadExtras? extras = null` у `LoadValuesAsync` `:1753-1762` і в обох викликах `LoadPeriodAsync` `:1779-1782`, `:1829-1833`; у `LoadPeriodAsync` `:1921-1932` — так само). Новий вкладений клас у `RecalculationService`:
```csharp
    /// <summary>✎ C-1 (далі C-5, C-11): що завантажувач періодів бере й віддає понад значення комірок.</summary>
    private sealed class PeriodLoadExtras
    {
        /// <summary>C-1: екземпляри ІНШИХ періодів (зсув ≠ 0) з тієї самої версії, що мають стан помилок формул.</summary>
        public List<(int Offset, long TableInstanceId)> ErrorInstances { get; } = [];
    }
```
   У `LoadPeriodAsync` після рядка `IReadOnlyList<TableInstanceRef> instances = tableDefIds is null ? all : [...]` (`:1959-1961`) додати:
```csharp
        // ✎ C-1: сусідній період — його комірки з помилкою теж «недовірені» для читачів цього прогону.
        if (offset != 0 && extras is not null)
        {
            extras.ErrorInstances.AddRange(instances
                .Where(t => t.HasFormulaErrors && (translations is null || !translations.ContainsKey(t.TableInstanceId)))
                .Select(t => (offset, t.TableInstanceId)));
        }
```
   (екземпляр ІНШОЇ версії, перекладений RPT-STABLE, не засівається — коди його колонок інші; рідкісний випадок, зазначений у «Ризиках»). Після обох `LoadValuesAsync` (тобто одразу після `:706` і після злиття каскадних `extraStored` `:964-967`) засіяти сусідні періоди:
```csharp
        foreach (var group in loadExtras.ErrorInstances.GroupBy(e => e.Offset))
        {
            var neighbour = await formulaErrors!.ReadInstancesAsync(
                    [.. group.Select(e => e.TableInstanceId)], new PeriodKey(periodKey.Value + group.Key), ct)
                .ConfigureAwait(false);
            errors.Seed(neighbour, group.Key);
        }
        loadExtras.ErrorInstances.Clear();
```
   (`formulaErrors is null` — `ErrorInstances` порожній не буде, тож оберни цикл у `if (formulaErrors is not null)`). Після `var context = NewContext(registryFields);` (`:775`) і в гілці `context = NewContext(full);` (`:976`) — `context.Blocked = errors.Blocked;`.
5. `RunAsync` — рішення, що писати. Замінити `:986-990`:
```diff
         var written = byInstance.Values.Sum(cells => cells.Count);
-        if (written == 0)
+
+        // ✎ C-1: комірки аркуша з помилкою — викликачу подання (RecalculateSheetForSubmitAsync), навіть коли нічого
+        // не пишеться (та сама помилка, що вже збережена).
+        errorSink?.AddRange(errors.Outcomes.Values
+            .Where(o => o.Kind is not null
+                        && (sheetDefId is null || (tables.TryGetValue(o.TableDefId, out var errorTable) && errorTable.SheetDefId == sheetDefId)))
+            .Select(o => new FormulaErrorCell(
+                o.TableDefId, instances.FirstOrDefault(i => i.TableDefId == o.TableDefId)?.SheetCopyId, o.RowKey,
+                o.Address.ColumnDefId, o.Kind!, o.ErrorCode!, o.Source?.PeriodKey, o.Source?.TableDefId,
+                o.Source?.RowKey, o.Source?.ColumnDefId)));
+
+        var errorDelta = formulaErrors is null
+            ? FormulaErrorDelta.Empty
+            : FormulaErrorDelta.Build(
+                errors, persisted, instance.DocumentId, instanceByTable, instances,
+                fullRun: dirty is null && crossPeriodSources is null,
+                targetFormulas: targets.ToHashSet(), liveFormulas: formulas.Keys.ToHashSet(), now: clock.UtcNow);
+        if (written == 0 && errorDelta.IsEmpty)
         {
             return 0;
         }
```
   (`errorSink` — новий параметр `List<FormulaErrorCell>? errorSink = null` у `RunAsync` `:442-449`, його передає `RecalculateDocumentAsync`, крок 7; `persisted` — з кроку 4; `instanceByTable` — `:509`.) Рядок `:1021` стає `.Where(t => (byInstance.ContainsKey(t.TableInstanceId) || errorDelta.Instances.Contains(t.TableInstanceId)) && tables.ContainsKey(t.TableDefId))`.
6. `FormulaErrorDelta` — у тому самому файлі `FormulaErrorTracker.cs`:
```csharp
/// <summary>✎ C-1: що змінити в calc.FormulaError і журналі за підсумками прогону.</summary>
internal sealed class FormulaErrorDelta
{
    public static FormulaErrorDelta Empty { get; } = new([], [], [], new HashSet<long>());

    private FormulaErrorDelta(
        List<FormulaErrorRow> upserts, List<FormulaErrorRow> deletes,
        List<(FormulaErrorRow Row, bool Resolved, string? Previous)> events, HashSet<long> instances)
    { Upserts = upserts; Deletes = deletes; Events = events; Instances = instances; }

    public List<FormulaErrorRow> Upserts { get; }
    /// <summary>Рядки, що зникають (з повним рядком — для журналу).</summary>
    public List<FormulaErrorRow> Deletes { get; }
    /// <summary>Події журналу: поява помилки (Resolved = false) і усунення (Resolved = true, Previous — старий код).</summary>
    public List<(FormulaErrorRow Row, bool Resolved, string? Previous)> Events { get; }
    public HashSet<long> Instances { get; }
    public bool IsEmpty => Upserts.Count == 0 && Deletes.Count == 0;

    public static FormulaErrorDelta Build(
        FormulaErrorTracker tracker, IReadOnlyList<FormulaErrorRow> persisted, long documentId,
        IReadOnlyDictionary<int, long> instanceByTable, IReadOnlyList<TableInstanceRef> instances,
        bool fullRun, IReadOnlySet<int> targetFormulas, IReadOnlySet<int> liveFormulas, DateTime now)
    {
        var byAddress = persisted.ToDictionary(p => p.Address);
        var upserts = new List<FormulaErrorRow>();
        var deletes = new List<FormulaErrorRow>();
        var events = new List<(FormulaErrorRow, bool, string?)>();

        foreach (var outcome in tracker.Outcomes.Values)
        {
            byAddress.TryGetValue(outcome.Address, out var was);
            if (outcome.Kind is null)
            {
                if (was is null) continue;
                deletes.Add(was);
                if (was.Kind == FormulaError.ErrorKind) events.Add((was, true, was.ErrorCode));
                continue;
            }

            if (!instanceByTable.TryGetValue(outcome.TableDefId, out var instanceId)) continue;
            var copy = instances.FirstOrDefault(i => i.TableInstanceId == instanceId)?.SheetCopyId;
            var same = was is not null && was.Kind == outcome.Kind && was.ErrorCode == outcome.ErrorCode
                       && was.SourceTableDefId == outcome.Source?.TableDefId && was.SourceRowKey == outcome.Source?.RowKey
                       && was.SourceColumnDefId == outcome.Source?.ColumnDefId;
            if (same) continue;   // ⛔ та сама помилка триває — ні запису, ні події (рішення PO п. 4)

            var row = new FormulaErrorRow(
                documentId, outcome.Address.PeriodKey.Value, instanceId, outcome.TableDefId, copy,
                outcome.Address.TableRowId, outcome.RowKey, outcome.Address.ColumnDefId, outcome.FormulaDefId,
                outcome.Kind, outcome.ErrorCode!, outcome.InputsJson,
                outcome.Source?.PeriodKey, outcome.Source?.TableDefId, outcome.Source?.RowKey, outcome.Source?.ColumnDefId,
                now);
            upserts.Add(row);
            if (outcome.Kind == FormulaError.ErrorKind) events.Add((row, false, null));
            else if (was?.Kind == FormulaError.ErrorKind) events.Add((was, true, was.ErrorCode));
        }

        // Повний прогін: рядки формул, які він рахував (або яких уже немає), але комірок яких не відвідав (рядок
        // видалено, ціль змінилась після переносу версії) — застарілі.
        if (fullRun)
        {
            foreach (var stale in persisted.Where(p => !tracker.Outcomes.ContainsKey(p.Address)
                                                        && (targetFormulas.Contains(p.FormulaDefId) || !liveFormulas.Contains(p.FormulaDefId))))
            {
                deletes.Add(stale);
                if (stale.Kind == FormulaError.ErrorKind) events.Add((stale, true, stale.ErrorCode));
            }
        }

        var touched = upserts.Select(u => u.TableInstanceId).Concat(deletes.Select(d => d.TableInstanceId)).ToHashSet();
        return new FormulaErrorDelta(upserts, deletes, events, touched);
    }
}
```
7. `RunAsync` — запис у транзакції. Замінити `:1104-1111`:
```diff
             if (batch.Count > 0)
             {
                 await WriteBatchAsync(
                     instance.DocumentId, batch, rowKeyByRowIdByInstance, isLateEdit, now, token, overridesActive)
                     .ConfigureAwait(false);
             }
 
+            // ✎ C-1 (D-475): стан помилок і події журналу — лише для аркушів, у які прогін пише (поданий/затверджений
+            // аркуш не чіпається, як і його значення), тим самим комітом, що й значення.
+            if (!errorDelta.IsEmpty)
+            {
+                bool Writable(long instanceId)
+                    => sheetOfInstance.TryGetValue(instanceId, out var sheetKey) && writable.Contains(sheetKey);
+
+                await formulaErrors!.ApplyAsync(
+                        [.. errorDelta.Upserts.Where(u => Writable(u.TableInstanceId))],
+                        [.. errorDelta.Deletes.Where(d => Writable(d.TableInstanceId)).Select(d => d.Address)],
+                        token)
+                    .ConfigureAwait(false);
+
+                var events = errorDelta.Events.Where(e => Writable(e.Row.TableInstanceId)).ToList();
+                if (events.Count > 0)
+                {
+                    await audit.WriteCellChangesAsync(
+                        [.. events.Select(e =>
+                        {
+                            var current = stored.TryGetValue(new CellKey(0, e.Row.TableDefId, e.Row.RowKey, e.Row.ColumnDefId), out var cell)
+                                ? Describe(cell)
+                                : null;
+                            return new CellChangeRecord(
+                                now, e.Row.Address, instance.DocumentId, e.Row.RowKey,
+                                OldValue: current, NewValue: current, SystemUserId,
+                                Origin: e.Resolved ? Documents.CellChangeOrigins.CalcErrorResolved : Documents.CellChangeOrigins.CalcError,
+                                IsLateEdit: false, CorrelationId: null,
+                                Reason: FormulaErrorAuditReason.Of(e.Row, e.Previous));
+                        })],
+                        token).ConfigureAwait(false);
+                }
+            }
+
             await uow.SaveChangesAsync(token).ConfigureAwait(false);
```
   `FormulaErrorAuditReason.Of` (у `FormulaErrorTracker.cs`): для появи — `"{ErrorCode}"` + (якщо є входи) `" · "` + `column=value` через `"; "` з `InputsJson` (обрізати до 1000 символів — межа `aud.CellChange.Reason`, `AuditWriter.cs:63`, `:170`); для усунення — `Previous` (старий код). `IsLateEdit = false` (подія, а не зміна значення). `applied` (повертане «записано комірок») не змінюється.
8. Новий метод порту подання. `src/Ecr.Application/Recalculation/ISubmitRecalculation.cs` — після `:38-39` додати метод із реалізацією за замовчуванням (підміни в тестах не ламаються, `RecalculateSheetUnderSubmitLockAsync` лишається для `ApprovalSnapshotRefresh.cs:127`):
```csharp
    /// <summary>
    /// ✎ C-1 (D-475): те саме, що <see cref="RecalculateSheetUnderSubmitLockAsync"/>, і повертає комірки аркуша з
    /// помилкою формули чи «не оновлені через помилку» — вони блокують подання.
    /// </summary>
    public async Task<IReadOnlyList<FormulaErrorCell>> RecalculateSheetForSubmitAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct, long? sheetCopyId = null)
    {
        await RecalculateSheetUnderSubmitLockAsync(documentId, sheetDefId, periodKey, ct, sheetCopyId).ConfigureAwait(false);
        return [];
    }
```
   `RecalculationService` (після `:377-380`):
```csharp
    /// <inheritdoc />
    public async Task<IReadOnlyList<FormulaErrorCell>> RecalculateSheetForSubmitAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct, long? sheetCopyId = null)
    {
        var errors = new List<FormulaErrorCell>();
        await RecalculateDocumentAsync(
                documentId, periodKey, sheetDefId, heldSheet: (sheetDefId, sheetCopyId), ScopeFilter.Of(sheetCopyId), ct,
                errorSink: errors)
            .ConfigureAwait(false);
        return errors;
    }
```
   `RecalculateDocumentAsync` (`:383-386`) отримує останній параметр `List<FormulaErrorCell>? errorSink = null` і передає його в `RunAsync` (`:404-406`: `..., writeSubmitted: writeSubmitted, errorSink: errorSink)`). Повний прогін аркуша обчислює ВСІ цілі формул таблиць аркуша (`:594-599`, `rowFilter = null`), тож перелік повний; «не оновлено» через помилку на ІНШОМУ аркуші дає засів із `calc.FormulaError` (крок 4).
9. Конструктор `RecalculationService` (`:16-65`) — новий останній необов'язковий параметр перед `) : ISubmitRecalculation`:
```diff
-    IDocumentSheetCopyStore? sheetCopies = null) : ISubmitRecalculation
+    IDocumentSheetCopyStore? sheetCopies = null,
+
+    // ✎ C-1 (D-475): стан помилок формул. Необов'язковий: прямі `new RecalculationService(...)` у тестах його не
+    // передають (тоді стан не читається й не пишеться, але залежні від помилки однаково не перераховуються).
+    IFormulaErrorStore? formulaErrors = null) : ISubmitRecalculation
```
10. `src/Ecr.Application/Recalculation/RowLocalPreview.cs` — перегляд поводиться так само (без збереженого стану: рядково-локальна формула читає лише свій рядок, а помилкові комірки рядка переоцінюються тут же, `:126-177`). Після створення `context` (`:114-122`) додати `var blocked = new Dictionary<CellKey, FormulaErrorSource>(); context.Blocked = blocked;`, а `:136-154` замінити:
```diff
-                var evaluated = formulaEngine.Evaluate(parsed, context);
                 var key = new CellKey(0, table.Id, rowKey, column.Id);
                 var address = new CellAddress(period, rowId, column.Id);
 
                 // ✎ D-461: ручне значення формула не перезаписує й у перегляді; залежні читають його (уже в `values`).
                 if (manual.Contains(address))
                 {
                     continue;
                 }
 
+                blocked.Remove(key);
+                context.BeginCell();
+                var evaluated = formulaEngine.Evaluate(parsed, context);
                 stored.TryGetValue(address, out var old);
 
-                if (evaluated.Value.IsError)
+                // ✎ C-1 (D-475): як фонова задача — комірка з помилкою і ті, що її читають, у перегляді не змінюються
+                // (останнє збережене значення), позначку «помилка» покаже зріз після фонового перерахунку.
+                if (context.BlockedRead is { } source)
+                {
+                    blocked[key] = source;
+                    continue;
+                }
+
+                if (evaluated.Value.IsError)
                 {
-                    // Помилка не пишеться як значення; залежні формули прочитають порожнє - їхній перегляд може бути
-                    // неповним, але фонова задача все виправить.
-                    values.Remove(key);
+                    blocked[key] = new FormulaErrorSource(
+                        period.Value, table.Id, rowKey, rowId, column.Id, evaluated.Value.ErrorCode ?? Ecr.Expressions.ExpressionErrors.BadValue);
                     continue;
                 }
```
   (рядок `var evaluated = ...` переноситься нижче перевірки ручного значення — ручна комірка й раніше не використовувала результат.)

#### C-1c. Подання блокується; «Перевірити» показує помилки
1. Новий файл `src/Ecr.Application/Validation/FormulaErrorIssues.cs`:
```csharp
// src/Ecr.Application/Validation/FormulaErrorIssues.cs
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;

namespace Ecr.Application.Validation;

/// <summary>
/// ✎ C-1 (рішення PO 11.11 п. 5, D-475): комірка з помилкою формули або «не оновлена через помилку» — Error, що
/// блокує подання аркуша, як <c>PERMIT-SPLIT</c> (<see cref="PermitCoverageIssues.SplitRuleCode"/>).
/// </summary>
public static class FormulaErrorIssues
{
    public const string RuleCode = "FORMULA-ERROR";

    public static IReadOnlyList<ValidationMessage> Messages(
        IReadOnlyList<FormulaErrorCell>? cells, IReadOnlyDictionary<int, TableDef> tables, string language)
    {
        var messages = new List<ValidationMessage>();
        foreach (var cell in cells ?? [])
        {
            if (!tables.TryGetValue(cell.TableDefId, out var table)
                || table.Columns.FirstOrDefault(c => c.Id == cell.ColumnDefId) is not { } column)
            {
                continue;
            }

            var sourceTable = cell.SourceTableDefId is { } sid && tables.TryGetValue(sid, out var st) ? st : null;
            var sourceColumn = sourceTable?.Columns.FirstOrDefault(c => c.Id == cell.SourceColumnDefId);
            var blocked = cell.Kind == FormulaError.BlockedKind;
            var key = blocked ? ValidationMessageTemplates.FormulaErrorBlocked : ValidationMessageTemplates.FormulaError;
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["column"] = column.HeaderL10n.Get("en") ?? column.Code.ToString(),
                ["code"] = cell.ErrorCode,
                ["source"] = sourceColumn is null
                    ? cell.SourceRowKey ?? string.Empty
                    : $"{sourceTable!.Code} · {sourceColumn.HeaderL10n.Get("en") ?? sourceColumn.Code.ToString()} ({cell.SourceRowKey})",
            };
            messages.Add(new ValidationMessage(
                ValidationSeverity.Error, RuleCode,
                ValidationMessageTemplates.Render(key, language, parameters),
                table.Id, cell.RowKey, column.Code.ToString(), BlocksSave: false,
                SourceTableDefId: blocked ? cell.SourceTableDefId : null,
                SourceColumnCode: blocked ? sourceColumn?.Code.ToString() : null,
                MessageKey: key, Params: parameters));
        }

        return messages;
    }
}
```
   (`SourceTableDefId`/`SourceColumnCode` дають `HiddenValidationIssues.CanSee` (`HiddenValidationIssues.cs:64-65`) сховати повідомлення, джерело якого читачеві закрите — S6.) Перевір, як у `PermitCoverageIssues.cs:328` береться назва колонки (`cell.HeaderL10n.Get("en") ?? cell.Code`) — тип `Code` там `EcrCode`; повтори той самий вираз.
2. `src/Ecr.Application/Validation/ValidationMessageTemplates.cs` — константи поруч із `:64` і рядки словника поруч із `:102-105`:
```csharp
    /// <summary>✎ C-1 (D-475): власна помилка формули комірки — блокує подання.</summary>
    public const string FormulaError = "validation.formula.error";

    /// <summary>✎ C-1 (D-475): комірка не оновлена через помилку в іншій — блокує подання.</summary>
    public const string FormulaErrorBlocked = "validation.formula.blocked";
```
```csharp
        [FormulaError] = (
            "{column}: formula error {code} — the value was not recalculated (the last correct value is kept). Submit is blocked until the formula calculates.",
            "{column}: ошибка формулы {code} — значение не пересчитано (оставлено последнее правильное). Подача заблокирована, пока формула не рассчитается.",
            "{column}: формула қатесі {code} — мән қайта есептелмеді (соңғы дұрыс мән қалдырылды). Формула есептелмейінше тапсыру бұғатталған."),
        [FormulaErrorBlocked] = (
            "{column}: not updated because of a formula error in {source}. Submit is blocked until it is fixed.",
            "{column}: не обновлено из-за ошибки формулы в {source}. Подача заблокирована до исправления.",
            "{column}: {source} ішіндегі формула қатесіне байланысты жаңартылмады. Түзетілгенше тапсыру бұғатталған."),
```
3. `src/Ecr.Application/Workflow/SubmitSheetHandler.cs:458-460`:
```diff
-        await recalculation
-            .RecalculateSheetUnderSubmitLockAsync(documentId, sheetDefId, key, ct, sheetCopyId)
-            .ConfigureAwait(false);
+        // ✎ C-1 (D-475): прогін повертає комірки аркуша з помилкою формули — вони блокують подання нижче, разом із
+        // валідацією (як PERMIT-SPLIT). Нуль нових звернень: це результат того самого прогону.
+        var formulaErrorCells = await recalculation
+            .RecalculateSheetForSubmitAsync(documentId, sheetDefId, key, ct, sheetCopyId)
+            .ConfigureAwait(false);
```
   і перед `if (blocking.Count > 0)` (`:628`):
```csharp
        // ✎ C-1 (рішення PO 11.11 п. 5): помилка формули блокує подання так само, як PERMIT-SPLIT.
        blocking.AddRange(Validation.FormulaErrorIssues.Messages(formulaErrorCells, tables, currentUser.Language));
```
   Далі діє наявний шлях: порядок `OrderAsOnScreen` (`:635`), маскування схованого (`:643-645`), `ECR-SUB-4221 validationBlocked` (`:647-667`). Транзакція подання відкочується разом із записом стану, зробленим прогоном усередині неї, — лишається стан останнього фонового перерахунку (той самий, що бачить користувач).
4. `src/Ecr.Application/Documents/ValidateDocumentHandler.cs` — новий останній параметр конструктора `IFormulaErrorStore? formulaErrors = null` (після `:36`); після блоку дозволів (`:166-173`) додати:
```csharp
        // ✎ C-1 (D-475): «Перевірити» показує те, що заблокує подання, — збережений стан помилок формул. Читання — лише
        // коли хоч один екземпляр його має (прапорець із того самого запиту екземплярів).
        if (formulaErrors is not null && instances.Any(i => i.HasFormulaErrors))
        {
            var errorSnapshot = await metadata.GetAsync(instances[0].TemplateVersionId, ct).ConfigureAwait(false);
            var errorRows = await formulaErrors.ListAsync(documentId, periodKey.Value, sheetCopyId: null, ct).ConfigureAwait(false);
            messages.AddRange(FormulaErrorIssues.Messages(
                [.. errorRows.Select(v => new Recalculation.FormulaErrorCell(
                    v.Row.TableDefId, v.Row.SheetCopyId, v.Row.RowKey, v.Row.ColumnDefId, v.Row.Kind, v.Row.ErrorCode,
                    v.Row.SourcePeriodKey, v.Row.SourceTableDefId, v.Row.SourceRowKey, v.Row.SourceColumnDefId))],
                errorSnapshot.Sheets.SelectMany(s => s.Tables).ToDictionary(t => t.Id),
                currentUser.Language));
        }
```
   (`instances` тут — лише оригінали, `:64-67`; так само `sheetCopyId: null`.)

#### C-1d. Журнал: нові походження і читачі, що їх відсікають
1. `src/Ecr.Application/Documents/CellChangeOrigins.cs` — після `:107`:
```csharp
    /// <summary>✎ C-1 (D-475): формула комірки дала помилку; значення НЕ змінено (OldValue = NewValue), код і входи — у Reason.</summary>
    /// <remarks>Система (не в <see cref="HumanOriginsSql"/>/<see cref="IsHuman"/>); клієнт заявити не може.</remarks>
    public const string CalcError = "CalcError";

    /// <summary>✎ C-1 (D-475): помилку формули усунуто (успішний перерахунок); у Reason — колишній код.</summary>
    public const string CalcErrorResolved = "CalcErrorResolved";

    /// <summary>
    /// ✎ C-1: походження, які НЕ є правкою значення людиною чи машиною-джерелом: перерахунок і події помилки
    /// розрахунку. Готовий перелік для <c>NOT IN (…)</c> у сирому SQL над <c>aud.CellChange</c>.
    /// </summary>
    public const string NotInputOriginsSql = "N'" + Recalculation + "', N'" + CalcError + "', N'" + CalcErrorResolved + "'";

    /// <summary>✎ C-1: те саме для C# (<see cref="NotInputOriginsSql"/>).</summary>
    public static bool IsNotInput(string? origin) =>
        string.Equals(origin, Recalculation, StringComparison.Ordinal)
        || string.Equals(origin, CalcError, StringComparison.Ordinal)
        || string.Equals(origin, CalcErrorResolved, StringComparison.Ordinal);
```
2. Читачі, що сьогодні відсікають лише `Recalculation`, — відсікати і події помилок (інакше подія стала б «зміною входу»/«останнім записом»):
   - `src/Ecr.Infrastructure/Persistence/AuditWriter.cs:199` — `.Where(c => !CellChangeOrigins.IsNotInput(c.Origin))` (подія не позначає документ «брудним» для застарілості результатів).
   - `src/Ecr.Infrastructure/Persistence/StaleResultsQuery.cs:88`, `:143` — `AND c.Origin <> N'Recalculation'` → `AND c.Origin NOT IN (N'Recalculation', N'CalcError', N'CalcErrorResolved')` (у сирому SQL — літерали, як було; той самий перелік, що `NotInputOriginsSql`).
   - `src/Ecr.Infrastructure/Persistence/MethodologyStore.cs:619` — те саме заміщення (момент зміни входів після прогону).
   - `src/Ecr.Infrastructure/Persistence/RowStore.cs:432`, `:444` (`HasMachineOriginAsync`, «останній запис — інтеграція/дозвіл») — `WHERE u.Origin <> {CellChangeOrigins.Recalculation}` → `WHERE u.Origin NOT IN ({CellChangeOrigins.Recalculation}, {CellChangeOrigins.CalcError}, {CellChangeOrigins.CalcErrorResolved})` (параметризовано, як було).
   - `src/Ecr.Infrastructure/Persistence/SheetChangeStore.cs:63-68` (CTE `c` у `AttributeSql`) — додати умову `AND c.Origin NOT IN (N'CalcError', N'CalcErrorResolved')` після `AND c.TableRowId IN (...)`: подія не є правкою рядка й не має «будити» банер чужих змін.
   - `src/Ecr.Infrastructure/Persistence/AuditReader.cs:288-305` (`CellChangeCountSql`, підсумок «Змін за колонкою») — додати `AND Origin NOT IN (N'CalcError', N'CalcErrorResolved')` до `where` обох частин (гарячої й архівної) — лічильник рахує зміни значень. Саму вкладку Cell changes (`ReadCellChangesAsync`) НЕ фільтрувати — там події мають бути видні.
   - Без змін (перевірено): `IntegrationCellPatcher.cs:785`, `:813` і `SourceEventSyncJob.cs:1406`, `:1614` беруть лише `HumanOriginsSql` (IN-перелік людських), `CellSourceStore.cs:37-47` читає `NewValue` останнього запису комірки — для події він дорівнює поточному значенню (`OldValue = NewValue = Describe(stored)`), тож відповідь та сама; `SheetActivity` класифікує через `IsHuman` → «система».
3. Клієнт — фільтр і підпис:
   - `src/Ecr.Web/src/features/audit/api.ts:25-34` — додати `'CalcError', 'CalcErrorResolved'` у `cellChangeOrigins` (після `'Recalculation'`); у `humanCellChangeOrigins` НЕ додавати.
   - `src/Ecr.Web/src/features/audit/CellChangesTable.tsx:71-87` — у `OriginIcons` додати `CalcError: 'M12 3 2 21h20zM12 10v5M12 18v.5'` (трикутник з «!») і `CalcErrorResolved: 'M5 12l5 5 9-9'` (галочка).
   - `tests/Ecr.Architecture.Tests/EndpointCoverageTests.cs:592-596` — у переліку ключів `audit.originLabel.${origin}` додати `"audit.originLabel.CalcError", "audit.originLabel.CalcErrorResolved"`.

#### C-1e. Контракт
1. Зріз — `src/Ecr.Application/Documents/Dto/TableSliceDto.cs`, після `FormulaOverride` (`:81-85`):
```csharp
    /// <summary>
    /// ✎ C-1 (D-475): комірки зі станом помилки формули: ключ <c>"{rowKey}:{columnCode}"</c>; <c>null</c> — таких немає
    /// (більшість зрізів, нуль байтів і нуль звернень).
    /// </summary>
    public IReadOnlyDictionary<string, CellCalcErrorDto>? CalcErrors { get; init; }
```
   і нові записи в тому самому файлі:
```csharp
/// <summary>✎ C-1 (D-475): стан помилки формули комірки зрізу.</summary>
/// <param name="Kind"><c>Error</c> — власна помилка; <c>Blocked</c> — не оновлено через помилку в <paramref name="Source"/>.</param>
/// <param name="ErrorCode">Код (<c>#DIV/0</c>, <c>#REF</c>, <c>#VALUE</c>, <c>#UNIT</c>, <c>#BUDGET</c>, <c>#N/A</c>, <c>#MULTI</c>…).</param>
/// <param name="Since">Відколи (UTC).</param>
/// <param name="Source">Комірка-причина для <c>Blocked</c>; <c>null</c> — для <c>Error</c> або джерело читачеві закрите.</param>
public sealed record CellCalcErrorDto(string Kind, string ErrorCode, DateTime Since, FormulaErrorSourceDto? Source);

/// <summary>✎ C-1: адреса комірки-причини.</summary>
/// <param name="PeriodKey"><c>null</c> — той самий період.</param>
public sealed record FormulaErrorSourceDto(string TableCode, string RowKey, string ColumnCode, int? PeriodKey);
```
2. `src/Ecr.Application/Documents/GetTableSliceHandler.cs` — новий останній параметр конструктора `IFormulaErrorStore? formulaErrors = null` (після `:93`); після `formulaOverride` (`:495-497`):
```csharp
        // ✎ C-1 (D-475): позначки помилок формул — лише коли екземпляр їх має (прапорець із запиту екземпляра).
        var calcErrors = instance.HasFormulaErrors && formulaErrors is not null
            ? await CalcErrorsAsync(tableInstanceId, instance.PeriodKey, keyById, columns, readable, snapshot, ct).ConfigureAwait(false)
            : null;
```
   і в ініціалізаторі (`:502-505`) `CalcErrors = calcErrors,`. Новий приватний метод (поруч із `FormulaOverrideAsync`, `:516`):
```csharp
    /// <summary>✎ C-1: стан помилок формул видимих комірок зрізу; джерело — лише якщо читач бачить його таблицю й колонку (S6).</summary>
    private async Task<IReadOnlyDictionary<string, CellCalcErrorDto>?> CalcErrorsAsync(
        long tableInstanceId, int periodKey, Dictionary<long, string> keyById, List<ColumnDto> columns,
        DocumentReadScope readable, Domain.Entities.Configuration.TemplateVersionSnapshot snapshot, CancellationToken ct)
    {
        var rows = await formulaErrors!.ReadInstancesAsync([tableInstanceId], new Domain.ValueObjects.PeriodKey(periodKey), ct)
            .ConfigureAwait(false);
        var visibleCodeById = columns.ToDictionary(c => c.Id, c => c.Code);
        var tablesById = snapshot.Sheets.SelectMany(s => s.Tables).ToDictionary(t => t.Id);
        var result = new Dictionary<string, CellCalcErrorDto>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!keyById.TryGetValue(row.TableRowId, out var rowKey) || !visibleCodeById.TryGetValue(row.ColumnDefId, out var code))
            {
                continue;
            }

            FormulaErrorSourceDto? source = null;
            if (row.Kind == Domain.Entities.Calculations.FormulaError.BlockedKind
                && row.SourceTableDefId is { } sourceTableId && row.SourceColumnDefId is { } sourceColumnId
                && readable.CanReadTable(sourceTableId) && readable.CanReadColumn(sourceColumnId)
                && tablesById.TryGetValue(sourceTableId, out var sourceTable)
                && sourceTable.Columns.FirstOrDefault(c => c.Id == sourceColumnId) is { } sourceColumn)
            {
                source = new FormulaErrorSourceDto(
                    sourceTable.Code.ToString(), row.SourceRowKey ?? string.Empty, sourceColumn.Code.ToString(),
                    row.SourcePeriodKey == periodKey ? null : row.SourcePeriodKey);
            }

            result[$"{rowKey}:{code}"] = new CellCalcErrorDto(row.Kind, row.ErrorCode, row.Since, source);
        }

        return result.Count == 0 ? null : result;
    }
```
   (Імена `readable`, `snapshot`, `columns`, `keyById` — ті самі змінні `HandleAsync`, `:189-200`, `:440-458`; тип `DocumentReadScope` — простір імен `Ecr.Application.Security`. Перевір, що `ColumnDto` має `Id` — він використовується в `:530` `columns.ToDictionary(c => c.Id, c => c.Code)`.)
3. Панель — `src/Ecr.Application/Calculations/Dto/CalculationDtos.cs:113-115`:
```diff
 public sealed record CalculationIssuesDto(
     IReadOnlyList<CalculationIssueDto> Items,
-    IReadOnlyList<CalculationIssueTableHintDto>? Tables = null);
+    IReadOnlyList<CalculationIssueTableHintDto>? Tables = null,
+    IReadOnlyList<FormulaErrorIssueDto>? FormulaErrors = null);
```
   (у `<summary>` записати `/// <param name="FormulaErrors">✎ C-1 (D-475): група «Помилки формул»; null — помилок немає.</param>`) і нові записи там само:
```csharp
/// <summary>✎ C-1 (D-475): рядок групи «Помилки формул».</summary>
/// <param name="RowNumber">Порядковий номер рядка (<c>doc.TableRow.Ordinal</c>) — для підпису «Рядок N».</param>
/// <param name="RowLabelL10n">Назва фіксованого рядка шаблону; <c>null</c> — динамічний рядок.</param>
/// <param name="Formula">Вираз формули; <c>null</c> — хоч один вхід читачеві закритий.</param>
/// <param name="Inputs">Вхідні значення, що дали помилку (лише видимі колонки); <c>null</c> — для Blocked.</param>
public sealed record FormulaErrorIssueDto(
    string TableCode, long? SheetCopyId, string RowKey, int RowNumber, LocalizedText? RowLabelL10n,
    string ColumnCode, LocalizedText ColumnHeaderL10n, string Kind, string ErrorCode, string? Formula,
    IReadOnlyList<FormulaErrorInputDto>? Inputs, FormulaErrorSourceDto? Source, DateTime Since);

/// <summary>✎ C-1: вхідне значення помилки.</summary>
/// <param name="PeriodOffset">0 — поточний період; −1 — попередній тощо.</param>
/// <param name="Value">Значення текстом (інваріантна культура); <c>null</c> — порожньо.</param>
public sealed record FormulaErrorInputDto(string TableCode, string RowKey, string ColumnCode, int PeriodOffset, string? Value);
```
   (`FormulaErrorSourceDto` — з `TableSliceDto.cs`, простір `Ecr.Application.Documents.Dto`; додай `using`.)
4. `src/Ecr.Application/Documents/GetCalculationIssuesHandler.cs` — новий останній параметр `IFormulaErrorStore? formulaErrors = null` (після `:31`). `:63-67` замінити:
```diff
         var rows = await issues.ListAsync(documentId, periodKey, sheetCopyId, ct).ConfigureAwait(false);
-        if (rows.Count == 0)
+        var errorRows = formulaErrors is null
+            ? []
+            : await formulaErrors.ListAsync(documentId, periodKey, sheetCopyId, ct).ConfigureAwait(false);
+        if (rows.Count == 0 && errorRows.Count == 0)
         {
             return new CalculationIssuesDto([]);
         }
```
   і в `return new CalculationIssuesDto(...)` (`:89-112`) третім аргументом `FormulaErrorsOf(errorRows, readable)`; новий приватний метод:
```csharp
    /// <summary>✎ C-1: група «Помилки формул» — лише видимі комірки; закриті входи й джерело відкидаються (S6).</summary>
    private static IReadOnlyList<FormulaErrorIssueDto>? FormulaErrorsOf(
        IReadOnlyList<FormulaErrorView> rows, DocumentReadScope readable)
    {
        var visible = rows.Where(v => readable.CanReadTable(v.Row.TableDefId) && readable.CanReadColumn(v.Row.ColumnDefId)).ToList();
        if (visible.Count == 0) return null;

        return [.. visible.Select(v =>
        {
            var inputs = FormulaErrorInputDtos.Parse(v.Row.InputsJson);   // JSON кроку C-1b.3 → записи з tableDefId/columnDefId
            var hidden = inputs.Any(i => !readable.CanReadTable(i.TableDefId) || !readable.CanReadColumn(i.ColumnDefId));
            var source = v.Row.SourceTableDefId is { } st && v.Row.SourceColumnDefId is { } sc
                         && readable.CanReadTable(st) && readable.CanReadColumn(sc) && v.SourceTableCode is { } stc && v.SourceColumnCode is { } scc
                ? new FormulaErrorSourceDto(stc, v.Row.SourceRowKey ?? string.Empty, scc,
                    v.Row.SourcePeriodKey == v.Row.PeriodKey ? null : v.Row.SourcePeriodKey)
                : null;
            return new FormulaErrorIssueDto(
                v.TableCode, v.Row.SheetCopyId, v.Row.RowKey, v.RowOrdinal, RowLabelL10n: null,
                v.ColumnCode, v.ColumnHeaderL10n, v.Row.Kind, v.Row.ErrorCode,
                hidden ? null : v.Formula,
                v.Row.Kind == Domain.Entities.Calculations.FormulaError.ErrorKind
                    ? [.. inputs.Where(i => readable.CanReadTable(i.TableDefId) && readable.CanReadColumn(i.ColumnDefId))
                        .Select(i => new FormulaErrorInputDto(i.TableCode ?? string.Empty, i.RowKey, i.ColumnCode ?? string.Empty, i.PeriodOffset, i.Value))]
                    : null,
                source, v.Row.Since);
        })];
    }
```
   `FormulaErrorInputDtos.Parse` — `internal static` помічник у `FormulaErrorTracker.cs`, що десеріалізує JSON кроку C-1b.3 у `record FormulaErrorInputRecord(int TableDefId, string? TableCode, string RowKey, int ColumnDefId, string? ColumnCode, int PeriodOffset, string? Value)` (camelCase властивості — `JsonSerializerOptions(JsonSerializerDefaults.Web)`); `null`/битий JSON → `[]`. `RowLabelL10n` — заповнити з `snapshot.RowsByKey[(TableDefId, RowKey)].LabelL10n`, якщо `metadata` передано (як у підказках `CalculationIssueHints`); без `metadata` — `null`. Читання `readable` уже є (`:69-72`); винеси його вище `if (rows.Count == 0 && errorRows.Count == 0)` не треба — він потрібен лише коли є що показати.
5. `docs/build/02-contracts.md`:
   - після `:4990` — новий абзац: «✎ 2026-11-11 (C-1, `D-475`): відповідь `calculation-issues` доповнено НЕОБОВ'ЯЗКОВИМ `formulaErrors: [{ "tableCode", "sheetCopyId", "rowKey", "rowNumber", "rowLabelL10n", "columnCode", "columnHeaderL10n", "kind": "Error"|"Blocked", "errorCode", "formula", "inputs": [{ "tableCode", "rowKey", "columnCode", "periodOffset", "value" }] | null, "source": { "tableCode", "rowKey", "columnCode", "periodKey" } | null, "since" }] | null` — поточні помилки формул (`calc.FormulaError`, міграція FE01); лише видимі комірки; `formula` = `null`, якщо хоч один вхід читачеві закритий; `source` = `null`, якщо джерело закрите. Зріз таблиці: `calcErrors: { "rowKey:code": { "kind", "errorCode", "since", "source" } } | null`. Журнал: походження `CalcError` (поява помилки; `oldValue = newValue`, код і входи — у `reason`) і `CalcErrorResolved` (усунення; у `reason` — колишній код). Подання: `422 ECR-SUB-4221 validationBlocked` з повідомленнями `ruleCode = FORMULA-ERROR` (`validation.formula.error`/`validation.formula.blocked`). Право й коди помилок — без змін; старі клієнти нові поля ігнорують.»
   - рядок `ECR-SUB-4221` (`:4259`) — дописати «; ✎ C-1: помилка формули комірки аркуша (`FORMULA-ERROR`, `D-475`)».
6. `contracts/openapi.snapshot.json` — `ECR_UPDATE_SNAPSHOT=1 dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~OpenApiSnapshotTests"` (окремим комітом, як вимагає тест, `OpenApiSnapshotTests.cs:83-84`); `src/Ecr.Web/src/api/schema.d.ts` — `cd src/Ecr.Web && npm run api:types` (`package.json:15`). Очікувано: нові схеми `CellCalcErrorDto`, `FormulaErrorSourceDto`, `FormulaErrorIssueDto`, `FormulaErrorInputDto`; нове необов'язкове `calcErrors` у `TableSliceDto` і `formulaErrors` у `CalculationIssuesDto` (`openapi.snapshot.json:1867-1891`).

#### C-1f. Клієнт: позначки в сітці, група «Помилки формул», фільтр журналу
1. Новий файл `src/Ecr.Web/src/features/grid/calcError.ts`:
```ts
import type { TableSliceDto } from '@/api/types';
import type { components } from '@/api/schema';
import { t } from '@/shared/i18n';
import { formatDateTime } from '@/shared/format/datetime';

/** ✎ C-1 (D-475): стан помилки формули комірки (сервер: `slice.calcErrors`). */
export type CellCalcError = components['schemas']['CellCalcErrorDto'];

export function calcErrorOf(slice: Pick<TableSliceDto, 'calcErrors'>, rowKey: string, columnCode: string): CellCalcError | null {
  return slice.calcErrors?.[`${rowKey}:${columnCode}`] ?? null;
}

/** Причина словами; ключі — літерали (сторож `EndpointCoverageTests` не любить динамічних). */
export function calcErrorCause(code: string): string {
  switch (code) {
    case '#DIV/0': return t('grid.calcError.cause.divideByZero');
    case '#REF': return t('grid.calcError.cause.badReference');
    case '#VALUE': return t('grid.calcError.cause.badValue');
    case '#UNIT': return t('grid.calcError.cause.badUnit');
    case '#BUDGET': return t('grid.calcError.cause.budget');
    case '#N/A': return t('grid.calcError.cause.notAvailable');
    case '#MULTI': return t('grid.calcError.cause.multipleMatches');
    default: return t('grid.calcError.cause.other', { code });
  }
}

/** Текст підказки (`title`): стан — і словами, не лише лінією. */
export function calcErrorHint(error: CellCalcError): string {
  const when = formatDateTime(error.since);
  if (error.kind === 'Blocked') {
    const source = error.source;
    return source === null || source === undefined
      ? t('grid.calcError.blockedHidden', { when })
      : t('grid.calcError.blocked', { source: `${source.tableCode} · ${source.columnCode} (${source.rowKey})`, when });
  }

  return t('grid.calcError.error', { cause: calcErrorCause(error.errorCode), when });
}
```
   (`formatDateTime` — той самий імпорт, що в `formulaOverride.ts:4`.)
2. `src/Ecr.Web/src/features/grid/DocumentGrid.tsx` — у `cellProperties` (`:3641`), поруч із ручною позначкою (`:3704-3708`):
```ts
        // ✎ C-1 (D-475): помилка формули / «не оновлено через помилку» — маркер ПОВЕРХ стану, причина й час — у `title`.
        const calcError = calcErrorOf(slice, rowKey, column.code);
        const calcErrorClass = calcError === null ? null : calcError.kind === 'Blocked' ? 'ecr-cell-calc-stale' : 'ecr-cell-calc-error';
        const calcErrorHintText = calcError === null ? null : calcErrorHint(calcError);
```
   і далі: умова раннього виходу (`:3754-3763`) — додати `&& calcErrorClass === null`; `hint` (`:3793-3804`) — `calcErrorHintText` ПЕРШИМ елементом масиву; `class` (`:3810-3822`) — додати `calcErrorClass` після `manualClass`; атрибут — `...(calcError === null ? {} : { 'data-calc-error': calcError.kind })` поруч із `data-formula-manual` (`:3827`). Імпорт `calcErrorOf, calcErrorHint` з `./calcError`.
3. `src/Ecr.Web/src/shared/theme/cell-states.css` — після блоку `ecr-cell-formula-manual` (`:398-425`):
```css
/*
 * ── ✎ C-1 (D-475): помилка формули і «не оновлено через помилку» ────────
 *
 * ⚠ ДОДАТКОВИЙ маркер поверх будь-якого стану; усі кути зайняті (див. save-error), тож носій — ЛІВА внутрішня лінія:
 * суцільна для власної помилки, переривчаста для «не оновлено». Форма, не лише колір (D-128); причина й час — у `title`.
 */
.ecr-cell-calc-error {
  box-shadow: inset 3px 0 0 #b3170c;
}

.ecr-cell-calc-stale {
  background-image: repeating-linear-gradient(to bottom, #b3170c 0 4px, transparent 4px 7px);
  background-size: 3px 100%;
  background-repeat: no-repeat;
}

:root[data-mantine-color-scheme='dark'] .ecr-cell-calc-error {
  box-shadow: inset 3px 0 0 #f07369;
}

:root[data-mantine-color-scheme='dark'] .ecr-cell-calc-stale {
  background-image: repeating-linear-gradient(to bottom, #f07369 0 4px, transparent 4px 7px);
}
```
   (`box-shadow` поєднується з `ecr-cell-formula-manual`/`out-of-window` комою — якщо комірка має обидва класи, додай комбінований селектор за зразком `:402-412`.)
4. `src/Ecr.Web/src/features/documents/useUnmatchedRows.ts` — у `UnmatchedRows` (`:10-17`) нове поле `readonly formulaErrors: readonly FormulaErrorIssue[];` (`type FormulaErrorIssue = components['schemas']['FormulaErrorIssueDto']` — у `features/calculationIssues/api.ts` поруч із `CalculationIssue`, `:5`); у тілі — `useMemo` за зразком `issues` (`:53-58`) з `originals.data?.formulaErrors ?? []` (лише `sheetCopyId == null`) і, для відкритої копії, `copy.data?.formulaErrors` цієї копії; повернути в об'єкті (`:67`).
5. Новий компонент `src/Ecr.Web/src/features/documents/FormulaErrorsBanner.tsx` — та сама форма, що `UnmatchedRowsBanner` (`Alert`, рядок підсумку, згортання з `localStorage` під `try/catch`, ключ `ecr.formulaErrors.collapsed:{userId}`), але `color="red"` і БЕЗ стану «згорнуто за замовчуванням» (помилка блокує подання — розгорнуто, поки людина не згорне). Вміст: заголовок `t('document.formulaErrors.title')`, підсумок `t('document.formulaErrors.summary', { count })`; групи за таблицею (як `groupUnmatchedRows`, ключ `tableCode + sheetCopyId`, назва таблиці й аркуша — з `tables`); у кожному рядку: `{column} ({row})`, причина `calcErrorCause(errorCode)` або для Blocked — `t('document.formulaErrors.blockedBy', { source })`, з якого часу (`formatDateTime(since)`), формула (`t('document.formulaErrors.formula', { formula })`, якщо не `null`), входи (`t('document.formulaErrors.inputs', { inputs: 'B=0; A=10' })`), кнопка-посилання `t('document.formulaErrors.open')` → `onOpenCell(tableInstanceId, rowKey, columnCode)`. Атрибут `data-testid="document-formula-errors"`.
6. `src/Ecr.Web/src/pages/DocumentPage.tsx` — лінивий імпорт за зразком `:158-160` і монтування ПЕРЕД `UnmatchedRowsBanner` (`:907`):
```tsx
      {unmatchedRows.formulaErrors.length > 0 && (
        <Suspense fallback={null}>
          <FormulaErrorsBanner
            userId={session.data?.userId}
            errors={unmatchedRows.formulaErrors}
            tables={tables.data ?? []}
            onOpenCell={openUnmatchedRow}
          />
        </Suspense>
      )}
```
   (`openUnmatchedRow` уже приймає `columnCode`, `:509-514`.) Список оновлюється сам: ключ запиту лежить під `['document', id, period]`, який скидає завершений перерахунок (`features/calculationIssues/api.ts:14-21`).

#### C-1g. Тексти, рішення, документи
1. Новий сід `src/Ecr.Infrastructure/Persistence/Sql/seed/features/D475-formula-errors.sql` — дві секції, точно за формою `D461-formula-override-ui.sql` (`:1-5` заголовок `#I18N`, `:70-74` заголовок `#UiEn`, маркери `-- D475:formula-errors ── … ──` і `-- D475:formula-errors ── кінець секції ──`, `OPTION (RECOMPILE); GO`; рядок `-- ⚠ kz — робочий переклад, на звірку термінологу.`). Ключі (en / ru / kz):

| Ключ | en | ru | kz |
|---|---|---|---|
| `grid.calcError.error` | Calculation error: {cause} · since {when}. The value shown is the last correct one. | Ошибка расчёта: {cause} · с {when}. Показано последнее правильное значение. | Есептеу қатесі: {cause} · {when} бастап. Соңғы дұрыс мән көрсетілген. |
| `grid.calcError.blocked` | Not updated because of an error in {source} · since {when}. The value shown is the last correct one. | Не обновлено из-за ошибки в {source} · с {when}. Показано последнее правильное значение. | {source} ішіндегі қатеге байланысты жаңартылмады · {when} бастап. Соңғы дұрыс мән көрсетілген. |
| `grid.calcError.blockedHidden` | Not updated because of an error in another cell · since {when}. | Не обновлено из-за ошибки в другой ячейке · с {when}. | Басқа ұяшықтағы қатеге байланысты жаңартылмады · {when} бастап. |
| `grid.calcError.cause.divideByZero` | division by zero (#DIV/0) | деление на ноль (#DIV/0) | нөлге бөлу (#DIV/0) |
| `grid.calcError.cause.badReference` | missing reference or registry entry (#REF) | нет ссылки или записи справочника (#REF) | сілтеме немесе анықтамалық жазбасы жоқ (#REF) |
| `grid.calcError.cause.badValue` | value of a wrong type (#VALUE) | значение неверного типа (#VALUE) | мән түрі қате (#VALUE) |
| `grid.calcError.cause.badUnit` | unit conversion is impossible (#UNIT) | невозможен перевод единиц (#UNIT) | бірліктерді түрлендіру мүмкін емес (#UNIT) |
| `grid.calcError.cause.budget` | the formula is too complex for one calculation (#BUDGET) | формула слишком сложна для одного расчёта (#BUDGET) | формула бір есептеу үшін тым күрделі (#BUDGET) |
| `grid.calcError.cause.notAvailable` | no matching value (#N/A) | нет подходящего значения (#N/A) | сәйкес мән жоқ (#N/A) |
| `grid.calcError.cause.multipleMatches` | several matching values (#MULTI) | несколько подходящих значений (#MULTI) | бірнеше сәйкес мән (#MULTI) |
| `grid.calcError.cause.other` | error {code} | ошибка {code} | қате {code} |
| `document.formulaErrors.title` | Formula errors | Ошибки формул | Формула қателері |
| `document.formulaErrors.summary` | {count} cell(s) not recalculated · Submit is blocked | Не пересчитано ячеек: {count} · подача заблокирована | Қайта есептелмеген ұяшықтар: {count} · тапсыру бұғатталған |
| `document.formulaErrors.blockedBy` | not updated — error in {source} | не обновлено — ошибка в {source} | жаңартылмады — {source} ішінде қате |
| `document.formulaErrors.formula` | Formula: {formula} | Формула: {formula} | Формула: {formula} |
| `document.formulaErrors.inputs` | Inputs: {inputs} | Входные значения: {inputs} | Кіріс мәндері: {inputs} |
| `document.formulaErrors.since` | since {when} | с {when} | {when} бастап |
| `document.formulaErrors.open` | Go to cell | Перейти к ячейке | Ұяшыққа өту |
| `audit.originLabel.CalcError` | Calculation error | Ошибка расчёта | Есептеу қатесі |
| `audit.originLabel.CalcErrorResolved` | Calculation error resolved | Ошибка расчёта устранена | Есептеу қатесі жойылды |
| `validation.formula.error` | (текст із C-1c.2) | (текст із C-1c.2) | (текст із C-1c.2) |
| `validation.formula.blocked` | (текст із C-1c.2) | (текст із C-1c.2) | (текст із C-1c.2) |

   (`validation.formula.*` — ті самі рядки, що запасні шаблони в `ValidationMessageTemplates`; так само зроблено для `validation.permit.splitNeeded` у `PERMITS-midmonth-split.sql:27-28`, `:41`.)
2. `docs/tz/10-decisions.md` — новий рядок основної таблиці ПІСЛЯ `D-474` (`:911`), у тому самому форматі (`| **D-475** | **✎ 2026-11-11. …** текст | посилання |`): «**✎ 2026-11-11. Помилка формули: значення не перезаписується, залежні не перераховуються, позначка, панель, журнал, блок подання.** (1) Комірка, формула якої дає помилку (`#DIV/0`, `#REF`, `#VALUE`, `#UNIT`, `#BUDGET`, `#N/A`, `#MULTI`…), лишає останнє правильне значення; формули, що її читають (транзитивно, і з інших періодів), від неї не перераховуються й лишають свої значення — «нічого не оновлено, як транзакція». (2) Стан — `calc.FormulaError` (міграція FE01; лише поточний, без партицій і FK); сітка позначає «помилка розрахунку» (причина, з якого часу) і «не оновлено через помилку в …». (3) Панель проблем розрахунку — група «Помилки формул» (комірка, формула, причина, входи, перехід до комірки); зникає після успішного перерахунку. (4) Журнал `aud.CellChange`: походження `CalcError`/`CalcErrorResolved`, запис при появі й усуненні, без дублів; вкладка Cell changes фільтрує їх як будь-яке походження. (5) Помилка формули (і «не оновлено через помилку») блокує подання аркуша (`ECR-SUB-4221`, `FORMULA-ERROR`), як PERMIT-SPLIT; затвердження не блокується. Заміщує обіцянку 02b §6.4 «`ValueString = '#DIV/0'`». | `00-PLAN.md` §4 (PO 11.11) · `04-correctness-domain.md` C-1 · `02b-expressions.md` §6.4 |». І рядок журналу змін у кінці файлу (після `:1139`): «| 2026-11-11 | `D-475`: помилка формули — значення не перезаписується, залежні не перераховуються, позначка в сітці, група «Помилки формул», походження журналу `CalcError`/`CalcErrorResolved`, блок подання (`FORMULA-ERROR`); `docs/CHECKSUMS.txt` оновлено в тому ж коміті | рішення PO 11.11 на схему координатора (`00-PLAN.md` §4) |». Номер — наступний вільний на момент злиття (на `main` 39aea41d9 останній — `D-474`).
3. `docs/CHECKSUMS.txt` — новий SHA-256 рядка `tz/10-decisions.md` (готовий рядок дає повідомлення падіння `SpecificationBaselineTests`, `CLAUDE.md:226`).
4. `docs/build/02b-expressions.md:524-525` — замінити «Комірка з помилкою зберігається як `IsEmpty = 0`, `ValueString = '#DIV/0'`, `IsCalculated = 1` — щоб її було видно у звіті, а не «просто порожньо».» на «✎ 2026-11-11 (`D-475`): значення комірки з помилкою НЕ змінюється (лишається останнє правильне), формули, що її читають, не перераховуються; стан помилки — `calc.FormulaError`, видно в сітці, у панелі проблем і в журналі (`CalcError`); помилка блокує подання.»

**Тест-доказ:**
- `tests/Ecr.Application.Tests/Recalculation/CascadeRecalculationTests.cs`, новий кейс `Помилка_формули_лишає_значення_і_не_перераховує_залежні`: сховище A=10, B=0, E=2 (входи), C=5, D=11 (`IsCalculated`); формули `C=[A]/[B]`, `D=[C]*2+[E]`; насіння — B і E; `RecalculateAsync`. Перевірити: у `ApplyBatchAsync` немає записів ні для C, ні для D. Без кроку C-1b.3 падає: D=12 записано (рахується від старого C=5 і нового E).
- Там само `Помилка_формули_пише_стан_і_одну_подію_журналу`: підміна `IFormulaErrorStore` (NSubstitute) — перший прогін: `ApplyAsync` з upsert C (`Error`, `#DIV/0`, `InputsJson` містить `"value":"0"`) і D (`Blocked`, джерело C); `WriteCellChangesAsync` отримав рівно одну подію `Origin = CalcError` для C з `OldValue == NewValue == "5"`. Другий прогін на тих самих даних (екземпляр із `HasFormulaErrors: true`, `ReadInstancesAsync` повертає рядки першого): `ApplyAsync` не викликано, подій немає. Без C-1b.6 (перевірки `same`) падає: другий upsert і друга подія.
- Там само `Успішний_перерахунок_знімає_помилку_і_пише_подію_усунення`: збережені рядки C (`Error`) і D (`Blocked`), B=2 → записи C=5 і D (нове число), `ApplyAsync(deletes: C, D)`, одна подія `CalcErrorResolved` для C з `Reason = "#DIV/0"`.
- `tests/Ecr.Application.Tests/Recalculation/RowLocalPreviewTests.cs`, кейс `Помилка_в_перегляді_не_дає_значень_ні_собі_ні_залежним`: рядково-локальні `C=[A]/[B]`, `D=[C]*2`, правка B=0 → у результаті перегляду немає ні C, ні D. Без C-1b.10 падає: D присутня (раніше перегляд давав залежним порожнє → `null` → «комірку очищено»).
- `tests/Ecr.Application.Tests/Workflow/SubmitApproveTests.cs` (поруч із `:385`), кейс `Помилка_формули_блокує_подання`: `ISubmitRecalculation` (підміна) повертає один `FormulaErrorCell` (`Error`, `#DIV/0`) → `BusinessRuleException` з `ErrorCodes.SubmitBlocked`, у `messages` — `ruleCode = FORMULA-ERROR`. Без C-1c.3 подання проходить.
- `tests/Ecr.Application.Tests/Documents/GetCalculationIssuesHandlerTests.cs`, кейси: група `formulaErrors` з формулою й входами; таблиця під забороною — рядка немає; закрите джерело Blocked — `source = null`; закритий вхід — `formula = null` і цей вхід відсутній.
- `tests/Ecr.Infrastructure.Tests/Persistence/FormulaErrorStoreTests.cs` (новий, SqlServer, за зразком `CalculationIssueStoreTests.cs:156-179`): `ApplyAsync` заміщує стан комірки (унікальний індекс не порушено); `ListAsync` — одна SQL-команда (`SqlClientCommandCounter`), рядок видаленого `doc.TableRow` не повертається; `RowStore.GetTableInstancesAsync` дає `HasFormulaErrors = true` лише екземпляру з рядком і лишається ОДНІЄЮ командою.
- `tests/Ecr.Infrastructure.Tests/Persistence/CalcErrorAuditOriginsTests.cs` (новий, SqlServer): рядок `aud.CellChange` з `Origin = CalcError` після рядка `Origin = Integration` — `RowStore.HasMachineOriginAsync` лишається `true`; `StaleResultsQuery`/`MethodologyStore` не вважають його зміною входу. Без C-1d.2 — `false`/«застаріло».
- `tests/Ecr.Api.Tests/FormulaErrorSubmitHttpTests.cs` (новий, e2e на справжньому хості, за зразком `DocumentPermitHistoryTests.cs:286-376`): документ із `C=[A]/[B]`, B=0 → `POST …/submit` = 422 `ECR-SUB-4221` з `FORMULA-ERROR`; `GET …/calculation-issues` містить `formulaErrors`; зріз має `calcErrors["R:C"]`; журнал має одну подію `CalcError`; B=2 → подання 204, `formulaErrors` = `null`.
- Клієнт: `src/Ecr.Web/src/features/grid/__tests__/calcError.test.ts` (підказки для `Error`/`Blocked`/закритого джерела, причини за кодами); `src/Ecr.Web/src/features/documents/__tests__/FormulaErrorsBanner.test.tsx` (група, перехід до комірки викликає `onOpenCell` з `columnCode`); `src/Ecr.Web/src/features/audit/__tests__/CellChangesTable.calcError.test.tsx` (підпис і піктограма нових походжень).

**Перевірка після впровадження:** `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~Recalculation|FullyQualifiedName~Workflow|FullyQualifiedName~CalculationIssues"`; `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~FormulaErrorStore|FullyQualifiedName~CalcErrorAuditOrigins|FullyQualifiedName~SubmitSheetQueryCount|FullyQualifiedName~CalculationIssueStore"`; `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~FormulaJobFixedCost|FullyQualifiedName~PatchCellsQueryCount|FullyQualifiedName~FormulaOverrideQueryCount|FullyQualifiedName~FormulaErrorSubmit|FullyQualifiedName~OpenApiSnapshot"`; `dotnet test tests/Ecr.Architecture.Tests` (`EndpointCoverageTests`, `SpecificationBaselineTests`, `SheetCopyQuerySitesTests`); `cd src/Ecr.Web && npx vitest run src/features/grid/__tests__/calcError.test.ts src/features/documents/__tests__/FormulaErrorsBanner.test.tsx src/features/audit/__tests__ && npx tsc --noEmit && npm run lint`. Стенд: сценарій вище вручну (позначки, панель, журнал із фільтром «Ошибка расчёта», блок «Подати», зникнення після виправлення); upgrade-перевірка ×2 (з `main` і з бази до FE01).

**Ризики і побічні ефекти:**
- **Тести фіксованої кількості запитів — стелі не змінюються.** `FormulaJobFixedCostTests` (`MaxWarmCommands = 20`, `FormulaJobFixedCostTests.cs:168`): прапорець — `EXISTS` у тій самій команді екземплярів, читання стану й запис — лише коли є помилки (у тесті їх немає), подій немає → 0 нових команд. `PatchCellsQueryCountTests` (24/26/32): `ResolveTableInstance` — та сама команда, перегляд рядка нових читань не має. `SubmitSheetQueryCountTests` (16/17): помилки для блоку подання — результат прогону; сам прогін без прапорців стан не читає. `FormulaOverrideQueryCountTests` (відносний): зріз читає стан лише з прапорцем. `CalculationIssueStoreTests` «читання проблем — одна команда» (`:156-179`) стосується `ICalculationIssueStore.ListAsync`, яка не змінюється. Нова команда без стелі: `GET …/calculation-issues` +1 (`IFormulaErrorStore.ListAsync`) — тесту кількості запитів для цього GET немає (`git grep calculation-issues tests` — лише `GetCalculationIssuesHandlerTests`, `InvisibleDocumentNotFoundTests`, тести підказок).
- **Більше рядків «Blocked»:** помилка в комірці, яку читає колонкова формула всіх рядків (наприклад, підсумок у кожному з 500 рядків), дасть 500 рядків `Blocked`. Це і є вимога PO («позначаються залежні»); стан розріджений і зникає з виправленням.
- **Зміна поведінки:** залежні від помилкових комірок формули більше не оновлюються (раніше — рахувались від старого числа); у документах зі «звичними» `#DIV/0` подання стане заблокованим, доки дані не виправлять. Потрібне повідомлення UAT.
- **Помилки в періоді іншої версії шаблону (RPT-STABLE) не засіваються** (крок C-1b.4): читач із сусіднього періоду на іншій версії перерахується від останнього правильного значення. Рідкісний випадок; зазначено в коді.
- **Rollup (D-230) не бачить стану помилок:** `RelationRecalculator` читає збережені/відкладені значення, тобто останнє правильне — узгоджено з «нічого не оновлено».
- **`TableInstanceRef` — позиційний запис:** новий останній параметр із значенням за замовчуванням зберігає всі виклики конструктора; рівність записів тепер враховує прапорець — використань рівності цілого запису в `src` не знайдено (`git grep "HashSet<TableInstanceRef>\|Dictionary<TableInstanceRef"` — порожньо).
- **Архівація** не змінюється (FK немає); затвердження (`ApprovalSnapshotRefresh.cs:127`) помилкою не блокується — PO вирішив лише про подання.
- **Бюджет маршруту 250 KB:** банер — лінивий імпорт, як `UnmatchedRowsBanner` (`DocumentPage.tsx:158-160`).

**Обґрунтування:** окрема таблиця «поточного стану» з прапорцем у вже наявному запиті — єдиний варіант, що дає «нуль нових звернень без помилок» і не чіпає табличний тип `MERGE` `doc.CellValue`. Блокування залежних через одне місце читання (`SliceEvaluationContext.Value`) охоплює всі форми посилань (одинична, діапазон, предикат, `GetCell`, міжперіодні зсуви) без змін в обчислювачі; поширення значення-помилки через `values` (попередня редакція) не годиться — `IFERROR` і агрегати (`SUM` пропускає/поглинає) дали б перерахунок «від помилки». Журнал — новими походженнями (без міграції, вкладка вже фільтрує за походженням), а не окремою таблицею: інакше вкладка Cell changes мала б зливати два джерела з курсорами. Блок подання — тим самим шляхом, що PERMIT-SPLIT (`blocking` → `ECR-SUB-4221`), і з результату прогону, що вже виконується під блокуванням подання (`SubmitSheetHandler.cs:458-460`), — без нових запитів.
**Рішення PO:** отримано 11.11 (див. рядок «✎ рішення PO 11.11» угорі).
**Впевненість:** підтверджено читанням (усі місця змін і їхні сусіди відкрито); поведінка на справжніх даних і вплив на UAT-документи — потребує перевірки на стенді.
**Залежності:** P-2 (той самий `RecalculationService.cs`, лінія L6) — до C-1; C-5 і C-11 — після C-1 (спільні `RecalculationService.cs`, `SliceEvaluationContext.cs`, `RowLocalPreview.cs`). Токен міграцій (FE01) — по черзі з D-3 (`RS2`), D-9 (`OB01`), D-5 (`RV01`). Контракт (`openapi.snapshot.json`, `schema.d.ts`) — по черзі з L16 (S-2), L17 (W-2) і C-10 (поле `monthTo`).

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
**✎ рішення PO 11.11:** варіант «(а)» — формули, валідація й методології беруть `ColumnDef.DefaultValue`, коли комірка порожня в сенсі 02b §6.3 (запису `doc.CellValue` немає; явна порожнеча `IsEmpty = 1` дефолту НЕ отримує). Дефолт підставляється при ЧИТАННІ і ніде не пишеться — узгоджено з `D-283` Q10=A («значення за замовчуванням порожньої комірки не пишеться», `docs/tz/10-decisions.md:736`). Виконується ПІСЛЯ C-1 (спільні `RecalculationService.cs`, `SliceEvaluationContext.cs`, `RowLocalPreview.cs`; клас `PeriodLoadExtras` з C-1b.4).
**Категорія:** correctness
**Де:**
- Правило: `docs/build/02b-expressions.md:486-495` (§6.3: «рядка `CellValue` немає → `ColumnDef.DefaultValue`, або `null`»; «`IsEmpty = 1` → завжди `null`»).
- Поле: `src/Ecr.Domain/Entities/Configuration/ColumnDef.cs:56` (`string? DefaultValue`, вільний текст, без перевірки — `SetPresentation` `:204-209`); обчислювані колонки — `ColumnDef.IsComputed` (`:80`).
- Єдиний серверний читач — DTO зрізу (`src/Ecr.Application/Documents/GetTableSliceHandler.cs:349`, коментар `:380-381`). Клієнт показує дефолт і рахує з ним підсумок: `src/Ecr.Web/src/features/grid/DocumentGrid.tsx:3989`, `src/Ecr.Web/src/features/grid/gridTotals.ts:102`; розбір за типом — `src/Ecr.Web/src/features/grid/edits.ts:273-330` (`coerce`).
- Шляхи обчислення без дефолту (усі, які покриває зміна):
  1. Перерахунок формул (повний, інкрементний, міжперіодний, каскад Rollup): значення контексту будує `RecalculationService.LoadPeriodAsync` (`src/Ecr.Application/Recalculation/RecalculationService.cs:1921-2016`, запис `values[key]` — `:2007-2008`) для поточного періоду (`:1779-1782`) і для кожного зсуву (`:1829-1833`); читання — `SliceEvaluationContext.Value` (`SliceEvaluationContext.cs:546-559`, `?? ExpressionValue.Null`). Через нього ж ідуть діапазони (`:359-363`), предикати (`:438`) і агрегати `SUM/AVERAGE/…` над колонкою (кожен елемент групи — `Value`).
  2. Перегляд рядка: `src/Ecr.Application/Recalculation/RowLocalPreview.cs:98-103` (значення лише зі збережених комірок).
  3. Валідація «Перевірити» і подання: `src/Ecr.Application/Validation/TableValidation.cs:157-209` (`SliceContext._values` з `cells`), `:222-260` (`MissingRequiredColumnMessages`: «заповнено» = є запис і не `IsEmpty`); викликачі — `src/Ecr.Application/Documents/ValidateDocumentHandler.cs:121-135`, `src/Ecr.Application/Workflow/SubmitSheetHandler.cs:576-596`. Комірковий шлях PATCH (`ValidationEngine.ValidateCell`, `:43-91`, `CellContext` `:354-358`) перевіряє ЗНАЧЕННЯ, яке пише людина, — дефолт там ні до чого.
  4. Методології: зіставлення правил — `src/Ecr.Calculations/MethodologyResolver.cs:229-242` (`ReadSliceAsync` → `byRow`), входи — `src/Ecr.Calculations/CalculationInputBuilder.cs:46-94` (той самий зріз через `TableSliceRead`, `MethodologyResolver.cs:292-293`).
  5. Зв'язки Rollup/Check (агрегат над колонкою іншої таблиці): `src/Ecr.Application/Calculations/RelationRecalculator.cs:133-164`, `src/Ecr.Application/Validation/RelationCheckRunner.cs:64-80` → `BuildRows` (`:114-…`).
**Проблема:**
```csharp
// SliceEvaluationContext.cs:558
return _values.TryGetValue(key, out var value) ? value : ExpressionValue.Null;
```
`_values` наповнюють лише збережені комірки, тож «не заповнювали» = `null` скрізь на сервері, а сітка показує й підсумовує дефолт. Сервер і екран рахують різні числа.
**Сценарій відтворення:** колонка Factor з `DefaultValue = "1.5"`, формула `Emission = [Qty]*[Factor]`. Ввести Qty=10, Factor не чіпати. Сітка показує Factor 1.5 і підсумок колонки 1.5; сервер рахує `10 * null = null` → Emission порожня; обов'язкова Factor — «не заповнено» при поданні; методологія з правилом `Factor = 1.5` рядок не зіставляє.
**Вплив:** будь-який шаблон із дефолтами у вхідних колонках; `D-474` (8)(iv) вводить `DefaultValue = NOTPASSEDOVERHAUL` для Lookup-колонки `Land_OverhauledEnginesRef` (код запису довідника, `docs/delivery/reference-data/land-template/land-template.model.json`) — без зміни він не діяв би ні в формулах, ні в правилах методологій.
**Зміна — покроково:**
1. Новий файл `src/Ecr.Application/Recalculation/ColumnDefaultValues.cs` — одне джерело правди «дефолт колонки як значення комірки» для всіх шляхів:
```csharp
// src/Ecr.Application/Recalculation/ColumnDefaultValues.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Recalculation;

/// <summary>
/// ✎ C-5 (рішення PO 11.11, 02b §6.3): значення за замовчуванням колонок версії як значення комірки — коли рядок є,
/// а запису комірки немає. Не пишеться ніде (D-283 Q10=A). Похідне від версії: нуль звернень у теплому стані.
/// </summary>
public sealed class ColumnDefaultValues(IMetadataCache metadata, IRegistryStore registries)
{
    /// <summary>Вид похідних даних версії (<see cref="IMetadataCache.GetDerivedAsync{T}"/>).</summary>
    public const string DerivedKind = "c5-column-defaults";

    public Task<ColumnDefaultSet> ForVersionAsync(int templateVersionId, CancellationToken ct)
        => metadata.GetDerivedAsync(
            templateVersionId, DerivedKind, token => BuildAsync(templateVersionId, token), ct,
            // Lookup-код, якого (ще) немає в довіднику, — не довше за вікно ревізії (той самий прийом, що D-461 F3).
            shortLived: set => set.HasUnresolvedLookup);

    private async Task<ColumnDefaultSet> BuildAsync(int templateVersionId, CancellationToken ct)
    {
        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);
        var values = new Dictionary<int, (int TableDefId, CellValueData Value)>();
        var lookups = new List<ColumnDef>();
        foreach (var column in snapshot.Sheets.SelectMany(s => s.Tables).SelectMany(t => t.Columns))
        {
            if (column.IsDeleted || column.IsComputed || string.IsNullOrWhiteSpace(column.DefaultValue))
            {
                continue;
            }

            if (column.DataType == CellDataType.Lookup)
            {
                lookups.Add(column);
                continue;
            }

            if (Parse(column) is { } value)
            {
                values[column.Id] = (column.TableDefId, value);
            }
        }

        var unresolved = false;
        foreach (var group in lookups.Where(c => c.LookupRegistryDefId is not null).GroupBy(c => c.LookupRegistryDefId!.Value))
        {
            // ⚠ Дефолт Lookup — КОД запису (як NOTPASSEDOVERHAUL у Land), а комірка тримає id (CellValueReader.cs:153-156):
            // одне читання на довідник при побудові похідного, далі — з кешу версії.
            var codes = group.Select(c => c.DefaultValue!.Trim()).Distinct(StringComparer.Ordinal).ToList();
            var entries = await registries.FindEntriesByCodesAsync(group.Key, codes, ct).ConfigureAwait(false);
            foreach (var column in group)
            {
                var text = column.DefaultValue!.Trim();
                var entry = entries.FirstOrDefault(e => string.Equals(e.Code.ToString(), text, StringComparison.Ordinal));
                if (entry is not null)
                {
                    values[column.Id] = (column.TableDefId, new CellValueData { ValueRegistryEntryId = entry.Id });
                }
                else
                {
                    unresolved = true;
                }
            }
        }

        return new ColumnDefaultSet(values, unresolved);
    }

    /// <summary>Текст дефолту → значення за типом колонки (інваріантна культура, той самий розбір, що PATCH).</summary>
    internal static CellValueData? Parse(ColumnDef column)
    {
        try
        {
            return Documents.CellValueReader.Read(column.DefaultValue, column, CultureInfo.InvariantCulture) is { IsEmpty: false } value
                ? value
                : null;
        }
        catch (BusinessRuleException)
        {
            return null;   // непридатний дефолт (текст у числовій колонці) — як його відсутність; сітка покаже текст як є
        }
    }
}

/// <summary>✎ C-5: дефолти версії за таблицею.</summary>
public sealed class ColumnDefaultSet
{
    public static ColumnDefaultSet Empty { get; } = new(new Dictionary<int, (int, CellValueData)>(), false);

    private readonly Dictionary<int, List<(int ColumnDefId, CellValueData Value)>> _byTable;

    internal ColumnDefaultSet(IReadOnlyDictionary<int, (int TableDefId, CellValueData Value)> byColumn, bool hasUnresolvedLookup)
    {
        _byTable = byColumn.GroupBy(p => p.Value.TableDefId)
            .ToDictionary(g => g.Key, g => g.Select(p => (p.Key, p.Value.Value)).ToList());
        HasUnresolvedLookup = hasUnresolvedLookup;
    }

    public bool HasUnresolvedLookup { get; }

    public bool IsEmpty => _byTable.Count == 0;

    /// <summary>Дефолти колонок таблиці (порожньо — немає).</summary>
    public IReadOnlyList<(int ColumnDefId, CellValueData Value)> Of(int tableDefId)
        => _byTable.TryGetValue(tableDefId, out var list) ? list : [];

    /// <summary>
    /// Зріз таблиці + віртуальні комірки дефолтів для пар (рядок, колонка), яких у зрізі НЕМАЄ. Наявна комірка
    /// (зокрема явна порожнеча <c>IsEmpty = 1</c>) не замінюється (02b §6.3).
    /// </summary>
    public IReadOnlyList<CellRecord> Complete(
        IReadOnlyList<CellRecord> cells, int tableDefId, IReadOnlyDictionary<string, long> rowIds, PeriodKey period)
    {
        var defaults = Of(tableDefId);
        if (defaults.Count == 0 || rowIds.Count == 0)
        {
            return cells;
        }

        var present = cells.Select(c => (c.Address.TableRowId, c.Address.ColumnDefId)).ToHashSet();
        var result = new List<CellRecord>(cells);
        foreach (var rowId in rowIds.Values)
        {
            foreach (var (columnDefId, value) in defaults)
            {
                if (!present.Contains((rowId, columnDefId)))
                {
                    result.Add(new CellRecord(new CellAddress(period, rowId, columnDefId), tableDefId, value));
                }
            }
        }

        return result;
    }
}
```
   Перевір перед написанням: `IRegistryStore.FindEntriesByCodesAsync(int, IReadOnlyCollection<string>, CancellationToken)` (`src/Ecr.Application/Ports/IRegistryStore.cs:398-399`), `IMetadataCache.GetDerivedAsync<T>(int, string, Func<CancellationToken, Task<T>>, CancellationToken, Func<T, bool>? shortLived = null)` (`IMetadataCache.cs:57-60`), `CellValueReader.Read(object?, ColumnDef, CultureInfo?)` (`CellValueReader.cs:130`), що помилки розбору — `BusinessRuleException` (`CellValueReader.cs:234`, `:304`), і як у `PermitCoverageIssues` береться код запису (`e.Code.ToString()`, `PermitCoverageIssues.cs:315`).
2. `src/Ecr.Application/DependencyInjection.cs` — поруч із `:167` (`IRowLocalPreview`) додати `services.AddScoped<Recalculation.ColumnDefaultValues>(); // ✎ C-5`.
3. Перерахунок — `src/Ecr.Application/Recalculation/RecalculationService.cs`:
   - поле класу поруч із `:71`: `private readonly ColumnDefaultValues _columnDefaults = new(metadata, registryStore);` (параметри первинного конструктора `metadata`, `registryStore` — `:19`, `:23`);
   - у `PeriodLoadExtras` (C-1b.4) — властивість `public ColumnDefaultSet Defaults { get; set; } = ColumnDefaultSet.Empty;`;
   - у `RunAsync` одразу після `var loadExtras = new PeriodLoadExtras();` (C-1b.4) — `loadExtras.Defaults = await _columnDefaults.ForVersionAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);`;
   - у `LoadPeriodAsync` — після внутрішнього `foreach (var cell in cells) { … }` (закривається на `:2014`), ще всередині `foreach (var table in instances)`:
```csharp
            // ✎ C-5 (рішення PO 11.11, 02b §6.3): рядок є, запису комірки немає → дефолт колонки (версії, що рахується:
            // `table.TableDefId` уже перекладено на неї для сусіднього періоду іншої версії). Наявна комірка, зокрема
            // явна порожнеча `IsEmpty = 1`, уже лежить у `values` як `Null` і дефолту не отримує. У `stored` не йде:
            // дефолтні колонки — входи, перерахунок їх не пише.
            if (extras is not null)
            {
                foreach (var (columnId, value) in extras.Defaults.Of(table.TableDefId))
                {
                    foreach (var rowKey in keys.Keys)
                    {
                        values.TryAdd(new CellKey(offset, table.TableDefId, rowKey, columnId), FromCellValue(value));
                    }
                }
            }
```
     (`keys` — ключі рядків екземпляра, `:1983-1985`; `FromCellValue` — `:2062-2101`, повертає `Number(entryId)` для Lookup, як збережена комірка.) Так дефолт отримують поточний період, кожен зсув (`[Period:-n]`, `[Period:#k]`), діапазони, предикати й агрегати над колонкою — усі читають `Value` над тим самим `values`. Q-17: комірки власника, яких немає, теж отримують дефолт (той самий `values.TryAdd`).
4. Перегляд — `src/Ecr.Application/Recalculation/RowLocalPreview.cs`: після `:98-103` (заповнення `values` зі `stored`):
```csharp
        // ✎ C-5: дефолти колонок для відредагованих рядків — так само, як фоновий перерахунок (RecalculationService.LoadPeriodAsync).
        var defaults = await new ColumnDefaultValues(metadata, registryStore)
            .ForVersionAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
        foreach (var (columnId, value) in defaults.Of(table.Id))
        {
            foreach (var rowKey in editedRows.Keys)
            {
                values.TryAdd(new CellKey(0, table.Id, rowKey, columnId), CellValueMapping.ToExpressionValue(value, ExpressionValue.Null));
            }
        }
```
   Важливо: це ПІСЛЯ заповнення зі `stored` і ДО `RegistryFieldsAsync` (`:105-106`) — Lookup-дефолт тоді дає `REGFIELD` свій запис.
5. Валідація — `src/Ecr.Application/Documents/ValidateDocumentHandler.cs:121-135` і `src/Ecr.Application/Workflow/SubmitSheetHandler.cs:576-596` (обидва мають `metadata` і `registries` у конструкторі): перед першим використанням `cells` у тілі циклу:
```diff
-            var cells = cellsByInstance.TryGetValue(instance.TableInstanceId, out var found)
-                ? found
-                : [];
             var rowIds = rowIdsByInstance.TryGetValue(instance.TableInstanceId, out var foundRows)
                 ? foundRows
                 : new Dictionary<string, long>(StringComparer.Ordinal);
+
+            // ✎ C-5 (рішення PO 11.11): правила рядка/таблиці й «обов'язкове заповнено» бачать дефолт незаповненої комірки
+            // (02b §6.3) — той самий, що формули. Явна порожнеча лишається порожньою.
+            var cells = (await columnDefaults.ForVersionAsync(snapshot.TemplateVersionId, ct).ConfigureAwait(false))
+                .Complete(
+                    cellsByInstance.TryGetValue(instance.TableInstanceId, out var found) ? found : [],
+                    table.Id, rowIds, periodKey);
```
   (у `SubmitSheetHandler` змінні — `slice`/`ids`, `key`, версія — `templateVersionId`, уже прочитана там; `columnDefaults` у кожному обробнику — поле `private readonly Recalculation.ColumnDefaultValues columnDefaults = new(metadata, registries);`). Обидва викликачі далі передають ці `cells` і в `TableValidation.RunAsync`, і в `MissingRequiredColumnMessages` — обов'язкова колонка з придатним дефолтом вважається заповненою. PATCH-шлях (`PatchCellsHandler.PatchRowValidationContext`, `PatchCellsHandler.cs:3665`) не змінюється: неповний контекст і так позначає ненадіслані комірки «невідомими» (`ValidationEngine.cs:394-401`), повну оцінку дає «Перевірити»/подання.
6. Методології — `src/Ecr.Calculations/MethodologyResolver.cs`: новий останній параметр конструктора `Application.Recalculation.ColumnDefaultValues? defaults = null` (після `:29`); `:229` змінити:
```diff
-        var slice = await cells.ReadSliceAsync(tableInstanceId, period, ct).ConfigureAwait(false);
+        var slice = await cells.ReadSliceAsync(tableInstanceId, period, ct).ConfigureAwait(false);
+
+        // ✎ C-5 (рішення PO 11.11): правило бачить дефолт незаповненої комірки; той самий доповнений зріз іде у входи
+        // (`Read` нижче → CalculationInputBuilder), тож зіставлення й аргументи не розходяться.
+        if (defaults is not null)
+        {
+            slice = (await defaults.ForVersionAsync(instance.TemplateVersionId, ct).ConfigureAwait(false))
+                .Complete(slice, instance.TableDefId, rowIds, period);
+        }
```
   `src/Ecr.Calculations/CalculationInputBuilder.cs:7` — `CalculationInputBuilder(ICellStore cellStore, IMetadataCache metadata, IRowStore rows, Application.Recalculation.ColumnDefaultValues? defaults = null)`; у власній гілці читання (`:46-51`, коли `reusable == false`) після обчислення `rowIds` — `slice = defaults is null ? slice : (await defaults.ForVersionAsync(instance.TemplateVersionId, ct).ConfigureAwait(false)).Complete(slice, instance.TableDefId, rowIds, instancePeriod);` (змінну `slice` оголоси як `IReadOnlyList<CellRecord>`). DI (`src/Ecr.Calculations/DependencyInjection.cs:33-34`) підставить сервіс із кроку 2 сам.
7. Зв'язки — `src/Ecr.Application/Calculations/RelationRecalculator.cs:82-85`: новий параметр `Recalculation.ColumnDefaultValues? defaults = null`; у циклі `:139-165` перед `list.AddRange(RelationCheckRunner.BuildRows(input.Snapshot, effective, rowIds));` — `if (defaults is not null) effective = [.. (await defaults.ForVersionAsync(input.TemplateVersionId, ct).ConfigureAwait(false)).Complete(effective, instance.TableDefId, rowIds, input.PeriodKey)];` (оголоси `effective` як `IReadOnlyList<CellRecord>`). `src/Ecr.Application/Validation/RelationCheckRunner.cs:24-32` — новий останній параметр `Recalculation.ColumnDefaultValues? defaults = null`; у `:70-74` — доповнити `instanceCells` так само перед `BuildRows`; викликачі `ValidateDocumentHandler.cs:161-163` і `SubmitSheetHandler.cs:604-606` передають своє поле `columnDefaults`.
8. `docs/build/02b-expressions.md:486-495` — після таблиці дописати: «✎ 2026-11-11 (C-5, рішення PO): дефолт діє в усіх серверних обчисленнях — формули (усі періоди, діапазони, предикати, агрегати), перегляд рядка, правила валідації й «обов'язкове заповнено», зіставлення й входи методологій, Rollup/Check. Не пишеться ніде (`D-283`); звіти `rpt.*` і Excel-експорт читають збережене значення. Дефолт Lookup — код запису довідника колонки; непридатний дефолт (не розбирається за типом) — як відсутній.»
**Тест-доказ:**
- `tests/Ecr.Application.Tests/Recalculation/CascadeRecalculationTests.cs`: `Незаповнена_комірка_дає_формулі_DefaultValue_колонки` (Factor без `CellValue`, `DefaultValue="1.5"`, Qty=10 → запис Emission=15); `Явна_порожнеча_дефолту_не_отримує` (Factor з `IsEmpty=1` → Emission порожня); `SUM_над_колонкою_рахує_дефолт_незаповнених` (2 рядки, Factor не заповнено, `Total = SUM([T].[R1:R2].[Factor])` → 3.0); `Попередній_період_дає_дефолт` (`[Period:-1].[T].[R1].[Factor]`, у минулому періоді рядок є, комірки немає → 1.5). Без кроку 3 усі (крім другого) падають — запису немає або 0. Підміна `IMetadataCache.GetDerivedAsync` має викликати `load` (як у наявних тестах похідних; перевір допоміжний метод у `TestMetadataCache`/фікстурі тестів перерахунку grep'ом `GetDerivedAsync tests/Ecr.Application.Tests`).
- `tests/Ecr.Application.Tests/Recalculation/ColumnDefaultValuesTests.cs` (новий): розбір за типами — `Decimal "1.5"` → 1.5, `Int "3"`, `Bool "true"`, `Date "2026-01-31"`, `String`, `Lookup "NOTPASSEDOVERHAUL"` → id запису (підміна `FindEntriesByCodesAsync`), `Decimal "abc"` → немає дефолту, `Formula` → немає; `Complete` не замінює наявну/порожню комірку.
- `tests/Ecr.Application.Tests/Recalculation/RowLocalPreviewTests.cs`: `Перегляд_рахує_з_дефолтом` (Qty у правці, Factor дефолтний → Emission=15 у перегляді).
- `tests/Ecr.Application.Tests/Validation/` (поруч із тестами `TableValidation`): обов'язкова колонка з дефолтом і без запису — `MissingRequired` немає; з `IsEmpty=1` — є.
- `tests/Ecr.Calculations.Tests/` (поруч із `CategoryRuleRowRejectionTests.cs`): правило методології `Factor = 1.5` зіставляє рядок без запису Factor; аргумент `@Factor` = 1.5.
**Перевірка після впровадження:** `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~Recalculation|FullyQualifiedName~Validation|FullyQualifiedName~ColumnDefault"`; `dotnet test tests/Ecr.Calculations.Tests`; `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~FormulaJobFixedCost|FullyQualifiedName~PatchCellsQueryCount"`; `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~SubmitSheetQueryCount"`. Стенд: документ із дефолтною колонкою — формула, правило й методологія рахують дефолт; явне очищення комірки — ні.
**Ризики і побічні ефекти:**
- Змінюються числа в чинних документах із дефолтами (після наступного перерахунку) і результати методологій — повідомити UAT.
- Запити: дефолти — похідне від версії (`GetDerivedAsync`): у теплому стані 0 звернень; холодна побудова — 0 звернень для версії без Lookup-дефолтів і одне `FindEntriesByCodesAsync` на довідник із Lookup-дефолтами. `FormulaJobFixedCostTests` (теплі раунди, `FormulaJobFixedCostTests.cs:150-156`), `PatchCellsQueryCountTests`, `SubmitSheetQueryCountTests` (шаблони без Lookup-дефолтів) — стелі не змінюються. Перевір, що `Metadata(doc)` (`SubmitSheetQueryCountTests.cs:427`) підтримує `GetDerivedAsync` (інакше — додати делегування до `load` у підміні тесту, без зміни стелі).
- Звіти (`rpt.*`) і Excel-експорт показують збережене (порожнє) — свідомо, `D-283`; позначено в 02b.
- Код Lookup-дефолту, якого немає в довіднику, — дефолту немає (і похідне живе лише вікно ревізії, тож новий запис підхопиться).
**Обґрунтування:** підстановка при ЧИТАННІ в одному місці кожного шляху (значення контексту / доповнений зріз) — без запису в `doc.CellValue` (`D-283`) і без розгалужень у правилах. Варіант «матеріалізувати при створенні рядка» PO відхилив (міняє «не заповнювали» на «заповнено», R-B4).
**Рішення PO:** отримано 11.11 — «(а)».
**Впевненість:** підтверджено читанням; формат дефолтів у чинних шаблонах стенда (числа з комою, коди Lookup) — потребує перевірки на стенді (`SELECT Id, DataType, DefaultValue FROM cfg.ColumnDef WHERE DefaultValue IS NOT NULL`).
**Залежності:** після C-1 (спільні файли; `PeriodLoadExtras`); після P-3 і P-5 (`TableValidation.cs`, `ValidationEngine.cs`, лінія L7 — `00-PLAN.md` §3).

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
**✎ рішення PO 11.11:** «так» на пропозицію координатора (`00-PLAN.md` §4). Дозвіл — первинний і може охоплювати кілька періодів; значення комірки між двома дозволами користувач ділить ВРУЧНУ (як сьогодні для місяця). Одиниця поділу для колонки БЕЗ місяця — сам період комірки (квартал/рік) цілими календарними місяцями, ключ частин — `PeriodKey` комірки; НЕ «місяць» із `PeriodKey.Sequence`. Автоматичного поділу немає. Місячні колонки й місячні проєкти — побайтно як зараз (доказ — тести нижче). Пріоритет — після перевірки на стенді, чи є квартальні/річні проєкти з історією дозволів (`00-PLAN.md` §5).
**Категорія:** correctness
**Де:**
- `src/Ecr.Application/Permits/History/PermitHistorySupport.cs:375-386` (`DataCellsInMonth`: для таблиці без місячних колонок — `period.Year == month.Year && period.Sequence == month.Month`, `:381`); `:298-347` (`RowDto`: ітерація `PermitTimeline.SharedMonths`, `:302`; ключ частин `monthKey`, `:304`, `:315`; дні частин від календарного місяця, `:305-311`); `:264-280` (`RowAsync`: ключі частин із `SharedMonths`, `:277-278`); запис рядка історії `RowTarget` — `:247-261`.
- `src/Ecr.Application/Documents/PatchCellsHandler.PermitValidity.cs:459-469` (`MonthOf`: для колонки без місяця — `new DateOnly(year, context.PeriodKey.Sequence, 1)`, `:461`; спільність — `SharedMonths(...)`, `:466-467`), `:480` (`InMonth`), `:512`/`:518` (`month` у тексті й `Details` відмови 4225); `:561-566` (очищення частин: ключ `(year * 100) + month`, де `month = … : context.PeriodKey.Sequence`). Межі періоду вже читаються в правилі (h): `:148-150`.
- `src/Ecr.Application/Permits/History/SaveCellSplitsHandler.cs:239-258` (`monthNumber = column.MonthNumber ?? period.Sequence`, `InMonth`, дні частин), `:111`, `:125` (ключ частин `monthKey`), `:146` (`month` у JSON аудиту).
- `src/Ecr.Application/Permits/History/PermitHistoryHandlers.cs:491` (`SharedMonths(headerSpans)`), `:520` (`DataCellsInMonth` для рядків, що покладаються на шапку), `:539-543` (ключі частин), `RowTarget` — `:480-482`, `:525-527`.
- `src/Ecr.Application/Validation/PermitCoverageIssues.cs:187-195` (ключі частин із місяців `ValidFrom`), `:197` (межі періоду — уже читаються), `:310-345` (`HistoryFindings`: `SharedMonths`/`InMonth`/`DataCellsInMonth`, `:317`), `:394-450` (`HeaderSplitFindingsAsync`, `:427`).
- `src/Ecr.Infrastructure/Persistence/Sql/05-rpt-views.sql:1550-1555` (місяць комірки `COALESCE(cd.MonthNumber, cv.PeriodKey % 100)`, `MonthKey`), `:1608` (вікно змін історії в межах місяця).
- Правило: `src/Ecr.Domain/ValueObjects/PeriodKey.cs:9-11` («виводити місяць арифметикою заборонено»); межі періодів — календарні місяці (`src/Ecr.Domain/Services/PeriodCalendar.cs:181-197`), читаються `IPeriodStore.FindPeriodBoundsAsync` (`src/Ecr.Application/Ports/IPeriodStore.cs:153-164`). Вікно дат історії — рік (`PermitHistorySupport.cs:151-155`), тож дозвіл може змінитись усередині кварталу.
- Клієнт показує одиницю як місяць: `src/Ecr.Web/src/features/permits/history/CellSplitDialog.tsx:64-65`, `HeaderSplitBanner.tsx:50-51`, `HeaderSplitDialog.tsx:69-70` (`monthOf(month.month)` → `formatMonthYear`); DTO — `src/Ecr.Application/Permits/History/PermitHistoryContracts.cs:53-54` (`SplitMonthDto(DateOnly Month, …)`).
**Проблема:** для таблиці без місячних колонок «місяць комірки» = `PeriodKey.Sequence`; у квартальному проєкті Sequence 2 — це Q2 (квітень–червень), а не лютий. Тому зміна дозволу 15.05 (усередині Q2) поділу не вимагає, а зміна 15.02 (поза Q2) вимагає поділу «лютого» для всіх комірок Q2.
**Сценарій відтворення:** квартальний проєкт, таблиця без місячних колонок покладається на дозвіл шапки. (а) Шапка A → B з 15.02.2026, документ Q2 (ключ 202602): введення числа → 422 `ECR-CELL-4225 splitRequired «month 02.2026»`; Issues/Validate вимагають поділу «лютого» (PERMIT-SPLIT блокує подання) — хибно, лютий не в Q2. (б) Шапка A → B з 15.05.2026, документ Q2: введення числа приймається одним значенням, поділу не вимагається — хибно, Q2 ділять два дозволи. Очікувано (PO 11.11): у (а) нічого не вимагається; у (б) — 4225 «04.2026–06.2026» і поділ Q2 на дві частини (A: 01.04–14.05, B: 15.05–30.06), ключ частин 202602.
**Вплив:** квартальні/річні проєкти з історією дозволів: хибні відмови й блокування подання в одних періодах, пропущений поділ в інших; звіт `rpt.v_DocumentCellsByPermit` зараховує значення не тому дозволу. Наявність таких проєктів — потребує перевірки на стенді (Land/HSE401 — місячні).
**Зміна — покроково:**
1. `src/Ecr.Application/Permits/History/PermitTimeline.cs` — нова одиниця поділу (після `DayRange`, `:8-15`) і два методи в `PermitTimeline` (поруч із `InMonth`, `:46-52`):
```csharp
/// <summary>
/// ✎ C-10 (рішення PO 11.11): одиниця поділу значення між дозволами — календарний місяць (місячна колонка) або сам
/// період комірки цілими календарними місяцями (колонка без місяця: квартал/рік; у місячному проєкті — той самий
/// місяць). <see cref="PartKey"/> — ключ <c>doc.CellPermitPart.PeriodKey</c>.
/// </summary>
public sealed record PermitSplitUnit(DateOnly First, DateOnly Last, int PartKey)
{
    /// <summary>Календарний місяць; ключ частин — <c>YYYYMM</c> (як досі).</summary>
    public static PermitSplitUnit Month(DateOnly month)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        return new PermitSplitUnit(first, first.AddMonths(1).AddDays(-1), (first.Year * 100) + first.Month);
    }

    /// <summary>Період комірки: від 1-го числа місяця початку до кінця місяця кінця; ключ частин — сам <c>PeriodKey</c>.</summary>
    public static PermitSplitUnit OfPeriod(Domain.ValueObjects.PeriodKey period, Ports.PeriodBounds bounds)
    {
        var first = new DateOnly(bounds.PeriodStart.Year, bounds.PeriodStart.Month, 1);
        var lastMonth = new DateOnly(bounds.PeriodEnd.Year, bounds.PeriodEnd.Month, 1);
        return new PermitSplitUnit(first, lastMonth.AddMonths(1).AddDays(-1), period.Value);
    }

    public bool IsSingleMonth => First.Year == Last.Year && First.Month == Last.Month;

    /// <summary>Підпис для текстів і <c>Details</c>: <c>MM.yyyy</c> або <c>MM.yyyy–MM.yyyy</c>.</summary>
    public string Label => IsSingleMonth
        ? First.ToString("MM.yyyy", CultureInfo.InvariantCulture)
        : First.ToString("MM.yyyy", CultureInfo.InvariantCulture) + "–" + Last.ToString("MM.yyyy", CultureInfo.InvariantCulture);
}
```
```csharp
    /// <summary>✎ C-10: дозволи, чинні хоч день одиниці (для місяця — те саме, що <see cref="InMonth"/>).</summary>
    public static IReadOnlyList<PermitSpan> InUnit(IReadOnlyList<PermitSpan> spans, PermitSplitUnit unit)
    {
        ArgumentNullException.ThrowIfNull(spans);
        ArgumentNullException.ThrowIfNull(unit);
        return [.. spans.Where(s => (s.From is null || s.From <= unit.Last) && (s.Until is null || s.Until >= unit.First))];
    }

    /// <summary>✎ C-10: одиницю ділять дозволи — зміна історії з датою ВСЕРЕДИНІ (не з першого дня; для місяця — те саме, що <see cref="SharedMonths"/>).</summary>
    public static bool IsShared(IReadOnlyList<PermitSpan> spans, PermitSplitUnit unit)
    {
        ArgumentNullException.ThrowIfNull(spans);
        ArgumentNullException.ThrowIfNull(unit);
        return spans.Any(s => s.From is { } from && from > unit.First && from <= unit.Last);
    }
```
   (`using Ecr.Application.Ports;` — `PeriodBounds` з `IPeriodStore.cs:164`.)
2. `src/Ecr.Application/Permits/History/PermitHistorySupport.cs` — у `PermitHistoryReader`:
   - `RowTarget` (`:247-250`) — новий останній параметр `Ports.PeriodBounds? Bounds = null` (після `HeaderFieldCode`).
   - нові методи (поруч із `DataCellsInMonth`, `:370-386`; сам `DataCellsInMonth` лишити як обгортку `=> DataCellsInUnit(..., PermitSplitUnit.Month(month))`, щоб не зламати інших викликачів до кінця заміни):
```csharp
    /// <summary>
    /// ✎ C-10 (рішення PO 11.11): одиниці, які рядок має поділити в цьому періоді. Місячні колонки — спільні місяці
    /// (як досі). Колонки без місяця: квартальний/річний період — сам період, якщо зміна історії всередині нього;
    /// місячний період або межі невідомі — побайтно як досі (усі спільні місяці історії).
    /// </summary>
    public static IReadOnlyList<PermitSplitUnit> SplitUnits(
        TableDef table, PeriodKey period, PeriodBounds? bounds, IReadOnlyList<PermitSpan> spans)
    {
        var months = PermitTimeline.SharedMonths(spans).Select(PermitSplitUnit.Month).ToList();
        if (PermitMonthCoverage.MonthColumns(table).Count > 0 || bounds is null)
        {
            return months;
        }

        var unit = PermitSplitUnit.OfPeriod(period, bounds);
        if (unit.IsSingleMonth)
        {
            return months;
        }

        return PermitTimeline.IsShared(spans, unit) ? [unit] : [];
    }

    /// <summary>✎ C-10: введені числові комірки рядка, що належать одиниці (для колонки без місяця — одиниця = період комірки).</summary>
    public static IReadOnlyList<ColumnDef> DataCellsInUnit(
        TableDef table, PeriodKey period, IReadOnlyDictionary<int, CellValueData> cells, IReadOnlySet<int> skip, PermitSplitUnit unit)
    {
        var monthColumns = PermitMonthCoverage.MonthColumns(table);
        IEnumerable<ColumnDef> candidates = monthColumns.Count > 0
            ? monthColumns.Where(c => period.Year == unit.First.Year && c.MonthNumber == unit.First.Month)
            : unit.PartKey == period.Value   // місячний проєкт: Month(m).PartKey = YYYYMM = PeriodKey лише для свого місяця
                ? table.Columns.Where(c => !c.IsDeleted && c.DataType is CellDataType.Decimal or CellDataType.Int)
                : [];
        return [.. candidates.Where(c => !skip.Contains(c.Id)
                                         && cells.TryGetValue(c.Id, out var v) && PermitMonthCoverage.HasData(v) && v.ValueNumeric is not null)];
    }
```
   - `RowDto` (`:298-347`): `foreach (var month in PermitTimeline.SharedMonths(row.Spans))` → `foreach (var unit in SplitUnits(row.Table, row.Period, row.Bounds, row.Spans))`; `monthKey` → `unit.PartKey`; `InMonth(row.Spans, month)` → `InUnit(row.Spans, unit)`; дні частин — `s.From is { } f && f > unit.First ? f : unit.First` і `s.Until is { } u && u < unit.Last ? u : unit.Last` (замість `month`/`last`); `DataCellsInMonth(…, month)` (`:323`, `:335`) → `DataCellsInUnit(…, unit)`; фільтр місячних колонок (`:331`) — `c.MonthNumber == unit.First.Month && row.Period.Year == unit.First.Year`; запис — `split.Add(new SplitMonthDto(unit.First, pending, spans, cellValues, unit.IsSingleMonth ? null : unit.Last));`.
   - `RowAsync` (`:264-280`): новий параметр `PeriodBounds? bounds = null` (останній) → у `new RowTarget(..., Bounds: bounds)`; ключі частин `:277` → `SplitUnits(target.Table, target.Period, bounds, target.Spans).Select(u => u.PartKey).ToList()`.
3. `src/Ecr.Application/Permits/History/PermitHistoryContracts.cs:53-54`:
```diff
 public sealed record SplitMonthDto(
-    DateOnly Month, IReadOnlyList<string> ColumnCodes, IReadOnlyList<PermitPartSpanDto> Parts, IReadOnlyList<CellSplitValueDto> Cells);
+    DateOnly Month, IReadOnlyList<string> ColumnCodes, IReadOnlyList<PermitPartSpanDto> Parts, IReadOnlyList<CellSplitValueDto> Cells,
+    DateOnly? MonthTo = null);
```
   (`/// <param name="MonthTo">✎ C-10: останній день одиниці, коли вона довша за місяць (квартал/рік); <c>null</c> — місяць.</param>`.)
4. `src/Ecr.Application/Permits/History/PermitHistoryHandlers.cs`:
   - `AddRowPermitHandler` (`:36-50`) і `UndoRowPermitHandler` (`:143-156`) — новий останній параметр `IPeriodStore? periods = null`; перед `return await PermitHistoryReader.RowAsync(...)` (`:135`, `:199`) — `var bounds = periods is null ? null : await periods.FindPeriodBoundsAsync(documentId, row.Instance.PeriodKey, ct).ConfigureAwait(false);` і передати `bounds` у `RowAsync`.
   - `GetPermitHistoryHandler` (`:399-409`) — новий останній параметр `IPeriodStore? periods = null`; після `var period = new PeriodKey(periodKey);` (`:424`) — `var bounds = periods is null ? null : await periods.FindPeriodBoundsAsync(documentId, periodKey, ct).ConfigureAwait(false);`; обидва `new PermitHistoryReader.RowTarget(...)` (`:480-482`, `:525-527`) — `Bounds: bounds`; `:491` `var shared = PermitTimeline.SharedMonths(headerSpans);` лишити для раннього виходу, а умову рядка `:520` замінити на `!PermitHistoryReader.SplitUnits(table, period, bounds, headerSpans).Any(u => PermitHistoryReader.DataCellsInUnit(table, period, byColumn, new HashSet<int>(), u).Count > 0)`; ранній вихід `if (shared.Count > 0 && relying.Count > 0)` (`:496`) → `if ((shared.Count > 0 || bounds is not null) && relying.Count > 0)`; ключі частин `:539-542` → `rowTargets.SelectMany(r => PermitHistoryReader.SplitUnits(r.Table, r.Period, r.Bounds, r.Spans)).Select(u => u.PartKey).Distinct().ToList()`.
5. `src/Ecr.Application/Documents/PatchCellsHandler.PermitValidity.cs` — `EnsureSharedMonthsAreSplitAsync` `:459-469`:
```diff
-        var year = context.PeriodKey.Year;
-        DateOnly MonthOf(int columnId)
-            => monthOf.TryGetValue(columnId, out var m) ? new DateOnly(year, m, 1) : new DateOnly(year, context.PeriodKey.Sequence, 1);
+        var year = context.PeriodKey.Year;
+
+        // ✎ C-10 (рішення PO 11.11): колонка без місяця ділиться за періодом комірки (квартал/рік), не за «місяцем» із
+        // Sequence (PeriodKey.cs:9-11). Межі — лише коли є така колонка (місячна таблиця межі не читає).
+        var bounds = monthOf.Count > 0
+            ? null
+            : await periods.FindPeriodBoundsAsync(context.Instance.DocumentId, context.PeriodKey.Value, ct).ConfigureAwait(false);
+        Permits.History.PermitSplitUnit UnitOf(int columnId)
+            => monthOf.TryGetValue(columnId, out var m)
+                ? Permits.History.PermitSplitUnit.Month(new DateOnly(year, m, 1))
+                : bounds is not null
+                    ? Permits.History.PermitSplitUnit.OfPeriod(context.PeriodKey, bounds)
+                    : Permits.History.PermitSplitUnit.Month(new DateOnly(year, context.PeriodKey.Sequence, 1));
 
         var hits = writes
-            .Select(w => (Write: w, Month: MonthOf(w.Address.ColumnDefId),
+            .Select(w => (Write: w, Unit: UnitOf(w.Address.ColumnDefId),
                           History: history.FirstOrDefault(h => h.Key.RowId == w.Address.TableRowId
-                              && Permits.History.PermitTimeline.SharedMonths(
-                                  Permits.History.PermitHistoryCoverage.SpansOf(0, h.Value)).Contains(MonthOf(w.Address.ColumnDefId)))))
+                              && Permits.History.PermitTimeline.IsShared(
+                                  Permits.History.PermitHistoryCoverage.SpansOf(0, h.Value), UnitOf(w.Address.ColumnDefId)))))
```
   далі `:480` `InMonth(spans, first.Month)` → `InUnit(spans, first.Unit)`; текст відмови `:512` `$"Місяць {first.Month:MM.yyyy} рядка …"` → `$"Період {first.Unit.Label} рядка …"`; `Details["month"]` (`:518`) → `first.Unit.Label`. (Останній запасний `Month(..Sequence..)` — лише коли періоду немає в `doc.Period`, тобто старої поведінки; для місячного періоду `OfPeriod` = той самий місяць, той самий `PartKey`.) `periods` — поле обробника, вже використане в `:148`.
   - `ClearPartsOfClearedCellsAsync` `:561-564`:
```diff
-            var month = monthOf.TryGetValue(address.ColumnDefId, out var m) ? m : context.PeriodKey.Sequence;
-            var parts = await permitHistory.CellPartsAsync(
-                    (year * 100) + month, context.Instance.DocumentId, context.Instance.TableDefId, context.Instance.SheetCopyId,
+            // ✎ C-10: ключ частин колонки без місяця — сам PeriodKey комірки (= YYYYMM у місячному проєкті, як досі).
+            var partKey = monthOf.TryGetValue(address.ColumnDefId, out var m) ? (year * 100) + m : context.PeriodKey.Value;
+            var parts = await permitHistory.CellPartsAsync(
+                    partKey, context.Instance.DocumentId, context.Instance.TableDefId, context.Instance.SheetCopyId,
```
6. `src/Ecr.Application/Permits/History/SaveCellSplitsHandler.cs`:
   - конструктор (`:18-37`) — новий останній параметр `IPeriodStore? periods = null`;
   - `PlanAsync` `:239-258`:
```diff
-        var monthNumber = column.MonthNumber is { } m ? (int)m : period.Sequence;
-        var month = new DateOnly(period.Year, monthNumber, 1);
-        var inMonth = PermitTimeline.InMonth(spans, month);
-        if (inMonth.Count < 2)
+        // ✎ C-10 (рішення PO 11.11): місячна колонка — свій місяць; колонка без місяця — період комірки (квартал/рік).
+        var bounds = column.MonthNumber is null && periods is not null
+            ? await periods.FindPeriodBoundsAsync(documentId, instance.PeriodKey, ct).ConfigureAwait(false)
+            : null;
+        var unit = column.MonthNumber is { } m
+            ? PermitSplitUnit.Month(new DateOnly(period.Year, m, 1))
+            : bounds is not null
+                ? PermitSplitUnit.OfPeriod(period, bounds)
+                : PermitSplitUnit.Month(new DateOnly(period.Year, period.Sequence, 1));
+        var inMonth = PermitTimeline.InUnit(spans, unit);
+        if (inMonth.Count < 2)
         {
             throw PartsInvalid("Цей місяць не ділиться між дозволами.");
         }
```
     і нижче `var last = month.AddMonths(1).AddDays(-1);` прибрати, дні частин — `s.From is { } f && f > unit.First ? f : unit.First`, `s.Until is { } u && u < unit.Last ? u : unit.Last`; `return new Plan(..., unit, parts, item.Parts, total);`; у `Plan` (`:276-279`) поле `DateOnly Month` → `PermitSplitUnit Unit`.
   - `:111` `var monthKey = (plan.Month.Year * 100) + plan.Month.Month;` → `var monthKey = plan.Unit.PartKey;`; `:146` `month = plan.Month.ToString("yyyy-MM", …)` → `month = plan.Unit.IsSingleMonth ? plan.Unit.First.ToString("yyyy-MM", CultureInfo.InvariantCulture) : $"{plan.Unit.First.ToString("yyyy-MM", CultureInfo.InvariantCulture)}..{plan.Unit.Last.ToString("yyyy-MM", CultureInfo.InvariantCulture)}"` (місячний рядок аудиту — без змін; підпис журналу бере його текстом, `AuditReader.PermitLabels.cs:134`, `:157`).
   - Ручний поділ НЕ змінюється за змістом: усі частини вводить людина, сума — лише коли введено всі (`:263-264`, «No pro rata, ever»).
7. `src/Ecr.Application/Validation/PermitCoverageIssues.cs`:
   - ключі частин `:189-194` — до множини місяців `ValidFrom` додати `periodKey.Value` (`… .Distinct()` → `… .Append(periodKey.Value).Distinct()`), та сама одна команда;
   - `HistoryFindings` (`:273-286`) — новий параметр `Ports.PeriodBounds? bounds` (передати `bounds` з `:197` у виклик `:229-231`); цикл `:310-345`: `foreach (var unit in Permits.History.PermitHistoryReader.SplitUnits(table, periodKey, bounds, spans))`, `InUnit`, `unit.PartKey` замість `monthKey`, `DataCellsInUnit(…, unit)`, `["month"] = unit.Label`;
   - `HeaderSplitFindingsAsync` (`:356-368`) — новий параметр `IPeriodStore periods` (виклик `:109-111` передає `periods`); після `var spans = …` (`:393`) — `var bounds = relying.Any(i => PermitMonthCoverage.MonthColumns(tables[i.TableDefId]).Count == 0) ? await periods.FindPeriodBoundsAsync(documentId, periodKey.Value, ct).ConfigureAwait(false) : null;`; ранній вихід `:395-398` → якщо `SharedMonths(spans).Count == 0` І (`bounds is null` або `!IsShared(spans, OfPeriod(periodKey, bounds))`); ключі частин `:402` — місяці `shared` плюс `periodKey.Value`; цикл `:420` — `foreach (var unit in Permits.History.PermitHistoryReader.SplitUnits(table, periodKey, bounds, spans))` з тими самими замінами, що в `HistoryFindings`.
8. `src/Ecr.Infrastructure/Persistence/Sql/05-rpt-views.sql` — `:1550-1555`:
```diff
--- Місяць комірки (NULL — номер поза 1..12: лише кінець періоду, без поділу).
-CROSS APPLY (SELECT CASE WHEN COALESCE(cd.MonthNumber, cv.PeriodKey % 100) BETWEEN 1 AND 12
-                         THEN DATEFROMPARTS(cv.PeriodKey / 100, COALESCE(cd.MonthNumber, cv.PeriodKey % 100), 1) END AS MonthStart) AS mo
-CROSS APPLY (SELECT CASE WHEN cd.MonthNumber IS NOT NULL THEN EOMONTH(mo.MonthStart)
-                         ELSE COALESCE(per.PeriodEnd, EOMONTH(mo.MonthStart)) END AS AsOf,
-                    YEAR(mo.MonthStart) * 100 + MONTH(mo.MonthStart) AS MonthKey) AS dt
+-- Одиниця поділу комірки. ✎ C-10 (рішення PO 11.11): місячна колонка — свій місяць (як було); колонка без місяця — сам
+-- період комірки цілими місяцями (квартал/рік; у місячному проєкті — той самий місяць), ключ частин — PeriodKey комірки.
+-- NULL — одиниці немає (номер місяця поза 1..12 і період невідомий): лише кінець періоду, без поділу.
+CROSS APPLY (SELECT CASE WHEN cd.MonthNumber IS NOT NULL
+                         THEN CASE WHEN cd.MonthNumber BETWEEN 1 AND 12 THEN DATEFROMPARTS(cv.PeriodKey / 100, cd.MonthNumber, 1) END
+                         WHEN per.PeriodStart IS NOT NULL
+                         THEN DATEFROMPARTS(YEAR(per.PeriodStart), MONTH(per.PeriodStart), 1)
+                         WHEN cv.PeriodKey % 100 BETWEEN 1 AND 12
+                         THEN DATEFROMPARTS(cv.PeriodKey / 100, cv.PeriodKey % 100, 1) END AS MonthStart) AS mo
+CROSS APPLY (SELECT CASE WHEN cd.MonthNumber IS NOT NULL THEN EOMONTH(mo.MonthStart)
+                         ELSE COALESCE(per.PeriodEnd, EOMONTH(mo.MonthStart)) END AS AsOf,
+                    CASE WHEN cd.MonthNumber IS NOT NULL OR per.PeriodEnd IS NULL THEN EOMONTH(mo.MonthStart)
+                         ELSE EOMONTH(per.PeriodEnd) END AS UnitEnd,
+                    CASE WHEN cd.MonthNumber IS NOT NULL THEN YEAR(mo.MonthStart) * 100 + MONTH(mo.MonthStart)
+                         ELSE cv.PeriodKey END AS MonthKey) AS dt
```
   і в підзапиті кількості дозволів (`:1608`) `AND s.ValidFrom <= EOMONTH(mo.MonthStart)` → `AND s.ValidFrom <= dt.UnitEnd`. Для місячного проєкту (`per.PeriodStart` — у місяці `PeriodKey % 100`, `PeriodCalendar.cs:186-195`) `MonthStart`, `UnitEnd = EOMONTH(місяця)` і `MonthKey = PeriodKey` збігаються з колишніми — рядки вʼюхи ті самі. Оновити коментар заголовка вʼюхи (`:1515-1517`, речення «Місяць комірки — як у застосунку (`SaveCellSplitsHandler`)… інакше номер періоду») на «…колонка без місяця — період комірки». Файл перевикористовується розгортанням (`CREATE OR ALTER VIEW`, `:1520`) — upgrade ідемпотентний; у `docs/CHECKSUMS.txt` його немає.
9. Клієнт — `src/Ecr.Web/src/features/permits/history/splitModel.ts` (поруч із `monthOf`, `:126`):
```ts
/** ✎ C-10: підпис одиниці поділу — місяць або «квітень 2026 – червень 2026» для кварталу/року (`monthTo`). */
export function splitUnitLabel(month: Pick<SplitMonth, 'month' | 'monthTo'>): string {
  const from = monthOf(month.month);
  const fromLabel = formatMonthYear(from.year, from.month);
  if (month.monthTo === null || month.monthTo === undefined) return fromLabel;
  const to = monthOf(month.monthTo);

  return to.year === from.year && to.month === from.month ? fromLabel : `${fromLabel} – ${formatMonthYear(to.year, to.month)}`;
}
```
   і в `CellSplitDialog.tsx:64-65`, `HeaderSplitBanner.tsx:50-51`, `HeaderSplitDialog.tsx:69-70` замінити пару рядків `const { year, month } = … monthOf(….month.month); const monthLabel = formatMonthYear(year, month);` на `const monthLabel = … splitUnitLabel(….month)` (з тією самою перевіркою на `null`/`undefined`, що була). Тексти (`permits.split.title`, `permits.split.why`…) мають плейсхолдер `{month}` — підпис кварталу туди лягає без нових ключів.
10. Контракт: `docs/build/02-contracts.md` — поруч з описом історії дозволів (рядок із `SplitMonthDto`/`splitMonths`; знайти `git grep -n "splitMonths" docs/build/02-contracts.md`) дописати «✎ 2026-11-11 (C-10): `splitMonths[].monthTo` — останній день одиниці, коли вона довша за місяць (колонка без місяця в квартальному/річному проєкті: одиниця — сам період, ключ частин — `PeriodKey`); `null` — місяць. `ECR-CELL-4225 month` — `MM.yyyy` або `MM.yyyy–MM.yyyy`.»; `contracts/openapi.snapshot.json` і `src/Ecr.Web/src/api/schema.d.ts` — регенерувати (як C-1e.6).
**Тест-доказ:**
- `tests/Ecr.Application.Tests/Permits/PermitTimelineTests.cs` — нові: `Одиниця_місяць_дорівнює_InMonth_і_SharedMonths` (для кількох історій `InUnit(spans, Month(m))` ≡ `InMonth(spans, m)`, `IsShared(spans, Month(m))` ≡ `SharedMonths(spans).Contains(m)`); `Квартал_ділиться_зміною_всередині_і_не_ділиться_зміною_поза` (Q2 = `OfPeriod(202602, 01.04–30.06)`: зміна 15.05 → shared, дві частини; зміна 15.02 → не shared; зміна 01.04 → не shared); `Місячний_період_дає_ту_саму_одиницю_що_місяць` (`OfPeriod(202602, 01.02–28.02)` = `Month(01.02)` за `First/Last/PartKey`; і для обрізаного початку проєкту `15.01–31.01` = `Month(01.01)`).
- `tests/Ecr.Api.Tests/DocumentPermitHistoryTests.Quarterly.cs` (нова частина `partial class DocumentPermitHistoryTests`, на справжньому хості): після `ArrangeAsync(null)` перевести проєкт у квартальний (`UPDATE doc.Project SET PeriodKind = 1 WHERE Id = @p`; `UPDATE doc.Period SET PeriodStart = '2026-01-01', PeriodEnd = '2026-03-31' WHERE ProjectId = @p AND PeriodKey = 202601`), шапка A → B з 15.02: введення в комірку → 422 `ECR-CELL-4225`, `month = "01.2026–03.2026"`; `PUT …/splits` з двома частинами → 200, у `doc.CellPermitPart` дві частини з `PeriodKey = 202601`, днями 01.01–14.02 і 15.02–31.03, сума записана; Validate без `PERMIT-SPLIT`. Друга перевірка: шапка A → B з 15.05 (поза Q1) → введення приймається одним значенням, PERMIT-SPLIT немає. Без кроків 5–7 падає: перша — 4225 не виникає (місяць = січень, а зміна в лютому), друга — проходить і без зміни (контроль).
- Місячні проєкти незмінні: наявні `tests/Ecr.Api.Tests/DocumentPermitHistoryTests.cs`, `.Split.cs`, `.Import.cs` (місячний документ 202601) мають пройти БЕЗ змін очікувань (зокрема `month = "01.2026"` у 4225 і `PeriodKey = 202601` частин), плюс `tests/Ecr.Infrastructure.Tests/Reporting/DocumentCellsByPermitViewTests.cs` без змін; новий кейс там само — квартальний документ: рядки вʼюхи для поділеної комірки беруть частини з ключем `PeriodKey` комірки.
**Перевірка після впровадження:** `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~Permit"`; `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~DocumentPermitHistory|FullyQualifiedName~PatchCellsQueryCount|FullyQualifiedName~OpenApiSnapshot"`; `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~DocumentCellsByPermitView|FullyQualifiedName~RptContract|FullyQualifiedName~SubmitSheetQueryCount"`; `cd src/Ecr.Web && npx vitest run src/features/permits/history && npx tsc --noEmit`. Стенд: перед злиттям — `SELECT p.Id, p.PeriodKind FROM doc.Project p WHERE p.PeriodKind <> 0 AND EXISTS (SELECT 1 FROM doc.Document d JOIN doc.PermitSegment s ON s.DocumentId = d.Id WHERE d.ProjectId = p.Id)` (чи є кого зачепити); e2e `permitSplit.spec.ts`, `permitHeaderSplit.spec.ts` — зелені без змін.
**Ризики і побічні ефекти:**
- Місячні проєкти: усі нові гілки для місячного періоду повертають ту саму одиницю, ключ і дні (`SplitUnits` — гілка `IsSingleMonth`, `UnitOf`/`PlanAsync` — `OfPeriod` = місяць, вʼюха — ті самі вирази); доводять наявні тести без змін очікувань.
- Запити: +1 (`FindPeriodBoundsAsync`) лише на шляхах із історією дозволів у таблиці без місячних колонок — PATCH (після перевірки історії), `PUT …/splits` (на колонку без місяця), GET історії (раз на запит, коли історія є), «Перевірити»/подання (у `HeaderSplitFindingsAsync`, коли є рядки, що покладаються на шапку). Тести фіксованої кількості запитів таких даних не мають (`PatchCellsQueryCountTests`, `SubmitSheetQueryCountTests` — без історії дозволів).
- Частини квартального проєкту, вже збережені за «місячним» ключем (якщо такі є), перестануть зараховуватися — потребує перевірки на стенді (запит вище); за наявності — окрема міграція даних за рішенням людини.
- Контракт: необов'язкове `monthTo` (старі клієнти ігнорують).
**Обґрунтування:** одиниця = період комірки — пряме рішення PO; узагальнення «місяць → одиниця» в одному типі (`PermitSplitUnit`) з гілкою «місячний період — як досі» дає ту саму поведінку місячних проєктів за побудовою, а не за збігом. Альтернативи «не ділити» і «пропорційно за днями» PO відхилив.
**Рішення PO:** отримано 11.11 — одиниця — період комірки, лише ручний поділ.
**Впевненість:** підтверджено читанням; наявність квартальних/річних проєктів з історією — потребує перевірки на стенді.
**Залежності:** після C-9 і D-1/D-3/C-13 у лінії L5 (`PermitHistoryHandlers.cs`, `PermitHistorySupport.cs`, `SaveCellSplitsHandler.cs`); `PatchCellsHandler.PermitValidity.cs` — не перетинається з лінією L3 (`PatchCellsHandler.Workbook.cs`); контракт — по черзі з C-1, L16, L17.

### C-11 — Середня — Посилання `[Period:-n].[T].[WHERE …]` фільтрує рядки поточного періоду
**✎ рішення PO 11.11:** варіант «(б)» — ПІДТРИМАТИ `[Period:-n].[T].[WHERE …].[Col]` (і `[Period:#k]…`): умова перевіряється на рядках ЗСУНУТОГО періоду, значення беруться з тих самих рядків. Попередню рекомендацію «відхиляти при публікації» скасовано. Виконується ПІСЛЯ C-1 і C-5 (спільні `SliceEvaluationContext.cs`, `RecalculationService.cs`, клас `PeriodLoadExtras`).
**Категорія:** correctness
**Де:**
- Обчислення: `src/Ecr.Application/Recalculation/SliceEvaluationContext.cs:331-335` (зсув посилання), `:371-375` (виклик предиката з `periodOffset`), `:413-449` (`EvaluatePredicate`: рядки — `_rowsByTable` ПОТОЧНОГО періоду, `:416`, `:431`; умова читає `[Current]`, тобто зсув 0, `:434-435`; значення — `Value(tableDefId, rowKey, columnDefId, periodOffset)` за ключем поточного рядка, `:438`); джерело рядків — конструктор, `:119`, `:139`.
- Розбір і зв'язування форму приймають: `src/Ecr.Expressions/Parsing/Parser.cs:1153-1212` (зсув — перший сегмент, предикат — передостанній); `src/Ecr.Expressions/Binding/ReferenceResolver.cs:94` (`FilterJson` + `PeriodOffset` + `AbsolutePeriod`); `src/Ecr.Expressions/Binding/PredicateValidator.cs:100-108` забороняє міжперіодне посилання лише ВСЕРЕДИНІ умови.
- Граф: `src/Ecr.Expressions/Binding/DependencyExtractor.cs:203-213`, `:241-243` — для предикатного посилання одне ребро (`DependsOnKind` 3/5, `ColumnDefId` = колонка значення, `RowKey = null`, `FilterJson`); у колонки умови ребер немає (`Visit` не спускається в умову — `src/Ecr.Application/Recalculation/RecalculationReadScope.cs:28-37`).
- Завантаження сусідніх періодів: `src/Ecr.Application/Recalculation/RecalculationService.cs:1801-1835` (зсуви з виразів цілей, `RecalculationReadScope.PeriodReferences`, `RecalculationReadScope.cs:91-…`), `LoadPeriodAsync` `:1921-2016` — ключі рядків сусіднього періоду вже читаються (`GetRowIdsBatchAsync`, `:1975-1976`) і використовуються лише для значень (`:1983-1985`), у контекст не потрапляють.
- Віяло на періоди-читачі: `src/Ecr.Application/Recalculation/CrossPeriodReaders.cs:75-145` (`ReaderSequences`), `:233-289` (`DirectlyAffected`), `:299-322` (`ReadAcrossPeriods`), `:360-373` (`Matches`) — збіг лише за `ColumnDefId` ребра; виклики — `src/Ecr.Infrastructure/Jobs/CrossPeriodFanOut.cs:146-161`, `:255`; ціль прогону читача — `RecalculationService.cs:572-580`. Насіння як «змінені колонки» — `RecalculationService.cs:519-540` (лише живі рядки: `tableOfRow` з `rowIdsByTable`).
- Правило: `docs/build/02b-expressions.md:344-358` (§4.2: предикат «над фактичними рядками екземпляра таблиці»); §3.2 `:196-237` (зсуви, `#k`, період без даних — `null`).
**Проблема:**
```csharp
foreach (var rowKey in rows.Keys.OrderBy(k => k, StringComparer.Ordinal))   // рядки ПОТОЧНОГО періоду
{
    CurrentRowKey = rowKey;
    var verdict = PredicateEvaluator.Evaluate(condition, this, …);            // умова — на даних поточного періоду
    if (…) matched.Add(Value(tableDefId, rowKey, columnDefId, periodOffset)); // значення — з іншого періоду за чужим ключем
}
```
Плюс дві дірки поширення: зміна колонки УМОВИ в періоді-джерелі не будить читачів (ребро — лише на колонку значення), видалення рядка в джерелі — теж (насіння видаленого рядка не потрапляє в «змінені колонки»).
**Сценарій відтворення:** місячний проєкт, динамічна таблиця Main; у січні рядки Kind='A' з Qty 3 і 4 (+ рядок Kind='B', Qty 10); формула лютого `S = SUM([Period:-1].[Main].[WHERE [Kind]='A'].[Qty])`. Фактично 0 (GUID-ключі лютого не мають значень у січні, `RowKey.cs:48`); для фіксованих рядків умова береться з лютневого Kind. Очікувано 7. Далі: у січні змінити Kind третього рядка на 'A' — очікувано S лютого = 17 (зараз читача не будять); видалити в січні рядок із Qty 4 — очікувано 13.
**Вплив:** будь-яка формула такого вигляду (у сідах і тестах не знайдено — `git grep "\[Period:…\]\.\[…\]\.\[WHERE"` порожньо) — латентна пастка для авторів шаблонів, тепер — підтримувана форма мови.
**Зміна — покроково:**
1. Розбір/публікація: змін не потрібно — `Parser.cs:1153-1212` уже будує `CellReferenceNode` з `PeriodOffset`/`AbsolutePeriod` і `RowSelector.Predicate`; `PredicateValidator.cs:100-108` і далі забороняє міжперіодне посилання ВСЕРЕДИНІ умови (умова однаково обчислюється на рядках зсунутого періоду — див. крок 3). Лише уточнити коментар класу `PredicateValidator.cs:6-20`, абзац «Крос-періодне посилання означало б читання чужого періоду під час фільтрації свого»: дописати «✎ C-11: зовнішній зсув `[Period:-n].[T].[WHERE …]` дозволено — тоді вся умова читає рядки зсунутого періоду (`SliceEvaluationContext.EvaluatePredicate`); зсув усередині умови лишається забороненим».
2. Рядки сусідніх періодів у контекст — `src/Ecr.Application/Recalculation/RecalculationService.cs`:
   - у `PeriodLoadExtras` (C-1b.4) — `public Dictionary<(int Offset, int TableDefId), IReadOnlyDictionary<string, long>> OtherPeriodRows { get; } = [];`;
   - у `LoadPeriodAsync`, одразу після обчислення `keys` (`:1983-1985`) усередині `foreach (var table in instances)`:
```csharp
            // ✎ C-11 (рішення PO 11.11): рядки ЗСУНУТОГО періоду — джерело рядків предиката `[Period:-n].[T].[WHERE …]`
            // (умова й значення — на рядках того періоду). Нуль нових звернень: ключі вже прочитано вище для значень.
            if (offset != 0 && extras is not null)
            {
                extras.OtherPeriodRows[(offset, table.TableDefId)] = keys;
            }
```
     (`table.TableDefId` — уже перекладений на версію прогону для сусіднього періоду іншої версії, `:1940-1953`; копія аркуша — свій вид, `:1955`);
   - `NewContext` (`:764-773`) — додати аргумент `otherPeriodRows: loadExtras.OtherPeriodRows`; у каскаді Rollup новий `LoadValuesAsync` (`:954-957`) пише в той самий `loadExtras` — словник доповнюється, контекст бачить той самий об'єкт.
3. Обчислення — `src/Ecr.Application/Recalculation/SliceEvaluationContext.cs`:
   - конструктор (`:113-125`) — новий останній параметр `IReadOnlyDictionary<(int PeriodOffset, int TableDefId), IReadOnlyDictionary<string, long>>? otherPeriodRows = null` і присвоєння `_otherPeriodRows = otherPeriodRows;` (поле `private readonly IReadOnlyDictionary<(int PeriodOffset, int TableDefId), IReadOnlyDictionary<string, long>>? _otherPeriodRows;`, поруч із `_rowsByTable`, `:51`) + `/// <param name="otherPeriodRows">✎ C-11: рядки сусідніх періодів (зсув, таблиця) → (ключ → id) — для предиката зі зсувом; null — немає.</param>`;
   - поле зсуву умови поруч із `CurrentColumnDefId` (`:320-321`): `private int _predicatePeriodOffset;`;
   - `Read`, `:331-335`:
```diff
-        var periodOffset = reference.AbsolutePeriod is { } absolute
-            ? absolute - Period.Sequence
-            : reference.PeriodOffset;
+        // ✎ C-11: усередині умови предиката зі зсувом посилання (лише `[Current]`, зсув 0 — PredicateValidator) читає
+        // рядок ЗСУНУТОГО періоду: до власного зсуву додається зсув предиката.
+        var periodOffset = (reference.AbsolutePeriod is { } absolute
+            ? absolute - Period.Sequence
+            : reference.PeriodOffset) + _predicatePeriodOffset;
```
   - `EvaluatePredicate`, `:416-446`:
```diff
-        if (!_rowsByTable.TryGetValue(tableDefId, out var rows))
+        // ✎ C-11 (рішення PO 11.11): рядки — того періоду, на який вказує посилання; періоду без екземпляра → порожня
+        // група (SUM = 0, AVERAGE = null — 02b §3.2, §6.1).
+        IReadOnlyDictionary<string, long>? rows = null;
+        if (periodOffset == 0 ? !_rowsByTable.TryGetValue(tableDefId, out rows)
+                              : _otherPeriodRows is null || !_otherPeriodRows.TryGetValue((periodOffset, tableDefId), out rows))
         {
             return [];
         }
 
         var matched = new List<ExpressionValue>();
         var savedTable = CurrentTableDefId;
         var savedRow = CurrentRowKey;
+        var savedOffset = _predicatePeriodOffset;
         try
         {
             CurrentTableDefId = tableDefId;
+            _predicatePeriodOffset = periodOffset;
 
             // ⚠ Порядок обходу — детермінований (Ordinal), а не порядок
             // словника: два прогони на тих самих даних мають дати той самий
             // список, інакше звірка з еталоном (ФВ-9.5) стає неможливою.
-            foreach (var rowKey in rows.Keys.OrderBy(k => k, StringComparer.Ordinal))
+            foreach (var rowKey in rows!.Keys.OrderBy(k => k, StringComparer.Ordinal))
             {
                 CurrentRowKey = rowKey;
                 var verdict = PredicateEvaluator.Evaluate(
                     condition, this, Domain.Enums.ExpressionDialect.Template);
                 if (verdict.Type == ExpressionValueType.Boolean && (bool)verdict.Value!)
                 {
                     matched.Add(Value(tableDefId, rowKey, columnDefId, periodOffset));
                 }
             }
         }
         finally
         {
             CurrentTableDefId = savedTable;
             CurrentRowKey = savedRow;
+            _predicatePeriodOffset = savedOffset;
         }
```
     Значення (`:438`) уже беруться зі зсуву — тепер за ключем рядка ТОГО періоду. Дефолт колонки умови/значення для зсунутого періоду дає C-5 (той самий `values`), стан помилок — C-1 (засів сусіднього періоду, `CellKey(offset, …)`).
4. Поширення на читачів — `src/Ecr.Application/Recalculation/CrossPeriodReaders.cs`, новий приватний помічник (поруч із `Matches`, `:360`):
```csharp
    /// <summary>
    /// ✎ C-11: чи читає міжперіодне ребро колонку. Предикатне посилання (`RowKey = null`, `FilterJson`) читає рядки
    /// іншого періоду за УМОВОЮ, колонок якої в графі немає (RecalculationReadScope.cs:28-37), — тож його будить будь-яка
    /// колонка його таблиці (зайвий перерахунок дешевший за пропущений, як у класі вище).
    /// </summary>
    private static bool ReadsColumn(FormulaDependency dependency, int columnDefId)
        => dependency.ColumnDefId is null
           || dependency.ColumnDefId == columnDefId
           || (dependency.RowKey is null && dependency.FilterJson is not null);
```
   і замінити умову колонки в трьох місцях: `Matches` `:365-366` (`&& (dependency.ColumnDefId is null || dependency.ColumnDefId == columnDefId)` → `&& ReadsColumn(dependency, columnDefId)`), `DirectlyAffected` `:280-281` (`&& (dependency.ColumnDefId is null || dependency.ColumnDefId == source.ColumnDefId)` → `&& ReadsColumn(dependency, source.ColumnDefId)`), `ReadAcrossPeriods` `:314-315` (те саме з `column.ColumnDefId`). Гілку Q-17 `OwnerInput` (`:249-258`, `DependsOnKind = 0`) не чіпати.
5. Видалення рядка в джерелі — `src/Ecr.Application/Recalculation/RecalculationService.cs:532-539`:
```diff
             foreach (var seed in dirty.Seeds)
             {
                 if (tableOfRow.TryGetValue(seed.TableRowId, out var seedTable))
                 {
                     changedColumns?.Add((seedTable, seed.ColumnDefId));
                     inputColumns?.Add((seedTable, seed.ColumnDefId));
                 }
+                else if (snapshot.ColumnsById.TryGetValue(seed.ColumnDefId, out var seedColumn))
+                {
+                    // ✎ C-11: насіння ВИДАЛЕНОГО рядка (DeleteRowHandler сіє всі колонки рядка, DeleteRowHandler.cs:504) —
+                    // рядка вже немає серед живих, але читачі інших періодів (предикат зі зсувом) мають перерахуватися.
+                    changedColumns?.Add((seedColumn.TableDefId, seed.ColumnDefId));
+                }
             }
```
   (`snapshot.ColumnsById` — `TemplateVersionSnapshot.cs:16`; `inputColumns` для видаленого рядка не додаємо — це правило Q-17 для ВВЕДЕНИХ значень власника.)
6. `docs/build/02b-expressions.md` §4.2 (`:353-358`) — після «обчислюється в рантаймі над фактичними рядками екземпляра таблиці» дописати: «✎ 2026-11-11 (C-11, рішення PO): зі зсувом періоду (`[Period:-1].[Main].[WHERE [Kind] = 'A'].[Qty]`, також `[Period:#k]`) — над рядками екземпляра ЗСУНУТОГО періоду: умова й значення беруться з тих самих рядків (динамічні рядки мають власні ключі в кожному періоді). Періоду без екземпляра — порожня група. Зміна будь-якої колонки таблиці чи видалення рядка в періоді-джерелі перераховує читачів. Зсув усередині умови — заборонено, як і раніше.»
**Тест-доказ:**
- `tests/Ecr.Application.Tests/Recalculation/CrossPeriodPredicateTests.cs` (новий; фікстура — як у наявних тестах `[Period:-1]` у `tests/Ecr.Application.Tests/Recalculation/` — знайти `git grep -ln "Period:-1" tests/Ecr.Application.Tests/Recalculation` і повторити побудову сховищ): (а) `Динамічні_рядки_попереднього_періоду_фільтруються_своєю_умовою` — січень: три рядки з GUID-ключами (A/3, A/4, B/10), лютий: інші GUID-ключі (B/100, A/1000); `S = SUM([Period:-1].[Main].[WHERE [Kind]='A'].[Qty])` у лютому → 7. Без кроків 2–3 — 0. (б) `Фіксовані_рядки_беруть_умову_з_того_самого_періоду` — фіксовані R1, R2: січень R1.Kind='A', R2.Kind='B'; лютий R1.Kind='B', R2.Kind='A'; Qty січня R1=3, R2=10 → 3 (без фіксу — 10: умова з лютого). (в) `Абсолютний_період_k` — `[Period:#1].[Main].[WHERE …]` у березні → те саме, що (а). (г) `Період_без_екземпляра_дає_порожню_групу` — у січні `[Period:-1]` → SUM = 0.
- `tests/Ecr.Application.Tests/Recalculation/CrossPeriodReadersTests.cs` (наявний, якщо є; інакше новий): зміна колонки Kind (не колонки значення) у січні → `ReaderSequences` містить лютий для предикатного ребра; для звичайного ребра `[Period:-1].[Main].[R1].[Qty]` зміна Kind читача НЕ дає (контроль точності). Без кроку 4 перший падає.
- `tests/Ecr.Application.Tests/Recalculation/CascadeRecalculationTests.cs`: `Видалений_рядок_потрапляє_в_змінені_колонки` — насіння з `TableRowId`, якого немає серед живих рядків, і колонкою таблиці Main → `changedColumns` містить `(Main, Qty)`. Без кроку 5 — порожньо.
- `tests/Ecr.Api.Tests/` (на справжньому хості, поруч із тестами WW-03 віяла — знайти `git grep -ln "CrossPeriodFanOut\|Period:-1" tests/Ecr.Api.Tests`): правка Kind у січні ставить і виконує перерахунок лютого; S лютого змінюється з 7 на 17.
**Перевірка після впровадження:** `dotnet test tests/Ecr.Expressions.Tests`; `dotnet test tests/Ecr.Application.Tests --filter "FullyQualifiedName~Recalculation|FullyQualifiedName~Publish"`; `dotnet test tests/Ecr.Api.Tests --filter "FullyQualifiedName~FormulaJobFixedCost"`; `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~CrossPeriod"`.
**Ризики і побічні ефекти:**
- Запити: нуль нових — ключі рядків сусіднього періоду вже читає `LoadPeriodAsync` (`:1975-1976`) для значень; періоди читаються лише ті, що названі у виразах цілей (`RecalculationReadScope.PeriodReferences`). `FormulaJobFixedCostTests` (без зсувів) не зачіпається.
- Віяло: предикатне ребро будить читача на будь-яку зміну таблиці-джерела — більше задач перерахунку для таких формул (лише їх; звичайні ребра — як було). Задачі coalesced на «документ × період» (WW-03).
- Пам'ять: словник рядків на (зсув, таблиця) — посилання на вже прочитані словники, без копій.
- Чинні опубліковані формули такого вигляду (не знайдено) після злиття почнуть давати інше (правильне) число — перед злиттям перевірити БД стенда: `SELECT f.Id, f.Expression FROM cfg.FormulaDef f WHERE f.Expression LIKE '%[[]Period:%[[]WHERE%'`.
- Excel-експорт формул: посилання на інший період у книзі одного періоду й так дає `MissingReference` (02b §3.2, «Excel-адаптер») — без змін.
**Обґрунтування:** найменша зміна, що робить семантику 02b §4.2 правдивою для зсуву: рядки й умова читаються з того самого періоду, звідки береться значення; механіка сусідніх періодів (WW-03) уже читає все потрібне. Поширення — консервативне (до таблиці), бо колонки умови в графі не зберігаються, а розширювати схему `cfg.FormulaDependency` і перепубліковувати версії — непропорційно.
**Рішення PO:** отримано 11.11 — «(б)».
**Впевненість:** підтверджено читанням.
**Залежності:** після C-1 і C-5 (спільні `SliceEvaluationContext.cs`, `RecalculationService.cs`, `PeriodLoadExtras`); `CrossPeriodReaders.cs` — після D-8 (лінія L6, `FormulaRecalculationJob.cs`/віяло), якщо D-8 його торкається. Лінія L8 (`PredicateValidator.cs`, лише коментар) — без конфлікту з P-4.

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
- **Група A (рушій і помилки формул — ОДНА лінія, строго послідовно):** C-1 (підкроки C-1a → C-1g, один PR) → C-5 → C-11. Спільні файли: `RecalculationService.cs` (усі три; клас `PeriodLoadExtras` вводить C-1, доповнюють C-5 і C-11), `SliceEvaluationContext.cs` (C-1, C-11), `RowLocalPreview.cs` (C-1, C-5), `SubmitSheetHandler.cs` і `ValidateDocumentHandler.cs` (C-1c, C-5). До C-1 — P-2 (звіт 03, той самий `RecalculationService.cs`, лінія L6); C-5 — після P-3 і P-5 (лінія L7: `TableValidation.cs`/`ValidationEngine.cs` та їхні викликачі `ValidateDocumentHandler.cs`, `SubmitSheetHandler.cs`).
  - ⚠ Для `00-PLAN.md` §3: C-11 більше не «лише `PredicateValidator.cs`» (L8) — після рішення PO він змінює `SliceEvaluationContext.cs`, `RecalculationService.cs`, `CrossPeriodReaders.cs` і йде в цьому ланцюжку після C-5; у `PredicateValidator.cs` — лише коментар (з P-4 не конфліктує). C-5 так само йде після C-1.
  - Інші файли C-1, спільні з іншими звітами, — послідовно з їхніми лініями: `GetTableSliceHandler.cs` (P-8, L20), `src/Ecr.Web/src/features/audit/api.ts` (W-3, L18 — нескінченна прокрутка журналів), `DocumentDeletionStore.cs` (лише якщо його змінює лінія звіту 02), `CrossPeriodReaders.cs` (C-11 — після D-8, якщо D-8 його торкається).
- **Група B (Excel):** C-4 → C-12 → C-3 послідовно (спільні `ImportDiffBuilder.cs`/`ExcelImporter.cs`).
- **Група C (міграція):** C-2 (`DocumentVersionMigrationStore.cs`). C-1 цей файл свідомо НЕ змінює (C-1a, крок 10).
- **Група D (workflow):** C-6 (`WorkflowStore.cs`), C-8 (`ReopenDocumentHandler.cs`) — паралельно; C-7 (`IAccessDecisionService.cs`, `AccessDecisionService.cs`, `RecallSheetHandler.cs`) — окремо (інтерфейс зачіпає підміни в тестах).
- **Група E (дозволи):** C-9 (`PermitHistoryHandlers.cs`), потім C-10 (`PermitTimeline.cs`, `PermitHistorySupport.cs`, `PermitHistoryContracts.cs`, `PermitHistoryHandlers.cs`, `PatchCellsHandler.PermitValidity.cs`, `SaveCellSplitsHandler.cs`, `PermitCoverageIssues.cs`, `05-rpt-views.sql`, клієнт `features/permits/history/*`); C-13 (`RegistryStore.cs`) — паралельно. C-10 — після перевірки на стенді, чи є квартальні/річні проєкти з історією дозволів (запит у C-10).

**Токен міграцій:** `FE01_FormulaError` (C-1) — по черзі з `RS2_…` (D-3), `OB01_…` (D-9), правкою `RV01` (D-5).
**Зміни контракту** (`openapi.snapshot.json`, `schema.d.ts`): C-1 (`TableSliceDto.calcErrors`, `CalculationIssuesDto.formulaErrors`, нові схеми) і C-10 (`SplitMonthDto.monthTo`) — по черзі між собою й з L16 (S-2), L17 (W-2), через `lane/integ-*`.
**Рішення PO:** отримано 11.11 для C-1, C-5, C-10, C-11 (`00-PLAN.md` §4); C-1 переходить у хвилю 1 (блокує подання). Нове рішення для реєстру — `D-475` (C-1g).

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
- **Видалення рядка не сповіщає читачів інших періодів** — реальна шкода лише в Q-17 (прапор `MonthColumnRouting` вимкнено) і для міжперіодних предикатів — ✎ 11.11: для них закриває C-11, крок 5 (насіння видаленого рядка → «змінені колонки»).
- **Значення затвердженого аркуша, перераховане в пам'яті, живить формули чернетки** — немає рішення про очікувану поведінку (ФВ-9.17); потребує питання до PO окремо, не включено як дефект.
- **FromMonth-режим міграції** — прапор `FromMonthEnabled` вимкнено (`appsettings.json`).
- **Повтор частково виконаної важкої міграції** — предикати ідемпотентні, водяний знак коректний.
- **Excel formula injection** — ClosedXML 0.104.2 пише рядок як текст (`cell.Value = text`), формул не створює.
- Вже виправлено 2026-10-31 (не перевідкривалося): реімпорт незмінених формульних комірок, міграція перевизначення без формули, ліміти видалення EF, правило очищення згортки D-462.

## Перевірено й у порядку

`StrictDecimalArithmetic` (ділення/переповнення → значення-помилки); `TemplateFunctions` (агрегати, ROUND з межами, SUMIF, IFERROR не ловить null); `Evaluator.ApplyBinary` (поширення помилки/null, виключення для `&`, `=`, ділення на null); `PeriodStateCalculator` (`Calculate`/`Effective`/`PlanTransitions`, лише вперед, вікно року D-204); `YearGraceWindow` (межі в поясі майданчика); `SubmissionPeriodSelector.Choose/MonthColumns`; `ClosedPeriodGuard` (з винятком документа); `RecalculationJob.RefusedPeriodsAsync/GuardCompletionAsync` (виняток документа); `PermitTimeline` (`Spans`, `InMonth`, `GapDays`, `Merge`); `SaveCellSplitsHandler` (одна транзакція, повна сума лише з усіх частин); `ImportDiffBuilder.ReadNumber/Read` (інваріантні число/дата); `ExcelExporter.WriteValue/WriteText`; `GenericCalculationModule.ToValue` (інваріантний розбір Legacy); `CollectedFold`/межі материалізації (опівніч майданчика, півінтервал); курсори черги погодження й аудиту (строгий `>`, однакова колація); пошук `DateTime.Now/UtcNow` в області — 0 збігів; культурозалежний розбір без `CultureInfo` — 0 збігів.

## Самоперевірка

- **C-1 (✎ переписано 11.11 на `main` 39aea41d9):** перечитано дослівно: `RecalculationService.cs:1370-1389` (діф «було» — `IsManualTarget`…`IsError continue`), `:986-990`, `:1020-1022`, `:1104-1111`, `:705-706`, `:764-775`, `:377-412`, `:16-65` (кінець первинного конструктора `IDocumentSheetCopyStore? sheetCopies = null) : ISubmitRecalculation`), `:1323-1327` (затверджений аркуш не пишеться), `:1959-1961`, `:2007-2008`; `SliceEvaluationContext.cs:546-559` (діф `Value`), `:320-321`; `RowLocalPreview.cs:114-122`, `:136-154`; `SubmitSheetHandler.cs:343`, `:458-460`, `:625-628`, `:635`, `:643-645`, `:662`; `ValidateDocumentHandler.cs:36`, `:64-67`, `:166-173`; `GetCalculationIssuesHandler.cs:31`, `:53`, `:63-67`, `:70-72`, `:89-112`; `CalculationDtos.cs:113-115`; `TableSliceDto.cs:58-86`, `:111-121`; `GetTableSliceHandler.cs:93`, `:190-200`, `:495-505`, `:516-571`; `RowStore.cs:85-88`, `:792-801`, `:1211`; `IRowStore.cs:353-354`; `CalculationsConfiguration.cs:582-600`; `EcrDbContext.cs:204`, `:285`; `Infrastructure/DependencyInjection.cs:184`; `DocumentDeletionStore.cs:264`; `CellChangeOrigins.cs:26-150`; `AuditWriter.cs:63`, `:170`, `:199`; `StaleResultsQuery.cs:88`, `:143`; `MethodologyStore.cs:619`; `RowStore.cs:432`, `:444`; `SheetChangeStore.cs:62-68`, `:174`; `AuditReader.cs:288-305`, `:442`; `CellSourceStore.cs:36-47`; `11-audit-tables.sql:53`, `:58`; `ISubmitRecalculation.cs:38-39`; `ApprovalSnapshotRefresh.cs:127`; `HiddenValidationIssues.cs:53-66`; `ValidationMessage.cs:31-42`; `ValidationMessageTemplates.cs:64`, `:102-105`, `:175-176`; клієнт — `DocumentGrid.tsx:3641`, `:3704-3708`, `:3754-3763`, `:3793-3804`, `:3810-3822`, `:3827`, `cell-states.css:336-347`, `:398-425`, `useUnmatchedRows.ts:10-17`, `:53-58`, `:67`, `DocumentPage.tsx:158-160`, `:509-514`, `:749-754`, `:907-917`, `features/audit/api.ts:25-34`, `CellChangesTable.tsx:71-91`, `EndpointCoverageTests.cs:592-596`. Імена, на які спирається зміна, існують (grep): `Entity<long>`, `CellAddress(PeriodKey,long,int)`, `CellKey(int,int,string,int)`, `CellChangeRecord(..., Reason)`, `IAuditWriter.WriteCellChangesAsync(IReadOnlyList<CellChangeRecord>, CancellationToken)`, `ScopeFilter.Of`, `ValidationMessage(... SourceTableDefId, SourceColumnCode, MessageKey, Params)`, `ValidationMessageTemplates.Render`, `ExpressionErrors.BadValue`, `TemplateVersionSnapshot.ColumnsById`/`RowsByKey`, `DocumentReadScope.CanReadTable/CanReadColumn`, `formatDateTime` (`features/grid/formulaOverride.ts:4`). Не перевірено grep'ом і позначено «перевір» у кроках: точні імена властивостей `TableRow` (`IsDeleted`, `Ordinal`), простір імен `LocalizedText`, наявність `InternalsVisibleTo` для `Ecr.Application.Tests`. Тести-докази падають без фіксу: каскадний — D записується зі старого C (рядок `:1446` після `continue`-гілки), «одна подія» — без перевірки `same` другий прогін пише знову, перегляд — `values.Remove` дає залежним `null`, подання — без C-1c.3 `blocking` порожній. Тести фіксованої кількості запитів: прапорець — `EXISTS` у тій самій команді, стан читається/пишеться лише з помилками (у храповиках їх немає) — стелі `FormulaJobFixedCostTests.cs:168`, `PatchCellsQueryCountTests.cs:83/271/447`, `SubmitSheetQueryCountTests.cs:81/83` не змінюються; нова команда без стелі — лише `GET …/calculation-issues` (тесту кількості для нього немає, `git grep calculation-issues tests`). Дублів немає: `KNOWN-ISSUES.md`, `OPEN-ITEMS.md`, `10-decisions.md` (пошук `#DIV`, «помилк формул», `FormulaError`, `CalcError`; `D-427` — про методології Legacy, не про формули шаблону). Відкинуто з попередньої редакції: «очищати комірку з помилкою» і «`ValueString='#DIV/0'`» (суперечать рішенню PO). Пройшла.
- **C-2:** `DocumentVersionMigrationStore.cs:1865-1869` і `:1760-1765`, етапи `:611`/`:748`, FK `RS1_SheetCopies.cs:202-210` — перечитано дослівно; `@sheetMap` оголошено в преамбулі (`:1475`). У коді міграції немає жодної згадки копій. «Застряглий маркер» позначено як потребує стенда. Пройшла.
- **C-3:** `ExcelImporter.cs:586-607`, `PatchCellsHandler.Workbook.cs:600`, `:652`, `:911` — збігаються; `EnterEditOfAsync`/`EnterEditNoWaitOfAsync` існують (`IAccessDecisionService.cs:499-517`), `TableInstanceRef.SheetCopyId` існує (`IRowStore.cs:354`), `RowWriteLock.EnterAsync(..., sheetCopyId)` використовується в `PatchCellsHandler.cs:3016`. `ExcelSheetCopiesTests` покриває лише експорт/перегляд (за звітом агента; не суперечить). Пройшла.
- **C-4:** `ImportDiffBuilder.cs:192`, `:831-832`, `:241-245` (replacesFormula), `ExcelExporter.cs:1222-1257`, `:1263-1275` — перечитано; `FormulaDef.Scope/ColumnDefId/AllowManualOverride/IsDeleted` існують (`FormulaDef.cs:23-51`), `FormulaOutputs.RowKeyOf(table, formula)` використовується експортером (`ExcelExporter.cs:1270`). У `tests/Ecr.Adapters.Tests` немає `FormulaScope.Cell`. Пройшла.
- **C-5 (✎ переписано 11.11):** перечитано: `SliceEvaluationContext.cs:546-559`; `RecalculationService.cs:1779-1782`, `:1829-1833`, `:1983-1985`, `:1996-2016`, `:2062-2101`, `:19`, `:23`, `:71`; `RowLocalPreview.cs:47-54`, `:98-106`; `ValidateDocumentHandler.cs:121-135`, `:161-163`; `SubmitSheetHandler.cs:576-596`, `:604-606`; `TableValidation.cs:22-61`, `:157-209`, `:222-260`; `ValidationEngine.cs:354-358`, `:394-401`; `MethodologyResolver.cs:24-29`, `:229-242`, `:292-293`; `CalculationInputBuilder.cs:7`, `:46-94`; `RelationRecalculator.cs:82-85`, `:133-164`; `RelationCheckRunner.cs:24-32`, `:64-80`, `:114-115`; `ColumnDef.cs:56`, `:80`, `:204-209`; `CellValueReader.cs:130-177`, `:153-156`, `:234`, `:302-306`, `:448`; `IRegistryStore.cs:398-399`; `IMetadataCache.cs:57-60`; `Application/DependencyInjection.cs:165-167`; `Calculations/DependencyInjection.cs:33-34`; `02b-expressions.md:486-495`; `D-283` (`10-decisions.md:736`) — узгоджено (дефолт не пишеться). Діф «було» у ValidateDocumentHandler збігається дослівно (`:123-128`). Компіляція за змістом: усі імена методів/типів знайдено grep'ом; `RegistryEntry.Code` — `EcrCode` (порівняння через `ToString()`, як `PermitCoverageIssues.cs:315`). Тести падають без фіксу: дефолт ніде не потрапляє у `values`/зріз. Відкинуто з попередньої редакції: ідея «параметр значення за замовчуванням у `ValidationEngine.cs:356`» — `CellContext` перевіряє значення, яке пише людина, дефолт там не застосовний. Пройшла.
- **C-6:** `WorkflowStore.cs:309` дослівно; `DocumentPeriodReopenRule.Apply` (`Ecr.Domain.Services`), `db.DocumentPeriodReopens`, поля `DocumentId/ClosedAt/OpenUntil/PeriodKeyValue` — існують (`DocumentPeriodReopen.cs`). Тест `DocumentPeriodReopenTests.cs:78-79` закріплює дозвіл подання (підтримує знахідку). Пройшла.
- **C-7:** `RecallSheetHandler.cs:108`, `:211`, `:232-238`; `EditRules.cs:707`, `:757-766`; `AccessProfile.cs:390-395`; `AccessDecisionService.cs:2765-2766` — перечитано. `EditRules.MeetsSubmit(AccessProfile, int, GrantLevel)` (`:373`), `BuildContextAsync(..., evaluateAccessWindow, ct, sheetCopyId)` (`:3098-3100`, підтверджено), `WithSheetCodeAsync`/`WithParticipationAsync` — існують (приватні в тому ж класі, де додається метод). `key`, `sheetCopyId` визначені в обох місцях Recall. Пройшла.
- **C-8:** `ReopenDocumentHandler.cs:109`, `:146`, `:150`; `ApproveSheetHandler.cs:113-129`; `AccessDecisionService.cs:2985-2994` — перечитано. Тексти/ключі відмови скопійовано з `:113-123`. Пройшла.
- **C-9:** `PermitHistoryHandlers.cs:174-179` (рядок зі сторожем), `:371-388` (шапка без) — перечитано; `IPermitHistoryStore.PartsAsync(long, IReadOnlyCollection<int>, CancellationToken)` фільтрує `PeriodKeyValue` (`PermitHistoryStore.cs:41-49`); `PermitSegment.ValidFrom` (`PermitSegment.cs:58`), `CellPermitPart.RegistryEntryId` (використовується в `SaveCellSplitsHandler`) — існують. Тестів на скасування шапки немає (`git grep "UndoHeaderPermit\|header/undo" tests` — порожньо). Пройшла.
- **C-10 (✎ переписано 11.11):** перечитано: `PermitTimeline.cs` повністю (`Spans`, `SharedMonths`, `InMonth`); `PermitHistorySupport.cs:151-155`, `:247-261`, `:264-280`, `:298-347`, `:370-386`; `PermitHistoryHandlers.cs:36-50`, `:135`, `:143-156`, `:199`, `:399-409`, `:424`, `:480-482`, `:491`, `:496`, `:520`, `:525-527`, `:539-543`; `PatchCellsHandler.PermitValidity.cs:148-150`, `:459-469`, `:480`, `:512`, `:518`, `:552`, `:561-566`; `SaveCellSplitsHandler.cs:18-37`, `:111-125`, `:146`, `:239-264`, `:276-279`; `PermitCoverageIssues.cs:109-111`, `:187-197`, `:229-231`, `:273-286`, `:310-345`, `:356-368`, `:393-402`, `:420-450`; `05-rpt-views.sql:1515-1520`, `:1550-1555`, `:1600-1611`; `PermitHistoryContracts.cs:46-75`; `PeriodCalendar.cs:181-197`; `IPeriodStore.cs:153-164`; `AuditReader.PermitLabels.cs:134`, `:157`; клієнт `splitModel.ts:72-90`, `:126`, `CellSplitDialog.tsx:64-65`, `HeaderSplitBanner.tsx:50-51`, `HeaderSplitDialog.tsx:69-70`, `shared/format/period.ts:23`. Діфи «було» (PatchCells `:459-466`, SaveCellSplits `:239-244`, ClearParts `:561-563`, вʼюха `:1550-1555`) збігаються дослівно. Рівносильність для місячних проєктів доведено побудовою (гілка `IsSingleMonth`, `Month(m).PartKey = YYYYMM = PeriodKey`, `IsShared(Month(m)) ⟺ SharedMonths ∋ m`) і перевіряється наявними тестами без зміни очікувань. Тест квартального сценарію падає без фіксу (4225 не виникає). Тестових реалізацій/прямих конструкторів змінених обробників у `tests` немає (`git grep "new GetPermitHistoryHandler\|new SaveCellSplitsHandler\|new AddRowPermitHandler"` — порожньо), тож нові необов'язкові параметри нічого не ламають. Відкинуто з попередньої редакції: «`SingleMonthOf` → не ділити квартал» (суперечить рішенню PO). Пройшла (наявність квартальних проєктів — стенд).
- **C-11 (✎ переписано 11.11):** перечитано: `SliceEvaluationContext.cs:51`, `:113-142`, `:320-378`, `:413-449`; `Parser.cs:1153-1212`; `ReferenceResolver.cs:94`; `PredicateValidator.cs:6-20`, `:44-64`, `:100-108`; `DependencyExtractor.cs:203-213`, `:241-243`; `RecalculationReadScope.cs:28-37`, `:91-121`; `RecalculationService.cs:519-540`, `:572-580`, `:764-773`, `:954-957`, `:1801-1835`, `:1940-1955`, `:1975-1985`; `RecalculationPlanBuilder.cs:76-109`, `:118-147`; `CrossPeriodReaders.cs:75-145`, `:233-289`, `:299-322`, `:360-373`; `CrossPeriodFanOut.cs:146-161`; `DeleteRowHandler.cs:504`; `FormulaDependency.cs:89-102` (`RowKey`, `FilterJson`); `TemplateVersionSnapshot.cs:16`. Діфи «було» (`Read` `:333-335`, `EvaluatePredicate` `:416-446`, насіння `:532-539`) збігаються дослівно. Нуль нових запитів — ключі рядків сусіднього періоду вже читаються (`:1975-1976`). Тести падають без фіксу: (а) 0 замість 7, (б) 10 замість 3, віяло — читача немає, видалений рядок — `changedColumns` порожній. Відкинуто з попередньої редакції: відмова при публікації (`expr.predicate.crossPeriod` на зовнішньому посиланні) — суперечить рішенню PO. Пройшла.
- **C-12:** `ExcelImporter.cs:1284-1285`, `:1321-1323`, `:1330-1340`, `ImportDiffBuilder.cs:899-902` — перечитано; перевантаження `LookupTargetEntries.ListAsync(registries, groups, registryId, ct)` без профілю використовує експортер. Новий ключ і сід — у кроках. Пройшла (стенд — для ролі з Deny).
- **C-13:** `RegistryStore.cs:343-373`, `:489-502`, `RegistryAdminHandlers.cs` (`references.Total > 0`) — перечитано; `db.PermitSegments`, `db.CellPermitParts` існують (`:492`, `:498`). Пройшла.
- **Вилучено після перевірки:** «шаблонна версія знімка подання» (низька шкода), «видалення рядка в Q-17» (прапор вимкнено), «значення затвердженого аркуша в каскаді» (немає правила) — перенесено до «Відкинуто». Дублів із `KNOWN-ISSUES.md`/`10-decisions.md`/виправленим 2026-10-31 не виявлено (пошук за ключовими словами кожної знахідки).
