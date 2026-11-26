# SGF-flaring: window steps (live)

Lane `lane/integ-sg-flaring-composition`. Live actions need the human's word; everything below is read-only or dry-run by default.

## 0. Prerequisites
- Gas composition is on live (STREAM / STREAM_CASE / GAS_COMPOSITION / COMPONENT). The one-off FLERT import refuses when STREAM
  already has rows, so **do not re-run the import** to get the flaring registries.
- PI AF data source + read-only account (Q-D, open with the human) — needed for the event maps, not for steps 1-2.
- Check that no live script names something `ROW` (D-580 makes `ROW.<field>` registry-row syntax inside REG* calls):
  ```sql
  SELECT m.Code, mv.VersionNumber, mv.Id
  FROM calc.MethodologyVersion mv JOIN calc.Methodology m ON m.Id = mv.MethodologyId
  WHERE mv.ScriptText COLLATE Latin1_General_CS_AS LIKE '%ROW%';
  ```
  Expected: 0 rows (the repo has none: no `const/table/var/param ROW`).

## 1. Flaring registries (FLARE_LOCATION, LP_SOUR_SPLIT, TOX_GAS_STREAM)
```
Ecr.MethodologyScriptMigration sgf-provision --connection "<live>"          # dry run: prints what would be created, rolls back
Ecr.MethodologyScriptMigration sgf-provision --connection "<live>" --apply  # commit (author svc-integration)
```
- Idempotent: an existing registry with rows is never changed; an existing EMPTY registry is filled; a rejected row = refusal and
  rollback. Review the warnings (pins without a stream or with 0/2+ SOR/EOR cases leave the case empty — the scripts then reject
  those rows visibly) and fix CASE_SOR/CASE_EOR by hand where needed.

## 2. Methodologies (generator)
- Export `Flert` / `Thermaloxidizer` from the legacy DB (`Ecr.MethodologyImport export --sql … --methodology Flert|Thermaloxidizer`),
  convert with the MS converter, then `SgFlaringBinding.Bind(body, consumer)` per consumer:
  IntOnshore, IntOffshore, ContOnshore, ContOffshore (one methodology, row column `SLOT` 1/2/3), ToxDaily, ToxMonthly.
- The body must not carry its own `rows from`; TOx bodies that read GCV_<T>_H2S must carry HSE_TO_SG_481_<T>_H2S (generator refuses).
- Publish with NumericMode = Legacy. Template tables/columns the scripts expect: INT_SG_EVENTS (EVENT_START, LOCATION lookup
  FLARE_LOCATION, GAS_COMPOSITION_ID, STREAM_TYPE, TOTAL…), CONT_SG_ONSHORE (DAY), CONT_SG_DISLAND (DAY, SLOT), TOX_DAILY (DATE, TRAIN,
  TYPE, GAS_COLUMN), TOX_MONTHLY (MONTH, …). Continuous consumers also output SGF_FCID (parity key).

## 3. Parity (another PC with the 2025 legacy DB)
```
powershell -NoProfile -ExecutionPolicy Bypass -File tools\hse30x\Compare-SgFlaringParity.ps1 -LegacyConnection "<legacy>" -EcrConnection "<ecr copy>" -From 2025-01-01 -To 2026-01-01 -OutDir <dir outside repo>
```
Exit 0 parity, 2 unexplained differences, 3 duplicate keys. Self-test: `Invoke-Pester tools\hse30x\SgFlaringParity.Tests.ps1`.

## M3 (decided by the human 26.11, D-580)
- An empty case property that a formula reads is missing data: the row is rejected (`case_has_no_temperature`, …), never computed
  at 0 °C; in parity these rows are the known difference `M3-noTemperature`. Outputs that depend on T_C (legacy formulas, transitive):
  `HSE30X/HSE300 × Int/Cont × SG_EC_Wzv`, `…_EC_Wist_Wzv`, `…_EC_Soot_tonn`, `…_EC_Soot_gsec` (16 formulas). Density/MW/Z: no formula.
