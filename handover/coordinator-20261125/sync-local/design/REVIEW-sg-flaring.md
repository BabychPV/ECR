# REVIEW (R3): DESIGN-sg-flaring.md (sour-gas flaring + thermal oxidizer on the ECR gas-composition registry)

Reviewer: independent agent, 25.11. READ-ONLY: SELECT on localhost `ECR` (legacy copy), `FLERT` and `EcrLandUi`; code read with `git show/grep`
on `origin/main` (ecdd84621) and `origin/lane/integ-b12-fix` (184bc11f6). I did not see the analyst's reasoning. Everything below was re-derived.

## Verdict: **FAIL**. Rework §5, then a short re-review.

§1–§3 (legacy analysis) are mostly correct and well sourced. The corrections are listed in A. §5 (binding) cannot be built as written.
Three of its mechanisms do not exist in the engine: per-event-date version lookup, a per-run read set, and a "MissingInputs with reason" for
registry misses. A fourth mechanism, a library `GASCOMP` holding the registry reads, would break AC-0 and the gas-composition
immutability lock without any error. The design also treats event ingestion as "missing" when the engine already exists. What is
actually missing is configuration plus three source shapes that the design does not describe. The required changes are in section R.

---

## A. Factual check of §1–§3

| # | Claim in design | Check | Result |
|---|---|---|---|
| A1 | Formulas total / with `@GCV`: INT30X 80/41, INT300 92/54, CONT30X 76/42, CONT300 86/52, TOX 152/118 | `ECR.dbo.AF_Formulas` (`FInfo_IsAvailable=1`, by name prefix) | **exact match** |
| A2 | Components referenced: INT30X 34+T, INT300 45+T, CONT30X 34+T, CONT300 45+T | regex `@GCV[._](SOR_\|EOR_)?(\w+)` over Flert/Thermaloxidizer/Common | **match** (35 / 46 / 35 / 46 names incl. `Temperature`) |
| A3 | TOX "47 (45 FLERT + Methanol; + Helium, C2H6S forced 0)" | same regex | **wrong count**: TOX references **48** names = all **46** ECR components (Methanol included) + `Helium` + `C2H6S`; no `Temperature` |
| A4 | Union = the 46 ECR `COMPONENT` codes | same | correct |
| A5 | L1: TOX `GCV_EOR_*` filled from `gsSor` | `R_Air_HSE_Thermaloxidizer_SG_Calculate.cs:21,32,35`; output `…_SG_Output.cs:26,29` | **confirmed** |
| A6 | L2: 417 duplicated, 318 SOR / 319 EOR / 575 SOR+EOR; 2026 used GC 2050 | FLERT `FlaringStream ⋈ Type ⋈ GasComposition`; `CalcResult_FlaringThermaloxidizer` (`StreamNum=417 → FCID_SG 2050`) | **confirmed**; the pin 417→575 reproduces the 2026 results |
| A7 | 481A/B H2S overwritten by the pre-formula | `…_SG_Calculate.cs:53-68` (name built with a **literal** `481`); `HSE_TO_SG_481_{SOR,EOR}_H2S = CST.k24 − Σ(others)` in AF_Formulas | **confirmed**. ⚠ The repo note `docs/build/archive/bridge-thermaloxidizer.md` says these formulas are "unreachable", which is wrong for H2S (true only for `481_*_Check1`). Correct that note, or the lane may drop the pre-formula |
| A8 | INT rule = stream + case + validity | `R_Air_HSE30X_Int_SG_Calculate.cs:66`, `R_Air_HSE300_Int_SG_Calculate.cs:64-68` | **incomplete**: the rule also requires **stream `isOnshore` = event `IsOnshore`**. ECR needs a mismatch check (issue, not zero) |
| A9 | L3: CONT/TOX lookups ignore isOnshore | `R_Air_HSE30X_Cont_General.cs:166, 233` | partly right: the CONT300 (`"416"`) and TOX lookups go by **number**. The CONT30X lookup goes by **`Stream_ID`** (line 233), so it is not affected |
| A10 | CONT split v1 3/7,3/7,1/7 (2024), v2 0.4/0.4/0.2 | `PI AF\ECR_01_Air.xml:3950973-3951010, 3951042` | confirmed (v1 window 2023-12-31T19:00Z…2024-12-31T18:59:59Z = local +05) |
| A11 | Counts 2 899 INT (1 657/1 242), 1 096 CONT, 109 FCID, 0 unresolved | `AF_FlaringsIntermittent_SG`, `CalcResult_*` | confirmed. CONT: 1 096 = 274 onshore + 3 × 274 offshore (1 result row per stream) |
| A12 | TOX 2026 SOR-only, GCs 2044/1573/1579/1581/2050/1661/2046 | `CalcResult_FlaringThermaloxidizer` | confirmed, and it reveals **L6** below |

