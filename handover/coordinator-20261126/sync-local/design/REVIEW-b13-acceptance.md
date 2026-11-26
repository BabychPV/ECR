# R3 independent acceptance review — b13: registry-as-of, project-delete

Reviewer: independent agent (read-only), 25.11. Base `184bc11f6`. Method: `git diff 184bc11f6..origin/lane/<branch>`, `git show` of the
touched and neighbouring files on the lane SHA, EF model snapshot (FK and column scan). Nothing was built, run, or queried on any DB.
Line numbers below are for the lane SHA.

---

## Lane 1 — `lane/integ-registry-as-of` @ `0814f75ac`

**Verdict: PASS-with-conditions** (fix F1-1 before the window; F1-2 is a check that must run before the window).

What holds:
- **Without the clause the engine path is the same as before.** `RegistriesAsync` takes the old `GetOrLoadAsync(ids, period.End)` path, `ScriptRegistryScopes` is unchanged
  (`RegistryAsOf is null`), the binder adds nothing, and `asof` is reported only as an unknown name. The one exception is the Date argument (F1-2).
- **D1:** scope becomes `Join(Year)` only under the clause. **D2:** the preview goes through the same `ScriptRowRun`, and the tests compare preview with run on both sides of
  the boundary (`RegistryAsOfEngineTests.cs:126-136,248-270`). **D3:** the intervals come from every entry of the loaded source, including Lookup targets and the HSE400
  overlay, which still returns `RegistrySnapshotSource`. **D4:** the windows come from `doc.Period`. **D5:** a new code on a non-temporal registry is let through. **D6:** the arch test
  covers the generic writers.
- **Date handling:** event cells are written in project time (`SourceEventRowBuilder.cs:147,208` → `ToProjectTime`). The value is compared as a `DateOnly` with the inclusive
  `binding.Period.Start/End`. A null or erroneous date gives a reject with a reason, not 0. The `ScriptEvaluationContext` is per row, so one row's snapshot does not leak
  into the next.
- **`RegistrySnapshotCache` keying:** the source key is `ids|RegistryAsOfUtc`, the cache is per run (`CalculationOrchestrator.cs:115`), and a failed load is evicted. This is
  consistent with the existing `GetOrLoadAsync`. The snapshot is built once per validity interval.
- **Fail-closed:** if `FlaringRegistryLock` is not injected for a flaring code, the handler throws (`ExternalRegistryGuard.cs:188-192`). `GasComposition.Edit` is required
  even when the edit list is empty (`FlaringRegistryLock.cs:280`).

| # | Sev | Where | Finding / failure scenario |
|---|---|---|---|
| F1-1 | **High** (AC-10 bypass) | `RegistryDefinitionDraftHandlers.cs:346-352` (`PublishRegistryDefinitionHandler`) vs guard at `RegistryDefinitionHandlers.cs:478` | The guard sits in `SaveRegistryDefinitionHandler.HandleAsync` **before** it calls `ApplyAsync`. `PublishRegistryDefinitionHandler` calls `apply.ApplyAsync(...)` directly. **Scenario:** a user with `Registry.EditDefinition` saves a draft of `LP_SOUR_SPLIT` (removes or retypes a field, changes a key or a rule). A user with `Registry.Publish` publishes it. The definition read by a closed or approved month changes without `GasComposition.Edit` and without a 409. **Fix:** move the guard into `ApplyAsync`, or call it in Publish too. Add the type to the arch test (it checks only 9 entry writers, and the check is per type). |
| F1-2 | Medium (behaviour change for **all** scripts) | `ScriptEvaluationContext.cs:329-330,447,487` (`dates: true` unconditionally); `CalculationSteps.cs:258`; `ScriptCalculationModule.cs:448`; `CalculationInputBuilder.cs` (`ValueDate`) | Before this lane, a `Date` cell reached every script as `null`. A non-optional `row.D` counted as a missing argument, so the row gave no result. Now every script sees a `Date` value, whether or not it has the clause. **Scenarios on live:** (a) a converted script (MS lossless, formula parity) does `row.D?`, `ISBLANK`, `coalesce` or arithmetic on a Date column. It now gets a Date instead of null: a different branch, or `#VALUE`. This breaks parity with the formula version, which still sees null. (b) A script with a non-optional `row.D` that was "missing" in every row now produces results on the next recalculation of open months. No test pins either case. **Condition:** before the window, list the live script versions that read a `Date`-typed column (on a copy, or with `ParityComparer` over all live script versions). If any exist, gate `dates:` on `script.RegistryAsOf != null` (or an explicit opt-in). |
| F1-3 | Low | `CalculationInputBuilder.cs:182-188` (`WithAliases`); `MonthColumnArgumentsModule.cs:324` | Month-input aliases (LAND-YEAR L2a) and the GCV argument module build `CalculationArgument` **without** `ValueDate`. A clause that reads a date through an alias, or a script with the clause run through `MonthColumnArgumentsModule`, rejects **every** row as `registryAsOfEmpty`. Today's SG generator does not use either path; record it as a known limit. |
| F1-4 | Low (TOCTOU) | `UpsertRegistryEntryHandler.cs:134` (the transaction starts at :249); same pattern in all guarded writers | `ListLockedPeriodsAsync` is read **before** the write transaction and without a lock. A period that closes, or a sheet approved, between the check and the commit lets the edit through. The window is small (hourly `PeriodStateJob`, a concurrent approve). Accept it, or re-check inside the transaction. |
| F1-5 | Low | `RegistryExternalKeyHandlers.cs` (`BindRegistryExternalKeyHandler` / `Unbind…`), `SaveRegistryHierarchyHandler`, `SwitchRegistrySourceHandler` | These are not under the flaring guard. They matter only if SG scripts resolve `FLARE_LOCATION` through external keys (AF id) or if `FLARE_LOCATION` is switched to External and synced. In that case a closed month's mapping can change. Confirm which path the SG generator uses. |
| F1-6 | Info | `ScriptCalculationModule.cs` `ReadArguments` / `calc.CalculationInput` | The date that chose the registry version is not stored as an input value. It appears only in the `RegistryAsOf` trace step text. Reproducibility (B-4) depends on the trace being kept. |

