# P-1124-2 — every methodology version on ECR Script, without losses (PHASE 1: design + evidence)

Human 24.11: move the system to the new representation WITHOUT LOSSES (calculations, quality); no two chairs, only the
new script editor; hidden Formulas versions in the DB rejected. Water ECW_C09_02_01 must be SOLVED, not excluded
(the permanent exclusion `$MsPermanentExclusions`, P-1123-4, goes away).

Status: phase 1 only. No production code, no commits. Evidence tooling (uncommitted, worktree `F:\ecr-wt\p11242`,
branch `lane/integ-p11242-ms-lossless` from origin/main `ecdd84621`): `tools/Ecr.MethodologyScriptMigration/Cli/P11242EvidenceCommand.cs`
+ 4 lines in `MigrationCli.cs` (command `p11242-evidence history|water`). Evidence: `F:\bld\p11242\`.

Copies used (never live): `EcrP11242Scratch` on `H:\EcrData\EcrP11242Scratch` restored from
`E:\EcrBackup\land-ui\w15z\bak\EcrLandUi-after-W15z-20261124-1653.bak` (target state), and for one experiment from
`...before-W15z-20261124-1629.bak` (+ EF migrations D568..P112320 applied to the copy). F: was too slow for restore
(1.3 MB/s, other lanes writing), so the copy sits on H: (H: free now 16.6 GB — drop the copy after review).

⚠ The backup is 16:53; live at 20:20 had 296 results / 61 snapshots, the copy has 348 results / 0 snapshots. Phase 2
must re-run every gate below on a fresh live backup taken right before the window.

## a) Inventory (copy after W15z) — `F:\bld\p11242\inventory-formulas-versions.csv`

| | count |
|---|---|
| Formulas versions | 78 (76 Published, 2 Draft) across 46 methodologies |
| current (effective) Formulas versions | 1 — water ECW_C09_02_01 V1 (id 46) |
| superseded Formulas versions (a newer Script version of the same methodology exists) | 74 published |
| drafts | Common V1 (id 1, library, 49 formulas, imported by nobody), ECW_C05_01 "test" (id 58) |
| with live-proven shadow ScriptText (byte-identical to the W15z package, hash-matched) | 73 |
| without shadow text | 5: Common V1 (draft), C05_01 test (draft), C09_02_01 V1 (water), C11_15 V1 (`rowsNoRules`), C12_03 V1 (argument the template no longer has; converted but excluded from apply) |
| methodologies with no Script version | 2: Common (1), ECW_C09_02_01 (46) |
| CalculationResult rows on Formulas versions | 348 on copy (296 on live) — 29 versions, ALL in document 96 (Land), runs 10106 (202609, 13.11) and 10153/10154 (202610/202611, 24.11 14:35); all runs `Current` |
| arc.CalculationResult on Formulas | 0 |
| SubmissionSnapshot | 0 on copy (61 on live, all newer than the backup) |
| Child rows still kept by Script versions (old formulas kept for rollback) | MethodologyFormula 762, Constant 5 635, Rule 343, CategoryRule 37 (Formulas versions: 733 / 14 784 / 735 / 68) |

Every current methodology except water already runs on Script (45 in-place switches + 21 new golden versions at W15z).
What is left on Formulas is **history**: superseded versions that hold results of document 96, plus water and 2 drafts.

## b) Conversion model — IN-PLACE (coordinator default) — confirmed, no blocker found

Same `MethodologyVersion.Id` becomes `Engine = Script` with the live-proven shadow `ScriptText`/`ScriptHash` already on
the row (73 of 78). Why it loses less than re-pointing results to the existing Script versions:

1. **Links stay true.** `CalculationResult.MethodologyVersionId` and `SubmissionSnapshot.MethodologyVersionsJson` keep
   pointing at the version that actually produced the number (version, effective date, rules, golden set). Re-pointing
   would rewrite immutable archive (B69) and claim that V6 produced a number V5 produced; the newer versions also differ
   on purpose (borrowed rules C11_10, new golden sets), so "same numbers" would be false for some rows.
2. **It is the mechanism already in production**: `SetScript` on a Formulas version (shadow) + `SwitchEngineForMigration`
   (Published/Deprecated) + `SwitchScriptEngineHandler` (atomic, audited `scriptMigration.switch`, ScriptHash gate) —
   45 live versions went this way at W15z.
3. ContentHash is untouched (it is fixed at publish, the switch never recomputes it); ScriptHash is the proven text hash.

What has to be added (one-time, explicit, logged — not a general backdoor):

* **`finalize-formulas` migration path** (new handler + endpoint `script-migration/finalize`, permission
  `Calculation.Publish`, plus tool command for copies): input = list of (versionId, ScriptHash, proof source). For each
  version it requires the proven hash = stored hash, writes provenance (below), switches engine, and refuses anything
  else. It is allowed ONLY while `Engine = Formulas` rows exist; after the last one it refuses with
  `formulasAlreadyFinalized` (so the path dies with the data). Each version = one `aud.PublicationEvent`
  (`operation = scriptMigration.finalize`, proof = `reference:ECR` / `synthetic` / `legacy-water`, hashes).
* **Gates relaxed only on this path, and only for NON-effective versions**: (i) PSS-23 golden set — superseded
  versions often have an empty golden set (55 of 93 test cases empty); the proof is the reference parity below
  instead; (ii) environment compile against the CURRENT template (C12_03 V1 uses an argument the template removed) —
  the script is compiled syntactically and against its own materialized outputs, not against today's template; (iii)
  `scriptSwitchDeprecated` (human 20.11, PSS-23 §6 p.9) — see question Q3. Effective versions keep every normal gate.
* **Provenance**: before the later removal lane deletes `MethodologyFormula/Constant/Rule/CategoryRule/...` rows, a
  one-time job writes each version's formula content as JSON (`formulas`, `constants`, `rules`, `categoryRules`,
  `requiredInputs`, source fingerprint = the converter's) into `aud` (new table `aud.MethodologyFormulaProvenance`
  (VersionId, ContentHash, SourceJson, SourceSha256, ArchivedAt) or `aud.PublicationEvent.ResultDiffJson` if size
  allows; audit filegroup, never shown in UI). Needs an EF migration → must be based AFTER
  `20261124152432` (calc-issue-orphans holds the token).
* Drafts: `SwitchEngine` (draft path already exists). Common V1 → convert as library (converter skipped it only for
  "draft without EffectiveFrom"; D-556 already handled library-without-golden-set). C05_01 "test" → see Q4.
* C11_15 V1 (no rules → old engine computes nothing): script with an explicit empty selection; proof = 0 rows both sides.

## c) Parity gate — results on the copy

Gate per version: Formulas vs its Script text on the same inputs, exact (`ParityDiff`, no tolerance — the D-571 tooling
is exact), ≥ 1 value compared, 0 mismatches, hash of the compared text = hash stored on the row.

### c1. Reference rows of the old system (ECR) and synthetic branch rows, mapped onto the OLD version ids
Cases = the W15z `ref-cases.json` / `syn-cases.json` (legacy ECR rows, all methodologies), replayed against EVERY
superseded version of the same methodology on the after-W15z copy (target environment: Land 1.0.20.0, NAE import).
Files: `F:\bld\p11242\{ref,syn}-cases-old-versions.json`, `{ref,syn}-parity-old-versions.json`,
`parity-old-versions-by-version.csv`.

| | result |
|---|---|
| versions checked | 73 (all with shadow text) |
| rows / values compared | 1 332 / 17 305 |
| mismatches | **0** |
| proven (≥ 1 value, 0 mismatches) | **71** |
| not proven | 2: ECW_C07_07 V1 (id 8), V2 (id 50) — the reference rows give no values for C07_07 (W15z proved V3 by its golden set only). Blocker B-1: needs C07_07 input rows (synthetic branch cases like other C07/C11) |

### c2. Stored CalculationResult values vs recomputation — NOT reproducible by EITHER engine (finding F-1)
`p11242-evidence history` (`F:\bld\p11242\history-parity-*.csv`, before-copy `F:\bld\p11242\before\`): for every
(version, document, period, table instance) that holds stored results, old engine and script were run on the same inputs.
Old vs script: 0 mismatches. But both select **0 rows** on all 39 (after) / 59 (before W15z) targets: the rows are
excluded by the D-425 activity filter / rules that require `Land_Status = Running`, and the result rows (e.g. T9a_28
`LTD-A36`) have no status. The stored numbers (run 10106, 13.11) were produced under an earlier configuration —
`Methodology.RowActivityFilter` is a container property outside `ContentHash` and changes without a new version
(Methodology.cs), and rule/borrowed-rule/template changes followed. So "stored = recomputed" cannot be a gate for these
296 values with any engine; the honest gate is c1 (version semantics on reference inputs) + c3 (identity of text).
These 296 results stay as they are (frozen numbers, valid link). See Q5.

### c3. Text identity
All 73 shadow texts on the copy are byte-identical (SHA-256) to the texts W15z proved on live (`after-w15z-version-hashes.csv`,
packages `.sync-local\w15z-ms-*\package.json`): 118 hashes matched, 0 missing.

### c4. Water ECW_C09_02_01 — required item, PROVEN
Root cause of "units problem" (P-1123-4 history: ec4a705b5, cb5b87edd, 8f160fc2e; D-571 (5)):
1. The exclusion was NOT a unit error of the methodology. D-571 excluded water because on Land 1.0.19.0 `T7b_00` had no
   `Concentration`/`Volume` inputs (`ECR-CALC-0438`); Concentration lives in month columns January..December of the
   substance rows, Volume is row `S3_9` of the same month — a Configuration methodology could not bind them. Land
   1.0.20.0 now has month-input aliases `Concentration`(January..December), `Volume`(Volume_M01..12 = formula
   `[S3_9].[<month>]`), `ECW_EC_tons`(_M01..12).
2. The units problem is in the TEMPLATE formula dialect: `UnitChecker` never derives a product unit
   (`expr.unit.productUndeclared` for mg/l × m3); the table formula therefore strips the unit with `IF(...,[Concentration],NULL)`
   and relies on /1e6 to land in t. In methodologies/script `productUndeclared` is NOT blocking and row fields read
   through month aliases have unknown unit (FP-4), so no unit is lost and none is invented.
3. Unit exactness: 1 mg/l × 1 m3 = 1 mg × 1000 = 1 g = 1e-6 t → `t = C · V / 1e6` (k1 = 1e6, unit `one`) is exact;
   template column units are mg_per_l / m3 / t (verified on the copy). Phase 2 adds a guard test: if the template units
   of Concentration/Volume/ECW_EC_tons change, k1 must change (test fails).

Candidate script (`F:\bld\p11242\water\B-running-filter.ecrs`):
```
rows from T7b_00
    active when row.Land_Status == entry("LAND_GENERIC_EQUIPSTATUS", "RUNNING");