**New legacy defects and behaviours that the design does not list:**

- **L6: Train 1 always uses 481B.** `R_Air_HSE_Thermaloxidizer_General.cs:288,295` checks `flareMain.Train == "1"`, but the data holds `"Train 1"`/`"Train 2"`
  (`AF_Air_HSE_Thermaloxidizer`; Category uses `Substring(len-1)`, line 339). Result: every Train 1 SG row in 2026 used 481B / GC 2046
  (query: `Train 1 … StreamNum 481B FCID_SG 2046` in both modes). The human must decide **fix (Train 1 → 481A, stream 574) or keep**. This is
  analogous to the EOR decision, and if fixed it is a second known parity difference.
- **L7: TOX skips the whole day/month when the HSE400 month is missing.** `General.cs:137-142` (`if (preHse400 == null) continue;`) skips SG as well.
  ECR must not inherit this. SG does not need HSE400, so a missing HSE400 month must surface as an issue and must not drop the rows.
- **L8: open events are dropped.** INT keeps only `EndDate != null` (`R_Air_HSE30X_Int_SG_General.cs:80`) and the AF query uses `EndTime IS NOT NULL`
  (`AF_Air_Flaring_Intermittent_SG_Data_Get`). Specify how the ECR event sync treats an open event frame (row without `$end` → wait/skip).
- **Cross-month events:** 47 of 2 899 INT events (1.6 %) start and end in different months. Legacy passes `CalculationDate = EndDate`
  (`Int_SG_Calculate.cs:61`). ECR attributes an event to the period of its **start** in project TZ (`SourceEventPeriods.Locate`, D-179). This gives a
  parity difference in monthly totals, which must be listed.

---

## B. Event-frame ingestion: what exists and what is missing (item 2)

**Exists on `origin/main` (the design §4.1 shows only "0 rows / 0 hits"):** `ext.SourceEventMap/FieldMap/ValueMap/Link/AttributeValue`
(`Domain/Entities/External/SourceEventMap.cs`: event → row of a **Dynamic** table, `$start/$end` mandatory, `LookupByCode/ByName/ValueMap`
into registry Lookup columns, single-attribute **equality** filter `SetFilter`, volume modes). Also `Jobs/SourceEventSyncJob.cs`,
`SourceEventFieldPlans.cs`, `Adapters.PiAf/PiSqlClientDataSource.cs` + `SourceEventFolder.cs`, `SourceEventPeriods.cs` (event → period by
start in project TZ) and `Calculations/FlareEventArguments.cs` (derived `Total/Duration/FlareUnitMode/IsPilot/Category`, used by
`CalculationInputBuilder.cs:142` for both engines).

**Missing (configuration plus contract), none of it in the design:**

