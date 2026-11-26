# REVIEW: DESIGN-document-move (R3, b14) — independent adversarial review

Reviewer: independent agent (no code to be written by reviewer). Date 25.11.
Inputs: `.sync-local\DESIGN-document-move.md`, card `.sync-local\ready\integ-document-move.md`, gate `gates\ref\01-vymoha-dyzain.md`.
Code: `origin/lane/integ-b13` @ d75032896 (read-only worktree). Data: `EcrLandB13Reh` (SELECT only).

## Verdict: **FAIL** (redesign §2.3, §3, §5 preview, §6, §7, then re-review)

The core idea is sound (re-key `doc.Document` + ALL `calc.CalculationRun` of the document; target calendar wins; Land's
source choice is year-scoped and project-agnostic). But four HIGH findings break the design as written: the Q-17 purge it
relies on does nothing in this case (H1), the HSE400 UI loses F_ for Feb–Nov (H2), the HSE401 rule contradicts the human's
25.11 answer (H4), and the preview/apply cannot run the Q-17 script as described (H5). Fixes are well defined below.

## Facts verified as stated (no change needed)

- Projects/periods/docs: LANDUI2026 Monthly, 202601–202609 Closed(3), 202610 Grace, 202611 Open, 202612 Scheduled; HSE401_2026/HSE400_2026/HSE400_GOLD Yearly, one period 202601; docs 93(p4), 96(p1), 97(p6), 98(p7), 100(p1).
- `doc.ProjectTemplate` rows (1,3→v13) and (1,5→v20) exist; `fn_TemplateVersionOf` = 13/20 in source and target.
- Doc 98: 6 instances, all 202601; 175 non-empty input cells in each of M01..M11 (16+34+38+15+34+38), no non-month cells, no F_ stored cells.
- Run 10181 (doc 98, ProjectId 7): 3 575 results, **all PeriodKey 202601, all with TableInstanceId = the Jan instance**, 325 per OutputCode M01..M11.
- Doc 96 current results 510 (202609: 446, 202610: 35, 202611: 29), CalculationIssue 99, FormulaError 0. No project-level (DocumentId NULL) runs anywhere.
- 93/98: no ApprovalState/Event, SubmissionSnapshot, DocumentPeriodReopen, EditAssignment, RowLock, SourceEventMap, arc.*; aud.CellChange 292 496 / 1 943.
- Every table with a ProjectId column is in §1 (`CalculationRun, RecalculationApproval, ColumnRenameProject, Document, Period, ProjectTemplate, ProjectTemplateVersion, ArchiveRun, ViewProjectScope, ApprovalRoute`); the last five are empty/irrelevant for 4/7. `calc.CalculationResult/Issue/Input/Step` carry no ProjectId (follow the run).
- Run re-key is mandatory and correct: besides `CurrentRunSelection.cs:30`, the rpt stable views (`ru.ProjectId = d.ProjectId`), `DocumentSourceIssues.cs:417`, `StaleResultsByIdsQuery.cs:202`, `RecalculationJob.Selective.cs:911`, `NightlyInputChangeQuery.cs:202` all join run→document by project.
- B80 resolution: `Hse400CompositionSnapshotLoader.cs:130-145` and `DocumentSourceBindingStore.cs:91-92,128-129` pick MAX(DocumentId) per year over all projects, excluding project code `HSE400_GOLD`. After the move 98 is still chosen. `ProjectPurgeStore.SourceBindingDependantsAsync` (l.281) returns [] once the project has no documents → deletion-preview "allowed".
- No triggers on doc/calc tables (only aud/arc/cfg immutability triggers); no temporal tables in doc/calc.

## Findings

