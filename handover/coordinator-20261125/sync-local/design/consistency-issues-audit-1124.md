# Consistency issues page — audit 24.11 (read-only)

Source of truth: `origin/main` ecdd84621 (24.11 16:20). ⚠ The `H:\ECR` working tree is at d4a19e0d7 (19.10) and is stale: it has no "Last check" line and an older job. All file:line below are `git show origin/main:<path>`. Live = `EcrLandUi`, SELECT only.

## Verdict

**It works, but it checks the wrong things for what we care about now.** The check is not driven by `ext.ConsistencyRule` at all. It runs 8 hard-coded checks every night and on demand. On live it ran successfully 37 times, the last at 24.11 16:12 UTC (manual, reason "oji"). All of them found 0 issues, and that is a true result for those 8 checks. But none of the 8 compares numbers, documents or methodology results. The live probes below found three real calculation defects that the page cannot show. The page text ("compares numbers that must agree across documents, tables and registries") claims more than the code does.

## 1. Mechanism

- **Rules are code, not data.** `ConsistencyCheckJob.CheckAsync` (`src/Ecr.Infrastructure/Jobs/ConsistencyCheckJob.cs:196-301`) runs these rule codes (constants `:42-63`):
  | Code | What | Sev |
  |---|---|---|
  | ORPHANED_CELL | `doc.CellValue.ValueRegistryEntryId` → missing registry entry (per partition) | 2 |
  | BROKEN_FK | `doc.TableRow` → missing `TableInstance` | 3 |
  | ARCHIVE_CHECKSUM (itg.ArchiveRun) | archive run checksums source≠target | 3 |
  | ARCHIVE_CHECKSUM (arc.Period, B70 `ArchiveTamperCheck`) | archived period changed after archiving | 3 |
  | UNBOUND_CALCULATED_COLUMN | Calculated column on a live template version without an active binding | 2 |
  | UNSOURCED_FORMULA_COLUMN | Formula column on a Published/Deprecated version without a formula or binding | 2 |
  | NON_CURRENT_RUN_ROWS | `calc.CalculationResult/Step/Input` rows of superseded runs left after the sweep (`:749-799`; only when `SupersededPurge:Enabled`) | 2 |
  | STALE_APPROVED_DEPENDENT | B80: approved consumer month whose HSE400 source changed after approval | 2 |
  It also re-scans `IsOrphaned` (`IOrphanScanner`). Results are written by MERGE plus a snapshot delete (D-480: a rule checked in full replaces its own findings).
- **`ext.ConsistencyRule` is a dead table.** Entity `src/Ecr.Domain/Entities/External/RawData.cs:92-121` has (Code, Expression, Severity, SourceEntityId, IsActive) and is meant for reconciling PI/source data (`docs/build/02a-db-schema.md:2577`). Its only reference is the `DbSet` (`EcrDbContext.cs:253`). There is no reader, handler, API, UI, seed or job use. 0 rows on live, and 0 rows is the expected state.
- **Schedule.** `RecurringScheduleService.cs:84` `NightlyCron = "0 15 2 * * ?"` (02:15 Kyiv = 00:15 UTC), registered at `:369`. On demand: `POST /api/v1/consistency/run` (`ConsistencyController.cs:107`; `RunConsistencyCheckHandler`). It needs `System.RunJob` and a reason (written to `aud.SecurityEvent`) and returns 409 while a check is in flight.
- **"Last check".** `src/Ecr.Web/src/features/consistency/lastCheck.ts:37-52` uses the newest `updatedAt` of `GET /api/v1/jobs?code=<ConsistencyCheckJob | IConsistencyCheckJob>&state=Succeeded&limit=1`, i.e. `itg.JobProgress`. The page shows it at `pages/admin/ConsistencyIssuesPage.tsx:257-259`. "Nov 24, 6:12 PM" = 16:12:56 UTC, the manual run.
- **Live evidence.**
  ```
  itg.MaintenanceRun JobCode='consistency-check': Succeeded 37 (12.10–24.11), Degraded 2, Failed 1
    41    Degraded 13.10  {"issues":253,...}
    348   Degraded 30.10  {"issues":7,...}          (false NON_CURRENT; fixed by D-480)
    20719 Failed   16.11  "Прогін не закрито: задачу скасовано або процес зупинився..." (process restart)
    20900 Succeeded 24.11 00:15  1 s   {"issues":0,"removed":0,"orphanRowsExamined":2472,...,"archiveCheckComplete":true}
    20917 Succeeded 24.11 16:12  12 s  {"issues":0,...,"orphanRowsExamined":6474,...}
  aud.SecurityEvent ConsistencyCheckRunRequested: 24.11 16:12 user 3 "oji"; 13.11 ×2; 12.11
  ext.ConsistencyRule = 0; aud.ConsistencyIssue = 0 (all / open)
  ```
  Duration on live is 1–16 s (`doc.CellValue` ≈ 531k rows).

