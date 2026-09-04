# 05 — Скелет проєкту

> **Найважливіший файл для Етапу 0.** Тут — повне дерево папок і посилання на
> частини з вмістом кожного файлу. Файли створюються **точно як написано**:
> нічого не змінювати, не «покращувати», не додавати від себе
> (`07-checkpoints.md`, Етап 0, крок 2).

## Частини

| Частина | Що містить | Файлів |
|---|---|---:|
| [`05a-skeleton-solution.md`](05a-skeleton-solution.md) | `.sln`, `Directory.*.props`, усі `.csproj`, `.editorconfig`, `.gitignore`, `appsettings` | ~20 |
| [`05b-skeleton-domain.md`](05b-skeleton-domain.md) | `Ecr.Domain`: сутності, значеннєві типи, enum'и, інваріанти | ~35 |
| [`05c-skeleton-application.md`](05c-skeleton-application.md) | `Ecr.Application`: порти, use-cases, DTO, безпека | ~40 |
| [`05d-skeleton-expressions.md`](05d-skeleton-expressions.md) | `Ecr.Expressions`: лексер, парсер, граф, обчислювач | ~25 |
| [`05e-skeleton-infrastructure.md`](05e-skeleton-infrastructure.md) | `Ecr.Infrastructure`: EF Core, репозиторії, кеш, задачі, SQL | ~45 |
| [`05f-skeleton-calculations.md`](05f-skeleton-calculations.md) | `Ecr.Calculations`: рушій розрахунків, generic-модуль | ~15 |
| [`05g-skeleton-adapters.md`](05g-skeleton-adapters.md) | `Ecr.Adapters.Excel`, `Ecr.Adapters.PiAf` | ~18 |
| [`05h-skeleton-api.md`](05h-skeleton-api.md) | `Ecr.Api`: контролери, DI, автентифікація, health | ~30 |
| [`05i-skeleton-web.md`](05i-skeleton-web.md) | `Ecr.Web`: React SPA | ~45 |
| [`05j-skeleton-tools.md`](05j-skeleton-tools.md) | `Ecr.Bootstrap.Excel`, `Ecr.DataGen`, `Ecr.Migration.PiAf` | ~12 |

Тести — окремо, у [`06-tests.md`](06-tests.md) і його частинах.

---

## 1. Дерево проєкту