---

## Lane 2 — `lane/integ-project-delete` @ `76d21e809`

**Verdict: PASS-with-conditions** (run the conditions C2-a and C2-b before AC-0 on live).

What holds:
- **Permission:** `Project.DeleteWithData` is global, has `IsDangerous=0`, and is granted only through the `SystemAdministrator '%'` pattern (`00-prelude.sql:580`). No other built-in
  pattern matches `Project.%`. The scoped `Project.Manage` (PeriodAdministrator) does not grant it.
- **Transaction:** a single `ExecuteInTransactionAsync`. The project row is read under `UPDLOCK,ROWLOCK` (`ProjectPurgeStore.cs:42`). The structure gate of each document is
  taken exclusively in id order (`PurgeProjectHandler.cs:170-173`). The audit rows are written in the same transaction.
- **Completeness:** I scanned the EF snapshot for every `ProjectId`/`DocumentId`/`PeriodId`/`TableInstanceId`/`TableRowId` column. Every table is covered by
  `DocumentDeletionStore.DeleteAsync`, `ProjectStore.DeleteAsync` or the purge's own deletes (approval history, project runs, `RecalculationApproval`, `ArchiveRun`,
  `ColumnRenameProject`, `rpt.ViewProjectScope`), with three exceptions. `itg.JobProgress` (C2-d) is excluded. `ext.SourceEventLink` cannot exist without a map, and a map blocks the
  delete. `aud.*` and `arc.*` are not touched. The integration test derives its table list from `sys.columns`, so it is not fitted to the current schema.
- **B69:** refused when the status is Archived, `IsArchiving` is set, or any `arc.TableInstance` row exists for the project's documents.
- **Client:** the confirm compares `typed === code` exactly (case-sensitive, no trim). The server compares `Trim()` + `Ordinal`, so it is at least as strict. The reason is required on
  both sides (≤500). The button is disabled while a blocker is present or the preview is loading or has failed.
- **Audit:** `ProjectDeletedWithData` records the reason, code, status, cell count, counts, months, and each document (id, key, template, cells). A `StructureChange` row (Guarded)
  records the reason.

