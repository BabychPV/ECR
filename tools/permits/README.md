# tools/permits: loading the Excel permit books into Permits

`Load-Permits.ps1` reads the three source books (`docs/source/Permits`: `PermitsInfo.Year.xlsx` Air, `PermitsInfoWater.Year.xlsx`
Water, `PermitsInfoWaste.Year.xlsx` Waste) and loads them through the public REST API of a **local** stand:
permit types (create, replace, publish) -> permits (Draft) -> rows (`entries/batch`) -> activation.
Windows PowerShell 5.1 and PowerShell 7; no modules, no Python. `Permits.Excel.Lib.ps1` is the xlsx reader and the pure mapping.

## Safety

* **Dry run is the default.** Nothing is written. With `-BaseUrl` and credentials the stand is only READ; without them the plan
  comes from the workbooks alone.
* `-Execute` needs `-BaseUrl` (loopback only), `-ConfirmStand <host:port or URL>`, `-Database <name>` (the confirmation must be
  `<host:port>/<database>`, as in `tools/land/EcrApi.ps1`) and `-User/-Password` (or `ECR_USER` / `ECR_PASSWORD`).
* Logs carry **counters only** (no cell values). Permit numbers are shown only with `-ShowNumbers`. The run file
  (`-LogDir`, default `%TEMP%\ecr-permits-load\permits-run-<runId>.json`) keeps numbers and ids, never row values.
* Per permit it is all-or-nothing: create Draft -> dry-run **all** 500-row chunks -> write chunks -> compare the row count with the
  server -> activate. Any failure deletes the Draft (rows go with it).
* Idempotency key `(type, number, ValidFrom)`: an existing Active/Expired permit is skipped (row count compared); an existing Draft
  with rows is reported as PARTIAL and never merged.
* Exit codes: 0 ok, 1 failure / invalid data, 2 warnings only, 3 guard error.

## Usage

```powershell
# 1. plan from the books only
.\Load-Permits.ps1
# 2. read the stand, predict create/skip (no writes)
.\Load-Permits.ps1 -BaseUrl http://localhost:5301 -User admin -Password (Read-Host p -AsSecureString)
# 3. write
.\Load-Permits.ps1 -BaseUrl http://localhost:5301 -ConfirmStand localhost:5301/EcrLandUi_PermitsTry -Database EcrLandUi_PermitsTry -Execute `
    -User admin -Password $pw -ServiceEntriesFile .\service-entries.csv -ServiceValidFrom 1900-01-01 -ServiceValidTo 2100-01-01
