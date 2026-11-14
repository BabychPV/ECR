# PE-7 «Land під рік, хвіст» — design note (B58, B59, B60, B73)

Lane `lane/integ-pe-batch-7`, base `origin/main` a1ff81973. Source edits only (live window W15u: no build/test yet).
No migration, no contract (`schema.d.ts`/OpenAPI), no seed, no `shared/ui`.

## B58 — C_Mm change does not reach the year total of a CLOSED January

- **Root cause.** `CrossPeriodFanOut.FindReadersAsync` (`src/Ecr.Infrastructure/Jobs/CrossPeriodFanOut.cs:215-219` on a1ff81973;
  the plan cites :184) skips every reader period whose state is `null` or `Closed`, and `FormulaRecalculationJob`
  (`FormulaRecalculationJob.cs:163-165`) re-checks the same. B24 (29aa6fed6) already lets the full run of a closed anchor
  write non-month targets under the year aggregate (`RecalculationJob.AnnualOnlyAnchorsAsync`, `RecalculationService`
  `annualPeriods`), but the fan-out path never reached it, so the total waited for an explicit/nightly recalc.
- **Decision (default).** The fan-out SHOULD reach a closed anchor — same rule as B24 (D-NEW-3 «так»): only when the
  version is `YearWithMonthColumns`, the reader is the year anchor, and the year aggregate is not `Closed`
  (`AnnualPeriodRule.AnchorWritesAnnualTargets`). «Stale + nightly» is not the intended behaviour: B24 already made the
  nightly path write it, so waiting adds nothing but staleness. Closed non-anchor months stay skipped.
- **Fix.**
  - `CrossPeriodFanOut.ClosedAnchorYearAsync` (new, public): router document (cached) → reader instances' version must be
    year layout (`RoutesComputedVersion`) → calendar of the year (1 query) → reader == anchor → effective states of the
    months via `IPeriodStore.FindPeriodStateAsync` (with document reopen) → year keys or `null`.
  - `FindReadersAsync`: a `Closed` reader is kept iff `ClosedAnchorYearAsync` is not null.
  - `FormulaRecalculationJob`: a `Closed` reader with sources runs `RecalculateFromOtherPeriodsAsync(..., annualPeriods)`
    when `ClosedAnchorYearAsync` (re-checked at execution) is not null.
  - `RecalculationService.RecalculateFromOtherPeriodsAsync(+annualPeriods)` → `RunAsync(annualPeriods)`: the B24 machinery
    (`AnnualOnlyBlocked`: only non-month routed columns; aggregate sheet status; no Rollup/Check; formula-error filter).
- **Test.** `tests/Ecr.Infrastructure.Tests/Jobs/YearLayoutTailSqlTests.cs`: `B58_правка_CMm_оновлює_підсумок_закритого_якоря_у_відкритому_році`
  (edit M9 in Sep → CM9 = 110 in owner; TOT in closed Jan = 110; CM1 of closed Jan untouched = −1).
  Control `B58_контроль_закритий_увесь_рік_підсумок_якоря_не_пишеться` (aggregate Closed → TOT stays 90).
  Red proof: revert the `FindReadersAsync` change → TOT stays 90.
- **Access/period impact.** No access-filter change. WHO triggers does not change (any edit that already fans out);
  WHAT gets written: non-month cells of a closed January in an open year, automatically — the same write B24 already does
  nightly. Flagged as Q1 (ТЗ 05-engines «закриті автоматично не перераховуються ніколи» vs D-NEW-3).
- **Cost.** Only on the closed-reader path: +reader instances (shared in the job), +1 calendar query, ≤12 state queries
  when the reader is the anchor of a year version. Nothing new on open readers.

## B59 — «Recalculate» on a year document computes only the anchor + C_M01

- **Root cause.** `RecalculateDocumentHandler.RequestAsync` (`RecalculateDocumentHandler.cs:324-343`) always enqueues the
  NAMED period; since L1 a run of month m writes only what m owns (anchor: non-month + C_M01), so other C_Mm stay stale.
