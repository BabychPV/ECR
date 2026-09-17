# Борг локалізації: відмови, чия подробиця — готове українське речення

> Файл **авторський**, не згенерований. Його читає
> `MessageKeyRatchetTests.Кидків_без_messageKey_не_стає_більше` (`Q-341`).

⛔ Це **не перелік звільнень**, на відміну від `trace-exempt.md`. Жоден рядок
нижче не стверджує, що локалізувати тут не треба, — кожен каже рівно одне: «це
місце ще не пройдене, і станом на замір їх тут стільки». Перелік має **лише
коротшати**.

**Предмет.** Мови продукту — `en`/`ru`/`kz`; української серед них немає
взагалі. Заголовок відповіді (`Title`) резолвиться каталогом `sys_ecr.UiString`
за кодом помилки (`D-95`), а подробиця (`Detail`) — лише тоді, коли виняток
несе `Details["messageKey"]`
(`ExceptionHandlingMiddleware.ResolveGenericMessageAsync`, `Q-314`). Без ключа
клієнтові їде речення, яке розробник писав для СЕРВЕРНОГО боку, — і відповідь
виходить двомовною.

**Як пройти рядок.** Додати `Details["messageKey"] = "err.<код>.<що саме>"` і
решту підстановок ОКРЕМИМИ полями (сирі значення рядками — резолвер підставляє
лише `string`); завести текст у
`src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql`; **лишити** українське
речення як запасне — резолвер повертається до нього, коли ключа в каталозі
немає. Зразок — `src/Ecr.Application/Documents/PatchCellsHandler.cs` (перший
зріз, `Q-341`), і його в переліку вже немає.

⚠ Сторож звіряє число **в обидва боки**. Стало більше — червоне, і повідомлення
називає файл із рядком. Стало менше — теж червоне: число зменшує той, хто
локалізував, інакше перелік тихо розходиться з дійсністю і перестає бути
заміром (той самий прийом, що `ContractIntegrityTests.ReservedCodes`).

⚠ Рахуються кидки лише тих типів, які `ExceptionHandlingMiddleware.Map`
віддає як **4xx**: `BusinessRuleException`, `AccessDeniedException`,
`ConcurrencyConflictException`, `DomainException`,
`SourceAuthenticationException`. Подробиця 500-ки стала й беззмістовна
навмисно (`ФВ-6.11`), локалізувати там нічого.

⛔ `NotFoundException` у рахунок **не входить**, і це не поблажка: у самого
типу немає параметра `Details` (`src/Ecr.Application/Errors/EcrException.cs`),
тож покласти ключ у його кидок сьогодні нема куди. Змінити тип — окрема
робота; доти рядки про 404 були б вимогами, які неможливо виконати.

**Замір 2026-09-17:** 316 кидків у 95 файлах.