```
ecr-web/
├── Ecr.sln
├── Directory.Build.props
├── Directory.Packages.props
├── .editorconfig
├── .gitignore
├── global.json
├── README.md
│
├── docs/                                  ← цей пакет (уже існує)
│
├── src/
│   ├── Ecr.Domain/
│   │   ├── Ecr.Domain.csproj
│   │   ├── Abstractions/
│   │   │   ├── IClock.cs
│   │   │   ├── Entity.cs
│   │   │   └── DomainException.cs
│   │   ├── Enums/
│   │   │   └── Enums.cs
│   │   ├── ValueObjects/
│   │   │   ├── PeriodKey.cs
│   │   │   ├── CellAddress.cs
│   │   │   ├── CellValueData.cs
│   │   │   ├── LocalizedText.cs
│   │   │   ├── EcrCode.cs
│   │   │   └── RowKey.cs
│   │   ├── Entities/
│   │   │   ├── Configuration/
│   │   │   │   ├── Template.cs
│   │   │   │   ├── TemplateVersion.cs
│   │   │   │   ├── SheetDef.cs
│   │   │   │   ├── TableDef.cs
│   │   │   │   ├── ColumnDef.cs
│   │   │   │   ├── RowDef.cs
│   │   │   │   ├── StyleDef.cs
│   │   │   │   ├── FormulaDef.cs
│   │   │   │   ├── FormulaDependency.cs
│   │   │   │   ├── ValidationRule.cs
│   │   │   │   ├── TableRelationDef.cs
│   │   │   │   ├── PeriodAccessRuleDef.cs
│   │   │   │   ├── SheetGroupRule.cs
│   │   │   │   ├── RegistryDef.cs
│   │   │   │   ├── RegistryFieldDef.cs
│   │   │   │   ├── CalculationBinding.cs
│   │   │   │   └── TemplateVersionSnapshot.cs
│   │   │   ├── Documents/
│   │   │   │   ├── Project.cs
│   │   │   │   ├── PeriodPolicy.cs
│   │   │   │   ├── Period.cs
│   │   │   │   ├── Document.cs
│   │   │   │   ├── DocumentSheet.cs
│   │   │   │   ├── TableInstance.cs
│   │   │   │   ├── TableRow.cs
│   │   │   │   └── CellValue.cs
│   │   │   ├── Dictionaries/
│   │   │   │   ├── RegistryEntry.cs
│   │   │   │   ├── RegistryValue.cs
│   │   │   │   ├── RegistryEntryLink.cs
│   │   │   │   └── RegistryExternalKey.cs
│   │   │   ├── Units/
│   │   │   │   ├── Dimension.cs
│   │   │   │   ├── Unit.cs
│   │   │   │   └── UnitConversion.cs
│   │   │   ├── Calculations/
│   │   │   │   ├── Methodology.cs
│   │   │   │   ├── MethodologyVersion.cs
│   │   │   │   ├── MethodologyFormula.cs
│   │   │   │   ├── MethodologyConstant.cs
│   │   │   │   ├── MethodologySubstance.cs
│   │   │   │   ├── MethodologyOutput.cs
│   │   │   │   ├── MethodologyRule.cs
│   │   │   │   ├── ScriptVersion.cs
│   │   │   │   ├── CalculationRun.cs
│   │   │   │   ├── CalculationResult.cs
│   │   │   │   └── SubmissionSnapshot.cs
│   │   │   ├── Security/
│   │   │   │   ├── User.cs
│   │   │   │   ├── Role.cs
│   │   │   │   ├── Permission.cs
│   │   │   │   ├── RoleAssignment.cs
│   │   │   │   ├── ResourceGrant.cs
│   │   │   │   └── PasswordPolicy.cs
│   │   │   ├── Workflow/
│   │   │   │   ├── ApprovalRoute.cs
│   │   │   │   ├── ApprovalStep.cs
│   │   │   │   └── ApprovalState.cs
│   │   │   └── External/
│   │   │       ├── DataSource.cs
│   │   │       ├── SourceEntity.cs
│   │   │       ├── EntityFieldMap.cs
│   │   │       └── CollectionSchedule.cs
│   │   └── Services/
│   │       ├── PeriodStateCalculator.cs
│   │       ├── UnitConverter.cs
│   │       └── ChangeClassifier.cs
│   │
│   ├── Ecr.Application/
│   │   ├── Ecr.Application.csproj
│   │   ├── DependencyInjection.cs
│   │   ├── Ports/
│   │   │   ├── ICellStore.cs
│   │   │   ├── IMetadataCache.cs
│   │   │   ├── IFormulaEngine.cs
│   │   │   ├── ICalculationModule.cs
│   │   │   ├── IExternalDataSource.cs
│   │   │   ├── IBackgroundJobScheduler.cs
│   │   │   ├── ISqlCapabilities.cs
│   │   │   ├── IUnitOfWork.cs
│   │   │   ├── IRepository.cs
│   │   │   ├── IAuditWriter.cs
│   │   │   ├── IExcelExporter.cs
│   │   │   ├── IExcelImporter.cs
│   │   │   └── IReportSnapshotBuilder.cs
│   │   ├── Security/
│   │   │   ├── EditDecision.cs
│   │   │   ├── AccessProfile.cs
│   │   │   ├── IAccessDecisionService.cs
│   │   │   └── IPasswordHasher.cs
│   │   ├── Errors/
│   │   │   └── EcrException.cs
│   │   ├── Templates/
│   │   │   ├── Dto/TemplateDtos.cs
│   │   │   ├── CreateTemplateVersionHandler.cs
│   │   │   ├── CloneTemplateVersionHandler.cs
│   │   │   ├── PublishTemplateVersionHandler.cs
│   │   │   ├── DiffTemplateVersionsHandler.cs
│   │   │   ├── PatchPresentationHandler.cs
│   │   │   └── GetTemplateStructureHandler.cs
│   │   ├── Documents/
│   │   │   ├── Dto/PatchCellsRequest.cs
│   │   │   ├── Dto/PatchCellsResponse.cs
│   │   │   ├── Dto/CellConflictDto.cs
│   │   │   ├── Dto/TableSliceDto.cs
│   │   │   ├── CreateDocumentHandler.cs
│   │   │   ├── GetTableSliceHandler.cs
│   │   │   ├── PatchCellsHandler.cs
│   │   │   ├── CreateRowHandler.cs
│   │   │   └── ValidateDocumentHandler.cs
│   │   ├── Periods/
│   │   │   ├── Dto/PeriodDtos.cs
│   │   │   ├── BuildPeriodCalendarHandler.cs
│   │   │   ├── SetCurrentPeriodHandler.cs
│   │   │   └── ReopenPeriodHandler.cs
│   │   ├── Workflow/
│   │   │   ├── SubmitSheetHandler.cs
│   │   │   ├── ApproveSheetHandler.cs
│   │   │   └── ReopenDocumentHandler.cs
│   │   ├── Registries/
│   │   │   ├── Dto/RegistryDtos.cs
│   │   │   ├── GetRegistryEntriesHandler.cs
│   │   │   ├── UpsertRegistryEntryHandler.cs
│   │   │   └── RegistryResolver.cs
│   │   ├── Units/
│   │   │   ├── Dto/UnitDtos.cs
│   │   │   └── ConvertUnitHandler.cs
│   │   ├── Calculations/
│   │   │   ├── Dto/CalculationDtos.cs
│   │   │   ├── RunCalculationHandler.cs
│   │   │   ├── PublishMethodologyHandler.cs
│   │   │   └── SimulateMethodologyHandler.cs
│   │   ├── Validation/
│   │   │   ├── ValidationEngine.cs
│   │   │   └── ValidationMessage.cs
│   │   ├── Recalculation/
│   │   │   ├── DirtySet.cs
│   │   │   └── RecalculationService.cs
│   │   └── Common/
│   │       ├── PagedResult.cs
│   │       ├── CursorPagination.cs
│   │       └── ICurrentUser.cs
│   │
│   ├── Ecr.Expressions/
│   │   ├── Ecr.Expressions.csproj
│   │   ├── Ast/AstNode.cs
│   │   ├── Lexing/Token.cs
│   │   ├── Lexing/Lexer.cs
│   │   ├── Parsing/Parser.cs
│   │   ├── Parsing/ParseResult.cs
│   │   ├── Binding/ReferenceResolver.cs
│   │   ├── Binding/DependencyExtractor.cs
│   │   ├── Binding/RangeExpander.cs
│   │   ├── Binding/TypeChecker.cs
│   │   ├── Binding/UnitChecker.cs
│   │   ├── Graph/DependencyGraph.cs
│   │   ├── Graph/TopologicalSorter.cs
│   │   ├── Evaluation/Evaluator.cs
│   │   ├── Evaluation/IEvaluationContext.cs
│   │   ├── Evaluation/EvaluationResult.cs
│   │   ├── Evaluation/ExpressionValue.cs
│   │   ├── Functions/FunctionRegistry.cs
│   │   ├── Functions/TemplateFunctions.cs
│   │   ├── Functions/MethodologyFunctions.cs
│   │   ├── Functions/ConvertFunction.cs
│   │   └── PeriodContext.cs
│   │
│   ├── Ecr.Infrastructure/
│   │   ├── Ecr.Infrastructure.csproj
│   │   ├── Persistence/
│   │   │   ├── EcrDbContext.cs
│   │   │   ├── EcrDbContextFactory.cs
│   │   │   ├── Configurations/            (по одному файлу на агрегат)
│   │   │   ├── Migrations/                (генерується на ПК-2)
│   │   │   ├── Sql/
│   │   │   │   ├── 01-filegroups.sql
│   │   │   │   ├── 02-partitions.sql
│   │   │   │   ├── 03-archive-proc.sql
│   │   │   │   ├── 04-partition-maintenance.sql
│   │   │   │   ├── 05-rpt-views.sql
│   │   │   │   └── 06-rcsi.sql
│   │   │   ├── Repositories/
│   │   │   ├── NormalizedCellStore.cs
│   │   │   ├── BulkCellLoader.cs
│   │   │   ├── UnitOfWork.cs
│   │   │   └── SeedRunner.cs
│   │   ├── Caching/
│   │   │   ├── MetadataCache.cs
│   │   │   └── AccessProfileCache.cs
│   │   ├── Security/
│   │   │   ├── AccessDecisionService.cs
│   │   │   ├── PasswordHasher.cs
│   │   │   └── SecurityStampValidator.cs
│   │   ├── Jobs/
│   │   │   ├── QuartzJobScheduler.cs
│   │   │   ├── PeriodStateJob.cs
│   │   │   ├── RecalculationJob.cs
│   │   │   ├── CollectionJob.cs
│   │   │   ├── ArchiveJob.cs
│   │   │   ├── ConsistencyCheckJob.cs
│   │   │   ├── ReportSnapshotJob.cs
│   │   │   ├── PartitionCheckJob.cs
│   │   │   └── NotificationJob.cs
│   │   ├── Expressions/
│   │   │   └── FormulaEngine.cs         ← переїхав з Ecr.Expressions (Q-013 A)
│   │   ├── Reporting/
│   │   │   └── ReportSnapshotBuilder.cs
│   │   ├── Startup/
│   │   │   ├── SqlCapabilitiesProbe.cs
│   │   │   ├── SchemaValidator.cs
│   │   │   └── MetadataWarmup.cs
│   │   └── DependencyInjection.cs
│   │
│   ├── Ecr.Calculations/
│   │   ├── Ecr.Calculations.csproj
│   │   ├── GenericCalculationModule.cs
│   │   ├── CalculationOrchestrator.cs
│   │   ├── MethodologyResolver.cs
│   │   ├── ConstantResolver.cs
│   │   ├── CalendarContext.cs
│   │   ├── NumericPolicy.cs
│   │   ├── CalculationInputBuilder.cs
│   │   ├── CalculationOutputWriter.cs
│   │   ├── TraceRecorder.cs
│   │   └── DependencyInjection.cs
│   │
│   ├── Ecr.Adapters.Excel/
│   │   ├── Ecr.Adapters.Excel.csproj
│   │   ├── ExcelExporter.cs
│   │   ├── ExcelImporter.cs
│   │   ├── ImportDiffBuilder.cs
│   │   ├── StyleMapper.cs
│   │   ├── FormulaTranslator.cs
│   │   └── DependencyInjection.cs
│   │
│   ├── Ecr.Adapters.PiAf/
│   │   ├── Ecr.Adapters.PiAf.csproj
│   │   ├── PiWebApiDataSource.cs
│   │   ├── PiSqlClientDataSource.cs
│   │   ├── PiAfCatalogReader.cs
│   │   ├── CollectionRunner.cs
│   │   ├── CatchUpPlanner.cs
│   │   ├── SourceUnitConverter.cs
│   │   └── DependencyInjection.cs
│   │
│   ├── Ecr.Api/
│   │   ├── Ecr.Api.csproj
│   │   ├── Program.cs
│   │   ├── appsettings.json
│   │   ├── appsettings.Development.json
│   │   ├── Controllers/
│   │   │   ├── AuthController.cs
│   │   │   ├── TemplatesController.cs
│   │   │   ├── TemplateVersionsController.cs
│   │   │   ├── ProjectsController.cs
│   │   │   ├── PeriodsController.cs
│   │   │   ├── DocumentsController.cs
│   │   │   ├── CellsController.cs
│   │   │   ├── RegistriesController.cs
│   │   │   ├── UnitsController.cs
│   │   │   ├── MethodologiesController.cs
│   │   │   ├── SecurityController.cs
│   │   │   ├── AuditController.cs
│   │   │   ├── JobsController.cs
│   │   │   ├── SourcesController.cs
│   │   │   └── ReportsController.cs
│   │   ├── Errors/
│   │   │   ├── EcrProblemDetails.cs
│   │   │   ├── ExceptionHandlingMiddleware.cs
│   │   │   └── ErrorCodes.cs
│   │   ├── Auth/
│   │   │   ├── AuthenticationSetup.cs
│   │   │   ├── CurrentUser.cs
│   │   │   └── SecurityStampMiddleware.cs
│   │   ├── Startup/
│   │   │   └── StartupSequence.cs
│   │   ├── Middleware/
│   │   │   └── CorrelationIdMiddleware.cs
│   │   ├── Health/
│   │   │   ├── DatabaseHealthCheck.cs
│   │   │   ├── JobsHealthCheck.cs
│   │   │   └── SourcesHealthCheck.cs
│   │   └── Observability/
│   │       └── EcrMetrics.cs
│   │
│   └── Ecr.Web/                            (React SPA — 05i)
│       ├── package.json
│       ├── tsconfig.json
│       ├── vite.config.ts
│       ├── index.html
│       └── src/...
│
├── tools/
│   ├── Ecr.Bootstrap.Excel/
│   ├── Ecr.DataGen/
│   └── Ecr.Migration.PiAf/
│
└── tests/
    ├── Ecr.TestKit/                        (спільні фікстури і хелпери)
    ├── Ecr.Domain.Tests/
    ├── Ecr.Application.Tests/
    ├── Ecr.Expressions.Tests/
    ├── Ecr.Calculations.Tests/
    ├── Ecr.Infrastructure.Tests/
    ├── Ecr.Api.Tests/
    └── Ecr.Architecture.Tests/
```