### H1 — HIGH: Q-17 Purge will NOT remove the Jan copies; "exactly one copy" is unattainable as designed
Evidence: `tools/q17/Preview-MonthColumnConsolidation.sql:480-487` — Purge excludes any copy whose period is Closed in the
document's project: `AND NOT EXISTS (… doc.Period p WHERE p.ProjectId=@Prj AND p.PeriodKey=c.P AND p.State=3)`. All
non-owner copies of doc 98 live in 202601, which is Closed (State 3) in LANDUI2026. `@WriteLocked` affects only Apply
(l.318), not Purge. Result: 175×10 = 1 750 duplicate input cells stay in the Jan instance permanently (`purgeSkippedLocked`).
Consequence: `Hse400CompositionSnapshotLoader` reads cells of ALL chosen instances (l.253-295) and `ToCompositionRows`
keeps the first value per (year, month, stream, indicator) (`Hse400CompositionMapping.cs:160-163`). Today copies are equal;
after any later Reopen+edit of an owner month, the stale Jan copy can win → Land silently computes on old composition.
(The UI grid and rpt `fam` join read owner only, so nobody would see it.)
Required change: (1) make the loader's INPUT path owner-aware (for a Monthly project + year-layout version, month m is
read only from the period-m instance; fallback to another instance only if the owner has no value) — see H3;
(2) decide the Jan copies explicitly: either extend the purge to the anchor copies under the move's system-relocation
authority (this deletes cells in a closed month → human word), or keep them and rely on (1). Add AC: "non-owner copies
of doc 98 after move = N (documented), none differs from owner" and a test where owner ≠ copy (red without (1)).

### H2 — HIGH: HSE400 UI shows empty F_ for Feb–Nov after relocation (UI and Land disagree)
Evidence: `MonthRowResultOverlay.cs:151-226` — in a routed monthly document month m's F_ is read from the OWNER instance
in period m (`ReadCurrentMatchingAsync(documentId, ownerPeriod, [ownerInstance])`); `CalculationResultStore.cs:901-905`
filters results by `PeriodKey`. Run 10181's results are all `PeriodKey 202601`, `TableInstanceId = Jan instance` (verified).
No run exists for 202602..202611, and Closed months cannot be recalculated (`RecalculationWritePolicy.cs:68`, D-468).
So: F_ Feb–Sep blank until Reopen+recalc; Oct/Nov blank until a recalc. Meanwhile the Land loader still takes all F_ from
the legacy Jan run (reads period 202601, maps month by OutputCode, l.298-322). Design §7 step 4 ("HSE400 doc in UI for
Nov shows the same values") will fail right after the move.
Required change: choose and write down one of: (i) **recommended** — after the move, document-level Reopen of doc 98
for 202601–202609 (human already said 25.11: closed HSE400 months change via Reopen), recalc doc 98 for 202601–202611
(routed: each period computes its own month), close again. This supersedes the legacy "all-months" run, so the F_
duplicate problem (§2.3) disappears instead of being patched; (ii) accept blank F_ in the HSE400 UI for closed months
(needs a human word). Add AC: "doc 98 F_ per (row, month) visible in UI/API = legacy values" with SQL proof.

### H3 — HIGH: proposed reader fix (a) is order-based and incomplete
Evidence: `found` is filled in SQL row order — instances query (l.130-137) and cells query (l.253-260) have no ORDER BY;
`OrderBy(TableDefId).ThenBy(Ordinal).ThenBy(Month)` (l.368) is stable, so ties keep arbitrary SQL order. "First wins"
between the legacy Jan F_ and a later per-period F_ is therefore nondeterministic, not "legacy first".
Required change: an explicit, order-independent rule in the loader, the same as the UI's: per (year, table, row, month)
prefer the value attached to the owner instance of the month (inputs AND F_), fallback to other instances only when the
owner has none; same-tier differing values → treat as conflict consistently with `MonthRowResultOverlay` (which leaves
the cell empty, l.232-236) or fail loudly — not "first". Apply only when the doc's project is Monthly and the version is
routed (`IMonthColumnRouter.RoutesComputedVersion` / same owner rule as Q-17), else unchanged (yearly docs keep 202601).
Test: two candidates with DIFFERENT values, red without the fix, both for an input row and an F_ row. Keep the PERF-11
fingerprint consistent (it already includes current runs and row stamps).

### H4 — HIGH: HSE401 handling contradicts the human's 25.11 quiz answer
Evidence: memory `human-one-project-live-1125.md` (✎ 25.11 quiz to this design): HSE401 is "just a table… no rules for
submission or approval" → **do not block it by closed months**. Design §3 says the opposite (doc 93 read-only until a
document Reopen). Code offers no exemption: HSE401 v13 has `WorkflowMode = 0` (Standard, verified), and
`SheetEditGate.cs:162-175` WorkflowNone only neutralises approval status, not period closure. In addition table 775 is
`PerPeriodInstance` (`Enums.cs:59`): in LANDUI2026 the default/current period is 202611, so doc 93 opens EMPTY there,
instances are created lazily per month, and tools/hse401 take "the first period" of the project
(`Hse401Stand.Lib.ps1:406`) — in LANDUI2026 that is 202601 by luck of ordering.
Required change: design a behaviour that satisfies the answer (e.g., per-template "period-independent / always editable"
setting, or relocation of doc 93 data to an open anchor — both are product behaviour → quiz with default), or exclude
doc 93 from this move until that exists. Add AC for HSE401 editability after the move, and for what the doc list shows
for HSE401 in the current month.