const k1_ECW_C09_02_01_ : one = 1000000;
output ECW_EC_tons : t = row.Concentration * row.Volume / k1_ECW_C09_02_01_;
```
Evidence:
* **Legacy system parity** (ECR `CalcResult_Emissions_Water_Land`, 45 rows; inputs `AF_Water_Land_07a_16` + volume):
  25 non-zero legacy emissions = C·V/1e6 exactly; Formulas (version 46) vs this Script on the 25 legacy input rows:
  **25 values, 0 mismatches**, and ECR value = legacy value for **25/25** (`water-legacy-parity.json`,
  `water-ecr-vs-legacy.csv`). The other 20 legacy rows are 0 with no volume (ECR gives "no value", not 0 —
  accepted D-425 semantics).
* **Live template preview** on the copy (synthetic inputs in doc 96, 202604/202605; `water-B-running-filter-values.csv`):
  0.001296 / 0.00003888 (= legacy April values), REMOVED row inactive, S3_9 row inactive, missing concentration → `#ARG`
  (methodology semantics MissingInputs; the table formula shows empty). The converted text without `active when`
  (`A-converted-V1`) ignored D-425 — the converter added no `active when` because the 1.0.19 table had no status column.
* Finding F-2 (data, not conversion): for the Bautino file the legacy volume is row 7009012 (Land_No 77), while the
  template S3_9 = 7002002 + 7002004 (B26, P-0118-24). Same formula, different volume row → Q2.