| Файл | Місць |
|---|---|
| `src/Ecr.Adapters.Excel/ExcelImporter.cs` | 7 |
| `src/Ecr.Adapters.PiAf/PiAfCatalogReader.cs` | 2 |
| `src/Ecr.Adapters.PiAf/PiSqlClientDataSource.cs` | 4 |
| `src/Ecr.Adapters.PiAf/PiWebApiDataSource.cs` | 4 |
| `src/Ecr.Adapters.PiAf/SourceUnitConverter.cs` | 2 |
| `src/Ecr.Application/Audit/GetCellChangesHandler.cs` | 4 |
| `src/Ecr.Application/Calculations/MethodologyAuthoringHandlers.cs` | 2 |
| `src/Ecr.Application/Calculations/MethodologyDraftHandlers.cs` | 2 |
| `src/Ecr.Application/Calculations/MethodologyPublishChecks.cs` | 2 |
| `src/Ecr.Application/Calculations/PublishMethodologyHandler.cs` | 8 |
| `src/Ecr.Application/Calculations/RunCalculationHandler.cs` | 5 |
| `src/Ecr.Application/Documents/CreateDocumentHandler.cs` | 4 |
| `src/Ecr.Application/Documents/DocumentQueryHandlers.cs` | 3 |
| `src/Ecr.Application/Documents/DownloadExportHandler.cs` | 1 |
| `src/Ecr.Application/Integration/IntegrationHandlers.cs` | 3 |
| `src/Ecr.Application/Localization/GetUiStringsHandler.cs` | 1 |
| `src/Ecr.Application/Localization/SetUiStringHandler.cs` | 2 |
| `src/Ecr.Application/Periods/BuildPeriodCalendarHandler.cs` | 1 |
| `src/Ecr.Application/Periods/GetPeriodCalendarHandler.cs` | 1 |
| `src/Ecr.Application/Periods/ReopenPeriodHandler.cs` | 3 |
| `src/Ecr.Application/Periods/SetCurrentPeriodHandler.cs` | 2 |
| `src/Ecr.Application/Projects/CloneProjectHandler.cs` | 2 |
| `src/Ecr.Application/Projects/ProjectQueryHandlers.cs` | 12 |
| `src/Ecr.Application/Registries/GetRegistryEntriesHandler.cs` | 1 |
| `src/Ecr.Application/Registries/RegistryAdminHandlers.cs` | 7 |
| `src/Ecr.Application/Registries/RegistryDefinitionHandlers.cs` | 11 |
| `src/Ecr.Application/Registries/SetEntryValidityHandler.cs` | 1 |
| `src/Ecr.Application/Registries/UpsertRegistryEntryHandler.cs` | 4 |
| `src/Ecr.Application/Reporting/ReportDefHandlers.cs` | 7 |
| `src/Ecr.Application/Reporting/ReportSnapshotHandlers.cs` | 1 |
| `src/Ecr.Application/Security/AccessDiagnostics.cs` | 1 |
| `src/Ecr.Application/Security/ChangePasswordHandler.cs` | 4 |
| `src/Ecr.Application/Security/EndSimulationHandler.cs` | 2 |
| `src/Ecr.Application/Security/GetCurrentUserHandler.cs` | 1 |
| `src/Ecr.Application/Security/LoginHandler.cs` | 1 |
| `src/Ecr.Application/Security/PasswordChangeGate.cs` | 1 |
| `src/Ecr.Application/Security/PermissionCheck.cs` | 2 |
| `src/Ecr.Application/Security/ResourceGrantHandlers.cs` | 3 |
| `src/Ecr.Application/Security/RoleAndUserHandlers.cs` | 19 |
| `src/Ecr.Application/Security/StartSimulationHandler.cs` | 4 |
| `src/Ecr.Application/Sources/EntityFieldMapHandlers.cs` | 6 |
| `src/Ecr.Application/Sources/MappingPreviewHandlers.cs` | 1 |
| `src/Ecr.Application/Templates/ColumnDefHandlers.cs` | 4 |
| `src/Ecr.Application/Templates/CreateTemplateVersionHandler.cs` | 2 |
| `src/Ecr.Application/Templates/FormulaDefHandlers.cs` | 3 |
| `src/Ecr.Application/Templates/PatchPresentationHandler.cs` | 4 |
| `src/Ecr.Application/Templates/PeriodAccessRuleHandlers.cs` | 8 |
| `src/Ecr.Application/Templates/PublishTemplateVersionHandler.cs` | 2 |
| `src/Ecr.Application/Templates/RowDefHandlers.cs` | 6 |
| `src/Ecr.Application/Templates/SheetDefHandlers.cs` | 3 |
| `src/Ecr.Application/Templates/TableDefHandlers.cs` | 3 |
| `src/Ecr.Application/Templates/TableRelationHandlers.cs` | 4 |
| `src/Ecr.Application/Templates/TemplateQueryHandlers.cs` | 4 |
| `src/Ecr.Application/Templates/ValidationRuleHandlers.cs` | 2 |
| `src/Ecr.Application/Units/ConvertUnitHandler.cs` | 3 |
| `src/Ecr.Application/Workflow/ApprovalRouteHandlers.cs` | 3 |
| `src/Ecr.Application/Workflow/ApproveSheetHandler.cs` | 2 |
| `src/Ecr.Application/Workflow/ReopenDocumentHandler.cs` | 4 |
| `src/Ecr.Application/Workflow/SubmitSheetHandler.cs` | 4 |
| `src/Ecr.Calculations/ConstantResolver.cs` | 3 |
| `src/Ecr.Calculations/MethodologyResolver.cs` | 1 |
| `src/Ecr.Domain/Entities/Calculations/CalculationRun.cs` | 1 |
| `src/Ecr.Domain/Entities/Calculations/Methodology.cs` | 3 |
| `src/Ecr.Domain/Entities/Calculations/MethodologyConstant.cs` | 6 |
| `src/Ecr.Domain/Entities/Calculations/MethodologyDependency.cs` | 1 |
| `src/Ecr.Domain/Entities/Calculations/MethodologyFormula.cs` | 3 |
| `src/Ecr.Domain/Entities/Calculations/MethodologyImport.cs` | 1 |
| `src/Ecr.Domain/Entities/Calculations/MethodologyRule.cs` | 1 |
| `src/Ecr.Domain/Entities/Calculations/MethodologyVersion.cs` | 7 |
| `src/Ecr.Domain/Entities/Configuration/CalculationBinding.cs` | 1 |
| `src/Ecr.Domain/Entities/Configuration/ColumnDef.cs` | 3 |
| `src/Ecr.Domain/Entities/Configuration/FormulaDef.cs` | 2 |
| `src/Ecr.Domain/Entities/Configuration/PeriodAccessRuleDef.cs` | 4 |
| `src/Ecr.Domain/Entities/Configuration/RegistryDef.cs` | 2 |
| `src/Ecr.Domain/Entities/Configuration/RegistryRuleDef.cs` | 3 |
| `src/Ecr.Domain/Entities/Configuration/SheetDef.cs` | 1 |
| `src/Ecr.Domain/Entities/Configuration/TableDef.cs` | 5 |
| `src/Ecr.Domain/Entities/Configuration/TableRelationDef.cs` | 5 |
| `src/Ecr.Domain/Entities/Configuration/TemplateVersion.cs` | 5 |
| `src/Ecr.Domain/Entities/Dictionaries/RegistryEntry.cs` | 1 |
| `src/Ecr.Domain/Entities/Dictionaries/RegistryEntryLink.cs` | 3 |
| `src/Ecr.Domain/Entities/Dictionaries/RegistryValue.cs` | 8 |
| `src/Ecr.Domain/Entities/Documents/Period.cs` | 3 |
| `src/Ecr.Domain/Entities/Documents/PeriodPolicy.cs` | 2 |
| `src/Ecr.Domain/Entities/Documents/Project.cs` | 3 |
| `src/Ecr.Domain/Entities/External/EntityFieldMap.cs` | 1 |
| `src/Ecr.Domain/Entities/Reporting/ReportDefinitions.cs` | 3 |
| `src/Ecr.Domain/Entities/Workflow/ApprovalState.cs` | 7 |
| `src/Ecr.Domain/Services/PeriodCalendar.cs` | 3 |
| `src/Ecr.Domain/Services/UnitConverter.cs` | 4 |
| `src/Ecr.Domain/ValueObjects/SiteTimeZone.cs` | 1 |
| `src/Ecr.Infrastructure/Jobs/QuartzJobScheduler.cs` | 1 |
| `src/Ecr.Infrastructure/Persistence/NormalizedCellStore.cs` | 1 |
| `src/Ecr.Infrastructure/Persistence/TemplateVersionStore.cs` | 1 |
| `src/Ecr.Infrastructure/Security/AccessDecisionService.cs` | 1 |
