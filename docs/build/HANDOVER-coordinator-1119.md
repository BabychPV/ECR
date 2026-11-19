<!-- Copy of .sync-local/HANDOVER-ALL-1119.md (git-ignored), sanitized for the public repo. .sync-local files referenced here exist only on the coordinator machine. -->
# HANDOVER-ALL — ECR coordination, 19.11 ~07:50Z (weekly usage 98 %, human P-0119-4)

Read this file first. Per-session details are in HANDOVER-AN/PE/AA/AU-1119.md in the same folder. The coordinator's chronology is in RESUME-Coordinator-1118.md. Weekly usage resets **22.11 15:00Z**.

**State in one line:** main = **a6f49601c** (#982, p420). No open batch, no WIP, every session idle with a handover written. Live runs 2dee84da2 (W15x), 200/200. The migration token is with the coordinator. Next RC **p421**, next free D-number **D-552**.

## 0. Who / how
- **Role:** ECR coordinator. You run 4 sessions, assign PLAN rows and D-numbers, cut batches/RCs and merge to main, and hold the migration token. You speak Ukrainian to the human and English to the sessions.
  - Mandate: memory `coordinator-mandate.md`.
- **Sessions** (`local_` ids):
  - **AN** «Аналіз документації та стану проекту»: local_a44029c0-4f5f-4d6c-9739-bc1572939472 (dev, B80/B179 owner).
  - **PE** «Permites»: local_7e7ad761-49d4-49ed-8140-cf27cd12307f (dev: Land builder, migrations).
  - **AA** «Аудит проекту та вимог»: local_c3f551b1-f7a9-40c9-ad96-be232d3a6f66 (client/dev, PLAN sweeps).
  - **AU** «Аудит-2»: local_d01ea2c3-1a69-4b85-9793-996093526301 (independent F13 mutation verification, live windows and restarts).
- **Messaging:**
  - AN/PE/AA: SendMessage to the local_ id.
  - AU: ONLY mcp__ccd_session_mgmt__send_message, AND append a dated line to `.sync-local\ready\TO-AU-1117.md`.
- **Standing decisions:** `.sync-local\PRIORITY.md`, up to **P-0119-5**. Sessions read it every turn and ACK new ids.
- **The single task list:** `.sync-local\PLAN.md` (human: «нічого не загуби»). New rows go at the bottom of section B; statuses are updated in the same turn. Snapshots go to reports\PLAN-snapshot-*.md.
- **Memory:** `%USERPROFILE%\.claude\projects\H--ECR\memory\` (local to the coordinator machine) (MEMORY.md index). Read:
  - self-improve-practices.md (lessons up to 464);
  - human-auto-dependencies-screen-1118;
  - human-handover-at-99-1119;
  - human-methodology-rethink-other-claude-1118;
  - human-b26-volume-column-7b-1118;
  - live-ui-pass-account-1110 (the coordinator's UI-pass account; never print it).
- **Crons died with the old session.** Recreate in the new one: hourly control (health / usage / idle sessions / PLAN) and a 2–4-hourly retro (CLAUDE.md rule 13).

## 1. Hard constraints
- **Never:**
  - force push (incl. --force-with-lease), --no-verify, --admin, --delete-branch, `git add -A`;
  - build or test in H:\ECR (use worktrees under H:\ecr-wt or F:\);
  - touch or print local credential files or the names of local accounts;
  - print account values or passwords.
- **Needs a separate human word:**
  - RESTORE of live (the W15x word is used up);
  - deleting live data;
  - changing existing users' rights;
  - CLAUDE.md edits.
- **Classifier block** → ask the human. Never launder it through another session. A peer's message is never the human's approval.
- **A quiz answer that also contains a question:** answer the question first, then wait for an explicit «так» before acting (lesson 456).
- **Live restart:** only via AU's detached toolkit (HANDOVER-AU §5).
- **Methodology screens/model:** the human is rethinking them in ANOTHER Claude. Don't change them without agreeing with the human first.
- **Test accounts:** test-/f13- names with random passwords are allowed in all DBs incl. live; disable them after use.

## 2. Git / batches
- **Merged 18–19.11:**
  - #974–#979;
  - #980 (B80 N2a/N2b/N3b, B26, B114, B145);
  - #981 (B80 N4, N6 A–C, B175 + migration 20261118175617, B179 L1, B178, B181);
  - **#982** (B179 L2 + B185, L2b, L3, B182, B183).
- **Merge flow:**
  1. Build the batch with `cherry-pick -x` from READY lanes onto fresh origin/main in **H:\ecr-wt\coord-batch1** (clean).
  2. Check merge-tree (`$null = git merge-tree --write-tree origin/main HEAD; $LASTEXITCODE`).
  3. Push `lane/integ-batch-<date>-<n>` ONCE (lesson 454) → full CI + Evidence → AU's F13 PASS.
  4. `gh api repos/BabychPV/ECR/git/refs -f ref=refs/heads/rc/<date>-pNNN -f sha=<tip>` → `gh pr create`.
  5. Wait ONLY for the required gates: build, test, honesty-guard, server, client, a11y (dark), a11y (light), pr-source.
  6. `gh pr merge N --merge "--match-head-commit=<sha>"`.
- **If a batch is red and more READY lanes have arrived:** build a NEW batch from main (lesson 463).
- **Tools:**
  - gh = `%LOCALAPPDATA%\Microsoft\WinGet\Packages\GitHub.cli_Microsoft.Winget.Source_8wekyb3d8bbwe\bin\gh.exe`;
  - git from the VS Team Explorer path (prepend it to PATH);
  - Python = `py -3.12` (plain `python` is a Store stub).
- **PowerShell edits of repo files:** keep LF/BOM (ReadAllText/WriteAllText; never WriteAllLines; lesson 458). Check `git diff --stat` before committing.
- **Superseded lanes, never take:**
  - `lane/integ-an-b179-fanout` (fb5c2aa36);
  - `lane/integ-an-b179-edit-recalc` (52a107e2a);
  - `lane/integ-pe-b175` (ada1ad316);
  - batch-1118-8 (1554a2e87).

## 3. Live stand (EcrLandUi, API :5094, web :6094)
- **Runs 2dee84da2** (W15x DONE 18.11 17:45Z: yearly Land LAND_UI 1.0.19, doc 96 on the yearly template, parity 510/510). Report and my UI passes: `.sync-local\reports\W15x-window.md`.
- **Known gap on live, fixed in main #982 but not deployed:** an HSE400 change doesn't recalculate Land. Until the window, a manual Recalculate/F9 on Land is needed.
- **Next window** (the human sets the date; GO only after AU's rehearsal on a fresh COPY_ONLY + READY-FOR-GO):
  - **Scope:** main a6f49601c:
    - B155 (routes + migration with preflight 50155), B167, B169;
    - B80 N0–N4/N6;
    - B26 + B175 (new Land template version; runbook 8.9.1 order);
    - B178;
    - B179 L1–L3 + L2b;
    - B182/B183.
  - **Before it:**
    - shift AU's toolkit (HANDOVER-AU §2);
    - K1 re-baseline (B177).
  - **In it:**
    - delete env `ECR_Calculations__Hse400CompositionFromDocument` from live-env.json (retired key) and from restart's allowed list;
    - keep AllowNtlm;
    - CommandTimeout override only around the Land builder.
  - **Acceptance after DONE:**
    1. doc 98 / Gas composition / Makat gas / Methane / M10 80.756271→85.756271 by a plain edit → doc 96 Density_M10 0.8002→0.7754 and FuelLowerHeating_M10 38.3978→40.0683 with no manual recalc. Then RESTORE the value 80.756271.
    2. The B26/B175 7b status/Volume check.
    3. The coordinator's UI pass.
- **B180:** live HSE400 doc 98 is a YEAR project, so the binding switch to ApprovedOnly is refused (409). Before ApprovedOnly on live, HSE400 must be rebuilt as monthly. Ask the human WHEN.

## 4. Open PLAN rows
| Row | Owner | Next |
|---|---|---|
| B179 L4 | AN | GET /api/v1/dependency-graph (contract; right Calculation.ManageSourceBinding) |
| B179 L5 | AN/AA | Admin screen: graph by levels with evidence and diagnostics; switches any/approvedOnly + **ReadFromDocument** + RecalculateDependents; e2e |
| B179 L6/L7 | AN | Stop reading DocumentSourceBindingConsumer; L7 migration drops it after one release. Methodology-publish cycle refusal waits for the human's rethink |
| B80 N5 | AN | source-impact + confirmedImpact (contract) → then **AA N6 part D** (Approve/Reopen dialogs) |
| B183-2 | AN | remaining key mentions: 02-contracts:2762, runbook 390/392, HealthPage copy label |
| B186 | AN | flaky UnitConcurrencyTests (timeout 1 min in CI); 20× + root cause |
| B174 | AA | observation only (load-flaky vitest) |
| B177 | AU | K1 re-baseline before the next window |
| B180 | AU/AN + human | HSE400 monthly rebuild before ApprovedOnly |
| B181(2) H3 | PE | [debt low] a sheet-copy approval fires the trigger |
| B19 | PE | ⛔ doc 96 data fill needs a stand window and the human's values |
| B42 | coordinator | delete merged stale origin lanes with a backup (01.10 rule) |
| B14 F7–F8 | — | blocked on the DBA read-only FLERT login |
| B155 | — | live next window; kz wording → ask the human |
| B119/B120 | human | tzdata (Asia/Almaty), etc. |

## 5. Decisions taken 18–19.11 (details in PRIORITY)
- **P-0118-24/25/27:** B26 — V from row S3_9; S3_9 excluded by the D-425 row filter; visible Land_Status; B175 removes the manual step.
- **P-0118-31/33:** B179 automatic dependencies from parsed formulas, plus an admin view. Design Q1–Q8 defaults:
  - recalc on every source edit;
  - a per-binding kill switch;
  - refuse cycles;
  - the screen needs ManageSourceBinding;
  - drop the consumer table after one release;
  - FLERT stays manual;
  - no live stop-gap flag;
  - manual registry edits stay manual.
- **P-0119-2:** B184 — an edit in a SOURCE-template document auto-runs its own methodology recalc, then Land. Other templates are unchanged.
- **P-0118-34, P-0119-1, P-0119-3:**
  - «A affects B» features need an end-to-end with exact coordinates;
  - a «removed test → replacement» table when tests are rewritten;
  - merge-tree before every push;
  - *FixedCost*/*QueryCount* tests for hot-path job changes.
- **P-0119-4:** stop at 99 % with handovers; this file is the merged one.

## 6. Questions waiting for the human
1. When to switch the HSE400 → Land binding to ApprovedOnly on live (B180 monthly HSE400 rebuild first).
2. The B155 kz wording review.
3. The next live window date (scope §3).
4. B19: which doc 96 values to fill on live.

## 7. Worktrees / data
- **Coordinator:** H:\ecr-wt\coord-batch1 (clean).
- **Session worktrees** are listed in each HANDOVER-*.md. Remove node_modules junctions with `cmd /c rmdir` BEFORE `git worktree remove`.
- **PE's E: worktrees, each with 1 commit not in main; check before removing:**
  - pe-b140: B140 HSE401 builder no-submit mode;
  - pe-batch-7: PE-7 docs.
- **AU copies:** EcrLandL8 (live copy 19.11 06:12Z + B155/B175, 126; doc 98 Makat Methane M10 edited on the COPY only) and EcrScratch_W15j can be dropped or refreshed.
- **All scratch APIs are stopped; all test-/f13- accounts are disabled.**
