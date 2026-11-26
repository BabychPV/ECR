# DESIGN — Gas composition owned by ECR (b12)

Author: dev agent, 24.11. Lane: `lane/integ-gas-composition` (from `origin/main` ecdd84621).
Requirement (human 24.11): UI to add / change / delete FLERT gas-composition data inside ECR, sidebar item,
gas versions (take FLERT logic if good, better if justified). Quiz 24.11: owner = ECR DB, FLERT read once
(never written, never dropped), ship in b12. Supersedes 11.11 (FLERT master, daily mirror) for gas composition.

## 1. FLERT as found (read-only, local copy `FLERT`, 24.11)

| table | rows | shape |
|---|---|---|
| `FlaringStream` | 523 | ID (manual, `MAX+1`), streamNumber, Name, isOnshore, isSour, streamHMB, excludeInListBox, StreamGroup, isHMB, ValidFrom, ValidTo; `UQ(Name, isOnshore)` |
| `FlaringStreamType` | 982 | ID ident, StreamID→stream, StreamType (free text = **design case**: `370 Summer`, `ПК-3 (370-220) зима`, `SOR/EOR Rev D04`, `No composition` …), RuleParse |
| `FlaringStreamGasComposition` | 982 | ID ident, StreamTypeID→type, 52 × `decimal(20,16)` (Temperature, 45 components incl. 3 alias pairs CO/Carbon_Monoxide, H2/Hydrogen, O2/Oxygen, Density, Molecular_Weight, Z) |
| `*_History` ×3 | 0 | same + `OriginalID`, `VersionCreatedBy` |
| `FuelGasType` | 6 | fuel-gas dictionary (HSE400 side, not composition) |

Data: 1 composition per (stream, case) (0 dup, 0 orphans); every `ValidFrom` = 2022-01-01, every `ValidTo` NULL,
history empty → **no version was ever created**. 0 NULL components; 834 Density = 0, 244 MW = 0; Σ mol %: 948 in
99.5–100.5, 11 in 90–99.5, 7 < 90, 16 = 0 (exactly the `No composition` cases). Alias pairs filled together: CO 8
(2 differ, 1e-6 and 3e-3), H2 8 (0 differ), O2 6 (0 differ).

### 1.1 FLERT versioning logic and verdict

`CreateOrUpdateGasComposition` (the only writer):
- `@StreamID` given → **in-place correction** of stream, type and composition, no history.
- `@StreamID` NULL → find stream by (Name, isOnshore); copy current row to `_History` with `ValidTo = GETDATE()`,
  update the current row with `ValidFrom = GETDATE()` — for all three tables, each **independently**.
- Readers (`usp_GetAllStreamDataFlattened`) UNION current + history and join the three tables on **equal
  `ValidFrom`**.

Verdict — not taken as is:
1. It is *transaction time* (when someone pressed Save), not *effective date*: a composition measured for
   March but entered on 15 April cannot be dated March; back-dating is impossible.
2. The equal-`ValidFrom` join breaks as soon as one table versions without the others (stream rename → the
   type/composition rows no longer join → NULL composition; legacy calcs then NRE, see FLERT-plan-1111 §2).
3. In-place correction (`@StreamID`) silently rewrites history that already fed approved reports.
4. No validation at all (Σ, ranges, duplicates).
What is worth keeping: the grain *stream → case → one composition per validity window*, history never
physically deleted, stream Id = integer the PI/AF event refers to (`GasCompositionID`).

## 2. ECR model (better, and already the calc-facing shape)

ECR already has the target shape (D-476, FEATURE-REGISTRY-TABLES §11.4, golden-tested in `Hse301GoldenTests`):
registries `STREAM` → `STREAM_CASE` (temporal, PK `STREAM + CASE_NAME`, key unique **per validity window**) →
`GAS_COMPOSITION` (composition child, PK `CASE + COMPONENT`, `MOL_PCT`) + `COMPONENT` (MW, nC, nS …).
Formulas read it with `REGSUM('GAS_COMPOSITION', ROW.CASE = !CASE, …)`; the resolver picks the case version
whose window overlaps the calculation period. **Decision: keep exactly this storage (no new tables, no EF
migration)**, change the owner and put a dedicated domain service + page on top:

| concern | rule |
|---|---|
| owner | registries become `SourceKind = Local` (ECR master). Generic registry editors (upsert, batch, CSV, delete, validity) **refuse** these three registries — `err.ECR-REG-0409.managedByGasComposition` ("edit on the Gas composition page") — so every write goes through the domain rules below. `COMPONENT` stays a normal Local registry. |
| stream | Id (`LEGACY_ID`) = integer referenced by PI events; new stream → `MAX+1` (FLERT rule). Name + onshore unique (FLERT UQ). Attributes are not versioned (FLERT also corrected them in place; they do not enter formulas except via lookups by Id). |
| case | (stream, case name); a case is a timeline of versions. Rename = all versions, allowed only when nothing locked. |
| version | `STREAM_CASE` entry with window `[ValidFrom, ValidTo)`; **effective dates, month granularity** (ValidFrom = 1st of a month — periods are months/years, a mid-month switch would make a month ambiguous). Timeline per case is contiguous, no overlap (also enforced by the temporal key): new version from F truncates the version containing F and runs to the next version's start (or open); delete extends the previous version over the gap. |
| components | 46 codes (FLERT columns, aliases folded to the short code — human 11.11 Q4); 0 ≤ x ≤ 100; zero = no row (= legacy NULL/0 → 0); removed component → row deactivated (re-add reactivates, history kept). |
| Σ check | FLERT had none. ECR: Σ outside 100 ± 0.5 (same tolerance as rule `SUM_100`) needs explicit confirmation (`acceptSumDeviation`), shown live in the editor; Σ = 0 allowed only confirmed and stored as `NO_COMPOSITION` (calc → MissingInputs, never zero emissions). Legacy rows with Σ ≠ 100 stay editable. |
| properties | T °C (> −273.15), ρ op, MW HYSYS, Z (≥ 0, empty = unknown; FLERT 0 → empty). |
| immutability | an operation is refused (409 `err.ECR-REG-0409.gasCompositionLocked`, lists the periods) if it changes the effective composition on any month that a **locked period** used: current calculation result of a methodology version that reads one of the four registries (`cfg.RegistryUse`) whose period is `Closed` or whose sheet is `Approved`. Correction of such history = new version from the first open month. |
| delete | version: allowed if not locked. Stream: soft delete of stream + its versions + rows, refused if locked or if anything outside references the stream entry. Nothing is hard-deleted; SQL temporal tables keep every old value (`RegistryAsOfUtc` re-runs). |
| audit | writer emits `RegistryValueChanged` per entry (old/new per field) + one `GasCompositionChanged` security event per user operation (op, stream, case, window before/after, Σ) — both visible in Audit trail → Security events. |
| recalculation | after each change: one coalesced `IRegistryImpactRecalculationJob` per changed registry × year that has open/grace dependents (same machinery as HSE400 3b / FLERT F6); approved sheets are skipped by that job (human 07.11/16.11 rule) and only flagged stale by `DataChangedAt`. |

Rights (seed, no migration): `GasComposition.View`, `GasComposition.Edit` (not dangerous).
View → SystemAdministrator (`%`), TemplateAdministrator, Approver, DataEntry, Viewer, Auditor (everyone with
`Registry.View`). Edit → SystemAdministrator only (precedent `Calculation.EditConstant`); others get it by grant.
Import from FLERT → `GasComposition.Edit` + `Integration.Manage` (reads an external DB).

## 3. Migration of FLERT data (one-shot, lossless, idempotent)

- Engine = existing D-476 path (`SqlDataSource` read-only adapter → `FlertCompositionMapping` → `RegistryEntryWriter`
  in ONE transaction). Re-purposed as **one-shot import**: allowed only while the ECR store has no live stream
  (`409 err.ECR-REG-0409.gasCompositionAlreadyImported` otherwise) → can never overwrite ECR edits; re-running
  it after success is a no-op refusal (idempotent).