# options: -Only AIR,WASTE   -NoActivate (leave Draft)   -ShowNumbers   -ChunkSize 500   -RunId x
#          -ResumeEmptyDrafts (load rows into an existing EMPTY Draft of the same key and activate it; a Draft WITH rows stays PARTIAL, never merged)
# undo of a run (only Draft permits and never-published types it created)
.\Load-Permits.ps1 -BaseUrl ... -ConfirmStand ... -Database ... -User ... -Password $pw -Rollback <runId> -Execute
```

`-SourceDir` defaults to `docs/source/Permits` of this checkout or of the main checkout (`docs/source` is not in git).

## Undo

The API deletes only **Draft** permits (`DELETE /permits/{id}`) and never-published types. An Active permit or a published type
cannot be deleted through the API (`ECR-PRM-4096` / `ECR-PRM-4094`). The only full undo of an Execute run is a **database restore**
(take a backup first; try on a copy: restore the `.bak` under another name and start a separate API stand on it).
`-Rollback <runId>` is for runs made with `-NoActivate` or interrupted ones.

## Customer decisions and defaults (plan section 5; every choice is a parameter)

| # | Question | Default / human answer 21.10 | Parameter |
|---|---|---|---|
| 1 | Number normalisation | numbers exactly as in Excel (dash kept); all permits are loaded, **including** the 3 Air service sheets (`N/A`, `No Permit - ...`) without rows | `-NormalizeDashUnderscore`, `-ExcludeNumberPatterns` |
| 1b | Land-dictionary-only entries (`permit_001..005`, `AF_<guid>`) | loaded as AIR permits without rows, Location = `service: Land dictionary only` | `-ServiceEntriesFile` (CSV `number;location;validFrom;validTo`, explicit path, file kept out of git), `-ServiceLocation`, `-ServiceValidFrom/-ServiceValidTo` |
| 2 | Land header `Permit` | Air only; Water goes to `T7_00.PermitNumber` (separate types) | not a loader option |
| 3 | Water/Waste types | 3 water types (Discharge / Intake / General) + Waste; key = Event frame / waste code + place | `Get-PermitTypeConfigs` in the lib |
| 5 | General water dates, Waste dates | General = dates of the Air permit with the same number (none -> skipped `no-dates`); Waste = from the R1 text | `-GeneralDatesFrom Air\|None` |
| 6 | Dirty data | `qwwe` / `Test_Water` rows dropped and counted; zeros (`mg/nm3 = 0`) kept as values | `-ExcludeTestRows`, `-TestMarkers` |
| 7 | Header totals | not stored | - |
| 10 | Units | `unitId = null` (the unit is in the column header) | - |

Dates: the workbook `End Date` is inclusive, `ValidTo = End + 1 day` (`-ValidToAddDays`); `Actual End Date` is stored (also +1 day)
only when it differs from `End Date` (`-ActualEndMode IfDiffers|None`). Service entries get **no invented dates**: an entry without
dates (in the file or in `-ServiceValidFrom/-ServiceValidTo`) is skipped as `no-dates`.

Data notes: one Air permit has no site at all in its rows (the key column `Site_En` gets the placeholder `-`, counted); the key
`Substance_Code + Site_En` is up to 210 characters long; the Waste `Platform` is up to 291.

## Tests

```powershell
powershell -NoProfile -File tools\permits\Tests\Run-Tests.ps1   # exit code = number of failed asserts
```

Currently 72 asserts. Plain asserts on generated fixture workbooks (no stand, no real data): dates (End+1), decimal/number mapping, test-row exclusion,
duplicate keys, Waste text dates, service entries, chunking, batch payload.

## Service entries file (not in git)

The Land dictionary `LAND_PERMIT` holds codes that occur in no Excel book (`permit_NNN`, `AF_<guid>`). They are loaded as AIR permits
without rows, Location `service: Land dictionary only`, dates `1900-01-01 .. 9999-12-31` (human decision 21.10; an entry without dates
is skipped as `no-dates`, dates are never invented). **The real ids are not stored in git**: the file lives outside the repository
(this team: `H:\ECR\.sync-local\permits\service-entries.csv`, `.sync-local/` is git-ignored) and is passed explicitly with
`-ServiceEntriesFile` (there is no default path). `tools/permits/service-entries.sample.csv` shows the format with synthetic ids.
Generate the real file by a SELECT on a COPY of the database (identifiers only, never other field values):

```sql
SELECT e.Code FROM dic.RegistryEntry e JOIN cfg.RegistryDef d ON d.Id = e.RegistryDefId
WHERE d.Code = N'LAND_PERMIT' AND e.IsDeleted = 0 AND e.Code NOT IN (/* numbers of the Excel books */)
```

then write `number;location;validFrom;validTo` lines (lines starting with `#` are comments). Not verified against the server before the
copy trial: see the test report.
## Header remap (`Remap-HeaderPermit.ps1`) - read-only template

Plans the move of the Land header field `Permit` (91 values, 1 template version, 7 `LAND_PERMIT` entries) from `LAND_PERMIT` to `PRM_AIR`
as an Id -> Id map matched 1:1 by number (dash/underscore ignored for matching only: Land `..._2023_2030` = Excel `..._2023-2030`).
`-Database` is mandatory (EcrLand* only), the connection is `ApplicationIntent=ReadOnly`, output is counters (ids and codes only with
`-ShowCodes`), `-MapOut` writes the map as JSON, exit 1 when a source entry that has documents has no 1:1 target.
`-Execute` is deliberately **not implemented**. Open questions before any write (also in the script header): the published template
versions keep the field pointing at `LAND_PERMIT` and the header PATCH refuses a foreign registry (`ECR-HDR-4223`), so a new template
version (or a decision to change the definition) is needed; may headers of approved/closed documents change; the target validity window
vs the document periods; direct SQL bypasses audit and recalculation.