| Consumer | Legacy source shape (evidence) | Gap in ECR |
|---|---|---|
| INT | 4 AF views `FlareEventFull_{Auto,Auto_Day,Manual,Manual_Day}_V`, filter `INSTR(FluidDepressurized,'sour')>0`, `EndTime IS NOT NULL`, `Total = AdjustedV`, `isAuto` (`AF_Air_Flaring_Intermittent_SG_Data_Get`); derived `IsOnshore/IsIslandA/IsIslandD` from `Location` **substring**, `IsSour` from `FluidDepressurized` (`PrepareData_GetFiltered_Flaring_Intermittent_SG`) → TypeReportID 300/301/302 | `SetFilter` is equality-only, so "contains 'sour'" needs either a filter operator (contract and probably a column, i.e. a migration) or ingest-all + `reject when` in the script. Specify the derivation of TypeReportID/IsOnshore. A source entity is needed per AF template/view |
| CONT | `AF_FlaringsContinuous`: **1 event frame per day, wide** (279 rows, 16 location attributes incl. `DIsland_LP_Sour_Analyzers`, `Onshore_LP_Sour_Analyzers`) | SourceEventMap maps 1 EF → 1 row. One map per location attribute (onshore, offshore sour) is needed, or an unpivot. The offshore ×3 split (one result per stream) must be designed |
| TOX | `AF_Air_HSE_Thermaloxidizer`: rows come from the Excel workbook **`HSE372_Thermaloxidizers_v1.0.2.0.xlsm`** (`Con_File_Name`), `Date` is **nvarchar `M/d/yyyy`**, 40 junk rows (`Train='0'`, NULL); monthly input = SQL **SUM of daily volumes** then recalculated (`PrepareData_GetFiltered_HSE_ThermaloxidizerByTrain`), not Σ of daily results | Confirm that the human's "AF event frames" applies to TOX too (its origin is an Excel connector). Specify the date parsing, the junk-row filter, and how the monthly grain is built (aggregated inputs → separate calc) |
| all | live `EcrLandUi`: `ext.DataSource` = only `FLERT` (Transport 2), `ext.SourceEntity` = 0, `SourceEventMap` = 0 | PI AF data source + credentials are a **human/DBA prerequisite** for AC-0/AC-4. The design does not list it |

---

## C. Script access to registries (item 3)

- Script engine: `REGFIND` (**primary key only, no alternate-key argument**; null key part returns **Null**, not found returns `#N/A`),
  `REGONE` (full scan), `REGSUM/REGCOUNT…` (empty result → **0**; an indexed path is used only when a top-level `AND` conjunct is
  `ROW.<lookup> = <entry>`: `Expressions/Evaluation/RegistryForms.cs` `Rows()`), `REGFIELD`, `entry("R","code")` (the entry must exist at save, otherwise
  `unknownRegistryEntry`; `docs/user/methodology-script.md` §3). Allowed in Legacy mode (doc §17).
- `cfg.RegistryUse` at publish/switch: `ScriptMaterializer.cs:297-313` writes one `"script"` use per code in **`script.RegistryCodes` of
  that script only**. `ScriptRegistryScopes.cs` states that registry reads inside an `import`ed library are not visible from the importer.
- **The design's library `GASCOMP` therefore breaks AC-0 without any visible error.** Dependents of a gas edit are found by
  `GasCompositionStore.ListDependentYearsAsync` (`:224-247`) and `RegistryImpactStore.QueryAsync` (join
  `RegistryUse.SourceId = CalculationResult.MethodologyVersionId`). A library's uses carry the *library* version id, and results carry the
  *consumer's* id, so they never match. The same join feeds `ListLockedPeriodsAsync` (`:187`), so **locked-period protection would also
  be bypassed**.
- Per-event-date versions: the snapshot is one per run at **`period.End`** (`ScriptCalculationModule.cs:554`, `IRegistrySnapshotLoader`
  "business date = last day of period"). `GASCASE(stream, case, onDate)` and the "(legacyId, caseName, yyyymm) index" from §5.2/§5.5/§5.7 do
  not exist. With month-aligned versions (gas-composition rule) and **monthly-period documents**, the start-month version falls out
  automatically: the event goes to its start period, and the snapshot of that period holds that month's version. This matches the human decision. In a
  **yearly** project every event would see the 31 December version, which is wrong whenever a version changes mid-year.