- Registries created `Local`; an existing `External` mirror (test stands) is switched to `Local` with an
  `aud.StructureChange` row.
- Lossless: every FLERT stream / type / composition Id kept (`LEGACY_ID`, `LEGACY_TYPE_ID`,
  `LEGACY_COMPOSITION_ID`), every window, every non-zero value at full `decimal(20,16)` precision. Only folded:
  long alias columns whose value differs from the short one (2 rows, CO) → kept verbatim in the version field
  `FLERT_ALIASES` (text) so nothing is lost. Zero ≡ no row (identical for every consumer: legacy maps NULL→0).
- Proof (scratch copy on G:, never live): counts + checksums per table — streams 523, cases 982, versions 982,
  component values = count of non-zero FLERT cells; `CHECKSUM_AGG` over (stream id, case, from, component,
  value) on both sides equal.
- Daily FLERT sync retired: recurring job removed, `Flert:Sync:*` keys removed, "Sync now" panel replaced by
  the import panel on the new page (visible only while not imported). FLERT is never written (adapter rolls back,
  arch guard `FlertReadOnlyAdapterTests` unchanged).

## 4. Calculations after the switch

Unchanged: they read the same registries with the same codes/fields. Live (EcrLandUi, 24.11, read-only):
mirror registries absent (sync disabled since go-live, every run `reason: disabled`), `cfg.RegistryUse` for the
four registries = 0, the 357 `GCV_*` formulas belong to `Common`/`HSE400` and read HSE400 (fuel gas), not FLERT.
So no live document/period consumes FLERT composition today → parity is "same registry content before/after"
(proved by checksum on the scratch copy: old sync path vs new import path) and the golden HSE301 tests stay green.

### 4.1 Who consumes the composition (R3 condition 1)