## Migration of the header field Permit (Q2 / Q3 / Q5)

Read-only research on the copy `EcrLandUi_PermitsTry` (SELECT) and on the code of origin/main 9ad9a19f0. Nothing was run on a stand.
Line numbers are in `src/` of that commit.

### Facts about the 91 values
* One project (2026-01-01 .. 2026-12-31), 12 periods: 2 Scheduled, 10 Open, none Grace/Closed. All 91 values sit on template version 6
  (`HeaderFieldDef` 66, `IsRequired = 1`, Lookup -> LAND_PERMIT); v7 is a draft. 7 `LAND_PERMIT` entries are used:
  598 `KZ01TEST0001` x3, 599 `KZ02TEST0002_2026` x13, 600 `KZ03TEST0003` x29, 601 `KZ04TEST0004` x2,
  602 `KZ05TEST0005` x1, 605 `KZ06TEST0006_2023_2030` x3 (Excel: `_2023-2030`), 606 `KZ07TEST0007` x40 (numbers here are SYNTHETIC; the real ones stay out of git).
* Sheet statuses (`wf.ApprovalState`, 4 rows in the whole database): 89 documents have no row (= Draft); document 2 has three sheets in
  period 202606: Approved, Rejected, Submitted; document 51 has one Draft row.

### Q2: may the header change / may the document migrate? (status -> verdict)
| Sheet status (strictest over the document) | Header PATCH (value change) | Version migration | Source |
|---|---|---|---|
| none / Draft / Rejected (89 + 1 documents, and the Rejected sheet of document 2 on its own) | allowed (needs Write on the project, `Registry` lookup rules below) | not blocked by status | `EditRules.CanEdit` denies only Submitted / Approved (`EditRules.cs:173-177`); `Strictest` ignores everything but Submitted/Approved (`DocumentHeaderHandlers.cs:615-632`); `EnsureEditable` `:429` |
| Submitted (document 2) | FORBIDDEN until the sheet is reopened: `403 ECR-ACCS-0403`, `messageKey err.ECR-ACCS-0403.headerLocked`, reason `DocumentSubmitted` | carried unchanged if the plan does not touch the sheet; refusal `sheetsLocked` (`409 ECR-DOC-0409`) only if it does | `DocumentHeaderHandlers.cs:570-612`, `EditRules.cs:173-174`; `MigrateDocumentVersionHandler.cs:835-839` (`ClassifyLocked`) |
| Approved (document 2) | FORBIDDEN until reopened (`DocumentApproved`); `CanReopen` covers Submitted and Approved (`EditRules.cs:443`) | same as Submitted | `EditRules.cs:176-177` |
| period Closed | header is locked only when ALL periods of the project are Closed (not the case: 0 Closed) | n/a | `DocumentHeaderHandlers.cs:570-577` |
| project archived / archiving | forbidden (`ProjectArchived`, `ArchivingInProgress`); migration refusal `projectArchived` | refused | `EditRules.cs:136-146`; `MigrateDocumentVersionHandler.cs:841-844` |
Rights: header PATCH needs a Write grant on the project (`DocumentHeaderHandlers.cs:213-220`); migration needs `Template.Edit` AND the
Manage grant on the project (`MigrateDocumentVersionHandler.cs:563, 799`). **The migration moves the whole project (all 91 documents) in
one transaction**, not one document.
Net: 90 of the 91 documents are editable as they are; **document 2 needs a Reopen of two sheets (Submitted and Approved)** for a PATCH
route; a migration does not need Reopen for it (the header-only change does not touch sheets), but see the blocker below.