- "Missing input with reason": `MissingArguments` is only raised for empty argument cells (`ScriptCalculationModule.cs:289,429-451`). A registry
  miss gives either `#N/A` (step error) or a `reject when` row (status "rejected", reason = condition text). A parameterised
  reason requires a contract change.
- Bridge: scripts cannot assign `@GCV.X` arguments. The converter yields script code (doc §14.1 pattern
  `REGFIELD(REGFIND(...))`). The generator must emit, **inline in each consumer script**, a set of `var` statements
  `REGSUM("GAS_COMPOSITION", ROW.CASE == case && ROW.COMPONENT == entry("COMPONENT","X"), ROW.MOL_PCT)`, with `ROW.CASE == case` first (index), and the literal `0`
  for Helium/C2H6S (`entry()` would fail at save). Stream by `LEGACY_ID`: `REGONE` (523-row scan per row, acceptable), or map
  `GasCompositionID` to a `STREAM` Lookup column at ingestion (`ValueMap`/`LookupByCode`).
- Prerequisite: **`Common` is Draft on live** (`calc.MethodologyVersion`: Common Engine 1, Status 0; no published version; `calc.MethodologyImport`
  empty). Consumers that `import Common` need a published Script version first (`importNoVersion`/`libraryNotScript`).

## D. Recalculation and performance (item 4)

- The existing path (gas edit → `IRegistryImpactRecalculationJob` per registry × year → documents with a *current* result reading the registry, open/grace
  periods, approved sheets skipped) is correct **only if the reads are inline** (see C).
- §5.6 "read set per run": **does not exist** (no read-set column or table; grep `ReadSet|EntriesRead` returns 0). It needs new persistence, i.e.
  **a migration**, which contradicts §5.1 "no migration". It also needs a new filter in `ListImpactedForYearAsync`. AC-3 rests on it. Recommendation: drop AC-3 to
  "year × registry, coalesced (existing)". There are few flaring documents (≈4–8 k rows/year), so the gain is small.
- `GasCompositionService.cs:366/441/477` enqueues for **both** `STREAM_CASE` and `GAS_COMPOSITION`, which means 2 coalesced jobs per year for the same documents.
  Verify that the per-document recalculation merges them; otherwise every edit causes double work.
- "~200 documents": the analysis talks about event volume, not about document fan-out. State how many flaring documents a project has (probably
  1–3 per area/year) and measure on the large backup (`<10 s` target is fine). The per-row cost is acceptable **only** with the indexed `REGSUM` form
  (≤ 46 × ~23 rows). A full scan would be 22 725 rows × 46 per row, giving `#BUDGET`/minutes.

## E. Parity plan (item 5)

Adequate in scope. Add the following:
- set the script versions to `NumericMode=Legacy` explicitly and state the tolerance against it;
- the keys: INT per `EFID`; CONT per (day, stream), with 3 rows/day offshore; TOX monthly **and** daily;
- the known differences: EOR (L1), L6 if fixed, cross-month attribution, TOX days skipped by L7;
- what cannot be tested locally: no EOR rows; no 2024 split v1; **no composition version was ever created in FLERT** (all `ValidFrom 2022-01-01`), so the
  version-boundary rule is unit-tested only. `EcrLegacyProd` is absent (`sys.databases`), so a full-year prod copy is a human/DBA request;
- project TZ: season (Apr–Sep) and month boundaries in **project local time**, with a test for an event at 19:30Z on the last day (+05 → next month).

## F. Security, migration, rollback, test data (item 6)

- Rights: `GasComposition.View/Edit` exist (seed `00-prelude.sql:153-154, 657-664`). The new registries `TOX_GAS_STREAM` and `LP_SOUR_SPLIT` are edited with the generic
  `Registry.Edit` and **have no lock rule**, so an edit can silently change closed months. Either put them under the gas-composition domain rules (lock and
  audit), or make them temporal and refuse edits that touch closed months.
- Migration: none needed if the read set is dropped. The registries need a **named seed section** (definitions + v1/v2 split + 8 TOX pins), which
  the design omits. An event-filter operator ("contains") would need a migration plus the migration token.