---

## 2. Формат опису файлу

Кожен файл у частинах `05a`…`05j` описаний так:

````
### `шлях/до/файлу.cs`
MODULE: <назва модуля> | STAGE: <номер етапу>
CONTRACT: 02-contracts.md#<якір>
SCOPE: <що робить>
NOT IN SCOPE: <чого тут робити не можна>

```csharp
<повний вміст файлу>
```
````

**`STAGE`** визначає, коли файл **реалізується**. Створюються **всі** файли на
Етапі 0 — зі скелетом і `NotImplementedException`.

---

## 3. Правила вмісту

| Категорія | Правило |
|---|---|
| **Контракти** (інтерфейси, DTO, enum, значеннєві типи) | реалізовані **повністю й остаточно**, без `TODO` |
| **Реалізації** | повна сигнатура + XML-doc + тіло `throw new NotImplementedException("TODO: …")` |
| **Текст TODO** | описує **що зробити**, а не «реалізувати метод». Погано: `"TODO: implement"`. Добре: `"TODO: зчитати зріз через ICellStore, застосувати AccessProfile покомірково, спроєктувати в TableSliceDto; порожні комірки не включати (ФВ-3.8)"` |
| **Файли проєкту** | повністю, з точними версіями пакетів |
| **SQL, конфіги** | повністю, робочі |
| **`using`** | усі потрібні, явно; `ImplicitUsings` увімкнено, але типи з інших збірок імпортуються явно |
| **Namespace** | file-scoped, збігається зі шляхом: `src/Ecr.Domain/Entities/Documents/Project.cs` → `namespace Ecr.Domain.Entities.Documents;` |
| **XML-doc** | українською, на кожному публічному типі й члені |