### H5 — HIGH: the Q-17 script cannot produce the preview, and embedding it needs explicit rules
Evidence: script l.139-142 — `#Docs` takes only documents whose CURRENT project has `PeriodKind = 0`; anchor, period
rows and locks use `@Prj = d.ProjectId` (l.184, 200, 256, 487). Before the move doc 98 is in a Yearly project → Preview
returns nothing for it → the move-preview's `relocation{ownerCopies, conflicts, lockedTargets}` would be 0 and "conflicts
0" would be a false green. Apply only works AFTER `UPDATE doc.Document SET ProjectId` inside the same transaction.
Also: the script opens its own `BEGIN TRAN`/`COMMIT` per document and `ROLLBACK`s on error (l.160, 511-515) — inside an
EF transaction that ROLLBACK kills the outer transaction; `SET XACT_ABORT ON`, `LOCK_TIMEOUT 60000`, `DEADLOCK_PRIORITY
LOW` (l.45-47) leak onto the pooled connection; the `Q17-APPLY-v1` token and `@AllowLive` live guard (l.51-54) become
satisfiable by any product code path.
Required change: add a `@TargetProjectId` (or equivalent) parameter so Preview computes the plan against the target
calendar without moving; or run preview as "move + Apply + read report + ROLLBACK" in one transaction. Define transaction
ownership (script without its own TRAN when embedded), reset of session SETs, and where the guard lives (permission +
reason in the handler). Single-source rule stays: one script file, parameterised.

### H6 — HIGH (latent): tools/hse400 will silently re-create HSE400_2026 and hijack Land's source
Evidence: `tools/hse400/Build-Hse400Template.ps1:202,245-247,400` — `-Stage Project` builds code `HSE400_<year>` and creates
the project + one document if absent. After the purge, any rerun creates a new HSE400 doc with a LARGER Id; the loader
and `ChosenSourceDocumentAsync` take MAX(DocumentId) per year → Land switches to an empty document with no error.
`Compare-Hse400Year.ps1 -Fill` writes into "HSE400_<year>" too. Design §7.8 updates only tools/hse401.
Required change: update tools/hse400 project resolution (LANDUI2026 / by document, refuse to create) in this lane; add a
post-window AC "exactly one non-GOLD HSE400 document with instances in 2026 = 98". Consider a guard (warning in
deletion/creation preview or an Evidence test) against a second HSE400 doc in the same year.

### M1 — MEDIUM: rights delta also REMOVES access
Evidence (`sec.ResourceGrant`): `PeriodAdministrator` has Level 5 on projects 4, 5, 6, 7 and **no** grant on LANDUI2026
(2 role assignments). After the move, users whose access came only from PeriodAdministrator lose HSE400/HSE401.
`LandRestricted` (Level 2 on project 1) gains HSE docs; its Deny grants cover only Land sheets (6…314), not 184/248.
Required: preview lists losers too; the human question Q3 must mention the loss, not only the gain.