- **Decision (default, from PLAN).** A recalc of a year-layout document fans out to all 12 months: ONE job «whole year»
  narrowed to the calendar year (`PeriodKey = null`, new `RecalculationRequest.Year`), not 12 jobs (keeps the ascending
  order for `[Period:-1]`, one document lock, one progress).
- **Fix.**
  - `RecalculationRequest.Year` (+ `FromKey/ToKey`); `RecalculationJob` filters the year in `ScopePeriodsAsync`,
    `RefusedPeriodsAsync` states and `SkippedScopesAsync` (closed months are dropped from the scope, closed anchor →
    non-month only (B24), approved skipped, as for the nightly path).
  - Handler: `YearLayoutYearAsync` (registry + version timeline of the PERIOD; no query without year versions) →
    payload `{PeriodKey: null, Year, SheetDefId, TriggeredByUserId}` on new target `YearScopedTargetOf` = `doc{id}-y{year}[-s{sheet}]`
    (distinct from nightly `doc{id}-year` and from per-period targets). Non-year versions: unchanged.
  - All fast-fail gates (permission, D-214 unnarrowed read, hidden sheet, archived project, closed named period with the
    B24 anchor exception) run unchanged BEFORE enqueueing.
- **Tests.** Application: `RecalculateDocumentSheetScopeTests.B59_річний_документ_перераховує_увесь_рік_однією_задачею`
  (theory: whole doc / sheet 3), control `B59_контроль_PerMonthCopies_перераховує_названий_період`, target uniqueness.
  Infrastructure: `YearLayoutTailSqlTests.B59_річна_задача_рахує_місяці_свого_року_і_не_чіпає_інший_рік`
  (Year=2099 job recomputes CM4; the 209812 instance keeps its stale −12). Red proofs: handler → named period;
  job without `FromKey/ToKey` → 2098 recomputed.
- **Access/period impact.** Permission and D-214 gates unchanged (the job writes under the same write policy). But a click
  by a `Document.View` user in one month now writes all open months of the year, incl. SUBMITTED sheets of other months
  (manual recalc writes submitted with journal + mark, UAT-recalc-ux). Flagged as Q2.

## B60 (small)

### (a) open January: non-month targets use January's sheet status, PATCH uses the aggregate
- **Root cause.** `RecalculationService.EnterSheetsAsync` (`RecalculationService.cs:1504-1565` on a1ff81973) takes the
  status of the RUN period for every sheet unless `annualPeriods` (closed anchor only); PATCH of a non-month column uses
  `AnnualPeriodRule.AggregateStatus`. A submitted January blocked the year total although other months are drafts.
- **Fix.** `RunAsync`: `OpenAnchorYearAsync` (router cache, 0 new queries) when the run is the anchor of a year version.
  Sheets whose written cells contain a non-month routed column (`AnnualTargetSheets`) take the locks of all months
  ascending (as PATCH) in `EnterSheetsAsync(openAnchor)`; per cell: non-month routed → aggregate status, month/other →
  the run period's status. New port member `IMonthColumnRouter.YearPeriodKeysAsync` (default `[]`; `MonthColumnRouter`
  from the cached calendar).
- **Test.** `YearLayoutTailSqlTests.B60_відкритий_якір_пише_неmісячне_за_агрегатом_аркуша_а_CM1_за_січнем`
  (Jan submitted, others draft → TOT written, CM1 not); control `…поданий_у_всіх_місяцях_підсумок_не_пишеться`.
- **Cost.** +11 sheet-lock statements per anchor run that writes non-month cells (same as a PATCH of a non-month column).
- Residual: formula-error rows of non-month cells still follow the run period's `writable` (`ApplyFormulaErrorsAsync`).