- Rollback: **no section**. Add: deactivate the `SourceEventMap`s, deprecate the methodology versions, revert the template version, keep the registry rows.
- Test data: the fixtures are good. Add 481 H2S balance, Train 1 (L6), CONT 3-stream split, cross-month event, null `GasCompositionID`/`StreamType`,
  `NO_COMPOSITION` case, onshore mismatch, missing HSE400 month (L7).

## G. Silent-zero risks (item 7)

| Path | Engine behaviour | Required guard |
|---|---|---|
| event without `GasCompositionID`/`StreamType` | `REGFIND` with null part returns **Null**; `REGSUM(ROW.CASE == null…)` returns **0** | `reject when` null key |
| stream/case not found | `REGONE`/`REGFIND` give `#N/A` (step error, no output) | acceptable, but give it an explicit message |
| `NO_COMPOSITION` case (16 in FLERT, Σ = 0, no rows) | `REGSUM` returns **0**, so all emissions are 0 | `reject when REGFIELD(case,"NO_COMPOSITION")` or Σ = 0 |
| CONT split registry missing for the date | 0 rows, so no output | `reject when REGCOUNT(LP_SOUR_SPLIT…)=0`; also Σ coef = 1 |
| TOX column without SOR/EOR case | Null | reject |
| Helium/C2H6S through `entry()` | save error | literal 0 |

---

## R. Required changes (before brief)

1. **No registry reads in an imported library.** Generate the composition reads inline in every consumer script. Alternatively, an explicit
   contract change: `ScriptMaterializer` writes `cfg.RegistryUse` for the transitive imports, with a test that a gas edit queues the consumer and that the lock sees it.
2. **Replace `GASCASE(onDate)`/yyyymm index** with the real mechanism: snapshot at `period.End` plus **monthly-period flaring documents**
   (state the project period kind). Otherwise an engine change (per-row date snapshot via `IRegistrySnapshotSource.Build`) is needed and must be scoped.
3. **Drop or re-scope AC-3 / §5.6 read set** (it needs a migration). Use the existing year × registry recalculation; check the double enqueue.
4. **Write the silent-zero guards** (table G) as `reject when` rules plus tests (AC-2). Decide whether "missing input with reason" needs a contract change
   or whether "rejected + condition text" is acceptable.
5. **Ingestion section per consumer** (table B): AF sources/views, filter `sour` (operator or reject), derived TypeReportID/IsOnshore, open
   events, CONT unpivot + 3-stream split, TOX Excel origin + text date + monthly SUM grain. Add the PI AF data source as a human/DBA prerequisite.
6. **Add L6 (Train 1 → 481B)** as a new quiz question to the human (default: fix, as with EOR). Add L7/L8 and cross-month attribution to the parity known differences.
7. **Correct the facts:** TOX 48 refs (46 + 2 forced 0); INT also matches `isOnshore`; CONT30X by `Stream_ID`; the archive `bridge-thermaloxidizer.md` statement about 481 H2S.
8. **Prerequisite: publish `Common` (Script)** and import the `Flert`/`Thermaloxidizer` constants; Helium/C2H6S as literal 0.
9. **New registries:** named seed section, rights and lock rule (F). Add a **rollback** section.
10. **Parity:** NumericMode Legacy; keys and known differences (E); prod-copy request for EOR/2025/version changes.

---

## Re-review rev 2 (25.11, read-only)

Scope: my blocking points R1–R10 against DESIGN-sg-flaring.md rev 2 §5, plus checks for new blockers. Human 25.11: L6 is fixed (H10). The human has since
**rejected the monthly-period approach**: each event row must use the composition valid on that row's own event date. So §5.3 (monthly projects +
`reject when period.Days > 31`) is **pending rev 3** (per-row dated registry read, engine change) and is not judged here.

**Verdict on everything except version selection: PASS-with-conditions. Overall: open until rev 3 of the version-selection part is re-reviewed.**