T7b_00 `ECW_EC_tons_Mnn` (now table formulas in 1.0.20.0, 0 results on copy): return them to the methodology?
* Return (recommended, default): template 1.0.21.0 makes them `Calculated` again with bindings to C09_02_01 V2 (Script,
  text above, golden set = the 25 legacy rows). One source of truth in the script editor (human: "only our new script
  editor"), D-425 + MissingInputs reporting, issues in calc review, legacy-proven numbers. Cost: a Land template
  version + document migration of T7b_00 (only calculated cells there; they are recomputed by the methodology, nothing entered by
  users is lost — verify on the fresh backup), a window step.
* Stay: zero template work now, but water stays a second, template-only calculation path (two chairs), no MissingInputs,
  the methodology is unbound and would still have to be converted (no-op) — contradicts the 24.11 decision.

## d) What becomes dead and removal order (separate lane after the live conversion)

After live has 0 `Engine = Formulas` rows (checked by an Architecture/DB guard):
1. API/UI (lane `integ-ms-only-editor` already rejects NEW Formulas versions): remove Formulas editor, formula/constant/
   rule/category-rule draft endpoints, `MethodologyEngine` selector, rollback `switch --engine Formulas`.
2. Engine: `GenericCalculationModule` (79 files reference it), Formulas branch of `MonthColumnArgumentsModule`,
   `MethodologyEvaluationContext` formula path, `formulaLinks.RebuildAsync`, `ParityComparer/ParityRunner` old side,
   `tools/Ecr.MethodologyScriptMigration` convert/shadow/parity (keep only `finalize` until done), `ScriptProgramLoader`
   `allowShadowLibraries`.
3. Data: after provenance (b) is written and verified (row counts + SHA): drop rows of `calc.MethodologyFormula`,
   `MethodologyConstant`, `MethodologyRule`, `CategoryRule`, `MethodologyCategory`, `MethodologyRequiredInput` that only
   the old engine reads (check each against the script materializer first — Output/Substance/Import/ReadTable/ReadField
   are written by the script path and stay), then the tables, then the enum value `Formulas` (keep value 0 reserved).
4. Docs: D-571 (5) water exclusion, `$MsPermanentExclusions`, HANDOVER-MS-1120 §3.

## Implementation size (phase 2, one integ lane `lane/integ-p11242-ms-lossless`)

| part | estimate |
|---|---|
| `finalize` handler + endpoint + tool command, guards (non-effective only, hash, one-time), audit | ~450 lines + ~400 test lines |
| provenance table + EF migration (after 20261124152432) + writer job | ~250 + 200 tests |
| C11_15 V1 empty-selection text, C12_03 V1 compile-without-template, Common/test drafts | ~150 + 150 tests |
| C07_07 V1/V2 synthetic reference cases (B-1) | ~50 + data |
| water: C09_02_01 V2 (Script, golden 25 legacy rows), model-gen 1.0.21.0 T7b_00 back to Calculated + bindings, remove `$MsPermanentExclusions` + MsPhaseLibTests, unit guard test | ~300 + template build + window step |
| window phase `Finalize` in `Invoke-MsMigrationPhase.ps1` (fresh backup → ref/syn parity on old ids → finalize → verify 0 Formulas) | ~150 |
| removal lane (d) | later, large (est. 3–5 k lines deleted, staged) |

## Blockers
* B-1 ECW_C07_07 V1/V2: no value compared on reference rows → need synthetic branch cases (like C07_16/C11_1x).
* B-2 Gate relaxations on the one-time path need a human word (Q3) because they reverse PSS-23 §6 p.9.
* B-3 Re-run on a fresh live backup (copy is 3.5 h older than live; 61 snapshots not present on the copy).

## Questions for the human (one word, default applies until answered)
* Q1 Water T7b_00 `ECW_EC_tons_Mnn`: «методологія» (back to Calculated, template 1.0.21.0) or «формула»? Default: методологія.
* Q2 Water volume source: «S3_9» (B26 as is) or «легасі» (per-facility discharge row, e.g. Bautino 7009012)? Default: S3_9, discrepancy logged.
* Q3 Superseded/deprecated versions switch to Script with reference-parity proof (overrides PSS-23 §6 p.9 «deprecated never to Script»)? «так»/«ні». Default: так.
* Q4 Draft ECW_C05_01 "test" (id 58): «конвертувати» or «видалити»? Default: конвертувати (no loss).
* Q5 296 stored results of doc 96 (not reproducible by either engine after D-425/rule changes): «лишити» (frozen, link valid) or «перерахувати»? Default: лишити.

## Phase 2 (24-25.11) — lane `lane/integ-p11242-ms-lossless`, base cb5b2ffcf (calc-issue-orphans), commits 1c07b4f20, fcac609a0

Defaults recorded by coordinator: Q1 методологія, Q2 S3_9, Q3 так, Q4 конвертувати, Q5 лишити.

* **F-1 resolved — it was a harness defect (F-3), not data.** `ParityHost` (tool, also used at W15z) never filled
  `ITemplateLayoutRegistry` (the API fills it with a hosted service), so year-layout (Land 1.0.19+) reads were PHYSICAL period slices,
  not Q-17 logical ones: status/codes stored in the anchor period 202601 were invisible. Fixed in fcac609a0. With routing:
  * stored results of superseded Formulas versions (348 on copy): old engine = stored 348/348, finalize text = stored 348/348;
  * current Script versions reproduce ALL 510 Current-run results of doc 96 (0 mismatches) — W15z dropped nothing;
  * F-2/C08_04_02: the W15z shadow of superseded V1 was truncated at V2's date (constants k9/k12/k14 with validity periods → only
    the pre-2024-01-02 values), while V1 computed 202609 when it was current (k9 = 1 from 2026) → script 0.4× stored. Finalize uses
    `convert --full-life` texts; proof includes stored-result reproduction → 0 mismatches.