## 2. Built-in checks regardless of rules

Yes: all 8 checks above always run. "0 / 0 / 0" is not "the page always shows 0". It means the 8 structural invariants hold on live. They covered nothing in the calculation-results area (see §4).

## 3. Requirements

- **ФВ-7.7** (`docs/tz/02-requirements.md` ~600) asks only for "a nightly check of integrity invariants with a log of discrepancies; a document does not move between versions without an explicit migration". It does not list concrete invariants. Delivered: nightly job, log, page, run-now, snapshot semantics (D-480), localized texts (D-480), archive tamper detection (D-504/B70), B80 N4 (STALE_APPROVED_DEPENDENT), D-98 (IsOrphaned).
- **Not delivered:** `RegistryKeyOverlapCheck` and `RegistryKeyConsistencyCheck` (registry key hashes match their values). Both are listed as `[parallel]` in ФВ-7.7 at `docs/build/FEATURE-REGISTRY-TABLES.md:1545`, and nothing in `src` has either name.
- **Source-data reconciliation rules (`ext.ConsistencyRule`):** schema only, no feature.
- **Not found** in docs/tz, docs/source or docs/build: a requirement for cross-document totals, Land vs HSE400 totals, or results vs methodology parity. The page description key `consistency.description` (`seed/features/COLL-ui20consistency.sql:25`) promises this anyway.

## 4. Proposed calc/methodology rules (probed on live 24.11)

| # | Rule | Live result now | Catches | Cost | Sev |
|---|---|---|---|---|---|
| R1 | **RESULT_VERSION_NOT_EFFECTIVE**: a Current run's result whose `MethodologyVersionId` ≠ the published version effective for the period | **29 methodologies on run 10106** (doc 96, 202609, Current since 13.11): results come from **Formulas** versions, while Script is now effective | months not recalculated after the Script switch or a version change | distinct (run, period, version) over `calc.CalculationResult` on Current runs only; small, ~1 query per period | 2 (3 if the month is approved — question) |
| R2 | **CALC_ISSUE_ORPHAN**: `calc.CalculationIssue` whose run no longer exists or is not Current | **323 of 416 orphaned** (MissingInputs 309, StepError 14; 13.11–23.11); only 93 sit on Current runs. Doc 96, 202610: 205 issues over 5 runs | issue counts growing after recalculation; problems panel noise | anti-join on 416 rows, trivial | 2 |
| R3 | **FORMULAS_VERSION_EFFECTIVE**: the effective published version of an active methodology has Engine=Formulas (after the 24.11 "Script only, lossless" decision) | **1**: `ECW_C09_02_01` V1 (2024-01-01), no Script version. 76 older Formulas versions exist but are all superseded by Script | an unconverted methodology | `calc.MethodologyVersion` (124 rows) | 2 |
| R4 | **CURRENT_RUN_MISSING_RESULT**: a bound table row whose RequiredInputs are all filled has no result (and no issue) in the Current run | not probed: needs row × required-input join | silent gaps ("empty cell, no error") | moderate: per (document, period), bound tables only; on doc 96 9288 live bindings → cap by MaxIssues, one query per Current run | 3 |
| R5 | **CURRENT_RUN_NO_RESULTS_WITH_BINDINGS**: a Current run with 0 results while the project has live bindings | run 10143 (doc 93) has 0 results but also 0 bindings → legitimate, so the rule needs the bindings condition; currently 0 hits | an engine that silently does nothing | trivial | 3 |
| R6 | **RESULT_PARITY_SAMPLE**: recompute N sampled result rows through the engine and compare with the stored values | n/a (needs the engine, not SQL) | stored result ≠ recomputation (stale or nondeterministic) | CPU, not SQL: 20 rows/night ≈ seconds. Keep it outside the SQL job (separate job class) | 3 |
| R7 | **BINDING_DEAD_COLUMN**: an active binding to a deleted ColumnDef | 775 (45 methodologies), **0** on template versions used by `doc.Project`/`doc.ProjectTemplate` → noise only | dead config | trivial | 1 (info), or filter to live versions → 0 |
| R8 | **OUTPUT_UNBOUND**: an output of the latest published version with no active binding | 11 | an output that is computed and shown nowhere | trivial | 1 |