| My point | rev 2 | Status |
|---|---|---|
| R1 library breaks RegistryUse/lock | inline generated reads, no `GASCOMP`; `Common` has no REG reads | **resolved** |
| R2 per-date version | monthly projects + 31-day guard | **pending rev 3**. Note for rev 3: live HSE400/HSE401 are `PeriodKind 2` (1 yearly period, month columns), LAND_UI is monthly. The guard would reject every row in a yearly project, and Custom periods ≤ 31 days that cross a month would pass. Rev 3 makes this moot if the read is per row date |
| R3 read set | dropped; year × registry recalc; dedupe test in scope | **resolved** |
| R4 silent zeros | reject guards table §5.5 (null key, not found, onshore, NO_COMPOSITION/Σ=0, split, pins); "rejected + condition text" accepted as missing input | **resolved**; C3 |
| R5 ingestion | per-consumer table §5.4; PI AF data source named as a human/DBA prerequisite; CONT two maps; TOX monthly = SUM inputs then recalc | **resolved**; C1, C2 |
| R6 L6/L7/L8 | H10 Train 1 → 481A/574; SG never reads HSE400; open EF not ingested [verify re-pick] | **resolved** (L8 needs its lane test) |
| R7 facts | — | accepted |
| R8 Common draft / Helium | prerequisite; literal 0 | **resolved** |
| R9 registries, seed, lock, rollback | §5.7 | **resolved in intent**; C4, C5 |
| R10 parity Legacy | §6 | accepted |

**Conditions (lane DoD):**
- **C1 `SetFilter("FluidDepressurized", Event, "Sour gas")` is safe at runtime.** The filter is applied in memory, trimmed and case-insensitive
  (`SourceEventSyncPlanner.PassesFilter`), and a missing/NULL attribute gives `Unknown` (not written, link kept). The value set is closed on the data: legacy SG has only
  `Sour gas` 2 899; FG (complement filter `INSTR(...,'sour')=0`) has only `Fuel gas` 4 819 and `Nitrogen` 1 500. But that is 2026 data only.
  **The proposed F13 "distinct ⊆ set" check cannot see dropped values:** ECR stores only events that pass, and a `Fail` is just counted as `filtered`. The check
  must read the source (AF query or raw sync attributes before the filter), or the sync must log distinct rejected filter values. Otherwise a new spelling such as
  "Sour gas/condensate" disappears silently.
- **C2 CONT two maps on one daily EF template are allowed.** `UQ_SourceEventMap` = (SourceEntity, Document, TableDef), so two tables are fine.
- **C3 `REGFIELD(gcase,"T_C") ?? 0`:** the `??` operator is not in `docs/user/methodology-script.md`. Use the documented null function / `if`. Generator test.
- **C4 The 409 lock for `LP_SOUR_SPLIT` / `TOX_GAS_STREAM` needs more than "reuse":**
  - (a) `GasCompositionStore.ListLockedPeriodsAsync(GasRegistryIds)` is typed to the 4 gas registries. It needs an overload by registry id.
  - (b) The guard point is `ExternalRegistryGuard`, called by about 20 handlers: upsert, `RegistryBatchHandler`, `RegistryEntryCsvHandlers`, `SetEntryValidityHandler`,
    `RegistryHierarchyHandlers`, `RegistryRestorePurgeHandlers`, `RegistryAdminHandlers` (definition changes), NAE/permit paths. The check must compare **old and new
    validity windows** of every touched entry (including delete/purge/restore and edits to the definition fields) against locked months. Write one test per write path,
    not just upsert.
  - (c) The generic editors check `Registry.Edit`. The `GasComposition.Edit` rule must be enforced in the same guard, otherwise any `Registry.Edit` holder can edit.
  - (d) Same semantics as gas composition: a closed/approved month whose rows were all rejected has no current result and is not locked. State this.
  - (e) Rev 3 note: with per-row dates the lock stays period-based, which is correct as long as a period is locked as a whole.
