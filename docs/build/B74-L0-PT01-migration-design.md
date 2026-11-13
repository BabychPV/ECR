# PT01: one EF migration for B74 P1 + Land-year L0 (design, one page)

Base origin/main e8a5c9ffc (last migration `20261112052154_FE01_FormulaError`). ADR: `docs/build/ADR-B74-PROJECT-TEMPLATES.md`.
Paths below are `src/Ecr.Infrastructure/Persistence/…` unless they start with `src/`. Nothing here is built yet.

## 1. Today
- `doc.Project.TemplateVersionId` int NOT NULL + `FK_Project_TV` (`src/Ecr.Domain/Entities/Documents/Project.cs:80`, `Configurations/DocumentConfiguration.cs:79-82`).
- Timeline `doc.ProjectTemplateVersion(ProjectId, FromPeriodKey, TemplateVersionId)`, PK (ProjectId, FromPeriodKey), FKs to Project and TemplateVersion, `IX_ProjectTemplateVersion_TemplateVersion` (`Migrations/20261101211255_RPTV01_ProjectVersionTimeline.cs:26-64`, `Configurations/ProjectTemplateVersionConfiguration.cs:16-29`). Seeded once (`RPTV01…cs:68-75`); afterwards written ONLY by `FinishSql` (`DocumentVersionMigrationStore.cs:2060-2082`). Empty timeline = project's current version.
- `doc.fn_VersionOf(@ProjectId, @PeriodKey)` (`RPTV01…cs:81-92`), mapped as `EcrDbContext.VersionOf` (`EcrDbContext.cs:102`, `:292-293`). Version migration already refuses another template (`MigrateDocumentVersionHandler.cs:1792`), so every timeline today has ONE TemplateId — the backfill below relies on it and asserts it.
- `doc.Document` has no template (`src/Ecr.Domain/Entities/Documents/Document.cs:24-42`); key `P{projectId}-V{versionId}-{nnnn}` (`DocumentStore.cs:1094-1095`), `UQ_Document(ProjectId, BusinessKey)` (`DocumentConfiguration.cs:178-179`).
- `cfg.TemplateVersion.TemplateId` and `.Layout` (`src/Ecr.Domain/Entities/Configuration/TemplateVersion.cs:34,43`; TL01). `PeriodKind` per project (`src/Ecr.Domain/Enums/Enums.cs:131-137`); a Yearly period key is `Year*100+1`, the same key as Monthly January (`src/Ecr.Domain/ValueObjects/PeriodKey.cs:82-84`).
- L0 (`.sync-local/reports/LAND-YEAR-plan-1112.md:95-103, :280`): `cfg.MonthInputAlias` + slice column `monthNumber`. **L0 does not touch the timeline.** It meets B74 at L3 (doc 96: 1.0.16 → 1.0.17), which writes timeline rows through `FinishSql`, so L3 must be written against the PT01 key.

## 2. PT01 `<ts>_PT01_ProjectTemplates` (one migration, one snapshot regen, one migration token)
| Object | Shape |
|---|---|
| `cfg.TemplateVersion` | + `AK_TemplateVersion_Id_Template UNIQUE (Id, TemplateId)`: the target of the composite FKs below (EF `HasAlternateKey`). |
| `doc.ProjectTemplate` (new) | `Id int IDENTITY PK_ProjectTemplate` (the `ResourceId` for a narrowed grant, `ResourceKind.ProjectTemplate = 5`, no migration: `Enums.cs:206-228`); `ProjectId int NOT NULL FK_ProjectTemplate_Project`; `TemplateId int NOT NULL FK_ProjectTemplate_Template`; `CurrentTemplateVersionId int NOT NULL`; `FK_ProjectTemplate_TV (CurrentTemplateVersionId, TemplateId) → cfg.TemplateVersion(Id, TemplateId)`; `UQ_ProjectTemplate (ProjectId, TemplateId)`; `IX_ProjectTemplate_CurrentVersion (CurrentTemplateVersionId)` for the «projects on version» counters. |
| `doc.ProjectTemplateVersion` | + `TemplateId int` (added NULL, backfilled, then NOT NULL); PK becomes **(ProjectId, TemplateId, FromPeriodKey)**; + `FK_PTV_Template`; + `FK_PTV_TV (TemplateVersionId, TemplateId) → cfg.TemplateVersion(Id, TemplateId)` (replaces the single-column FK; the index stays). FK to `doc.ProjectTemplate` waits for P6: EF-built test projects have no ProjectTemplate row. |
| `doc.Document` | + `TemplateId int NULL` + `FK_Document_Template`, `IX_Document_Project_Template (ProjectId, TemplateId)`. NULL means «the project's primary template» until P3 makes the constructor require it; NOT NULL in P6 (Q2). |
| `cfg.MonthInputAlias` (L0) | `Id int IDENTITY PK`; `TableDefId FK cfg.TableDef`; `Alias nvarchar(64)`; `MonthNumber tinyint CHECK (MonthNumber BETWEEN 1 AND 12)`; `ColumnDefId FK cfg.ColumnDef`; `UQ_MonthInputAlias (TableDefId, Alias, MonthNumber)`; `IX_MonthInputAlias_Column (ColumnDefId)`. No backfill: the builder fills it for 1.0.17. |
| functions | new `doc.fn_TemplateVersionOf(@ProjectId, @TemplateId, @PeriodKey)` = the RPTV01 body filtered by TemplateId, falling back to `ProjectTemplate.CurrentTemplateVersionId`, then to `Project.TemplateVersionId` when that is the same template. **`doc.fn_VersionOf(@ProjectId, @PeriodKey)` keeps its signature** (compat path for the ~15 `EcrDbContext.VersionOf`/SQL callers and ~40 project-version reads): body = `fn_TemplateVersionOf(@ProjectId, <TemplateId of Project.TemplateVersionId>, @PeriodKey)`. On any single-template project the result is identical. `EcrDbContext.TemplateVersionOf(p, t, k)` is mapped next to it. |