* **Coordinator ask (live doc 96, runs 10179/10180 after switch: 35 MissingInputs, 7 StepError, 3 CategoryRuleRejected, 35 results):**
  before-W15z copy (1.0.19, Formulas, runs 10148/10147): 35/29 results, 38 MissingInputs, 7 StepError; after-W15z pre-switch
  (1.0.20, Formulas, 10153/10154): 35/29, 35, 7; harness on the same doc×period, before-switch versions on formulas vs current
  Script versions: values 446/35/29 identical, MissingInputs 35 = 35, StepError equal, +3 rejected rows only for ECW_C11_10 V4
  (W15z GoldenVersions «borrowed rules» select 3 more rows that its category rule rejects — values unchanged). The switch is not
  a regression. MissingInputs are genuine data gaps: doc 96 has 1 input cell in 202610 and 0 in 202611 (e.g. Running hours only in
  202609). Separate defect card: the UI text «this table has no such column» is shown when the issue's column hint has no name —
  the hint does not resolve month-input aliases (Land_RunningHoursMonthly → *_Mnn) of year layouts (`unmatchedRowsModel.ts`).
* **Rehearsal on copy of live (after-W15z backup, E:\EcrData\EcrP11242Scratch):** WaterV2 (C09_02_01 V2 Script, id 128, golden =
  25 legacy rows, published) → FinalizeProve: 78 proven, 0 blocked (reference 53, synthetic 17, legacy 6, stored 1, unusedLibrary 1;
  21 294 values, 0 mismatches; history 348/348) → Finalize: Formulas versions 78 → 0, 78 audit events with formula provenance,
  CalculationResult 4123 all on Script, dangling result/snapshot links 0; repeat finalize → 409 finalizeNothingLeft.