Legacy (copy `ECR` on localhost, SELECT only, 25.11; C# references from FLERT-plan-1111 §2):

| Legacy consumer | What it reads | ECR methodology on live today | Reads ECR composition? |
|---|---|---|---|
| HSE30X **Intermittent SG** (`R_Air_HSE30X_Int_SG_Calculate`, SQL `AF_Air_Flaring_Intermittent_SG_Sync`, `PrepareData_GetFiltered_Flaring_Intermittent_SG`) | `Flert_GetAllStreamDataFlattened` by event StreamID + StreamType, valid at period | none (live has `ECW_C05…C12`, `HSE400`, `Common`; no flaring-SG methodology) | **no** |
| HSE300 Intermittent SG (`TypeReportID=300`) | same | none | **no** |
| HSE30X **Continuous SG** (onshore stream 416 `SOR`; offshore `LP_Sour_Analyzers_StreamIDs` + season) — `PrepareData_GetFiltered_Flaring_Continuous` | same | none | **no** |
| **Thermal oxidizer** SG (`R_Air_HSE_Thermaloxidizer_*`, reports `Report_Air_Flarings_Thermaloxidizer_*`) | StreamNumber + `SOR/EOR`, incl. SO2/Methanol columns | none | **no** |
| Intermittent/Continuous **FG**, TOx FG (`AF_Air_Flaring_Intermittent_FG_Sync`) | **HSE400** fuel gas, not FLERT | `HSE400` + `Common` (357 `GCV_*` formulas) | n/a — HSE400 document, unchanged |
| Reports `Report_Air_Flarings_*`, `Report_Air_A1/B1/B4_230_*`, Land reports `Report_Air_Land_*` (via `Flert_*` synonyms for stream names/flags) | stream attributes / composition for SSRS | SSRS not in ECR scope (D-52) | n/a |

Live facts: `cfg.RegistryUse` for STREAM/STREAM_CASE/GAS_COMPOSITION/COMPONENT = 0; FLERT mirror never populated
(daily sync ran `reason=disabled` every night). **Gap (report, not fixed tonight):** the SG-flaring and thermal-oxidizer
methodologies that consume composition in the legacy system are not yet configured in ECR (they were planned as FLERT
F4/F5: event→case link + `GCV_<X>` bridge or `REGSUM` formulas — FLERT-plan-1111 §8). Proposal: after b12, a lane
"HSE30X SG on ECR composition": (1) import the `Flert`/HSE30X SG methodologies (Script, D-NEW methodology rule 24.11),
(2) bridge `GCV_<X>` ← `GAS_COMPOSITION` of the resolved case (same generator as `hse400_bridges.py`), (3) event→case
resolution by `GasCompositionID`=Stream Id + StreamType, (4) parity vs legacy `CalcResult_Flarings*_SG` for 1–2 months.
Until then the page is the system of record and the data is ready (same shape the golden HSE301 tests read).

### 4.2 Live import evidence (scratch copy of live on G:, 25.11)

`EcrGasScratch` = copy-only backup of `EcrLandUi` (24.11 22:xx) restored on G:\EcrData\gasscratch; app from the lane,
FLERT data source → local `FLERT` (read-only). `POST /api/v1/gas-composition/import` → job `Succeeded`, 982 rows read,
added 523 streams / 982 versions / 22 725 component rows, 155 s; second import → `alreadyImported` (FLERT not read).
`tools/gas-composition/Import-GasCompositionFromFlert.ps1 -VerifyOnly`: streams 523=523, versions 982=982, compositions
982=982, non-zero components 22 725=22 725, Σ 97776.0280881837443339 = same, CHECKSUM_AGG −1484951059 = same.
Row-level SQL parity (scratchpad `parity.sql`): stream attributes 0 mismatches, versions (stream, case, window, FLERT ids,
T/ρ/MW/Z, RuleParse) 0, component values 0; FLERT_ALIASES kept for type 1538/1539 (CO vs Carbon_Monoxide).
Real-data defect found and fixed: FLERT flags are `smallint` → mapping rejected every row (`unreadableValue`).

### 4.3 Window stage (R3 condition 2)

`tools/gas-composition/Import-GasCompositionFromFlert.ps1 -EcrServer … -EcrDatabase EcrLandUi -FlertServer …`
(dry run: FLERT counts + Σ + checksum, refuses with exit 2 if ECR already has streams) → `-Apply -BaseUrl … -Credential …`
(POST import, waits for the run, compares; exit 3 on any mismatch) → `-VerifyOnly` any time later.
Prerequisites on live: `ext.DataSource` FLERT (Transport Sql, read-only login) + `ECR_Secrets__Sql__FLERT__CompositionQuery`.

## 5. UI — Configure → "Gas composition" (`/admin/gas-composition`)

- Sidebar item in CONFIGURE, permission `GasComposition.View`, icon `flask`.
- List: search (name / number / Id), filters onshore/offshore, sour, hidden; columns Id, number, name, location,
  cases, current version date, Σ status badge. "New stream" (Edit).
- Stream detail (drawer/page): attributes form; cases as tabs/sections, each with a **version timeline**
  (from → to, current badge, locked badge with reason); version editor: effective month picker, T/ρ/MW/Z,
  components table (46 rows grouped: inerts, C1–C14, aromatics, sulphur, other) with mol % inputs, live
  Σ indicator (green within 100 ± 0.5), "copy from previous version" default, compare-with-previous view
  (changed rows highlighted). Delete with confirm dialog naming what extends. Read-only mode without Edit.
- Keyboard: all actions buttons/links, focus management in dialogs; dark/light tokens; 375 px stack layout.

## 6. D-NEW (text for `docs/tz/10-decisions.md`)

D-NEW — Gas composition (FLERT streams/cases/compositions) is owned by ECR from b12 (human 24.11, supersedes
D-476 master = FLERT). Data lives in registries STREAM / STREAM_CASE / GAS_COMPOSITION (Local), edited only on
the Gas composition page under domain rules (effective-month versions without overlap, Σ = 100 ± 0.5 or
confirmed, immutable for months used by closed/approved periods, soft delete). FLERT is read once by a
one-shot import (refused when ECR already has data); the daily sync is retired; FLERT is never written.

## 7. Questions (default applies until answered)

1. Grant `GasComposition.Edit` to TemplateAdministrator too? Default **no** (SystemAdministrator only).
2. Version effective date — month granularity? Default **yes** (1st of month).