Backfill (each step guarded and wrapped in `EXEC(N'…')`, because the idempotent script compiles a batch before the column exists: same trap as `RPTV01…cs:79-80`):
```sql
IF EXISTS (SELECT 1 FROM doc.ProjectTemplateVersion t JOIN cfg.TemplateVersion v ON v.Id = t.TemplateVersionId
           JOIN doc.Project p ON p.Id = t.ProjectId JOIN cfg.TemplateVersion pv ON pv.Id = p.TemplateVersionId
           WHERE v.TemplateId <> pv.TemplateId) THROW 51074, N'PT01: timeline row of another template', 1;
INSERT doc.ProjectTemplate (ProjectId, TemplateId, CurrentTemplateVersionId)
SELECT p.Id, v.TemplateId, p.TemplateVersionId FROM doc.Project p JOIN cfg.TemplateVersion v ON v.Id = p.TemplateVersionId
WHERE NOT EXISTS (SELECT 1 FROM doc.ProjectTemplate x WHERE x.ProjectId = p.Id AND x.TemplateId = v.TemplateId);
UPDATE t SET TemplateId = v.TemplateId FROM doc.ProjectTemplateVersion t JOIN cfg.TemplateVersion v ON v.Id = t.TemplateVersionId WHERE t.TemplateId IS NULL;
UPDATE d SET TemplateId = v.TemplateId FROM doc.Document d JOIN doc.Project p ON p.Id = d.ProjectId
JOIN cfg.TemplateVersion v ON v.Id = p.TemplateVersionId WHERE d.TemplateId IS NULL;
```
P1 code in the same lane (dual write, nothing reads the new rows except the compat function): `FinishSql` writes the timeline with TemplateId and updates `ProjectTemplate.CurrentTemplateVersionId` in the same statement as `Project.TemplateVersionId` (`DocumentVersionMigrationStore.cs:2063-2099`); `CreateProjectHandler`/`CloneProjectHandler` attach the template; `CreateDocumentHandler` (`:193`) sets `TemplateId`; the `ProjectTemplateVersion` constructor gains `templateId` (3 test files). Rows created around the domain (EF fixtures) are repaired by an idempotent block at the end of `Sql/seed/99-epilogue.sql` (the same two INSERT/UPDATE statements). PT01 carries only the L0 `MonthInputAlias` entity/config (so the table is in the migration). The separate L0 code step carries the metadata snapshot, the clone in `TemplateVersionCloner`, `PublishChecks.CheckMonthInputAliases`, the slice column `monthNumber` and the alias PUT, all contract work.

