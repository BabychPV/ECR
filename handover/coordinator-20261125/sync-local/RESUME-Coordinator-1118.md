Процес — скіл ecr-subtask-gates; cron :13 = hourly.py; стан — `board.py --repo H:\ECR`

# Coordinator resume — 18.11 09:45Z

## Who/how
- I am the ECR coordinator. Sessions (local_ ids):
  - AN «Аналіз» — local_a44029c0-4f5f-4d6c-9739-bc1572939472 (SendMessage works)
  - PE «Permites» — local_7e7ad761-49d4-49ed-8140-cf27cd12307f (SendMessage works)
  - AU «Аудит-2» — local_d01ea2c3-1a69-4b85-9793-996093526301 — letters ONLY via mcp__ccd_session_mgmt__send_message + append to .sync-local\ready\TO-AU-1117.md (+ PRIORITY.md)
  - AA «Аудит проекту та вимог» — local_c3f551b1-f7a9-40c9-ad96-be232d3a6f66 (SendMessage works)
- Human: Ukrainian. Sessions: English. PRIORITY.md entries up to P-0118-20 = standing decisions.
- Cron (session-only):
  - control e509c972 hourly;
  - retro 1c8e6538 every 4 h;
  - ONE-SHOTS 63c7a436 at 18:41 local (16:41Z): quiet reminder;
  - ONE-SHOT a2dc735b at 18:58 local (16:58Z): GO W15x.

## Main / merges (18.11)
- main = 6cf34eaa1. Merged today:
  - #975 batch-1118-2 (B80 N0/N1, B162, B164, L7c, L7c-2, D-132), p413;
  - #976 batch-1118-1 (yearly Land: B170 A2, L3 anchor, (a′), archived guard), p414;
  - #977 batch-1118-3 (B167), p415;
  - #978 batch-1118-4 (B155 p1 server+client, B165, B169, B171, B172, docs D-539…548), p416.
- Next RC p417.
- Merge flow:
  - gh at %LOCALAPPDATA%\Microsoft\WinGet\Packages\GitHub.cli_Microsoft.Winget.Source_8wekyb3d8bbwe\bin\gh.exe; git from VS Team Explorer path; batch worktree H:\ecr-wt\coord-batch1;
  - steps: merge-tree vs main → gh api refs rc/2026-11-18-pNNN → pr create → wait for gate/honesty-guard (loop 45 s) → `gh pr merge --merge --match-head-commit`.
- Open: batch-1118-5 @c5583c822 = AA B155 names (contract + seed) + PE B173 preflight 50155 (D-551). CI running. AU F13 before 16:45Z or after W15x.
- Migration token: with me (returned by PE at 555873eb5).
- D next free: D-552 (RESERVED-1116.txt).

## W15x live Land window — TODAY 17:00Z (human «давай сьогодні ввечері»)
- SHA 2dee84da2e042a4896f539f724e4884fd85e747d (NOT later main).
- READY-FOR-GO from AU: rehearsal x2 PASS, parity 510/510, rpt 83/83, 55 anchor instances.
- Quiet CPU + disk 0: 16:45–19:00Z (P-0118-19).
- Human words:
  - RESTORE allowed ONLY on a Land data-step failure in W15x (P-0118-14);
  - test-w15x-landbuilder account on live OK (17.11 07:45Z permission), disabled after.
- Plan file: .sync-local\reports\W15x-land-window-plan.md.
- After DONE:
  - my UI pass of yearly doc 96 on http://localhost:6094 (Windows sign-in lacks Document.View; need the human's local sign-in);
  - then tell PE to drop EcrLandPe/:5290.
- Incident 05:40Z (lesson 450): the rehearsal moved the live app dir. Fixed, 647 files hash-equal, guard proven.

## Economy
- Weekly usage 88 % (resets 22.11 15:00Z). No new work outside the plan; one subagent per session; sonnet where possible.
- At 96 %: the handover rule.

## Open PLAN rows
- B173 → in batch-5.
- B155 kz wording: ask the human later.
- B169 is in main; next window drops the CommandTimeout override.
- Next live window after W15x: main incl. B155, B167, B169, B80 etc.
- Lessons up to 452 in memory/self-improve-practices.md.

- ✎ 11:05Z: #979 batch-1118-5 p417 → main 8c138a0e0 (AU F13 PASS 5/5). Next RC p418. No open batch. D next D-552. W15x stays on 2dee84da2.

- ✎ 12:30Z: P-0118-22..25 (pause lifted; B26 = V from S3_9 + row activity filter D-425). Open batch-1118-6 @cd7d2850f = AN B80 N2a (0c492c89a); full CI running; AU F13 after W15x. In work: AN N2b, PE B26 rework (lane/pe/b26-volume), AA B114 (lane/aa/b114).

- ✎ 12:40Z: batch-1118-6 @9962a81ea = AN N2a + AA B114. AA → B145 (lane/aa/b145).

- ✎ 14:15Z: batch-1118-6 @929120489 = N2a + B114 + B145 + B26 (4 commits). Full CI restarted. F13 after W15x. PLAN B174, B175 added.

- ✎ 14:25Z: batch-1118-6 @0711af1a1 (+N2b). AN → N3.

- ✎ 16:39Z: batch-1118-6 @d8f3dc32f FULL CI GREEN (N2a, N2b, N3b, B114, B145, B26+Pester fix). Waiting AU F13 after W15x DONE → merge p418.

- ✎ 16:58Z: W15x GO sent (live 200/200, CPU 15 %). Waiting DONE → disable builder account, drop CommandTimeout, my UI pass (human local sign-in), PE drop EcrLandPe, F13 batch-1118-6 → p418.

- ✎ 17:47Z: W15x DONE (report reports\W15x-window.md). Quiet ended. Pending: my UI pass of doc 96 year view (human local sign-in), F13 batch-1118-6 → p418.

- ✎ 18:40Z: migration token → PE (B175, cfg.RowDef.CellDefaultsJson). My UI pass W15x done (parts 1+2 in reports\W15x-window.md); defect B178 (history text on month column). AN on N4 (lane/integ-an-b80-n4, contract).

- ✎ 18:35Z: P-0118-32 full speed to 98 %. READY waiting for next batch-1118-7 (after batch-6 merge): AA B178 e5366ffd0. B179 auto-deps design → AN after N4. PE B175 (token).