### M2 — MEDIUM: job race and missing job kinds
Evidence: doc 98's recalculation jobs have `TargetKey = NULL`, `DocumentId = 98` (itg.JobProgress) — the
`IRecalculationJob~project{id}-` prefix check covers only project recalcs; `migrate-project{id}[-template{id}]`
(`MigrateDocumentVersionHandler.cs:744,755`) is not covered. A job claimed between the check and commit reads the old
ProjectId (RCSI) and writes a Current run with `ProjectId = 7` → invisible to every reader (all join by project), then
deleted by the purge (`ProjectPurgeStore.cs:75`).
Required: window step "recalc quiet / worker paused" before the move; guard includes migrate targets and any Queued/
Running job with DocumentId; post-commit check `SELECT … FROM calc.CalculationRun r JOIN doc.Document d … WHERE
r.ProjectId <> d.ProjectId` = 0 before purging sources.

### M3 — MEDIUM: "enqueue nothing" is not the whole story — the first nightly will recalc
Evidence: relocation writes `aud.CellChange` Origin `Migration` (script l.463-467) → `NightlyInputChangeQuery.cs:162-168`
(branch 1, doc 98) and l.195-206 (branch 8, source document of Land 96/100) fire. The first night recalculates doc 98
and Land 96/100 for 202610/202611 (and HSE401 doc 93 in open months, creating empty instances).
Required: state the expected nightly effects; run the parity check (manual Land 96 recalc 202610/11 + checksum) BEFORE
the first nightly, with the H3 loader fix deployed.

### M4 — MEDIUM: verification does not see silent breakage for Jan–Sep
Evidence: Land 96 can be recalculated only for 202610/202611 (closed months need Reopen), so the result checksum
exercises only the M10/M11 composition. The "510 + checksum" of STORED results is trivially unchanged by the move.
Required: add a value-level diff of the loader output itself — all rows (year, month, stream, indicator, value) of
`ReadDocumentsAsync` before/after on the copy and on live (harness test or a diagnostic run) — for all 11 months; doc 98
owner values per (row, month) = pre-move values (175×11, value-equal, not counts); HSE400 rpt `_m` views per (row, month)
equal before/after; non-owner differing copies = 0.

### M5 — MEDIUM: rpt row multiplication
Evidence: `rpt.v_HSE400_GasComposition_*` — for `PeriodKind = 0` month values come from `fam`, aggregated per
(DocumentId, Yr, RowKey) and joined to EVERY period instance row. After relocation each HSE400 row appears under 11
`_PeriodKey`s, each carrying the full-year values (today: 1 row). Any consumer summing without filtering `_PeriodKey`
multiplies by 11. The human's 25.11 answer covers filtering by project code, not this.
Required: list in preview/Q5 ("reports must take one `_PeriodKey` per year or use `_m` by month"), check SSRS datasets
reading `v_HSE400_*`/`v_HSE401_*`.

### M6 — MEDIUM: rollback is only a full RESTORE
Evidence: §6 — reverse of the relocated move does not exist; RESTORE needs a separate human word and drops every other
live change made after the backup.
Required: (1) do NOT purge source projects in the same window — purge after the first nightly confirmed parity (purge is
the only irreversible step that needs no data); (2) pre-image export (instances, rows, cells, runs ids/ProjectId) of 93/98
tagged with the move CorrelationId to allow a targeted reverse script rehearsed on the copy; (3) explicit stop criteria
(which parity mismatch → stop before purge).

### M7 — MEDIUM: option choice — a scripted one-time tool is materially safer for THIS consolidation
The human's words (25.11) ask for one project with the four documents and purge via UI; they do not ask for a move UI
(AC-1 "through UI" was added by the card). All hard parts (run re-key, relocation, Jan copies, F_ recalculation,
HSE401 rule) are identical in (a) and (b); (a) adds ~1 000 lines of R3 code, a contract, a permission and e2e for a
cross-kind move that will likely never repeat (future 200-doc moves will be Monthly→Monthly, where only Document+runs
re-key is needed).
Recommendation: **(b+)** a `tools/document-move` script in the Q-17 pattern (Preview/Apply, `-AllowLive`, CorrelationId,
aud.SecurityEvent row, same-transaction re-key + Apply, pre-image export), rehearsed twice on `EcrLandB13Reh` with the
M4 value diffs; product changes limited to the H1/H3 loader owner-awareness (+ test) and tools/hse400/hse401 updates.
If the UI feature is still wanted, ship it for same-PeriodKind moves only (refuse cross-kind) — small and low-risk.
This choice needs a one-word human answer (default: b+).

