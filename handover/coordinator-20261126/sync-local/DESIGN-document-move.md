# DESIGN rev 2: document-move — consolidate live into LANDUI2026 (one-off rehearsed script + 2 product fixes)

Card `.sync-local\ready\integ-document-move.md` (R3, b14). Review of rev 1: `.sync-local\design\REVIEW-document-move.md`
(FAIL: H1–H6, M1–M7, L1–L3). Human decisions 25.11 (end of this file): **one-off rehearsed SCRIPT** in the window (no UI
move feature); closed HSE400 months change only via Reopen with reason; LANDUI2026 roles/route apply; HSE401 = input table
(old `AF_Air_HSE401_Import`, limits L+H) with **no submit/approval**, must **not** be blocked by closed LANDUI2026 months and
must **not** open empty in the current period. Base `origin/lane/integ-b13` d75032896; facts from `EcrLandB13Reh` (SELECT).

## R2-0. New finding (not in review) — N1 HIGH: HSE401 formulas change value after the move
Table 775 has 9 formulas `… / IF([H_BASIS] = NULL, [Period].Days, [H_BASIS]) …`. `PeriodContext.Days = End − Start + 1`
(`Ecr.Expressions/PeriodContext.cs:44-49`). Period 202601: HSE401_2026 = 2026-01-01…2026-12-31 → **365**; LANDUI2026 =
January → **31**. The first recalculation of doc 93 in LANDUI2026 would multiply these results by ~11.8.
Decision: HSE401 **v14** (same change as B75 "no workflow") replaces `[Period].Days` with the year length
`IF([Period].Year % 4 = 0, 366, 365)` (`%` is the dialect's MOD), so values are identical in both projects. v14 is migrated
in HSE401_2026 BEFORE the move (Days = 365 there → parity provable before anything moves). Prevention: P2 extends the D-474
publish check (`PublishChecks.cs:1260-1286`, "[Period].Days in an anchor-owned cell") to year-scoped PerPeriodInstance tables.

## R2-1. Findings → decision → where verified

| # | Decision | Verified by |
|---|---|---|
| **H1** Q-17 Purge skips Closed 202601 → 1 750 stale copies | Relocation of doc 98 is a **move**, not a copy: `Move-Document.sql` runs Q-17 Apply (`@WriteLocked = 1`, owners 202602–202611) and then deletes the 202601 cells of M02..M11 itself, only where the owner value is equal (else abort), `aud.CellChange` Origin `Migration` with the move CorrelationId. Belt and braces: owner-aware loader (H3). Human word: Q1 below | SQL AC T1-3: non-owner copies of doc 98 = **0**; owner cells per (row, month) = pre-image (175×11, value-equal) |
| **H2** UI F_ empty for Feb–Nov (legacy run 10181 is all-months at 202601) | After the move: document Reopen of doc 98 for 202601–202609 (`DocumentPeriodReopensController`, `Period.Reopen`, reason "b14 move: recalc HSE400 by month", human 25.11 Q1=yes), recalculate doc 98 for each of 202601–202611 (202612 has no data), close the reopens. The new 202601 run supersedes 10181 (D-560 deletes it → kept in the pre-image). No "all-months" run remains | AC W-4: F_ per (row, month) over current runs of doc 98 = the 3 575 legacy values (325×11), each attached to its owner instance; UI/API slice of Mar and Nov shows them |
| **H3** "first wins" is order-dependent | P1: `Hse400CompositionSnapshotLoader` picks per (year, table, rowKey, month) the value of the **owner instance** of the month (input cells and F_ results alike); another instance only if the owner has none; two different values at the same tier → no value + log warning (same as `MonthRowResultOverlay.cs:232-236`). Applies only when the document's project is Monthly and `IMonthColumnRouter.RoutesComputedVersion(version)`; yearly docs unchanged. PERF-11 fingerprint unchanged (already covers instances, row stamps, current runs) | P1 tests, failing first on the base: owner ≠ Jan copy for an input row and for an F_ row → owner wins; equal-tier conflict → empty; yearly doc → unchanged |
| **H4** HSE401 blocked by closed months / opens empty | (a) HSE401 v14 `WorkflowMode = None` (existing B75: no submit, approve, route) via `tools/hse401/Update-Hse401Template.ps1`. (b) **P2, one predicate:** a table is *year-scoped* when it is `MonthsInColumns` **or `PerPeriodInstance`** on a `YearWithMonthColumns` version (D-474: such a document is ONE copy per year). Change `MonthColumnRouter.AppliesToTable` (`MonthColumnRouter.cs:77-83`) and `AccessDecisionService.cs:2415-2417` (`isMonthsTable` → the router predicate). Effects through the existing ~30 `AppliesToTable` call sites: every cell of table 775 is owned by the year anchor (202601), writes/rows/recalc go there (no new instances in 202611), and edit access uses the annual aggregate `AnnualPeriodRule.AggregateState` (Open while any 2026 month is Open/Scheduled) instead of January's Closed. The client already opens year-layout documents at the anchor (`yearLayout.ts`, `DocumentPage.tsx:665-724`). Today only HSE401 v13/v14 has such a table (Land v24: MonthsInColumns 81 + Static 2; HSE400 v20: MonthsInColumns 6) | P2 tests failing first: monthly project, Jan Closed, Nov Open → edit at 202601 allowed; PATCH addressed to 202611 lands in 202601 (no instance created); whole year Closed → `PeriodClosed`; Land fixture unchanged. Window AC W-6 (SysAdmin login): doc 93 opens at "Year 2026" with 292 437 values, a cell edit succeeds and is reverted |
| **H5** Q-17 Preview empty before the re-key; transaction/SET/guard | No product embedding. `tools/document-move/Move-Document.sql` = one sqlcmd session: `BEGIN TRAN` → re-key → `:r` Q-17 body (Apply) → anchor-copy removal → checks → report → `COMMIT` (Apply) or `ROLLBACK` (Preview). So Preview computes the real post-move state with the target calendar. Q-17's own `BEGIN TRAN` nests (@@TRANCOUNT 2); its `ROLLBACK` aborts everything → the wrapper fails if `@@TRANCOUNT = 0` after the include. SET options live only in that session. Guard: `Move-Document.ps1` supplies `Q17-APPLY-v1` and `@AllowLive` only with `-AllowLive -CorrelationId -Reason`, refuses `ECR`, `FLERT`, `EcrLegacyProd` | Rehearsal ×2 on a restored copy: Preview report = Apply report = live Preview report (same numbers) |
| **H6** tools/hse400 re-creates HSE400_2026 → Land switches source | T2: `Build-Hse400Template.ps1` / `Compare-Hse400Year.ps1` take `-ProjectCode` (default `LANDUI2026`) and refuse to create a document when a non-GOLD HSE400 document with instances in that year exists. Same for `tools/hse401` (`Hse401Stand.Lib.ps1:397,406` project and period = year anchor) | T2 Pester tests (`tools/tests/Hse400-Model.Tests.ps1` +); post-window SQL AC: exactly one non-GOLD HSE400 doc with 2026 instances = 98 |
| **M1** rights also removed | Measured: PeriodAdministrator holders (users 3, 11) also hold SystemAdministrator and LandOwner, which have grants on project 1 → **0 users lose access**. Preview lists per-user effective project access before/after (ResourceGrant × RoleAssignment) | Preview section "rightsDelta": lose = [] |
| **M2** job race, missing kinds | Window: stop `EcrWorker` (deploy-ecr.ps1 pattern) and confirm `itg.JobProgress` Queued/Running = 0 for ALL documents. Script refuses otherwise (any DocumentId, `IRecalculationJob~project{1,4,7}-`, `migrate-project{…}`). Post-commit, same transaction: `SELECT COUNT(*) FROM calc.CalculationRun r JOIN doc.Document d ON d.Id = r.DocumentId WHERE r.ProjectId <> d.ProjectId` = 0, else ROLLBACK | Apply report line `projectMismatch = 0`; re-run after the first nightly |
| **M3** first nightly recalculates | Expected: relocation audit (Origin `Migration`) wakes `NightlyInputChangeQuery` for doc 98 and, through B80, Land 96/100 in 202610/202611; doc 93 none (P2: no instances outside the anchor). All parity checks (R2-4) run **before** the first nightly, with P1+P2 deployed; repeated after it | W-8 / W-10 |
| **M4** verification blind to Jan–Sep | Value-level, all 11 months: (1) loader INPUTS = owner cells (175×11) and current-run F_ (325×11) per (row, month) vs pre-image; (2) rpt `v_HSE400_*_m` per (row, month) before/after; (3) rehearsal only: Reopen Land 96 202601–202609 on the copy and recalc the full year before and after the move → Land results diff over all 12 months = 0 (proxy of the loader output for every month); (4) live: Land 96 recalc 202610/202611 checksum = baseline; stored 510 checksum; CalculationIssue 99; FormulaError 0 | Rehearsal report + W-7 |
| **M5** rpt rows ×11 | After relocation HSE400 has the same shape as Land (one instance per month, year values joined to every `_PeriodKey`). Consumers must take one `_PeriodKey` per year or use `_m` by month. Human Q3 | W-7 rpt diff on `_m` per (row, month) |
| **M6** rollback = full RESTORE only | `Export-PreImage.sql` copies, before Apply, into side DB `EcrMovePre_<CorrelationId>` (C:\EcrData): doc.Document rows 93/98, all calc.CalculationRun of 93/98 (+ CalculationResult/Step/Input/Issue of run 10181 and 10183), doc.TableInstance/TableRow/CellValue/CellStyle of 93/98 (all periods). `Reverse-Move.sql` (rehearsed) restores them and re-keys back to 4/7 — possible only while the source projects exist → **purge of HSE400_2026 and HSE401_2026 after the first nightly passed** (TEST_COORD_2026, HSE400_GOLD can go in the window). Stop criteria R2-5 | Rehearsal: Apply → Reverse → all baselines equal |
| **M7** option | Decided by the human: script (b+). No UI feature, no contract, no permission | — |
| L1 | 1 `doc.CellStyle` each on 93/98 not relocated: accepted (cosmetic) | — |
| L2 | Period policy changes to LANDUI2026's; covered by Q1 (Reopen path) | preview prints both policies |
| L3 | P1+P2 deploy in the same window, before the move; ≤ 1 stand window per day | W-0 |

## R2-2. Lanes

**P (product), one integ lane `lane/integ-b14-move` (R3, full CI), two commits:**
- P1 loader. Files: `src/Ecr.Infrastructure/Persistence/Hse400CompositionSnapshotLoader.cs`;
  new `tests/Ecr.Infrastructure.Tests/Persistence/Hse400CompositionOwnerTests.cs`. Data built with production stores
  (monthly project, HSE400-like year version, PatchCells into owner and anchor, runs via `CalculationResultStore`).
  AC-P1: three tests red on d75032896, green after; SQL call count of `ReadDocumentsAsync` unchanged (PERF-11 ratchet).
- P2 year-scoped table. Files: `src/Ecr.Infrastructure/Persistence/MonthColumnRouter.cs`,
  `src/Ecr.Application/Ports/IMonthColumnRouter.cs` (doc), `src/Ecr.Infrastructure/Security/AccessDecisionService.cs`,
  `src/Ecr.Application/Templates/PublishChecks.cs`; tests `tests/Ecr.Infrastructure.Tests/Persistence/MonthColumnRouterYearScopedTableTests.cs`,
  `tests/Ecr.Infrastructure.Tests/Security/AccessDecisionYearScopedTableTests.cs`,
  `tests/Ecr.Application.Tests/Documents/PatchCellsYearScopedTableTests.cs`, PublishChecks test. AC-P2: H4 tests red on
  base; Land and HSE400 fixtures unchanged; the implementer lists every `AppliesToTable(` call site (30) with "no change
  needed / changed" in the ready letter; mutation of the predicate turns the tests red.
- No migration, no contract, no seed.

**T (tools), lane `lane/integ-b14-move-tools` (tools only, fast CI + Pester):**
- T1 `tools/document-move/`: `Move-Document.ps1` (modes Preview | Apply | Reverse, `-AllowLive`, `-CorrelationId`,
  `-Reason`, `-Documents 98,93`, `-TargetProject LANDUI2026`), `Move-Document.sql`, `Export-PreImage.sql`,
  `Reverse-Move.sql`, `README.md`; `tests/Ecr.Infrastructure.Tests/Persistence/DocumentMoveSqlTests.cs` in the pattern of
  `Q17MonthColumnConsolidationSqlTests` (fixture built by production stores). Writes `aud.SecurityEvent`
  `DocumentMovedToProject` (DetailsJson: from, to, reason, counts, CorrelationId).
  Refusals: worker jobs; version mismatch (`fn_TemplateVersionOf` per period); `SingleDocumentPerProject`; key clash;
  missing target period; workflow history with different PeriodKind; archive (B69); `cfg.ColumnRenameProject`;
  project-level runs; Q-17 conflicts > 0; owner ≠ anchor copy.
  AC-T1: SQL tests (success yearly→monthly, each refusal, Reverse restores byte-equal); rehearsal ×2 reports identical.
- T2 `tools/hse400/Build-Hse400Template.ps1`, `tools/hse400/Compare-Hse400Year.ps1`, `tools/hse401/Hse401Stand.Lib.ps1`,
  `tools/hse401/Build-Hse401Template.ps1`, `tools/hse401/Update-Hse401Template.ps1` (v14: `workflow-mode None`,
  year-length formula; project `LANDUI2026` after the move), both READMEs, `tools/tests/Hse400-Model.Tests.ps1`.
  AC-T2: Pester; dry-run against the rehearsal copy refuses to create a second HSE400 2026 document.

## R2-3. Window (one stand window; P lane deployed first)

W-0 deploy P1+P2 (RC with green full CI). W-1 HSE401 v14: draft → publish → LANDUI2026 template 3 → v14 → migrate doc 93
in HSE401_2026 → recalc 202601 → diff of 292 437 cells and formula results vs baseline = 0. W-2 stop EcrWorker; jobs = 0;
full backup; baselines (R2-4); `Export-PreImage`. W-3 `Move-Document -Mode Preview` → report = rehearsal report.
W-4 `-Mode Apply -AllowLive`. W-5 start worker; document Reopen of doc 98 202601–202609; recalc doc 98 202601–202611;
quiet; close reopens. W-6 UI (SysAdmin): HSE400 Mar and Nov values + F_; HSE401 opens at Year 2026, edit + revert.
W-7 parity (R2-4). W-8 purge TEST_COORD_2026, HSE400_GOLD (UI). W-9 after the first nightly: repeat W-7 + `projectMismatch`.
W-10 purge HSE400_2026, HSE401_2026 (UI); deletion-preview "allowed" first.

## R2-4. Parity set (rehearsal and live, before/after)
doc 96: stored 510 results + checksum (PeriodKey, SourceRowKey, OutputCode, Value); Land 96 recalc 202610/11 checksum;
CalculationIssue 99, FormulaError 0; doc 98 owner cells 175×11 and F_ 325×11 value-equal; non-owner copies 0; doc 93
cells 292 437 + formula results checksum; rpt `_m` HSE400/HSE401 per (row, month); queue 0; `projectMismatch` 0;
one non-GOLD HSE400 doc in 2026. Rehearsal only: full-year Land 96 recalc diff = 0.

## R2-5. Stop criteria → action
Any Preview number ≠ rehearsal → stop before Apply. After Apply, any parity mismatch, `projectMismatch` > 0, or W-6 failure
→ `Reverse-Move` (no purge happened) → re-verify baselines. RESTORE only by a separate human word.

## R2-6. Questions for the human (one word; default applies)
1. HSE400 relocation **moves** the Feb–Nov values out of January (deletes the January copies only if equal): **yes**?
2. HSE401 stays editable while any 2026 month of LANDUI2026 is open and closes with the year: **yes**?
3. Any report summing `v_HSE400_*`/`v_HSE401_*` across `_PeriodKey`? default **no**.
4. Purge HSE400_2026/HSE401_2026 the day after the window (after the first nightly): **yes**?

---
⛔ SUPERSEDED — rev 1 below is kept for history; rev 2 above replaces it.
---

# DESIGN: document-move — move a document to another project of the same year (SysAdmin)

Card: `.sync-local\ready\integ-document-move.md` (R3, b14). Base read: `origin/lane/integ-b13` d75032896. Facts measured
read-only on the rehearsal copy `EcrLandB13Reh` (25.11). Goal (human 25.11): one live project LANDUI2026 with Land 96,
HSE400 98, HSE401 93, test Land 100; then purge HSE400_2026, HSE401_2026, TEST_COORD_2026, HSE400_GOLD through the UI.

## 0. Facts that drive the design (rehearsal copy)

| Project | Id | PeriodKind | Periods | Docs |
|---|---|---|---|---|
| LANDUI2026 | 1 | **Monthly** | 202601–202609 Closed, 202610 Grace, 202611 Open, 202612 Scheduled | 96, 100 |
| HSE401_2026 | 4 | **Yearly** | 202601 Open | 93 (tpl 3, v13) |
| HSE400_2026 | 7 | **Yearly** | 202601 Open | 98 (tpl 5, v20) |
| HSE400_GOLD | 6 | Yearly | 202601 Open | 97 (gold, deleted by purge) |
| TEST_COORD_2026 | 5 | Monthly | 12 | none |

- LANDUI2026 already has `doc.ProjectTemplate` rows for templates 3 (HSE401, v13) and 5 (HSE400, v20).
  `doc.fn_TemplateVersionOf` gives the same version in source and target for 93 (13) and 98 (20).
- HSE400 v20 and HSE401 v13 have `cfg.TemplateVersion.Layout = YearWithMonthColumns`; HSE400 tables are `MonthsInColumns`,
  HSE401 table 775 is `PerPeriodInstance`. Both templates are `SingleDocumentPerProject = 1`.
- **Doc 98 stores all months in ONE instance (202601)**: 175 non-empty cells in each month column M01..M11.
  Doc 96 (same layout, monthly project) stores month m in period 2026m (`MonthColumnRouter`, Q-17).
  Doc 93: 292 437 cells, all in 202601, non-month columns.
- Runs are document-scoped: 98 → one current run 202601 (3 575 results, `ProjectId = 7`); 93 → run 202601 (`ProjectId = 4`).
  Doc 96 = 510 current results (446 + 35 + 29 for 202609/10/11) — the AC-0 baseline.
- 93/98: no `wf.ApprovalState`, `wf.ApprovalEvent`, `calc.SubmissionSnapshot`, `doc.DocumentPeriodReopen`, `EditAssignment`,
  `RowLock`, `ext.SourceEventMap`, `arc.*`, queued jobs. `aud.CellChange`: 93 → 292 496 rows, 98 → 1 943 rows (all 202601).
- Binding `cfg.DocumentSourceBinding` HSE400_LAND_COMPOSITION: `ReadFromDocument = 1`, `SourceState = Any`.

## 1. What is keyed by ProjectId (and what the move does with it)

| Data | Key | Move action |
|---|---|---|
| `doc.Document.ProjectId` (`UQ_Document (ProjectId, BusinessKey)`) | project | UPDATE; refuse on key clash. Key text (`P7-V20-0001`) kept — see Q4 |
| `calc.CalculationRun.ProjectId` (`UX_CalculationRun_Current (ProjectId, PeriodKey, DocumentId)`) | project | **UPDATE all runs of the document** (every status). ⛔ `CurrentRunSelection.RunsOfDocumentAsync` joins `d.ProjectId = r.ProjectId`; without this the F_ rows of HSE400 disappear and Land changes |
| Project-level runs (`DocumentId = NULL`) of the source | project | refuse if the source has any (they may hold the document's results). 93/98: none |
| Periods `doc.Period (ProjectId, PeriodKey)`, `Project.PeriodKind` | project | not moved; target calendar applies (§3). Refuse if the target lacks a period row for any key with data or any owner key after relocation |
| Template binding `doc.ProjectTemplate`, timeline `doc.ProjectTemplateVersion` | project×template | refuse if the target has no binding, or `fn_TemplateVersionOf(target)` ≠ `fn_TemplateVersionOf(source)` for any period with instances (migrate-version first) |
| `SingleDocumentPerProject` | template | refuse if the target already has a document of this template |
| Approval `wf.ApprovalState/ApprovalEvent`, `calc.SubmissionSnapshot`, `wf.ValidationResult`, `doc.DocumentPeriodReopen` | document | move with the document unchanged; refuse if PeriodKind differs and any of them exists (a Yearly 202601 approval would silently mean "January") |
| Cells, rows, instances, results, issues, formula errors, header/index values, discussions, locks, edit assignments | document | unchanged (except relocation, §3) |
| Approval route `wf.ApprovalRoute` | project | target route applies (LANDUI2026: `PRJ_1`, all templates); listed in preview |
| Rights `sec.ResourceGrant` (`ResourceKind.Project`) | project | target grants apply. LANDUI2026 grants LandDataEntry(3), LandApprover(5), Viewer(1), LandRestricted(2), Approver(4), Auditor(5), LandOwner, SysAdmin; HSE projects have only SysAdmin, PeriodAdministrator, LandOwner. Preview lists roles that gain/lose access (Q3) |
| `calc.RecalculationApproval`, `itg.ArchiveRun`, `cfg.ColumnRenameProject`, `rpt.ViewProjectScope` | project | none exist for 4/7; refuse if source has `cfg.ColumnRenameProject` rows for this template (B127 names would change) |
| Archive B69 `arc.*`, `Project.Status/IsArchiving` | project/doc | refuse if either project is archived/archiving or the document has any `arc.TableInstance` |
| Jobs `itg.JobProgress` (`DocumentId`, `TargetKey = IRecalculationJob~project{id}-…`) | doc + project | 409 if any Queued/Running job has this `DocumentId` or a target prefix of source or target project |
| Audit `aud.*` | document (no ProjectId) | follows the document; immutable |
| rpt stable views | `_ProjectId/_ProjectCode/_PeriodKey` | rows show LANDUI2026; after relocation HSE400 rows spread over `_PeriodKey` 2026m like Land (consumer: Q5) |

## 2. B80: how Land finds HSE400, and what must change

- Resolution is **by year across ALL projects**, not by project: `DocumentSourceBindingStore.ChosenSourceDocumentAsync`
  and `Hse400CompositionSnapshotLoader.ReadDocumentsAsync` take the **largest DocumentId** with instances of the bound
  tables in that year, excluding project `HSE400_GOLD`. Moving 98 does not change the choice (98 stays the largest).
  **No binding change is needed.**
- Purge blocker: `ProjectPurgeStore.SourceBindingDependantsAsync` counts consumers with `d.ProjectId != projectId`;
  once 98 leaves HSE400_2026 that project has no documents and deletion-preview becomes "allowed".
- What can still change Land numbers (HIGH, must be proven on the copy):
  1. `CalculationRun.ProjectId` not re-keyed → F_ rows lost (§1).
  2. Inputs: the loader reads every instance of the chosen document, month from `ColumnDef.MonthNumber`, so it is
     storage-agnostic; but after relocation without purge the same (row, month) exists twice. `ToCompositionRows` keeps
     the **first** value — harmless only while copies are equal. Relocation must leave exactly one copy (§3).
  3. F_ from runs: the legacy 202601 run carries F_ for all months (attached to the 202601 instance). Later per-period runs
     of doc 98 in the monthly project (nightly 202610/202611) add a second F_ for those months → first-wins again.
     Closed months cannot be recalculated (D-468, Reopen only), so the legacy run stays. **Required:** either (a) the
     loader prefers, per (year, table, row, month), the result of the run whose period owns the month (code change in
     `Hse400CompositionSnapshotLoader`, + test), or (b) the window recalculates doc 98 for all 12 periods (needs a
     document Reopen of 202601–202609). Recommendation: (a) in this lane; it also protects any HSE400 doc created in a
     monthly project. Measure on the copy before deciding.
- `DocumentSourceGate.OwnedMonths` uses the **source project's** PeriodKind; with `SourceState = Any` it is not used. If the
  binding is later set to `ApprovedOnly`, approvals of 98 become monthly (Jan approval no longer covers the year) — intended.

## 3. Periods: rule for a moved document

**Rule: the target calendar wins; the document keeps its own states; the move never reopens or closes anything.**
- No `doc.Period` rows are created or changed. Missing target period for a key with data → refuse.
- Per-document states (ApprovalState, events, snapshots, DocumentPeriodReopen) move unchanged, but only when PeriodKind is
  equal; PeriodKind differs + any workflow history → refuse (93/98 have none).
- **Yearly → Monthly relocation (98):** month column M{m} must live in period 2026m (Q-17 owner rule), non-month input in
  the anchor (MIN period of the year = 202601). Without it, LANDUI2026 shows HSE400 Feb..Nov empty and new edits split
  storage. The move runs the Q-17 consolidation for this one document in the same transaction: Apply (owner copies,
  mirror rows, `aud.CellChange` with the move's CorrelationId) then Purge (drop non-owner copies equal to the owner).
  Conflicts must be 0 (preview shows them; >0 → refuse). Target months that are Closed are written as a system relocation
  (Q-17 `@WriteLocked = 1` semantics), shown in preview; afterwards edits there need the normal document Reopen with reason.
  Single source of the rules: execute the Q-17 script text (`tools/q17/Preview-MonthColumnConsolidation.sql`, embedded)
  with `@DocumentId`, not a second C# copy; its live guard (`@AllowLive`) is satisfied by the SysAdmin UI action + reason.
- HSE401 (93) has no `MonthsInColumns` table → no relocation; its data stays in 202601, which in a monthly project means
  "January / year anchor" (Q2). Jan is Closed in LANDUI2026 → doc 93 becomes read-only until a document Reopen.
- Why not "refuse": Jan–Sep are closed in the target, so refusing blocks the human's goal. Why not "reopen with reason":
  reopening is a human decision per month (07.11, one path); a move must not silently unlock a project's history.

## 4. Options

| Option | Cost | Risk | Verdict |
|---|---|---|---|
| (a) Product feature "Move document" (SysAdmin, preview, reason, audit, UI in Documents) | ~1.5 days: handler + store + preview ~700 lines with tests, UI dialog ~300, e2e; no EF migration (seed permission + error keys + contract regen) | R3; reuses purge patterns (locks, 409, preview) and Q-17 script | **Recommended** (human asked for a product way via UI) |
| (b) One-off live SQL (UPDATE ProjectId ×2 tables + Q-17 Apply/Purge) | ~3 h | no preview/rights/audit event; easy to miss `CalculationRun.ProjectId`; not repeatable for the 200-doc future | fallback only |
| (c) Re-create documents + copy data | high | new DocumentIds (links, aud history lost), F_ must be recalculated (Land parity risk), 292k cells copy | rejected |

## 5. Contract, rights, concurrency, audit

- `GET /api/v1/documents/{id}/move-preview?targetProjectId=N` →
  `{ documentId, documentKey, templateCode, source{id,code,periodKind}, target{id,code,periodKind}, sameYear, versionMatch[{periodKey,source,target}], periods[{periodKey,targetState,hasData}], relocation{needed, ownerCopies, rowsCreated, instancesCreated, purged, conflicts, lockedTargets[]}, runsRekeyed, workflowItems{states,events,snapshots,reopens}, rightsDelta{gain[],lose[]}, routeTarget, blockers[], warnings[] }` (`blocker` = catalog key, as `ProjectPurgePreviewDto`).
- `POST /api/v1/documents/{id}/move` `{ targetProjectId, documentKey (typed confirm), reason }` → 200 `{ moved, runsRekeyed, relocation }`.
- Errors: 409 `ECR-DOC-0409` keys `moveBlocked.<reason>` (archived, jobsRunning, versionMismatch, singleDocument, keyClash,
  periodMissing, workflowKindMismatch, conflicts, projectRuns, columnRenames); 422 `ECR-DOC-0422` (reason/confirm/same project/
  other year). Register per the 4-file error-code rule.
- Permission `Document.MoveToProject`, **global**, SysAdmin only (same reasoning as `Project.DeleteWithData`). Live check
  account: the SysAdmin used for the purge e2e (not Windows login — B74 403 lesson).
- One transaction: `ISheetEditGate` exclusive on the document's structure (same lock as delete/purge), `UPDLOCK` on both
  `doc.Project` rows and the document, facts re-read under lock, 547/fact drift → 409 "changed during move".
- Audit: `aud.SecurityEvent` `DocumentMovedToProject` (DetailsJson: from/to, reason, counts, relocation summary,
  CorrelationId); relocation `aud.CellChange` rows carry the same CorrelationId. Recompute nothing; enqueue nothing.
- Classes (new): `Ecr.Application/Documents/Move/MoveDocumentHandler.cs` (+ `MoveDocumentPreviewDto`),
  `Ecr.Application/Ports/IDocumentMoveStore.cs`, `Ecr.Infrastructure/Persistence/DocumentMoveStore.cs`,
  `DocumentsController` two actions, UI `features/documents/moveDocument.ts` + dialog in the document list (SysAdmin).
  Changed: `Hse400CompositionSnapshotLoader` (owner-run preference, §2.3), seed permission, `ErrorCodes.cs` ×4 files.
- Hot path: none (one-off admin action). Loader change: SQL count per load unchanged (same queries; ordering only).

## 6. Test data, verification, rollback

- Tests build data with production code (create project Yearly + Monthly via `POST /projects`, attach templates, create
  documents, `PatchCells`, run recalculation), then move. Cases: success same kind; Yearly→Monthly with relocation
  (cells per owner = before, 0 duplicates); each blocker; F_ duplicate preference (red without the loader change);
  `CalculationRun.ProjectId` re-key (red without it); B80 purge preview becomes allowed; rights (non-SysAdmin 403).
- Parity measures (before/after, copy then live): doc 96 current results count (510) **and** checksum over
  (PeriodKey, SourceRowKey, OutputCode, Value); `calc.FormulaError` and `calc.CalculationIssue` counts (96: 99);
  doc 98 non-empty cells per (RowKey, month) = 175×11 with identical values; doc 93 cells 292 437 identical checksum;
  Land 96 recalculation of 202610/202611 after the move gives the same checksum; queue length 0.
- Rollback: full backup before the window (RESTORE only by separate human word). Before purging sources, reverse move
  is possible only for same-kind moves; for 98 (relocated) the reverse needs month→anchor consolidation that does not
  exist → rollback = backup. Hence: move 98 and verify parity **before** any purge.

## 7. Live window steps

1. Backup; record parity baselines (§6). 2. Move-preview 98 → LANDUI2026 (expect: version 20=20, relocation 175×10
moved, conflicts 0, lockedTargets 202602–202609, rights delta listed). 3. Move 98. 4. Verify Land 96: 510 + checksum,
issues 99; HSE400 doc in UI for Nov shows the same values. 5. Preview + move 93. 6. Doc 100 already in LANDUI2026 — no
action. 7. Deletion-preview HSE400_2026 → allowed; purge HSE400_2026, HSE401_2026, TEST_COORD_2026, HSE400_GOLD (warning
accepted) through the UI. 8. Re-verify parity; update `tools/hse401` default project code (`HSE401_2026` hard-coded in
`Hse401Stand.Lib.ps1`, `Build-Hse401Template.ps1`, `Update-Hse401Template.ps1`).

## 8. Open questions for the human (one word each; default applies until answered)

1. **Closed months:** HSE400 values Feb–Sep land in closed LANDUI2026 months and become read-only until a document Reopen — OK? (default: **yes**)
2. **HSE401 data:** in a monthly project doc 93's data sits in January (year anchor) — keep it there? (default: **yes**)
3. **Rights:** Land roles of LANDUI2026 (data entry, approvers, viewers) will see/edit HSE400 and HSE401 docs — acceptable, narrow later per template? (default: **yes**)
4. **Document code:** keep old keys `P7-V20-0001`/`P4-V11-0001` (rename later via change-key)? (default: **keep**)
5. **Reports:** does any external report filter rpt views by project code HSE400_2026/HSE401_2026? (default: **no**)
6. **Approval route:** HSE400 submissions follow LANDUI2026 route PRJ_1? (default: **yes**)

## ✎ 25.11 відповіді людини (квіз координатора)
1. HSE400 98: місяці лют–вер у закритих місяцях LANDUI2026 — правка лише через Reopen з причиною: **ТАК**.
2. Ролі й маршрут погодження LANDUI2026 діють для HSE400/HSE401; звузити за шаблоном пізніше: **ТАК**.
3. Зовнішні звіти не фільтрують за кодом HSE400_2026/HSE401_2026: **НІ (не фільтрують)**.
4. HSE401: людина — «401 це просто таблиця в яку вносять дані по яких розраховується емісії там немає правил для надсилання або погодження».
   → Вимога до дизайну: HSE401 (93) у LANDUI2026 НЕ отримує подання/погодження/маршруту і НЕ має блокуватися закритим/погодженим місяцем LANDUI2026 (січень 2026 там закритий!). Перевірити, як шаблон без workflow поводиться із закритими періодами місячного проєкту; якщо блокується — AC і рішення в цій же лінії. Ключі документів — лишити (дефолт).

✎ 25.11: людина нагадала — це вже було в наданому довіднику (.sync-local\design\FLARES_TOX_HSE400_DATAFLOW.md): HSE401 у старій системі = таблиця вводу AF_Air_HSE401_Import (ліміти L+H), яку читають fn_Air_TOx_Emissions_tonn, Report_Air_Flarings_Intermittent_SG, Report_Air_Flarings_Thermaloxidizer_* (рядки 933/949/954). Без подання/погодження. Для факельних методологій HSE401 — джерело лімітів.


✎ 25.11 рішення людини (квіз): **разовий відрепетируваний скрипт** у вікні (Preview/Apply, live guard, CorrelationId, pre-image), не UI-кнопка. Огляд rev1: FAIL — design\REVIEW-document-move.md (H1–H6, M1–M7). Далі rev 2.


## ✎ 25.11 повторний огляд rev 2: PASS-WITH-FIXES (B1–B6 у REVIEW-document-move.md; B1–B3 — до вікна). Відповіді людини (квіз 2):
1. Переклад місяців doc 98 у свої періоди + видалення січневих копій (лише при рівності; pre-image): **ТАК**.
2. HSE401 — людина: «для неї діє діапазон з проекту рік + зміщення після цього редагування не доступне бо вже відбудиться архівування. це має бути параметр налаштування в шаблоні».
   → P2 змінюється: НЕ «відкрито, поки відкритий хоч один місяць», а **параметр шаблону** «вікно редагування = рік проєкту + зміщення» (як «Closes» проєкту; після — архівування, редагування недоступне). Налаштовується в шаблоні (адмін), HSE401 — увімкнено; Land/HSE400 — без змін. Потрібні: поле налаштування шаблону (міграція/контракт → R3 у lane P), UI налаштування, AccessDecisionService/SheetEditGate за цим параметром, тест «зміщення минуло → відмова», узгодження з архівом B69.
3. Видалення HSE400_2026 і HSE401_2026: **ОДРАЗУ У ВІКНІ** (не наступного дня). Наслідок: відкат після видалення — лише повне відновлення бекапу (RESTORE — окремим словом людини); тому B3-перевірки паритету мусять пройти у вікні ДО видалення (W-7 перед W-8), а Reverse-Move — лише до видалення.