## ✎ 18:40Z snapshot
- Live 2dee84da2 (W15x DONE 17:45Z). main 8c138a0e0. Next RC p418.
- batch-1118-6 @d8f3dc32f (worktree H:\ecr-wt\coord-batch1, branch lane/integ-batch-1118-6) CI GREEN; AU F13 running → merge p418. Contents: AN N2a/N2b/N3b, AA B114/B145, PE B26 (+Pester).
- Waiting for batch-1118-7 (cut from main after p418): AA B178 e5366ffd0.
- In work: AN B80 N4 (lane/integ-an-b80-n4) then B179 L1→L2 (design approved P-0118-33); AA B80 N6 client (after N4 contract); PE B175 (migration token with PE).
- Usage 93 %; P-0118-32 full speed to 98 %, then handover.
- My UI pass W15x done; HSE400→Land live test proved gap → B179. Memory: human-auto-dependencies-screen-1118, human-full-speed-until-98-1118, human-b26-volume-column-7b-1118.

- ✎ 19:15Z: batch-1118-7 queue += AA N6 part A 1dcca5cdd (seed B80-health-source.sql).

- ✎ 19:30Z: batch-1118-7 queue += PE B175 generic lane/integ-pe-b175 @ada1ad316 (13 commits, MIGRATION 20261118175617_B175RowDefCellDefaults, contract, seed) — or the rebased -r lane with T7b wiring if READY before the cut. Token with PE.

- ✎ 19:45Z: batch-1118-7 queue += AN N4 lane/integ-an-b80-n4 @864609041 (2 commits on batch-6 tip: 4edb2084a + 864609041 contract; seed B80-stale-approved-dependent.sql). Queue for batch-7: B178 e5366ffd0, N6-A 1dcca5cdd (+B–D later), B175 (generic ada1ad316 or -r lane), N4. Contract regen conflicts likely (N4 and B175 both regen schema.d.ts) → regenerate once on the batch tip, last commit.

- ✎ 19:55Z: batch-1118-7 queue += AN B179 L1 lane/an/b179-graph @e09c0e91f (11c487661, e09c0e91f; on batch-6 tip). AN → L2 lane/integ-an-b179-fanout (live fix).

- ✎ 20:05Z: #980 p418 → main 9063a4a1a. Next RC p419. batch-7 to cut. B181 → PE.

- ✎ 20:20Z: batch-1118-7 assembled LOCALLY (not pushed) in H:\ecr-wt\coord-batch1 on main 9063a4a1a: B178, N6-A, N4 (2), L1 (2; 02-contracts conflict resolved keeping both, LF preserved). Waiting B175-r + B181 → push once.

- ✎ 20:30Z: local batch-7 += AA N6 B+C 27680facb (seed B80-calc-issues-source.sql).