* B-1 closed: C07_07 V1/V2 synthetic cases (every tank-farm entry × days/winter/quantity) 864 values, 0 mismatches; C12_03 V1
  (argument removed from template) synthetic + CompositionMass 150 values, 0 mismatches; C11_15 V1 hand text (no row rules).
* No EF migration (provenance in immutable `aud.PublicationEvent`, as B179 L7) → migration-token order unaffected; idempotent
  migration script on the copy run twice: 0 changes. rpt.* untouched.
* NOT done: Land template 1.0.21.0 (T7b_00 ECW_EC_tons_Mnn back to Calculated + bindings) — water V2 publishes and is bound on
  templates ≤ 1.0.19; on 1.0.20 documents T7b_00 is still computed by the table formula (same semantics, preview = legacy values).

## Land 1.0.21.0 — water back to one calculation path (P-1124-2 follow-up, 25.11)

* Model: `gen_ecr_model.py` `DECIDED_TABLE_FORMULAS = {}`; T7b_00 `ECW_EC_tons` (12 month columns) Calculated, output of
  ECW_C09_02_01 (aca0f3d31). Builder (d9aa7f4b2): D-474 reverse step Formula -> Calculated in the draft clone (only columns with no
  stored values; table formula soft-deleted, draft only); LP-08 reads output codes also from `/outputs` (Script versions have no
  formulas). Planner (38ce8ac93): Formula -> Calculated = "recomputed", not guardedWithData.