## 3. Idempotency and upgrade (live shape: LANDUI2026 Monthly, Land 1.0.16 = version 18, doc 96; HSE401/HSE400 Yearly)
- `dotnet ef migrations script FE01 PT01 --idempotent` run twice with `sqlcmd -I -b`: the second run changes 0 (history guard plus `NOT EXISTS`/`IS NULL` guards).
- Before, on the rehearsal copy (E:, never live): save `SELECT p.Id, pe.PeriodKey, doc.fn_VersionOf(p.Id, pe.PeriodKey)` for every period into a table. After: the same query with the new function must give **0 differences**. Also 0 rows from `Document.TemplateId IS NULL`, and `COUNT(ProjectTemplate) = COUNT(Project)`. Expected for the three projects: one ProjectTemplate row each; Land's timeline keeps its rows with TemplateId = Land's template.
- Run the upgrade main→SHA ×2 plus smoke in parallel with the batch CI (CLAUDE.md 12.10 #2).
- The `UPDATE doc.Document` bumps `RowVersion`, so an open document session gets one 409 on its next header save. Deploy in the window with the app stopped.

## 4. Rollback
`Down`: THROW if any project has more than one ProjectTemplate row (data would be lost; only possible after P3), then reverse order: restore the RPTV01 body of `fn_VersionOf`, drop `fn_TemplateVersionOf`, PK back to (ProjectId, FromPeriodKey), drop TemplateId and the FKs, drop `Document.TemplateId`, `doc.ProjectTemplate`, the alternate key and `cfg.MonthInputAlias`. The previous build running on the PT01 schema reads fine (EF selects mapped columns only) and can insert Documents (nullable). It CANNOT run a version migration: its `FinishSql` INSERT has no TemplateId. So: no version migration in the rollback window; the repair block fixes drift on redeploy.

## 5. Contract
B74 P1/PT01: none (no DTO changes; the project «templates» DTO comes in P4). The L0 code step (separate from the PT01 migration commit) carries the contract: an optional `monthNumber` on the slice column and the bulk alias `PUT` (Q3), with `openapi.snapshot.json` + `schema.d.ts` in the same commit.

## 6. Read sites P2 moves, in order (guard `ProjectTemplateVersionB74GuardTests`: 22 types / 48 IL refs, 6 files / 18 SQL reads)
1. Resolvers: `ProjectVersionTimelineService.cs:58-71` (+ a template-aware overload on `IProjectVersionTimeline`); `DocumentStore.cs` 9 IL + SQL `:1287` (`GetTemplateVersionIdAsync :1307`, `…ForPeriodAsync :1330`, `Find…/Lock… :1265/:1279`, list/card `:136-144, :533-541`, `:85-98`, `:686`).
2. Write-path reads: `SheetEditGate.cs:297`, `RowStore.cs:87, :801, :911, :1213`, `MonthColumnRouter.cs:107-110, :139-148`, `ArchiveAwareCellReader.cs:311`, `GetDocumentTemplateHandler.cs:106`.
3. Security (differential Deny tests, no filter mutation): `AccessDecisionService.cs:939, :1132, :3036, :3068, :3157` (8 refs), `EffectiveAccessStore`, `ResourceNameResolver`.
4. Workflow: `ApprovalQueueStore` (3), `StepApproverStore.cs:38`, `ListMyApprovalQueueHandler.cs:135`, `ListMyReturnedSheetsHandler.cs:152`.
5. Periods/jobs: `PeriodStateJob.cs:327, :487`, `PeriodStateCatchUp.cs:105`, `ReopenPeriodHandler.cs:202`, `SetCurrentPeriodHandler.cs:114`, `DocumentPeriodReopenStore.cs:32`.
6. Calc/collection: `CalculationResultStore.cs:408-409`, `CollectionStore.cs:741`, `SourceEventMapStore`, `ConsistencyCheckJob`, `MetadataWarmup`.
7. Counters/admin: `TemplateVersionStore` (5), `ProjectStore.cs:74`, `TemplateVersionDiscardStore.cs:80`; seed `Sql/seed/99-epilogue.sql:250-386` (P2/P5).
P3: `CloneProjectHandler.cs:96, :146`, `MigrateDocumentVersionHandler.cs:799, :815, :1266`, `DocumentVersionMigrationStore.cs:90, :1233, :1243, :2064, :2088`. P6: `ProjectConfiguration` (`DocumentConfiguration.cs:81`).

## 7. Answers, risks, window checklist (coordinator, 13.11: design APPROVED)
- **Q1 (answered by the human 13.11).** HSE400 is monthly too, so a shared project has one period kind and PT01 has no PeriodKind. B75 (a per-template workflow flag) would add columns to `doc.ProjectTemplate`; PT01 hard-codes nothing against it. Earlier analysis, kept for the record: Land stays Monthly even under the year layout (owner months); HSE401/HSE400 are Yearly, and their key 202601 collides with Monthly January. PT01 does NOT add `ProjectTemplate.PeriodKind`. Its keys leave room for it without re-keying: the column goes on `doc.ProjectTemplate` (surrogate Id + UQ), and only `doc.Period`'s `UQ_Period` would widen (ADR, «PeriodKind extensibility»).
- **Q2 (answered: the default).** `Document.TemplateId` is NULL plus a repair block in `99-epilogue.sql`; NOT NULL in P6.
- **Q3 (answered: the default).** Bulk `PUT /template-versions/{id}/month-input-aliases`, draft only. It is a CONTRACT change: it goes in the separate L0 code step with the openapi/schema regen, not in the PT01 migration commit (PT01 creates only the table + entity/config).
- **R1 (rehearsal measurement).** `fn_VersionOf` becomes a nested scalar UDF calling `fn_TemplateVersionOf` (SQL 2016 floor: `SqlServer2016FloorTests`, no inlining). Measure doc 96 `/year/summary` against the 369–380 ms baseline. If it is >10 % slower, P2 first passes `d.TemplateId` directly (`EcrDbContext.TemplateVersionOf`).
- **Window checklist.** (a) `Down` THROWs when a project has more than one `ProjectTemplate` row. (b) No version migration in the rollback window: the previous build's `FinishSql` INSERT has no TemplateId. (c) App stopped during the `Document` backfill (RowVersion bump).
- **F13 evidence.** The parity table (`fn_VersionOf` per project×period, before vs after) with 0 differences, plus the idempotent script run twice (the second run changes 0).
- R2 #872 (`lane/integ-pe-batch-pe2`) rewrites `FinishSql` (+108 lines): PT01 rebases after it. The `migratingTo` marker stays per project until P3.
- R3 Combining L0 into PT01 couples their reverts. Down is per object, and L0 has no data until 1.0.17.