### (b) IsLateEdit of a closed-anchor run is never set
- **Root cause.** `RecalculationService.cs:1118-1122`: `isLateEdit = periodState == Grace` — the anchor is `Closed`.
- **Fix.** For `annualPeriods` runs: `AnnualPeriodRule.IsLate(YearStatesAsync(...))` (≤12 state queries, closed-anchor
  runs only). WW-03 rule (cross-period derived change is never «late») is kept.
- **Test.** `YearLayoutTailSqlTests.B60_прогін_закритого_якоря_пізній_за_агрегатом_року` (theory: rest Grace → 1,
  rest Open → 0).
- Residual: an OPEN-anchor run in Grace writing non-month cells while another month is Open still marks them late (per
  batch flag) — PATCH would not. Left as is (needs per-record flag in `WriteBatchAsync`).

## B73 — sheet log breaks across a version migration

- **Root cause.** `GetSheetActivityHandler` (`GetSheetActivityHandler.cs:85-101`) bounds `aud.CellChange` by the
  CURRENT version's visible column Ids (and owner-period pairs from G1); `aud.CellChange` keeps old Ids after migration.
- **Fix.** Same as V-4/#856 (`GetCellChangesHandler.WithVisibleLineageAsync`): optional `IColumnPathMapper` (DI already
  registers it); ONE `LineageAsync` over the visible columns; widen `VisibleColumnIds` and every
  `CellChangeOwnerColumns.ColumnDefIds` with their lineage. Widening starts only from already-visible (and D-214-checked
  owner) columns → Deny hides pre-migration edits too.
- **Test.** `tests/Ecr.Infrastructure.Tests/Persistence/SheetActivityVersionLineageTests.cs` (period 209910):
  log = [8 (new Id), 7 (old Id), a (old C0)], no «99» (same code, other table); differential Deny C1 → only «a».
  Red proof: revert `WithLineageAsync` call → log = [8].
- **Deny mutation (for AU):** `src/Ecr.Application/Security/DocumentReadScope.cs:365` (`VisibleColumnIdsOfSheet`) —
  disable `.Where(CanReadColumn)` → expected red:
  `SheetActivityVersionLineageTests.Deny_колонки_ховає_і_її_правки_до_переносу_диференційно` (C1 and its pre-migration
  P1 «7» reappear). Not mutated by me (rule: no access-filter mutation); my own red proof = revert of `WithLineageAsync`.
- Residual [debt]: row add/delete entries (`aud.RowChange`) are bounded by TableDefId and break the same way — needs a
  table lineage (port method + SQL); workflow events are keyed by sheet CODE and do not break.

## Questions (default applies until answered)
- Q1 (B58): automatic fan-out writes non-month cells of a closed anchor while the year is open — default YES (D-NEW-3/B24).
- Q2 (B59): one «Recalculate» in a year document recomputes all open months, incl. submitted sheets of other months
  (with journal + mark) — default YES (PLAN default). Alternative: year scope only for `Calculation.Recalculate` holders.
- Q3 (B59): a closed NON-anchor viewed month still fast-fails 4221 even though the year job would compute open months —
  default keep (unchanged), revisit if the year page sends a non-anchor period.

## Test isolation (retro 375, coordinator 13.11)
- `YearLayoutTailSqlTests`: every test `try/finally CleanupAsync` (own Current runs → Superseded, own ApprovalState
  deleted, own period states → Open). `SheetActivityVersionLineageTests`: immutable `aud.CellChange` rows dated ~300 days ago.
- Coordinator: Q1 yes (D-NEW in READY), Q2 yes, Q3 keep; debts B102/B103/B104.

## Commit order (one fix + its tests per commit)
B73 → B59 → B58 → B60. `RecalculationService.cs` carries hunks of B58 (`RecalculateFromOtherPeriodsAsync`) and B60
(`RunAsync`/`EnterSheetsAsync`/helpers); `YearLayoutTailSqlTests.cs` carries tests of B58/B59/B60 — staged per commit by
building partial index blobs (no interactive `add -p`).