Notes:
- `origin/lane/integ-calc-issue-orphans` cb5b2ffcf (24.11 17:53, "FK_CIssue_Run, cleanup with the run") fixes R2 at the source. After it merges, R2 is a backstop only (an FK makes orphans impossible), so add it only if the FK is not merged.
- Cost on the year-layout document is driven by `calc.CalculationResult` (≈4.1k rows) and `doc.CellValue` (531k). R1, R2, R3, R5, R7 and R8 together cost far less than ORPHANED_CELL, which already runs.

## 5. UX of the page

- **The "Rule" filter is a free-text search on `ruleCode`** (`ConsistencyIssuesPage.tsx:246`, hint "Leave empty for every rule."), not a list. It works technically: the server filters by `ruleCode`. With 0 findings, though, any text gives "no match", and the page never tells you which rule codes exist, so the filter cannot be used meaningfully.
- **Empty state:** "No consistency issues recorded / … an empty journal means the last run found nothing" (`CATALOG-en-base.sql:2743-2744`) plus the 0/0/0 stat strip. It does not say *which checks ran* or how many items they examined. The wording "no rules defined" would be wrong: rules exist, they are code. The real gap is "Checked: 8 rules, N cells/rows/columns, 0 findings".
- **Description overclaims:** "compares numbers that must agree … across documents, tables and registries" — no rule compares numbers.
- **No way to add a rule from the UI**, and there is no API for one either. `ext.ConsistencyRule` is unused.

## 6. Minimal fix plan

- **L1 `lane/integ-consistency-ux`** (client + seed, ~200 lines, ≥40% tests):
  - "Checks performed" block: each rule code with a one-line description and its findings count in the last run. Derive it from a static client list plus `totals`, or add per-rule counts to `MaintenanceRun.DetailsJson`.
  - Turn the Rule filter into a select of the known codes.
  - Empty state: "Last check ran 8 checks; nothing found".
  - Correct `consistency.description` (en/ru/kz, with "Змінені тексти" rows).
- **L2 `lane/integ-consistency-calc-rules`** (server, ~350–450 lines incl. tests): R1, R3, R5 (+ R2 if the FK lane does not merge; R8 as info). Each rule goes into `Collect(...)` with an evaluated scope, gets `consistency.finding.*` en/ru/kz keys, and needs a mutation proof per rule. Expected first-night findings on live: R1 = 29 methodologies on doc 96/202609, R3 = 1.
- **L3 (later)** R4 and R6 as a separate job (needs the engine; one sample size); `RegistryKeyOverlapCheck`/`RegistryKeyConsistencyCheck` (ФВ-7.7 debt).
- **L4 (optional)** `ext.ConsistencyRule`: decide whether to drop it (migration token) or leave it.

## Questions to the human (one word, default applies until answered)

1. Should R1 (results not on the effective methodology version) be an **error** for approved months? Default: **warning**.
2. `ext.ConsistencyRule` (custom rules defined by an admin): **build** or **drop**? Default: **leave as is, no UI** (no requirement asks for it).
3. Nightly parity spot check (R6), recomputing ~20 rows? Default: **yes, after L2**.
4. Doc 96, 202609 still holds Formulas-version results from 13.11: **recalculate**? Default: **yes**, unless the month is approved (then reopen only by your word).