### L1 — LOW: one `doc.CellStyle` row on 93/98 is not relocated (Q-17 ignores styles). Cosmetic.
### L2 — LOW: period policy changes from `HSE400_OPEN` (grace 800 / close 900 days) to `LAND_OPEN` (15/45); HSE400
months will close on Land's schedule. Accepted by the human's "closed months via Reopen"; show in preview.
### L3 — LOW: live window must include deployment of the new code (loader fix) before the move and respect "≤1 stand
window per day"; §7 does not list it.

## Minimal set of design changes to re-submit
1. §2.3/§3: owner-aware loader (inputs + F_), deterministic; decision on Jan copies (H1, H3).
2. §3: HSE400 Reopen+recalc 202601–202611 after the move (or human word on blank F_) (H2).
3. §3: HSE401 behaviour consistent with the 25.11 answer, or exclude doc 93 (H4).
4. §5: Q-17 parameterisation for target project + transaction/SET/guard rules (H5).
5. §7: tools/hse400 update, worker quiet, parity before first nightly, purge deferred, value-level diffs (H6, M2–M4, M6).
6. §4: option choice question to the human (M7).

---

# Re-review rev 2 (25.11)

Same method: read-only worktree `origin/lane/integ-b13` d75032896 (removed afterwards), SELECT-only on `EcrLandB13Reh`.

## Verdict: **PASS-WITH-FIXES**

Rev 2 closes every rev-1 HIGH in design. The two product predicates are correctly scoped (only HSE401 is affected).
Six fixes (B1–B6 below) must go into the P/T lane briefs and ACs **before** coding. None of them needs another design
round, but B1, B2 and B3 block **readiness for the window**.

## Status of rev-1 findings

| # | Status | Evidence / remaining gap |
|---|---|---|
| H1 Jan copies stay | **resolved** | The move script deletes the 202601 M02..M11 cells itself, and only where owner = copy (else abort); AC "non-owner copies = 0" plus P1 as a backstop. Q-17 l.487 no longer matters. Human Q1 is still open (default yes). |
| H2 blank F_ in UI | **resolved, with fix B2** | Reopen+recalc 202601–202611 replaces run 10181. The recalc order matters, see B2. W-4 compares against the pre-image (10181 is deleted by D-560), which is correct. |
| H3 order-dependent loader | **resolved** | P1 is an explicit owner rule for inputs and F_, with conflict → no value. Note: "SQL count unchanged" holds only if the owner is derived from the chosen instances (`year*100+m` among them) and PeriodKind from the existing project join. Otherwise it costs one more query; accept and state either way. |
| H4 HSE401 blocked/empty | **partially** | P2 is right for access and writes. Gap: instances outside the anchor, see B1. |
| H5 preview/tx/SET/guard | **resolved, with fix B4** | Running in-tx and then ROLLBACK gives real post-move numbers. Details in B4. |
| H6 tools/hse400 recreate | **resolved** (design) | T2 plus the post-window AC "one non-GOLD HSE400 doc = 98". The MAX(DocumentId) fragility stays by design and is guarded by T2 and the AC. |
| M1 rights lost | **resolved on the copy** | Verified: users 3 and 11 hold PeriodAdministrator together with SystemAdministrator and LandOwner (granted on project 1), so 0 lose access. The live preview must re-measure (live assignments may differ); the design already prints the per-user delta. |
| M2 job race | **resolved** | Worker stopped, script refuses on any Queued/Running job, in-tx `projectMismatch = 0`. Interactive users during the window are not covered: either lock the documents (UPDLOCK on doc 93/98 rows plus SheetEditGate) or announce a maintenance window. LOW. |
| M3 nightly | **resolved** | Expected effects are stated; parity runs before the first nightly. |
| M4 value-level checks | **resolved** | Owner cells and F_ per (row, month), rpt `_m`, and a full-year Land recalc on the copy via Reopen. This is the strongest check in the set. |
| M5 rpt ×11 | **resolved** (human Q3) | — |
| M6 rollback | **partially**, see B3 | — |
| M7 option | **resolved** (human chose the script) | — |
| L1–L3 | **resolved** | L3 is covered by W-0. |