* Water V2 text unchanged from 976c7a502 (`active when Running`). Tried `where Running` + `active when Running || null` to hide
  S3_9: that turns S3_9 into NoMatchingRule (a real problem diagnostic), so rejected. With V2, S3_9 (volume row, no status) is
  a D-425 excluded row -> one RowInactive summary per period (B66), as V1 gave on templates <= 1.0.19. The window compare lists
  these as `notices`, outside the "issues must not grow" gate.
* Window step: `window/Invoke-LandWater1021.ps1` (81cd593bf): account | snapshot | builder | verifytpl | dryrun | apply | recalc |
  compare.
* Rehearsal (fresh after-W15z copy on G:, 25.11 01:38-02:04), WaterV2 -> FinalizeProve -> Finalize -> builder -> apply 1.0.20 ->
  1.0.21 -> recalc: formula versions 78 -> 0; 78 proven, blocked 0; template gate 12/12 Calculated, 0 live formulas, 12/12
  bindings, 24/24 aliases; dryrun canApply, lost 0, transferred 21 984; water per cell 4/4, 0 differences (0.001296,
  0.00003888, 0.003864, 0.000252); inputs 254 -> 254, lost 0; issues doc 96 CategoryRuleRejected 6 / MissingInputs 73 /
  StepError 14 before and after; results 96: 510 -> 514 (the 4 water results), 97: 38, 98: 3575; dangling 0; notices
  T7b_00 RowInactive S3_9 x2 (202610, 202611).
* c11-10 substance-registry check (coordinator 25.11): finalize/convert do not materialize substances (DiffAgainstStored only
  compares codes; SwitchEngineForMigration writes nothing else); water V2 has no substances. calc.MethodologySubstance
  (version, ordinal, entry id, code, registry) before MS vs after Finalize vs after the whole window: 555 rows, 0 changes, all
  LAND_AIR_SUBSTANCE — on the lane as is AND with 1ab7bf9e3 cherry-picked locally (both rehearsals PASS).