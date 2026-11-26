---
name: w15u-toolkit-lessons-1113
description: "AU window-toolkit lessons from W15u — the HSE400 flag is env-only (keep it in every restart); next PrevBak/LiveSha; rules for altered tables, new modules, calendar closes"
metadata:
  node_type: memory
  type: project
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-14T10:21:52.349Z
---

After W15u (13.11) live runs with the env flag ECR_Calculations__Hse400CompositionFromDocument=true. The build's appsettings has it false.

- Every later restart (next window, a rollback, a manual restart) MUST pass it: in w15u-drafts restart-w15u that is W15U_HSE400_FLAG=1. Otherwise Land silently loses its HSE400 composition and FormulaErrors come back.
- ✎ 14.11 B105: the flag is now PERSISTENT in `F:\ecr-backup\land-ui\live-env.json` (whitelisted keys). restart-w15u reads this file on every live start, including rollback. Carry the live-env block into the w15v restart script. Restart live only through the toolkit.
- ✎ 14.11 W15v DONE: live = 363dc0baa. Toolkit is in w15v-drafts; report in .sync-local\reports\W15v-window.md.
  - Next window: PrevBak = E:\EcrBackup\land-ui\w15v\bak\EcrLandUi-after-W15v-20261114-1221.bak (88976C25…).
  - #909 edits old migration RV01 — review this before the toolkit.
  - Lesson: a scratch-mode build pins Sql into the scratch dir. Either copy the pins into the canonical build, or rebuild with -Replace before live.
  - Check C: before GO; EcrTest_* DBs fill it up.
- (old) Next window W15v: LiveSha a1ff81973; PrevBak = E:\EcrBackup\land-ui\w15u\bak\EcrLandUi-after-W15u-20261113-2356.bak (sha 007F5283…).

Toolkit rules added in w15u-drafts (carry them over):
- RebuiltIdxs/RebuiltIdxShape: an index rebuilt by an applied Sql file passes only with its exact new shape.
- A migration that ALTERS tables needs MigCols, NewCols/StampCols, MigIdxReplaced, MigFks and MigModules.
- NewNonRpt/ArcTextNames: non-rpt modules NEW in the range are absent before apply-sql and carry no Cyrillic.
- W15U_RCBRANCH lets a scratch run target a lane/candidate head with -AllowRcSha.
- The scratch root is on F:; only the live run gates on C: ≥ 15 GB.

**Why:** these are the gaps that stopped the W15u rehearsals. The flag one is invisible until a restart drops it.

**How to apply:** clone w15u-drafts → w15v, keep the flag in restart, and set the K1 baseline right before GO. A calendar policy close can freeze a month: reopening it needs the human's word (see [[w15u-window-1113]]).