---

## 4. Правила залежностей між проєктами

```
Ecr.Domain            → (нічого)
Ecr.Application       → Ecr.Domain, Ecr.Expressions      (tz/03 §3.3; Q-008)
Ecr.Expressions       → Ecr.Domain
Ecr.Calculations      → Ecr.Domain, Ecr.Application, Ecr.Expressions
Ecr.Infrastructure    → Ecr.Domain, Ecr.Application, Ecr.Expressions
Ecr.Adapters.Excel    → Ecr.Domain, Ecr.Application
Ecr.Adapters.PiAf     → Ecr.Domain, Ecr.Application
Ecr.Api               → усі вище
tools/*               → Ecr.Domain, Ecr.Application, Ecr.Infrastructure
```

⛔ **Заборонено назавжди** (перевіряється `Ecr.Architecture.Tests`):

* `Ecr.Domain` → будь-що;
* `Ecr.Application` → `Ecr.Infrastructure`, `Ecr.Adapters.*`, EF Core;
* `Ecr.Domain`/`Ecr.Application` → типи з іменами `Af*`, `Legacy*`, `Pi*`;
* будь-що → `Ecr.Api`.

---

## 5. Що робити, якщо не збирається

Порядок на Етапі 0 (крок 5):

1. **Відсутній `using`** → додати. Записати `BOOTSTRAP-FIX`.
2. **Версія пакета не резолвиться** → взяти найближчу стабільну в тій самій
   мажорній лінії. Записати.
3. **Неоднозначність типів** (напр. `Unit` із `Ecr.Domain.Entities.Units` і
   щось інше) → повна кваліфікація або `using X = …`. Записати.
4. **Помилка в самому скелеті** (друкарська, неузгоджена сигнатура) →
   виправити мінімально, **записати обов'язково і докладно**: це дефект
   пакета, і про нього має дізнатися людина.

⛔ **Заборонено на Етапі 0:** міняти імена типів, сигнатури, склад проєктів,
структуру папок, архітектурні межі. Якщо здається, що без цього не збереться —
це `questions.md` і зупинка, а не творчість.