| # | Sev | Where | Finding / failure scenario |
|---|---|---|---|
| C2-a | **Medium** (AC-0 / B80 correctness depends on live data) | `ProjectPurgeStore.cs:269-270` → `DocumentSourceBindingStore.cs:86-94` | B80 dependants are counted only when this project holds the **chosen** source document. Chosen = `MAX(DocumentId)` among non-GOLD documents that have bound HSE400 stream tables in that year. **Scenario:** `TEST_COORD_2026` (or `HSE401_2026`) holds a document on the HSE400 template that was created after HSE400_2026's. Then (1) LANDUI2026 already reads from it, not from HSE400_2026; (2) purging `TEST_COORD_2026` is **refused** (`purgeHasDependants`) and blocks AC-0; (3) `HSE400_2026` would **not** be refused. If HSE400_2026's document is the max, it **is** refused (as required) once LANDUI2026 has instances of the consumer template in 2026. **Condition:** before AC-0, run `GET /projects/{id}/deletion-preview` (read-only) on live for HSE400_2026, LANDUI2026, HSE401_2026, TEST_COORD_2026 and HSE400_GOLD. Expect: HSE400_2026 blocked by dependants, the other three that will be deleted not blocked. |
| C2-b | Medium (AC-0 blocker + misleading text) | `ProjectPurgeStore.cs:201-216` (`EventMapDependantsAsync`); `DocumentDeletionStore.cs:194-210` | An `ext.SourceEventMap` on the project's **own** document is reported as a "dependant" with the project's own code. The UI says "Other projects read this one". **Scenario:** HSE401_2026 (AF event documents) has event maps. The purge is refused with the message "documents of other projects read its data (HSE401_2026/…)". The admin has no in-dialog way to proceed. **Fix:** a separate blocker key (`purgeHasEventMap`) with text that says to remove the map, or delete the maps as part of the purge (they are configuration of the project being deleted). |
| C2-c | Medium (operational) | `PurgeProjectHandler.cs:150-216` (one transaction, synchronous HTTP); `DocumentDeletionStore.cs:216-218` (cell delete without per-`PeriodKey` equality); `appsettings.json` `CommandTimeoutSeconds: 60` | The whole project is deleted in one transaction inside the request. Each `ExecuteDelete` has a 60 s timeout, and the cell delete is a subquery without partition elimination (written for small drafts). **Scenario:** HSE401_2026 or TEST_COORD_2026 has hundreds of thousands of cells. The command times out, the transaction rolls back (no data loss), and the purge cannot complete. While it runs, lock escalation on `doc.CellValue`/`calc.*` can block every user on live. **Condition:** measure on a restored copy before AC-0, or run it in a quiet window. |
| C2-d | Low | `itg.JobProgress` (excluded from the purge and from the test scan) | Queued or running jobs whose `DocumentId` points at a purged document are neither cancelled nor deleted. They fail on the next claim, or report 404/FK in the jobs UI. A running recalculation that inserts after the commit fails on `FK CalculationRun→Project`, so no orphan data is left. |
| C2-e | Low (TOCTOU) | `PurgeProjectHandler.cs:150-173`; `ProjectPurgeStore.cs:58,101` | Only the project row is locked. The document list, the `arc.*` check and the B80 dependants are snapshot reads (RCSI). (1) A document created in the project after the list is read makes `DELETE doc.Project` fail on FK → 500 + rollback. This is safe but not a 409. (2) A consumer document created in another project at the same moment is not seen. Both are rare. The preview→purge gap is otherwise safe: the facts are re-read in the transaction, and only the code is carried over from the preview. |
| C2-f | Low / question | `PurgeProjectHandler.cs:123-216` | Approved and closed months are deleted without reopening them. The human wrote «просто відкрий місяця і видали» ("just open the month and delete"). The lane takes this as permission to delete any data that is not archived. This is consistent with "delete with all data", but the decision is not recorded on the card. Record it in the D-register. Also note: no `TouchSourceConsumersAsync` and no source-deleted trigger run (unlike single-document delete). This is correct only because the chosen-source case is refused (C2-a). |
| C2-g | Info | `ProjectPurgeStore.cs:229`; `tools/hse400/*` | HSE400_GOLD is never a B80 source (consistent with the loader), so it is deletable. The template tools (`Build-Hse400Template.ps1`, README) expect `HSE400_GOLD` to exist for golden tests and `migrate-version`. Re-create it or update the tools after AC-0. |

---

## Summary
- registry-as-of: **PASS-with-conditions**.
  - Must fix: F1-1. Draft publish bypasses the AC-10 lock.
  - Must check: F1-2. The `Date` value is now visible to every script, not only to scripts with the clause.
  - Known limits: F1-3 to F1-5.
- project-delete: **PASS-with-conditions**.
  - Before AC-0: run the read-only previews on live (C2-a).
  - Before AC-0: decide how event maps are handled (C2-b).
  - Before AC-0: measure the purge on a copy (C2-c).
  - Lower priority: C2-d to C2-g.