## N1 — `[Period].Days` in HSE401 (new HIGH): confirmed, and the fix is correct
- Table 775 has exactly 9 formulas with `[Period].Days` (H_RATE, H_RQ1..4_T/H). No other `[Period]` usage in v13.
- Formula recalculation builds `PeriodContext` with `CalendarMode.Actual` from the period's bounds (`RecalculationService.cs:2876-2881`, `RowLocalPreview.cs:133`). So `Days` = End−Start+1: 365 in HSE401_2026 and 31 for January in LANDUI2026. The ×11.8 error is real.
- The dialect supports both `%` (Modulo, `Parser.cs:529`) and `[Period].Year` (`Evaluator.cs:1040`). `IF([Period].Year % 4 = 0, 366, 365)` = 365 for 2026, so W-1 parity in HSE401_2026 is provable. LOW: the rule is wrong for century years (2100). Acceptable; one line in the version note.
- No `cfg.CalculationBinding` on tables 773/775, so extending `CheckYearLayoutPeriodDependence` (`PublishChecks.cs:1286`) to PerPeriodInstance tables will not refuse v14 on bindings. Only v13 (the single year-layout version with a PerPeriodInstance table) would now fail a re-publish, which is intended.
- Before the move, P2 has no effect on doc 93 in the Yearly HSE401_2026: `GetDocumentAsync` and `ResolveInstancesAsync` require `PeriodKind == Monthly`. So W-1 parity is not disturbed by P2.

## P2 — year-scoped PerPeriodInstance: side effects
- Scope verified. Year-layout versions are 13 (HSE401: PerPeriodInstance ×1), 20 (HSE400: MonthsInColumns ×6) and 22/23/24 (Land: MonthsInColumns ×81, Static ×2). Static and MonthsInRows stay excluded, so **Land and HSE400 see no change**.
- Table 775 has 62 columns and **none is a month column**. `OwnerOf` (`MonthColumnRouter.cs:301-320`) gives the anchor to every column, and computed columns go to the anchor because `RoutesComputedVersion` is true. The claim "all cells → 202601" is true.
- The 31 `AppliesToTable(` call sites were checked for a table with no month columns. Overlay, MonthColumnArguments, CrossPeriodFanOut (fans out only to periods with existing instances: anchor only), RecalculationJob owner reads, PatchCells routing, Excel/Integration patchers, audit/sheet-changes and TableFillStore either are no-ops or redirect to the anchor.
- AccessDecisionService has a single gating point (l.2415-2426). `NewRowDecisions` (l.1686) derives from the same slices, and the writer re-check uses `EditDecision.AnnualPeriods` (`PatchCellsHandler.cs:1229`), so access and writes stay consistent.
- Recalc in a closed anchor: `RecalculationJob.AnnualOnlyAnchorsAsync` (l.3342-3409) is layout-based, so doc 93 can be recalculated in closed January while the year is open. There is no test for this, see B5.
- **Gap, see B1:** two places still decide by `LayoutKind == MonthsInColumns` rather than the router predicate. `RowStore.cs:1146` mirrors anchor rows into a newly created instance; the `CreateRowHandler.cs:164` path does the same. A non-anchor HSE401 instance created lazily (a GET with `periodKey=202611`, a deep link, the month view in the document list) gets **no rows**. Doc 93 has 6 430 rows, all of them dynamic. Routed cell reads are per row of the view instance, so that view is empty: the "opens empty" defect, just moved off the default path.

