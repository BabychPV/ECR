# LAND-YEAR B5 — status and design of the remaining slices (L0 contract, L2, L3, L5, L7)

PE, 2026-11-14. Base: `origin/main` 95e488bdb (#903). Source plan: `.sync-local/reports/LAND-YEAR-plan-1112.md`
(approved 12.11 ~15:00Z; slices L0–L8, §c.1 G1–G6, §c row 20). Design only: nothing here is built or tested.
Status was established with `git log origin/main` / `git grep` on the tip; "not found" = no code on main reads/writes it.

## 1. Status of every plan item

| Item | What | Status | Evidence / what is missing |
|---|---|---|---|
| L0 table | `cfg.MonthInputAlias` + entity/config | **DONE** | PT01 768f18a1e (#883, B74 P1); discard store deletes aliases 3c88bf670 |
| L0 code | metadata snapshot, clone in `CloneTemplateVersionHandler`, `PublishChecks.CheckMonthInputAliases` | **TODO** | no reader of `MonthInputAlias` outside PT01/discard/builder lib |
| L0 contract | bulk `PUT /template-versions/{id}/month-input-aliases` (draft only), ADR-B74 Q3 | **TODO** | no endpoint; `Build-LandTemplate.ps1:300-302` refuses `-Stage YearMonths` with other stages until it exists |
| L0 slice `monthNumber` | column month for the grid | **DONE (verify)** | structure column DTO already has `isMonthColumn` + `monthNumber` (`schema.d.ts` ~39517–39596); L7 only has to use it |
| L1 | owner of calculated cells, recalc writes only owner + fan-out | **DONE** | 55032ec0d, b27de246a (#850); B24/L1b 29aa6fed6, 827735b45, dfd88565f |
| L1 tail | B58/B59/B60/B73 (PE-7), B102–B104 (PE-8), B25 = G6 | **DONE** | 85d1694ac, b4358b237, c5ae6575c, 521e9b06f (#899); faaeb85f3, 5339d6b7c, 3de3b4b9b (#903); d80f817e8 |
| L2 | per-owner overlay, alias inputs, year mode of results/issues | **TODO** | `CalculatedCellOverlay.cs` and `CalculationInputBuilder.cs` have no year/alias code; year reader exists (`CalculationResultStore.Year.cs`, `ICalculationYearReader`, #862) |
| L3 | migration doc 96 1.0.16 PerMonthCopies → 1.0.17 Year | **TODO** | prerequisite lane l4 version migration merged (#845, V-6 8c66017ce); planner/store still 1:1 by code; handler only refuses Yearly+PerMonthCopies (`MigrateDocumentVersionHandler.cs:807`) |
| L4 | builder `-Stage YearMonths`, `LandYearMonths.Lib.ps1`, tests | **DONE** | 1ce1c1c19, 4a51b1d6e, 1c4049774, 95f5e8247 (#846) — runs only once L0 contract exists |
| L5 | rpt views route 6/8 for year-layout versions | **TODO** | `05-rpt-views.sql:375` still `@colType NOT IN (6, 8)`; blockers gone: R-3 24a3ef36f, AN-163 9c4c74524 on main |
| L6 server | year modes | **PARTIAL** | G1/G2/G4/G5 done (below); still TODO: validation/Issues year mode (`GetValidationResultHandler.cs`), sheet activity `?year=` merge, workflow history all months, calc-issues year mode, export of calc M-columns (needs L2) |
| L7 client | year UI | **PARTIAL** | History owner-period done (`DocumentInspector.tsx:860-871`); everything else in §c rows 1–19 TODO; Issues still drops non-anchor cells (`DocumentInspector.tsx:306`) |
| L8 | rehearsal + perf (`tools/perf/Measure-LandYear.ps1`) | **TODO** | script not on main; after L0–L7 |
| G1 | sheet log reads owner period | **DONE** | e1825cc65, tests c8251129a (#893) |
| G2 | CSV audit export owner period | **DONE** | e3d23e6ea, c8251129a |
| G3 | /audit page Period column = viewed month | **TODO (L7)** | `AuditPage.tsx` untouched |
| G4 | "changed since export" from owner instance | **DONE (server)** | 22e04d3a6, c8251129a; client marker unchanged (fine: server answers) |
| G5 | rejection reasons from owner | **DONE** | df5836115, 9dc0d60a0 |
| G6 | "previous" values from owner (B25) | **DONE** | d80f817e8 |
| Row 20 | old "Calculation results" block under sheets | **DONE** | 70ef03567 (block → link to "all months"); year page #862 + B77 |

## 2. Remaining items — next change, needs, order, size, tests

Common test rules: periods `2099xx` only, each test creates its own project/document/users (no shared rows), every
new read path that is filtered by access gets an **Allow/Deny pair** (same data, the Deny side must not see it), and every
READY lists a **«Deny mutation (for AU)»** line: the one-line mutation that must turn the Deny test red.

### 2.1 L0a — alias metadata, clone, publish check (no contract, no migration)
- **Change:** `IMetadataCache` snapshot loads `cfg.MonthInputAlias` per table (`TableMeta.MonthInputAliases: (Alias, Month) → ColumnDefId`);
  `CloneTemplateVersionHandler` copies aliases with the column-id map; `PublishChecks.CheckMonthInputAliases` (append) —
  target is a month column of the same table with `MonthNumber = m`, alias ≠ any live column code, table is MonthsInColumns
  on a YearWithMonthColumns version. New error code only if no existing ECR-TMPL code fits (4 files, 7 guards — avoid if possible:
  reuse `ECR-TMPL-0422` family with a new messageKey).
- **Needs:** no migration, no contract, no live window.
- **Depends on:** nothing. **Size:** ~250 + ~250 tests.
- **Tests:** `PublishMonthInputAliasTests` (wrong month, foreign table, alias = live code → refused; valid → passes);
  clone test (aliases follow new ids); metadata test. Deny pair: n/a (config, admin-only path already covered).
  Deny mutation (for AU): drop the `MonthNumber = m` comparison in the check → the wrong-month test goes red.

### 2.2 L0b — `PUT /template-versions/{id}/month-input-aliases` (CONTRACT, own lane)
- **Change:** controller action on `TemplateVersionsController`, handler `SaveMonthInputAliasesHandler` (replace-all for the
  version, draft only → 409 for published), same validation as 2.1 at save time, `GET` of the same path for the builder's
  verify step; `openapi.snapshot.json` + `schema.d.ts` regenerated last; `docs/build` contract note (not `docs/tz`).
  Builder: allow `-Stage YearMonths` with other stages, `Verify-LandTemplate.ps1 -YearMonths` checks the 2 352 rows.
- **Needs:** contract change (lane `lane/integ-*`, full CI); no migration; no live window.
- **Depends on:** L0a. **Size:** ~200 + ~250 tests + generated.
- **Tests:** API tests: draft → 200 and rows replaced; published → 409; foreign template column → 400; caller without
  template-admin right → 403 (Allow/Deny pair on the same draft). Deny mutation (for AU): remove the permission attribute
  → the 403 test goes red.

### 2.3 L2 — alias inputs + per-owner overlay (no migration; contract only if year mode of results is added)
- **Change 1 (inputs):** `CalculationInputBuilder.BuildAsync` — for a run of period m on a year-layout table, add an
  argument per alias with `MonthNumber = m` (value of its target cell from the routed slice already read). Size ~120.
- **Change 2 (overlay):** `CalculatedCellOverlay.ApplyAsync` — year-layout view: read the current run of every period of the
  year via `ICalculationYearReader.ReadYearAsync` (one query), map `(m, SourceRowKey, OutputCode)` → binding whose column
  `MonthNumber = m`. PerMonthCopies path unchanged (gate = version Layout). Size ~200.
- **Change 3:** `GetCalculationResultsHandler` / `GetCalculationIssuesHandler` year mode (items tagged with month) —
  only the issues part is needed by L7 (results page already year-aware, #862). Contract: adds `monthNumber` to issue items
  → keep it in **L0b's contract lane or a separate contract lane**, not in the L2 code batch. Size ~150.
- **Needs:** no migration; change 3 = contract; no live window.
- **Depends on:** L0a (aliases in metadata). L1 done. **Order:** 1 → 2 → (3 in contract lane).
- **Tests:** golden: methodology arg `@Land_GasConsumption` resolves to `_M10` in run 209910 and `_M09` in 209909 (own doc);
  overlay: results of 209909 and 209910 land in M09/M10 of the same row, a result of a Closed month stays; perf test reusing
  `CalculationYearPerfTests` (one query, ≤ budget). Allow/Deny pair: user with Deny on sheet B sees no M-column result of
  sheet B through the overlay (mirror of 67351b775). Deny mutation (for AU): drop the readable-column filter in the
  overlay → Deny test red. Parity: the L8 zero-diff check is the acceptance.

### 2.4 L3 — version migration PerMonthCopies → YearWithMonthColumns (no EF migration; live window for doc 96)
- **Change:** `VersionMigrationPlanner` — `VersionMigrationMonthColumn(Source, Month, Target, TargetTable)` when the target has
  `C_M01..C_M12` (or alias rows) and C is removed; counted as transfer; selector-only removals reported "recomputed", not
  loss. `DocumentVersionMigrationStore` — `@monthColumnMap (o, m, n, t)` PK `(o, m)`, extra UPDATE of `CellValue` /
  `CellStyle` / `CellFormulaOverride` joined on `m = PeriodKey % 100` before the 1:1 map, idempotency guard extended,
  timeline rows written with the PT01 key (TemplateId). `MigrateDocumentVersionHandler` — allow PerMonthCopies → Year
  for Monthly projects when every month-varying column has a target (else refuse with the list); always enqueue
  `MigrationRecalculation` for the year; D-13 bindings check accepts 12 month bindings for one old binding. Persist the
  planner's month map with the SecurityEvent so History can show pre-migration rows (plan §d.3).
- **Needs:** no EF migration (JSON map in Prelude); no contract unless the dry-run report DTO gains new fields (it will:
  "transferred to month columns" / "recomputed" counts → **contract**, keep in its own lane); **live window** to run it on
  doc 96 (human's word; RESTORE fallback per 09.11 rule).
- **Depends on:** L0a+L0b (1.0.17 can be built), L2 (results visible after migration), PT01 (done). **Size:** ~600 + ~500 tests.
- **Tests:** `DocumentVersionMigrationTests.YearLayout.cs`: own doc with 209901–209912, varying formula cell of 209909 moves to
  `C_M09` in place; Closed month value survives without recompute; rerun = 0 changes; `.Race`/`.BackgroundProgress`
  re-proved; refusal when a varying column has no monthly target. Allow/Deny: migration endpoint already admin-only —
  pair = template admin 200 vs document editor 403. Deny mutation (for AU): skip the `% 100` join → the in-place test red;
  for access: drop the right check → 403 test red.

### 2.5 L5 — rpt views for year-layout versions (no migration; Sql script; views regenerated on start)
- **Change:** `05-rpt-views.sql` — `usp_GenerateTemplateViews` (`:375-516`) and `usp_GenerateStableViews` (`:1063-1300`):
  for `Layout = 1` route type 6 like entered (slot `Mo = MonthNumber`, owner via `rpt.ViewSlot`), type 8 `rv` from the
  owner instance's current run; `_Text` comment `:497` updated. Old codes `C` stay in stable views (NULL for 2026+ under 1.0.17).
- **Needs:** no EF migration, no contract, no live window (views are regenerated by the start-up job; the window only for
  the L8/live check).
- **Depends on:** L2 semantics (same owner rule) but no code dependency; R-3 and AN-163 already merged. **Size:** ~300 + ~250 tests.
- **Tests:** `ReportViewTemplateLayoutTests` + `ReportViewStableTests`: own year doc 2099, `C_M09` in the view = run of 209909,
  formula M-cell from owner instance; PerMonthCopies text byte-identical (fingerprint unchanged). Allow/Deny: rpt views
  are DB-role scoped — no app access path; state "n/a". Deny mutation (for AU): route 8 from the row's own instance
  (old rule) → the 209909 test red.

### 2.6 L7 — client year UI + G3 (needs L6 server pieces; contract for the server parts)
Server prerequisites (L6 remainder, **contract**, one lane): validation/Issues year mode (12 periods, item `periodKey`),
`GET sheets/{id}/activity?year=` merged by time with month tag, workflow history for all months, calc-issues year mode
(from 2.3 change 3). Size ~600 + tests.

Client batches (no migration, no contract of their own; start only after the server contract is pushed; e2e
`yearLayout.spec.ts` on a real stand in the same lane, per CLAUDE.md 12.10 p.6):
- **L7a (S items):** page `?periodKey` → anchor, recovery picker guard, Info tab owner/aggregate, calc-results panel hidden
  for year docs, stale reminder (server already ORs — verify), lock banners "рік YYYY", table chat no filter. ~250 + tests.
- **L7b (inspector):** Issues 12 periods (`DocumentInspector.tsx:306` filter), formula bar `ownerPeriodKey` from
  `monthNumber`, sheet log year (`SheetLog.tsx`), document log grouped by month. ~400 + tests.
- **L7c:** export label "рік", period reopen month chooser, live refresh of 12 owner periods, G3 `AuditPage.tsx` Period
  column = viewed month (needs `ownerPeriodKey`/`viewPeriodKey` in audit rows — check the DTO; if absent → contract in L6),
  "changed since export" marker check (server done). ~400 + tests.
- **L7d:** grid width — collapsed month bands per output block (Q1 answer), perf on T9_07 (590 cols). ~300 + tests.
- **Tests:** vitest per file (owner month in history/issues/sheet-log items), e2e `yearLayout.spec.ts` on a year doc with
  months 209909/209910: Issues of Oct visible, sheet log names the month, export one sheet. Allow/Deny: Issues/sheet log
  of a Deny sheet → empty in the year view (server pair in L6). Deny mutation (for AU): drop the sheet filter in the
  year merge of `GetSheetActivityHandler` → Deny test red.

## 3. Proposed batches (≤ 3–6 fixes each; migration/contract separate)

| # | Batch (lane) | Contents | Kind | After |
|---|---|---|---|---|
| 1 | `lane/integ-b5-l0a-l2` | L0a metadata + clone + publish check; L2 change 1 (alias inputs); L2 change 2 (overlay) | code only, 3–4 fixes | — |
| 2 | `lane/integ-b5-contract-1` | L0b alias PUT/GET + builder unblock; L2 change 3 issues `monthNumber`; L6 validation year mode; sheet activity `?year=`; workflow history all months | **contract**, full CI | 1 |
| 3 | `lane/integ-b5-l5` | L5 TemplateViews + StableViews (one fix, two procs) | Sql script | 1 (semantics) |
| 4 | `lane/integ-b5-l7a` + `l7b` | client S items; inspector Issues/sheet log/formula bar/doc log + e2e | client | 2 pushed |
| 5 | `lane/integ-b5-l7c-d` | export label, reopen chooser, live refresh, G3 audit page, grid bands | client | 4 |
| 6 | `lane/integ-b5-l3` | L3 planner/store/handler + dry-run DTO fields | **contract**, own lane | 1, 2 |
| 7 | L8 | rehearsal on E: + `Measure-LandYear.ps1`, parity 1.0.16 Sep–Nov vs 1.0.17 M09–M11 = 0 diff | scratch | 1–6 |
| — | live window | migrate doc 96 | human's word | 7 |

**First batch = #1:** L0a + L2 inputs + L2 overlay — no migration, no contract, no live window; unblocks everything else.

## 4. Open questions (default applies until answered)

1. **Error code for alias publish check:** new `ECR-TMPL-04xx` or reuse `0422` with a new messageKey? — default: reuse `0422` + messageKey `monthInputAliasInvalid` (no new catalog key).
2. **L3 dry-run report fields** (transferred-to-month / recomputed counts) — contract now, or text in the existing warnings list? — default: new fields, in the L3 contract lane.
3. **Calc-results "all months" page after 1.0.17** — keep or retire once doc 96 is migrated? — default: keep (human 12.11: "for the transition"), revisit after L8.
4. **Stable rpt views: old code `C` beside `C_Mmm`** — SSRS reports bound to `C` read NULL for 2026+ under 1.0.17. Notify report owners / add a compat column? — default: no compat column; list affected codes in the L5 READY for the human.
5. **L13 "R-2" on `05-rpt-views.sql`** (plan §b.5 order "after L13 R-3 → R-2") — no rpt R-2 commit found on main; is it still pending? — default: treat as not blocking (R-3 + AN-163 merged), rebase if it lands first.
6. **G3 audit DTO** — does the audit row already carry the owner and the viewed period? — default: verify at L7c start; if not, add to batch 2's contract.