### BLOCKER found in the code: v6 -> v8 with `Permit` on PRM_AIR is refused by the standard migration
* `CompareHeader` marks a changed `LookupRegistryDefId` as **Guarded** with the number of values (`VersionMigrationPlanner.cs:711`), i.e.
  91 guarded values -> refusal `guardedWithData` in BOTH modes (Safe, Presentation); `acceptGuardedOverwrite` removes only values under a
  new cell formula (`:139-143`, ACC-OW). So the dry run of the migration would report `CanApply = false`.
* Even if it passed, `HeaderSql` only renames the field (`HeaderFieldDefId o -> n`) and deletes unmapped fields; it does NOT touch
  `ValueRegistryEntryId` (`DocumentVersionMigrationStore.cs:956-966`): the 91 values would keep pointing at LAND_PERMIT entries.
* The API route is closed the other way round: a PATCH with a PRM_AIR entry is refused while the field is on v6 (`ECR-HDR-0422
  lookupEntryMissing`, `DocumentHeaderHandlers.cs:290-320`), and the required field cannot be cleared (`ValidateValue`).
So the Id -> Id move needs a **product change** (not a script): a lookup-value remap in the migration (map old entry id -> new entry id
by number, applied in `HeaderSql` and exempt from `Guarded` when every value is mapped, refusal when one is not). Untested; this is a
design proposal, needs the migration owner and Land-safety proof (migration + Compare before/after).

### Q3: validity check of the target entry
* Rule (`DocumentHeaderHandlers.cs:503-560`, `ECR-HDR-4223`): only on the PATCH route and only for CHANGED lookup fields; window = the
  PROJECT window `[PeriodStart, PeriodEnd + 1 day)` = `[2026-01-01, 2027-01-01)` (not a single period); entry validity is the half-open
  `[ValidFrom, ValidTo)` (`ValidityWindow.OverlapsSegment`: `windowStart < ValidTo && ValidFrom < windowEnd`, null = open). Also refused:
  other registry, deleted, inactive (`:526-545`). The migration does not run this check (it keeps values as they are).
* Result with Excel dates (`ValidTo = End + 1`) for the 7 used numbers: **91 of 91 pass, 0 fail** - KZ01 [2026-04-29, 2027-01-01),
  KZ02, KZ03 and KZ07 [2026-01-01, 2027-01-01), KZ04 [2026-03-10, 2026-10-01), KZ05 [2026-04-01, 2026-10-01),
  KZ06 [2023-01-01, 2031-01-01); every window overlaps 2026. Conditions: the permit is **Active** (a Draft is `IsActive = 0` ->
  `inactiveEntry`) and the entry window equals the permit window.
* Not measured: the Land-only service entries (no documents).

### Q5: recalculation and Land formulas
* Land formulas do not read the header `Permit`: 0 of 29 950 `cfg.FormulaDef` and 0 of 566 `calc.MethodologyFormula` expressions mention
  `Permit` or `HDR.`; permit bindings 0 (`cfg.PermitSheetBinding`).
* Header PATCH route: for every document with a changed value it enqueues a full document recalculation for EVERY non-Scheduled period
  (`DocumentHeaderHandlers.cs:355-365, 648-660`) - here 91 documents x 10 Open periods = **910 recalculation jobs** of the Land
  documents, although nothing depends on `Permit`. Realistic cost: heavy (the Land recalc is the 4.4M-result workload); the
  "Failed jobs <= 5" gate of Compare would be at risk and the run must be paced/observed. Not measured.
* Migration route: no recalculation unless the new version adds/changes formulas (`formulaTables > 0`, one coalesced project job,
  `MigrateDocumentVersionHandler.cs:777-782`, `MigrationRecalculation.cs`); a v8 cloned from v6 changing only the lookup target has none.
  Submitted/Approved sheets are never recalculated by it.

### Not established without a stand
The real dry-run report of v6 -> v8 (the refusal above is derived from the code), the real PATCH behaviour with PRM_AIR entries, the cost
of 910 jobs, whether `FindEntryStandingsAsync` returns the permit window exactly as the Permits header entry carries it, the Reopen
procedure and rights for document 2, and what the constructor does when the Lookup target of a header field is a `PRM_` registry.