## In-transaction Preview/Apply wrapper
- Nesting is fine. Q-17 `BEGIN TRAN`/`COMMIT` take @@TRANCOUNT 2→1. On error the Q-17 CATCH does `ROLLBACK; THROW`, which aborts the batch, and with `sqlcmd -b` everything is rolled back. The wrapper's "@@TRANCOUNT = 0 after the include" check is reached only on success; harmless. SET options stay in the sqlcmd session. Correct.
- See B4 for the parameter block, live Preview and report equality.

## Fixes (into briefs/ACs)

**B1 (blocks the window) — P2 must cover instance creation outside the anchor.** For a routed year-scoped non-month table, either (a) do not create a non-anchor instance and serve the anchor instance for any view period, or (b) mirror anchor rows using the router predicate instead of `LayoutKind` (`RowStore.cs:1146`, `CreateRowHandler.cs:164`). (a) avoids up to 11 × 6 430 mirrored rows. AC test: GET slice doc 93-like at the Nov period shows the anchor rows (values equal) and creates no row-less instance. Window W-6 adds a direct GET of doc 93 with `periodKey=202611`.

**B2 (blocks the window) — W-5 recalc order.** Recalculate doc 98 for 202602…202611 first and **202601 last**, with P1 already deployed. Until the 202601 run supersedes 10181 (D-560 deletes it), the legacy F_ keeps covering the months that are not yet recalculated. With 202601 first, F_ M02..M11 disappear for the whole of W-5, and any dependent Land recalc in that gap computes without them. Also state that no Land recalc runs until the last doc 98 month is current, then recalc Land 96 202610/11 and compare.

**B3 (blocks the window) — Reverse-Move must match the state it reverses.** By W-7 or W-9 the reverse has to:
- delete every post-move run of 93/98 (W-5 recalcs, nightly) with their results, steps, inputs and issues;
- re-insert runs 10181 and 10183 with their original Ids (IDENTITY_INSERT or an equivalent) and their results;
- delete the `doc.DocumentPeriodReopen` rows from W-5;
- remove the instances, rows and cells created in 202602–202611 and restore the Jan cells.

Land 96/100 current runs superseded in 202610/11 after the move are not in the pre-image. Either export them too, or make "recalc Land 96/100 202610/11 after Reverse, checksum = baseline" an explicit reverse step. The rehearsal must run Reverse **after** a W-5 equivalent (Reopen+recalc+one nightly), not only right after Apply. `aud.*` rows stay; triggers forbid UPDATE/DELETE, and the reverse must not try.

**B4 — wrapper details.**
- The Q-17 parameter block is substituted by text, as `Apply-MonthColumnConsolidation.ps1` does. Wrapper variable names must not clash with Q-17's (`@Doc, @Prj, @n, @Now, …`) because `:r` puts both in one batch.
- Live Preview (W-3) runs Q-17 in Apply mode, so it also needs the token and `@AllowLive`. Make Preview end in an unconditional `ROLLBACK` enforced by the script itself, not by a ps1 flag, and apply `-AllowLive` only to Apply.
- "Live Preview = rehearsal report" will fail on legitimate live edits made since the copy was taken. Either rehearse on a backup taken on window day, or compare against rules (conflicts 0, copies = 175×10, `projectMismatch` 0, owner = copy) and not exact equality.
- Preview holds locks and consumes sequence values on live: acceptable with the worker stopped; state it.

**B5 — P2 recalc test.** Add a test: monthly project with Jan Closed and Nov Open; recalculating a year-scoped PerPeriodInstance doc writes its formula columns into the closed anchor (the `AnnualOnlyAnchorsAsync` path); with the whole year Closed it is refused. Window W-6 adds one recalc of doc 93 after the move, with a formula-result checksum equal to W-1.

**B6 — small items.** P1: say how the owner is found without extra queries, or accept +1 query and update the PERF-11 ratchet. N1: note the century-year limit. Interactive edits during the window: lock or maintenance notice (M2).

## Questions still open for the human (from rev 2 R2-6)
Q1 move instead of copy (default yes), Q2 HSE401 editable until the year closes (default yes), Q3 reports by `_PeriodKey` (default no), Q4 purge the day after (default yes). The defaults are consistent with this review.