## ✎ 20:40Z snapshot (before /compact)
- Usage 95 % (P-0118-32: full speed to 98 %, then handover: all push WIP, RESUME, PE returns migration token). Context 40 %.
- main 9063a4a1a (#980 p418). Next RC p419.
- batch-1118-7 PUSHED @e94d0f915 (lane/integ-batch-1118-7, worktree H:\ecr-wt\coord-batch1): B178, N6-A 1dcca5cdd, N4 (4edb2084a, 864609041), B179 L1 (11c487661, e09c0e91f; 02-contracts conflict resolved), N6 B+C 27680facb. Full CI started. F13 AU next (incl. B180-dependent [blocks] e2e).
- Pending READY: PE B175-r (lane/integ-pe-b175-r, T7b wiring; token with PE), PE B181 test (lane/pe/b181), AN B179 L2 (lane/integ-an-b179-fanout, live fix). Add them on top of batch-7 or in batch-8.
- PLAN rows new today: B174–B181. Decisions P-0118-21…35.
- Live 2dee84da2 W15x DONE; next window should carry main (B155, B167, B169, B80, B26, B175, B179 L2) — needs K1 re-baseline (B177) and runbook 8.9.1 order for B26.

- ✎ 20:55Z: MIGRATION TOKEN back with me (PE B175 c40b1b2e7). batch-1118-7 += B175-r (14 commits, 8b9a7637a) + B181 test (50615dc8c); pushed.

- ✎ 21:50Z: #981 p419 → main 3dbc74fe2. Next RC p420. Token with me. Open: AN B179 L2, B182, N5/N6-D; B180 (HSE400 monthly on live) before ApprovedOnly; next live window to carry main 3dbc74fe2 (B155,B167,B169,B80 N0–N4,B26,B175,B178…) + K1 re-baseline B177.

- ✎ 22:00Z: AN B179 L2 READY lane/integ-an-b179-fanout-r2 @d26828b70 (NOT fb5c2aa36). Asked +1 commit: remove Hse400CompositionFromDocument key (binding bit decides). Then batch-1118-8 = L2 alone → F13 → p420.

- ✎ 22:10Z: batch-8 queue += AN B182 lane/an/b182 @72dd2fdb9 (test-only; K4 equivalent, no seam). Waiting new -r2 tip (key removal) → build batch-8 = L2 -r2 + B182, push once.

- ✎ 22:25Z: batch-1118-8 PUSHED @84e0435f5 (L2 -r2 + B182). F13 AU after CI → p420.

- ✎ 22:55Z: batch-8 F13 CONDITIONAL (AU e2e on wrong stream FG_Internal; asked redo on Makat gas). AN to add 2 named tests (approved consumer skipped/submitted marked; closed period not enqueued) → delta F13 → p420. Context 44 % → compact pending.

- ✎ 23:15Z: batch-8 e2e: HSE400 Recalculate→Land auto ✅; cell edit path ❌ → B184 L2b (AN, batch-9). batch-8 merge after AN (c) named tests + AU delta.

- ✎ 23:35Z: P-0119-2 B184 yes (sources only). batch-8 tip 1554a2e87 (+cf6343efe) awaiting AU delta F13 + CI → p420. batch-9 queue: AN L3 c8e4f31e1, L2b 52a107e2a (both on -r2).

- ✎ 23:50Z: batch-8 @1554a2e87 CI RED (FormulaJobFixedCostTests 21>20, L2 binding read in formula job). AN fixing on -r2 via cache, no ceiling raise. AU delta (c) PASS. Then re-assemble batch-8 → CI → p420.

- ✎ 05:10Z (19.11): usage 97 %. batch-8 waiting AN -r2 query fix. batch-9 queue L3 c8e4f31e1, L2b 52a107e2a. main 3dbc74fe2, next RC p420, migration token with me.

- ✎ 05:20Z: AN -r2 fix NOT started (agent idle 5h; lesson 462). Plan in RESUME-AN, PLAN B185. batch-8 stays unmerged.

- ✎ 05:40Z: AN B183 READY lane/an/b183 @bbc9e6358 (on -r2; seed «Змінені тексти»; contract desc only). Merge order: B185 → B183 → L3 c8e4f31e1 → L2b 52a107e2a. L5 screen += ReadFromDocument toggle (coordinator decision, fits P-0118-33 Q2). Leftover key mentions → B183-2.

- ✎ 05:55Z: HANDOVER-ALL-1119.md written (from 4 session handovers). Usage 98 %.

- ✎ 06:05Z: batch-1119-1 PUSHED @a16d2cea5 (L2 -r2 b8056aa32 w/ B185, B182, B183, L3, L2b-r2 a3469e334); replaces batch-8. HANDOVER-ALL updated.

- ✎ 06:20Z: batch-1119-1 e2e PASS (live copy: Makat Methane edit → doc96 Density_M10 0.7754 auto). Pending CI + F13 mutations (reports\F13-batch-1119-1.md).

- ✎ 07:40Z: #982 p420 → main a6f49601c. HANDOVER-ALL updated. No open batch.

- ✎ 07:50Z: HANDOVER-ALL-1119.md rewritten clean (main a6f49601c, no open batch). PLAN B179/B182–B185 statuses updated.

- ✎ 08:40Z: 99 % → HANDOVER sent to all 4; HANDOVER-ALL-1119.md final; crons to be removed.

- ✎ 09:50Z: #983 p421 → main dc8fb5d69 (handover in git). Next RC p422.

- ✎ 17:25Z 22.11: #1003 b7 merged → main 1626dff91. perf-login merged main (ceed3c310). Agents: F1–F4 fix (+2 stale hook tests, in-chain reader test), flaky B186/B187. Usage weekly 2 % (reset 22.11). Context 61 % → compact soon.

- ✎ 18:50Z 22.11: b8 assembled lane/integ-b8 @08ce366b9 (merges). Agent finishing docs D-566 + flaky RecalcLaneGlobalCapDbTests + full runs in H:\ecr-wt\coord-b8. Then CI → rc/2026-11-22-b8 → PR → merge → stand window (human: coordinator decides timing). TRACKING.md current.

- ✎ 22.11 ~19:55Z: #1004 b8 merged → main a9a3c0067. 69 more branches deleted (bundle). W15y rehearsal agent started (stops at READY-FOR-GO). F5, F6/F7 agents running → b9 (+ toolbar-stable-actions, pe/b140-prep).

## ✎ 22.11 ~20:20Z SNAPSHOT (before /compact) — read this + TRACKING.md
- Working from origin/lane/handover-1122 docs (HANDOVER-coordinator-1122.md) + human words 22.11 (memory: never-drop-ecr-database-1122, human-tracking-md-always-1122, human-work-rules-tokens-models-batches-stand-1122).
- main = a9a3c0067 (b8 #1004). b7 #1003 merged earlier. Next RC name: rc/2026-11-2x-b9.
- Branch cleanup done: 22+69 deleted (bundle F:\ecr-backup\branches\origin-all-20261122.bundle, list deleted-20261122.txt). DBs cleaned; ECR restored (never drop ECR/EcrLandUi/FLERT/EcrLegacyProd).
- Running agents (background, Opus):
  * W15y window rehearsal (worktree H:\ecr-wt\coord-w15y; toolkit .sync-local\ready\w15y-drafts; plan → reports\W15y-window-plan.md) — stops at READY-FOR-GO; then MY GO (human: coordinator decides stand timing) → my stand verification with evidence (HSE400→Land doc98 Makat Methane M10 80.756271→85.756271 → doc96 Density_M10 0.7754; PERF changed months; 7b; Windows login).
  * F5 lane/cascade-f5 (224159beb, ed6f8fffe + closing pre-queue gap + D-567).
  * F7 lane/cascade-f6-f7 (c71252d33 alias readers + own-table reads recorded at publish, D-568; F6 = no gap).
- b9 = main + lane/cascade-f5 + lane/cascade-f6-f7 + lane/integ-toolbar-stable-actions + lane/pe/b140-prep (+ TRACKING copy to docs/build) → CI → RC → PR → e2e → merge → stand.
- Open after b9: PERF-06 open questions (yearly/quarterly source wakes whole year; manual Recalculate after cascade wakes newer months; measure ~200 HSE400 consumers), B180, calc.CalculationStep size, MS methodology debts (HANDOVER-MS-1120 §3), B155 kz, B19.
- Crons: control 541b2f71 (:13 hourly), retro 7ba3ba50 (:37 every 2h) — session-only.
- Usage weekly 4 %. D next free: D-569.

## ✎ 23.11 ~17:00Z SNAPSHOT (before /compact) — read this + TRACKING.md + PRIORITY.md (P-1123-1..6)

**main and live**
- main is ed2b9f138 (b9, #1005).
- Live runs a9a3c0067 (W15y window 23.11, verified: HSE400→Land, K1 IDENTICAL, 0 Failed).

**Plan (P-1123-2):** ONE window W15z after all lanes are merged, then one full verification on live.

**Lanes and status**
| Lane | Head | Status |
|---|---|---|
| lane/integ-b10 | 87a643c41 | CI+Evidence ✅. F5-R (D-570), F5 Quartz gate, B80-N5f, titleBlocked. The agent is checking a 19/20 class-init flake. |
| lane/integ-nae-import | a2f8c6be6 | CI+Evidence ✅ after rerun (pwsh 139). P-1123-1, D-569. |
| lane/integ-b179-l7 | af1eaf264 | On top of nae, D-574. At assembly, put the migration after D568 and regenerate the snapshot; re-pin SeedReviewed. |
| lane/integ-ms-migrate | 67718914c | Agent still working: 8 synthetic references (P-1123-6), target 44/46. D-571. |
| lane/integ-land-builder | 18041dd4e | Agent still working. Blocker: C11_10 loses 28 results T9a_28/LTD-A36 202610/11. Units = relabel path (coordinator decision). Water T7b formula C·V/1e6 with the Running filter. Owns the cfg.*Def trigger fix. |
| lane/integ-rpt-views | 2064f2ef3 | Linear generation done. Agent still working: additive `_m` month-as-row views (P-1123-5 amended; D-53 kept). Must not touch the trigger. D-573. |
| lane/integ-perf06b | — | Agent working (D-572): yearly source months, monthly detector branch, 200-consumer measure. |
| lane/integ-ci-pwsh139 | bd7a787f5 | Mitigation + diagnostics. Root cause not proven. |

**Toolkit:** H:\ECR\.sync-local\ready\w15z-drafts, plan reports\W15z-window-plan.md. Rehearsal #1 green on b9.

**Re-rehearsal on the final SHA:** about 2.5 h, run in parallel with batch CI. GO outside 01:00–05:00 local.

**After W15z, the full live check:**
- HSE400→Land;
- F5 422 on Quartz;
- F5-R flag;
- 7b Volume/Land_Status;
- NAE import «Цех» + replace + preview;
- the methodology editor, with 44/46 on Script;
- C11_10 V4 rows vs ECR after nightly;
- rpt `_m` views;
- Windows login;
- 0 Failed.

**Other**
- D-575 is the next free decision number.
- Scratch DBs: EcrLandMs (F:) still in use by the MS agent; EcrLandBld is recreated by the builder agent.
- Never drop ECR, EcrLandUi, FLERT or EcrLegacyProd.
- Rules: no debts (23.11); ≤5 .NET agents; ≥6 GB free before a new agent.
- Crons: control 5ab901fc (:13), retro 7ba3ba50 (:37).
- Weekly usage 18%.

## ✎ 23.11 ~20:45 SNAPSHOT (before /compact) — read this + TRACKING.md + PRIORITY.md (P-1123-1..15) + cards `.sync-local\ready\integ-*.md`

**Skill.** `ecr-subtask-gates` v3 (`.sync-local\gates\SKILL.md` + `ref\`) — the process from now on.
- Python: `%LOCALAPPDATA%\Programs\Python\Python312\python.exe -X utf8`; set `$env:GIT` to the VS git.
- Cards: 11 in `ready\integ-*.md`; `board.py --repo H:\ECR` shows the state.
- `LIVE-SHA` = a9a3c0067 (W15y).

**main and live:** main ed2b9f138; live a9a3c0067.

**Lanes (tip / CI)**

| Lane | Tip | CI | Note |
|---|---|---|---|
| b10 | 12024f8a3 | ✅ | JQ-FIFO, queue tie-break: fixes the :88 flake on every lane. Agent b10 (Opus, new after the restart) is still on the 40× trx run (AC-6) |
| nae-import | a2f8c6be6 | ✅ | |
| ms-migrate | 53f0ae257 | ✅ | dry run on 1.0.20.0: 45/45, 0 mismatches, ~35 min |
| land-builder | accf83d57 | ✅ | agent (Opus) is doing D-575 (Scheduled periods not evaluated, P-1123-9) in `H:\ecr-wt\coord-bld`; 10 local commits; copy EcrLandBld2 |
| perf06b | ffb4e2133 | in progress | Quartz 9612 → 812 |
| rpt-views | f38b3582e | red only on :88 | |
| b179-l7 | af1eaf264 | red only on :88 | |
| rowlock-ui | 7096248ed | red only on :88 | |
| source-hints | ab8a03bb3 | ❌ buttonScale guard | returned to its agent |
| ci-pwsh139 | bd7a787f5 | ✅ | |
| b74-p3p4 | — | — | agent (Opus) for the NEXT window (P-1123-11), D-576 |

**Batch b11.** Assemble once builder D-575 and source-hints are ready. Migration order: D568 (main) → NAE → b10 JQ-FIFO (check whether it has a migration) → L7 last; regenerate the EF snapshot; re-pin SeedReviewed. Take the trigger commit once, from builder (b62c3a3e9 = rpt b89c7d42d). Overlaps: builder ↔ perf06b (IPeriodStore, PeriodStore, RecalculationJob).

**Window W15z.** Human allowed the night (P-1123-8); avoid only 02:15 and 03:30 (nightly recalc). Live MS parity takes 16 min. Checklist: `board.py --window <main SHA>`, including AC-0 NAE «Цех» (Substance row + column B).

**Live access.** The human gave the land-owner account in chat (password not stored). Windows login has no rights (403).

**Decisions today:**
- P-1123-9: Scheduled periods are not evaluated (D-575).
- P-1123-10: the «All months» link stays as is.
- P-1123-11: B74 goes next window.

D-577 is the next free number.

**Crons:** control 5ab901fc (:13), retro 7ba3ba50 (:37). Weekly usage 36%.

## ✎ 24.11 ~16:40 SNAPSHOT (before /compact) — read this + TRACKING.md (tail) + PRIORITY.md (P-1123-16..20)

**State.** Window W15z NOT yet done; live still a9a3c0067 (W15y). main ed2b9f138.
- **b11 pushed @2f9b72b29** (CI in progress, Evidence ✅). Local `H:\ecr-wt\coord-b11` = 2f9b72b29 + 141021a20 (B74 e2e label fix, NOT pushed).
- Migrations order: … D568 → JQFifoEnqueueSeq → B179L7 → M7RunInputsCellChangeWatermark (copy at 135).
- Contents: 10 original lanes + D-575 methodologies + MS script fix + P-1123-18 sweep + M4/M11/M1 + reviews #1–#4 fixes (F5 holds whole doc-year while any job alive; actual-source ancestors; per-version month narrowing; Quartz FIFO index; M7 watermark) + B74 P3+P4 (P-1123-19) + RevoGrid hideAttribution.
- **Pending:** agent m11 (a2863af1c39cada90, worktree H:\ecr-wt\coord-m11, lane/integ-b11-m11 @a40d19a6c) doing P-1123-20 (F5-R also for non-cell source changes: methodology results, copies, row deletes, header) + LOW -dependents-approved watermark → one push → I cherry-pick into coord-b11 → build + has-pending (startup = Infrastructure csproj!) + Architecture → push b11 → CI + narrow review #5 + round 5b (only new F5-R kinds + smoke) → rc/2026-11-24 → PR → e2e → merge `--match-head-commit` → GO.
- **Round 5 on 2f9b72b29: ALL PASS, K1 IDENTICAL** (evidence F:\bld\w15zr\matrix-2f9b).
- **GO package:** `F:\bld\go-w15z\w15zgo_build.ps1 -Sha <40> -Worktree H:\ecr-wt\w15z-r` (~2.5 min) → `F:\bld\stage\w15z-<sha9>`; `w15zgo_live.ps1 -Sha <40> -Step preflight|stop|backup|migrate|applysql|deploy|start|account|land|ms|postcheck|k1|backupafter|accountoff` (plan-only unless `-Execute -LiveConfirmed`); checklist `F:\bld\w15zr\GO-checklist.md`. Rehearsal agent ae8637d09aa73686d keeps copy EcrLandW15zR.
- Live window rules: avoid 02:15/03:30; RESTORE only with separate human word (P-1123-16); after window check count of false-positive F5-R marks (NULL watermark on old runs).
- Agents: m4 aff7d0c0a84b5f485 (idle, client), b74 aac703970bea63bfe (idle), rehearsal ae8637d09aa73686d (idle).
- Lessons 490–502 in self-improve-practices. Weekly usage 48 %. D-577 next free.
## ✎ 24.11 ~18:00 SNAPSHOT (координатор)
- LIVE = b3060f0fd (main ecdd84621, PR #1006 злито 16:20). W15z GO виконано 16:29–16:54: міграції 131→136, land GATES GREEN, MS 45/45 Script, K1 IDENTICAL, бекапи before/after у E:\EcrBackup\land-ui\w15z\bak. Перевірка live (слово людини P-квіз «Так, дозволяю»): M1/M2/M4/M7/M11/M13 + UI (RevoGrid, Open HSE400, B74 Template step, NAE «Цех» прев'ю) — усе PASS, усе відкочено, акаунт f13-w15z-verify вимкнено. Скрипт: scratchpad\coord\live_verify.ps1.
- Завтра після 00:30: перевірити нічний перерахунок — Current-прогони doc96 з NULL SourceStateJson (зараз лише 202609) отримали стан; 0 Failed; M12 (погоджені не перераховані).
- Lane в роботі (наступна пачка b12): (1) lane/integ-ms-only-editor — P-1124-1 «лише новий редактор» (агент opus, ще не запушено); (2) lane/integ-calc-issue-orphans — сироти calc.CalculationIssue (агент opus, ще не запушено); (3) lane/integ-b74-add-template-ux fdcf892a3 — прийнято; (4) Node24: c80db89e9 на lane/integ-ci-node24 від застарілого dev/integration — cherry-pick на свіжий main у пачку.
- Далі: звести b12 = main + (1..4) → пуш + CI + e2e dispatch паралельно → репетиція на копії EcrLandW15zR → rc → злиття → вікно → перевірка live (сам).
- Прибирання після: worktree w15z-go, b11-e2e, coord-m11 тощо; БД EcrE2EB11Fix (F:) — лише scratch.
- Уроки 503–511 у memory/self-improve-practices.md.
## ✎ 24.11 ~18:40 (Київ) SNAPSHOT перед /compact
- ПІСЛЯ /compact ПЕРШИМ: виклик скіла ecr-subtask-gates; CronList — має бути :13 (hourly.py першим кроком, id 1c4c3414) і :37 ретро (7ba3ba50); якщо нема — створити (ref\07). Людині ОДНИМ рядком: «скіл викликано, cron знайдено».
- LIVE b3060f0fd (W15z), LIVE-SHA оновлено; test_gates 41 OK; перший hourly — фоновий b1nkcgq73 (може загубитись після compact — перезапустити й розібрати [!]: 11 карток → main/live-verified через where_is.py; картки для ms-only-editor, calc-issue-orphans, b74-add-template-ux, project-settings-ux, node24, P-1124-2..4, audit-ux, rm-report-snapshots; wt_audit --ack після тріажу).
- Пачка b12: H:\ECR-wt\coord-b12 (lane/integ-b12 = main ecdd84621 + node24 95bf3eca6). Прийнято: project-settings-ux 493133b1c (запушено), b74 2a20dcd98 (+ агент доробляє singleDocumentPerProject у ProjectTemplateDto, блок Next; перевірка StatusFromGitScriptTests на main).
- Агенти в роботі: ms-only-editor (F:\ecr-wt\ms-only-editor; лише скрипт-редактор, БЕЗ перегляду формульних версій — людина), calc-issue-orphans (F:\ecr-wt\calc-issue-orphans, коміт cb5b2ffcf, лишились idempotent x2/мутації/пуш), P-1124-2 фаза 1 (повна конвертація Formulas→Script без втрат + вода, копія EcrP11242Scratch, дизайн .sync-local\design\P-1124-2-formulas-to-script.md, СТОП на рев'ю), P-1124-3 rm-report-snapshots (F:\ecr-wt\rm-snapshots; таблиці rpt не дропати), P-1124-4 audit-ux (H:\ECR-wt\audit-ux; місцевий час, колонки, фільтри реально).
- Перед вікном b12: board.py --window → людині «що побачите / чого ще не буде» (урок 512); після — екрани людини (методології, майстер документа, Settings, маршрути, аудит) зі знімками; нуль слідів перевірки.
## ✎ 24.11 23:17 (Київ) SNAPSHOT (контекст 37 %)
ПІСЛЯ /compact ПЕРШИМ: скіл ecr-subtask-gates; CronList (:13 1c4c3414, :37 7ba3ba50); hourly з коренем C:\ecr-wt.
ЛЮДИНА СПИТЬ, слово: довести все до кінця і ОНОВИТИ СТЕНД вночі (memory human-b12-overnight-window-go-1124); b12 = УСІ зміни в роботі + склад газів (чекає). Не 02:15/03:30. Вхід у live UI :6094 уже в панелі браузера.
ДИСКИ: SSD C:/G:/H: — копії C:\ecr-wt, бази G:\EcrData; HDD D:/E:/F: — лише бекапи.
LANE (усі від ecdd84621, якщо не сказано):
- b74-add-template-ux fd8fae11f ✅CI; project-settings-ux 493133b1c ✅; audit-ux 57a26ccdd ✅; ci-node24 95bf3eca6 (в coord-b12)
- calc-issue-orphans cb5b2ffcf (міграція 20261124152432) — CI червоний флейком → approver-edit-flake (F:\ecr-wt\approver-edit-flake, агент a3a1ee84, 20× іде)
- rm-report-snapshots 69b6d8a6e ✅ — агент a8cede88 доробляє: Done кампанії, ReportViewer home, мертвий код, DROP rpt.Report* + rpt-види SSRS (людина: можна, якщо не потрібні; SSRS-дані лишити), міграція після calc-orphans
- p11242-ms-lossless fcac609a0 ✅ (на cb5b2ffcf, без міграції) — агент a3e8522a: Land 1.0.21.0 вода + повна репетиція вікна на G:
- ms-only-editor (F:\ecr-wt\ms-only-editor, некомічене) — агент a3a5f2d6: лише Script UI + UI-прохід + повний екран
- missing-input-month-alias a679ff483 (CI іде) — прийнято умовно
- consistency-checks — агент a42545f9 (R1–R5,R8, UX, DROP ext.ConsistencyRule після rm-snapshots)
- gas-composition — агент a0461e80 (R3, дизайн → збірка, міграція остання)
- c11-10-borrowed-rules — агент a7e42fa0
ЧЕРГА МІГРАЦІЙ: calc-orphans → rm-snapshots → consistency → gas-composition.
ВІКНО: зібрати b12 в H:\ECR-wt\coord-b12 (lane/integ-b12) → dotnet build зведеного → CI+e2e+upgrade×2+idempotent → репетиція G: → вікно (стадії P-1124-2: WaterV2/FinalizeProve/Finalize/Land 1.0.21.0; перерахунок doc 96/202609 якщо не погоджено) → LIVE-SHA, UI-прохід зі знімками, CalculationIssue за видами ДО/ПІСЛЯ, нуль слідів → ранковий звіт.

## ✎ 25.11 01:17 (Київ) SNAPSHOT-2 (контекст 42 %)
ПРИЙНЯТО у b12 (CI зелений або червоне = доведений флейк): b74 fd8fae11f, project-settings 493133b1c, audit-ux 57a26ccdd, ci-node24 95bf3eca6, calc-orphans cb5b2ffcf, approver-edit-flake f4a8fbcb7 (CI у черзі), month-alias a679ff483, rm-snapshots-2 aae840277 ✅ (міграція 20261124194915).
В РОБОТІ: ms-only-editor 703dd5132 ЧЕРВОНИЙ (scenarios без прапора, vitest methodologyTimestamps) + доробки 1–6 (агент a3a5f2d6); p11242 fcac609a0 ✅ + Land 1.0.21.0 + звірка реєстрів речовин (a3e8522a); c11-10 aa1bfed68 CI іде + доробки (a7e42fa0); e2e-contrast-main 327dbc94a CI іде (a8cede88); consistency-checks (a42545f9, міграція після rm-snapshots-2); gas-composition (a0461e80, R3, міграція остання).
ЧЕРГА МІГРАЦІЙ: calc-orphans 20261124152432 → rm-snapshots 20261124194915 → consistency → gas-composition.
КОНФЛІКТ ПАЧКИ: ms-only-editor × rm-snapshots-2 у seed UI-dead-buttons.sql (агент ms-only розводить); rm-snapshots-2 побудована на calc-orphans (у пачку — замість calc-orphans окремо).
ВІКНО: стадії P-1124-2 (WaterV2 → FinalizeProve → Finalize → Land 1.0.21.0) + c11-10 Invoke-MsRowNarrowing (потрібна збірка з 1ab7bf9e3) + перерахунок doc 96 202609–202611 (якщо не погоджено).
ПРИБРАТИ ПІСЛЯ ПАЧКИ: E:\ecr-wt\miss-alias, F:\ecr-wt\calc-orphans-main, H:\ECR-wt\rm-snapshots (стара), scratch EcrP11242Scratch, EcrC1110Scratch (агент).
CPU 99 %, тижневе 71 %.

## ✎ 25.11 02:17 SNAPSHOT-3: b12 локально H:\ECR-wt\coord-b12 = 73d9ee257 (8ba865b4d + c11-10 ×4). Прийнято також: flake f4a8fbcb7 CI ✅, c11-10 aa1bfed68 ✅. Лишилось додати: ms-only-editor (f26e54d28, CI у черзі), p11242 (+Land 1.0.21.0, звірка реєстрів речовин), consistency-checks (20d9e1279 на rm-snapshots-2 — брати лише ЇЇ коміти поверх aae840277), gas-composition (без міграції, пуш ~03:30–04:30). Далі: build+Architecture зведеного → пуш lane/integ-b12 (один CI) → репетиція G: → вікно.

## ✎ 25.11 03:16 SNAPSHOT-4: план вікна — .sync-local\WINDOW-b12.md. b12 локально = ... + c11-10 ×4 + p11242 ×7 (rc=0). Лишилось у пачку: ms-only-editor (92e39cf06, CI у черзі), consistency (78bc04dd1 — коміти поверх aae840277; чекаю відновлення 3 тестів), gas-composition (0bcd16870 + доробки 4 пункти). CI у черзі GitHub довго.

## ✎ 25.11 04:16 SNAPSHOT-5: усі lane прийнято, крім consistency (CI 92f29a52e іде). Збирання b12 раунд 2 у H:\ECR-wt\coord-b12 — агент a8cede88 (ms-only fd0e7a72d + consistency + gas 692ea2dcd). Далі: пуш b12 → CI+e2e → репетиція WINDOW-b12.md на G: → вікно.

## ✎ 25.11 06:16 SNAPSHOT-6 (контекст 55 %): b12 = lane/integ-b12 8e8f2ac04 (CI ✅, e2e 2 падіння) → агент a3a5f2d6 робить lane/integ-b12-fix (контраст опису діалогу New draft version light; список років Documents без 2025). Потім: cherry-pick фіксу в b12 → пуш → CI + e2e (повний) → вікно за WINDOW-b12.md. Людина 25.11: перевідкрити doc 96/202609 у вікні (так); SG-факели — картка після b12 (так); газ Edit лише SysAdmin, 1-е число (ок); CN1_2/CN2_2/CN3_1 — кодом (закрито). Репетиція: EcrLandB12Reh на G: (API :5197 працює), збірка C:\ecr-wt\b12-bld; тимчасовий SysAdmin-акаунт для імпорту газу на live — створити й вимкнути після.

## ✎ 25.11 07:19 SNAPSHOT-7 (контекст 64 %): ВІКНО b12 ЗАВЕРШЕНО (LIVE-SHA 8e8f2ac04; lane/integ-b12 = 184bc11f6). UI-прохід live — ок. В роботі агенти: nav-mygroups (sonnet, lane/integ-nav-mygroups), chunk-reload + project-delete (opus, 2 lane), UI-флоу на тестовому doc 100 у панелі браузера (opus; видалити doc 100 після). Людина: видалити HSE401_2026, TEST_COORD_2026, HSE400_GOLD (HSE400_2026 лишити) — через UI після розгортання project-delete; бекап E:\EcrBackup\land-ui\b12\bak\EcrLandUi-before-cleanup-20261125-0714.bak. Наступна пачка b13: nav-mygroups, chunk-reload, project-delete, na-not-applicable (варіант 2 — явно), b12-ui-polish, sg-flaring-composition, script-row-key, gates-hourly-speed. Питання людині відкрите: Volume без даних — 0 чи порожньо.

## ✎ 25.11 07:36 (Київ) SNAPSHOT-8 ПЕРЕД /compact
ПІСЛЯ /compact ПЕРШИМ: скіл ecr-subtask-gates; CronList (:13 1c4c3414 hourly, :37 7ba3ba50 ретро) — нема → створити (ref\07, корінь C:\ecr-wt додати); людині одним рядком «скіл викликано, cron знайдено».
LIVE: b12 ВИКОНАНО 25.11 06:36–07:00; LIVE-SHA 8e8f2ac04 (lane/integ-b12 = 184bc11f6, +2 e2e-файли). Журнал live: C:\ProgramData\Ecr\logs\ecr-20261125_012.log (спільний файл; фільтр 'Now listening on: http://localhost:5094'). Вхід людини в UI — у панелі браузера.
LANDUI2026: прив'язано HSE401, HSE400 (+T401, LAND_UI); TEST_COORD не прив'язано (службовий). LAND_UI — ~200 документів у проєкті (НЕ «один документ»).
ВИДАЛИТИ проєкти HSE401_2026, TEST_COORD_2026, HSE400_GOLD (людина; HSE400_2026 лишити) — UI не дає (лише чернетки) → чекаю lane/integ-project-delete, потім через UI. Бекап: E:\EcrBackup\land-ui\b12\bak\EcrLandUi-before-cleanup-20261125-0714.bak.
ТЕСТ-ФЛОУ: doc 100 LAND_UI-P1-0001 «TEST b12 flow (delete after)» — агент acc31ca9 у панелі браузера (5 рядків, перерахунок, автоперерахунок, колізії, подання/погодження, видалення doc 100 після). Чекати звіт; перевірити SELECT, що doc 100 прибрано, doc 96 без змін (результати 510).
АГЕНТИ b13 (усі від 184bc11f6): chunk-reload + project-delete (a6ee3f74, opus); required-banner + усі українські тексти сервера в UI + сторож (a9162a5a); number-display: газ-редактор + Permits + сітка (a0461e80); nav-mygroups c016bae4a ✅ (e2e 45554498376 іде).
КАРТКИ b13 planned: b74-no-advisory (LAND_UI без плашки), na-not-applicable (людина: варіант 2 — явно застосовні виходи), b12-ui-polish (NULL номери потоків, Common без версій, бейджі LEGACY·ERRORSONLY, роки Documents), sg-flaring-composition (людина: так), script-row-key, gates-hourly-speed, ms-formulas-api-retire.
ВІДКРИТЕ ПИТАННЯ людині: Volume_Mnn без даних — 0 чи порожньо.

## SNAPSHOT-9 (25.11 13:16)
ПІСЛЯ /compact ПЕРШИМ: скіл ecr-subtask-gates; CronList (:13 1c4c3414 hourly, :37 7ba3ba50 ретро) — нема → створити (ref\07, корені + C:\ecr-wt E:\ecr-wt); людині одним рядком.
ЛІМІТ: тижневий 90 % (скидання 29.11 17:00). Режим економії: нових великих агентів не ставити; на 98 % — стоп агентів, пуш WIP, HANDOVER.
LIVE: b12 8e8f2ac04 (LIVE-SHA формат виправлено — перший рядок = SHA). Doc 100 лишається до лейну land-doc-delete-closed-period (людина «ок», січень не відкривати).
b13 (усі CI зелені): number-display 9f3ffe845 accepted; chunk-reload 61377c410 accepted; nav-mygroups c016bae4a; required-banner d2636f051 accepted (+overhauled-default); registry-as-of 73147ae9b (R3 огляд PASS-з-умовами → F1-1/F1-2 виправлено) reported; project-delete 76d21e809 + фікси огляду (C2-b, C2-c вимір, черга, 409, HSE400_GOLD попередження) — агент a6ee3f74 працює.
Зведення lane/integ-b13: агент a9fd6d10 (sonnet), C:\ecr-wt\b13; сказано НЕ запускати e2e до фінальних SHA registry-as-of і project-delete → потім cherry-pick нових комітів, перегенерація контракту, CI + e2e. Огляд: .sync-local\design\REVIEW-b13-acceptance.md.
СКЛАД ГАЗУ → факели: DESIGN-sg-flaring.md rev3 + REVIEW-sg-flaring.md (PASS-з-умовами); рішення P-1125-SG/SG2/SG3 (усі три, PI AF, EOR і L6 виправити, склад на дату рядка, HSE372 резерв, відхилено+причина); лейн методологій — після скидання ліміту. Людині/DBA: PI AF джерело read-only + копія старої ECR з 2025.
ОДИН ПРОЄКТ (P-1125-ONEPROJ): LANDUI2026 = doc 96 Land, 98 HSE400, 93 HSE401, 100 тест; перенесення документа — картка integ-document-move (R3, b14, дизайн після скидання або «перенесення зараз»); потім видалити HSE400_2026/HSE401_2026/TEST_COORD_2026/HSE400_GOLD через UI (C2-a: спершу deletion-preview на live).
КАРТКИ b13/b14 planned: land-gas-silent-zero, land-doc-delete-closed-period, auto-recalc-methodology, doc-flow-ui-polish, server-language-rest, b74-no-advisory, na-not-applicable, b12-ui-polish, script-row-key, gates-hourly-speed.
Вікно b13 — лише словом людини.

## SNAPSHOT-10 (25.11 ~19:00) — читати першим після /compact
- LIVE = d75032896 (b13), app F:\ecr-backup\land-ui\app-b13, API PID 32856 / web 33648, набір вікна E:\EcrBackup\land-ui\b13 (WINDOW-b13.md). Лічильники до/після b13 однакові (doc 96 = 510). Картки b13 → live-verified (SHA в AC = cherry-pick SHA пачки).
- Тижневий 94 % (скидання 29.11 17:00 Київ). На 98 %: стоп агентів, пуш WIP, HANDOVER. Людина 25.11: «починай b14».
- b14 агенти (opus) у роботі: docui (C:\ecr-wt\b14-docui: documents-board-yearly + doc-notices-compact), numfmt (b14-numfmt: number-format-consistent), flert (b14-flert: source-test-flert + live-журнал; копія EcrLandB14Flert на G:). Чекати їхні звіти → приймання (SHA, CI, e2e, мої скрини).
- document-move: людина — РАЗОВИЙ СКРИПТ (не UI). DESIGN-document-move.md rev 2 + повторний огляд PASS-WITH-FIXES (B1–B6, design\REVIEW-document-move.md). Відповіді людини (квіз 2) — внизу DESIGN: doc 98 переклад місяців + видалення січневих копій = так; HSE401 — вікно редагування = ПАРАМЕТР ШАБЛОНУ «рік проєкту + зміщення» (після — архів), а не «поки відкритий місяць» → P2 переробити (міграція/контракт, R3); видалення HSE400_2026/HSE401_2026 — одразу у вікні (паритет ДО видалення). Реалізація ліній P (продукт: P1 loader owner-aware, P2 параметр шаблону) і T (tools/document-move + hse400/hse401 v14 [Period].Days) — після скидання ліміту.
- Інші картки b14 (planned): project-delete-large, e2e-airlimits-isolation, server-language-rest, doc-flow-ui-polish (+«немає доступу» замість 404), b12-ui-polish, b74-no-advisory, na-not-applicable, script-row-key, land-gas-silent-zero (High), land-doc-delete-closed-period, auto-recalc-methodology.
- Факели: БД ECR з 2025 від людини + design\FLARES_TOX_HSE400_DATAFLOW.md; паритет SG 12 міс, FG лип–гру, TOx на 2026; HSE401 = AF_Air_HSE401_Import (ліміти). Бракує PI AF read-only з live. Після скидання.
- Прибрати пізніше (не під час воріт): EcrLandB13Reh, EcrLandB14Flert, EcrReqBanner, EcrE2EReqBanner, C:\ecr-wt\b13-bld, b13-reh, G:\EcrData\b13reh.
- Cron: :13 1c4c3414 hourly, :37 7ba3ba50 ретро. Уроки до 549.

✎ SNAPSHOT-10 доповнення (25.11 19:16): b14 accepted — number-format-consistent (c23722627), source-test-flert (1f8d47993; журнал live насправді пишеться, урок 550). У роботі лише агент docui (board + notices). Нова картка source-test-categories. Тижневий 94 %. Після docui → зведення lane/integ-b14 (база d75032896) з трьох ліній, один повний CI + e2e, репетиція, вікно за словом людини (Start-LiveDetached.ps1 для перевірки журналу).


## SNAPSHOT-11 (25.11 ~23:00) — читати першим після /compact (доповнює SNAPSHOT-10)
- Тижневий 95 %. Людина: на 99 % — повний HANDOVER-ALL + перенесення скіла на інший ПК + розділ «For the next Claude» (англ.) + пуш гілки handover/coordinator-<дата> без секретів (пам'ять human-handover-at-99-skill-transfer-1125). На 98 % — стоп агентів, WIP у lane.
- b14 усі 4 частини accepted: documents-board-yearly 666b90c8a, doc-notices-compact cede54ad0 (на базі board), number-format-consistent c23722627, source-test-flert 1f8d47993. Агент зводить lane/integ-b14 у C:\ecr-wt\b14 від d75032896 (звіт → .sync-local\reports\b14-assembly.md), далі повний CI + повний e2e. Потім: репетиція на копії live (як b13: WINDOW-b13.md, набір E:\EcrBackup\land-ui\b13; новий tools/live/Start-LiveDetached.ps1 перевіряє журнал), WINDOW-b14.md, людині «що побачите», вікно лише за словом.
- Нові картки: stale-banner-audit (банер «another user», сервер), source-test-categories. document-move: дизайн rev 2 + огляд PASS-WITH-FIXES + рішення людини (скрипт; HSE401 вікно редагування = параметр шаблону; видалення проєктів одразу у вікні) — реалізація після скидання 29.11.
- Прибрати пізніше: EcrLandB13Reh, EcrLandB14Flert, EcrReqBanner, EcrE2EReqBanner, C:\ecr-wt\b13-bld, b13-reh, b14-docui, b14-numfmt, b14-flert, G:\EcrData\b13reh, G:\EcrData\b14flert.

✎ SNAPSHOT-11 доповнення (25.11 21:16): lane/integ-b14 @7afb19c97 — CI 45671209757, e2e 45671375248, Evidence 45671210169 (rerun) — усі success. Агент зведення робить репетицію на копії EcrLandB14Reh → WINDOW-b14.md. Далі: людині «що побачите», вікно b14 лише за словом (набір як b13, app-b14). Тижневий 95 %. Картки: config-fallback-guard (нестабільний сторож), stale-banner-audit, source-test-categories.