- **C5 Seed `-- SGF:`** is a hot-file section. The ready letter must name it. `FLARE_LOCATION` ValueMap: an unmapped new AF location gives null `LOCATION`, the onshore guard
  rejects the row, and that is visible (acceptable). Add a test for it.

---

## Re-review rev 3 (25.11, read-only): §5.2a per-row dated read, lock, C1/C3/C4/C5

**Verdict: PASS-with-conditions.** No blocker remains. The conditions D1–D6 below are lane DoD items.

What was verified (code on `origin/lane/integ-b12-fix` 184bc11f6):
- The building block exists. `IRegistrySnapshotLoader.LoadSourceAsync` + `IRegistrySnapshotSource.Build(date)` (L5-12) is already used per date by
  `TemplateRegistrySnapshots.cs:85-89`.
- `ScriptEvaluationContext` is created **per row** (`ScriptCalculationModule.cs:258`) and takes `_registries` from the binding (`ScriptEvaluationContext.cs:39,68,84,141`).
  A per-row snapshot (E5) therefore fits without restructuring. `GetRegistryField` (ROW.lookup paths) reads the same field, so it is covered.
- E7 holds: `MonthColumnArgumentsModule.cs:147,160-179` goes through `PrepareScriptAsync`. The yearly month-column shape works for event rows (each row has its own date) and
  for `TOX_MONTHLY` static rows carrying `MONTH`.
- `cfg.RegistryUse` and recalc are unchanged. The registry codes stay literals in the consumer, `ScriptMaterializer.cs:297-313` writes the same rows, and gas-edit recalc stays
  registry × year (`ListImpactedForYearAsync`). Re-runs stay reproducible (`RegistryAsOfUtc` on the source load).
- Lock: the existing `GasCompositionRules.RequireUnlocked/LocksOf` (`GasCompositionRules.cs:45-64, 241-271`) **already** compares the edit range with the whole
  period window (`yyyyMM` → month, `yyyy00` → year). The "required check" is therefore met by reusing these rules. An approved yearly document locks the whole year,
  which is correct.
- C1 (pre-filter AF check), C3 (`?:` instead of `??`), C5 (seed section named; unmapped location → visible reject) are resolved.

Conditions:
- **D1. E2 is mis-stated.** `PeriodScope` has only `Month/Year/All/literal month` (`Ecr.Expressions/Binding/PeriodScope.cs:29-41`). There is no "period end" kind. Specify:
  registries read under `registry as of` get `PeriodScope.Year` (or `All` when unsure). This matters only for the B179 graph and source cascades; the gas-edit recalc does not use it.
- **D2. Missing E item: preview/trace parity.** The live preview (`ScriptPreviewRunner`, the methodology preview handlers) must honour `registry as of`, otherwise the preview shows
  `period.End` values. Add a test that preview equals run for a row on each side of a version boundary.
- **D3. E6 interval set** must include every temporal registry in the loaded source, including the transitive Lookup targets that `LoadSourceAsync` adds. Add a test with a
  version change only in a Lookup target (e.g. `STREAM_CASE`) that is not named in the script.
- **D4. Lock store:** return windows from `doc.Period.PeriodStart/PeriodEnd`, not from key arithmetic (Quarterly/Custom keys are not `yyyyMM`/`yyyy00`). Reuse
  `RequireUnlocked/LocksOf`.
- **D5 (new, important). `FLARE_LOCATION` as non-temporal = window (−∞, +∞)** means that after the first closed/approved period **every** edit, including **inserting a new AF
  location code**, gets a 409. A new AF location could then never be mapped, and its events would be rejected forever. Either let inserts of a new code through (it changes no
  locked result, because those rows had no result), or make `FLARE_LOCATION` temporal. Add a test for it.
- **D6. Guard callers:** most of the 21 `ExternalRegistryGuard` callers are permit/NAE paths for other registries. The architecture test must assert the flaring guard on the
  **generic registry writers** (upsert, batch, CSV, validity, hierarchy, restore/purge, admin/definition), and must not force it onto unrelated handlers.
