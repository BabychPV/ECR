# ADR B74: a project (year + site) carries several templates — option A

Status: ACCEPTED (owner, 13.11 ~08:30Z, via the coordinator). P0 = this ADR + the architecture guard; no schema change yet.
Base: origin/main e8a5c9ffc. Plan: `.sync-local/reports/B74-project-multi-template-plan-1113.md`.
Migration design (PT01, folding in Land-year L0): `docs/build/B74-L0-PT01-migration-design.md`.

## Context

Today a project has exactly one template version: `doc.Project.TemplateVersionId` is required
(`src/Ecr.Domain/Entities/Documents/Project.cs:80`, FK `FK_Project_TV` in
`src/Ecr.Infrastructure/Persistence/Configurations/DocumentConfiguration.cs:79-82`). A document has no template of its own;
its version is the project's (`DocumentStore.GetTemplateVersionIdAsync`, `DocumentStore.cs:1307-1317`), a request for any
other version is refused (`err.ECR-DOC-0422.versionNotProject`, `CreateDocumentHandler.cs:264-290`). The version timeline
`doc.ProjectTemplateVersion(ProjectId, FromPeriodKey)` and `doc.fn_VersionOf(project, period)` (migration
`20261101211255_RPTV01_ProjectVersionTimeline.cs:26-93`) answer «which version for this period» per project.

The owner wants one project per «year · site» holding several templates (Land, HSE401, HSE400, …).

## Decision

**Option A.** A new association `doc.ProjectTemplate(ProjectId, TemplateId, CurrentTemplateVersionId)`, a template on the
document (`Document.TemplateId`), and the version timeline keyed by (project, template, from-period). The backfill is 1:1
from `Project.TemplateVersionId`, so on day one every project has exactly one template and nothing changes.

Rejected: **B** (version per document; ~14–18 lane-days, high risk, more than asked) and **C** (a group above projects,
UI only; does not meet the requirement).

## Owner answers (13.11 ~08:30Z)

1. Option A (re-explained in plain words; accepted).
2. The business key carries the template code: `LAND-P1-0001`, `HSE401-P1-0001`. `UQ_Document (ProjectId, BusinessKey)`
   (`DocumentConfiguration.cs:178-179`) stays as it is: the code makes keys unique across templates.
3. A project grant covers all templates of the project by default and can be narrowed to one template. Narrowing is a
   `ResourceKind` append (`ProjectTemplate = 5`, `ResourceId = doc.ProjectTemplate.Id`); new kinds need no migration
   (`src/Ecr.Domain/Enums/Enums.cs:206-228`).
4. Existing projects stay single-template. New «2026 · site» projects are created the new way. Moving Land into a shared
   project is a separate tool after Land-under-year.

## Phases

| Phase | Content | Exit criterion |
|---|---|---|
| P0 | This ADR; guard `ProjectTemplateVersionB74GuardTests` (no new reads of `Project.TemplateVersionId` in C# IL or raw SQL; ratchet) | guard green, proven red on a new read |
| P1 | Migration PT01 (+ Land-year L0): `doc.ProjectTemplate`, `Document.TemplateId`, timeline keyed by template, `fn_VersionOf` compatibility path; writers keep the new rows in sync with `Project.TemplateVersionId` | upgrade main→PT01 ×2 + idempotent script, 0 behaviour change |
| P2 | Read paths move to the document's version (document → template → timeline); each allowlisted site in the guard closes; security reads with differential Deny tests | guard lists only P3/P6 sites |
| P3 | Write paths: create project, attach template, new document by template (business key with template code), version migration per (project, template), clone | multi-template project works end to end via API |
| P4 | UI: project without a fixed template, «Templates» tab, wizard project → template | e2e on the stand |
| P5 | Consistency checks, reports (rpt views already derive the version from `TableDefId`), tools | rpt/consistency tests |
| P6 | New site-year projects; move tool for LANDUI2026 after Land-under-year; drop `Project.TemplateVersionId`, `FK_Project_TV`, the compatibility path | guard allowlist empty |

## Coordinator answers on the PT01 design (13.11)

- **Q2 `Document.TemplateId`**: NULL in PT01 (NULL = the project's primary template) plus an idempotent repair block in
  `Sql/seed/99-epilogue.sql`. It becomes NOT NULL in P6.
- **Q3 L0 alias write path**: a bulk `PUT /template-versions/{id}/month-input-aliases`, for draft versions only. This is a
  contract change, so it ships in the separate L0 code step with the `openapi.snapshot.json`/`schema.d.ts` regen, not in the
  PT01 migration commit. PT01 only creates the `cfg.MonthInputAlias` table.
- **Q1 PeriodKind per template**: OPEN (the human is being asked). PT01 does NOT add `ProjectTemplate.PeriodKind`.

### PeriodKind extensibility (so that Q1 needs no re-keying)

If the answer is «per template», it is additive:
- **Column**: `doc.ProjectTemplate.PeriodKind tinyint NULL` (NULL = `Project.PeriodKind`). `doc.ProjectTemplate` is keyed by a
  surrogate `Id` plus `UQ_ProjectTemplate (ProjectId, TemplateId)`, so the column changes no key.
- **Timeline**: `doc.ProjectTemplateVersion` is keyed `(ProjectId, TemplateId, FromPeriodKey)`. `FromPeriodKey` is then
  read in that template's period kind, and the key stays as it is.
- **Periods**: `doc.Period` is unique per `(ProjectId, PeriodKey)` (`UQ_Period`,
  `Configurations/DocumentConfiguration.cs:145-146`), and a Yearly key equals the Monthly January key
  (`src/Ecr.Domain/ValueObjects/PeriodKey.cs:82-84`). Per-template kinds therefore need a `PeriodKind tinyint NOT NULL
  DEFAULT <project kind>` on `doc.Period`, with `UQ_Period` widened to `(ProjectId, PeriodKind, PeriodKey)`. That re-keys
  `doc.Period`, not anything that PT01 adds.

## Consequences

- Until P2 closes, `doc.fn_VersionOf(project, period)` keeps its signature and answers for the project's primary template
  (the template of `Project.TemplateVersionId`); a project with a second template must not be created before P2 (P3 is gated).
- `PeriodKind` stays per project (`Enums.cs:131-137`): LANDUI2026 is Monthly, HSE401/HSE400 are Yearly. Land and HSE can
  share a project only after Land-under-year and an answer on period kind per template (open question in the design doc).
- Merge first: `lane/integ-pe-batch-pe2` (#872, version migration), an-b69 (archive), #862 (calc results).
